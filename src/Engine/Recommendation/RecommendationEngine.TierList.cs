using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — тир-лист патча, бан-рейты, тир-лист банов, винрейт в патче.
public sealed partial class RecommendationEngine
{
    /// <summary>Строка тир-листа: чемпион, роль, винрейт, игры, пик/бан-рейт, грейд.</summary>
    public readonly record struct TierEntry(
        int ChampionId, string Role, double Winrate, int Games,
        double PickRate, double BanRate, char Grade);

    // Тир-лист показываем только на достаточной выборке (иначе метрики шумят).
    private const int TIER_MIN_GAMES = 250;

    // Грейд не по чистому винрейту, а по META-SCORE — сила (нижняя граница
    // Уилсона) + ПРИСУТСТВИЕ (пик- и бан-рейт). Иначе наверх лезут низкопикрейтные
    // однотрики с раздутым WR, а реальные мета-пики (их все пикают/банят при
    // ~49-51% WR) проваливаются. Веса откалиброваны по нашим данным.
    private const double W_PICK = 1.2;   // вклад пик-рейта в meta (откалибровано по данным)

    private const double W_BAN  = 0.4;   // вклад бан-рейта (бан-рейты крупнее пик-рейтов)

    private const double BAN_CAP = 25.0; // потолок бан-рейта в meta: 79%-бан нового

                                         // чемпиона не должен один тащить его в топ
    private const double TIER_MIN_SHARE = 0.01; // мин. пик-рейт роли (1%) — отсекаем

                                         // почти не пикаемых (новые/офф-мета)

    // Грейд — по РАНГУ meta-score внутри роли: доля чемпионов роли выше данного.
    // Ранговая шкала устойчива к масштабу meta (с банами или без) и всегда даёт
    // спред S..D. Пороги дают «пирамиду»: S ≈ верх 5%, дальше шире.
    private static char GradeOfRank(double frac) =>
        frac < 0.05 ? 'S' : frac < 0.14 ? 'A' : frac < 0.30 ? 'B' : frac < 0.55 ? 'C' : 'D';

    /// <summary>
    /// Тир-лист патча для текущего бакета: топ-N чемпионов на каждой роли,
    /// грейд S..D по рангу. Два режима ранжирования:
    ///   byWinrate=false — по meta-score (сила + присутствие пик/бан) — «тир»;
    ///   byWinrate=true  — по чистому винрейту (нижняя граница Уилсона).
    /// Роли — top→jgl→mid→adc→sup.
    /// </summary>
    public IReadOnlyList<TierEntry> TierList(int perRole = 15, bool byWinrate = false)
    {
        var result = new List<TierEntry>();

        // Тир-лист считаем по ТЕКУЩЕМУ патчу (@p1), без смеси 3 патчей — чтобы WR
        // совпадал с тем, что видят игроки на других сайтах. Смесь остаётся у
        // движка рекомендаций (там выборки тоньше); удержание патча (см. Create)
        // гарантирует, что @p1 уже набрал достаточно данных.
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            // Ключ ранжирования = то, что показываем: в WR-столбце сортируем по
            // сырому винрейту (иначе выше по %-у стоит ниже — путаница), в тире
            // по meta-score. Мелкие выборки уже отсечены порогом TIER_MIN_GAMES.
            var scored = TierRows(role).OrderByDescending(s => byWinrate ? s.Wr : s.Meta).ToList();

            // Грейд — по рангу в роли; показываем top-N (уже отсортированы).
            for (int i = 0; i < scored.Count && i < perRole; i++)
            {
                var s = scored[i];
                char grade = GradeOfRank((double)i / scored.Count);
                result.Add(new TierEntry(s.Id, role, s.Wr, s.Games, s.Pick, s.Ban, grade));
            }
        }
        return result;
    }

    // Строка тир-листа роли до сортировки: винрейт, игры, пик/бан-рейт, meta-score.
    private readonly record struct TierRow(int Id, double Wr, int Games, double Pick, double Ban, double Meta);

    /// Все чемпионы роли, прошедшие пороги тир-листа, с их meta-score — в
    /// текущем патче и бакете. Порядок не задан: сортирует тот, кто зовёт.
    private List<TierRow> TierRows(string role)
    {
        var banRates = BanRates();
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT champion_id, SUM(games) g, SUM(wins) w FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch=@p1
            GROUP BY champion_id
            HAVING g >= @min
               AND g >= @share * (
                   SELECT SUM(games) FROM base_wr
                   WHERE role=@r AND tier_bucket=@t AND patch=@p1)";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", TIER_MIN_GAMES);
        cmd.Parameters.AddWithValue("@share", TIER_MIN_SHARE);

        var rows = new List<(int Id, double G, double W)>();
        double roleTotal = 0;
        using (var rd = cmd.ExecuteReader())
            while (rd.Read())
            {
                var g = rd.GetDouble(1);
                rows.Add((rd.GetInt32(0), g, rd.GetDouble(2)));
                roleTotal += g;
            }
        if (roleTotal <= 0) return [];

        // Метрики каждого чемпиона роли: сила (LB), присутствие, meta-score.
        return [.. rows.Select(x =>
        {
            double wr   = 100.0 * x.W / x.G;
            double lb   = WilsonLower(x.W, x.G) * 100.0;   // сила (штраф за малую выборку)
            double pick = 100.0 * x.G / roleTotal;          // пик-рейт роли, %
            double ban  = banRates.GetValueOrDefault(x.Id);
            double meta = (lb - 50.0) + W_PICK * pick + W_BAN * Math.Min(ban, BAN_CAP);
            return new TierRow(x.Id, wr, (int)Math.Round(x.G), pick, ban, meta);
        })];
    }

    private readonly Dictionary<string, Dictionary<int, double>> _tierRank = new();

    /// <summary>
    /// Место чемпиона в тир-листе роли (по meta-score, как в режиме «тир»): доля
    /// чемпионов роли выше него, 0 — первый. Нет в тир-листе (меньше 1% игр
    /// роли или мало игр в патче) — 1, как последний. Кэш на жизнь движка.
    /// Советы банов поднимают по нему тех, кто стоит в тир-листе высоко.
    /// </summary>
    private double TierRank(int champId, string role)
    {
        if (!_tierRank.TryGetValue(role, out var ranks))
        {
            ranks = [];
            try
            {
                var rows = TierRows(role).OrderByDescending(r => r.Meta).ToList();
                for (int i = 0; i < rows.Count; i++) ranks[rows[i].Id] = (double)i / rows.Count;
            }
            catch { /* нет данных — все без места */ }
            _tierRank[role] = ranks;
        }
        return ranks.GetValueOrDefault(champId, 1.0);
    }

    /// Грейд чемпиона в тир-листе роли (S…D) — тот же, что видит игрок.
    private char TierGrade(int champId, string role) => GradeOfRank(TierRank(champId, role));

    private Dictionary<int, double>? _banRates;

    /// <summary>
    /// Бан-рейт в текущем патче: в скольких процентах матчей бакета чемпиона
    /// банят. Знаменатель — число матчей, сумма участников base_wr / 10.
    ///
    /// Кэш на жизнь движка: движок пересобирается при смене базы и бакета.
    /// </summary>
    public IReadOnlyDictionary<int, double> BanRates()
    {
        if (_banRates is { } cached) return cached;
        var matches = ScalarD(@"
            SELECT COALESCE(SUM(games),0)/10.0 FROM base_wr
            WHERE tier_bucket=@t AND patch=@p1");
        var bans = BansByChampion();
        return _banRates = matches > 0
            ? bans.ToDictionary(kv => kv.Key, kv => 100.0 * kv.Value / matches)
            : new Dictionary<int, double>();
    }

    /// Бан-рейт одного чемпиона в текущем патче, %. Нет данных — 0.
    public double BanRate(int champId) => BanRates().GetValueOrDefault(champId);

    /// <summary>
    /// Винрейт чемпиона на роли в текущем патче и его сдвиг к прошлому — для
    /// фазы планирования: видно, что мейна понерфили, ещё до того, как его
    /// показал. Порог выборки тот же, что у тир-листа (TIER_MIN_GAMES): меньше —
    /// null, сдвига нет, если прошлого патча нет или он не набран.
    /// </summary>
    public (double Wr, double? Delta)? PatchWr(int champId, string role)
    {
        var key = (champId, role);
        if (_patchWr.TryGetValue(key, out var cached)) return cached;
        (double G, double W) now = (0, 0), prev = (0, 0);
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT patch, SUM(games), SUM(wins) FROM base_wr
                WHERE champion_id=@c AND role=@r AND tier_bucket=@t AND patch IN (@p1, @p2)
                GROUP BY patch";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var row = (rd.GetDouble(1), rd.GetDouble(2));
                if (rd.GetString(0) == _p1) now = row; else prev = row;
            }
        }
        catch { /* нет данных — без числа */ }

        (double, double?)? res = null;
        if (now.G >= TIER_MIN_GAMES)
        {
            var wr = 100.0 * now.W / now.G;
            double? delta = _p2 != _p1 && prev.G >= TIER_MIN_GAMES ? wr - 100.0 * prev.W / prev.G : null;
            res = (wr, delta);
        }
        return _patchWr[key] = res;
    }

    private readonly Dictionary<(int, string), (double Wr, double? Delta)?> _patchWr = new();

    // Чемпион попадает в роль тир-листа банов, если на ней четверть его игр и больше.
    private const double BAN_ROLE_SHARE = 0.25;

    /// <summary>
    /// Тир-лист банов: на каждой роли — кого банят чаще всего в текущем патче.
    ///
    /// Банят чемпиона, а не чемпиона на роли, поэтому роль ему даём по играм:
    /// он стоит там, где играется хотя бы в четверти своих игр, и с ПОЛНЫМ
    /// бан-рейтом — тем, что видит игрок. Делить баны по долям ролей значило бы
    /// показывать «Ясуо 6%» на топе при 25% на деле. Без порога вышло бы как в
    /// мета-тир-листе, где Ясуо с 1% игр на боте стоит в топе стрелков.
    /// </summary>
    public IReadOnlyList<TierEntry> BanTierList(int perRole = 15)
    {
        var rates = BanRates();
        var rows  = new List<(int Id, string Role, double G, double W)>();
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT champion_id, role, SUM(games), SUM(wins) FROM base_wr
                WHERE tier_bucket=@t AND patch=@p1
                GROUP BY champion_id, role";
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                rows.Add((rd.GetInt32(0), rd.GetString(1), rd.GetDouble(2), rd.GetDouble(3)));
        }
        catch { return []; }

        var champTotal = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Sum(r => r.G));
        var roleTotal  = rows.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.Sum(r => r.G));

        var result = new List<TierEntry>();
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            var list = rows
                .Where(r => r.Role == role && r.G >= TIER_MIN_GAMES
                            && r.G >= BAN_ROLE_SHARE * champTotal[r.Id]
                            && rates.GetValueOrDefault(r.Id) > 0)
                .OrderByDescending(r => rates.GetValueOrDefault(r.Id))
                .ToList();
            for (int i = 0; i < list.Count && i < perRole; i++)
            {
                var r = list[i];
                result.Add(new TierEntry(r.Id, role, 100.0 * r.W / r.G, (int)Math.Round(r.G),
                                         100.0 * r.G / roleTotal[role], rates.GetValueOrDefault(r.Id),
                                         GradeOfRank((double)i / list.Count)));
            }
        }
        return result;
    }

    // Взвешенные баны по чемпиону (для presence). Таблицы может не быть в старой
    // базе — тогда пусто, бан-рейт = 0 (тир-лист опирается на пик-рейт).
    private Dictionary<int, double> BansByChampion()
    {
        var map = new Dictionary<int, double>();
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(bans) FROM champion_bans
                WHERE tier_bucket=@t AND patch=@p1 GROUP BY champion_id";
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) map[rd.GetInt32(0)] = rd.GetDouble(1);
        }
        catch { /* нет таблицы — presence только по пик-рейту */ }
        return map;
    }

    private double ScalarD(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0.0 : Convert.ToDouble(v);
    }

    /// <summary>Ключ локализации имени бакета (silver/gold/emerald/master).</summary>
    public string TierBucketLocKey => $"bucket.{TierBucket}";
}
