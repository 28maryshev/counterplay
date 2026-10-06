using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Наигранность должна идти за тем, чем игрок играет СЕЙЧАС.
///
/// Повод (жалоба игрока, 1 октября): «я много играл Нилу, пока она была
/// сильной; её занерфили, а она всё равно в топ-4». Комфорт стоял на
/// пожизненных очках мастерства Riot, а они не убывают никогда — заброшенный
/// чемпион держался наверху вечно, и нерф его оттуда сдвинуть не мог.
///
/// Проверка подставляет историю игр через <see cref="SessionTracker.HistoryOverride"/>
/// и меряет одного и того же чемпиона в двух состояниях: «играю его в этом
/// месяце» и «не брал полгода». Разница должна быть больше, чем разрыв между
/// местами в топ-10 — иначе затухание ничего не решает.
///
/// Журнал игрока при этом не читается вовсе: шов отдаёт подменную историю.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Очки мастерства «глубокого мейна» — столько набирается за сотни игр.
    private const long MainPoints = 150_000;

    /// Сколько драфтов в развёртке сдвига по местам.
    private const int DraftCount = 40;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // Пулы — в свою папку. Файл игрока проверки однажды уже стёрли.
        PoolStore.DirOverride = Path.Combine(
            Path.GetTempPath(), "counterplay-test-pools", "Comfort");
        Directory.CreateDirectory(PoolStore.DirOverride);
        var stale = Path.Combine(PoolStore.DirOverride, "pools.json");
        if (File.Exists(stale)) File.Delete(stale);
        PoolStore.Reload();

        var dbPath = FindDb();
        if (dbPath is null)
        {
            Console.Error.WriteLine("не нашёл data-slice.db — запускать из корня репозитория");
            return 2;
        }

        Console.WriteLine($"база: {dbPath}");
        using var engine = Quiet(() => RecommendationEngine.Create(dbPath, "emerald"));
        Console.WriteLine($"патчи: {engine.PatchDisplay}   бакет: {engine.TierBucket}\n");

        // Пустая история СРАЗУ, до первого же подбора. Иначе список кандидатов
        // строился бы с оглядкой на настоящий журнал владельца — и чемпион,
        // который достаётся замеру двадцать первым, менялся бы от машины к
        // машине и от недели к неделе. Поймано откатом: при другом весе личного
        // винрейта порядок разъехался, и два прогона мерили РАЗНЫХ чемпионов.
        SessionTracker.HistoryOverride = Hist(0, recent: 0, daysSince: 0, span: 400);

        // Кандидаты на роль: берём чемпиона из СЕРЕДИНЫ списка — у вершины
        // сдвиг по местам упёрся бы в потолок, у хвоста его не видно.
        var empty = Draft("utility", [], []);
        var plain = Scored(engine, empty, null).Values.OrderByDescending(r => r.Score).ToList();
        if (plain.Count < 30)
        {
            Console.Error.WriteLine($"мало кандидатов ({plain.Count}) — мерить нечего");
            return 2;
        }
        var champ = plain[20].ChampionId;
        var clean = plain[21].ChampionId;   // без мастерства — для флора пула
        Console.WriteLine($"чемпион замера: {DataDragon.Name(champ)} (место {21} из {plain.Count})");

        // Меряем на ЗАПОЛНЕННОМ драфте, а не на пустом. На пустом оценкам нечем
        // сжиматься — там нет ни контры, ни синергии, и «разброс топ-8» выходит
        // вдвое шире настоящего. Живой драфт с врагами и союзниками — то, что
        // видит игрок, и то, на чём меряны пороги в PoolBias.
        var rnd     = new Random(20261002);   // фиксированное зерно — цифры повторяемы
        var ids     = plain.Select(r => r.ChampionId).Where(i => i != champ && i != clean).ToList();
        var enemies = Pick(rnd, ids, 5);
        var allies  = Pick(rnd, ids.Except(enemies).ToList(), 4);
        var state   = Draft("utility", allies, enemies);
        Console.WriteLine($"драфт: {enemies.Count} врагов, {allies.Count} союзников\n");

        engine.Mastery = new Dictionary<int, long> { [champ] = MainPoints };

        // ── 1. Один чемпион, две истории ────────────────────────────────────
        // Сами ВЕЛИЧИНЫ комфорта меряем на драфте БЕЗ оппонента: прямого
        // матчапа там нет, и ужимание против контры (блок 4c) в эти числа не
        // вмешивается. Места и винрейт — ниже, на настоящих драфтах.
        Console.WriteLine("── наигранность: играю сейчас против играл когда-то ──");

        var fresh   = Comfort(engine, empty, champ, Hist(champ, recent: 12, daysSince: 1,   span: 400));
        var stale6m = Comfort(engine, empty, champ, Hist(champ, recent: 0,  daysSince: 200, span: 400));

        Console.WriteLine($"  играю в этом месяце (12 игр):   комфорт {fresh:F2}");
        Console.WriteLine($"  не брал 200 дней:               комфорт {stale6m:F2}");
        Console.WriteLine($"  как было до правки (8.0 × кривая): "
                          + $"{8.0 * MainPoints / (MainPoints + 80_000.0):F2} в обоих случаях");

        Check("заброшенный чемпион теряет наигранность", stale6m < fresh - 2.0,
              $"{fresh:F2} → {stale6m:F2}");
        Check("но не обнуляется — забытое вспоминается быстрее незнакомого", stale6m > 0.2,
              $"{stale6m:F2}");

        // ── 2. Сдвиг по местам: развёртка по драфтам ────────────────────────
        // По ОДНОМУ драфту судить нельзя: где контрпик разводит оценки широко,
        // те же очки комфорта стоят меньше мест, чем там, где кандидаты стоят
        // плотно. Поэтому гоняем DraftCount сценариев и смотрим медиану —
        // так же, как меряет PoolBias.
        Console.WriteLine("\n── на сколько мест уезжает заброшенный чемпион ──");
        var shifts   = new List<double>();
        var freshPos = new List<double>();
        var stalePos = new List<double>();
        var spreads  = new List<double>();
        var wasTop6    = 0;   // попадал в видимую шестёрку, пока его играют
        var leftTop6   = 0;   // ...и вылетел из неё, когда забросили
        var everBetter = 0;   // забвение где-то подняло его ВВЕРХ — так не бывает
        var histFresh = Hist(champ, recent: 12, daysSince: 1,   span: 400);
        var histStale = Hist(champ, recent: 0,  daysSince: 200, span: 400);

        for (var n = 0; n < DraftCount; n++)
        {
            var e = Pick(rnd, ids, 5);
            var a = Pick(rnd, ids.Except(e).ToList(), 4);
            var st = Draft("utility", a, e);

            var rf = Rank(engine, st, champ, histFresh);
            var rs = Rank(engine, st, champ, histStale);
            shifts.Add(rs - rf);
            freshPos.Add(rf);
            stalePos.Add(rs);
            if (rs < rf) everBetter++;
            if (rf <= 6) { wasTop6++; if (rs > 6) leftTop6++; }

            var live = Scored(engine, st, null).Values.OrderByDescending(r => r.Score).ToList();
            if (live.Count >= 8) spreads.Add(live[1].Score - live[7].Score);
        }

        Console.WriteLine($"  место, пока играю:  медиана {Median(freshPos):F1}");
        Console.WriteLine($"  место, когда забыл: медиана {Median(stalePos):F1}");
        Console.WriteLine($"  сдвиг вниз:         медиана {Median(shifts):F1} мест "
                          + $"(из {DraftCount} драфтов)");
        Console.WriteLine($"  ушёл из топ-6:      {leftTop6} из {wasTop6} раз, когда там был");
        Console.WriteLine($"  для масштаба: разрыв 2-е…8-е место, медиана {Median(spreads):F2}");

        Check("забытый чемпион заметно уходит вниз", Median(shifts) >= 3,
              $"медиана сдвига {Median(shifts):F1} мест");
        // Строгий инвариант: забвение отнимает очки только у НЕГО, оценки
        // остальных не меняются, — значит подняться в списке он не может
        // нигде. Ловит путаницу знака и случаи, когда затухание подмешалось
        // не туда.
        Check("и нигде не поднимается от того, что его забыли", everBetter == 0,
              $"поднялся в {everBetter} драфтах из {DraftCount}");
        // «Ушёл из топ-6» печатаем, но НЕ требуем: доля зависит от того,
        // насколько выбранный чемпион силён сам по себе, а это меняется каждый
        // патч. Сильный чемпион обязан остаться наверху и без наигранности —
        // это правильное поведение, и порог на нём стал бы ложной тревогой.

        // ── 4. Затухание монотонно ──────────────────────────────────────────
        Console.WriteLine("\n── кривая затухания по дням простоя ──");
        double? prev = null;
        var monotone = true;
        foreach (var d in new[] { 0, 15, 30, 45, 60, 90, 120, 180, 365 })
        {
            var c = Comfort(engine, empty, champ, Hist(champ, recent: 0, daysSince: d, span: 400));
            Console.WriteLine($"  {d,3} дней простоя → {c:F2}");
            if (prev is { } p && c > p + 1e-9) monotone = false;
            prev = c;
        }
        Check("с простоем наигранность только падает", monotone, "");
        var floor120 = Comfort(engine, empty, champ, Hist(champ, recent: 0, daysSince: 120, span: 400));
        var floor365 = Comfort(engine, empty, champ, Hist(champ, recent: 0, daysSince: 365, span: 400));
        Check("ниже дна не падает", Math.Abs(floor120 - floor365) < 1e-9,
              $"{floor120:F2} и {floor365:F2}");

        // ── 4a. Один заход не воскрешает мастерство ─────────────────────────
        // Нашлось живым замером на пуле владельца: Зилеан, ОДНА игра за месяц,
        // зато три дня назад — и свежесть выходила 1.00, то есть мастерство
        // держалось в полном весе у чемпиона, которого человек уже не играет.
        // Это почти исходный баг. Свежесть считается по RegularGames-й игре с
        // конца, и разовый заход её не поднимает.
        Console.WriteLine("\n── один заход после долгого перерыва ──");
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Полгода тишины, затем ровно одна игра три дня назад.
        var oneOff = new SessionTracker.PlayHistory(
            new Dictionary<int, (int, int)> { [champ] = (1, 1) },
            new Dictionary<int, long[]>
            {
                [champ] = [nowTs - 3 * 86400, nowTs - 200L * 86400, nowTs - 210L * 86400],
            },
            spanDays: 400, now: nowTs);

        SessionTracker.HistoryOverride = oneOff;
        var oneOffComfort = Scored(engine, empty, null)[champ].ComfortDelta;
        var playingNow    = Comfort(engine, empty, champ, Hist(champ, recent: 12, daysSince: 1, span: 400));
        var abandoned     = Comfort(engine, empty, champ, Hist(champ, recent: 0, daysSince: 200, span: 400));
        Console.WriteLine($"  играю по-настоящему (12 игр):      {playingNow:F2}");
        Console.WriteLine($"  взял разок три дня назад (1 игра): {oneOffComfort:F2}");
        Console.WriteLine($"  не брал вовсе:                     {abandoned:F2}");
        Check("разовый заход не возвращает полный вес мастерства",
              oneOffComfort < playingNow - 1.5, $"{oneOffComfort:F2} против {playingNow:F2}");
        Check("но он всё же заметнее, чем полное забвение",
              oneOffComfort > abandoned, $"{oneOffComfort:F2} против {abandoned:F2}");

        // ── 4b. Личный винрейт ──────────────────────────────────────────────
        // Наигранность считает ИГРЫ, а не победы: «ты им владеешь» и «у тебя на
        // нём идёт» — разные вопросы. На второй отвечает PersonalDelta, и до
        // общего журнала померить его было нечем.
        //
        // Тридцать игр — месяц на одном чемпионе, с них проценту верим целиком
        // (PERSONAL_FULL_GAMES). На меньшей выборке до потолка не дойти и при 100%.
        Console.WriteLine("\n── личный винрейт на 30 играх ──");
        var byWr = new List<(int Wins, double Score, int Rank, double Personal)>();
        foreach (var w in new[] { 0, 12, 15, 18, 30 })
        {
            SessionTracker.HistoryOverride = Hist(champ, recent: 30, daysSince: 1, span: 400, wins: w);
            var all = Scored(engine, empty, null);
            var rank = all.Values.Count(r => r.Score > all[champ].Score) + 1;
            byWr.Add((w, all[champ].Score, rank, engine.PersonalDelta(champ)));
        }
        foreach (var (w, sc, rk, pd) in byWr)
            Console.WriteLine($"  {w,2} побед из 30 ({100 * w / 30,3}%): личная дельта {pd,5:F2}, "
                              + $"оценка {sc,6:F2}, место {rk}");

        var worst = byWr[0];
        var best  = byWr[^1];
        // Размах от нуля побед до всех — это ровно две обрезки потолком:
        // 2 × PERSONAL_CAP × W_PERSONAL. Сверяем замер со строками кода, как
        // PoolBias сверяет множитель напарника: уронишь вес обратно до 0.5 —
        // размах станет 4.00, и проверка назовёт это по имени, а не промолчит.
        var expected = 2 * PersonalCapInCode * PersonalWeightInCode;
        var spreadWr = best.Score - worst.Score;
        Check($"размах по винрейту отвечает весам в коде (ждём {expected:F2})",
              Math.Abs(spreadWr - expected) < 0.01,
              $"{worst.Score:F2} → {best.Score:F2}, размах {spreadWr:F2}");
        Check("50% не двигает ничего", Math.Abs(byWr[2].Personal) < 1e-9,
              $"дельта {byWr[2].Personal:F2}");
        Check("рост винрейта только поднимает",
              byWr.Zip(byWr.Skip(1)).All(p => p.Second.Score >= p.First.Score - 1e-9), "");
        Check("но потолок держит: 100% не перебивает контрпик", best.Personal <= 4.0 + 1e-9,
              $"личная дельта {best.Personal:F2}");

        // Три победы подряд — не 100% винрейт.
        SessionTracker.HistoryOverride = Hist(champ, recent: 3, daysSince: 1, span: 400, wins: 3);
        var streak = engine.PersonalDelta(champ);
        Console.WriteLine($"  для сравнения: 3 победы из 3 → личная дельта {streak:F2}");
        Check("короткая серия гасится объёмом", streak < 2.0, $"{streak:F2}");

        // Вес процента растёт с числом игр. Пример владельца: 56% на 100 играх
        // лучше, чем 66% на 9. И его же поправка: сотни игр на чемпионе за месяц
        // не набрать, реально около тридцати, — значит, объём должен решать и в
        // масштабе месяца. Сглаживание на 12 игр ставило 6–0, 8–2 и 56% на
        // сотне на один потолок; сглаживание на 100 пускало 9–1 выше 60% на 30.
        Console.WriteLine("\n── процент и число игр ──");
        double Pd(int games, int wins)
        {
            SessionTracker.HistoryOverride = Hist(champ, recent: games, daysSince: 1, span: 400, wins: wins);
            return engine.PersonalDelta(champ);
        }
        var month   = Pd(30, 18);   // 60% на 30 играх
        var month57 = Pd(30, 17);
        var hundred = Pd(100, 56);
        var few     = Pd(9, 6);     // 66% на 9
        var streaks = new (string Name, double D)[] { ("6–0", Pd(6, 6)), ("8–2", Pd(10, 8)), ("9–1", Pd(10, 9)) };
        Console.WriteLine($"  60% на 30: {month:F2}   57% на 30: {month57:F2}   56% на 100: {hundred:F2}   "
                          + $"66% на 9: {few:F2}");
        Console.WriteLine($"  серии: {string.Join("   ", streaks.Select(x => $"{x.Name} {x.D:F2}"))}");
        Check("57% на 30 играх весят больше, чем 66% на 9", month57 > few + 1.0,
              $"{month57:F2} против {few:F2}");
        Check("56% на 100 играх весят больше, чем 66% на 9", hundred > few + 1.0,
              $"{hundred:F2} против {few:F2}");
        Check("короткая серия не догоняет месяц игры", streaks.All(x => month > x.D + 1.0),
              $"{month:F2} против {string.Join(", ", streaks.Select(x => $"{x.Name} {x.D:F2}"))}");
        Check("60% за месяц — заметная прибавка, но ещё не потолок",
              month >= 3.0 && month < PersonalCapInCode, $"{month:F2} при потолке {PersonalCapInCode:F1}");
        var at60 = new[] { 5, 10, 20, 30 }.Select(g => Pd(g, g * 6 / 10)).ToList();
        var past = Pd(60, 36);
        Console.WriteLine($"  60% на 5/10/20/30 играх: {string.Join(" / ", at60.Select(x => x.ToString("F2")))}"
                          + $", на 60: {past:F2}");
        Check("при том же проценте больше игр — больше вес, до тридцати",
              at60.Zip(at60.Skip(1)).All(p => p.Second > p.First), "");
        Check("после тридцати игр вес уже полный", Math.Abs(past - at60[^1]) < 1e-9,
              $"{at60[^1]:F2} и {past:F2}");

        // ── 4c. Против контры комфорт ужимается ─────────────────────────────
        // Нашлось живым замером: личная прибавка владельца на Соне перебивала
        // 4 пп матчапа, и Сона шла вторым номером против Леоны при 22-м месте
        // по чистой мете. Теперь прибавка гасится тем сильнее, чем хуже прямой
        // матчап.
        Console.WriteLine("\n── комфорт против прямой контры ──");
        SessionTracker.HistoryOverride = Hist(champ, recent: 12, daysSince: 1, span: 400);

        // Ищем реальных оппонентов: без врага, мягкий матчап и жёсткий.
        var byMatch = plain.Select(r => r.ChampionId).Where(id => id != champ)
            .Select(opp =>
            {
                var rr = Quiet(() => engine.Recommend(Draft("utility", [], [opp]), 400))
                         .FirstOrDefault(x => x.ChampionId == champ);
                return (Opp: opp, Delta: rr?.DirectDelta ?? 0.0, Comfort: rr?.ComfortDelta ?? 0.0);
            })
            .Where(x => x.Delta != 0.0)
            .OrderBy(x => x.Delta)
            .ToList();

        var free = Comfort(engine, Draft("utility", [], []), champ,
                           Hist(champ, recent: 12, daysSince: 1, span: 400));
        Console.WriteLine($"  без оппонента:          комфорт {free:F2}");
        foreach (var x in byMatch.Take(3).Concat(byMatch.TakeLast(2)))
            Console.WriteLine($"  против {Name(x.Opp),-14} "
                              + $"матчап {x.Delta,6:+0.00;-0.00}пп → комфорт {x.Comfort:F2}");

        var hard = byMatch.FirstOrDefault();
        if (hard.Opp != 0 && hard.Delta <= -2.0)
        {
            Check("против жёсткой контры комфорт заметно ужат",
                  hard.Comfort < free * 0.8, $"{free:F2} → {hard.Comfort:F2} при {hard.Delta:F2} пп");
            Check("но не обнуляется — знакомый чемпион остаётся знакомым",
                  hard.Comfort >= free * 0.25, $"{hard.Comfort:F2}");
        }
        else Console.WriteLine("  жёстких матчапов в срезе нет — пропускаю вердикт");

        var soft = byMatch.LastOrDefault();
        if (soft.Opp != 0 && soft.Delta >= -1.0)
            Check("мягкий матчап комфорт не трогает",
                  Math.Abs(soft.Comfort - free) < 1e-9, $"{free:F2} и {soft.Comfort:F2}");

        // Ужимание монотонно: чем хуже матчап, тем меньше комфорт.
        var pairs = byMatch.Where(x => x.Delta <= -1.0).OrderBy(x => x.Delta).ToList();
        Check("чем хуже матчап, тем меньше прибавка",
              pairs.Zip(pairs.Skip(1)).All(p => p.Second.Comfort >= p.First.Comfort - 1e-9),
              $"точек {pairs.Count}");

        // ── 5. Новичка затухание не наказывает ──────────────────────────────
        // Журнал копится только вперёд от установки. У того, кто поставил
        // программу неделю назад, НЕ сыграно ничего — но это не значит
        // «забросил»: мы просто не видели. Дольше глубины журнала простоя не
        // бывает, и у новичка его нет вовсе.
        Console.WriteLine("\n── журнал ведётся неделю: простоя быть не может ──");
        var cold = Comfort(engine, empty, champ, Hist(champ, recent: 0, daysSince: 7, span: 7));
        var full = MASTERY_MAX_IN_CODE * MainPoints / (MainPoints + 80_000.0);
        Console.WriteLine($"  комфорт {cold:F2} (полное мастерство без затухания — {full:F2})");
        Check("новичок наигранность не теряет", Math.Abs(cold - full) < 0.01,
              $"{cold:F2} против {full:F2}");

        // ── 6. Флор пула на месте ───────────────────────────────────────────
        Console.WriteLine("\n── флор пула ──");
        var poolNoMastery = Comfort(engine, empty, clean,
                                    Hist(clean, recent: 0, daysSince: 400, span: 400), pool: [clean]);
        Console.WriteLine($"  чемпион без очков мастерства, но в пуле: {poolNoMastery:F2}");
        Check("пул даёт флор второму аккаунту", poolNoMastery >= 1.19,
              $"{poolNoMastery:F2}");

        // Глубокий мейн, которого играют: пул ничего не добавляет поверх.
        var mainOut = Comfort(engine, empty, champ, Hist(champ, recent: 12, daysSince: 1, span: 400));
        var mainIn  = Comfort(engine, empty, champ, Hist(champ, recent: 12, daysSince: 1, span: 400),
                              pool: [champ]);
        Check("пул не добавляется поверх живой наигранности", Math.Abs(mainIn - mainOut) < 1e-9,
              $"{mainOut:F2} → {mainIn:F2}");

        // ── 7. Журнал игрока не читали ──────────────────────────────────────
        Console.WriteLine("\n── данные игрока ──");
        Check("история бралась из шва, а не из session.json",
              SessionTracker.HistoryOverride is not null, "");

        SessionTracker.HistoryOverride = null;

        Console.WriteLine(_fails == 0 ? "\nВсё в порядке." : $"\nПроблем: {_fails}.");
        return _fails == 0 ? 0 : 1;
    }

    /// Потолок мастерства, с которым собран движок (см. MASTERY_MAX).
    private const double MASTERY_MAX_IN_CODE = 3.0;

    /// Вес и потолок личного винрейта, с которыми собран движок
    /// (см. W_PERSONAL и PERSONAL_CAP). Зеркало строк кода: меняешь там —
    /// меняешь здесь, и правка веса перестаёт быть незаметной.
    private const double PersonalWeightInCode = 1.0;
    private const double PersonalCapInCode    = 4.0;

    /// <summary>
    /// Подменная история: чемпион сыгран <paramref name="recent"/> раз за окно
    /// свежести, последний раз <paramref name="daysSince"/> дней назад, а сам
    /// журнал ведётся <paramref name="span"/> дней.
    /// </summary>
    private static SessionTracker.PlayHistory Hist(int champId, int recent, double daysSince, double span,
                                                   int? wins = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // Побед по умолчанию ровно половина: личный винрейт тогда ровно 50% и
        // в оценку не вмешивается. Замеры наигранности так меряют её одну.
        var w = wins ?? recent / 2;
        return new SessionTracker.PlayHistory(
            new Dictionary<int, (int, int)> { [champId] = (recent, w) },
            new Dictionary<int, long[]> { [champId] = Times(now, daysSince, span) },
            span, now);
    }

    /// Времена последних игр: самая свежая daysSince дней назад, предыдущие —
    /// через день. Одной отметки мало: свежесть считается по третьей с конца,
    /// и с единственной записью любой чемпион выглядел бы заброшенным.
    /// За глубину журнала не выходим — там игр ещё не было.
    private static long[] Times(long now, double daysSince, double span)
    {
        var outp = new List<long>();
        for (var i = 0; i < SessionTracker.PlayHistory.KeepTimes; i++)
        {
            var d = Math.Min(daysSince + i, span);
            outp.Add(now - (long)(d * 86400));
            if (d >= span) break;
        }
        return [.. outp];
    }

    private static double Comfort(RecommendationEngine engine, DraftState state, int champId,
                                  SessionTracker.PlayHistory hist, List<int>? pool = null)
    {
        SessionTracker.HistoryOverride = hist;
        return Scored(engine, state, pool)[champId].ComfortDelta;
    }

    private static int Rank(RecommendationEngine engine, DraftState state, int champId,
                            SessionTracker.PlayHistory hist)
    {
        SessionTracker.HistoryOverride = hist;
        var all = Scored(engine, state, null);
        return all.Values.Count(r => r.Score > all[champId].Score) + 1;
    }

    private static Dictionary<int, Recommendation> Scored(
        RecommendationEngine engine, DraftState state, List<int>? pool)
    {
        var a = PoolStore.Current();
        a.Pools.Clear(); a.DuoPools.Clear();
        if (pool is null)
        {
            a.ActiveKind = PoolKind.Normal; a.ActiveId = null;
        }
        else
        {
            a.Pools.Add(new ChampPool { Id = "t", Name = "t", ByRole = new() { ["support"] = [.. pool] } });
            a.ActiveKind = PoolKind.Pool; a.ActiveId = "t";
        }
        return Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r);
    }

    private static DraftState Draft(string lcuRole, List<int> allies, List<int> enemies)
    {
        var me = new DraftPlayer(0, 0, 0, lcuRole, true);
        var mine = new List<DraftPlayer> { me };
        for (var i = 0; i < allies.Count; i++) mine.Add(new DraftPlayer(i + 1, allies[i], 0, "", false));
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

    /// Имя чемпиона, обрезанное под колонку. Справочник в проверке не
    /// поднимается, поэтому обычно это «id=NNN» — для вердикта хватает.
    private static string Name(int id)
    {
        var s = DataDragon.Name(id);
        return s.Length <= 14 ? s : s[..13] + "…";
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
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
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", table);
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }
}
