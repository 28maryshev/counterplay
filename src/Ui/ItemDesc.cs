using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Path = System.Windows.Shapes.Path;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Geometry = System.Windows.Media.Geometry;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Stretch = System.Windows.Media.Stretch;

namespace Counterplay;

/// Описание предмета так, как его пишет клиент: характеристики со значками,
/// свойства с названиями, числа цветом.
///
/// Riot размечает описание тегами: число в &lt;attention&gt;, название свойства в
/// &lt;passive&gt;, магический урон в &lt;magicDamage&gt;. Раньше мы теги вырезали и
/// показывали серой простынёй — в ней не за что зацепиться взглядом.
///
/// Цвет берём из ТЕГОВ: их имена одинаковы во всех языках. А вот значок статa
/// по тегу не определить — «50 силы умений» это просто текст. Поэтому строки
/// характеристик сверяем с АНГЛИЙСКИМ описанием того же предмета: у Riot они
/// идут в одном порядке на всех языках, так что по английской строке понятно,
/// что за характеристика, а показываем строку на языке игрока. Словарь ключевых
/// слов на пятнадцать языков не нужен — и не рассыплется на новом патче.
public static class ItemDesc
{
    /// Кусок описания: либо текст со своим цветом, либо значок характеристики.
    public sealed record Part(string Text, Brush Brush, bool Bold,
                              Geometry? Icon = null, Brush? IconBrush = null);

    private static Brush B(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static Geometry G(string path)
    {
        var g = Geometry.Parse(path);
        g.Freeze();
        return g;
    }

    // ── Цвета текста ──────────────────────────────────────────────────────
    private static readonly Brush Plain    = B("#9FB3C8");   // обычный текст
    private static readonly Brush Value    = B("#F0D48A");   // числа и проценты
    private static readonly Brush Ability  = B("#E6EDF3");   // название свойства
    private static readonly Brush Magic    = B("#6AB0FF");
    private static readonly Brush Physical = B("#E8853F");
    private static readonly Brush True     = B("#EFE3C8");
    private static readonly Brush Heal     = B("#57C98A");
    private static readonly Brush Status   = B("#C79BE8");   // контроль и состояния

    private static Brush ColorOf(string tag) => tag switch
    {
        "attention" or "scalelevel" or "scalear" or "scalemr" or "scalemana"
            or "scalehealth" or "scaleap" or "scalead" or "ornnbonus" => Value,
        "passive" or "active" or "rules" or "keywordmajor" => Ability,
        "magicdamage" => Magic,
        "physicaldamage" => Physical,
        "truedamage" => True,
        "healing" or "lifesteal" or "shield" => Heal,
        "status" or "keywordstealth" => Status,
        _ => Plain,
    };

    private static bool BreaksBefore(string tag) => tag is "passive" or "active" or "rules";

    // ── Значки характеристик ──────────────────────────────────────────────
    //
    // Рисуем векторами в коробке 16×16: шрифтовых значков для этого нет, а
    // картинками пришлось бы тащить полтора десятка файлов в сборку.
    private static readonly Geometry Heart = G(
        "M8,14.2 C2,9.6 1.4,6.5 3,4.6 C4.4,3 6.9,3.3 8,5.1 C9.1,3.3 11.6,3 13,4.6 " +
        "C14.6,6.5 14,9.6 8,14.2 Z");
    private static readonly Geometry Shield = G(
        "M8,1.2 L14,3.6 V8.2 C14,11.5 11.4,13.7 8,15 C4.6,13.7 2,11.5 2,8.2 V3.6 Z");
    private static readonly Geometry Sword = G(
        "M12.4,1.4 L14.6,3.6 L7.2,11 L5,8.8 Z M4.4,9.4 L6.6,11.6 L4.2,14 L2,11.8 Z");
    private static readonly Geometry Spark = G(
        "M8,1 L9.7,6.3 L15,8 L9.7,9.7 L8,15 L6.3,9.7 L1,8 L6.3,6.3 Z");
    private static readonly Geometry Chevrons = G(
        "M2.6,2.4 L8.4,8 L2.6,13.6 Z M8,2.4 L13.8,8 L8,13.6 Z");
    private static readonly Geometry Star = G(
        "M8,1 L9.8,5.7 L14.8,5.9 L10.9,9 L12.2,13.9 L8,11.1 L3.8,13.9 L5.1,9 " +
        "L1.2,5.9 L6.2,5.7 Z");
    private static readonly Geometry Arrow = G(
        "M1,6.4 H8 V3.2 L14.6,8 L8,12.8 V9.6 H1 Z");
    private static readonly Geometry Hourglass = G(
        "M3.4,1.6 H12.6 L8,8 L12.6,14.4 H3.4 L8,8 Z");
    private static readonly Geometry Drop = G(
        "M8,1.4 C11.6,6.1 13,8.3 13,10.3 C13,13 10.8,14.8 8,14.8 " +
        "C5.2,14.8 3,13 3,10.3 C3,8.3 4.4,6.1 8,1.4 Z");
    private static readonly Geometry Diamond = G("M8,2.6 L13.4,8 L8,13.4 L2.6,8 Z");

    /// Что за характеристика — и каким значком её показать.
    private static readonly (string Key, Geometry Icon, string Color)[] Stats =
    [
        // Порядок важен: «Health Regen» должен находиться раньше «Health»,
        // иначе строка про восстановление достанется сердцу с цветом здоровья.
        ("ability haste",      Hourglass, "#6BD8C8"),
        ("heal and shield",    Shield,    "#57C98A"),
        ("health regen",       Heart,     "#7FD8A0"),
        ("mana regen",         Drop,      "#7FC4FF"),
        ("move speed",         Arrow,     "#36D6E7"),
        ("attack speed",       Chevrons,  "#F0C24B"),
        ("critical strike",    Star,      "#E8503F"),
        ("attack damage",      Sword,     "#E8853F"),
        ("ability power",      Spark,     "#A78BFA"),
        ("magic resist",       Shield,    "#8C7BE8"),
        ("armor penetration",  Sword,     "#C79BE8"),
        ("magic penetration",  Spark,     "#C79BE8"),
        ("lethality",          Sword,     "#C79BE8"),
        ("life steal",         Drop,      "#E8503F"),
        ("omnivamp",           Drop,      "#E8503F"),
        ("adaptive force",     Spark,     "#F0C24B"),
        ("tenacity",           Shield,    "#9FB3C8"),
        ("armor",              Shield,    "#C8A15A"),
        ("health",             Heart,     "#57C98A"),
        ("mana",               Drop,      "#4FA8FF"),
    ];

    private static (Geometry Icon, Brush Brush) IconFor(string englishLine)
    {
        var s = englishLine.ToLowerInvariant();
        foreach (var (key, icon, color) in Stats)
            if (s.Contains(key)) return (icon, B(color));
        return (Diamond, Plain);
    }

    // ── Разбор ────────────────────────────────────────────────────────────

    private static string StatsBlock(string raw)
    {
        var m = Regex.Match(raw ?? "", "<stats>(.*?)</stats>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    private static List<string> StatLines(string block)
    {
        var s = Regex.Replace(block, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        return s.Split('\n')
                .Select(x => Regex.Replace(x, @"\s+", " ").Trim())
                .Where(x => x.Length > 0)
                .ToList();
    }

    /// <param name="raw">Описание на языке игрока.</param>
    /// <param name="rawEn">Оно же по-английски — по нему опознаём характеристики.</param>
    public static IReadOnlyList<Part> Parse(string raw, string rawEn)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var parts = new List<Part>();

        // 1. Характеристики: значок + строка на языке игрока.
        var local = StatLines(StatsBlock(raw));
        var english = StatLines(StatsBlock(rawEn));
        for (var i = 0; i < local.Count; i++)
        {
            // Английская строка того же номера: порядок у Riot одинаковый.
            var (icon, brush) = i < english.Count ? IconFor(english[i]) : (Diamond, Plain);
            parts.Add(new Part("", Plain, false, icon, brush));
            parts.Add(new Part(" " + local[i] + (i < local.Count - 1 ? "\n" : ""), Plain, false));
        }

        // 2. Всё остальное — свойства, активные умения, правила.
        var rest = Regex.Replace(raw, "<stats>.*?</stats>", "",
                                 RegexOptions.Singleline | RegexOptions.IgnoreCase);
        rest = Regex.Replace(rest, "<flavorText>.*?</flavorText>", "",
                             RegexOptions.Singleline | RegexOptions.IgnoreCase);
        rest = Regex.Replace(rest, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        var text = new List<Part>();
        var stack = new Stack<string>();
        var written = new System.Text.StringBuilder();
        var headers = new Stack<bool>();   // был ли открытый тег ЗАГОЛОВКОМ
        var i2 = 0;

        void Add(string s, string? tag)
        {
            if (s.Length == 0) return;
            var decoded = System.Net.WebUtility.HtmlDecode(s);
            written.Append(decoded);
            text.Add(new Part(decoded,
                              tag is null ? Plain : ColorOf(tag),
                              tag is "passive" or "active" or "rules"));
        }

        // Тег в начале строки открывает НОВОЕ свойство. Тем же тегом Riot
        // помечает ссылку на свойство внутри предложения («…applies Squall to
        // them»), и перенос строки там разрывал фразу пополам.
        bool AtLineStart()
        {
            for (var k = written.Length - 1; k >= 0; k--)
            {
                var c = written[k];
                if (c == '\n') return true;
                if (!char.IsWhiteSpace(c)) return false;
            }
            return true;   // ещё ничего не написано
        }

        foreach (Match m in Regex.Matches(rest, "</?([a-zA-Z]+)[^>]*>"))
        {
            if (m.Index > i2) Add(rest[i2..m.Index], stack.Count > 0 ? stack.Peek() : null);
            i2 = m.Index + m.Length;

            var tag = m.Groups[1].Value.ToLowerInvariant();
            if (m.Value.StartsWith("</"))
            {
                if (stack.Count > 0) stack.Pop();
                // После заголовка — с новой строки. Но если у Riot там уже стоит
                // перенос, своего не добавляем: между названием свойства и его
                // описанием получалась пустая строка.
                if (headers.Count > 0 && headers.Pop()
                    && !(i2 < rest.Length && rest[i2] == '\n')) Add("\n", null);
            }
            else
            {
                var header = BreaksBefore(tag) && AtLineStart();
                // Пустая строка перед заголовком — но только если выше что-то есть.
                if (header && written.Length > 0) Add("\n", null);
                headers.Push(header);
                stack.Push(tag);
            }
        }
        if (i2 < rest.Length) Add(rest[i2..], stack.Count > 0 ? stack.Peek() : null);

        var tidy = Tidy(text);
        if (tidy.Count > 0)
        {
            if (parts.Count > 0) parts.Add(new Part("\n\n", Plain, false));
            parts.AddRange(tidy);
        }
        return parts;
    }

    /// Схлопывает лишние пробелы и пустые строки, не теряя цвета кусков.
    private static List<Part> Tidy(List<Part> parts)
    {
        var res = new List<Part>();
        foreach (var p in parts)
        {
            var t = Regex.Replace(p.Text, @"[ \t]+", " ");
            t = Regex.Replace(t, @" ?\n ?", "\n");
            t = Regex.Replace(t, @"\n{3,}", "\n\n");
            if (t.Length == 0) continue;
            res.Add(p with { Text = t });
        }
        while (res.Count > 0 && res[0].Text.Trim('\n', ' ').Length == 0) res.RemoveAt(0);
        if (res.Count > 0) res[0] = res[0] with { Text = res[0].Text.TrimStart('\n', ' ') };
        while (res.Count > 0 && res[^1].Text.Trim('\n', ' ').Length == 0) res.RemoveAt(res.Count - 1);
        if (res.Count > 0) res[^1] = res[^1] with { Text = res[^1].Text.TrimEnd('\n', ' ') };
        return res;
    }
}

/// Присоединяемое свойство: складывает куски описания в TextBlock.
///
/// Inlines у TextBlock не биндятся — это коллекция, а не свойство зависимости.
/// Обходим обычным способом: свойство принимает данные и собирает содержимое само.
public static class InlineText
{
    public static readonly DependencyProperty PartsProperty =
        DependencyProperty.RegisterAttached(
            "Parts", typeof(IEnumerable<ItemDesc.Part>), typeof(InlineText),
            new PropertyMetadata(null, OnPartsChanged));

    public static void SetParts(DependencyObject d, IEnumerable<ItemDesc.Part>? v) =>
        d.SetValue(PartsProperty, v);

    public static IEnumerable<ItemDesc.Part>? GetParts(DependencyObject d) =>
        (IEnumerable<ItemDesc.Part>?)d.GetValue(PartsProperty);

    private static void OnPartsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IEnumerable<ItemDesc.Part> parts) return;

        foreach (var p in parts)
        {
            if (p.Icon is not null)
            {
                // Значок живёт внутри строки текста, поэтому идёт контейнером с
                // выравниванием по центру: иначе он «висит» над строкой.
                var shape = new Path
                {
                    Data = p.Icon,
                    Fill = p.IconBrush ?? p.Brush,
                    Stretch = Stretch.Uniform,
                    Width = 11,
                    Height = 11,
                    Margin = new Thickness(0, 0, 1, 0),
                };
                tb.Inlines.Add(new InlineUIContainer(shape)
                {
                    BaselineAlignment = BaselineAlignment.Center,
                });
                continue;
            }

            // Run создаём заново на каждое присваивание: один и тот же объект
            // нельзя положить в два TextBlock.
            tb.Inlines.Add(new Run(p.Text)
            {
                Foreground = p.Brush,
                FontWeight = p.Bold ? FontWeights.Bold : FontWeights.Normal,
            });
        }
    }
}
