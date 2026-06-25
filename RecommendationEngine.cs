using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record Recommendation(
    int    ChampionId,
    double Score,
    double BaseDelta,     // %пп vs 50%
    double DirectDelta,   // %пп матчап vs прямой оппонент
    double OtherDelta,    // %пп средний vs прочие враги
    double SynergyDelta,  // %пп средняя синергия с союзниками
    double ComfortDelta,  // бонус за «комфорт»: часто наигранный чемпион игрока
    string[] Reasons);

public sealed class RecommendationEngine : IDisposable
{
    // Лаплас-сглаживание: при малом числе игр тянем к 50%.
    private const double K      = 50.0; // для базового WR (данных много)
    private const double K_PAIR = 20.0; // для парных таблиц (синергия/матчап) — данных мало
    private const double PRIOR  = 0.5;
    private const double CONF_GAMES = 40.0; // темпер синергии по объёму выборки

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
    private static (double Bonus, List<string> Reasons) DraftFit(
        int champId, ChampionTraits.Arch? enemyDom, double uncertainty)
    {
        double bonus = 0;
        var reasons = new List<string>();

        // 5. Нейтральный пик — безопасен при неизвестном составе.
        if (ChampionTraits.IsNeutral(champId) && uncertainty > 0.1)
        {
            bonus += W_NEUTRAL * uncertainty;
            if (uncertainty >= 0.5)
                reasons.Add("Нейтральный пик — безопасен, пока состав врага не ясен.");
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
            bonus += W_TRIFECTA * want;

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
            bonus += W_STYLE * style;

            if (style >= 3)
                reasons.Add(dom switch
                {
                    ChampionTraits.Arch.PickPoke   => "Вкатывается на их дальнобойный состав (заход/рывки).",
                    ChampionTraits.Arch.Dive       => "Прикрывает команду от их дайва (пил/дизенгейдж).",
                    _                              => "Перебивает их фронт дальним уроном/бёрстом.",
                });
        }

        return (bonus, reasons);
    }

    // Минимум игр на роли суммарно по всем агрегируемым патчам.
    private const int MIN_GAMES = 30;

    // Минимум игр для боковых подсказок (контры/синергия в панелях команд).
    // Данные по парам очень разрежены (медиана ~2 игры), поэтому порог низкий.
    private const int HINT_MIN_GAMES = 5;

    // Количество патчей для агрегации (берём последние N).
    private const int PATCH_WINDOW = 3;

    private readonly SqliteConnection _db;
    private readonly string _p1, _p2, _p3; // последние 3 патча (могут совпадать если патчей меньше)

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
    private static bool HasData(string path)
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

    public IReadOnlyList<Recommendation> Recommend(DraftState state)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var candidates = GetCandidates(myRole);
        Console.WriteLine($"  [диаг] role={myRole}  tier={TierBucket}  патчи={PatchDisplay}  кандидатов={candidates.Count}");

        // Исключаем из кандидатов: залоченные пики обеих команд, баны, а также
        // чемпионов, наведённых (ховер) ЧУЖИМИ слотами — их уже не запикать.
        // Свой ховер не исключаем: это мой кандидат, он должен оставаться в списке.
        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            if (p.ChampionId != 0) taken.Add(p.ChampionId);
            else if (!p.IsLocalPlayer && p.PickIntentId != 0) taken.Add(p.PickIntentId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

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

        // Динамический вес синергии: без информации о врагах синергия с союзниками
        // важна так же, как контрпик против прямого оппонента.
        var knownEnemies = state.TheirTeam.Count(p => p.EffectiveChampionId != 0);
        var wSynergy = W_SYNERGY + (W_DIRECT - W_SYNERGY) * Math.Max(0.0, 1.0 - knownEnemies / 5.0);

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

        return candidates
            .Where(id => !taken.Contains(id))
            .Select(champId =>
            {
                var baseDelta = (SmoothedWr(champId, myRole) - PRIOR) * 100;

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
                var (draftBonus, draftReasons) =
                    DraftFit(champId, enemyDom, uncertainty);

                // Item value (п.1): штраф за стак уязвимости + бонус за наказание врага.
                var (vulnPen, vulnCat) = ItemValue.VulnPenalty(champId, vulnAllyIds);
                var (exploit, forced)  = ItemValue.ExploitBonus(champId, allEnemyIds);
                if (vulnPen >= 0.9 && vulnCat is { } vc)
                    draftReasons.Add($"Не стакай {ItemValue.CatName(vc)} — один предмет гасит всю команду.");
                if (exploit > 0 && forced is { } fc)
                    draftReasons.Add($"Враг застакал {ItemValue.CatName(fc)} — наказываешь его вынужденный предмет.");

                var score   = W_BASE * baseDelta + W_DIRECT * directDelta + W_OTHER * otherDelta
                            + wSynergy * synDelta + W_POOL * comfortDelta + draftBonus
                            - W_VULN * vulnPen + W_EXPLOIT * exploit;
                var reasons = BuildReasons(champId, directDelta, directOppId, synDelta, synByAlly,
                                           otherDelta, otherByEnemy, baseDelta, comfortDelta)
                                .Concat(draftReasons).ToArray();
                return new Recommendation(champId, score, baseDelta, directDelta, otherDelta, synDelta, comfortDelta, reasons);
            })
            .OrderByDescending(r => r.Score)
            .Take(6)
            .ToList();
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

    private double SmoothedWr(int champId, string role)
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
        return SmoothedAgg(cmd);
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

    /// Топ N чемпионов той же роли, лучше всего контрящих данного врага.
    /// Если роль неизвестна — запрос по всем ролям.
    public IReadOnlyList<int> TopCounters(int enemyId, string? enemyRole = null, int top = 3)
    {
        if (enemyId == 0) return [];
        try
        {
            var cmd = _db.CreateCommand();
            var roleFilter = string.IsNullOrEmpty(enemyRole) ? "" : " AND role = @r";
            cmd.CommandText = $@"
                SELECT champion_id,
                       SUM(wins + @k * @prior) * 1.0 / SUM(games + @k) AS wr
                FROM   matchup
                WHERE  vs_champion_id = @v AND tier_bucket = @t{roleFilter}
                       AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id
                HAVING SUM(games) >= @hint
                ORDER  BY wr DESC
                LIMIT  @top";
            cmd.Parameters.AddWithValue("@v",     enemyId);
            cmd.Parameters.AddWithValue("@hint",  HINT_MIN_GAMES);
            cmd.Parameters.AddWithValue("@t",     TierBucket);
            cmd.Parameters.AddWithValue("@p1",    _p1);
            cmd.Parameters.AddWithValue("@p2",    _p2);
            cmd.Parameters.AddWithValue("@p3",    _p3);
            cmd.Parameters.AddWithValue("@k",     (double)K);
            cmd.Parameters.AddWithValue("@prior", PRIOR);
            cmd.Parameters.AddWithValue("@top",   top);
            if (!string.IsNullOrEmpty(enemyRole))
                cmd.Parameters.AddWithValue("@r", enemyRole);
            var result = new List<int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) result.Add(rd.GetInt32(0));
            return result;
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
                SELECT champion_id,
                       SUM(wins + @k * @prior) * 1.0 / SUM(games + @k) AS wr
                FROM   synergy
                WHERE  ally_id = @a AND role = @r AND tier_bucket = @t
                       AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id
                HAVING SUM(games) >= @hint
                ORDER  BY wr DESC
                LIMIT  @top";
            cmd.Parameters.AddWithValue("@a",     allyId);
            cmd.Parameters.AddWithValue("@hint",  HINT_MIN_GAMES);
            cmd.Parameters.AddWithValue("@r",     myRole);
            cmd.Parameters.AddWithValue("@t",     TierBucket);
            cmd.Parameters.AddWithValue("@p1",    _p1);
            cmd.Parameters.AddWithValue("@p2",    _p2);
            cmd.Parameters.AddWithValue("@p3",    _p3);
            cmd.Parameters.AddWithValue("@k",     (double)K);
            cmd.Parameters.AddWithValue("@prior", PRIOR);
            cmd.Parameters.AddWithValue("@top",   top);
            var result = new List<int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) result.Add(rd.GetInt32(0));
            return result;
        }
        catch { return []; }
    }

    private static double SmoothedAgg(SqliteCommand cmd)
    {
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return PRIOR;
        var games = rd.GetDouble(0);
        var wins  = rd.GetDouble(1);
        return (wins + K / 2.0) / (games + K); // Лаплас
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
        if      (comfortDelta >= 5.0) lines.Add("Ты много играешь на этом чемпионе — уверенный комфорт-пик.");
        else if (comfortDelta >= 2.5) lines.Add("Знакомый чемпион из твоего пула — комфортный выбор.");

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
            if      (directDelta >=  3.0) lines.Add($"Уверенно выигрывает линию против {oppName} (+{directDelta:F1}%)");
            else if (directDelta >=  1.5) lines.Add($"Выигрывает линию против {oppName} (+{directDelta:F1}%)");
            else if (directDelta >=  0.5) lines.Add($"Небольшое преимущество против {oppName}");
            else if (directDelta <= -2.0) lines.Add($"Сложный матчап vs {oppName} ({directDelta:F1}%)");
            else                          lines.Add($"Нейтральный матчап против {oppName}");
        }

        // 3. Выгодные матчапы против конкретных врагов
        if (otherByEnemy.Count > 0)
        {
            var good = otherByEnemy.Where(x => x.Delta >= 1.5)
                                   .OrderByDescending(x => x.Delta).Take(2).ToList();
            if (good.Count > 0)
            {
                var names = string.Join(", ", good.Select(x => DataDragon.Name(x.Id)));
                lines.Add($"Выгодные матчапы против {names}");
            }
        }

        // 4. Если понятных объяснений не нашлось, но статистика хорошая —
        // мягкая общая фраза вместо сухих процентов.
        if (explained == 0 && synByAlly.Count > 0 && synDelta > 0.5)
            lines.Add("Хорошо сочетается с текущим составом твоей команды.");

        // 5. Базовый WR
        if      (baseDelta >=  2.5) lines.Add($"Один из сильнейших в патче — WR {50 + baseDelta:F1}%");
        else if (baseDelta >=  1.0) lines.Add($"Хороший базовый WR {50 + baseDelta:F1}%");
        else if (baseDelta <= -1.5) lines.Add($"Низкий WR {50 + baseDelta:F1}% — нужно знать чемпиона");

        if (lines.Count == 0) lines.Add($"Нейтральный пик, WR ≈{50 + baseDelta:F1}%");
        return [.. lines];
    }

    public void Dispose() => _db.Dispose();
}
