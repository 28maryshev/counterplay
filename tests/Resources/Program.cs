using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Ресурсы, которые код спрашивает по имени.
///
/// `FindResource("HintTip")` не проверяется ничем: компилятор видит строку,
/// разметка живёт отдельно, а падает оно уже у игрока. Так и вышло в 1.3.48 —
/// стиль подсказок переехал в общий словарь и потерял ключ, три его
/// использования в разметке я заменил, а ЧЕТВЁРТОЕ сидело в коде. Программа
/// падала целиком при каждом показе сессии: после игры, в начале драфта, на
/// банах. Заодно пропадал график ранга — он рисуется там же.
///
/// Причина, по которой четвёртое не нашлось: ripgrep считает
/// `OverlayWindow.xaml.cs` бинарным (в нём есть служебные символы) и вместо
/// строк печатает «Binary file matches». Поиск по коду прошёл мимо.
///
/// Поэтому проверка читает файл САМА и спрашивает каждый ключ у настоящего
/// окна — с теми же ресурсами приложения, что подмешивает Program.cs.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Разметка окна и код-behind рядом с ней.
    private static readonly string[] Sources =
    [
        @"src\Ui\OverlayWindow.xaml.cs",
        @"src\Ui\PoolSettings.cs",
        @"src\Ui\SettingsWindow.cs",
        @"src\Ui\ConfirmWindow.cs",
    ];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var root = FindRepoRoot();
        if (root is null) { Console.WriteLine("корень репозитория не найден"); return 0; }

        var app = new System.Windows.Application();

        // Путь к словарю берём ИЗ Program.cs, а не повторяем строкой: иначе
        // проверка подтверждала бы свою копию, а не то, чем пользуется сама
        // программа. Ошибка в пути видна только на запуске у игрока.
        var themeUri = Regex.Match(
            File.ReadAllText(Path.Combine(root, "src", "App", "Program.cs"), Encoding.UTF8),
            @"Source\s*=\s*new Uri\(""([^""]+Theme\.xaml)""").Groups[1].Value;
        Check("путь к словарю найден в Program.cs", themeUri.Length > 0, themeUri);
        if (themeUri.Length == 0) return Done();

        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(themeUri, UriKind.Relative),
            });
            Check("общий словарь оформления загружается", true, themeUri);
        }
        catch (Exception ex)
        {
            Check("общий словарь оформления загружается", false, ex.Message);
            return Done();
        }

        var w = new OverlayWindow();

        // Ключи, которые код спрашивает по имени, вместе с тем, У КОГО он их
        // спрашивает: часть ресурсов объявлена не в окне, а внутри элемента
        // (шаблон варианта рун живёт в ресурсах полосы рун), и у окна их нет.
        var asked = new SortedSet<(string Owner, string Key)>();
        foreach (var rel in Sources)
        {
            var path = Path.Combine(root, rel);
            if (!File.Exists(path)) continue;
            // Комментарии выбрасываем: упоминание ключа в пояснении — не вызов.
            var text = Regex.Replace(File.ReadAllText(path, Encoding.UTF8), @"//[^\n]*", "");
            foreach (Match m in Regex.Matches(text, @"(?:(\w+)\.)?(?:Try)?FindResource\(""([^""]+)""\)"))
                asked.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        Console.WriteLine("  (спрашивают: "
            + string.Join(", ", asked.Select(a => a.Owner.Length > 0 ? $"{a.Owner}.{a.Key}" : a.Key)) + ")");
        Check("вызовы вообще нашлись", asked.Count > 0, $"{asked.Count}");

        var missing = asked.Where(a => Resolve(w, a.Owner, a.Key) is null)
                           .Select(a => a.Owner.Length > 0 ? $"{a.Owner}.{a.Key}" : a.Key).ToList();
        Check("каждый ключ из кода есть в ресурсах", missing.Count == 0,
              missing.Count == 0 ? "да" : "нет: " + string.Join(", ", missing));

        // Фирменный вид подсказок: стиль без ключа должен подхватываться сам.
        var tipStyle = app.TryFindResource(typeof(System.Windows.Controls.ToolTip)) as Style;
        Check("подсказки оформлены приложением", tipStyle is not null,
              tipStyle is null ? "стиля нет" : $"{tipStyle.Setters.Count} свойств");

        w.Close();

        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: всё, что код спрашивает, разметка отдаёт"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Ищет ресурс там же, где его ищет код: у названного элемента, если вызов
    /// был на нём, иначе у самого окна. Элемента с таким именем в разметке нет
    /// (вызов на локальной переменной) — спрашиваем окно, это ближайшее, что
    /// можно проверить.
    /// </summary>
    private static object? Resolve(FrameworkElement w, string owner, string key)
    {
        if (owner.Length > 0 && w.FindName(owner) is FrameworkElement el)
            return el.TryFindResource(key);
        return w.TryFindResource(key);
    }

    /// Корень репозитория: от рабочей папки вверх до Counterplay.csproj.
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Counterplay.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
