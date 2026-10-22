using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Пустой lockfile. Китайский клиент (WeGame) оставляет его пустым всегда,
/// обычный — на долю секунды при старте. Раньше чтение бросало FormatException,
/// оно выходило из цикла ожидания клиента, и программа показывала ошибку и
/// больше не подключалась.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        await EmptyLockfileKeepsWaiting();
        ParsesClientArgs();
        await LiveClient();

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    // Пустой файл по явному пути: ожидание должно продолжаться до отмены, а не падать.
    private static async Task EmptyLockfileKeepsWaiting()
    {
        Console.WriteLine("пустой lockfile:");
        var dir = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "LcuCmdLine");
        Directory.CreateDirectory(dir);
        var lf = Path.Combine(dir, "lockfile");
        File.WriteAllText(lf, "");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        string outcome;
        try
        {
            await LockfileReader.WaitForAsync(lf, cts.Token);
            outcome = "вернул учётные данные из пустого файла";
        }
        catch (OperationCanceledException) { outcome = "ждёт"; }
        catch (Exception ex) { outcome = $"{ex.GetType().Name}: {ex.Message}"; }
        Check(outcome == "ждёт", $"ожидание продолжается, а не падает ({outcome})");
    }

    // Командная строка в том виде, в каком её получает LeagueClientUx. Порт клиента
    // Riot (`--riotclient-app-port`) идёт раньше нужного и не должен с ним спутаться.
    private static void ParsesClientArgs()
    {
        Console.WriteLine("разбор командной строки:");
        const string tencent =
            "\"D:\\WeGameApps\\英雄联盟\\LeagueClient\\LeagueClientUx.exe\" " +
            "\"--riotclient-auth-token=AAAA\" \"--riotclient-app-port=50001\" " +
            "\"--no-rads\" \"--region=TENCENT\" \"--locale=zh_CN\" " +
            "\"--remoting-auth-token=Tok-En_123\" \"--respawn-command=LeagueClient.exe\" " +
            "\"--app-port=60123\" \"--install-directory=D:\\WeGameApps\\英雄联盟\\LeagueClient\" " +
            "\"--rso_platform_id=HN1\"";
        var c = LcuCommandLine.Parse(tencent, 42);
        Check(c?.Port == 60123, $"порт из --app-port, не из --riotclient-app-port ({c?.Port})");
        Check(c?.Password == "Tok-En_123", $"пароль из --remoting-auth-token ({c?.Password})");
        Check(c?.HttpBase == "https://127.0.0.1:60123", $"адрес клиента ({c?.HttpBase})");

        var bare = LcuCommandLine.Parse("LeagueClientUx.exe --remoting-auth-token=xyz --app-port=7000", 1);
        Check(bare?.Port == 7000 && bare.Password == "xyz", "аргументы без кавычек");

        Check(LcuCommandLine.Parse("\"--riotclient-app-port=50001\" \"--remoting-auth-token=x\"", 1) == null,
              "без --app-port — нет данных, чужой порт не подставляется");
        Check(LcuCommandLine.Parse("\"--app-port=60123\"", 1) == null, "без пароля — нет данных");
    }

    // Запущенный клиент: порт из командной строки тот же, что в lockfile, и клиент
    // пускает с её паролем и по HTTP, и по WebSocket. Сам пароль с lockfile НЕ
    // совпадает: клиент принимает оба (проверено 23.10.2026). Пароль на экран не выводим.
    private static async Task LiveClient()
    {
        Console.WriteLine("запущенный клиент:");
        if (!LcuFinder.IsClientRunning()) { Console.WriteLine("  клиент не запущен — пропуск"); return; }

        var fromCmd = LcuCommandLine.TryRead();
        Check(fromCmd != null, "командная строка клиента читается");
        if (fromCmd == null) return;

        if (LcuFinder.FindLockfilePath() is { } path)
        {
            try
            {
                var fromFile = LockfileReader.Read(path);
                Check(fromCmd.Port == fromFile.Port, $"порт совпадает с lockfile ({fromCmd.Port})");
            }
            catch (Exception ex) { Console.WriteLine($"  lockfile не читается ({ex.GetType().Name})"); }
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = new LcuHttpClient(fromCmd);
        var (status, _) = await http.GetAsync("/lol-gameflow/v1/availability", cts.Token);
        Check(status == 200, $"HTTP с паролем из командной строки (код {status})");

        string ws;
        try
        {
            await using var sock = new LcuEventSocket(fromCmd);
            await sock.ConnectAsync(cts.Token);
            ws = "подключён";
        }
        catch (Exception ex) { ws = $"{ex.GetType().Name}: {ex.Message}"; }
        Check(ws == "подключён", $"WebSocket с паролем из командной строки ({ws})");
    }
}
