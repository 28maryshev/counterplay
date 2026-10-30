using System.Diagnostics;
using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Lockfile от прошлого запуска клиента. Компьютер выключился посреди игры,
/// клиент не успел стереть файл, и в нём остались старые порт и пароль. После
/// включения клиент запускается, но переписывает файл не сразу. Программа
/// успевала прочитать старый, полминуты стучалась на мёртвый порт и только
/// потом перечитывала файл, а окно всё это время сидело в трее.
///
/// Номер процесса в файле выдаёт старый: такого процесса уже нет или под этим
/// номером живёт чужая программа.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static readonly string Dir =
        Path.Combine(Path.GetTempPath(), "counterplay-test-root", "StaleLockfile");

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Directory.CreateDirectory(Dir);

        await DeadPidKeepsWaiting();
        await ForeignPidKeepsWaiting();
        await LiveLockfileAccepted();
        await RewrittenWhileWaiting();
        LiveClient();

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    private static string Lockfile(string name, string text)
    {
        var lf = Path.Combine(Dir, name);
        File.WriteAllText(lf, text);
        return lf;
    }

    // Номер процесса, которого уже нет: запустили и дождались выхода.
    private static int DeadPid()
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow  = true,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        return p.Id;
    }

    // Файл, который годится: номер и имя — нашего же процесса.
    private static string LiveText(int port) =>
        $"{Process.GetCurrentProcess().ProcessName}:{Environment.ProcessId}:{port}:pw:https";

    private static async Task<string> Wait(string lf, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        try
        {
            var c = await LockfileReader.WaitForAsync(lf, cts.Token);
            return $"порт {c.Port}";
        }
        catch (OperationCanceledException) { return "ждёт"; }
        catch (Exception ex) { return $"{ex.GetType().Name}: {ex.Message}"; }
    }

    // Процесса из файла нет — файл от прошлого запуска, по нему не подключаемся.
    private static async Task DeadPidKeepsWaiting()
    {
        Console.WriteLine("процесса из lockfile нет:");
        var lf = Lockfile("dead", $"LeagueClient:{DeadPid()}:1111:old:https");
        var outcome = await Wait(lf, TimeSpan.FromSeconds(2.5));
        Check(outcome == "ждёт", $"старый файл не принят, ожидание идёт ({outcome})");
    }

    // После перезагрузки номер достаётся другой программе: процесс есть, но не клиент.
    private static async Task ForeignPidKeepsWaiting()
    {
        Console.WriteLine("под номером из lockfile чужой процесс:");
        var lf = Lockfile("foreign", $"LeagueClient:{Environment.ProcessId}:1111:old:https");
        var outcome = await Wait(lf, TimeSpan.FromSeconds(2.5));
        Check(outcome == "ждёт", $"файл чужого процесса не принят ({outcome})");
    }

    private static async Task LiveLockfileAccepted()
    {
        Console.WriteLine("lockfile живого процесса:");
        var lf = Lockfile("live", LiveText(2222));
        var outcome = await Wait(lf, TimeSpan.FromSeconds(2.5));
        Check(outcome == "порт 2222", $"принят сразу ({outcome})");
    }

    // Как было у владельца: клиент запущен, на диске ещё старый файл, через
    // мгновение клиент пишет новый. Подключаться надо по новому и сразу.
    private static async Task RewrittenWhileWaiting()
    {
        Console.WriteLine("клиент переписал lockfile во время ожидания:");
        var lf = Lockfile("rewritten", $"LeagueClient:{DeadPid()}:1111:old:https");
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            File.WriteAllText(lf, LiveText(3333));
        });
        var sw = Stopwatch.StartNew();
        var outcome = await Wait(lf, TimeSpan.FromSeconds(5));
        Check(outcome == "порт 3333", $"взят новый файл, а не старый ({outcome}, {sw.Elapsed.TotalSeconds:0.0} с)");
    }

    // Настоящий lockfile запущенного клиента сверку проходит: номер в нём —
    // процесса LeagueClient. Иначе починка не пускала бы к живому клиенту.
    private static void LiveClient()
    {
        Console.WriteLine("запущенный клиент:");
        if (LcuFinder.FindLockfilePath() is not { } path) { Console.WriteLine("  клиент не запущен — пропуск"); return; }
        LcuCredentials c;
        try { c = LockfileReader.Read(path); }
        catch (Exception ex) { Console.WriteLine($"  lockfile не читается ({ex.GetType().Name}) — пропуск"); return; }
        Check(LockfileReader.IsLive(c), $"lockfile клиента принят ({c.Name}, процесс {c.Pid})");
    }
}
