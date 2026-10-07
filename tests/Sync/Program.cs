using System.IO;
using System.Text;
using System.Net.Http;
using System.Text.Json.Nodes;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Синхронизация между компьютерами — сквозной прогон через настоящий сервер.
///
/// Изображаем два компьютера одного человека: первый выкладывает, второй
/// забирает, потом оба правят своё и сливаются. Проверяем главное — что при
/// слиянии НИЧЕГО НЕ ПРОПАДАЕТ: пулы и история у игрока в одном экземпляре, и
/// потерянное он назад не получит.
///
/// Работаем с НАСТОЯЩИМИ файлами в папке программы: другого пути к SyncClient
/// нет. Всё, что было, сохраняется и возвращается на место, в том числе при
/// падении. Пароль владельца тоже — он в отдельном файле и переживает прогон.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Свой профиль: строка на сервере не пересечётся ни с чьей настоящей.
    private const string TestPuuid = "TEST-sync-e2e-000000000000000000000000000000000000000000000000000000";
    private const string TestPass = "проверка-синхронизации-e2e";

    private static string Dir => AppPaths.Root;

    private static readonly string[] Files = ["pools.json", "ui.json", "session.json", "sync.dat", "sync.rev"];

    private static async Task<int> Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к любому
        // хранилищу. Раньше проверка писала в настоящие файлы и «возвращала
        // как было»: правку, сделанную рядом работающей программой, это откатывало.
        AppPaths.RootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "counterplay-test-root", "Sync");
        System.IO.Directory.CreateDirectory(AppPaths.RootOverride);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        // Сохраняем всё, что тронем.
        var saved = new Dictionary<string, string?>();
        foreach (var f in Files)
        {
            var p = Path.Combine(Dir, f);
            saved[f] = File.Exists(p) ? Convert.ToBase64String(File.ReadAllBytes(p)) : null;
        }

        try
        {
            await Run();
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"сервер недоступен ({e.Message}) — проверять нечего");
            return 0;
        }
        finally
        {
            foreach (var (f, data) in saved)
            {
                var p = Path.Combine(Dir, f);
                if (data is null) { if (File.Exists(p)) File.Delete(p); }
                else File.WriteAllBytes(p, Convert.FromBase64String(data));
            }
            foreach (var junk in Directory.GetFiles(Dir, "*.before-sync")) File.Delete(junk);
            Console.WriteLine("\nсвои файлы и пароль возвращены на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: синхронизация переносит и сливает без потерь"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static async Task Run()
    {
        SyncPassword.Set(TestPass);

        // ── Компьютер А: свои пулы и своя история ──────────────────────────
        WritePools("ПК-А");
        WriteSession(gameTs: 1000, pairTs: 1000);
        var up = await SyncClient.SyncAsync(TestPuuid);
        Check("первая выкладка прошла", up.Ok, up.Message);
        if (!up.Ok) return;

        // ── Компьютер Б: пусто, всё приходит с сервера ─────────────────────
        WipeLocal();
        var down = await SyncClient.SyncAsync(TestPuuid);
        Check("на пустом компьютере всё забралось", down.Ok && down.Pulled > 0,
              $"{down.Message}, файлов {down.Pulled}");
        Check("пулы доехали", ReadPools() == "ПК-А", ReadPools());
        Check("история доехала", HasGame(1000), "да");

        // ── Оба поправили своё: слияние не должно терять ───────────────────
        // Б играет свои игры…
        WriteSession(gameTs: 2000, pairTs: 2000, keepExisting: true);
        var b = await SyncClient.SyncAsync(TestPuuid);
        Check("второй компьютер выложил своё", b.Ok, b.Message);

        // …а А в это время сыграл другие и о них не знает.
        WipeLocal();
        WritePools("ПК-А");
        WriteSession(gameTs: 3000, pairTs: 3000);
        var a2 = await SyncClient.SyncAsync(TestPuuid);
        Check("слияние прошло", a2.Ok, a2.Message);

        // Главное: после слияния на месте ВСЕ три игры, а не только последние.
        Check("игра компьютера А осталась", HasGame(1000), "1000");
        Check("игра компьютера Б осталась", HasGame(2000), "2000");
        Check("новая игра добавилась", HasGame(3000), "3000");
        Check("связки слиты по всем трём", PairCount() >= 3, $"{PairCount()}");

        // ── Чужой пароль чужого не открывает ───────────────────────────────
        //
        // Пароль КАЖДЫЙ РАЗ новый, и это важно. Раньше он был постоянным, а
        // проход, как всякий проход, в конце ещё и выкладывает — так проверка
        // сама набивала «чужую» строку своими данными и со второго раза
        // забирала их обратно. Держалась она только на том, что сервер не даёт
        // заводить новые строки чаще двадцати в час: пока лимит был выбран,
        // строка оставалась пустой и проверка проходила. Лимит отпустило —
        // проверка развалилась, хотя в коде синхронизации ничего не менялось.
        SyncPassword.Set("чужой-" + Guid.NewGuid().ToString("N"));
        var alien = await SyncClient.SyncAsync(TestPuuid);
        // Другой пароль — другой адрес строки: чужого он не видит и не портит.
        Check("с другим паролем чужое не открывается", alien.Pulled == 0,
              $"забрано {alien.Pulled}");
        SyncPassword.Set(TestPass);
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static void WipeLocal()
    {
        foreach (var f in new[] { "pools.json", "ui.json", "session.json", "sync.rev" })
        {
            var p = Path.Combine(Dir, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void WritePools(string mark) =>
        File.WriteAllText(Path.Combine(Dir, "pools.json"),
            new JsonObject
            {
                ["acc"] = new JsonObject
                {
                    ["Pools"] = new JsonArray(new JsonObject
                    {
                        ["Id"] = "p1", ["Name"] = mark, ["ByRole"] = new JsonObject()
                    }),
                    ["DuoPools"] = new JsonArray()
                }
            }.ToJsonString());

    private static string ReadPools()
    {
        var p = Path.Combine(Dir, "pools.json");
        if (!File.Exists(p)) return "(нет файла)";
        var node = JsonNode.Parse(File.ReadAllText(p));
        return (string?)node?["acc"]?["Pools"]?[0]?["Name"] ?? "(не разобралось)";
    }

    /// Журнал с одной игрой и одной связкой. keepExisting — дописать к тому, что есть.
    private static void WriteSession(long gameTs, long pairTs, bool keepExisting = false)
    {
        var path = Path.Combine(Dir, "session.json");
        JsonObject root;
        if (keepExisting && File.Exists(path))
            root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        else
            root = new JsonObject { ["Accounts"] = new JsonObject() };

        var accounts = root["Accounts"]!.AsObject();
        if (accounts[TestPuuid] is not JsonObject acc)
            accounts[TestPuuid] = acc = new JsonObject();

        if (acc["Queues"] is not JsonObject queues) acc["Queues"] = queues = new JsonObject();
        if (queues["solo"] is not JsonObject solo) queues["solo"] = solo = new JsonObject();
        if (solo["Games"] is not JsonArray games) solo["Games"] = games = [];
        games.Add(new JsonObject { ["Ts"] = gameTs, ["ChampionId"] = 1, ["Win"] = true });

        if (acc["Pairs"] is not JsonObject pairs) acc["Pairs"] = pairs = new JsonObject();
        pairs[$"mate|solo|1|2"] = new JsonObject
        {
            ["Won"] = new JsonArray(pairTs),
            ["Lost"] = new JsonArray(),
            ["Name"] = "проверка"
        };
        File.WriteAllText(path, root.ToJsonString());
    }

    private static bool HasGame(long ts)
    {
        var path = Path.Combine(Dir, "session.json");
        if (!File.Exists(path)) return false;
        var games = JsonNode.Parse(File.ReadAllText(path))?["Accounts"]?[TestPuuid]?["Queues"]?["solo"]?["Games"]?.AsArray();
        return games?.Any(g => (long?)g?["Ts"] == ts) == true;
    }

    private static int PairCount()
    {
        var path = Path.Combine(Dir, "session.json");
        if (!File.Exists(path)) return 0;
        var pairs = JsonNode.Parse(File.ReadAllText(path))?["Accounts"]?[TestPuuid]?["Pairs"]?.AsObject();
        if (pairs is null) return 0;
        var n = 0;
        foreach (var (_, v) in pairs) n += v?["Won"]?.AsArray().Count ?? 0;
        return n;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
