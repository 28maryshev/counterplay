using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Шум пар вслепую — по настоящему числу игр.
///
/// Истинный разброс пары движок получает как наблюдаемый² − шум², поэтому шум
/// должен быть ровно тем, что сидит в данных. До 27.11.2026 он считался по
/// `p.g` — взвешенной сумме игр (свежий патч ×1, прошлый ×0,6) плюс 25
/// псевдо-игр сводки по дивизионам, — как будто это настоящие игры. Взвешенная
/// доля шумит меньше, а псевдо-игры шума не несут вовсе. Шум выходил завышен,
/// разброс проседал, пары усаживались к центру, и доля контр падала. Хуже
/// всего — в первые дни, когда свежий патч в окне мал против прошлого: на
/// живой базе 26.11 (16.23 + 16.22) типичный шанс контры у саппорта упал с
/// 28% до 8%, у леса — до 3,5%, и «надёжен вслепую» у этих ролей пропал совсем.
/// Матчапы между патчами при этом не изменились.
///
/// Сводка по дивизионам собрана по всем бакетам тех же патчей, то есть свои
/// игры пары входят в неё целиком, и через её псевдо-игры свой шум
/// возвращается. Поэтому «псевдо-игры шума не несут» — тоже неправда: верна
/// только полная дисперсия (Σw²n + 2·25·G/P + 25²/P) / (G + 25)², где G —
/// взвешенные игры, P — игры сводки.
///
/// Проверка — на двух синтетических базах с одними и теми же настоящими
/// матчапами (разброс 1,5 пп):
///   • эталон — огромные выборки, шума нет: что движок должен видеть;
///   • проверяемая — как в жизни. Саппорт: свежий патч в 5 раз меньше
///     прошлого, пары по ~1100 и ~220 игр (шум порядка разброса) — ловит веса
///     патчей. Лес: только свежий патч, пары по ~100 игр, сводка вшестеро
///     больше своих игр (как в живой базе) — ловит счёт сводки: и «25
///     настоящих игр» (шум завышен), и «бесшумные псевдо-игры» (занижен).
/// Проверяемых выборок восемь, сравнивается среднее: одна выборка на 780 пар
/// шумит на ±5% по разбросу, а шанс контры от разброса зависит круто (−10%
/// разброса — около −10 пп шанса). Сиды зафиксированы.
///
/// Вторая часть — живая база (только чтение): разброс пар, который видит
/// движок, сверяется по каждой роли с разбросом без шума — ковариацией пар
/// между двумя патчами окна (LiveChecks).
/// </summary>
internal static class Program
{
    private static int _fails;

    private const double Tau = 1.5;                 // истинный разброс пар, пп
    private const string Bucket = "emerald";
    private const string Old = "26.1", Fresh = "26.2";

    // Роль: сколько чемпионов, сглаживание долей (1/(i + c)), игр роли в
    // прошлом и свежем патче.
    private sealed record Role(string Name, int Count, int Smooth, double OldGames, double FreshGames, int FirstId);

    private static readonly Role Support = new("support", 40, 5, 1_800_000, 360_000, 1);
    private static readonly Role Jungle  = new("jungle",  80, 30, 0, 640_000, 101);

    private const int Samples = 8;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "BlindNoise");
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;

        // Настоящие матчапы — одни на всё; эталон — они же без шума.
        var truth = new[] { Support, Jungle }.ToDictionary(r => r.Name, r => Truth(r, new Random(20261127)));
        var refDb = Path.Combine(root, "reference.db");
        Build(refDb, truth, new Random(1), exact: true);
        Measure(refDb, out var refSpread, out var refTypical, out var refPatches);
        SqliteConnection.ClearAllPools();
        try { File.Delete(refDb); } catch { /* не страшно */ }

        var spread  = new Dictionary<string, List<double>> { ["support"] = [], ["jungle"] = [] };
        var typical = new Dictionary<string, List<double>> { ["support"] = [], ["jungle"] = [] };
        var livePatches = "";
        for (int k = 0; k < Samples; k++)
        {
            var db = Path.Combine(root, $"noisy{k}.db");
            Build(db, truth, new Random(1000 + k), exact: false);
            Measure(db, out var sp, out var ty, out livePatches);
            foreach (var r in new[] { "support", "jungle" }) { spread[r].Add(sp[r] / refSpread[r]); typical[r].Add(ty[r] - refTypical[r]); }
            SqliteConnection.ClearAllPools();
            try { File.Delete(db); } catch { /* не страшно */ }
        }
        Console.WriteLine($"  эталон: патчи {refPatches}; проверяемые ({Samples} выборок): патчи {livePatches}");
        foreach (var r in new[] { Support, Jungle })
            Console.WriteLine($"  {r.Name,-8} эталон: разброс {refSpread[r.Name]:F2} пп, шанс контры {100 * refTypical[r.Name]:F1}%; " +
                              $"выборки: разброс ×{string.Join(" ", spread[r.Name].Select(x => x.ToString("F2")))}; " +
                              $"шанс {string.Join(" ", typical[r.Name].Select(x => (100 * x).ToString("+0;-0;0")))} пп");
        foreach (var r in new[] { "support", "jungle" })
            Console.WriteLine($"  {r}: среднее ×{spread[r].Average():F3} ± {Se(spread[r]):F3}, шанс {100 * typical[r].Average():+0.0;-0.0} ± {100 * Se(typical[r]):F1} пп");

        // Допуски — по среднему из выборок. Верная формула (27.11.2026): саппорт
        // ×0,97 ± 0,02 и −5 ± 1,5 пп, лес ×0,99 ± 0,03 и −3 ± 3 пп. Поломки:
        // шум по взвешенной сумме — ×0,75 / ×0,62 и −23 / −37 пп; без весов
        // патчей — саппорт ×0,77 и −21 пп; сводка без своих игр в ней (нет
        // члена 2c·G/P) — лес ×1,22 и +14 пп.
        Check("веса патчей: разброс пар на окне «свежий + прошлый» — как без шума",
              Near(spread["support"].Average(), 1, 0.10),
              $"×{spread["support"].Average():F2} от эталона (допуск ±10%); ниже — шум пары посчитан по " +
              "взвешенной сумме игр (прошлый патч ×0,6), а не по Σw²·игры");
        Check("веса патчей: типичный шанс контры — как без шума",
              Near(typical["support"].Average(), 0, 0.10),
              $"{100 * typical["support"].Average():+0.0;-0.0} пп от эталона {100 * refTypical["support"]:F1}% (допуск ±10 пп)");
        Check("сводка по дивизионам: малые пары не прячут и не раздувают разброс",
              Near(spread["jungle"].Average(), 1, 0.10),
              $"×{spread["jungle"].Average():F2} от эталона (допуск ±10%); ниже — 25 псевдо-игр сводки " +
              "посчитаны в шум как настоящие игры, выше — как бесшумные (а свои игры пары в сводке есть)");
        Check("сводка по дивизионам: типичный шанс контры — как без шума",
              Near(typical["jungle"].Average(), 0, 0.10),
              $"{100 * typical["jungle"].Average():+0.0;-0.0} пп от эталона {100 * refTypical["jungle"]:F1}% (допуск ±10 пп)");

        LiveChecks();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: шум пар вслепую — по настоящему числу игр"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static bool Near(double x, double target, double tol) => Math.Abs(x - target) <= tol;

    private static double Se(List<double> xs)
    {
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1) / xs.Count);
    }

    // Доли и настоящие матчапы роли: t(A,B) ~ N(0, Tau), t(B,A) = −t(A,B).
    private sealed record RoleTruth(Role Role, double[] Share, double[,] Effect);

    private static RoleTruth Truth(Role r, Random rng)
    {
        var share = Enumerable.Range(1, r.Count).Select(i => 1.0 / (i + r.Smooth)).ToArray();
        var sum = share.Sum();
        for (int i = 0; i < share.Length; i++) share[i] /= sum;
        var t = new double[r.Count, r.Count];
        for (int a = 0; a < r.Count; a++)
            for (int b = a + 1; b < r.Count; b++)
            {
                t[a, b] = Tau * Normal(rng);
                t[b, a] = -t[a, b];
            }
        return new RoleTruth(r, share, t);
    }

    private static double Normal(Random rng) =>
        Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    private static int Binomial(Random rng, int n, double p)
    {
        var k = 0;
        for (int i = 0; i < n; i++) if (rng.NextDouble() < p) k++;
        return k;
    }

    /// База: base_wr и matchup своего бакета по патчам и сводка по дивизионам
    /// ('all'/'all') — свои игры плюс впятеро больше «других дивизионов»: в живой
    /// базе сводка примерно вшестеро больше окна своего бакета. exact —
    /// эталон: игр в тысячу раз больше, победы ровно по вероятности.
    private static void Build(string path, Dictionary<string, RoleTruth> truth, Random rng, bool exact)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var tx = con.BeginTransaction();
        var ddl = con.CreateCommand();
        ddl.CommandText = @"
            CREATE TABLE base_wr (champion_id INTEGER, role TEXT, tier_bucket TEXT, patch TEXT, games INTEGER, wins INTEGER);
            CREATE TABLE matchup (champion_id INTEGER, role TEXT, vs_champion_id INTEGER, tier_bucket TEXT, patch TEXT,
                                  games INTEGER, wins INTEGER);";
        ddl.ExecuteNonQuery();

        var mu = con.CreateCommand();
        mu.CommandText = "INSERT INTO matchup VALUES (@a, @r, @b, @t, @p, @g, @w)";
        foreach (var n in new[] { "@a", "@r", "@b", "@t", "@p", "@g", "@w" }) mu.Parameters.Add(new SqliteParameter(n, null));
        void Pair(int a, string role, int b, string tier, string patch, long g, long w)
        {
            mu.Parameters["@a"].Value = a; mu.Parameters["@r"].Value = role; mu.Parameters["@b"].Value = b;
            mu.Parameters["@t"].Value = tier; mu.Parameters["@p"].Value = patch;
            mu.Parameters["@g"].Value = g; mu.Parameters["@w"].Value = w;
            mu.ExecuteNonQuery();
        }
        var bw = con.CreateCommand();
        bw.CommandText = "INSERT INTO base_wr VALUES (@a, @r, @t, @p, @g, @w)";
        foreach (var n in new[] { "@a", "@r", "@t", "@p", "@g", "@w" }) bw.Parameters.Add(new SqliteParameter(n, null));

        foreach (var (_, rt) in truth)
        {
            var r = rt.Role;
            // Эталон — один патч с огромными выборками; проверяемая — как в жизни.
            var patches = exact ? [(Fresh, 1e9)]
                                : new[] { (Old, r.OldGames), (Fresh, r.FreshGames) }.Where(p => p.Item2 > 0).ToArray();
            var poolG = new long[r.Count, r.Count];
            var poolW = new long[r.Count, r.Count];
            foreach (var (patch, total) in patches)
            {
                var g = new long[r.Count]; var w = new long[r.Count];
                for (int a = 0; a < r.Count; a++)
                    for (int b = a + 1; b < r.Count; b++)
                    {
                        var n = (long)Math.Round(total * rt.Share[a] * rt.Share[b]);
                        if (n <= 0) continue;
                        var p = 0.5 + rt.Effect[a, b] / 100;
                        var k = exact ? (long)Math.Round(n * p) : Binomial(rng, (int)n, p);
                        Pair(r.FirstId + a, r.Name, r.FirstId + b, Bucket, patch, n, k);
                        Pair(r.FirstId + b, r.Name, r.FirstId + a, Bucket, patch, n, n - k);
                        g[a] += n; w[a] += k; g[b] += n; w[b] += n - k;
                        // Сводка: свои игры плюс впятеро больше «других дивизионов».
                        var other = exact ? (long)Math.Round(5 * n * p) : Binomial(rng, (int)(5 * n), p);
                        poolG[a, b] += 6 * n; poolW[a, b] += k + other;
                    }
                for (int a = 0; a < r.Count; a++)
                {
                    bw.Parameters["@a"].Value = r.FirstId + a; bw.Parameters["@r"].Value = r.Name;
                    bw.Parameters["@t"].Value = Bucket; bw.Parameters["@p"].Value = patch;
                    bw.Parameters["@g"].Value = g[a]; bw.Parameters["@w"].Value = w[a];
                    bw.ExecuteNonQuery();
                }
            }
            for (int a = 0; a < r.Count; a++)
                for (int b = a + 1; b < r.Count; b++)
                {
                    if (poolG[a, b] <= 0) continue;
                    Pair(r.FirstId + a, r.Name, r.FirstId + b, "all", "all", poolG[a, b], poolW[a, b]);
                    Pair(r.FirstId + b, r.Name, r.FirstId + a, "all", "all", poolG[a, b], poolG[a, b] - poolW[a, b]);
                }
        }
        tx.Commit();
    }

    // ── Живая база: разброс, который видит движок, — как разброс без шума ────
    //
    // Синтетика ловит известные ошибки формулы, но не всё, что может случиться
    // с живыми данными. Здесь сверка с правдой, которую можно добыть из самой
    // базы. «Выше ожидаемого» одной пары в двух патчах окна шумит независимо,
    // поэтому ковариация по соперникам (центр — по чемпиону, веса — доли поля)
    // показывает разброс без шума. В неё входит и постоянство игроков (одни и
    // те же люди в обоих патчах), так что это скорее верхняя оценка.
    //
    // Замер 27.11.2026 (16.23 + 16.22): движок / ковариация — саппорт 1,12 / 1,12,
    // лес 1,11 / 1,15, мид 1,61 / 1,66, топ 1,77 / 1,81, АДК 0,96 / 1,07, в
    // среднем ×0,96. Со старым шумом (по взвешенной сумме игр) — ×0,63…0,78 по
    // ролям, в среднем ×0,73: проверки по живой базе тогда были зелёными, хотя
    // «надёжен вслепую» у саппорта, леса и АДК не выдавался никому. Погрешность
    // ковариации — около ±6% по роли (больше всех у АДК), ±3% в среднем.
    private static void LiveChecks()
    {
        Console.WriteLine();
        Console.WriteLine("── живая база: разброс пар движка против разброса без шума ──");
        // База матчей — настоящая, только чтение; своя папка программы уже стоит.
        DataDb.DirOverride = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        var path = RecommendationEngine.FindDb();
        if (path is null) { Check("живая база найдена", false, "нет data.db"); return; }
        using var e = RecommendationEngine.Create(path, "emerald");
        var patches = e.PatchDisplay.Split(", ");
        if (patches.Length < 2)
        {
            // Одного патча в окне не бывает, пока в базе есть прошлый (удержание
            // патча); сверять тогда не с чем.
            Console.WriteLine($"  окно из одного патча ({e.PatchDisplay}) — сверять не с чем, пропуск");
            return;
        }
        using var con = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        con.Open();

        var ratios = new List<double>();
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            var field = e.FieldShares(role);
            var w = field.Sum(f => f.Share);
            var engine = Math.Sqrt(field.Sum(f => f.Share * Math.Pow(e.BlindProfileOf(f.Id, role, role).True, 2)) / w);
            var clean  = CrossPatchSpread(con, e.TierBucket, role, patches[0], patches[1], field);
            var ratio  = clean > 0 ? engine / clean : double.NaN;
            ratios.Add(ratio);
            Check($"{role}: разброс движка — от 0,75 до 1,3 разброса без шума",
                  ratio is >= 0.75 and <= 1.30,
                  $"движок {engine:F2} пп, без шума {clean:F2} пп (×{ratio:F2})");
        }
        var mean = ratios.Average();
        Check("в среднем по ролям разброс движка — как разброс без шума (±15%)", Near(mean, 1, 0.15),
              $"×{mean:F2}; ниже — шум пар завышен (со счётом взвешенной суммы как игр было ×0,73), " +
              "выше — занижен");
    }

    /// Разброс пар без шума: ковариация «выше ожидаемого» пары в двух патчах.
    private static double CrossPatchSpread(SqliteConnection con, string bucket, string role, string pA, string pB,
                                           IReadOnlyList<(int Id, double Share)> field)
    {
        Dictionary<int, double> Base(string patch)
        {
            var c = con.CreateCommand();
            c.CommandText = "SELECT champion_id, SUM(games), SUM(wins) FROM base_wr " +
                            "WHERE tier_bucket=@t AND patch=@p AND role=@r GROUP BY champion_id";
            c.Parameters.AddWithValue("@t", bucket); c.Parameters.AddWithValue("@p", patch); c.Parameters.AddWithValue("@r", role);
            var d = new Dictionary<int, double>();
            using var rd = c.ExecuteReader();
            while (rd.Read()) d[rd.GetInt32(0)] = (rd.GetDouble(2) + 25) / (rd.GetDouble(1) + 50);
            return d;
        }
        Dictionary<(int, int), double> Pairs(string patch)
        {
            var c = con.CreateCommand();
            c.CommandText = "SELECT champion_id, vs_champion_id, SUM(games), SUM(wins) FROM matchup " +
                            "WHERE tier_bucket=@t AND patch=@p AND role=@r GROUP BY champion_id, vs_champion_id";
            c.Parameters.AddWithValue("@t", bucket); c.Parameters.AddWithValue("@p", patch); c.Parameters.AddWithValue("@r", role);
            var d = new Dictionary<(int, int), double>();
            using var rd = c.ExecuteReader();
            while (rd.Read()) if (rd.GetDouble(2) > 0) d[(rd.GetInt32(0), rd.GetInt32(1))] = rd.GetDouble(3) / rd.GetDouble(2);
            return d;
        }
        var (bA, bB, pa, pb) = (Base(pA), Base(pB), Pairs(pA), Pairs(pB));
        double acc = 0, total = 0;
        foreach (var (a, sa) in field)
        {
            if (!bA.ContainsKey(a) || !bB.ContainsKey(a)) continue;
            var cells = new List<(double W, double X, double Y)>();
            foreach (var (b, sb) in field)
            {
                if (a == b || !bA.ContainsKey(b) || !bB.ContainsKey(b)) continue;
                if (!pa.TryGetValue((a, b), out var ra) || !pb.TryGetValue((a, b), out var rb)) continue;
                cells.Add((sb, (ra - BlindMath.ExpectedWr(bA[a], bA[b])) * 100, (rb - BlindMath.ExpectedWr(bB[a], bB[b])) * 100));
            }
            var w = cells.Sum(c => c.W);
            if (w <= 0) continue;
            var mx = cells.Sum(c => c.W * c.X) / w;
            var my = cells.Sum(c => c.W * c.Y) / w;
            acc += sa * cells.Sum(c => c.W * (c.X - mx) * (c.Y - my)) / w;
            total += sa;
        }
        var cov = total > 0 ? acc / total : 0;
        return Math.Sign(cov) * Math.Sqrt(Math.Abs(cov));
    }

    /// Что движок видит по роли: истинный разброс пар (среднее по полю с долями)
    /// и типичный шанс нарваться на контру вслепую.
    private static void Measure(string db, out Dictionary<string, double> spread, out Dictionary<string, double> typical,
                                out string patches)
    {
        spread = []; typical = [];
        using var e = RecommendationEngine.Create(db, Bucket);
        patches = e.PatchDisplay;
        foreach (var r in new[] { Support, Jungle })
        {
            var field = e.FieldShares(r.Name);
            var w = field.Sum(f => f.Share);
            spread[r.Name]  = Math.Sqrt(field.Sum(f => f.Share * Math.Pow(e.BlindProfileOf(f.Id, r.Name, r.Name).True, 2)) / w);
            typical[r.Name] = e.Blind(field[0].Id, r.Name, r.Name, new HashSet<int>()).Typical;
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
