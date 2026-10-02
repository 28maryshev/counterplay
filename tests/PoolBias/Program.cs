using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Замер перекоса пула: насколько чемпион из пула (обычного или дуо) получает
/// преимущество в оценке перед обычным подбором.
///
/// Считаем на живых данных (data-slice.db) по случайным, но воспроизводимым
/// драфтам. Вывод — цифры и вердикт по порогам; ничего не меняем ни в базе, ни
/// в настройках: пул подкладываем в ПАМЯТИ и на диск не сохраняем.
/// </summary>
internal static class Program
{
    // db-роль → позиция в терминах LCU (её ждёт DraftState).
    private static readonly (string Db, string Lcu)[] Roles =
    [
        ("top", "top"), ("jungle", "jungle"), ("mid", "middle"), ("adc", "bottom"), ("support", "utility")
    ];

    private const int DraftsPerRole = 40;   // сценариев на роль
    private const int PoolSize      = 5;    // чемпионов в подставном пуле

    private static int _fails;

    /// Множитель, с которым собран движок (см. DUO_MATE_MULT).
    private const double DuoMateMultInCode = 1.5;

    /// Вес синергии, с которым собран движок (см. W_SYNERGY). Копия нужна, чтобы
    /// посчитать ожидаемый выбор напарника; сверяется с исходником там же, где
    /// множитель напарника.
    private const double WSynergyInCode = 1.2;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // Пулы — в свою папку, НЕ в файл игрока. Проверки однажды уже оставили
        // в его pools.json тестовые аккаунты, а свои пулы — пустыми.
        PoolStore.DirOverride = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "counterplay-test-pools", "PoolBias");
        System.IO.Directory.CreateDirectory(PoolStore.DirOverride);
        // И начинаем с ЧИСТОГО листа: состояние, оставшееся от прошлого
        // запуска, делает проверку неповторяемой. SetFavourite, например,
        // переключает — со звездой от прошлого раза он её гасит, и проверка
        // падает через раз.
        var stale = System.IO.Path.Combine(PoolStore.DirOverride, "pools.json");
        if (System.IO.File.Exists(stale)) System.IO.File.Delete(stale);
        PoolStore.Reload();


        var dbPath = FindDb();
        if (dbPath is null)
        {
            Console.Error.WriteLine("не нашёл data-slice.db — запускать из корня репозитория");
            return 2;
        }
        // «Файл игрока не тронули» сверяем по СЧЁТЧИКУ записей, а не по отметке
        // времени: рядом обычно крутится сама программа, и с появлением
        // автосинхронизации она переписывает тот же pools.json по своему
        // расписанию. Отметка тогда говорит о чужой работе, а не о нашей, и
        // проверка падала на ровном месте — хуже, чем не проверять вовсе.
        var savesBefore = PoolStore.SaveCount;

        // Пулы пользователя правим только в памяти, но всё равно возвращаем на
        // место: вдруг когда-нибудь ниже появится Persist.
        var acc      = PoolStore.Current();
        var poolsBak = acc.Pools.ToList();
        var duosBak  = acc.DuoPools.ToList();
        var kindBak  = acc.ActiveKind;
        var idBak    = acc.ActiveId;
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            acc.Pools.Clear(); acc.Pools.AddRange(poolsBak);
            acc.DuoPools.Clear(); acc.DuoPools.AddRange(duosBak);
            acc.ActiveKind = kindBak; acc.ActiveId = idBak;
        };

        Console.WriteLine($"база: {dbPath}");
        using var engine = Quiet(() => RecommendationEngine.Create(dbPath, "emerald"));
        Console.WriteLine($"патчи: {engine.PatchDisplay}   бакет: {engine.TierBucket}\n");

        var rnd = new Random(20260922);   // фиксированное зерно — цифры повторяемы

        var shifts      = new List<int>();      // сдвиг по местам от бонуса пула
        var bonuses     = new List<double>();   // фактическая прибавка к Score
        var duoBonuses  = new List<double>();
        var gaps        = new List<double>();   // разрыв между соседями в топ-10
        var spread6     = new List<double>();   // разброс от 1-го до 6-го места
        var jumpedIn    = 0;                    // залетел в топ-6 из-за бонуса
        var jumpedCases = 0;
        var poolVsBest  = new List<double>();   // скор показанного из пула минус скор общего №1
        var duoVsBest   = new List<double>();
        var onlyVsFull  = new List<double>();   // тот же чемпион: в карточке пула и в общем списке
        var strong      = new List<double>();   // пул из сильных (места 1–15) против общего №1
        var weak        = new List<double>();   // пул из слабых (места 40–120)

        foreach (var (dbRole, lcuRole) in Roles)
        {
            // Кандидаты роли (в порядке силы на пустом драфте) — из них и лепим врагов.
            var empty = Draft(lcuRole, [], []);
            var all   = Scored(engine, empty, dbRole, null, false).Values
                        .OrderByDescending(r => r.Score).ToList();
            if (all.Count < 20) { Console.WriteLine($"{dbRole}: мало кандидатов ({all.Count}) — пропускаю"); continue; }
            Console.WriteLine($"{dbRole,-8} кандидатов на роль: {all.Count}");
            var ids = all.Select(r => r.ChampionId).ToList();

            for (var n = 0; n < DraftsPerRole; n++)
            {
                var enemies = Pick(rnd, ids, 5);
                var allies  = Pick(rnd, ids.Except(enemies).ToList(), 4);
                var state   = Draft(lcuRole, allies, enemies);

                var byId  = Scored(engine, state, dbRole, null, false);
                var plain = byId.Values.OrderByDescending(r => r.Score).ToList();
                if (plain.Count < 12) continue;
                var order = plain.Select(r => r.ChampionId).ToList();

                // Разрывы в оценке: на фоне чего мы измеряем бонус.
                for (var i = 0; i < Math.Min(10, plain.Count) - 1; i++)
                    gaps.Add(plain[i].Score - plain[i + 1].Score);
                spread6.Add(plain[0].Score - plain[Math.Min(5, plain.Count - 1)].Score);

                // Пул из СЕРЕДИНЫ списка: так виден и сдвиг, и перепрыгивание.
                var pool = Pick(rnd, order.Skip(8).ToList(), PoolSize);
                if (pool.Count < PoolSize) continue;

                // ── обычный пул ────────────────────────────────────────────
                var withPool = Scored(engine, state, dbRole, pool, duo: false);
                foreach (var id in pool)
                {
                    var b = withPool[id].Score - byId[id].Score;
                    bonuses.Add(b);

                    var was = order.IndexOf(id) + 1;
                    var now = Rank(withPool, id);
                    shifts.Add(was - now);
                    jumpedCases++;
                    if (was > 6 && now <= 6) jumpedIn++;
                }
                // Ничего, кроме пула, бонус трогать не должен.
                foreach (var r in plain.Take(12))
                    if (!pool.Contains(r.ChampionId)
                        && Math.Abs(withPool[r.ChampionId].Score - r.Score) > 1e-9)
                        Fail($"{dbRole}: чемпион вне пула {r.ChampionId} изменил оценку "
                             + $"({r.Score:F3} → {withPool[r.ChampionId].Score:F3})");

                // Карточка пула против общего №1 — то, что человек видит рядом.
                var shownPool = pool.Select(id => withPool[id]).MaxBy(r => r.Score)!;
                poolVsBest.Add(shownPool.Score - plain[0].Score);

                // ── дуо-пул: должен вести себя ровно так же ─────────────────
                var withDuo = Scored(engine, state, dbRole, pool, duo: true);
                foreach (var id in pool)
                {
                    duoBonuses.Add(withDuo[id].Score - byId[id].Score);
                    if (Math.Abs(withDuo[id].Score - withPool[id].Score) > 1e-9)
                        Fail($"{dbRole}: дуо даёт не тот же бонус, что обычный пул "
                             + $"({withPool[id].Score:F3} → {withDuo[id].Score:F3})");
                }
                duoVsBest.Add(pool.Select(id => withDuo[id]).Max(r => r.Score) - plain[0].Score);

                // Карточки пула считаются НЕ по общему списку, а по only-списку
                // (в нём нет порога кандидатов). Если оценка от этого меняется —
                // это и есть скрытая прибавка, которой в общем списке нет.
                var viaOnly = Quiet(() => engine.Recommend(state, pool.Count, pool))
                              .ToDictionary(r => r.ChampionId, r => r.Score);
                foreach (var id in pool)
                    if (viaOnly.TryGetValue(id, out var sc))
                        onlyVsFull.Add(sc - withPool[id].Score);

                // Пул из разных слоёв силы: видно, «вытаскивает» ли бонус слабый пул.
                var top15 = Pick(rnd, order.Take(15).ToList(), PoolSize);
                if (top15.Count == PoolSize)
                {
                    var w = Scored(engine, state, dbRole, top15, false);
                    strong.Add(top15.Max(id => w[id].Score) - plain[0].Score);
                }
                // Нижняя четверть списка кандидатов — «слабый пул». Берём долей,
                // а не фиксированным местом: кандидатов на роль всего ~30–50.
                var tail = Pick(rnd, order.Skip(order.Count * 3 / 4).ToList(), PoolSize);
                if (tail.Count == PoolSize)
                {
                    var w = Scored(engine, state, dbRole, tail, false);
                    weak.Add(tail.Max(id => w[id].Score) - plain[0].Score);
                }
            }
        }

        // ── наигранность: пул НЕ складывается с мастерством, а берёт максимум ──
        MasteryCheck(engine);

        // ── надбавка за напарника по дуо-пулу ──────────────────────────────
        CheckMultInSource();
        MateUpliftSweep(engine, rnd, DuoMateMultInCode);
        MateGateCheck(engine, rnd);
        MateSuggestCheck(engine, rnd);

        Report("прибавка к оценке за пул",        bonuses);
        Report("прибавка к оценке за дуо-пул",    duoBonuses);
        Report("разрыв между соседями в топ-10",  gaps);
        Report("разброс оценок с 1-го по 6-е",    spread6);
        Report("сдвиг по местам (мест вверх)",    shifts.Select(x => (double)x).ToList());
        Report("пул №1 минус общий №1",           poolVsBest);
        Report("дуо №1 минус общий №1",           duoVsBest);
        Report("пул из сильных минус общий №1",   strong);
        Report("пул из слабых минус общий №1",    weak);
        Report("карточка пула минус общий список", onlyVsFull);

        Console.WriteLine();
        Console.WriteLine($"залетели в топ-6 из-за бонуса: {jumpedIn} из {jumpedCases} "
                          + $"({100.0 * jumpedIn / Math.Max(1, jumpedCases):F1}%)");

        var medBonus = Median(bonuses);
        var medGap   = Median(gaps);
        var medSpread= Median(spread6);
        Console.WriteLine();
        Console.WriteLine($"бонус пула = {medBonus / Math.Max(1e-9, medSpread) * 100:F0}% "
                          + $"от разброса первой шестёрки и {medBonus / Math.Max(1e-9, medGap):F1} "
                          + "шага между соседями");

        // ── пороги ────────────────────────────────────────────────────────
        Check("бонус пула не больше четверти разброса топ-6",
              medBonus <= 0.25 * medSpread, $"{medBonus:F2} против {0.25 * medSpread:F2}");
        Check("дуо не получает больше обычного пула",
              Math.Abs(Median(duoBonuses) - medBonus) < 1e-9, "");
        Check("медианный сдвиг не больше 3 мест",
              Median(shifts.Select(x => (double)x).ToList()) <= 3, $"{Median(shifts.Select(x => (double)x).ToList()):F1}");
        Check("в топ-6 из-за бонуса залетает меньше 15% случаев",
              100.0 * jumpedIn / Math.Max(1, jumpedCases) < 15, "");
        Check("карточка пула считается так же, как общий список",
              onlyVsFull.Count > 0 && onlyVsFull.Max(Math.Abs) < 1e-9,
              $"макс. расхождение {(onlyVsFull.Count > 0 ? onlyVsFull.Max(Math.Abs) : -1):F6}");
        Check("слабый пул остаётся слабым (не выходит вперёд общего №1)",
              Median(weak) < -1.0, $"{Median(weak):F2}");

        // Файл пулов трогать мы не должны.
        Check("пулы игрока на диск не писались", PoolStore.SaveCount == savesBefore,
              $"записей за проверку: {PoolStore.SaveCount - savesBefore}");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: перекоса нет" : $"ИТОГ: провалено проверок — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── помощники ─────────────────────────────────────────────────────────

    /// <summary>
    /// Пересчёт при заданном состоянии пулов. <paramref name="pool"/> = null —
    /// режим «Обычный», пул выключен: это база отсчёта, и подставлять её надо
    /// ЯВНО. Первая версия теста этого не делала и мерила от реального пула
    /// пользователя — тогда «чемпионы вне пула» выглядели так, будто у них
    /// отнимают 1.2, хотя это они же её и теряли.
    ///
    /// Всё живёт в ПАМЯТИ: Persist не зовём, pools.json не трогаем.
    /// </summary>
    private static Dictionary<int, Recommendation> Scored(
        RecommendationEngine engine, DraftState state, string dbRole, List<int>? pool, bool duo)
    {
        var a = PoolStore.Current();
        a.Pools.Clear(); a.DuoPools.Clear();
        if (pool is null)
        {
            a.ActiveKind = PoolKind.Normal; a.ActiveId = null;
        }
        else if (duo)
        {
            a.DuoPools.Add(new DuoPool { Id = "t", FriendName = "t", Mine = new() { [dbRole] = [.. pool] } });
            a.ActiveKind = PoolKind.Duo; a.ActiveId = "t";
        }
        else
        {
            a.Pools.Add(new ChampPool { Id = "t", Name = "t", ByRole = new() { [dbRole] = [.. pool] } });
            a.ActiveKind = PoolKind.Pool; a.ActiveId = "t";
        }
        return Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r);
    }

    /// <summary>
    /// Наигранность и дуо. Проверяем три вещи сразу:
    ///  • «комфорт» по мастерству не складывается с флором пула — берётся максимум;
    ///  • в дуо-пуле всё ровно так же, как в обычном (и для новичка, и для мейна);
    ///  • половина ДРУГА никакой прибавки не получает — это не мои кандидаты.
    /// </summary>
    private static void MasteryCheck(RecommendationEngine engine)
    {
        var state = Draft("utility", [], []);
        var plain = Scored(engine, state, "support", null, false).Values
                    .OrderByDescending(r => r.Score).ToList();
        if (plain.Count < 20) { Console.WriteLine("наигранность: мало кандидатов — пропускаю"); return; }

        var main   = plain[15].ChampionId;   // на нём будет мастерство
        var plain2 = plain[16].ChampionId;   // а этот без мастерства
        var mateId = plain[17].ChampionId;   // половина друга
        var bak    = engine.Mastery;
        var histBak = SessionTracker.HistoryOverride;
        try
        {
            engine.Mastery = new Dictionary<int, long> { [main] = 400_000 };   // глубокий мейн
            // «Глубокий мейн» — это тот, кого ИГРАЮТ: наигранность теперь
            // затухает с простоем. Без своей истории проверка висела бы на том,
            // когда владелец последний раз брал случайного саппорта: при 400k
            // очков и трёхмесячном простое комфорт падает до 1.26 — впритык к
            // флору пула (1.2), а через месяц уйдёт под него, и «пул не
            // добавляется поверх наигранности» развалится на ровном месте.
            var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SessionTracker.HistoryOverride = new SessionTracker.PlayHistory(
                new Dictionary<int, (int, int)> { [main] = (10, 5) },   // 5 из 10 — винрейт ровно 50%
                // Десять игр подряд, по одной в день: свежесть считается по
                // третьей с конца, и одной отметки времени тут не хватило бы.
                new Dictionary<int, long[]>
                {
                    [main] = [.. Enumerable.Range(0, 10).Select(i => nowTs - (long)i * 86400)],
                },
                spanDays: 400, now: nowTs);
            var baseline = Scored(engine, state, "support", null, false);

            var poolMain = Scored(engine, state, "support", [main], duo: false)[main];
            var duoMain  = Scored(engine, state, "support", [main], duo: true)[main];
            var poolNew  = Scored(engine, state, "support", [plain2], duo: false)[plain2];
            var duoNew   = Scored(engine, state, "support", [plain2], duo: true)[plain2];

            Console.WriteLine($"наигранность: комфорт мейна {baseline[main].ComfortDelta:F2}, "
                              + $"он же в пуле {poolMain.ComfortDelta:F2}, в дуо {duoMain.ComfortDelta:F2}");
            Console.WriteLine($"без наигранности: вне пула {baseline[plain2].ComfortDelta:F2}, "
                              + $"в пуле {poolNew.ComfortDelta:F2}, в дуо {duoNew.ComfortDelta:F2}");

            Check("пул не добавляется поверх наигранности",
                  Math.Abs(poolMain.ComfortDelta - baseline[main].ComfortDelta) < 1e-9,
                  $"{baseline[main].ComfortDelta:F2} → {poolMain.ComfortDelta:F2}");
            Check("дуо усиливает мейна так же, как обычный пул",
                  Math.Abs(duoMain.ComfortDelta - poolMain.ComfortDelta) < 1e-9, "");
            Check("дуо усиливает ненаигранного так же, как обычный пул",
                  Math.Abs(duoNew.ComfortDelta - poolNew.ComfortDelta) < 1e-9,
                  $"{poolNew.ComfortDelta:F2} и {duoNew.ComfortDelta:F2}");

            // Половина друга: кладём чемпиона ТОЛЬКО в набор друга.
            var a = PoolStore.Current();
            a.Pools.Clear(); a.DuoPools.Clear();
            a.DuoPools.Add(new DuoPool
            {
                Id = "t", FriendName = "t",
                Mine   = new() { ["support"] = [plain2] },
                Friend = new() { ["support"] = [mateId] },
            });
            a.ActiveKind = PoolKind.Duo; a.ActiveId = "t";
            var withFriend = Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r);

            Console.WriteLine($"половина друга: комфорт {withFriend[mateId].ComfortDelta:F2} "
                              + $"(вне пула было {baseline[mateId].ComfortDelta:F2})");
            Check("чемпион из половины ДРУГА прибавки не получает",
                  Math.Abs(withFriend[mateId].Score - baseline[mateId].Score) < 1e-9,
                  $"{baseline[mateId].Score:F2} → {withFriend[mateId].Score:F2}");
        }
        finally { engine.Mastery = bak; SessionTracker.HistoryOverride = histBak; }
    }

    /// <summary>
    /// Развёртка по силе надбавки за напарника.
    ///
    /// Меряем ДВА состояния одного драфта: союзник просто союзник и он же —
    /// половина активного дуо-пула. Прирост линеен по ОБОИМ сомножителям (в
    /// оценку входит слагаемое (k−1)·дельта_напарника·доверие), поэтому одного
    /// замера хватает, чтобы посчитать любую пару «множитель × совместные игры»
    /// без пересборки движка.
    /// </summary>
    private static void MateUpliftSweep(RecommendationEngine engine, Random rnd, double built)
    {
        // Связка должна быть НАИГРАННОЙ, иначе мерить нечего: надбавка умножается
        // на доверие по числу совместных игр, и у пустой связки она неслышна.
        // Подставляем 24 игры — столько у самого частого напарника на живой
        // истории владельца, выше в жизни практически не бывает.
        var pairBak = SessionTracker.Preview;
        SessionTracker.Preview =
        [
            new SessionTracker.PairStat(MatePuuid(1), "напарник", "solo", 0, 0,
                                        MateGamesMeasured, MateGamesMeasured / 2)
        ];

        // Замеренные драфты: для каждого — оценки без надбавки и прирост при built.
        var cases = new List<(List<int> Pool, Dictionary<int, double> Base, Dictionary<int, double> Gain)>();

        try
        {
            foreach (var (dbRole, lcuRole) in Roles)
            {
                var ids = Scored(engine, Draft(lcuRole, [], []), dbRole, null, false).Values
                          .OrderByDescending(r => r.Score).Select(r => r.ChampionId).ToList();
                if (ids.Count < 20) continue;

                for (var n = 0; n < 40; n++)
                {
                    var enemies = Pick(rnd, ids, 5);
                    var left    = ids.Except(enemies).ToList();
                    var mate    = Pick(rnd, left, 1);
                    if (mate.Count == 0) continue;
                    var allies  = new List<int> { mate[0] };
                    allies.AddRange(Pick(rnd, left.Except(mate).ToList(), 3));
                    var state   = Draft(lcuRole, allies, enemies);
                    var pool    = Pick(rnd, ids.Except(enemies).Except(allies).ToList(), PoolSize);
                    if (pool.Count < PoolSize) continue;

                    var a = PoolStore.Current();
                    a.Pools.Clear(); a.DuoPools.Clear();
                    a.DuoPools.Add(new DuoPool { Id = "t", FriendName = "t",
                                                 Mine = new() { [dbRole] = [.. pool] } });
                    a.ActiveKind = PoolKind.Duo; a.ActiveId = "t";
                    var without = Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r.Score);

                    // Напарник — ТОТ САМЫЙ человек: союзник №1, он же mate[0]. Одной
                    // россыпью чемпионов в половине друга движок напарника не считает
                    // (это правило осталось только в песочнице), поэтому опознаём его
                    // так, как в бою, — по puuid хозяина половины.
                    a.DuoPools[0].FriendPuuid = MatePuuid(1);
                    a.DuoPools[0].Friend = new() { [dbRole] = [mate[0]] };
                    var with = Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r.Score);

                    var gain = without.Keys.ToDictionary(id => id, id => with[id] - without[id]);
                    cases.Add((pool, without, gain));
                }
            }
        }
        finally { SessionTracker.Preview = pairBak; }

        if (cases.Count == 0) { Console.WriteLine("надбавка: нет сценариев"); return; }

        var measured = cases.SelectMany(c => c.Pool.Select(id => c.Gain[id])).ToList();
        Report($"замерено на сборке (k={built:F1}, {MateGamesMeasured} игр вдвоём)", measured);

        // Прирост линеен по ОБОИМ сомножителям: в оценку входит слагаемое
        // (k−1) · дельта_напарника · доверие. Поэтому одного замера хватает, чтобы
        // посчитать любую пару (k, игры) — без пересборки движка на каждый вариант.
        (List<double> Pts, List<double> Moves, int FlipPool, int FlipTop) Row(double factor)
        {
            var pts   = new List<double>();
            var moves = new List<double>();
            int flipPool = 0, flipTop = 0;

            foreach (var (pool, base_, gain) in cases)
            {
                double At(int id) => base_[id] + gain[id] * factor;
                int RankAt(int id) => base_.Keys.Count(o => At(o) > At(id)) + 1;

                foreach (var id in pool)
                {
                    pts.Add(gain[id] * factor);
                    var rBase = base_.Keys.Count(o => base_[o] > base_[id]) + 1;
                    moves.Add(Math.Abs(rBase - RankAt(id)));
                }
                if (pool.MaxBy(id => base_[id]) != pool.MaxBy(At)) flipPool++;
                if (base_.Keys.MaxBy(id => base_[id]) != base_.Keys.MaxBy(At)) flipTop++;
            }
            return (pts, moves, flipPool, flipTop);
        }

        void Head(string first)
        {
            Console.WriteLine($"  {first,-12} {"медиана",8} {"10%",8} {"90%",8}   "
                              + $"{"|сдвиг|",7} {"90%",5}   {"сменился",9} {"сменился",9}");
            Console.WriteLine($"  {"",-12} {"очки",8} {"",8} {"",8}   "
                              + $"{"мест",7} {"",5}   {"пик пула",9} {"общий №1",9}");
        }

        void Line(string label, double factor)
        {
            var (pts, moves, flipPool, flipTop) = Row(factor);
            var sp = pts.OrderBy(x => x).ToList();
            var sm = moves.OrderBy(x => x).ToList();
            Console.WriteLine($"  {label,-12} {Median(pts),8:F2} {sp[(int)(0.1 * (sp.Count - 1))],8:F2} "
                              + $"{sp[(int)(0.9 * (sp.Count - 1))],8:F2}   "
                              + $"{Median(moves),7:F1} {sm[(int)(0.9 * (sm.Count - 1))],5:F0}   "
                              + $"{100.0 * flipPool / cases.Count,8:F0}% {100.0 * flipTop / cases.Count,8:F0}%");
        }

        var confMeasured = MateConf(MateGamesMeasured);

        Console.WriteLine();
        Console.WriteLine($"НАДБАВКА ЗА НАПАРНИКА — по множителю ({cases.Count} драфтов, "
                          + $"связка наиграна: {MateGamesMeasured} игр, доверие {confMeasured:F2})");
        Head("k");
        foreach (var k in new[] { 1.0, 1.3, 1.5, 1.7, 2.0, 2.5, 3.0 })
            Line($"{k:F1}", (k - 1.0) / (built - 1.0));
        Console.WriteLine("  (надбавка двусторонняя: плохую связку она так же усиливает в минус)");

        Console.WriteLine();
        Console.WriteLine($"ТО ЖЕ — ПО ЧИСЛУ СОВМЕСТНЫХ ИГР (множитель боевой, {built:F1})");
        Head("игр вдвоём");
        foreach (var g in new[] { 0, 1, 2, 3, 6, 12, 24 })
            Line($"{g} ({MateConf(g):F2})", MateConf(g) / confMeasured);
        Console.WriteLine("  (в скобках — доверие к связке. На живой истории владельца у 250");
        Console.WriteLine("   записей из 251 ровно одна игра на пару чемпионов, поэтому порог");
        Console.WriteLine("   стоит на играх с ЧЕЛОВЕКОМ, а не на паре чемпионов)");

        Console.WriteLine();
        // Сторож тут про ШОВ, а не про величину множителя. Сравнивать замер со
        // строкой развёртки бессмысленно: при k = built доля прироста f = 1, и
        // строка равна замеру ПО ПОСТРОЕНИЮ — такой сторож не упадёт никогда.
        // Падать он обязан от другого: перестанет опознаваться напарник или
        // ужмётся доверие к связке — надбавка исчезнет целиком и все замеры
        // станут нулями. Ровно так и вышло, когда опознание ужалось до puuid:
        // развёртка мерила нули и молчала. Само значение множителя проверить
        // нечем — дельта связки считается внутри движка и наружу не выходит.
        Check("надбавка за напарника включается (напарник опознан, связка наиграна)",
              measured.Any(x => Math.Abs(x) > 1e-9),
              $"ненулевых замеров {measured.Count(x => Math.Abs(x) > 1e-9)} из {measured.Count}");
        Console.WriteLine();
    }

    /// <summary>
    /// Кого предлагаем НАПАРНИКУ, пока он молчит.
    ///
    /// Дуо-пул предлагает пару сразу после банов и вторую половину выбирает сам.
    /// Главное требование владельца: это должен быть подбор ПОД ДРАФТ, а не
    /// «лучший под мой пик». Поэтому проверяется и то, что выбор меняется от
    /// состава врагов, и то, что он не сводится к одной дельте пары.
    ///
    /// Ожидаемый выбор считается через ПУБЛИЧНЫЕ <c>PartnerScores</c> и
    /// <c>PairStats</c>, а не повторением перебора: иначе проверка сверяла бы
    /// код с его же копией.
    /// </summary>
    private static void MateSuggestCheck(RecommendationEngine engine, Random rnd)
    {
        const string myDb = "adc", myLcu = "bottom", friendDb = "support";

        var mineIds = Scored(engine, Draft(myLcu, [], []), myDb, null, false).Values
                      .OrderByDescending(r => r.Score).Select(r => r.ChampionId).ToList();
        var friendIds = Scored(engine, Draft("utility", [], []), friendDb, null, false).Values
                        .OrderByDescending(r => r.Score).Select(r => r.ChampionId).Take(8).ToList();
        var enemyPool = Scored(engine, Draft("middle", [], []), "mid", null, false).Values
                        .OrderByDescending(r => r.Score).Select(r => r.ChampionId)
                        .Where(id => !friendIds.Contains(id)).Take(20).ToList();
        if (mineIds.Count == 0 || friendIds.Count < 3 || enemyPool.Count < 10)
        {
            Console.WriteLine("подбор напарника: нет данных");
            return;
        }

        var mineId = mineIds[0];
        var half = new Dictionary<string, List<int>> { [friendDb] = [.. friendIds] };

        double PairDelta(int f) => engine.PairStats(mineId, myDb, f, friendDb).Delta;

        Console.WriteLine();
        Console.WriteLine($"ПОДБОР НАПАРНИКА: мой пик {mineId}, в половине друга {friendIds.Count} чемпионов");

        // ── Выбор считается по драфту, а не по одной паре ────────────────────
        var state  = Draft(myLcu, [], []);
        var scores = Quiet(() => engine.PartnerScores(state, half));
        var best   = engine.BestPartner(state, mineId, myDb, half, scores);

        double Total(int f) => (scores.TryGetValue(f, out var s) ? s : 0.0)
                               + WSynergyInCode * PairDelta(f);

        Check("напарник предложен из половины друга", friendIds.Contains(best), $"{best}");
        Check("предложен лучший по сумме «его сила в драфте + пара со мной»",
              Math.Abs(Total(best) - friendIds.Max(Total)) < 1e-9,
              $"{Total(best):F2} против лучшей {friendIds.Max(Total):F2}");

        // ── Зависимость от врагов ───────────────────────────────────────────
        //
        // Главный вопрос владельца: «это ведь работает в зависимости от пиков
        // союзников и врагов?». Сперва — что оценки половины вообще шевелятся от
        // состава врагов; затем — что от него меняется и сам выбор.
        var e1 = Quiet(() => engine.PartnerScores(Draft(myLcu, [], Pick(rnd, enemyPool, 5)), half));
        var e2 = Quiet(() => engine.PartnerScores(Draft(myLcu, [], Pick(rnd, enemyPool, 5)), half));
        var moved = friendIds.Count(f =>
            Math.Abs((e1.TryGetValue(f, out var a) ? a : 0) - (e2.TryGetValue(f, out var b) ? b : 0)) > 1e-9);
        Check("оценки половины друга зависят от состава врагов",
              moved > 0, $"разошлись у {moved} из {friendIds.Count}");

        // Сколько раз выбор расходится с «лучшим по одной лишь паре». Если бы не
        // расходился никогда, драфт в подборе напарника ничего не решал бы.
        var pairOnly = friendIds.OrderByDescending(PairDelta).First();
        int differs = 0, rounds = 20;
        for (var i = 0; i < rounds; i++)
        {
            var st = Draft(myLcu, [], Pick(rnd, enemyPool, 5));
            var sc = Quiet(() => engine.PartnerScores(st, half));
            if (engine.BestPartner(st, mineId, myDb, half, sc) != pairOnly) differs++;
        }
        Console.WriteLine($"  выбор отличается от «лучшего по паре» в {differs} из {rounds} драфтов");
        Check("подбор не сводится к одной дельте пары",
              differs > 0, $"{differs} из {rounds}");

        // ── Занятых и забаненных не предлагаем ──────────────────────────────
        var busy      = Draft(myLcu, [], [best]);
        var busySc    = Quiet(() => engine.PartnerScores(busy, half));
        var afterBusy = engine.BestPartner(busy, mineId, myDb, half, busySc);
        Check("занятого врагом не предлагаем",
              afterBusy != best && friendIds.Contains(afterBusy), $"{afterBusy}");

        var bannedSt  = state with { MyTeamBans = [best] };
        var afterBan  = engine.BestPartner(bannedSt, mineId, myDb, half,
                                           Quiet(() => engine.PartnerScores(bannedSt, half)));
        Check("забаненного не предлагаем", afterBan != best, $"{afterBan}");

        Check("без половины друга предложения нет",
              engine.BestPartner(state, mineId, myDb, new Dictionary<string, List<int>>()) == 0, "0");
        Console.WriteLine();
    }


    /// <summary>
    /// Порог по совместным играм — ЗАМЕРОМ, а не пересчётом.
    ///
    /// Строку «0 игр» в развёртке даёт умножение замера на ноль, и она была бы
    /// нулевой при любом движке: такой сторож не проверяет ничего. Поэтому порог
    /// гоняется отдельно — напарник опознан, связка пустая, и оценки обязаны
    /// совпасть с теми, что были БЕЗ напарника, до последнего знака.
    /// </summary>
    private static void MateGateCheck(RecommendationEngine engine, Random rnd)
    {
        var pairBak = SessionTracker.Preview;
        SessionTracker.Preview = [];          // связка есть, совместных игр в ней нет
        var worst = 0.0;
        var drafts = 0;

        try
        {
            foreach (var (dbRole, lcuRole) in Roles)
            {
                var ids = Scored(engine, Draft(lcuRole, [], []), dbRole, null, false).Values
                          .OrderByDescending(r => r.Score).Select(r => r.ChampionId).ToList();
                if (ids.Count < 20) continue;

                for (var n = 0; n < 4; n++)
                {
                    var enemies = Pick(rnd, ids, 5);
                    var left    = ids.Except(enemies).ToList();
                    var mate    = Pick(rnd, left, 1);
                    if (mate.Count == 0) continue;
                    var allies  = new List<int> { mate[0] };
                    allies.AddRange(Pick(rnd, left.Except(mate).ToList(), 3));
                    var state   = Draft(lcuRole, allies, enemies);
                    var pool    = Pick(rnd, ids.Except(enemies).Except(allies).ToList(), PoolSize);
                    if (pool.Count < PoolSize) continue;

                    var a = PoolStore.Current();
                    a.Pools.Clear(); a.DuoPools.Clear();
                    a.DuoPools.Add(new DuoPool { Id = "t", FriendName = "t",
                                                 Mine = new() { [dbRole] = [.. pool] } });
                    a.ActiveKind = PoolKind.Duo; a.ActiveId = "t";
                    var without = Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r.Score);

                    a.DuoPools[0].FriendPuuid = MatePuuid(1);
                    a.DuoPools[0].Friend = new() { [dbRole] = [mate[0]] };
                    var with = Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r.Score);

                    foreach (var id in without.Keys)
                        worst = Math.Max(worst, Math.Abs(with[id] - without[id]));
                    drafts++;
                }
            }
        }
        finally { SessionTracker.Preview = pairBak; }

        // Уберите в движке множитель на доверие — расхождение станет ненулевым,
        // и проверка назовёт это по имени.
        Check("без совместных игр надбавки нет (замер)",
              drafts > 0 && worst < 1e-9,
              $"драфтов {drafts}, максимальное расхождение {worst:F6}");
    }

    /// Потолок множителя — решение владельца «выше не надо».
    ///
    /// Проверяется ПО ИСХОДНИКУ движка, а не по замеру. Доля смены лидера
    /// меряется на живых данных и дрейфует от патча к патчу, а шаг с 1.5 до 1.7
    /// это всего 18% против 26%: порог на самом замере либо краснел бы от
    /// дрейфа, либо пропускал подъём. Исходник же не дрейфует.
    private const double DuoMateMultCeiling = 1.5;

    /// Множитель из ИСХОДНИКА движка — и сверка с копией рядом.
    ///
    /// Копия (DuoMateMultInCode) нужна развёртке для подписей и для пересчёта
    /// строк. Разойдясь с кодом, она врала бы молча: вся таблица считалась бы от
    /// одного числа, а движок работал бы с другим.
    private static void CheckMultInSource()
    {
        var root = FindRepoRoot();
        var path = root is null ? null : Path.Combine(root, "src", "Engine", "RecommendationEngine.cs");
        if (path is null || !File.Exists(path))
        {
            Check("множитель прочитан из исходника движка", false,
                  "RecommendationEngine.cs не найден — проверять нечего");
            return;
        }

        var m = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(path), @"DUO_MATE_MULT\s*=\s*([0-9]+(?:\.[0-9]+)?)");
        if (!m.Success)
        {
            Check("множитель прочитан из исходника движка", false,
                  "строка DUO_MATE_MULT не найдена");
            return;
        }

        var inSource = double.Parse(m.Groups[1].Value,
                                    System.Globalization.CultureInfo.InvariantCulture);
        Check("копия множителя совпадает с кодом движка",
              Math.Abs(inSource - DuoMateMultInCode) < 1e-9,
              $"в движке {inSource:F2}, в проверке {DuoMateMultInCode:F2}");
        Check($"множитель не выше потолка {DuoMateMultCeiling:F1}",
              inSource <= DuoMateMultCeiling + 1e-9,
              $"{inSource:F2}");

        var ws = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(path), @"W_SYNERGY\s*=\s*([0-9]+(?:\.[0-9]+)?)");
        Check("копия веса синергии совпадает с кодом движка",
              ws.Success && Math.Abs(double.Parse(ws.Groups[1].Value,
                  System.Globalization.CultureInfo.InvariantCulture) - WSynergyInCode) < 1e-9,
              ws.Success ? $"в движке {ws.Groups[1].Value}, в проверке {WSynergyInCode:F1}" : "строка не найдена");
    }

    /// Корень репозитория: от рабочей папки вверх до Counterplay.csproj.
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Counterplay.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }


    /// Сколько совместных игр подставляем, пока мерим. Столько у самого частого
    /// напарника на живой истории владельца — выше в жизни практически не бывает.
    private const int MateGamesMeasured = 24;

    /// Доверие к связке, как его считает движок (см. MATE_CONF). Копия нужна
    /// только для ПОДПИСЕЙ в развёртке: сторожа на ней не висят, так что
    /// разойдясь с кодом она ни о чём не соврёт.
    private static double MateConf(double games) => games / (games + 6.0);

    /// Синтетический puuid союзника №n. Настоящего клиента тут нет, а напарник
    /// опознаётся именно по puuid, так что хватает любой устойчивой строки.
    private static string MatePuuid(int n) => $"poolbias-ally-{n}";

    private static DraftState Draft(string lcuRole, List<int> allies, List<int> enemies)
    {
        var me = new DraftPlayer(0, 0, 0, lcuRole, true);
        var mine = new List<DraftPlayer> { me };
        // Союзникам выдаём puuid: напарника движок опознаёт по ЧЕЛОВЕКУ, а не по
        // тому, что тот взял чемпиона из половины друга (см. Party.MateChampion).
        // Без puuid надбавка за напарника не включается вовсе, и развёртка по
        // множителю мерила бы одни нули.
        for (var i = 0; i < allies.Count; i++)
            mine.Add(new DraftPlayer(i + 1, allies[i], 0, "", false, 0, MatePuuid(i + 1)));
        var theirs = enemies.Select((id, i) => new DraftPlayer(10 + i, id, 0, "", false)).ToList();
        return new DraftState(mine, theirs, [], [], me, lcuRole, null,
                              false, false, [], false, -1, false, [], -1, -1, false);
    }

    private static List<int> Pick(Random rnd, List<int> from, int n)
    {
        var copy = from.ToList();
        var outp = new List<int>();
        for (var i = 0; i < n && copy.Count > 0; i++)
        {
            var k = rnd.Next(copy.Count);
            outp.Add(copy[k]); copy.RemoveAt(k);
        }
        return outp;
    }

    private static int Rank(Dictionary<int, Recommendation> all, int id) =>
        all.Values.Count(r => r.Score > all[id].Score) + 1;

    private static void Report(string name, List<double> xs)
    {
        if (xs.Count == 0) { Console.WriteLine($"{name,-34} нет данных"); return; }
        var s = xs.OrderBy(x => x).ToList();
        Console.WriteLine($"{name,-34} медиана {Median(xs),6:F2}   " +
                          $"10% {s[(int)(0.1 * (s.Count - 1))],6:F2}   " +
                          $"90% {s[(int)(0.9 * (s.Count - 1))],6:F2}   " +
                          $"n={xs.Count}");
    }

    private static double Median(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }

    private static void Fail(string msg)
    {
        Console.WriteLine($"  [ПЛОХО] {msg}");
        _fails++;
    }

    /// Движок сыплет диагностику в консоль на каждый вызов — заглушаем.
    private static T Quiet<T>(Func<T> f)
    {
        var old = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { return f(); }
        finally { Console.SetOut(old); }
    }

    /// Боевая база (та же, что у приложения), иначе — срез из репозитория.
    /// В срезе нет таблиц матчапов и синергии, и мерить на нём нечего.
    private static string? FindDb()
    {
        var live = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "data.db");
        if (File.Exists(live) && HasTable(live, "matchup") && HasTable(live, "synergy")) return live;

        string? dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            if (dir is null) break;
            var p = Path.Combine(dir, "data-slice.db");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static bool HasTable(string db, string table)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
            cmd.Parameters.AddWithValue("@n", table);
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
        }
        catch { return false; }
    }

}
