using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Чужие игры в журнале — под любым вошедшим аккаунтом.
///
/// Владелец 14 октября вошёл на чужом компьютере, и его последние игры легли
/// в журнал её аккаунта — пачкой, одним временем, без LP (гонка при смене
/// аккаунта, до 1.3.63). Чистка 1.3.63 убирала игры ТЕКУЩЕГО аккаунта из
/// журналов других, с опорой на его свежую историю: на её компьютере сидит она,
/// его история там не видна — и 24 октября её профиль всё ещё показывал его
/// соло-игры в «последних пяти» и его чемпионов в «винрейте за 30 дней».
///
/// Здесь — компьютер хозяйки: вошла она, в её журнале пачка его игр, его записи на
/// этом компьютере без номеров (опереться не на что). Держит:
///
/// - пачка его игр убирается по подробным играм клиента (её среди участников
///   нет, он есть), её игра в той же пачке остаётся — даже та, где он играл с ней;
/// - его журнал не тронут; пачки нет — клиента больше не спрашивают;
/// - синхронизация не возвращает убранное с копии другого компьютера, а
///   компьютер, который сам не доказывал, убирает по пришедшим отпечаткам.
///
/// Папка своя (<c>AppPaths.RootOverride</c>), клиент и сервер подставные:
/// данных игрока проверка не касается.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Her = "TEST-foreign-her-000000000000000000000000000000000000000000000000000";
    private const string Owner = "TEST-foreign-owner-0000000000000000000000000000000000000000000000000";

    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    /// Когда легла пачка — десять дней назад, как у владельца.
    private static readonly long Pack = Now - 10 * 86400;

    /// Его игры: номер, очередь клиента, начало, чемпион, исход.
    private static readonly (long Id, int Queue, string Key, long Start, int Champ, bool Win)[] HisGames =
    [
        (8_000_000_001, 420, "solo",   Pack - 5 * 86400, 37,  true),
        (8_000_000_002, 420, "solo",   Pack - 4 * 86400, 37,  true),
        (8_000_000_003, 420, "solo",   Pack - 3 * 86400, 161, false),
        (8_000_000_004, 440, "flex",   Pack - 2 * 86400, 22,  true),
        (8_000_000_005, 400, "normal", Pack - 1 * 86400, 127, false),
    ];

    /// Её нормал, сыгранный с ним вместе и записанный той же пачкой.
    private const long Together = 8_000_000_009;

    private static string Dir => AppPaths.Root;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "ForeignGames");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;

        using var server = new FakeSyncServer();
        SyncClient.EndpointOverride = server.Url;
        SyncPassword.Set("чужие-игры-" + Guid.NewGuid().ToString("N"));
        try
        {
            Rule();
            await Journal();
        }
        finally
        {
            SyncClient.EndpointOverride = null;
            try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: чужие игры уходят под любым аккаунтом и не возвращаются"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Доказательство по подробной игре ────────────────────────────────

    private static void Rule()
    {
        Console.WriteLine("подробная игра — доказательство:");
        var others = new HashSet<string> { Owner };
        var (id, queue, key, start, champ, win) = HisGames[0];

        var a = SessionTracker.ForeignAnchor(id, Game(queue, start, (Owner, champ, win)), Her, others);
        Check("её нет, он есть — опора его игры",
              a is not null && a.Id == id && a.Queue == key && a.ChampionId == champ && a.Win == win && a.Start == start,
              $"{a}");
        Check("она среди участников — игра её, даже если и он там",
              SessionTracker.ForeignAnchor(id, Game(queue, start, (Owner, champ, win), (Her, 99, win)), Her, others) is null, "");
        Check("ни её, ни других аккаунтов компьютера — доказывать нечего",
              SessionTracker.ForeignAnchor(id, Game(queue, start), Her, others) is null, "");
        Check("участники без puuid — доказывать нечего",
              SessionTracker.ForeignAnchor(id, Game(queue, start, (Owner, champ, win)).Replace(Owner, ""), Her, others) is null, "");
        Check("очередь, которую журнал не ведёт, — мимо",
              SessionTracker.ForeignAnchor(id, Game(1700, start, (Owner, champ, win)), Her, others) is null, "");
    }

    // ── 2. Журнал на её компьютере и синхронизация ─────────────────────────

    private static async Task Journal()
    {
        Console.WriteLine("\nеё компьютер, вошла она:");

        // Копия её журнала с пачкой уже лежит на сервере: так было бы, если бы
        // синхронизация успела выложить его до починки.
        Write("session.json", Dirty().ToJsonString());
        var up = await SyncClient.SyncAsync(Her);
        Check("копия с пачкой выложена", up.Ok, up.Message);

        using var lcu = new FakeLcu(Her, "хозяйка",
            [(450, 14, true, DateTimeOffset.UtcNow.AddDays(-1)), (450, 63, false, DateTimeOffset.UtcNow.AddDays(-2))]);
        foreach (var g in HisGames)
            lcu.Details[g.Id] = Game(g.Queue, g.Start, (Owner, g.Champ, g.Win));
        lcu.Details[Together] = Game(400, Pack - 3600, (Her, 99, true), (Owner, 37, true));
        using var http = new LcuHttpClient(lcu.Credentials);

        var view = await SessionTracker.RefreshAsync(http, CancellationToken.None);
        var her = Acc(Her);
        Check("в «последних пяти» соло нет его игр", view is not null && view.Queues["solo"].Last5.Count == 0,
              $"{view?.Queues["solo"].Last5.Count}");
        Check("его соло и флекс убраны из её журнала",
              Games(her, "solo").Count == 0 && Games(her, "flex").Count == 0,
              $"соло {Games(her, "solo").Count}, флекс {Games(her, "flex").Count}");
        Check("её нормалы на месте, и игра с ним из той же пачки тоже",
              Games(her, "normal").Count == 4 && Games(her, "normal").Any(g => (int)g["ChampionId"]! == 99),
              string.Join(" ", Games(her, "normal").Select(g => g["ChampionId"])));
        Check("его журнал не тронут", Games(Acc(Owner), "solo").Count == 3, $"{Games(Acc(Owner), "solo").Count}");
        Check("убранное запомнено отпечатками", her["Foreign"]?.AsArray().Count == 5,
              $"{her["Foreign"]?.AsArray().Count}");

        var asked = lcu.DetailAsks;
        await SessionTracker.RefreshAsync(http, CancellationToken.None);
        Check("пачки нет — подробные игры больше не спрашиваются", lcu.DetailAsks == asked,
              $"{asked} → {lcu.DetailAsks}");

        Console.WriteLine("\nсинхронизация:");
        var back = await SyncClient.SyncAsync(Her);
        her = Acc(Her);
        Check("проход с копией, где пачка ещё есть, её не возвращает",
              back.Ok && Games(her, "solo").Count == 0 && Games(her, "flex").Count == 0 && Games(her, "normal").Count == 4,
              $"соло {Games(her, "solo").Count}, флекс {Games(her, "flex").Count}, нормал {Games(her, "normal").Count}");

        // Другой компьютер: та же пачка, но сам он ничего не доказывал.
        Write("session.json", Dirty().ToJsonString());
        var other = await SyncClient.SyncAsync(Her);
        her = Acc(Her);
        Check("компьютер без доказательства убирает по пришедшим отпечаткам",
              other.Ok && Games(her, "solo").Count == 0 && Games(her, "flex").Count == 0 && Games(her, "normal").Count == 4,
              $"соло {Games(her, "solo").Count}, флекс {Games(her, "flex").Count}, нормал {Games(her, "normal").Count}");
    }

    /// <summary>
    /// Журнал её компьютера до починки. У неё — старые нормалы и ARAM и пачка
    /// одним временем: пять его игр и её нормал с ним. У него — те же игры, но
    /// без номеров (записаны до 1.3.63): опереться на его записи нельзя.
    /// </summary>
    private static JsonObject Dirty()
    {
        var mine = new Dictionary<string, List<JsonObject>>
        {
            ["normal"] = [Rec(Now - 90 * 86400, 876, false), Rec(Now - 89 * 86400, 19, true), Rec(Now - 88 * 86400, 120, false)],
            ["aram"] = [Rec(Now - 80 * 86400, 432, true)],
            ["solo"] = [], ["flex"] = [],
        };
        foreach (var g in HisGames) mine[g.Key].Add(Rec(Pack, g.Champ, g.Win));
        mine["normal"].Add(Rec(Pack, 99, true));

        var his = new Dictionary<string, List<JsonObject>> { ["solo"] = [], ["flex"] = [], ["normal"] = [] };
        foreach (var g in HisGames) his[g.Key].Add(Rec(g.Start + 1800, g.Champ, g.Win));

        // История клиента — её ARAM; номера подставной клиент раздаёт сверху вниз.
        long[] seen = [7_000_000_000, 6_999_999_999, .. HisGames.Select(g => g.Id), Together];
        return new JsonObject
        {
            ["LastAccount"] = Her,
            ["Accounts"] = new JsonObject
            {
                [Her] = Account("хозяйка", mine, seen),
                [Owner] = Account("владелец", his, []),
            },
        };

        static JsonObject Rec(long ts, int champ, bool win) =>
            new() { ["Ts"] = ts, ["ChampionId"] = champ, ["Win"] = win, ["Wr"] = 50.0 };

        static JsonObject Account(string nick, Dictionary<string, List<JsonObject>> queues, long[] seen)
        {
            var q = new JsonObject();
            foreach (var (key, games) in queues)
                q[key] = new JsonObject { ["Games"] = new JsonArray(games.Cast<JsonNode>().ToArray()) };
            return new JsonObject
            {
                ["Nick"] = nick,
                ["SeenGames"] = new JsonArray(seen.Select(x => (JsonNode)x).ToArray()),
                ["Queues"] = q,
            };
        }
    }

    /// <summary>
    /// Подробная игра, как её отдаёт клиент: десять участников, у каждого puuid.
    /// Названные — в начале списка, остальные места занимают незнакомцы;
    /// победители в команде 100.
    /// </summary>
    private static string Game(int queue, long start, params (string Puuid, int Champ, bool Win)[] named)
    {
        var parts = new JsonArray();
        var ids = new JsonArray();
        for (var i = 0; i < 10; i++)
        {
            var (puuid, champ, win) = i < named.Length ? named[i] : ($"TEST-stranger-{i}", 500 + i, i % 2 == 0);
            parts.Add(new JsonObject
            {
                ["participantId"] = i + 1, ["championId"] = champ, ["teamId"] = win ? 100 : 200,
                ["stats"] = new JsonObject { ["win"] = win },
            });
            ids.Add(new JsonObject
            {
                ["participantId"] = i + 1, ["player"] = new JsonObject { ["puuid"] = puuid },
            });
        }
        return new JsonObject
        {
            ["queueId"] = queue, ["gameCreation"] = start * 1000, ["gameDuration"] = 1800,
            ["participants"] = parts, ["participantIdentities"] = ids,
        }.ToJsonString();
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static JsonObject Acc(string puuid) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "session.json")))!["Accounts"]![puuid]!.AsObject();

    private static List<JsonNode> Games(JsonObject acc, string queue) =>
        acc["Queues"]?[queue]?["Games"]?.AsArray().Where(g => g is not null).Select(g => g!).ToList() ?? [];

    private static void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(Dir, name), body);

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
