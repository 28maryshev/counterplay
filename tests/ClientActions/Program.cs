using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Отчёт о действиях в клиенте за драфт. На клиенте Tencent подключение
/// работает, а работают ли пик, бан, чат, руны и предметы, видно не было:
/// клиента у нас нет (журнал 2026-11-26). Теперь программа считает исходы
/// своих действий и отправляет их сайту, когда драфт кончился.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        // Ничего из папки игрока: подписи набора предметов и прочее читают AppPaths.
        var root = Path.Combine(Path.GetTempPath(), "cp-client-actions-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        try
        {
            Counting();
            ReportBody();
            SiteReadsSameFields();
            await ChatOutcomes();
            await SpellsAndItems();
            await DeadClient();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    private static ClientActionStats.Entry? Of(IReadOnlyList<ClientActionStats.Entry> list, string kind) =>
        list.FirstOrDefault(e => e.Kind == kind);

    private static string Show(ClientActionStats.Entry? e) =>
        e is null ? "нет" : $"{e.Ok}/{e.Fail}, код {e.Code?.ToString() ?? "—"}, {e.Detail ?? "—"}";

    // Счёт: удачи и неудачи по видам, код и пояснение последней неудачи.
    private static void Counting()
    {
        Console.WriteLine("счёт исходов:");
        ClientActionStats.Take();
        Check(ClientActionStats.Record(ClientActionStats.Pick, 200) == 200, "Record возвращает код как есть");
        ClientActionStats.Record(ClientActionStats.Pick, 204);
        ClientActionStats.Record(ClientActionStats.Pick, 500);
        ClientActionStats.Record(ClientActionStats.Pick, 422);
        ClientActionStats.Record(ClientActionStats.Chat, ClientActionStats.NoConversation, "chat,customGame");
        ClientActionStats.Record(ClientActionStats.Ban, 403, "беседы чата");
        ClientActionStats.Record("teleport", 500);

        var got = ClientActionStats.Take();
        var pick = Of(got, ClientActionStats.Pick);
        Check(pick is { Ok: 2, Fail: 2, Code: 422 }, $"пик: 2 удачи, 2 неудачи, код последней ({Show(pick)})");
        var chat = Of(got, ClientActionStats.Chat);
        Check(chat is { Ok: 0, Fail: 1, Code: ClientActionStats.NoConversation, Detail: "chat,customGame" },
              $"чат: беседы нет, какие были ({Show(chat)})");
        var ban = Of(got, ClientActionStats.Ban);
        Check(ban is { Fail: 1, Code: 403, Detail: null }, $"пояснение не латиницей не уходит ({Show(ban)})");
        Check(got.All(e => ClientActionStats.Kinds.Contains(e.Kind)), "неизвестный вид не учитывается");
        Check(got.Select(e => e.Kind).SequenceEqual(
                  ClientActionStats.Kinds.Where(k => got.Any(e => e.Kind == k))),
              "порядок — как в списке видов");
        Check(ClientActionStats.Take().Count == 0, "Take обнуляет: второй раз пусто");
        Check(!ClientActionStats.Succeeded(ClientActionStats.NoAnswer)
              && !ClientActionStats.Succeeded(ClientActionStats.NoConversation), "0 и -2 — неудачи");
    }

    private static void ReportBody()
    {
        Console.WriteLine("тело отчёта:");
        var entries = new[]
        {
            new ClientActionStats.Entry(ClientActionStats.Pick, 1, 0, null, null),
            new ClientActionStats.Entry(ClientActionStats.Chat, 0, 2, -2, "chat"),
        };
        using var doc = JsonDocument.Parse(Telemetry.ActionsReportJson("abcdef12", entries));
        var r = doc.RootElement;
        Check(r.GetProperty("installId").GetString() == "abcdef12", "номер установки");
        var a = r.GetProperty("actions");
        Check(a.GetArrayLength() == 2, $"по строке на вид ({a.GetArrayLength()})");
        var c = a[1];
        Check(c.GetProperty("kind").GetString() == "chat" && c.GetProperty("ok").GetInt32() == 0
              && c.GetProperty("fail").GetInt32() == 2 && c.GetProperty("code").GetInt32() == -2
              && c.GetProperty("detail").GetString() == "chat",
              $"поля kind, ok, fail, code, detail ({c})");
        Check(a[0].GetProperty("code").ValueKind == JsonValueKind.Null, "без неудач код пустой");
    }

    // Имена полей и видов принимает сайт. Разошлись — сайт молча отбросит
    // отчёт. Папки сайта на машине может не быть — тогда пропуск.
    private static void SiteReadsSameFields()
    {
        Console.WriteLine("сайт принимает те же поля и виды:");
        var route = @"C:\Indexcounterplay\app\api\telemetry\actions\route.ts";
        if (!File.Exists(route)) { Console.WriteLine("  —    исходника сайта нет на этой машине — пропуск"); return; }
        var src = File.ReadAllText(route);
        Check(Regex.IsMatch(src, @"const\s*\{\s*installId\s*,\s*actions\s*\}\s*=\s*await\s+req\.json\(\)"),
              "тело — { installId, actions }");
        foreach (var f in new[] { "kind", "ok", "fail", "code", "detail" })
            Check(Regex.IsMatch(src, $@"a\?\.{f}\b"), $"поле действия «{f}» сайт читает");
        var set = Regex.Match(src, @"const KINDS = new Set\(\[([^\]]*)\]\)");
        Check(set.Success, "в маршруте найден список видов");
        if (!set.Success) return;
        var site = Regex.Matches(set.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();
        Check(site.SetEquals(ClientActionStats.Kinds),
              $"виды совпадают (сайт: {string.Join(",", site.Order())}; программа: {string.Join(",", ClientActionStats.Kinds.Order())})");
    }

    // Чат драфта на подставном клиенте: все исходы, которых мы ждём от Tencent.
    private static async Task ChatOutcomes()
    {
        Console.WriteLine("чат драфта:");
        using var fake = new FakeLcu("p", "n", []);
        using var http = new LcuHttpClient(fake.Credentials, TimeSpan.FromSeconds(5));
        const string list = "/lol-chat/v1/conversations";
        ClientActionStats.Take();

        fake.Fixed[list] = (200, """[{"id":"a1","type":"chat"},{"id":"b2","type":"customGame"},{"id":"a3","type":"chat"}]""");
        var code = await DraftChat.SendAsync(http, "привет", CancellationToken.None);
        var e = Of(ClientActionStats.Take(), ClientActionStats.Chat);
        Check(code == ClientActionStats.NoConversation, $"беседы драфта нет — свой код ({code})");
        Check(e is { Fail: 1, Code: ClientActionStats.NoConversation, Detail: "chat,customGame" },
              $"в отчёте — какие беседы были ({Show(e)})");

        fake.Fixed[list] = (404, "{}");
        await DraftChat.SendAsync(http, "привет", CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Chat);
        Check(e is { Fail: 1, Code: 404, Detail: "list" }, $"список бесед не отдался — его код, пометка list ({Show(e)})");

        fake.Fixed[list] = (200, """[{"id":"cs1","type":"championSelect"}]""");
        fake.Fixed["/lol-chat/v1/conversations/cs1/messages"] = (200, "{}");
        code = await DraftChat.SendAsync(http, "привет", CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Chat);
        Check(code == 200 && e is { Ok: 1, Fail: 0 }, $"отправлено — удача ({Show(e)})");
        Check(fake.Requests.Contains("POST /lol-chat/v1/conversations/cs1/messages"), "фраза ушла POST-ом в беседу драфта");

        fake.Fixed["/lol-chat/v1/conversations/cs1/messages"] = (500, "{}");
        await DraftChat.SendAsync(http, "привет", CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Chat);
        Check(e is { Fail: 1, Code: 500 }, $"клиент не принял — код отправки ({Show(e)})");
    }

    private static async Task SpellsAndItems()
    {
        Console.WriteLine("спеллы и набор предметов:");
        using var fake = new FakeLcu("p", "n", []);
        using var http = new LcuHttpClient(fake.Credentials, TimeSpan.FromSeconds(5));
        ClientActionStats.Take();

        const string sel = "/lol-champ-select/v1/session/my-selection";
        fake.Fixed[sel] = (204, "");
        var ok = await RunesImporter.ApplySpellsAsync(http, [4, 14], CancellationToken.None);
        var e = Of(ClientActionStats.Take(), ClientActionStats.Spells);
        Check(ok && e is { Ok: 1, Fail: 0 }, $"спеллы встали — удача ({Show(e)})");
        fake.Fixed[sel] = (500, "{}");
        await RunesImporter.ApplySpellsAsync(http, [4, 14], CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Spells);
        Check(e is { Fail: 1, Code: 500 }, $"спеллы не встали — код ({Show(e)})");
        await RunesImporter.ApplySpellsAsync(http, [4], CancellationToken.None);
        Check(Of(ClientActionStats.Take(), ClientActionStats.Spells) is null, "нечего ставить — не считается");

        fake.Fixed["/lol-summoner/v1/current-summoner"] = (404, "{}");
        await RunesImporter.ExportItemSetAsync(http, [], [], [], "BOTTOM", 22, "Эш", CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Items);
        Check(e is { Fail: 1, Code: 404, Detail: "summoner" }, $"набор: «кто я» не ответил — код и шаг ({Show(e)})");

        fake.Fixed["/lol-summoner/v1/current-summoner"] = (200, """{"summonerId":7}""");
        fake.Fixed["/lol-item-sets/v1/item-sets/7/sets"] = (200, """{"itemSets":[]}""");
        ok = await RunesImporter.ExportItemSetAsync(http, [], [], [], "BOTTOM", 22, "Эш", CancellationToken.None);
        e = Of(ClientActionStats.Take(), ClientActionStats.Items);
        Check(ok && e is { Ok: 1, Fail: 0 }, $"набор положен — удача ({Show(e)})");
        Check(fake.Requests.Contains("PUT /lol-item-sets/v1/item-sets/7/sets"), "набор ушёл PUT-ом");
    }

    // Клиент не отвечает (порт закрыт) — неудача с кодом 0, без исключения.
    private static async Task DeadClient()
    {
        Console.WriteLine("клиент не отвечает:");
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var http = new LcuHttpClient(new LcuCredentials("LeagueClient", 1, port, "x", "https"), TimeSpan.FromSeconds(2));
        ClientActionStats.Take();
        var code = await DraftChat.SendAsync(http, "привет", CancellationToken.None);
        var e = Of(ClientActionStats.Take(), ClientActionStats.Chat);
        Check(code == ClientActionStats.NoAnswer && e is { Fail: 1, Code: ClientActionStats.NoAnswer },
              $"чат: код 0 ({Show(e)})");
    }
}
