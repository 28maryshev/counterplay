using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — чтение таблиц базы: кандидаты, базовые винрейты, матчапы, синергия,
// пулы; сглаживание (Уилсон).
public sealed partial class RecommendationEngine
{
    // ---------- Запросы к БД — агрегируют по 3 патчам ----------

    private List<int> GetCandidates(string role)
    {
        var cmd = _db.CreateCommand();
        // Взвешенная сумма игр по 3 патчам: кандидат проходит по абсолютному порогу
        // И по доле игр роли (отсекает редкие офф-мета пики вроде Амуму-саппорта).
        cmd.CommandText = $@"
            SELECT champion_id FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id
            HAVING SUM(games*{PW}) >= @min
               AND SUM(games*{PW}) >= @share * (
                   SELECT SUM(games*{PW}) FROM base_wr
                   WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3))";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        cmd.Parameters.AddWithValue("@share", MIN_ROLE_SHARE);
        var list = new List<int>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetInt32(0));
        return list;
    }

    private (double g, double w) RawBase(int champId, string role)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM base_wr
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
    // Есть ли в базе сводка по дивизионам (старые базы её не знают).
    private bool? _hasPool;

    private bool HasPool
    {
        get
        {
            if (_hasPool is { } v) return v;
            try
            {
                var c = _db.CreateCommand();
                c.CommandText = "SELECT 1 FROM matchup WHERE tier_bucket='all' LIMIT 1";
                _hasPool = c.ExecuteScalar() is not null;
            }
            catch { _hasPool = false; }
            return _hasPool.Value;
        }
    }

    // Сводка спрашивается по разу на пару за драфт, поэтому держим её в памяти:
    // кандидатов до десяти, врагов до пяти, и каждый пересчёт лез бы в базу заново.
    private readonly Dictionary<string, (double g, double w)> _poolCache = new();

    private (double g, double w) PoolRow(string table, string where, params (string, object)[] args)
    {
        var key = table + "|" + string.Join(",", args.Select(a => a.Item2));
        if (_poolCache.TryGetValue(key, out var hit)) return hit;
        var res = (0.0, 0.0);
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM {table}
                                 WHERE {where} AND tier_bucket='all' AND patch='all'";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            res = RawAgg(cmd);
        }
        catch { /* нет сводки — работаем как раньше */ }
        _poolCache[key] = res;
        return res;
    }

    /// Подмешивает сводку по дивизионам к паре из своего бакета.
    private (double g, double w) WithPool((double g, double w) own, (double g, double w) pool)
    {
        if (pool.g < POOL_MIN) return own;         // сводки нет или она сама мала
        var rate = pool.w / pool.g;
        return (own.g + POOL_PRIOR, own.w + POOL_PRIOR * rate);
    }

    private (double g, double w) RawMatchup(int champId, string role, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM matchup
            WHERE champion_id=@c AND role=@r AND vs_champion_id=@v
              AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        var own = RawAgg(cmd);
        if (!HasPool) return own;
        return WithPool(own, PoolRow("matchup",
            "champion_id=@c AND role=@r AND vs_champion_id=@v",
            ("@c", champId), ("@r", role), ("@v", vsId)));
    }

    private readonly Dictionary<(int, string), Dictionary<int, (double g, double w)>> _matchupsOf = new();

    /// <summary>
    /// Все матчапы чемпиона на роли разом: соперник → (games, wins) нашего
    /// чемпиона против него — то же, что RawMatchup по каждой паре, со сводкой
    /// по дивизионам. Советам банов нужны все соперники каждого защищаемого, и
    /// по запросу на пару выходили сотни запросов на каждое событие драфта.
    /// Кэш на жизнь движка.
    /// </summary>
    private Dictionary<int, (double g, double w)> MatchupsOf(int champId, string role)
    {
        if (_matchupsOf.TryGetValue((champId, role), out var cached)) return cached;
        var own = new Dictionary<int, (double g, double w)>();
        var pool = new Dictionary<int, (double g, double w)>();
        // Свой бакет — с весами патчей, сводка — как есть (у неё патч 'all').
        void Read(Dictionary<int, (double g, double w)> into, string where, string weight)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT vs_champion_id, COALESCE(SUM(games*{weight}),0), COALESCE(SUM(wins*{weight}),0)
                FROM matchup WHERE champion_id=@c AND role=@r AND {where}
                GROUP BY vs_champion_id";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                into[rd.GetInt32(0)] = (Convert.ToDouble(rd.GetValue(1)), Convert.ToDouble(rd.GetValue(2)));
        }
        try
        {
            Read(own, "tier_bucket=@t AND patch IN (@p1,@p2,@p3)", PW);
            if (HasPool) Read(pool, "tier_bucket='all' AND patch='all'", "1.0");
        }
        catch { /* нет данных — пусто */ }

        var res = new Dictionary<int, (double g, double w)>();
        foreach (var vs in own.Keys.Union(pool.Keys))
            res[vs] = WithPool(own.GetValueOrDefault(vs), pool.GetValueOrDefault(vs));
        return _matchupsOf[(champId, role)] = res;
    }

    // Кросс-ролевой матчап на боте: мой чемпион (role) против вражеского дуо-партнёра
    // (vsRole). Защищено try/catch — в старых базах таблицы может не быть.
    private (double g, double w) RawBotlane(int champId, string role, int vsId, string vsRole)
    {
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM botlane_matchup
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
            var own = RawAgg(cmd);
            if (!HasPool) return own;
            return WithPool(own, PoolRow("botlane_matchup",
                "champion_id=@c AND role=@r AND vs_champion_id=@v AND vs_role=@vr",
                ("@c", champId), ("@r", role), ("@v", vsId), ("@vr", vsRole)));
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Сырые (games, wins) синергии. Обе стороны пары суммируются (champ+ally и
    /// ally+champ) — синергия симметрична, а записана она одной строкой.
    ///
    /// Роль союзника важна не меньше своей: «Ясуо на миде» и «Ясуо в боте» —
    /// разные пары, и складывать их значит усреднять то, что вместе не
    /// встречается. Замер по базе: у 11% пар число переезжает через порог
    /// кружка (+0,3), в крайних случаях разница доходит до 11 пп. Объём при
    /// этом почти не страдает — в медиане маржинал шире всего в 1,13 раза.
    ///
    /// Роль союзника неизвестна (ARAM, блайнд) или по ней пусто — откат на
    /// сумму по всем его ролям: приблизительно, но лучше, чем промолчать.
    /// </summary>
    private (double g, double w) RawSynergy(int champId, string role, int allyId,
                                            string? allyRole = null)
    {
        // Откат решаем по СВОИМ играм, а не по итогу с приором: приор добавляет
        // 25 псевдо-игр и сам по себе делает выборку «непустой». Пара, у
        // которой на этой паре линий не сыграно ничего, должна уходить на сумму
        // по ролям, а не жить на одной ставке.
        if (!string.IsNullOrEmpty(allyRole) && allyRole != role
            && SynergyOwn(champId, role, allyId, allyRole).g > 0)
            return SynergyRows(champId, role, allyId, allyRole);
        return SynergyRows(champId, role, allyId, null);
    }

    private (double g, double w) SynergyRows(int champId, string role, int allyId, string? allyRole)
    {
        var own = SynergyOwn(champId, role, allyId, allyRole);
        if (!HasPool) return own;

        // Сводка по дивизионам — это ПРИОР, то есть ставка на исход пары.
        // Берём её по той же паре ролей; если такой сводки мало (роль редкая),
        // ставку делает более широкая — лучше грубый приор, чем никакого.
        var pool = PoolPair(champId, role, allyId, allyRole);
        if (allyRole is not null && pool.g < POOL_MIN) pool = PoolPair(champId, role, allyId, null);
        return WithPool(own, pool);
    }

    // Игры пары в СВОЁМ бакете, без сводки по дивизионам.
    private (double g, double w) SynergyOwn(int champId, string role, int allyId, string? allyRole)
    {
        var byAlly = allyRole is not null;
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@c AND role=@r AND ally_id=@a
                    {(byAlly ? "AND ally_role=@ar" : "")}
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@a AND ally_id=@c AND ally_role=@r
                    {(byAlly ? "AND role=@ar" : "")}
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        if (byAlly) cmd.Parameters.AddWithValue("@ar", allyRole);
        return RawAgg(cmd);
    }

    // Сводка по дивизионам для пары — обе стороны записи, как и сам запрос выше.
    private (double g, double w) PoolPair(int champId, string role, int allyId, string? allyRole)
    {
        var byAlly = allyRole is not null;
        var a = byAlly
            ? PoolRow("synergy", "champion_id=@c AND role=@r AND ally_id=@a AND ally_role=@ar",
                      ("@c", champId), ("@r", role), ("@a", allyId), ("@ar", allyRole!))
            : PoolRow("synergy", "champion_id=@c AND role=@r AND ally_id=@a",
                      ("@c", champId), ("@r", role), ("@a", allyId));
        var b = byAlly
            ? PoolRow("synergy", "champion_id=@a AND ally_id=@c AND ally_role=@r AND role=@ar",
                      ("@a", allyId), ("@c", champId), ("@r", role), ("@ar", allyRole!))
            : PoolRow("synergy", "champion_id=@a AND ally_id=@c AND ally_role=@r",
                      ("@a", allyId), ("@c", champId), ("@r", role));
        return (a.g + b.g, a.w + b.w);
    }

    // Синергия пары с ОБЕИМИ известными ролями (обе стороны записи). Точнее, чем
    // RawSynergyAny, но выборка меньше — вызывающий делает откат при нуле игр.
    private (double g, double w) RawSynergyRoles(int m, string mRole, int f, string fRole)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@m AND role=@mr AND ally_id=@f AND ally_role=@fr
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@f AND role=@fr AND ally_id=@m AND ally_role=@mr
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@m",  m);
        cmd.Parameters.AddWithValue("@mr", mRole);
        cmd.Parameters.AddWithValue("@f",  f);
        cmd.Parameters.AddWithValue("@fr", fRole);
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
    /// Роли в том виде, в каком они лежат в базе. Нужны, чтобы подставлять роль
    /// в запрос можно было без опаски: список закрытый, чужого сюда не попадёт.
    private static readonly HashSet<string> DbRoles =
        new(StringComparer.Ordinal) { "top", "jungle", "mid", "adc", "support" };

    private static double Delta(double g, double w, double k) => ((w + k / 2.0) / (g + k) - PRIOR) * 100;

    /// Очки перевеса со знаком — в том же виде, что на карточках драфта.
    private static string Points(double v) => (v >= 0 ? "+" : "") + v.ToString("F1");

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

    /// <summary>
    /// Подзапрос «кто эту роль реально играет»: тот же отбор, что у кандидатов
    /// подбора (абсолютный порог игр И доля роли). Парные таблицы его не знают
    /// и хранят что угодно, включая Сивир на миде на пятнадцати играх, — а
    /// такой «контрпик» ни выбрать нельзя, ни проверить.
    /// </summary>
    private string RolePlayersSql => $@"
        SELECT champion_id FROM base_wr
        WHERE  role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
        GROUP  BY champion_id
        HAVING SUM(games*{PW}) >= @roleMin
           AND SUM(games*{PW}) >= @roleShare * (
               SELECT SUM(games*{PW}) FROM base_wr
               WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3))";

    private void AddRolePlayerParams(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("@t",         TierBucket);
        cmd.Parameters.AddWithValue("@roleMin",   MIN_GAMES);
        cmd.Parameters.AddWithValue("@roleShare", MIN_ROLE_SHARE);
    }
}
