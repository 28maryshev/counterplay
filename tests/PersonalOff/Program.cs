using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Выключатель личного мастерства (настройка DraftPersonal, владелец 19.11:
/// «голый алгоритм подбора чисто по чемпионам»).
///
/// Выключено — у пика нет наигранности и личного винрейта, его оценка ровно
/// та же, что у игрока без истории и очков мастерства; нет строк «твой
/// винрейт», «ты много играешь», «твои игры против Y»; личный счёт против
/// соперника не двигает баны; в ARAM наигранности нет. Включили обратно —
/// всё вернулось без перезапуска (настройку движок читает на каждом расчёте).
///
/// Живая база игрока — только чтение; журнал, очки мастерства и счёт против
/// соперников подменены; настройки — в своей папке (AppPaths.RootOverride).
/// Нет базы — проверять нечем, проверка это пишет и выходит с успехом.
/// </summary>
internal static class Program
{
    private static int _fails;
    private const int Sona = 37, Leona = 89, Braum = 201, Nami = 267;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PersonalOff");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        try
        {
            var playerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
            DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — движок проверять нечем"); return 0; }
            Assets(playerDir);
            using var engine = RecommendationEngine.Create(db, "emerald");

            Check("по умолчанию личное мастерство включено", AppSettings.Current.DraftPersonal, "");
            Picks(engine);
            Bans(engine);
            Aram(engine);
        }
        finally
        {
            AppSettings.Current.DraftPersonal = true;
            SessionTracker.FoeGamesOverride = null;
            SessionTracker.HistoryOverride = null;
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: без личного мастерства подбор голый, с ним — как был"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// Мой месяц: Сона 20 игр (15 побед), Браум 15 (7), Нами 6 (5) — свежие,
    /// игры каждый день. Пусто — игрок без истории.
    private static void Me(RecommendationEngine engine, bool played)
    {
        var games = played
            ? new Dictionary<int, (int, int)> { [Sona] = (20, 15), [Braum] = (15, 7), [Nami] = (6, 5) }
            : new Dictionary<int, (int, int)>();
        var times = Enumerable.Range(0, SessionTracker.PlayHistory.KeepTimes).Select(i => Now - i * 86400L).ToArray();
        SessionTracker.HistoryOverride = new SessionTracker.PlayHistory(
            games, games.ToDictionary(kv => kv.Key, _ => times), 60, Now);
        engine.Mastery = played
            ? new Dictionary<int, long> { [Sona] = 300_000, [Braum] = 120_000, [Nami] = 40_000 }
            : new Dictionary<int, long>();
    }

    /// Фон: 30 ровных игр против разных саппортов — мой винрейт на роли 50%.
    private static List<SessionTracker.FoeGame> Background()
    {
        var bg = new[] { 412, 111, 117, 16, 40, 432, 350, 147, 25, 63 };
        return [.. Enumerable.Range(0, 30).Select(i =>
            new SessionTracker.FoeGame(i % 2 == 0 ? Sona : Braum, "support", bg[i % bg.Length], i % 2 == 0, Now - i * 3600))];
    }

    // ── Пики ──────────────────────────────────────────────────────────────

    private static void Picks(RecommendationEngine engine)
    {
        Console.WriteLine("пики против Леоны:");
        int[] only = [Sona, Braum, Nami];

        // Голая оценка: игрок без истории, без очков, без счёта против Леоны.
        Me(engine, played: false);
        SessionTracker.FoeGamesOverride = [];
        var bare = engine.Recommend(Pick(Leona), only.Length, only);

        // Игрок с историей; против Леоны на Соне 2–1.
        Me(engine, played: true);
        var vs = Background();
        vs.AddRange([new(Sona, "support", Leona, true, Now - 100), new(Sona, "support", Leona, false, Now - 200),
                     new(Sona, "support", Leona, true, Now - 300)]);
        SessionTracker.FoeGamesOverride = vs;

        var on = engine.Recommend(Pick(Leona), only.Length, only);
        var sonaOn = on.FirstOrDefault(r => r.ChampionId == Sona);
        Check("включено: у Соны есть наигранность и личный винрейт",
              sonaOn?.Facts is { Comfort: > 0, Personal: > 0 },
              sonaOn?.Facts is { } f ? $"наигранность {f.Comfort:F2}, винрейт {f.Personal:F2}" : "Соны нет");
        Check("включено: строка «Твои игры на нём против Леона: 2–1»",
              sonaOn?.Reasons.Any(r => r.Contains(MatchupLine) && r.Contains("2–1")) == true,
              sonaOn is null ? "" : string.Join(" | ", sonaOn.Reasons));
        Check("включено: оценка Соны выше голой", sonaOn is not null && sonaOn.Score > Score(bare, Sona) + 0.5,
              $"{Score(bare, Sona):F2} → {sonaOn?.Score:F2}");

        AppSettings.Current.DraftPersonal = false;
        var off = engine.Recommend(Pick(Leona), only.Length, only);
        foreach (var id in only)
        {
            var r = off.FirstOrDefault(x => x.ChampionId == id);
            var name = DataDragon.Name(id);
            Check($"выключено: {name} — та же оценка, что у игрока без истории",
                  r is not null && Math.Abs(r.Score - Score(bare, id)) < 1e-9,
                  $"голая {Score(bare, id):F4}, сейчас {r?.Score:F4}");
            Check($"выключено: {name} — ноль наигранности и личного винрейта",
                  r?.Facts is { Comfort: 0, Personal: 0 } && r.ComfortDelta == 0,
                  r?.Facts is { } f2 ? $"{f2.Comfort:F2} / {f2.Personal:F2}" : "нет");
            var personal = r?.Reasons.Where(x => x.Contains(MatchupLine) || x.Contains(WrLine)
                                                 || x.Contains(Loc.T("reason.comfortHigh"))
                                                 || x.Contains(Loc.T("reason.comfortMid"))).ToList() ?? [];
            Check($"выключено: {name} — ни строки про свои игры", personal.Count == 0, string.Join(" | ", personal));
            var icons = r?.Badges?.Icons
                .Where(i => i.Kind is "comfort" or "familiar" or "personalUp" or "personalDown").ToList() ?? [];
            Check($"выключено: {name} — без значков наигранности и личного винрейта", icons.Count == 0,
                  string.Join(", ", icons.Select(i => i.Kind)));
        }

        AppSettings.Current.DraftPersonal = true;
        var back = engine.Recommend(Pick(Leona), only.Length, only).FirstOrDefault(r => r.ChampionId == Sona);
        Check("включили обратно — без перезапуска оценка Соны прежняя",
              back is not null && sonaOn is not null && Math.Abs(back.Score - sonaOn.Score) < 1e-9,
              $"{sonaOn?.Score:F4} / {back?.Score:F4}");
    }

    // ── Баны ──────────────────────────────────────────────────────────────

    private static void Bans(RecommendationEngine engine)
    {
        Console.WriteLine("\nбаны (Леоне проигрываю 1–9):");
        Me(engine, played: true);
        var bg = Background();
        var vs = new List<SessionTracker.FoeGame>(bg);
        for (int i = 0; i < 10; i++) vs.Add(new(Sona, "support", Leona, i == 0, Now - i * 7200));

        var onBase = BanList(engine, bg);
        var onVs   = BanList(engine, vs);
        Check("включено: счёт 1–9 поднимает Леону", Score(onVs, Leona) > Score(onBase, Leona) + 1,
              $"{Score(onBase, Leona):F2} → {Score(onVs, Leona):F2}");

        AppSettings.Current.DraftPersonal = false;
        var offBase = BanList(engine, bg);
        var offVs   = BanList(engine, vs);
        Check("выключено: счёт против Леоны её не двигает", Math.Abs(Score(offVs, Leona) - Score(offBase, Leona)) < 1e-9,
              $"{Score(offBase, Leona):F4} / {Score(offVs, Leona):F4}");
        var leona = offVs.FirstOrDefault(b => b.ChampionId == Leona);
        Check("выключено: довода «Твой счёт против него» нет",
              leona is null || !leona.Reasons.Any(r => r.StartsWith(VsLine, StringComparison.Ordinal)),
              leona is null ? "" : string.Join(" | ", leona.Reasons));
        AppSettings.Current.DraftPersonal = true;
    }

    // ── ARAM ─────────────────────────────────────────────────────────────

    private static void Aram(RecommendationEngine engine)
    {
        Console.WriteLine("\nARAM:");
        Me(engine, played: true);
        var on = engine.RecommendAram(AramState(), 6).FirstOrDefault(r => r.ChampionId == Sona);
        Check("включено: наигранность на Соне есть", on is { ComfortDelta: > 0 }, $"{on?.ComfortDelta:F2}");
        AppSettings.Current.DraftPersonal = false;
        var off = engine.RecommendAram(AramState(), 6).FirstOrDefault(r => r.ChampionId == Sona);
        Check("выключено: наигранности нет", off is { ComfortDelta: 0 }, $"{off?.ComfortDelta:F2}");
        AppSettings.Current.DraftPersonal = true;
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static string MatchupLine => Prefix("reason.personalMatchup", 3);
    private static string WrLine      => Prefix("reason.personalWr", 2);
    private static string VsLine      => Prefix("reason.personalVs", 2);

    /// Начало строки до первой подстановки — чтобы искать её среди доводов.
    private static string Prefix(string key, int args) =>
        Loc.T(key, Enumerable.Repeat<object>("\u0001", args).ToArray()).Split('\u0001')[0];

    private static double Score(IEnumerable<Recommendation> l, int id) => l.FirstOrDefault(r => r.ChampionId == id)?.Score ?? double.NaN;
    private static double Score(IEnumerable<BanRec> l, int id) => l.FirstOrDefault(b => b.ChampionId == id)?.Score ?? 0;

    private static IReadOnlyList<BanRec> BanList(RecommendationEngine engine, List<SessionTracker.FoeGame> games)
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

    private static DraftState AramState()
    {
        var team = new List<DraftPlayer> { new(0, Sona, 0, "", true) };
        return new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [], Me: team[0], MyPosition: "",
            DirectOpponent: null, ExposedToCounter: false, InBanPhase: false, Bench: [Braum, Nami], IsAram: true,
            MyPickActionId: 1, MyPickInProgress: false, ActiveCells: [], FirstPickCell: -1,
            MyBanActionId: -1, MyBanInProgress: false);
    }

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
