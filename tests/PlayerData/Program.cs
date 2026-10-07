using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Песочница не трогает данные игрока — и пулы со статистикой после неё на месте.
///
/// Повод (7 октября). Песочница писала свой аккаунт «test-account» в настоящий
/// pools.json, переключала очередь графика в его журнале игр, сохраняла поверх
/// его настроек и синхронизировала его файлы под чужой строкой. Тестовый
/// аккаунт уехал на сервер. Владелец: «добавь проверку перед релизом, чтобы
/// данные и статистика больше никогда не слетали после того, как я что-то
/// настраиваю в песочнице».
///
/// Как устроено:
/// 1. В коде нет путей в папку игрока мимо AppPaths. Иначе подмена папки не
///    работает, и проверки (а то и песочница) пишут в настоящие файлы.
/// 2. Во временной папке штатным кодом собираются «данные игрока»: пулы с
///    дуо-пулом, журнал игр со связками, настройки, пароль синхронизации.
/// 3. Песочница проходит всё, что в ней делают: вход, профиль «мой аккаунт»,
///    правка пулов, выбор друга, очередь графика, настройки, язык, пароль,
///    синхронизация, возврат к тестовому профилю, выход в боевой режим. Шаги —
///    те же методы, что зовёт сама песочница (TestMode.EnterSandboxData и др.).
/// 4. Ни один файл игрока не изменился, новые файлы — только в sandbox/.
///    После выхода пулы, дуо-пул и статистика игрока видны, как были, а
///    подмены песочницы сняты.
///
/// Настоящая папка игрока не трогается: всё во временной. Для страховки её
/// файлы сверяются до и после.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Me     = "PLAYER-0000000000000000000000000000000000000000000000000000000000";
    private const string Friend = "FRIEND-0000000000000000000000000000000000000000000000000000000000";

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        // Журнал выбирает файл один раз, при первой записи, — и это не данные
        // игрока. Выключен, как во всех проверках.
        Log.FileDisabled = true;

        var real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        var realBefore = Snapshot(real, skipSandbox: false);

        // ── 1. Мимо AppPaths в папку игрока никто не ходит ─────────────────
        Console.WriteLine("── пути в папку игрока ──");
        var bypass = Bypasses();
        Check("все пути в папку игрока идут через AppPaths", bypass.Count == 0,
              bypass.Count == 0 ? "" : string.Join("; ", bypass));

        // ── 2. Данные игрока во временной папке ────────────────────────────
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-playerdata", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        try
        {
            Seed(root);
            var before = Snapshot(root, skipSandbox: true);
            Console.WriteLine($"\nданные игрока: {before.Count} файлов во временной папке");

            // ── 3. Песочница ───────────────────────────────────────────────
            Console.WriteLine("\n── песочница ──");
            Sandbox.Active = true;     // как Program при запуске с «test»
            Party.Sandbox  = true;
            Log.Write("проверка PlayerData: песочница");

            var dbPath = FindDb();
            if (dbPath is null)
            {
                Console.Error.WriteLine("не нашёл базу (data.db или data-slice.db)");
                return 2;
            }
            using var engine = Quiet(() => RecommendationEngine.Create(dbPath, "emerald"));
            var tm = typeof(PoolStore).Assembly.GetType("Counterplay.TestMode")!;
            void Call(string name, params object[] args) =>
                tm.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

            Call("EnterSandboxData", engine);
            Check("у песочницы свои пулы", PoolStore.Current().Pools.Any(p => p.Name == "Тест"),
                  string.Join(", ", PoolStore.Current().Pools.Select(p => p.Name)));

            // Профиль «мой аккаунт»: копия пулов игрока.
            Call("ApplyMyData", engine, true);
            var copy = PoolStore.Current();
            Check("«мой аккаунт» видит пулы игрока", copy.Pools.Any(p => p.Name == "Мой пул")
                  && copy.DuoPools.Any(d => d.FriendPuuid == Friend),
                  string.Join(", ", copy.Pools.Select(p => p.Name).Concat(copy.DuoPools.Select(d => d.FriendName))));
            Check("и его связки", SessionTracker.TopPairs(Friend, 50).Count > 0,
                  $"{SessionTracker.TopPairs(Friend, 50).Count}");

            // Всё, что в песочнице настраивают.
            copy.Pools.Add(new ChampPool { Name = "Правка в песочнице", ByRole = new() { ["mid"] = [103] } });
            copy.DuoPools[0].Friend["mid"] = [4, 45, 99];
            PoolStore.Persist();
            PoolStore.SetActive(PoolKind.Duo, copy.DuoPools[0].Id);
            PoolStore.SetActive(PoolKind.Pool, copy.Pools[0].Id);
            Party.SandboxMate(Friend, "Друг игрока");
            SessionTracker.SetSelectedQueue("aram");
            AppSettings.Current.DuoWinratesAllTime = !AppSettings.Current.DuoWinratesAllTime;
            AppSettings.Current.PoolSplit = 0.31;
            AppSettings.SaveQuiet();
            AppSettings.Save();
            Settings.Set("language", "xx");
            Check("пароль из песочницы не меняется", !SyncPassword.Set("чужой-пароль"), "");
            var sync = await SyncClient.SyncAsync(Me);
            Check("песочница не синхронизируется", !sync.Ok, sync.Message);
            await SyncClient.AutoAsync(Me, "проверка");
            Log.Write("проверка PlayerData: всё настроено");

            // Обратно к тестовому профилю и снова к своему — как щёлкают в панели.
            Call("ApplyMyData", engine, false);
            Call("ApplyMyData", engine, true);
            Call("ApplyMyData", engine, false);

            // Выход в боевой режим.
            Call("LeaveSandboxData");

            // ── 4. Данные игрока целы и видны ──────────────────────────────
            Console.WriteLine("\n── после песочницы ──");
            var after = Snapshot(root, skipSandbox: true);
            var changed = before.Where(kv => !after.TryGetValue(kv.Key, out var h) || h != kv.Value)
                                .Select(kv => kv.Key).ToList();
            var added = after.Keys.Except(before.Keys).ToList();
            Check("ни один файл игрока не изменился", changed.Count == 0, string.Join(", ", changed));
            Check("новые файлы — только в папке песочницы", added.Count == 0, string.Join(", ", added));

            Check("подмены песочницы сняты",
                  !Sandbox.Active && PoolStore.DirOverride is null && SessionTracker.PreviewGames is null
                  && SessionTracker.Preview is null && DuoShare.Preview is null && !Party.Sandbox, "");

            PoolStore.Reload();
            PoolStore.SetAccount(Me, "Игрок");
            var mine = PoolStore.Current();
            Check("пулы игрока на месте", mine.Pools.Count == 1 && mine.Pools[0].Name == "Мой пул",
                  string.Join(", ", mine.Pools.Select(p => p.Name)));
            Check("дуо-пул на месте", mine.DuoPools.Count == 1 && mine.DuoPools[0].FriendPuuid == Friend
                  && mine.DuoPools[0].Friend["mid"].SequenceEqual([38, 134]),
                  string.Join(", ", mine.DuoPools.Select(d => $"{d.FriendName} {string.Join("/", d.Friend.GetValueOrDefault("mid") ?? [])}")));
            Check("тестового аккаунта у игрока нет", PoolStore.ReadLive("test-account") is null, "");

            SessionTracker.UseStoredAccount(true);   // как после входа в клиент
            var pairs = SessionTracker.TopPairs(Friend, 50);
            var mate  = SessionTracker.MateStats(Friend);
            Check("связки с другом видны", pairs.Count == 2 && mate.Games == 7 && mate.Wins == 4,
                  $"пар {pairs.Count}, игр {mate.Games}, побед {mate.Wins}");
            var stats = SessionTracker.ChampStatsMap(0, SessionTracker.QueuesRanked);
            Check("статистика по чемпионам видна", stats.GetValueOrDefault(89).Games == 4 && stats.GetValueOrDefault(412).Games == 3,
                  string.Join(", ", stats.Select(kv => $"{kv.Key}: {kv.Value.Games}")));
            Check("очередь графика игрока не переключилась", SessionTracker.GetSelectedQueue() == "solo",
                  SessionTracker.GetSelectedQueue());
            SessionTracker.UseStoredAccount(false);
        }
        finally
        {
            AppPaths.RootOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { /* временная — не страшно */ }
        }

        // Страховка: настоящая папка игрока. Если рядом работает программа, она
        // сама пишет в свои файлы — тогда разница не наша.
        var realAfter = Snapshot(real, skipSandbox: false);
        var realChanged = realBefore.Where(kv => kv.Key != "log.txt"
                                                 && (!realAfter.TryGetValue(kv.Key, out var h) || h != kv.Value))
                                    .Select(kv => kv.Key).ToList();
        var running = Process.GetProcessesByName("Counterplay").Length > 0;
        if (realChanged.Count > 0 && running)
            Console.WriteLine($"  [?] в настоящей папке изменилось {string.Join(", ", realChanged)} — "
                              + "рядом работает программа, это могла быть она");
        else
            Check("настоящая папка игрока не тронута", realChanged.Count == 0, string.Join(", ", realChanged));

        Console.WriteLine(_fails == 0 ? "\nИТОГ: песочница данные игрока не трогает" : $"\nИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// «Данные игрока» штатным кодом: пулы с дуо-пулом, журнал игр со связками
    /// (его пишет только обновление из клиента — кладём файлом той же схемы),
    /// настройки интерфейса, язык, пароль синхронизации.
    /// </summary>
    private static void Seed(string root)
    {
        PoolStore.DirOverride = null;
        PoolStore.Reload();
        PoolStore.SetAccount(Me, "Игрок");
        var a = PoolStore.Current();
        a.Pools.Add(new ChampPool { Name = "Мой пул", ByRole = new() { ["support"] = [89, 412] } });
        a.DuoPools.Add(new DuoPool
        {
            FriendName = "С другом", FriendPuuid = Friend, FriendNick = "Друг игрока",
            Mine = new() { ["support"] = [89, 412] }, Friend = new() { ["mid"] = [38, 134] },
        });
        PoolStore.Persist();
        PoolStore.SetActive(PoolKind.Duo, a.DuoPools[0].Id);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long Ago(int days) => now - days * 86400L;
        var session = $$"""
        {
          "LastAccount": "{{Me}}",
          "Accounts": {
            "{{Me}}": {
              "Nick": "Игрок",
              "SelectedQueue": "solo",
              "QueueChosen": true,
              "Queues": {
                "solo": { "Games": [
                  { "Ts": {{Ago(6)}}, "ChampionId": 89,  "Win": true,  "Lp": 20,  "Wr": 51 },
                  { "Ts": {{Ago(5)}}, "ChampionId": 89,  "Win": false, "Lp": -19, "Wr": 50 },
                  { "Ts": {{Ago(4)}}, "ChampionId": 412, "Win": true,  "Lp": 21,  "Wr": 51 },
                  { "Ts": {{Ago(3)}}, "ChampionId": 89,  "Win": true,  "Lp": 20,  "Wr": 52 },
                  { "Ts": {{Ago(2)}}, "ChampionId": 412, "Win": false, "Lp": -20, "Wr": 51 },
                  { "Ts": {{Ago(1)}}, "ChampionId": 89,  "Win": false, "Lp": -18, "Wr": 50 },
                  { "Ts": {{Ago(1)}}, "ChampionId": 412, "Win": true,  "Lp": 19,  "Wr": 51 }
                ] }
              },
              "Pairs": {
                "{{Friend}}|solo|89|38|support|mid":   { "Won": [{{Ago(6)}}, {{Ago(3)}}], "Lost": [{{Ago(5)}}, {{Ago(1)}}], "Name": "Друг игрока" },
                "{{Friend}}|solo|412|134|support|mid": { "Won": [{{Ago(4)}}, {{Ago(1)}}], "Lost": [{{Ago(2)}}], "Name": "Друг игрока" }
              }
            }
          }
        }
        """;
        File.WriteAllText(Path.Combine(root, "session.json"), session);

        AppSettings.Current.DuoWinratesAllTime = false;
        AppSettings.Current.PoolSplit = 0.55;
        AppSettings.SaveQuiet();
        Settings.Set("language", "ru");
        SyncPassword.Set("пароль-игрока");
    }

    /// Места в коде, которые собирают путь в папку игрока сами, мимо AppPaths.
    private static List<string> Bypasses()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var res = new List<string>();
        foreach (var f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(f) == "AppPaths.cs") continue;
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                if (Regex.IsMatch(lines[i], @"SpecialFolder\.ApplicationData\b"))
                    res.Add($"{Path.GetRelativePath(src, f)}:{i + 1}");
        }
        return res;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Counterplay.csproj")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("не нашёл корень репозитория");
    }

    /// Файл → хэш. Папку песочницы пропускаем: там песочнице и место.
    private static Dictionary<string, string> Snapshot(string dir, bool skipSandbox)
    {
        var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return res;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(dir, f);
            if (skipSandbox && rel.StartsWith("sandbox" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            // Кэши картинок и справочников общие с боевой программой и не данные игрока.
            if (!skipSandbox && Regex.IsMatch(rel, @"^(icons|items|ddragon|sandbox)\\|^data\.db|^data-version")) continue;
            try { res[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))); }
            catch { /* занят — пропустим */ }
        }
        return res;
    }

    private static string? FindDb()
    {
        var live = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "data.db");
        if (File.Exists(live)) return live;
        string? dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6 && dir is not null; i++)
        {
            var p = Path.Combine(dir, "data-slice.db");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static T Quiet<T>(Func<T> f)
    {
        var o = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { return f(); } finally { Console.SetOut(o); }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
