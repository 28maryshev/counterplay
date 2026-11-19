using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Личные матчапы (владелец 14.11, по разбору: «мнение игрока о матчапе решает,
/// банить или нет»).
///
/// 1. Журнал пишет врагов по ролям из подробной игры, как союзников, и
///    дочитывает их у старых игр с номером — не больше 25 за обновление.
///    При первом запуске враги есть сразу: дочитывание идёт после того, как
///    журнал наполнен прошлыми играми.
/// 2. Баны: кому я проигрываю сверх ожидаемого, тот поднимается с доводом
///    «Твой счёт против него: 1–9»; кого обыгрываю — опускается, без довода.
/// 3. Карточка пика: «Твои игры на нём против Леоны: 2–1» — от двух игр.
///
/// Журнал — подставной клиент и своя папка. Баны и карточка — на живой базе
/// игрока (только чтение) с подменой журнала; нет базы — эта часть пропускается.
/// </summary>
internal static class Program
{
    private static int _fails;
    private const string Me = "TEST-foes-me-00000000000000000000000000000000000000000000000000000000";
    private const int Sona = 37, Leona = 89, Blitz = 53, Braum = 201;

    [STAThread]
    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PersonalFoes");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        try
        {
            await Journal();
            await Backfill(root);
            Engine();
        }
        finally
        {
            SessionTracker.FoeGamesOverride = null;
            SessionTracker.HistoryOverride = null;
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: личные матчапы пишутся, дочитываются и учитываются"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Журнал: враги из подробной игры ─────────────────────────────────

    /// Игры — новейшие первыми: (враг-саппорт, победа). Номера у клиента
    /// идут от 7 000 000 000 вниз в том же порядке (FakeLcu).
    private static FakeLcu Client(IReadOnlyList<(int Foe, bool Win)> games)
    {
        var now = DateTimeOffset.UtcNow;
        var lcu = new FakeLcu(Me, "я", games.Select((g, i) => (420, Sona, g.Win, now.AddHours(-2 * (i + 1)))));
        for (int i = 0; i < games.Count; i++)
            lcu.Details[7_000_000_000L - i] = FakeLcu.LaneGame(Me, Sona, games[i].Foe, games[i].Win, now.AddHours(-2 * (i + 1)));
        return lcu;
    }

    private static async Task Journal()
    {
        Console.WriteLine("журнал пишет врагов:");
        SessionTracker.DropCache();
        // Леона: 1–3, Блицкранк: 2–0.
        var games = new List<(int, bool)> { (Leona, false), (Blitz, true), (Leona, true), (Leona, false), (Blitz, true), (Leona, false) };
        using var lcu = Client(games);
        using var http = new LcuHttpClient(lcu.Credentials);
        await SessionTracker.RefreshAsync(http, CancellationToken.None);

        var foes = SessionTracker.FoeGames();
        Check("первый запуск: у всех шести игр есть соперник на линии", foes.Count == 6, $"{foes.Count}");
        Check("роль — саппорт, соперник — их саппорт",
              foes.All(f => f.Role == "support") && foes.Count(f => f.Foe == Leona) == 4 && foes.Count(f => f.Foe == Blitz) == 2,
              string.Join(", ", foes.Select(f => $"{f.Role}:{f.Foe}")));
        Check("исходы те же: против Леоны 1–3, против Блицкранка 2–0",
              foes.Count(f => f.Foe == Leona && f.Win) == 1 && foes.Count(f => f.Foe == Blitz && f.Win) == 2, "");
        var stored = Acc()["Queues"]!["solo"]!["Games"]!.AsArray();
        Check("в записи журнала — враги по ролям (саппорт и стрелок)",
              stored.All(g => g?["Foes"]?["support"] is not null && g["Foes"]?["adc"] is not null),
              stored.FirstOrDefault()?["Foes"]?.ToJsonString() ?? "нет");
    }

    // ── 1б. Дочитывание старых игр ─────────────────────────────────────────

    private static async Task Backfill(string root)
    {
        Console.WriteLine("\nстарые игры дочитываются понемногу:");
        File.Delete(Path.Combine(root, "session.json"));
        File.Delete(Path.Combine(root, "session.json.bak"));
        SessionTracker.DropCache();
        var games = Enumerable.Range(0, 30).Select(i => (i % 3 == 0 ? Blitz : Leona, i % 2 == 0)).ToList();
        using var lcu = Client(games);
        using var http = new LcuHttpClient(lcu.Credentials);

        await SessionTracker.RefreshAsync(http, CancellationToken.None);
        var first = SessionTracker.FoeGames().Count;
        Check("за одно обновление — не больше 25 игр", first == 25, $"{first}");
        SessionTracker.DropCache();
        await SessionTracker.RefreshAsync(http, CancellationToken.None);
        var second = SessionTracker.FoeGames().Count;
        Check("за второе — остальные", second == 30, $"{second}");
        var asks = lcu.DetailAsks;
        SessionTracker.DropCache();
        await SessionTracker.RefreshAsync(http, CancellationToken.None);
        Check("прочитанные второй раз не спрашивают", lcu.DetailAsks == asks, $"{lcu.DetailAsks - asks} лишних");
    }

    // ── 2–3. Баны и карточка ──────────────────────────────────────────────

    private static void Engine()
    {
        Console.WriteLine("\nбаны и карточка:");
        var playerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  базы нет — движок проверять нечем"); return; }
        Assets(playerDir);
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var times = Enumerable.Range(0, SessionTracker.PlayHistory.KeepTimes).Select(i => now - i * 86400L).ToArray();
        var pool = new Dictionary<int, int> { [Sona] = 20, [Braum] = 15 };
        SessionTracker.HistoryOverride = new SessionTracker.PlayHistory(
            pool.ToDictionary(kv => kv.Key, kv => (kv.Value, kv.Value / 2)), pool.ToDictionary(kv => kv.Key, _ => times), 60, now);

        // Фон: 30 ровных игр против разных саппортов — мой винрейт на роли 50%.
        var bg = new[] { 412, 111, 117, 16, 267, 40, 432, 350, 147, 25 };
        var games = new List<SessionTracker.FoeGame>();
        for (int i = 0; i < 30; i++) games.Add(new(i % 2 == 0 ? Sona : Braum, "support", bg[i % bg.Length], i % 2 == 0, now - i * 3600));
        var baseline = Bans(engine, games);
        // Леоне я проигрываю 1–9, Блицкранка обыгрываю 9–1 (оба частые саппорты).
        var vs = new List<SessionTracker.FoeGame>(games);
        for (int i = 0; i < 10; i++) vs.Add(new(Sona, "support", Leona, i == 0, now - i * 7200));
        for (int i = 0; i < 10; i++) vs.Add(new(Braum, "support", Blitz, i != 0, now - i * 7200));
        var with = Bans(engine, vs);

        double Score(IReadOnlyList<BanRec> l, int id) => l.FirstOrDefault(b => b.ChampionId == id)?.Score ?? 0;
        var personal = Loc.T("reason.personalVs", "\u0001", "\u0001").Split('\u0001')[0];
        Console.WriteLine($"  Леона {Score(baseline, Leona):F1} → {Score(with, Leona):F1}; Блицкранк {Score(baseline, Blitz):F1} → {Score(with, Blitz):F1}");
        Check("проигрываю 1–9 — очки бана растут", Score(with, Leona) > Score(baseline, Leona) + 1, "");
        var leona = with.FirstOrDefault(b => b.ChampionId == Leona);
        Check("и довод «Твой счёт против него: 1–9»",
              leona?.Reasons.Any(r => r.StartsWith(personal, StringComparison.Ordinal) && r.Contains("1–9")) == true,
              leona is null ? "Леоны нет" : string.Join(" | ", leona.Reasons));
        Check("обыгрываю 9–1 — очки падают", Score(with, Blitz) < Score(baseline, Blitz) - 1, "");
        var blitz = with.FirstOrDefault(b => b.ChampionId == Blitz);
        Check("и про счёт против него не пишет",
              blitz is null || !blitz.Reasons.Any(r => r.StartsWith(personal, StringComparison.Ordinal)),
              blitz is null ? "" : string.Join(" | ", blitz.Reasons));
        // 3–3 — ничего: перевес около нуля (усадка к ожиданию).
        var even = new List<SessionTracker.FoeGame>(games);
        for (int i = 0; i < 6; i++) even.Add(new(Sona, "support", Leona, i % 2 == 0, now - i * 7200));
        SessionTracker.FoeGamesOverride = even;
        var e = engine.PersonalVsFoes("support")[Leona];
        Check("3–3 — перевес около нуля (меньше пункта)", Math.Abs(e.Edge) < 1.0, $"{e.Edge:+0.0;-0.0}");
        SessionTracker.FoeGamesOverride = vs;
        var l = engine.PersonalVsFoes("support")[Leona];
        Check("1–9 — перевес в пределах жёсткой контры (−3…−10)", l.Edge is < -3 and > -10, $"{l.Edge:+0.0;-0.0}");

        // Карточка: я саппорт, их саппорт — Леона, у меня на Соне против неё 2–1.
        var card = new List<SessionTracker.FoeGame>(games)
        {
            new(Sona, "support", Leona, true, now - 100), new(Sona, "support", Leona, false, now - 200),
            new(Sona, "support", Leona, true, now - 300), new(Braum, "support", Leona, true, now - 400),
        };
        SessionTracker.FoeGamesOverride = card;
        var recs = engine.Recommend(Pick(Leona), 60, [Sona, Braum]);
        var line = Loc.T("reason.personalMatchup", "\u0001", "\u0001", "\u0001").Split('\u0001')[0];
        var sona = recs.FirstOrDefault(r => r.ChampionId == Sona);
        var braum = recs.FirstOrDefault(r => r.ChampionId == Braum);
        Check("карточка Соны: «Твои игры на нём против Леона: 2–1»",
              sona?.Reasons.Any(r => r.Contains(line) && r.Contains("2–1")) == true,
              sona is null ? "Соны нет" : string.Join(" | ", sona.Reasons));
        Check("у Браума одна игра — строки нет", braum is not null && !braum.Reasons.Any(r => r.Contains(line)),
              braum is null ? "Браума нет" : string.Join(" | ", braum.Reasons));
        SessionTracker.FoeGamesOverride = null;
    }

    private static IReadOnlyList<BanRec> Bans(RecommendationEngine engine, List<SessionTracker.FoeGame> games)
    {
        SessionTracker.FoeGamesOverride = games;
        var team = new List<DraftPlayer>
        {
            new(0, 0, 0, "top", false), new(1, 0, 0, "jungle", false), new(2, 0, 0, "middle", false),
            new(3, 0, 0, "bottom", false), new(4, 0, 0, "utility", true),
        };
        return engine.RecommendBans(new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [], Me: team[4], MyPosition: "utility",
            DirectOpponent: null, ExposedToCounter: false, InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [], FirstPickCell: -1,
            MyBanActionId: 0, MyBanInProgress: true), null, top: 200);
    }

    private static DraftState Pick(int opp)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        var foe = new DraftPlayer(5, opp, 0, "utility", false);
        return new DraftState(
            MyTeam: team, TheirTeam: [foe], MyTeamBans: [], TheirTeamBans: [], Me: team[0], MyPosition: "utility",
            DirectOpponent: foe, ExposedToCounter: false, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 1, MyPickInProgress: true, ActiveCells: [0], FirstPickCell: 0,
            MyBanActionId: -1, MyBanInProgress: false);
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static JsonObject Acc() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppPaths.Root, "session.json")))!["Accounts"]![Me]!.AsObject();

    private static void Assets(string playerDir)
    {
        var from = Path.Combine(playerDir, "ddragon");
        if (Directory.Exists(from))
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
