using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Один компьютер, два аккаунта разных людей.
///
/// Владелец ввёл свой пароль синхронизации на чужом компьютере. Строка на
/// сервере — от пароля и вошедшего аккаунта, а везла она файлы целиком: строка
/// аккаунта хозяйки стала общей для двух компьютеров, и пулы с настройками
/// одного человека заменяли пулы и настройки другого («побеждает свежий»). Там
/// же две июльские игры хозяйки её компьютер догнал из истории 24.10 и записал
/// сегодняшними: каждая легла дважды, обе — первыми в «последних пяти».
///
/// Держит:
///
/// - под хозяином пароля синхронизация везёт всё, как раньше;
/// - под другим аккаунтом — только его журнал игр: свои пулы и настройки
///   остаются, хотя на сервере свежее, журналы других аккаунтов не трогаются, а
///   строка на сервере больше не держит ни пулов, ни настроек;
/// - пароль, заданный до 1.3.74, достаётся аккаунту с самым большим журналом,
///   и выбор запоминается;
/// - догнанная из истории игра пишется временем своего конца и встаёт на своё
///   место, а не первой;
/// - поздняя копия игры с номером сводится с ранней записью очевидца; запись
///   без номера позже записи с номером не трогается — это может быть другая
///   игра на том же чемпионе.
///
/// Папка своя (<c>AppPaths.RootOverride</c>), клиент и сервер подставные:
/// данных игрока проверка не касается.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Him = "TEST-shared-him-0000000000000000000000000000000000000000000000000000";
    private const string Her = "TEST-shared-her-0000000000000000000000000000000000000000000000000000";

    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Dir => AppPaths.Root;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SharedComputer");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;

        using var server = new FakeSyncServer();
        SyncClient.EndpointOverride = server.Url;
        try
        {
            Rule();
            await LateGames();
            await OwnerSync();
            await OwnerGuess();
        }
        finally
        {
            SyncClient.EndpointOverride = null;
            try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: два человека на одном компьютере друг другу не мешают"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. «Та же игра»: ранний очевидец и поздняя копия с номером ──────────

    private static void Rule()
    {
        Console.WriteLine("та же игра — поздняя копия с номером:");
        long start = Now - 100 * 86400;
        var late = SessionTracker.SameGames(
        [
            new("normal", 0, start + 1700, 43, false, null, null),          // очевидец, июль
            new("normal", 1, Now - 3600, 43, false, 7_917_210_018, start),  // догнано сегодня
        ], []);
        Check("очевидцу — номер, поздняя копия убрана",
              late is [{ Keep: 0, Drop: [1], Id: 7_917_210_018 }],
              string.Join(" ", late.Select(s => $"{s.Keep}←{string.Join(",", s.Drop)}")));

        // Очевидец догнал игру на следующий день (до 1.3.63): по истории клиента
        // — та же игра, в слиянии (опоры только с номером) — предел три часа.
        var anchor = new SessionTracker.GameAnchor(7_917_210_018, start, "normal", 43, false);
        List<SessionTracker.GameRef> nextDay =
        [
            new("normal", 0, start + 21 * 3600, 43, false, null, null),
            new("normal", 1, Now - 3600, 43, false, 7_917_210_018, start),
        ];
        var byHistory = SessionTracker.SameGames(nextDay,
            [anchor, new(7_920_281_084, start + 3 * 86400, "aram", 42, true)]);
        Check("по истории: запись на следующий день — та же игра (до следующей игры)",
              byHistory is [{ Keep: 0, Drop: [1] }],
              string.Join(" ", byHistory.Select(s => $"{s.Keep}←{string.Join(",", s.Drop)}")));
        var byMerge = SessionTracker.SameGames(nextDay, []);
        Check("в слиянии предел три часа остаётся",
              byMerge.All(s => !s.Drop.Contains(0) && s.Keep != 0),
              string.Join(" ", byMerge.Select(s => $"{s.Keep}←{string.Join(",", s.Drop)}")));

        var other = SessionTracker.SameGames(
        [
            new("solo", 0, start + 1500, 37, true, 8_000_000_001, start),   // игра с номером
            new("solo", 1, start + 5000, 37, true, null, null),             // следующая, старой версией
        ], []);
        Check("запись без номера ПОЗЖЕ записи с номером не съедена",
              other.All(s => !s.Drop.Contains(1) && s.Keep != 1),
              string.Join(" ", other.Select(s => $"{s.Keep}←{string.Join(",", s.Drop)}")));
    }

    // ── 2. Догнанные из истории игры ───────────────────────────────────────

    /// <summary>
    /// Как у хозяйки 24.10: «виденные» — чужие номера, поэтому история не
    /// считается первым запуском, и её июльские игры догоняются как новые. Одна
    /// из них уже лежит дважды: запись очевидца июлем и копия с номером,
    /// догнанная сегодня.
    /// </summary>
    private static async Task LateGames()
    {
        Console.WriteLine("\nдогнанные из истории игры:");
        var july14 = DateTimeOffset.UtcNow.AddDays(-100);
        var july19 = DateTimeOffset.UtcNow.AddDays(-95);
        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        // Подставной клиент раздаёт номера сверху вниз, новейшим — больший.
        const long Yesterday = 7_000_000_000, July19 = 6_999_999_999, July14 = 6_999_999_998;
        using var lcu = new FakeLcu(Her, "хозяйка",
            [(400, 99, true, yesterday), (450, 42, true, july19), (400, 43, false, july14)]);

        Write("session.json", new JsonObject
        {
            ["LastAccount"] = Her,
            ["Accounts"] = new JsonObject
            {
                [Her] = Account([1, 2, 3], new()
                {
                    ["normal"] = [Rec(Now - 120 * 86400, 876, false),
                                  // Очевидец догнал её на следующий день, как в июле.
                                  Rec(july14.ToUnixTimeSeconds() + 21 * 3600, 43, false),
                                  Rec(Now - 3600, 43, false, July14, july14.ToUnixTimeSeconds())],
                    // Свежая ARAM: догнанная июльская обязана встать перед ней.
                    ["aram"] = [Rec(Now - 110 * 86400, 14, true), Rec(Now - 10 * 86400, 63, false)],
                }),
            },
        }.ToJsonString());

        using var http = new LcuHttpClient(lcu.Credentials);
        var view = await SessionTracker.RefreshAsync(http, CancellationToken.None);
        var her = Acc(Her);
        var normal = Games(her, "normal");
        var aram = Games(her, "aram");

        Check("июльская игра лежит одной записью — очевидца, с номером",
              normal.Count(g => (int)g["ChampionId"]! == 43) == 1
              && normal.Any(g => (long?)g["Id"] == July14 && (long)g["Ts"]! == july14.ToUnixTimeSeconds() + 21 * 3600),
              string.Join(" ", normal.Select(g => $"{g["ChampionId"]}@{Day((long)g["Ts"]!)}")));
        Check("догнанная игра записана концом игры, а не сегодняшним числом",
              aram.Any(g => (long?)g["Id"] == July19 && (long)g["Ts"]! == july19.ToUnixTimeSeconds() + 1800),
              string.Join(" ", aram.Select(g => $"{g["ChampionId"]}@{Day((long)g["Ts"]!)}")));
        Check("вчерашняя игра — последней в списке нормалов",
              (long?)normal[^1]["Id"] == Yesterday, $"{normal[^1]["ChampionId"]}");
        Check("порядок по времени", normal.Concat(aram).GroupBy(_ => 0).All(_ =>
                  Sorted(normal) && Sorted(aram)), "");
        var last5 = view?.Queues["normal"].Last5;
        // «Последние пять» — новейшая первой.
        Check("в «последних пяти» нормалов первая — вчерашняя, а не июльская",
              last5 is { Count: 3 } && last5[0].ChampionId == 99,
              string.Join(" ", last5?.Select(g => g.ChampionId) ?? []));

        static bool Sorted(List<JsonNode> g) =>
            g.Zip(g.Skip(1)).All(p => (long)p.First["Ts"]! <= (long)p.Second["Ts"]!);
    }

    // ── 3. Синхронизация: чей пароль ───────────────────────────────────────

    /// <summary>
    /// Его компьютер и её компьютер, оба с его паролем. Строка его аккаунта —
    /// его; строка её аккаунта — общая для двух компьютеров.
    /// </summary>
    private static async Task OwnerSync()
    {
        Console.WriteLine("\nсинхронизация — чей пароль:");
        var pass = "общий-компьютер-" + Guid.NewGuid().ToString("N");

        // Его компьютер под ним: всё уезжает, как раньше.
        HisComputer(DateTime.UtcNow.AddHours(-2));
        SyncPassword.Set(pass, Him);
        Check("пароль запомнен за тем, кто его ввёл", SyncPassword.Owner == Him, SyncPassword.Owner ?? "нет");
        var up = await SyncClient.SyncAsync(Him);
        WipeLocal();
        var down = await SyncClient.SyncAsync(Him);
        Check("под хозяином пароля пулы, настройки и журнал едут",
              up.Ok && down.Ok && Read("pools.json") == HisPools && Read("ui.json") == HisUi
              && Acc(Him)["Queues"] is not null && Acc(Her)["Queues"] is not null,
              $"{down.Message}");

        // Её компьютер под ней, пароль его (хозяин — он): её пулы и настройки
        // свежее, и раньше они уехали бы в строку её аккаунта целиком.
        WipeLocal();
        Write("pools.json", HerPools, DateTime.UtcNow);
        Write("ui.json", HerUi, DateTime.UtcNow);
        Write("session.json", Session(him: [Rec(Now - 50 * 86400, 37, true)],
                                      her: [Rec(Now - 7200, 117, true, 8_100_000_001, Now - 9000)]),
              DateTime.UtcNow);
        SyncPassword.Set(pass, Him);
        var hers = await SyncClient.SyncAsync(Her);
        Check("её компьютер: свои пулы и настройки на месте",
              hers.Ok && Read("pools.json") == HerPools && Read("ui.json") == HerUi, hers.Message);

        // Его компьютер под ней: её свежая игра пришла, его пулы и настройки —
        // нет, и его журнал не тронут.
        HisComputer(DateTime.UtcNow.AddHours(-2));
        var hisBefore = Acc(Him).ToJsonString();
        var r = await SyncClient.SyncAsync(Her);
        Check("его компьютер под ней: её свежая игра пришла",
              r.Ok && Games(Acc(Her), "solo").Any(g => (long?)g["Id"] == 8_100_000_001),
              string.Join(" ", Games(Acc(Her), "solo").Select(g => g["ChampionId"])));
        Check("его пулы и настройки не заменены её свежими",
              Read("pools.json") == HisPools && Read("ui.json") == HisUi, "");
        Check("его журнал не тронут", Acc(Him).ToJsonString() == hisBefore, "");
        Check("ни пулов, ни настроек «пришло» не насчитано",
              r.Got is { Pools: false, Settings: false }, $"{r.Got}");

        // Что лежит в строке её аккаунта: заберём её компьютером, где хозяйка —
        // она, начисто.
        WipeLocal();
        SyncPassword.Set(pass, Her);
        var peek = await SyncClient.SyncAsync(Her);
        var accounts = JsonNode.Parse(Read("session.json")!)!["Accounts"]!.AsObject();
        Check("в строке её аккаунта — только её журнал, без пулов и настроек",
              peek.Ok && Read("pools.json") is null && Read("ui.json") is null
              && accounts.Count == 1 && accounts.ContainsKey(Her),
              $"пулы {(Read("pools.json") is null ? "нет" : "есть")}, аккаунтов {accounts.Count}");
    }

    /// Пароль, заданный до 1.3.74: чей он — неизвестно до первого прохода.
    private static async Task OwnerGuess()
    {
        Console.WriteLine("\nпароль, заданный до 1.3.74:");
        var pass = "старый-пароль-" + Guid.NewGuid().ToString("N");

        HisComputer(DateTime.UtcNow.AddHours(-2));
        SyncPassword.Set(pass);
        Check("хозяин не известен", SyncPassword.Owner is null, SyncPassword.Owner ?? "");
        var r = await SyncClient.SyncAsync(Her);
        Check("достался аккаунту с самым большим журналом, а не вошедшему",
              r.Ok && SyncPassword.Owner == Him, SyncPassword.Owner ?? "нет");
        Check("и под ней пулы не тронуты", Read("pools.json") == HisPools, "");

        WipeLocal();
        SyncPassword.Set(pass);
        await SyncClient.SyncAsync(Her);
        Check("журнала нет — хозяин тот, кто вошёл", SyncPassword.Owner == Her, SyncPassword.Owner ?? "нет");
    }

    // ── данные ─────────────────────────────────────────────────────────────

    private const string HisPools = """{"him":{"Pools":[{"Name":"его пул"}]},"her":{"Pools":[]}}""";
    private const string HerPools = """{"him":{"Pools":[]},"her":{"Pools":[{"Name":"её пул"}]}}""";
    private const string HisUi = """{"Opacity":0.9,"DraftRunes":true}""";
    private const string HerUi = """{"Opacity":0.5,"DraftRunes":false}""";

    /// Его компьютер: его пулы, настройки и журнал (у него игр больше), файлы
    /// записаны раньше её.
    private static void HisComputer(DateTime at)
    {
        WipeLocal();
        Write("pools.json", HisPools, at);
        Write("ui.json", HisUi, at);
        Write("session.json", Session(
            him: [Rec(Now - 50 * 86400, 37, true), Rec(Now - 40 * 86400, 161, false), Rec(Now - 30 * 86400, 22, true)],
            her: [Rec(Now - 60 * 86400, 876, false)]), at);
    }

    private static string Session(JsonObject[] him, JsonObject[] her) => new JsonObject
    {
        ["Accounts"] = new JsonObject
        {
            [Him] = Account([], new() { ["solo"] = [.. him] }),
            [Her] = Account([], new() { ["solo"] = [.. her] }),
        },
    }.ToJsonString();

    private static JsonObject Account(long[] seen, Dictionary<string, List<JsonObject>> queues)
    {
        var q = new JsonObject();
        foreach (var (key, games) in queues)
            q[key] = new JsonObject { ["Games"] = new JsonArray(games.Cast<JsonNode>().ToArray()) };
        return new JsonObject
        {
            ["SeenGames"] = new JsonArray(seen.Select(x => (JsonNode)x).ToArray()),
            ["Queues"] = q,
        };
    }

    private static JsonObject Rec(long ts, int champ, bool win, long? id = null, long? start = null)
    {
        var o = new JsonObject { ["Ts"] = ts, ["ChampionId"] = champ, ["Win"] = win, ["Wr"] = 50.0 };
        if (id is not null) { o["Id"] = id; o["Start"] = start; }
        return o;
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static JsonObject Acc(string puuid) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "session.json")))!["Accounts"]![puuid]!.AsObject();

    private static List<JsonNode> Games(JsonObject acc, string queue) =>
        acc["Queues"]?[queue]?["Games"]?.AsArray().Where(g => g is not null).Select(g => g!).ToList() ?? [];

    private static string Day(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).ToString("dd.MM");

    private static string? Read(string name)
    {
        var p = Path.Combine(Dir, name);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    private static void Write(string name, string body, DateTime? at = null)
    {
        var p = Path.Combine(Dir, name);
        File.WriteAllText(p, body);
        if (at is { } t) File.SetLastWriteTimeUtc(p, t);
    }

    private static void WipeLocal()
    {
        foreach (var f in new[] { "pools.json", "ui.json", "session.json", "session.json.bak", "sync.rev" })
        {
            var p = Path.Combine(Dir, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
