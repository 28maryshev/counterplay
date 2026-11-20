using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// RunesUk — справочник рун по-украински.
///
/// Владелец (20.11): «делай переводы в рунах на остальные языки». Строки
/// интерфейса рун переведены на все 15 языков, а названия и описания самих рун
/// приходят от Riot на языке интерфейса — кроме украинского: локали у Data
/// Dragon нет (Loc: uk → en_US), и игрок видел руны по-английски. Перевод вшит
/// в программу (assets/runes/uk.json, RuneIcons.Translation.cs).
///
///   1. переведены все руны и деревья нынешнего справочника (новая руна от
///      Riot — проверка назовёт её);
///   2. цифры в каждом описании — те же, что в английском, с которого
///      переводили;
///   3. перевод не устарел: английский, с которого переводили, совпадает с
///      нынешним (Riot поменял описание — перевести заново);
///   4. в украинском интерфейсе программа показывает перевод;
///   5. описание, которое Riot поменял после перевода, — английским, а не
///      устаревшим переводом; в английском интерфейсе перевода нет.
/// Настройки — в своей папке, справочники — кэш игрока копией или из сети.
internal static class Program
{
    private static readonly List<string> Failures = [];

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "RunesUk");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        try
        {
            CopyCaches();
            Loc.SetLanguage("uk");
            DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
            Run();
        }
        finally
        {
            AppPaths.RootOverride = null;
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine(Failures.Count == 0 ? "ИТОГ: руны по-украински" : $"ИТОГ: провалено — {Failures.Count}");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Run()
    {
        // Английский справочник — то, с чего переводили и что видит украинский
        // игрок без перевода.
        Loc.SetLanguage("en");
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        var ids = Enumerable.Range(8000, 2000).Where(RuneIcons.Known).ToList();
        var english = ids.ToDictionary(id => id, id => (Name: RuneIcons.NameOf(id), Desc: RuneIcons.DescOf(id)));
        Check("английский справочник загружен", ids.Count > 50 && RuneIcons.NameOf(8473) == "Bone Plating",
              $"рун {ids.Count}, 8473 — {RuneIcons.NameOf(8473)}");

        using var file = JsonDocument.Parse(Embedded("uk"));
        var tr = file.RootElement.GetProperty("runes");
        var trees = file.RootElement.GetProperty("trees");

        // 1. Всё переведено.
        var missing = ids.Where(id => !tr.TryGetProperty(id.ToString(), out _)).Select(id => $"{id} {english[id].Name}").ToList();
        var styles = new[] { 8000, 8100, 8200, 8300, 8400 };
        var noTree = styles.Where(t => !trees.TryGetProperty(t.ToString(), out _)).ToList();
        Check("переведены все руны и деревья справочника", missing.Count == 0 && noTree.Count == 0,
              missing.Count == 0 && noTree.Count == 0 ? $"{ids.Count} рун, 5 деревьев"
                  : $"нет перевода: {string.Join(", ", missing)} {string.Join(", ", noTree)}");

        // 2. Цифры те же.
        static List<string> Nums(string s) => [.. Regex.Matches(s, @"\d+(?:[.,]\d+)?").Select(m => m.Value).Order()];
        var badNums = tr.EnumerateObject()
            .Where(r => !Nums(r.Value.GetProperty("desc").GetString()!).SequenceEqual(Nums(r.Value.GetProperty("src").GetString()!)))
            .Select(r => r.Name).ToList();
        Check("цифры в переводе — те же, что в английском", badNums.Count == 0,
              badNums.Count == 0 ? "все" : "расходятся: " + string.Join(", ", badNums));

        // 3. Перевод не устарел.
        var stale = ids.Where(id => tr.TryGetProperty(id.ToString(), out var r) && r.GetProperty("src").GetString() != english[id].Desc)
                       .Select(id => $"{id} {english[id].Name}").ToList();
        Check("перевод не устарел: Riot описаний не менял", stale.Count == 0,
              stale.Count == 0 ? "все совпадают" : "перевести заново (Riot поменял описание): " + string.Join(", ", stale));

        // 4. Украинский интерфейс — перевод.
        Loc.SetLanguage("uk");
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        var bp = tr.GetProperty("8473");
        Check("в украинском интерфейсе — перевод: название, описание, дерево",
              RuneIcons.NameOf(8473) == bp.GetProperty("name").GetString()
              && RuneIcons.DescOf(8473) == bp.GetProperty("desc").GetString()
              && RuneIcons.StyleName(8400) == trees.GetProperty("8400").GetString()
              && RuneIcons.StaleTranslations == stale.Count,
              $"{RuneIcons.NameOf(8473)} · {RuneIcons.StyleName(8400)} · {RuneIcons.DescOf(8473)[..Math.Min(60, RuneIcons.DescOf(8473).Length)]}…");

        // 5. Описание, которое Riot поменял после перевода, — английским.
        Loc.SetLanguage("en");
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        Check("в английском интерфейсе перевода нет", RuneIcons.NameOf(8473) == "Bone Plating"
              && RuneIcons.DescOf(8473) == english[8473].Desc, RuneIcons.NameOf(8473));
        using var old = JsonDocument.Parse("""
            {"runes": {"8473": {"name": "Кістяна пластина", "desc": "застарілий переклад: 10-20", "src": "старий англійський текст"}}}
            """);
        typeof(RuneIcons).GetMethod("ApplyTranslation", BindingFlags.NonPublic | BindingFlags.Static, [typeof(JsonElement)])!
            .Invoke(null, [old.RootElement]);
        Check("Riot поменял описание после перевода — название переведено, описание свежее английское",
              RuneIcons.NameOf(8473) == "Кістяна пластина" && RuneIcons.DescOf(8473) == english[8473].Desc
              && RuneIcons.StaleTranslations == 1,
              $"{RuneIcons.NameOf(8473)} · {RuneIcons.DescOf(8473)[..Math.Min(50, RuneIcons.DescOf(8473).Length)]}… · устарело {RuneIcons.StaleTranslations}");
    }

    /// Перевод, вшитый в программу: заодно проверяем, что он туда попал.
    private static string Embedded(string lang)
    {
        var asm = typeof(RuneIcons).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith($".runes.{lang}.json", StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// Справочники игрока — копией в свою папку (сеть не нужна, если они есть).
    private static void CopyCaches()
    {
        var playerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        foreach (var sub in new[] { "ddragon" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) Failures.Add(what);
    }
}
