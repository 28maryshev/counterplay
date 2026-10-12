using System.Diagnostics;
using System.Text;

namespace Counterplay.Tests;

/// <summary>
/// Дымовая проверка собранной программы.
///
/// 1.3.48 ушла к игрокам с падением при показе профиля: проверки гоняли код,
/// а собранную программу перед выпуском не запускал никто. Здесь программа
/// собирается так же, как в <c>build/release.ps1</c> (самодостаточная, win-x64),
/// во временную папку и запускается с ключом <c>--smoke</c>: скрытым окном она
/// проходит путь игрока (профиль, объявление, баны, пики, наведение, руны,
/// окна пулов и настроек) и выходит с кодом 0, если ничего не упало.
///
/// Что и как проверяет сам режим — <c>src/App/Smoke.cs</c>. Выпуск зовёт его
/// на том exe, что уедет игрокам.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var repo = FindRepo();
        var outDir = Path.Combine(Path.GetTempPath(), "counterplay-smoke-build");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);

        Console.WriteLine("сборка как для выпуска…");
        var publish = Run("dotnet",
            $"publish \"{Path.Combine(repo, "Counterplay.csproj")}\" -c Release -r win-x64 " +
            $"--self-contained true -o \"{outDir}\" -nologo -v q", repo);
        if (publish != 0) { Console.WriteLine("ИТОГ: программа не собралась"); return 1; }

        var report = Path.Combine(Path.GetTempPath(), "counterplay-smoke-test.txt");
        if (File.Exists(report)) File.Delete(report);
        Console.WriteLine("запуск с --smoke…");
        var sw = Stopwatch.StartNew();
        var code = Run(Path.Combine(outDir, "Counterplay.exe"), $"--smoke \"{report}\"", outDir);
        Console.WriteLine($"код выхода {code}, {sw.Elapsed.TotalSeconds:0} с");
        Console.WriteLine();

        if (!File.Exists(report)) { Console.WriteLine("ИТОГ: программа не написала итог — упала до сценария"); return 1; }
        foreach (var line in File.ReadAllLines(report)) Console.WriteLine("  " + line);
        return code == 0 ? 0 : 1;
    }

    private static int Run(string exe, string args, string dir)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
        })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    /// Корень репозитория — там, где лежит Counterplay.csproj.
    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) return d.FullName;
        throw new InvalidOperationException("не нашёл Counterplay.csproj выше " + AppContext.BaseDirectory);
    }
}
