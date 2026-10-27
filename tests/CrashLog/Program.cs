using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Падение программы оставляет след в <c>log.txt</c>.
///
/// 27 октября клик по нику напарника ронял программу, а <c>log.txt</c>
/// обрывался на самом клике: исключение нашлось только в журнале событий
/// Windows. Теперь <c>Log.CatchCrashes</c> пишет его в журнал до закрытия.
///
/// Проверка запускает саму себя дочерним процессом, и тот падает по-настоящему
/// двумя путями: в обработчике окна (так было 27 октября) и в отдельном потоке.
/// Журнал ребёнка лежит во временной папке (<c>AppPaths.RootOverride</c>),
/// окон нет. Отдельно сверяется, что <c>Main</c> перехват включает.
/// </summary>
internal static class Program
{
    private const string Marker = "проверка-падения-7f3a";
    private static int _fails;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2) return Child(args[0], args[1]);

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Падение пишется в log.txt");

        foreach (var mode in new[] { "ui", "thread" })
        {
            var dir = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "CrashLog-" + mode);
            try { Directory.Delete(dir, true); } catch { }
            Directory.CreateDirectory(dir);

            var (code, stderr) = RunChild(mode, dir);
            var logPath = Path.Combine(dir, "log.txt");
            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";

            Check($"{mode}: ребёнок правда упал", code != 0, code == 0 ? First(stderr) : "");
            Check($"{mode}: строки до падения в журнале есть", log.Contains("до падения"),
                  log.Length == 0 ? "log.txt пуст или его нет" : "");
            var written = log.Contains("ПАДЕНИЕ") && log.Contains(Marker);
            Check($"{mode}: падение записано в журнал", written,
                  written ? "" : log.Length == 0 ? "log.txt пуст" : "последняя строка: " + Last(log));
            Check($"{mode}: в записи тип исключения и стек до места падения",
                  log.Contains("InvalidOperationException") && log.Contains(nameof(Boom)), "");

            try { Directory.Delete(dir, true); } catch { }
        }

        // Перехват работает, только если его включили. Program разложен на
        // части Program.*.cs — читаем их все.
        var root = FindRepoRoot();
        var main = root is null ? "" : string.Concat(
            Directory.GetFiles(Path.Combine(root, "src", "App"), "Program*.cs")
                     .Select(f => Code(File.ReadAllText(f))));
        Check("Main включает перехват падений", main.Contains("Log.CatchCrashes()"),
              root is null ? "не найден корень репозитория" : "");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: падение остаётся в журнале" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── ребёнок: включает перехват, пишет строку и падает ───────────────
    private static int Child(string mode, string dir)
    {
        AppPaths.RootOverride = dir;
        Log.CatchCrashes();
        Log.Write("до падения");

        if (mode == "ui")
        {
            // Как 27 октября: исключение в обработчике на потоке окна. Окна
            // нет — хватает очереди приложения.
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Dispatcher.BeginInvoke(new Action(Boom));
            app.Run();
        }
        else
        {
            var t = new Thread(Boom) { IsBackground = false };
            t.Start();
            t.Join();
        }
        return 0;   // сюда доходить не должно
    }

    private static void Boom() => throw new InvalidOperationException(Marker);

    private static (int Code, string Stderr) RunChild(string mode, string dir)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(dir);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(); } catch { }
            return (-999, "не закрылся за минуту");
        }
        return (p.ExitCode, stderr.Result);
    }

    private static string Last(string log) =>
        log.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";

    private static string First(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    /// Корень репозитория: от рабочей папки вверх до Counterplay.csproj.
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Counterplay.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// Код без комментариев: упоминание в пояснении — не вызов.
    private static string Code(string src)
    {
        var sb = new StringBuilder();
        foreach (var line in src.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//") || t.StartsWith("///")) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
