using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Отчёт о подключении: на каком сервере клиент и какая версия игры.
/// Без этого сервер игрока только угадывался по способу подключения: игроки
/// из Гонконга подключались так же, как китайские, а доказать, что они на
/// сервере Tencent, было нечем (журнал 2026-11-25).
/// </summary>
internal static class Program
{
    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        ParsesClientAnswers();
        CleansValues();
        ReportBody();
        SiteReadsSameFields();
        await DeadClientGivesEmpty();
        await LiveClient();

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    // Ответы клиента в том виде, в каком он их отдаёт.
    private static void ParsesClientAnswers()
    {
        Console.WriteLine("разбор ответов клиента:");
        Check(ClientServerInfo.ParseField(
                  """{"locale":"en_GB","region":"EUW","webLanguage":"en","webRegion":"euw"}""", "region") == "EUW",
              "регион из region-locale");
        Check(ClientServerInfo.ParseField("""{"locale":"zh_CN","region":"TENCENT"}""", "region") == "TENCENT",
              "регион китайского клиента");
        Check(ClientServerInfo.ParseField("""{"locale":"en_US"}""", "region") == null, "без поля region — пусто");
        Check(ClientServerInfo.ParseField("<html>", "region") == null, "не JSON — пусто, без исключения");
        Check(ClientServerInfo.ParseField("\"RU\"", "region") == null, "строка вместо объекта — пусто");

        // Ответы живого клиента 26.11.2026 (ник и номера игрока заменены).
        Check(ClientServerInfo.ParseField(
                  """{"gameName":"x","gameTag":"y","platformId":"EUW1","puuid":"z","summonerId":1}""",
                  "platformId") == "EUW1",
              "платформа из /lol-chat/v1/me");
        Check(ClientServerInfo.ParseField(
                  """{"environment":"euw1","userId":"1","userName":""}""", "environment") == "euw1",
              "окружение клиента Riot — запасной путь платформы");
        Check(ClientServerInfo.ParseField(
                  """{"errorCode":"RPC_ERROR","httpStatus":404,"message":"Could not find the namespace \\"LoginDataPacket\\""}""",
                  "platformId") == null,
              "404 клиента — не платформа");
        var gv = ClientServerInfo.Version(ClientServerInfo.ParseString(
            "\"16.23.8418428+branch.releases-16-23.code.public.content.release.anticheat.vanguard\""));
        Check(gv == "16.23.8418428", $"версия живого клиента — номер без ветки сборки ({gv ?? "пусто"})");
        Check(ClientServerInfo.ParseString(
                  """{"errorCode":"RPC_ERROR","httpStatus":404,"message":"x"}""") == null,
              "ошибка клиента (объект) — не значение");
        Check(ClientServerInfo.ParseString("") == null && ClientServerInfo.ParseString(null) == null,
              "пустой ответ — пусто");
        Check(ClientServerInfo.ParseString("42") == null, "число — не строка");
    }

    // В отчёт уходит только то, что похоже на код сервера и номер версии.
    private static void CleansValues()
    {
        Console.WriteLine("чистка значений:");
        Check(ClientServerInfo.Code("HN1") == "HN1" && ClientServerInfo.Code("TENCENT") == "TENCENT",
              "коды серверов проходят");
        Check(ClientServerInfo.Code("NA_1") == "NA_1", "подчёркивание можно");
        Check(ClientServerInfo.Code("EUW 1") == null, "пробел — не код");
        Check(ClientServerInfo.Code("艾欧尼亚") == null && ClientServerInfo.Code("ЕВРОПА") == null,
              "не латиница — не код");
        Check(ClientServerInfo.Code(new string('A', 17)) == null, "длиннее 16 — не код");
        Check(ClientServerInfo.Code("") == null, "пустая строка — не код");

        Check(ClientServerInfo.Version("16.23.725.1234") == "16.23.725.1234", "номер версии проходит");
        Check(ClientServerInfo.Version("16.23.1+x") == "16.23.1", "хвост после + отрезается");
        Check(ClientServerInfo.Version("+branch") == null, "без номера перед + — не версия");
        Check(ClientServerInfo.Version("Releases/16.23") == null, "лишние знаки — не версия");
        Check(ClientServerInfo.Version(".23") == null, "с точки — не версия");
        Check(ClientServerInfo.Version(new string('1', 33)) == null, "длиннее 32 — не версия");
    }

    // Тело отчёта: поля сервера есть, а при неудаче (сервера не узнать) — пустые.
    private static void ReportBody()
    {
        Console.WriteLine("тело отчёта:");
        var info = new ClientServerInfo("TENCENT", "HN1", "16.23.725.1234");
        using var ok = JsonDocument.Parse(Telemetry.ClientReportJson("abcdef12", true, "cmdline", true, null, info));
        var r = ok.RootElement;
        Check(Str(r, "region") == "TENCENT", $"region в отчёте ({Str(r, "region") ?? "нет"})");
        Check(Str(r, "platform") == "HN1", $"platform в отчёте ({Str(r, "platform") ?? "нет"})");
        Check(Str(r, "game") == "16.23.725.1234", $"game в отчёте ({Str(r, "game") ?? "нет"})");
        Check(Str(r, "via") == "cmdline" && r.GetProperty("ok").GetBoolean(), "прежние поля на месте");

        using var fail = JsonDocument.Parse(Telemetry.ClientReportJson("abcdef12", false, null, null, null, null));
        var f = fail.RootElement;
        Check(f.TryGetProperty("region", out var fr) && fr.ValueKind == JsonValueKind.Null,
              "отчёт о неудаче: region пустой");
        Check(info.ToString() == "сервер TENCENT/HN1, игра 16.23.725.1234", $"строка для журнала ({info})");
    }

    // Имена полей читает сайт. Разошлись — сервер и версия молча перестанут
    // доезжать. Папки сайта на машине может не быть — тогда пропуск.
    private static void SiteReadsSameFields()
    {
        Console.WriteLine("сайт читает те же поля:");
        var route = @"C:\Indexcounterplay\app\api\telemetry\client\route.ts";
        if (!File.Exists(route)) { Console.WriteLine("  —    исходника сайта нет на этой машине — пропуск"); return; }
        var m = Regex.Match(File.ReadAllText(route), @"const\s*\{([^}]*)\}\s*=\s*await\s+req\.json\(\)");
        Check(m.Success, "в маршруте найден разбор тела");
        if (!m.Success) return;
        var fields = m.Groups[1].Value.Split(',').Select(s => s.Trim()).ToHashSet();
        using var body = JsonDocument.Parse(Telemetry.ClientReportJson("abcdef12", true, null, null, null, null));
        foreach (var p in body.RootElement.EnumerateObject())
            Check(fields.Contains(p.Name), $"поле «{p.Name}» сайт читает");
    }

    // Клиент не отвечает (порт закрыт) — пустые поля, а не исключение: отчёт
    // о подключении должен уйти и без сервера.
    private static async Task DeadClientGivesEmpty()
    {
        Console.WriteLine("клиент не отвечает:");
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        var creds = new LcuCredentials("LeagueClient", 1, port, "x", "https");
        using var http = new LcuHttpClient(creds, TimeSpan.FromSeconds(2));
        ClientServerInfo? info = null;
        string? error = null;
        try { info = await ClientServerInfo.ReadAsync(http, CancellationToken.None); }
        catch (Exception ex) { error = ex.GetType().Name; }
        Check(error == null, $"без исключения ({error ?? "нет"})");
        Check(info == ClientServerInfo.None, $"все поля пустые ({info})");
    }

    // Запущенный клиент: все три поля читаются, версия — с номера патча.
    private static async Task LiveClient()
    {
        Console.WriteLine("запущенный клиент:");
        if (!LcuFinder.IsClientRunning()) { Console.WriteLine("  клиент не запущен — пропуск"); return; }
        LcuCredentials? creds = null;
        if (LcuFinder.FindLockfilePath() is { } path)
            try { creds = LockfileReader.Read(path); } catch { }
        creds ??= LcuCommandLine.TryRead();
        Check(creds != null, "учётные данные клиента нашлись");
        if (creds == null) return;

        using var http = new LcuHttpClient(creds, TimeSpan.FromSeconds(5));
        var info = await ClientServerInfo.ReadAsync(http, CancellationToken.None);
        Console.WriteLine($"  {info}");
        Check(info.Region != null, "регион читается");
        Check(info.Platform != null, "платформа читается (клиент вошёл в аккаунт)");
        Check(info.Game != null && Regex.IsMatch(info.Game, @"^\d+\.\d+"), "версия игры — с номера патча");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
