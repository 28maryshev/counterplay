using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record BanRec(int ChampionId, double Score, string[] Reasons);

public sealed record Recommendation(
    int    ChampionId,
    double Score,
    double BaseDelta,     // %пп vs 50%
    double DirectDelta,   // %пп матчап vs прямой оппонент
    double OtherDelta,    // %пп средний vs прочие враги
    double SynergyDelta,  // %пп средняя синергия с союзниками
    double ComfortDelta,  // бонус за «комфорт»: часто наигранный чемпион игрока
    double StyleDelta,    // вклад «против стиля врага» (трифекта + анти-стиль)
    string[] Reasons,
    int    Rank   = 0,    // место в полном списке (1 = лучший)
    bool   IsMyPick = false); // это мой уже выбранный/наведённый чемпион

public sealed class RecommendationEngine : IDisposable
{
    // Лаплас-сглаживание: при малом числе игр тянем к 50%.
    private const double K      = 50.0; // для базового WR (данных много)
    private const double K_PAIR = 20.0; // для парных таблиц (синергия/матчап) — данных мало
    private const double PRIOR  = 0.5;
    private const double CONF_GAMES = 60.0;  // темпер синергии по объёму выборки
    private const double BASE_CONF  = 250.0; // темпер базового WR по объёму выборки

    private const double W_BASE    = 1.0;
    private const double W_DIRECT  = 2.5;
    private const double W_OTHER   = 0.8;
    private const double W_SYNERGY = 1.2;
    private const double W_POOL     = 1.0; // вес «комфорта» (наигранность чемпиона)
    private const double W_NEUTRAL  = 1.0; // нейтральный пик при неопределённости
    private const double W_TRIFECTA = 0.8; //архетип-контра (камень-ножницы-бумага)
    private const double W_STYLE    = 0.6; // анти-стиль: инструменты против компы врага
    private const double W_VULN     = 2.0; // штраф за стак одной уязвимости в команде
    private const double W_EXPLOIT  = 1.0; // бонус за наказание вынужденного предмета врага
    private const double W_STRUCT   = 1.6; // структурная синергия с ключевым тиммейтом (jg↔линия, адк↔сапп)
    private const double W_DMGBAL   = 1.2; // баланс типа урона (AD/AP): не стакать один тип
    private const double W_BOTLANE      = 1.5; // контрпик против вражеского дуо на боте (2v2)
    private const double W_BOTLANE_BOTH = 1.8; // когда виден весь вражеский бот (адк+сапп)
    // Порог включения бот-матчапов: сумма игр в botlane_matchup. ~20k записей —
    // это примерно столько же матчей с собранной бот-статистикой (по ~1 на пару).
    private const double BOTLANE_MIN_GAMES = 20000;

    // Веса рекомендации банов.
    private const double W_BAN_META    = 1.0; // сила чемпиона в патче (WR)
    private const double W_BAN_POP     = 1.0; // популярность (часто пикается)
    private const double W_BAN_COUNTER = 2.0; // насколько контрит твой пул (усилено)
    private const double W_BAN_ALLY    = 1.3; // контрит уже показанный пик союзника

    // Очки мастерства игрока (championId → points) из LCU. Пусто = без учёта пула.
    public IReadOnlyDictionary<int, long> Mastery { get; set; } =
        new Dictionary<int, long>();

    // Бонус за наигранность: сатурирующая кривая 0..~8 (200k очков ≈ +5.7).
    private double ComfortDelta(int champId) =>
        Mastery.TryGetValue(champId, out var pts) && pts > 0
            ? 8.0 * pts / (pts + 80_000.0)
            : 0.0;

    // Драфт-фичи: нейтральный пик (при неопределённости), трифекта композиций
    // и анти-стиль (инструменты против доминирующего архетипа врага).
    // Возвращает суммарный взвешенный бонус к score и причины для UI.
    // StyleScore — отдельно вклад «против стиля врага» (трифекта+анти-стиль), для показа в карточке.
    private static (double Bonus, double StyleScore, List<string> Reasons) DraftFit(
        int champId, ChampionTraits.Arch? enemyDom, double uncertainty)
    {
        double bonus = 0, styleScore = 0;
        var reasons = new List<string>();

        // 5. Нейтральный пик — безопасен при неизвестном составе.
        if (ChampionTraits.IsNeutral(champId) && uncertainty > 0.1)
        {
            bonus += W_NEUTRAL * uncertainty;
            if (uncertainty >= 0.5)
                reasons.Add(Loc.T("reason.neutral"));
        }

        if (enemyDom is { } dom)
        {
            var (f2b, dive, pick) = ChampionTraits.Archetype(champId);

            // 2. Трифекта: что нужно взять, чтобы побить доминанту врага.
            //    dive ← frontToBack, pickPoke ← dive, frontToBack ← pickPoke.
            var want = dom switch
            {
                ChampionTraits.Arch.Dive       => f2b,
                ChampionTraits.Arch.PickPoke   => dive,
                _                              => pick, // FrontToBack ← pickPoke
            };

            // 3. Анти-стиль: конкретные инструменты против стиля врага.
            double style = dom switch
            {
                ChampionTraits.Arch.PickPoke =>
                    ChampionTraits.Gapclose(champId) + ChampionTraits.Engage(champId),
                ChampionTraits.Arch.Dive =>
                    ChampionTraits.Peel(champId) + ChampionTraits.Disengage(champId),
                _ /* FrontToBack */ =>
                    (ChampionTraits.LongRange(champId) ? 2 : 0) + ChampionTraits.Burst(champId),
            };

            styleScore = W_TRIFECTA * want + W_STYLE * style;
            bonus += styleScore;

            if (style >= 3)
                reasons.Add(Loc.T(dom switch
                {
                    ChampionTraits.Arch.PickPoke   => "reason.antiPickPoke",
                    ChampionTraits.Arch.Dive       => "reason.antiDive",
                    _                              => "reason.antiFront",
                }));
        }

        return (bonus, styleScore, reasons);
    }

    // Минимум игр на роли суммарно по всем агрегируемым патчам.
    private const int MIN_GAMES = 30;

    // Боковые подсказки (контры у врагов / синергия у союзников) ранжируем по
    // нижней границе Уилсона и показываем только при достаточной выборке. Иначе
    // всплывал шум: пара на 10–15 играх со случайным перевесом выдавалась за
    // «контру» (напр. Сион «контрил» Кейл на 16 играх, хотя реально проигрывает).
    private const int    HINT_MIN_GAMES = 30;   // минимум совместных игр на пару
    private const double HINT_MIN_EDGE  = 0.50; // показываем, только если LB Уилсона > 50%
    private const double WILSON_Z       = 1.28; // ~80% односторонняя уверенность

    // Количество патчей для агрегации (берём последние N).
    private const int PATCH_WINDOW = 3;

    private readonly SqliteConnection _db;
    private readonly string _p1, _p2, _p3; // последние 3 патча (могут совпадать если патчей меньше)
    private readonly bool _botlaneReady;   // достаточно ли бот-данных, чтобы их учитывать

    public string TierBucket  { get; }
    public string PatchDisplay { get; } // "16.12, 16.11, 16.10" для вывода

    private RecommendationEngine(SqliteConnection db, string tierBucket, string[] patches)
    {
        _db          = db;
        TierBucket   = tierBucket;
        PatchDisplay = string.Join(", ", patches);
        // Заполняем до 3 слотов: если патчей меньше — дублируем последний (IN без эффекта)
        _p1 = patches.Length > 0 ? patches[0] : "0.0";
        _p2 = patches.Length > 1 ? patches[1] : _p1;
        _p3 = patches.Length > 2 ? patches[2] : _p2;

        // Бот-матчапы (#4) включаем только когда накоплено достаточно бот-данных.
        // Считаем по объёму записей botlane_matchup, а НЕ по общему числу матчей:
        // старые матчи бот-статистику не содержат, их учитывать нельзя.
        _botlaneReady = BotlaneTotalGames(db) >= BOTLANE_MIN_GAMES;
    }

    // Сумма игр в botlane_matchup (0, если таблицы нет — старая база).
    private static double BotlaneTotalGames(SqliteConnection db)
    {
        try
        {
            var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(games),0) FROM botlane_matchup";
            var r = cmd.ExecuteScalar();
            return r is null or DBNull ? 0 : Convert.ToDouble(r);
        }
        catch { return 0; }
    }

    // Порядок фолбэка: если в базе нет данных для нужного бакета, берём ближайший.
    private static readonly string[] BucketFallback = ["silver", "gold", "emerald", "master"];

    public static RecommendationEngine Create(string dbPath, string tierBucket)
    {
        // Не используем Mode=ReadOnly — WAL-режим требует доступа к -shm файлу даже для чтения.
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();

        // Берём последние PATCH_WINDOW патчей (корректная версионная сортировка).
        var patchCmd = db.CreateCommand();
        patchCmd.CommandText = @"
            SELECT DISTINCT patch FROM base_wr
            ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
                     CAST(SUBSTR(patch, INSTR(patch, '.') + 1)     AS INTEGER) DESC
            LIMIT @n";
        patchCmd.Parameters.AddWithValue("@n", PATCH_WINDOW);
        var patches = new List<string>();
        using (var rd = patchCmd.ExecuteReader())
            while (rd.Read()) patches.Add(rd.GetString(0));

        if (patches.Count == 0) patches.Add("0.0");

        // Проверяем бакет по последнему патчу; если нет данных — ищем ближайший.
        var effectiveBucket = tierBucket;
        var checkCmd = db.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM base_wr WHERE tier_bucket=@t AND patch=@p";
        checkCmd.Parameters.AddWithValue("@t", tierBucket);
        checkCmd.Parameters.AddWithValue("@p", patches[0]);
        var count = (long)(checkCmd.ExecuteScalar() ?? 0L);

        if (count == 0)
        {
            foreach (var b in BucketFallback)
            {
                checkCmd.Parameters["@t"].Value = b;
                var n = (long)(checkCmd.ExecuteScalar() ?? 0L);
                if (n > 0) { effectiveBucket = b; break; }
            }
            Console.WriteLine($"  [предупреждение] Нет данных для бакета '{tierBucket}' — использую '{effectiveBucket}'.");
        }

        return new RecommendationEngine(db, effectiveBucket, [.. patches]);
    }

    // Ищет data.db рядом с exe, потом в pipeline/.
    // Пропускает пустые/неинициализированные файлы (без таблицы base_wr) —
    // иначе пустой data.db в рабочей папке перекрывал бы настоящую базу.
    public static string? FindDb()
    {
        var candidates = new[]
        {
            "data.db",
            Path.Combine("pipeline", "data.db"),
            Path.Combine(AppContext.BaseDirectory, "data.db"),
            Path.Combine(AppContext.BaseDirectory, "pipeline", "data.db"),
            // Постоянное место для установленной версии (скачивается при первом запуске).
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "Counterplay", "data.db"),
            @"C:\Counterplay\pipeline\data.db",
        };
        return candidates.FirstOrDefault(p => File.Exists(p) && HasData(p));
    }

    // Валиден ли файл БД: ненулевой размер и есть таблица base_wr с данными.
    public static bool HasData(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return false;
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='base_wr')";
            return (long)(cmd.ExecuteScalar() ?? 0L) == 1;
        }
        catch { return false; }
    }

    // LCU position → ключ роли в БД
    public static string LcuToDbRole(string pos) => pos.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "middle"  => "mid",
        "bottom"  => "adc",
        "utility" => "support",
        _         => pos
    };

    public IReadOnlyList<Recommendation> Recommend(DraftState state, int topN = 6)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var candidates = GetCandidates(myRole);
        Console.WriteLine($"  [диаг] role={myRole}  tier={TierBucket}  патчи={PatchDisplay}  кандидатов={candidates.Count}");

        // Исключаем из кандидатов: залоченные пики обеих команд, баны, а также
        // чемпионов, наведённых (ховер) ЧУЖИМИ слотами — их уже не запикать.
        // СВОЙ пик/ховер НЕ исключаем: хочу видеть его в списке (насколько «угадал»).
        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            if (p.IsLocalPlayer) continue;            // мой слот — оставляем в подборе
            if (p.ChampionId != 0) taken.Add(p.ChampionId);
            else if (p.PickIntentId != 0) taken.Add(p.PickIntentId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        // Мой уже выбранный (или наведённый) чемпион — чтобы пометить и гарантированно показать.
        var myPickId = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;

        // Учитываем ховеры (EffectiveChampionId): рекомендации обновляются ещё
        // на этапе наведения чемпиона союзником/врагом, не дожидаясь лока.
        var directOppId = state.DirectOpponent?.EffectiveChampionId ?? 0;
        // Роли врагов скрыты (Blind/Solo-Duo) → прямого оппонента по позиции нет.
        // Определяем его эвристикой: враг, у которого частая роль = моей.
        if (directOppId == 0)
            directOppId = InferDirectOpponent(state, myRole);

        var otherEnemyIds = state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0 && p.EffectiveChampionId != directOppId)
            .Select(p => p.EffectiveChampionId).ToList();
        var allyData = state.MyTeam
            .Where(p => p.EffectiveChampionId != 0 && !p.IsLocalPlayer)
            .Select(p => (Id: p.EffectiveChampionId, Role: LcuToDbRole(p.Position))).ToList();

        // Бот — это 2v2: при адк/саппорте контрим и вражеского дуо-партнёра.
        // Пример: вражеский Эзреаль (адк) контрит Блицкранга (саппорт) — он сблинкуется
        // с хука, поэтому Блиц получит штраф против такого бота.
        var duoRole = myRole == "adc" ? "support" : myRole == "support" ? "adc" : null;
        var enemyDuoId = duoRole == null ? 0 : state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0 && LcuToDbRole(p.Position) == duoRole)
            .Select(p => p.EffectiveChampionId).FirstOrDefault();
        // Если виден ВЕСЬ вражеский бот (и прямой оппонент, и дуо) — даём боту
        // чуть больший вес: пик можно подогнать под известный 2v2.
        var wBotlane = (duoRole != null && enemyDuoId != 0 && directOppId != 0)
            ? W_BOTLANE_BOTH : W_BOTLANE;

        // Динамический вес синергии: без информации о врагах синергия с союзниками
        // важнее, но НЕ настолько, чтобы перебить сильный базовый пик шумной парной
        // статистикой (потолок 1.9, не полный W_DIRECT=2.5) — иначе чемпион на
        // десятке совместных игр всплывает в топ при пустой вражеской команде.
        const double W_SYN_MAX = 1.9;
        var knownEnemies = state.TheirTeam.Count(p => p.EffectiveChampionId != 0);
        var wSynergy = W_SYNERGY + (W_SYN_MAX - W_SYNERGY) * Math.Max(0.0, 1.0 - knownEnemies / 5.0);

        // ── Драфт-фичи (архетип/нейтральность) ──────────────────────────────
        var allEnemyIds = state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0).Select(p => p.EffectiveChampionId).ToList();
        // Неопределённость драфта: 1.0 если врагов не видно, 0 если все 5 + есть оппонент.
        var uncertainty = Math.Clamp(
            1.0 - knownEnemies / 5.0 + (directOppId == 0 ? 0.2 : 0.0), 0.0, 1.0);
        // Доминирующий архетип врага определяем при 2+ известных пиках.
        ChampionTraits.Arch? enemyDom = allEnemyIds.Count >= 2
            ? ChampionTraits.Dominant(allEnemyIds) : null;

        // Item value (п.1): союзники (без меня) и враги для профиля уязвимостей.
        var vulnAllyIds = allyData.Select(a => a.Id).ToList();

        // Структурная синергия (п.4): id союзных джанглера и АДК.
        var jungleAllyId = allyData.FirstOrDefault(a => a.Role == "jungle").Id;
        var adcAllyId    = allyData.FirstOrDefault(a => a.Role == "adc").Id;

        var ordered = candidates
            .Where(id => !taken.Contains(id))
            .Select(champId =>
            {
                // Базовый WR + темпер по выборке: мало игр → WR ближе к 50%
                // (иначе «68% на 100 играх» раздувает оценку и обоснование).
                var (bg, bw)  = RawBase(champId, myRole);
                var baseDelta = Delta(bg, bw, K) * (bg / (bg + BASE_CONF));

                // Прямой оппонент — отдельный матчап
                var (dg, dw)    = directOppId != 0 ? RawMatchup(champId, myRole, directOppId) : (0, 0);
                var directDelta = directOppId != 0 ? Delta(dg, dw, K_PAIR) : 0.0;

                // Прочие враги: дельта по каждому (для reasons) + ПУЛ (для скора).
                // Пул складывает игры/победы по всем врагам и сглаживает один раз —
                // редкие пары не обнуляются делением на количество врагов.
                var otherRaw = otherEnemyIds
                    .Select(e => { var (g, w) = RawMatchup(champId, myRole, e); return (Id: e, G: g, W: w); })
                    .ToList();
                var otherByEnemy = otherRaw.Select(x => (x.Id, Delta: Delta(x.G, x.W, K_PAIR))).ToList();
                var otherDelta = otherRaw.Count > 0
                    ? Delta(otherRaw.Sum(x => x.G), otherRaw.Sum(x => x.W), K_PAIR) : 0.0;

                // Синергия с союзниками: то же — дельта по каждому + ПУЛ для скора.
                var synRaw = allyData
                    .Select(a => { var (g, w) = RawSynergy(champId, myRole, a.Id); return (a.Id, a.Role, G: g, W: w); })
                    .ToList();
                var synByAlly = synRaw.Select(x => (x.Id, x.Role, Delta: Delta(x.G, x.W, K_PAIR))).ToList();
                var synGames = synRaw.Sum(x => x.G);
                // Темпер по выборке: мало совместных игр → синергии меньше доверия,
                // чтобы «чемпионы на 8 играх» не доминировали при ×2.5 без врагов.
                var synConf  = synGames / (synGames + CONF_GAMES);
                var synDelta = synRaw.Count > 0
                    ? Delta(synGames, synRaw.Sum(x => x.W), K_PAIR) * synConf : 0.0;

                var comfortDelta = ComfortDelta(champId); // наигранность игрока

                // Драфт-фичи: нейтральность, трифекта, анти-стиль.
                var (draftBonus, styleScore, draftReasons) =
                    DraftFit(champId, enemyDom, uncertainty);

                // Item value (п.1): штраф за стак уязвимости + бонус за наказание врага.
                var (vulnPen, vulnCat, vulnCnt) = ItemValue.VulnPenalty(champId, vulnAllyIds);
                var (exploit, forced)  = ItemValue.ExploitBonus(champId, allEnemyIds);
                if (vulnPen >= 0.9 && vulnCat is { } vc)
                {
                    var tail = vulnCnt >= 4 ? Loc.T("reason.vulnNearAll")
                             : vulnCnt == 3 ? Loc.T("reason.vuln3")
                             :                Loc.T("reason.vuln2");
                    draftReasons.Add(Loc.T("reason.dontStack", ItemValue.CatName(vc), tail));
                }
                if (exploit > 0 && forced is { } fc)
                    draftReasons.Add(Loc.T("reason.exploit", ItemValue.CatName(fc)));

                // Баланс типа урона (AD/AP): штраф за стак одного типа (≥3), бонус за
                // разбавление. Сильнее против танков; при 4-м одном типе — заметно вниз.
                var (dmgDelta, dmgStack, dmgAd) = ItemValue.DamageBalance(champId, vulnAllyIds, allEnemyIds);
                if (dmgStack)
                    draftReasons.Add(Loc.T("reason.dmgStack", Loc.T(dmgAd ? "cat.physical" : "cat.magic")));
                else if (dmgDelta > 0.5)
                    draftReasons.Add(Loc.T("reason.dmgBalance", Loc.T(dmgAd ? "cat.physical" : "cat.magic")));

                // Структурная синергия джангл↔саппорт (п.4).
                var (structBonus, structReasons) = StructuralBonus(champId, myRole, jungleAllyId, adcAllyId);
                draftReasons.AddRange(structReasons);

                // Бот 2v2: матчап против вражеского дуо-партнёра (кросс-роль).
                var (btg, btw)   = _botlaneReady && enemyDuoId != 0 && duoRole != null
                    ? RawBotlane(champId, myRole, enemyDuoId, duoRole) : (0.0, 0.0);
                var botlaneDelta = btg > 0 ? Delta(btg, btw, K_PAIR) : 0.0;
                if (botlaneDelta >= 1.0)
                    draftReasons.Add(Loc.T("reason.botlaneGood", DataDragon.Name(enemyDuoId)));
                else if (botlaneDelta <= -2.0)
                    draftReasons.Add(Loc.T("reason.botlaneBad", DataDragon.Name(enemyDuoId)));

                var score   = W_BASE * baseDelta + W_DIRECT * directDelta + W_OTHER * otherDelta
                            + wSynergy * synDelta + W_POOL * comfortDelta + draftBonus
                            - W_VULN * vulnPen + W_EXPLOIT * exploit + W_STRUCT * structBonus
                            + wBotlane * botlaneDelta + W_DMGBAL * dmgDelta;
                var reasons = BuildReasons(champId, directDelta, directOppId, synDelta, synByAlly,
                                           otherDelta, otherByEnemy, baseDelta, comfortDelta)
                                .Concat(draftReasons).ToArray();
                return new Recommendation(champId, score, baseDelta, directDelta, otherDelta, synDelta, comfortDelta, styleScore, reasons);
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        // Топ-6: мой уже выбранный чемпион остаётся в подборе наравне со всеми и
        // помечается, но НЕ пиннится — если из-за новых пиков он вышел из топа,
        // он выпадает из списка так же, как любой другой кандидат.
        return ordered.Take(topN)
            .Select((r, i) => r with { Rank = i + 1, IsMyPick = r.ChampionId == myPickId })
            .ToList();
    }

    // ---------- Рекомендация банов ----------

    /// Кого банить: сильные/популярные в патче чемпионы твоей роли, которые ещё и
    /// плохи лично для тебя (контрят твой пул). Возвращает топ-N.
    public IReadOnlyList<BanRec> RecommendBans(DraftState state, int top = 5)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var stats = RoleStats(myRole);            // champId → (games, wins)
        if (stats.Count == 0) return [];
        var maxGames = stats.Values.Max(v => v.Games);

        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
            if (p.EffectiveChampionId != 0) taken.Add(p.EffectiveChampionId);
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        // Мои мейны на этой роли (по мастерству) — чтобы банить их контр-пики.
        var myMains = Mastery
            .Where(kv => stats.ContainsKey(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(5).Select(kv => kv.Key).ToList();

        var scores  = new Dictionary<int, double>();
        var reasons = new Dictionary<int, List<string>>();
        void AddReason(int id, string r)
        {
            if (!reasons.TryGetValue(id, out var l)) reasons[id] = l = [];
            if (!l.Contains(r)) l.Add(r);
        }

        // 1. Сила в патче + популярность + контр-пики моего пула (кандидаты моей роли).
        foreach (var x in stats.Keys)
        {
            if (taken.Contains(x)) continue;
            var (g, w) = stats[x];
            var metaWr = ((w + K / 2.0) / (g + K) - PRIOR) * 100 * (g / (g + BASE_CONF));
            var pop    = maxGames > 0 ? g / maxGames : 0;

            // Насколько x бьёт мой пул: берём САМЫЙ контрящий мейн (не среднее) —
            // чтобы поймать «против Соны/Сораки стабильно выигрывает Леона».
            int beatMain = 0; double counterMe = 0;
            foreach (var m in myMains)
            {
                var (mg, mw) = RawMatchup(m, myRole, x);
                if (mg <= 0) continue;
                var s = -Delta(mg, mw, K_PAIR);
                if (s > counterMe) { counterMe = s; beatMain = m; }
            }

            var score = W_BAN_META * metaWr + W_BAN_POP * (pop * 10) + W_BAN_COUNTER * counterMe;
            if (score <= 0) continue;
            scores[x] = score;

            if (counterMe >= 1.5 && beatMain != 0)
                AddReason(x, Loc.T("reason.countersPool", DataDragon.Name(beatMain)));
            if (metaWr >= 1.5) AddReason(x, Loc.T("reason.strongPatch", $"{50 + metaWr:F1}"));
            if (pop >= 0.6)    AddReason(x, Loc.T("reason.oftenPicked"));
        }

        // 2. Защита союзников: банить тех, кто контрит уже показанные пики союзников.
        foreach (var ally in state.MyTeam)
        {
            var aid = ally.EffectiveChampionId;
            if (aid == 0 || ally.IsLocalPlayer) continue;
            var aRole = LcuToDbRole(ally.Position);
            foreach (var c in TopCounters(aid, string.IsNullOrEmpty(aRole) ? null : aRole, 4))
            {
                if (taken.Contains(c)) continue;
                var (mg, mw) = RawMatchup(aid, aRole, c);
                if (mg <= 0) continue;
                var strength = -Delta(mg, mw, K_PAIR); // насколько c бьёт союзника
                if (strength < 1.0) continue;
                scores[c] = scores.GetValueOrDefault(c) + W_BAN_ALLY * strength;
                AddReason(c, Loc.T("reason.countersAlly", DataDragon.Name(aid)));
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(top)
            .Select(kv =>
            {
                var rs = reasons.GetValueOrDefault(kv.Key) ?? [];
                if (rs.Count == 0) rs.Add(Loc.T("reason.notablePick"));
                return new BanRec(kv.Key, kv.Value, [.. rs]);
            })
            .ToList();
    }

    // Суммарные (games, wins) по всем кандидатам роли за окно патчей.
    private Dictionary<int, (double Games, double Wins)> RoleStats(string role)
    {
        var result = new Dictionary<int, (double, double)>();
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT champion_id, SUM(games), SUM(wins) FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id HAVING SUM(games) >= @min";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            result[rd.GetInt32(0)] = (rd.GetDouble(1), rd.GetDouble(2));
        return result;
    }

    // Структурные правила синергии джангл↔саппорт (п.4): не из статистики, а из
    // логики команды («агро-джанглеру нужен сетап», «оллин-АДК нужен инициатор»).
    private static (double Bonus, List<string> Reasons) StructuralBonus(
        int champId, string myRole, int jungleAllyId, int adcAllyId)
    {
        double b = 0;
        var reasons = new List<string>();

        // Союзный джанглер агрессивен, но без своего жёсткого контроля → ему нужен
        // лейнер с CC-сетапом и приоритетом; слабый ранний рушит его план.
        if (jungleAllyId != 0 &&
            ChampionTraits.EarlyPower(jungleAllyId) >= 2 && ChampionTraits.HardCc(jungleAllyId) == 0)
        {
            if (ChampionTraits.HardCc(champId) >= 1)
            {
                b += 1;
                reasons.Add(Loc.T("reason.jgSetup", DataDragon.Name(jungleAllyId)));
            }
            if (ChampionTraits.EarlyPower(champId) >= 2) b += 0.5;
            if (ChampionTraits.EarlyPower(champId) == 0) b -= 1;
        }

        // Саппорт под стиль АДК.
        if (myRole == "support" && adcAllyId != 0)
        {
            if (ChampionTraits.EngageDependentAdc(adcAllyId) && ChampionTraits.Engage(champId) >= 2)
            {
                b += 1;
                reasons.Add(Loc.T("reason.adcEngage", DataDragon.Name(adcAllyId)));
            }
            if (ChampionTraits.ScaleAdcCarry(adcAllyId) && ChampionTraits.Peel(champId) >= 2)
            {
                b += 1;
                reasons.Add(Loc.T("reason.adcScale", DataDragon.Name(adcAllyId)));
            }
        }

        return (b, reasons);
    }

    // Прямой оппонент, когда роли врагов скрыты: ищем врага, чья ЧАСТАЯ роль
    // совпадает с моей. Если на мою роль никто не подходит — оппонента нет.
    private int InferDirectOpponent(DraftState state, string myRole)
    {
        foreach (var p in state.TheirTeam)
        {
            var id = p.EffectiveChampionId;
            if (id == 0) continue;
            if (InferPrimaryRole(id) == myRole) return id;
        }
        return 0;
    }

    private readonly Dictionary<int, string> _roleCache = new();

    // Самая частая роль чемпиона по числу игр (base_wr), с кэшем.
    private string InferPrimaryRole(int champId)
    {
        if (_roleCache.TryGetValue(champId, out var cached)) return cached;
        var role = "";
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT role FROM base_wr
                WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY role ORDER BY SUM(games) DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            role = cmd.ExecuteScalar() as string ?? "";
        }
        catch { /* нет данных — пустая роль */ }
        _roleCache[champId] = role;
        return role;
    }

    // ---------- Запросы к БД — агрегируют по 3 патчам ----------

    private List<int> GetCandidates(string role)
    {
        var cmd = _db.CreateCommand();
        // Суммируем игры по всем 3 патчам — кандидат проходит если набрал MIN_GAMES суммарно.
        cmd.CommandText = @"
            SELECT champion_id FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id
            HAVING SUM(games) >= @min";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        var list = new List<int>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetInt32(0));
        return list;
    }

    private (double g, double w) RawBase(int champId, string role)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM base_wr
            WHERE champion_id=@c AND role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // Сырые (games, wins) матчапа — направленно: мой чемпион против vsId.
    private (double g, double w) RawMatchup(int champId, string role, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM matchup
            WHERE champion_id=@c AND role=@r AND vs_champion_id=@v
              AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // Кросс-ролевой матчап на боте: мой чемпион (role) против вражеского дуо-партнёра
    // (vsRole). Защищено try/catch — в старых базах таблицы может не быть.
    private (double g, double w) RawBotlane(int champId, string role, int vsId, string vsRole)
    {
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM botlane_matchup
                WHERE champion_id=@c AND role=@r AND vs_champion_id=@v AND vs_role=@vr
                  AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@v",  vsId);
            cmd.Parameters.AddWithValue("@vr", vsRole);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            return RawAgg(cmd);
        }
        catch { return (0, 0); }
    }

    // Сырые (games, wins) синергии. Данные крайне редки, поэтому:
    //  • маржинализуем роль союзника (любой ally_role / role у обратной записи),
    //  • суммируем ОБЕ стороны пары (champ+ally и ally+champ) — синергия симметрична.
    // Это максимизирует выборку. Пул по союзникам делает вызывающий код.
    private (double g, double w) RawSynergy(int champId, string role, int allyId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games, wins FROM synergy
                  WHERE champion_id=@c AND role=@r AND ally_id=@a
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games, wins FROM synergy
                  WHERE champion_id=@a AND ally_id=@c AND ally_role=@r
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    private static (double g, double w) RawAgg(SqliteCommand cmd)
    {
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return (0, 0);
        return (rd.GetDouble(0), rd.GetDouble(1));
    }

    // Лаплас-дельта в процентных пунктах относительно 50%.
    private static double Delta(double g, double w, double k) => ((w + k / 2.0) / (g + k) - PRIOR) * 100;

    // Нижняя граница доверительного интервала Уилсона для доли побед: штрафует
    // малые выборки, поэтому редкие пары не всплывают как «контра»/«синергия».
    private static double WilsonLower(double wins, double games)
    {
        if (games <= 0) return 0;
        double p = wins / games, z = WILSON_Z, z2 = z * z;
        double centre = p + z2 / (2 * games);
        double margin = z * Math.Sqrt(p * (1 - p) / games + z2 / (4 * games * games));
        return (centre - margin) / (1 + z2 / games);
    }

    // (champion_id, games, wins) → ранг по Уилсону, только уверенно выгодные пары.
    // Команда уже отфильтрована по HINT_MIN_GAMES; пусто — ничего не показываем.
    private static List<int> RankByWilson(SqliteCommand cmd, int top)
    {
        var scored = new List<(int Id, double Lb)>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
            {
                double g = Convert.ToDouble(rd.GetValue(1));
                double w = Convert.ToDouble(rd.GetValue(2));
                var lb = WilsonLower(w, g);
                if (lb > HINT_MIN_EDGE) scored.Add((rd.GetInt32(0), lb));
            }
        return scored.OrderByDescending(x => x.Lb).Take(top).Select(x => x.Id).ToList();
    }

    /// Топ N чемпионов той же роли, лучше всего контрящих данного врага.
    /// Если роль неизвестна — запрос по всем ролям.
    public IReadOnlyList<int> TopCounters(int enemyId, string? enemyRole = null, int top = 3)
    {
        if (enemyId == 0) return [];
        try
        {
            var cmd = _db.CreateCommand();
            var roleFilter = string.IsNullOrEmpty(enemyRole) ? "" : " AND role = @r";
            // По всем дивизионам сразу: данные по парам разрежены, фильтр по бакету
            // добил бы выборку. Ранжируем/фильтруем по Уилсону в RankByWilson.
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games) AS g, SUM(wins) AS w
                FROM   matchup
                WHERE  vs_champion_id = @v{roleFilter} AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id
                HAVING SUM(games) >= @min";
            cmd.Parameters.AddWithValue("@v",   enemyId);
            cmd.Parameters.AddWithValue("@min", HINT_MIN_GAMES);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            if (!string.IsNullOrEmpty(enemyRole))
                cmd.Parameters.AddWithValue("@r", enemyRole);
            return RankByWilson(cmd, top);
        }
        catch { return []; }
    }

    /// Топ N чемпионов МОЕЙ роли с наилучшей синергией с данным союзником.
    public IReadOnlyList<int> TopSynergies(int allyId, string myRole, int top = 3)
    {
        if (allyId == 0 || string.IsNullOrEmpty(myRole)) return [];
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT champion_id, SUM(games) AS g, SUM(wins) AS w
                FROM   synergy
                WHERE  ally_id = @a AND role = @r AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id
                HAVING SUM(games) >= @min";
            cmd.Parameters.AddWithValue("@a",   allyId);
            cmd.Parameters.AddWithValue("@min", HINT_MIN_GAMES);
            cmd.Parameters.AddWithValue("@r",   myRole);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            return RankByWilson(cmd, top);
        }
        catch { return []; }
    }

    // ---------- Обоснование ----------

    private static string[] BuildReasons(
        int    champId,
        double directDelta, int    directOppId,
        double synDelta,    IReadOnlyList<(int Id, string Role, double Delta)> synByAlly,
        double otherDelta,  IReadOnlyList<(int Id, double Delta)> otherByEnemy,
        double baseDelta,   double comfortDelta)
    {
        var lines = new List<string>();

        // 0. Комфорт: часто наигранный чемпион игрока — упоминаем первым.
        if      (comfortDelta >= 5.0) lines.Add(Loc.T("reason.comfortHigh"));
        else if (comfortDelta >= 2.5) lines.Add(Loc.T("reason.comfortMid"));

        // 1. Развёрнутые объяснения синергии с конкретными союзниками.
        // Сначала пары с наибольшей статистической синергией. Дедупим по ТИПУ
        // связки — чтобы не повторять одинаковые по смыслу фразы. До 2 объяснений.
        var seenKinds = new HashSet<string>();
        foreach (var a in synByAlly.OrderByDescending(x => x.Delta))
        {
            var ex = TeamSynergies.ExplainPair(champId, a.Id, a.Role);
            if (ex is null) continue;
            if (!seenKinds.Add(ex.Value.Kind)) continue; // тот же тип связки уже показан
            lines.Add(ex.Value.Text);
            if (seenKinds.Count >= 2) break;
        }
        var explained = seenKinds.Count;

        // 2. Матчап с прямым оппонентом
        if (directOppId != 0)
        {
            var oppName = DataDragon.Name(directOppId);
            if      (directDelta >=  3.0) lines.Add(Loc.T("reason.lineWinStrong", oppName, $"{directDelta:F1}"));
            else if (directDelta >=  1.5) lines.Add(Loc.T("reason.lineWin", oppName, $"{directDelta:F1}"));
            else if (directDelta >=  0.5) lines.Add(Loc.T("reason.lineEdge", oppName));
            else if (directDelta <= -2.0) lines.Add(Loc.T("reason.lineHard", oppName, $"{directDelta:F1}"));
            else                          lines.Add(Loc.T("reason.lineNeutral", oppName));
        }

        // 3. Выгодные матчапы против конкретных врагов
        if (otherByEnemy.Count > 0)
        {
            var good = otherByEnemy.Where(x => x.Delta >= 1.5)
                                   .OrderByDescending(x => x.Delta).Take(2).ToList();
            if (good.Count > 0)
            {
                var names = string.Join(", ", good.Select(x => DataDragon.Name(x.Id)));
                lines.Add(Loc.T("reason.favMatchups", names));
            }
        }

        // 4. Если понятных объяснений не нашлось, но статистика хорошая —
        // мягкая общая фраза вместо сухих процентов.
        if (explained == 0 && synByAlly.Count > 0 && synDelta > 0.5)
            lines.Add(Loc.T("reason.fitsTeam"));

        // 5. Базовый WR
        if      (baseDelta >=  2.5) lines.Add(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}"));
        else if (baseDelta >=  1.0) lines.Add(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}"));
        else if (baseDelta <= -1.5) lines.Add(Loc.T("reason.baseLow", $"{50 + baseDelta:F1}"));

        if (lines.Count == 0) lines.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));
        return [.. lines];
    }

    public void Dispose() => _db.Dispose();
}
