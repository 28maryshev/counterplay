using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — тир-лист патча, бан-рейты, тир-лист банов, винрейт в патче.
public sealed partial class RecommendationEngine
{
    /// <summary>Строка тир-листа: чемпион, роль, винрейт, игры, пик/бан-рейт, грейд (OP, S…C).</summary>
    public readonly record struct TierEntry(
        int ChampionId, string Role, double Winrate, int Games,
        double PickRate, double BanRate, string Grade);

    // Тир-лист показываем только на достаточной выборке (иначе метрики шумят).
    private const int TIER_MIN_GAMES = 250;

    // Мин. доля игр роли — 0,5%. Ниже — случайные пики, их сила — шум. С 1% до
    // 20.10 из тир-листа выпадали нишевые, но живые пики (Хеймердингер и Клед на
    // топе, маги на боте), а усадка (TIER_PRIOR) и так не пускает малые выборки
    // наверх.
    private const double TIER_MIN_SHARE = 0.005;

    // Тир-лист — по СИЛЕ: винрейту, усаженному к 50% на TIER_PRIOR воображаемых
    // игр, (победы + 1000) / (игры + 2000). До 20.10 оценка была LB + 1,2·пик +
    // 0,4·бан: присутствие давало до 20 очков против ±3 у силы, наверху стояли
    // самые популярные (Кейтлин 49% — первая на боте), а бан-рейт чемпиона
    // засчитывался каждой его роли целиком (Ясуо с 1% игр на боте — A). С
    // эталонным тир-листом владельца порядок сходился на 0,23–0,39 (Спирмен),
    // по силе — на 0,79–0,94; пик и бан сверху совпадение не улучшали. 2000 —
    // лучшее совпадение грейдов и честная статистика: при 2000 играх ошибка
    // винрейта ±1,1%, это и есть разброс сил в роли. Журнал 2026-10-20.
    private const double TIER_PRIOR = 2000;

    // Грейд — по самой силе, а не по месту: средний винрейт роли — 50% по
    // построению (в каждой игре один победитель на роли), так что шкала общая
    // для всех ролей. Раньше грейд шёл пирамидой по месту (S — верх 5% роли, D —
    // нижние 45%), и Орн с 51,9% был C. Пороги подобраны по эталону: 73% грейдов
    // совпадают, 96% — с точностью до ступени (пирамида давала 12% и 32%).
    private const double GRADE_S = 50.3;
    private const double GRADE_A = 49.5;
    private const double GRADE_B = 47.8;

    // OP — сильный (от 51,5%) и при этом массовый: перевес силы над 50% × пикрейт
    // / доля игр без бана (PBI, «влияние на пики и баны») от 5 — его лишние
    // победы заметны на всю роль. Порог, а не «три лучших»: OP бывает один, два,
    // ни одного (владелец 20.10: «у других чаще всего 1»; у тиров со статистикой
    // верх — порог на общей оценке силы и популярности). На 16.18–16.20 по ролям
    // чаще 1, иногда 0 или 2; на 16.20 все семь OP — OP и у эталона (Треш 13,8,
    // Релл с той же силой — 2,7: S). Потолок OP_MAX — на случай перекошенного патча.
    private const int    OP_MAX           = 3;
    private const double OP_MIN_POWER     = 51.5;   // %
    private const double OP_MIN_INFLUENCE = 5.0;    // (сила − 50) × пикрейт / (1 − бан), %·%

    private static string GradeOf(double power) =>
        power >= GRADE_S ? "S" : power >= GRADE_A ? "A" : power >= GRADE_B ? "B" : "C";

    /// Влияние чемпиона на роль: перевес силы над 50% × пикрейт, с поправкой на
    /// баны (кого банят, того меньше берут не из-за слабости). Бан — по
    /// чемпиону, с потолком 90%, чтобы не делить на ноль.
    private static double Influence(TierRow r) =>
        (r.Power - 50.0) * r.Pick / (1.0 - Math.Min(r.Ban, 90.0) / 100.0);

    /// Грейды всех чемпионов роли: OP — сильные и массовые (не больше OP_MAX, по
    /// влиянию), остальные по порогам силы.
    private static Dictionary<int, string> Grades(IEnumerable<TierRow> rows)
    {
        var list = rows.ToList();
        var op = list.Where(r => r.Power >= OP_MIN_POWER && Influence(r) >= OP_MIN_INFLUENCE)
                     .OrderByDescending(Influence).Take(OP_MAX)
                     .Select(r => r.Id).ToHashSet();
        return list.ToDictionary(r => r.Id, r => op.Contains(r.Id) ? "OP" : GradeOf(r.Power));
    }

    /// Порядок тира: OP — первыми, дальше по силе. Иначе нишевой S с силой выше
    /// стоял бы над OP (Тарик над Браумом), а OP — полоса над S, как на сайте.
    private static List<TierRow> TierOrder(IEnumerable<TierRow> rows, Dictionary<int, string> grades) =>
        [.. rows.OrderBy(r => grades[r.Id] == "OP" ? 0 : 1).ThenByDescending(r => r.Power)];

    /// <summary>
    /// Тир-лист патча для текущего бакета: топ-N чемпионов на каждой роли,
    /// грейд OP, S…C по силе. Два режима ранжирования:
    ///   byWinrate=false — по силе (винрейт, усаженный к 50%) — «тир»;
    ///   byWinrate=true  — по сырому винрейту.
    /// Грейд у чемпиона один в обоих режимах. Роли — top→jgl→mid→adc→sup.
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
            // OP первыми, дальше по силе.
            var rows   = TierRows(role);
            var grades = Grades(rows);
            var order  = byWinrate ? rows.OrderByDescending(s => s.Wr).ToList() : TierOrder(rows, grades);
            foreach (var s in order.Take(perRole))
                result.Add(new TierEntry(s.Id, role, s.Wr, s.Games, s.Pick, s.Ban, grades[s.Id]));
        }
        return result;
    }

    // Строка тир-листа роли до сортировки: винрейт, игры, пик/бан-рейт, сила.
    private readonly record struct TierRow(int Id, double Wr, int Games, double Pick, double Ban, double Power);

    /// Все чемпионы роли, прошедшие пороги тир-листа, с их силой — в текущем
    /// патче и бакете. Порядок не задан: сортирует тот, кто зовёт.
    private List<TierRow> TierRows(string role)
    {
        var banRates = BanRates();
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT champion_id, SUM(games), SUM(wins) FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch=@p1
            GROUP BY champion_id";
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);

        var rows = new List<(int Id, double G, double W)>();
        using (var rd = cmd.ExecuteReader())
            while (rd.Read()) rows.Add((rd.GetInt32(0), rd.GetDouble(1), rd.GetDouble(2)));

        // Пикрейт — от ВСЕХ игр роли, вместе с теми, кто не прошёл порог: это
        // доля, которую видит игрок, и по ней же отбирается OP.
        double roleTotal = rows.Sum(x => x.G);
        if (roleTotal <= 0) return [];

        return [.. rows
            .Where(x => x.G >= TIER_MIN_GAMES && x.G >= TIER_MIN_SHARE * roleTotal)
            .Select(x => new TierRow(
                x.Id,
                100.0 * x.W / x.G,
                (int)Math.Round(x.G),
                100.0 * x.G / roleTotal,
                banRates.GetValueOrDefault(x.Id),
                100.0 * (x.W + TIER_PRIOR / 2) / (x.G + TIER_PRIOR)))];
    }

    // Место и грейд чемпионов роли — для советов банов. Кэш на жизнь движка.
    private readonly Dictionary<string, (Dictionary<int, double> Rank, Dictionary<int, string> Grade)> _tierOf = new();

    private (Dictionary<int, double> Rank, Dictionary<int, string> Grade) TierOf(string role)
    {
        if (_tierOf.TryGetValue(role, out var cached)) return cached;
        Dictionary<int, double> ranks  = [];
        Dictionary<int, string> grades = [];
        try
        {
            var all = TierRows(role);
            grades = Grades(all);
            var rows = TierOrder(all, grades);
            for (int i = 0; i < rows.Count; i++) ranks[rows[i].Id] = (double)i / rows.Count;
        }
        catch { /* нет данных — все без места */ }
        return _tierOf[role] = (ranks, grades);
    }

    /// <summary>
    /// Место чемпиона в тир-листе роли (как в режиме «тир»: OP первыми, дальше
    /// по силе): доля
    /// чемпионов роли выше него, 0 — первый. Нет в тир-листе (меньше 0,5% игр
    /// роли или мало игр в патче) — 1, как последний.
    /// Советы банов поднимают по нему тех, кто стоит в тир-листе высоко.
    /// </summary>
    private double TierRank(int champId, string role) => TierOf(role).Rank.GetValueOrDefault(champId, 1.0);

    /// Грейд чемпиона в тир-листе роли (OP, S…C) — тот же, что видит игрок; нет
    /// в тир-листе — пустая строка.
    private string TierGrade(int champId, string role) => TierOf(role).Grade.GetValueOrDefault(champId, "");

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
            // Грейда у тир-листа банов нет: под иконкой бан-рейт, порядок и так по нему.
            foreach (var r in list.Take(perRole))
                result.Add(new TierEntry(r.Id, role, 100.0 * r.W / r.G, (int)Math.Round(r.G),
                                         100.0 * r.G / roleTotal[role], rates.GetValueOrDefault(r.Id), ""));
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
