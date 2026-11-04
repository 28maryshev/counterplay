using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Counterplay.Tests;

/// <summary>
/// Вес контры по ролям — по исходам матчей, а не на глаз.
///
/// Замер (pipeline/calibrate.py, 04.11.2026): сколько настоящих пунктов победы
/// несёт один показанный пункт контры — топ 0,71, мид 0,70, АДК 0,53, саппорт
/// 0,49, лес 0,40, бот 2 на 2 — 0,52. Вес = W_COUNTER_PP × доля, вслепую —
/// W_COUNTER_PP (ожидаемое уже очищено от шума).
///
/// Что держит:
///   * три копии формулы сходятся — движок, бот Discord (bot/lib/scoring.js) и
///     калибровка (её сглаживание повторяет движок);
///   * раскладка держит замер: лес меньше всех, средний вес прежний (2,1), нет
///     надбавки «виден весь их бот»;
///   * подбор на живой базе (только чтение) кладёт каждое слагаемое с тем
///     весом, что в таблице: известный матчап — с весом роли, бот 2 на 2 — с
///     W_BOTLANE и при известной паре, ожидаемое вслепую — с W_BLIND.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Yasuo = 157, LeeSin = 64, Jinx = 222, Thresh = 412;

    private static readonly string[] Roles = ["top", "jungle", "mid", "adc", "support"];

    private static readonly Type E = typeof(RecommendationEngine);

    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам:
        // пулов и журнала игр нет, и личные слагаемые молчат.
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "CounterWeights");
        if (Directory.Exists(AppPaths.RootOverride)) Directory.Delete(AppPaths.RootOverride, recursive: true);
        Directory.CreateDirectory(AppPaths.RootOverride);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = real;   // база матчей — настоящая, только читаем
        PoolStore.DirOverride = Path.Combine(AppPaths.RootOverride, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();

        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Sources();
        Live();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: вес контры по ролям — по замеру, во всех копиях и в подборе"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── Исходники: движок, бот, калибровка ──────────────────────────────────

    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Counterplay.csproj"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("не нашёл корень репозитория");
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static double C(string name) => Convert.ToDouble(E.GetField(name, Any)!.GetValue(null));

    private static double DirectWeight(string role) => (double)E.GetMethod("DirectWeight", Any)!.Invoke(null, [role])!;

    private static void Sources()
    {
        Console.WriteLine("── три копии формулы ──");
        var repo = Repo();
        // Вся папка класса, а не один файл: константа может переехать в другую часть.
        var engine = string.Concat(Directory.GetFiles(Path.Combine(repo, "src", "Engine", "Recommendation"), "*.cs")
                                            .Select(File.ReadAllText));
        var bot = File.ReadAllText(Path.Combine(repo, "bot", "lib", "scoring.js"));
        var calib = File.ReadAllText(Path.Combine(repo, "pipeline", "calibrate.py"));

        var g = C("W_COUNTER_PP");
        var trust = Roles.ToDictionary(r => r, r => DirectWeight(r) / g);
        var botTrust = C("W_BOTLANE") / g;

        // Бот
        var bg = Regex.Match(bot, @"const W_COUNTER_PP = ([\d.]+);");
        var bt = Regex.Match(bot, @"const LANE_TRUST = \{([^}]*)\}");
        var bb = Regex.Match(bot, @"const W_BOTLANE = W_COUNTER_PP \* ([\d.]+);");
        Check("бот: W_COUNTER_PP как в программе", bg.Success && Math.Abs(Num(bg.Groups[1].Value) - g) < 1e-9,
              bg.Success ? $"{bg.Groups[1].Value} и {g}" : "строки нет");
        var botLane = bt.Success
            ? Regex.Matches(bt.Groups[1].Value, @"(\w+):\s*([\d.]+)").ToDictionary(m => m.Groups[1].Value, m => Num(m.Groups[2].Value))
            : [];
        var diff = Roles.Where(r => !botLane.TryGetValue(r, out var v) || Math.Abs(v - trust[r]) > 1e-9).ToList();
        Check("бот: доли правды по ролям как в программе", bt.Success && diff.Count == 0,
              diff.Count == 0 ? string.Join(", ", Roles.Select(r => $"{r} {trust[r]:F2}")) : "расходятся: " + string.Join(", ", diff));
        Check("бот: доля бота 2 на 2 как в программе", bb.Success && Math.Abs(Num(bb.Groups[1].Value) - botTrust) < 1e-9,
              bb.Success ? $"{bb.Groups[1].Value} и {botTrust:F2}" : "строки нет");
        Check("нет надбавки «виден весь их бот» ни в программе, ни в боте",
              !engine.Contains("W_BOTLANE_BOTH") && !bot.Contains("W_BOTLANE_BOTH"), "");

        // Калибровка: её сглаживание обязано повторять движок, иначе она мерит
        // долю правды не той цифры, что показывает подбор.
        var cm = Regex.Match(calib, @"K, K_PAIR, MATCHUP_CONF = ([\d.]+), ([\d.]+), ([\d.]+)");
        var ct = Regex.Match(calib, @"TIER_SHRINK = ([\d.]+)");
        var cp = Regex.Match(calib, @"PREV_WEIGHT = ([\d.]+)");
        var same = cm.Success && ct.Success && cp.Success
                   && Num(cm.Groups[1].Value) == C("K") && Num(cm.Groups[2].Value) == C("K_PAIR")
                   && Num(cm.Groups[3].Value) == C("MATCHUP_CONF") && Num(ct.Groups[1].Value) == C("TIER_PRIOR")
                   && ((string)E.GetField("PW", Any)!.GetValue(null)!).Contains($"THEN {cp.Groups[1].Value} ");
        Check("калибровка сглаживает как движок (K, K_PAIR, MATCHUP_CONF, усадка силы, вес прошлого патча)", same,
              same ? "" : "сверь константы pipeline/calibrate.py с RecommendationEngine");

        // Раскладка
        Console.WriteLine();
        Console.WriteLine("── раскладка по ролям ──");
        var weights = Roles.ToDictionary(r => r, DirectWeight);
        Console.WriteLine("  веса: " + string.Join(", ", Roles.Select(r => $"{r} {weights[r]:F2}")) + $"; бот 2 на 2 {C("W_BOTLANE"):F2}; вслепую {C("W_BLIND"):F2}");
        Check("лес — меньше всех (его исход зависит от всех линий понемногу)",
              Roles.Where(r => r != "jungle").All(r => trust[r] > trust["jungle"] + 0.05), $"лес {trust["jungle"]:F2}");
        Check("топ и мид — больше всех", trust["top"] >= Roles.Except(["top", "mid"]).Max(r => trust[r])
                                         && trust["mid"] >= Roles.Except(["top", "mid"]).Max(r => trust[r]),
              $"топ {trust["top"]:F2}, мид {trust["mid"]:F2}");
        var mean = weights.Values.Average();
        Check("средний вес прежний (2,1): калибровка меняет раскладку, а не общий вес контры",
              Math.Abs(mean - 2.1) < 0.05, $"{mean:F3}");
        Check("вслепую — W_COUNTER_PP (ожидаемое уже в настоящих пунктах)", C("W_BLIND") == g, $"{C("W_BLIND")}");
        Check("доли правды — доли (0…1)", trust.Values.Append(botTrust).All(t => t is > 0 and < 1), "");
    }

    // ── Подбор на живой базе ────────────────────────────────────────────────

    private static void Live()
    {
        Console.WriteLine();
        Console.WriteLine("── подбор на живой базе: каждое слагаемое со своим весом ──");
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  базы нет — подбор не проверяем"); return; }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        // Первый пик саппорта: всё вслепую, и линия, и их АДК.
        var first = Draft("utility", []);
        Expect(engine, "саппорт, первый пик", first, "support", [],
               r => C("W_BLIND") * r.DirectDelta, expectBlind: true);

        // Мид против известного Ясуо — вес роли.
        Expect(engine, "мид против Ясуо", Draft("middle", [new(5, Yasuo, 0, "middle", false)]), "mid", [Yasuo],
               r => DirectWeight("mid") * r.DirectDelta, expectBlind: false);

        // Лес против известного Ли Сина — вес леса, меньший из всех.
        Expect(engine, "лес против Ли Сина", Draft("jungle", [new(5, LeeSin, 0, "jungle", false)]), "jungle", [LeeSin],
               r => DirectWeight("jungle") * r.DirectDelta, expectBlind: false);

        // АДК против всего их бота: линия с весом АДК, их саппорт — W_BOTLANE,
        // и без надбавки за то, что видна вся пара.
        Expect(engine, "АДК против Джинкс и Треша", Draft("bottom",
                   [new(5, Jinx, 0, "bottom", false), new(6, Thresh, 0, "utility", false)]), "adc", [Jinx, Thresh],
               r =>
               {
                   var bot = engine.VersusDelta(r.ChampionId, "adc", Thresh, "support");
                   return DirectWeight("adc") * (r.DirectDelta - bot) + C("W_BOTLANE") * bot;
               }, expectBlind: false);
    }

    private static DraftState Draft(string pos, List<DraftPlayer> enemies)
    {
        var me = new DraftPlayer(0, 0, 0, pos, true);
        return new DraftState(
            MyTeam: [me], TheirTeam: enemies, MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: pos, DirectOpponent: enemies.FirstOrDefault(p => p.Position == pos),
            ExposedToCounter: true, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    /// Оценка каждого кандидата = сила + ожидаемое слагаемое контры + то, что
    /// от контры не зависит (предметы, связки, стиль — считаются теми же
    /// функциями движка). Союзников, пулов и журнала нет — личные слагаемые и
    /// синергия молчат; врагов кроме названных нет — «прочие» тоже.
    private static void Expect(RecommendationEngine engine, string name, DraftState state, string role, List<int> enemies,
                               Func<Recommendation, double> counter, bool expectBlind)
    {
        var recs = engine.Recommend(state, 400);
        var bad = new List<string>();
        // Стиль врага — как в подборе: от двух известных врагов.
        ChampionTraits.Arch? dom = enemies.Count >= 2 ? ChampionTraits.Dominant(enemies) : null;
        foreach (var r in recs)
        {
            var id = r.ChampionId;
            var fit = E.GetMethod("DraftFit", Any)!.Invoke(null, [id, dom])!;
            var st  = E.GetMethod("StructuralBonus", Any)!.Invoke(null, [id, role, 0, 0, new List<int>()])!;
            var rest = (double)fit.GetType().GetField("Item1")!.GetValue(fit)!
                     - C("W_VULN") * ItemValue.VulnPenalty(id, []).Penalty
                     + C("W_EXPLOIT") * ItemValue.ExploitBonus(id, enemies).Bonus
                     + C("W_STRUCT") * (double)st.GetType().GetField("Item1")!.GetValue(st)!
                     + C("W_DMGBAL") * ItemValue.DamageBalance(id, [], enemies).Delta;
            var want = C("W_BASE") * r.BaseDelta + counter(r) + rest;
            if (Math.Abs(want - r.Score) > 1e-6 || r.Blind != expectBlind)
                bad.Add($"{id}: {r.Score:F3} ≠ {want:F3}{(r.Blind != expectBlind ? " (вслепую?)" : "")}");
        }
        Check($"{name}: оценка = сила + контра с весом из таблицы ({recs.Count} кандидатов)",
              recs.Count > 10 && bad.Count == 0, bad.Count == 0 ? "" : string.Join("; ", bad.Take(3)));
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
