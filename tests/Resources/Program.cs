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

    /// Код окон — весь `src\Ui`, со вложенными папками. Список файлов здесь
    /// раньше был перечислен руками и молча пропускал отсутствующие: после
    /// разбора окна драфта на части (`src\Ui\Overlay\OverlayWindow.*.cs`)
    /// проверка смотрела бы мимо него и оставалась зелёной.
    private static IEnumerable<string> Sources(string root) =>
        Directory.GetFiles(Path.Combine(root, "src", "Ui"), "*.cs", SearchOption.AllDirectories);

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
        var files = Sources(root).ToList();
        Check("код окна драфта среди прочитанного",
              files.Any(f => f.EndsWith("OverlayWindow.xaml.cs")) && files.Count(f => f.Contains("OverlayWindow.")) > 10,
              $"файлов {files.Count}, частей окна {files.Count(f => f.Contains("OverlayWindow."))}");
        foreach (var path in files)
        {
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

        Templates(w);
        w.Close();

        return Done();
    }

    /// <summary>
    /// Шаблоны окна вынесены в словари (OverlayResources.xaml, Templates/*.xaml).
    /// Ошибка в шаблоне — ключ ресурса, которого словарь не видит, обработчик,
    /// которого нет у словаря, — всплывает только когда шаблон показан, то есть
    /// у игрока на том экране, где он встречается. Поэтому каждый шаблон из
    /// ресурсов окна (со всеми подключёнными словарями) здесь разворачивается.
    /// </summary>
    private static void Templates(FrameworkElement w)
    {
        var all = new List<(string Where, object Key, DataTemplate T)>();
        void Walk(ResourceDictionary d, string where)
        {
            foreach (var key in d.Keys)
                if (d[key] is DataTemplate t) all.Add((where, key, t));
            foreach (var m in d.MergedDictionaries)
                Walk(m, m.Source?.OriginalString.Split('/').Last() ?? m.GetType().Name);
        }
        Walk(w.Resources, "окно");

        var broken = new List<string>();
        foreach (var (where, key, t) in all)
        {
            try { _ = t.LoadContent(); }
            catch (Exception ex) { broken.Add($"{where}:{key} — {ex.GetBaseException().Message}"); }
        }
        Console.WriteLine($"  (шаблонов в ресурсах окна: {all.Count}: "
            + string.Join(", ", all.Select(a => a.Key).Distinct()) + ")");
        Check("вынесенные шаблоны на месте",
              new[] { "PickCardTemplate", "TeamSlotTemplate", "BuildRowTemplate", "BanCardTemplate",
                      "BanSlot", "PlanCardTemplate", "RolePoolCellTemplate", "TierEmblem",
                      "RuneOptionTemplate" }
                  .All(k => all.Any(a => Equals(a.Key, k))), "");
        Check("каждый шаблон окна разворачивается", broken.Count == 0,
              broken.Count == 0 ? $"{all.Count}" : string.Join("; ", broken));
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
