using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using FontWeights = System.Windows.FontWeights;

namespace Counterplay;

/// Описание предмета цветом — как в самом клиенте.
///
/// Riot размечает описание тегами: число в &lt;attention&gt;, название свойства в
/// &lt;passive&gt;, магический урон в &lt;magicDamage&gt; и так далее. Раньше мы теги
/// вырезали и показывали серой простынёй — в ней не за что зацепиться взглядом.
/// Здесь разметка превращается в куски текста со своим цветом.
///
/// Опираемся на ТЕГИ, а не на слова: значения тегов одинаковы во всех языках, и
/// раскраска по ключевым словам («Attack Damage», «Сила атаки», «공격력») потребовала
/// бы словаря на пятнадцать языков и всё равно рассыпалась бы на новом патче.
public static class ItemDesc
{
    /// Кусок описания: текст, цвет и жирность.
    public sealed record Part(string Text, Brush Brush, bool Bold);

    private static Brush B(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    // Палитра под цвета клиента, но в наших тонах: числа золотом, названия
    // свойств светлые, урон по типам — своим цветом.
    private static readonly Brush Plain   = B("#9FB3C8");   // обычный текст
    private static readonly Brush Value   = B("#F0D48A");   // числа и проценты
    private static readonly Brush Ability = B("#E6EDF3");   // название свойства
    private static readonly Brush Magic   = B("#6AB0FF");
    private static readonly Brush Physical = B("#E8853F");
    private static readonly Brush True    = B("#EFE3C8");
    private static readonly Brush Heal    = B("#57C98A");
    private static readonly Brush Status  = B("#C79BE8");   // контроль и состояния

    private static Brush ColorOf(string tag) => tag.ToLowerInvariant() switch
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

    // Теги, после которых текст идёт с новой строки: так характеристики и
    // свойства не слипаются в одно предложение.
    private static bool BreaksBefore(string tag) =>
        tag is "passive" or "active" or "rules";

    /// Разбирает разметку Riot в куски с цветом. Лор («flavorText») выбрасываем:
    /// в подсказке он занимает место и ничего не объясняет.
    public static IReadOnlyList<Part> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];

        var s = Regex.Replace(raw, "<flavorText>.*?</flavorText>", "",
                              RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);

        var parts = new List<Part>();
        var stack = new Stack<string>();
        var i = 0;

        void Add(string text, string? tag)
        {
            if (text.Length == 0) return;
            var brush = tag is null ? Plain : ColorOf(tag);
            var bold = tag is "passive" or "active" or "rules";
            parts.Add(new Part(System.Net.WebUtility.HtmlDecode(text), brush, bold));
        }

        foreach (Match m in Regex.Matches(s, "</?([a-zA-Z]+)[^>]*>"))
        {
            if (m.Index > i) Add(s[i..m.Index], stack.Count > 0 ? stack.Peek() : null);
            i = m.Index + m.Length;

            var tag = m.Groups[1].Value.ToLowerInvariant();
            if (m.Value.StartsWith("</"))
            {
                if (stack.Count > 0) stack.Pop();
                // Свойство закончилось — дальше текст с новой строки.
                if (BreaksBefore(tag)) Add("\n", null);
            }
            else
            {
                if (BreaksBefore(tag)) Add("\n\n", null);
                stack.Push(tag);
            }
        }
        if (i < s.Length) Add(s[i..], stack.Count > 0 ? stack.Peek() : null);

        // Чистка пробелов и пустых строк, накопившихся от разметки.
        var text = string.Concat(parts.Select(p => p.Text));
        if (string.IsNullOrWhiteSpace(text)) return [];

        return Tidy(parts);
    }

    /// Схлопывает лишние пробелы и пустые строки, не теряя цвета кусков.
    private static List<Part> Tidy(List<Part> parts)
    {
        var res = new List<Part>();
        foreach (var p in parts)
        {
            var t = Regex.Replace(p.Text, @"[ \t]+", " ");
            t = Regex.Replace(t, @" ?\n ?", "\n");
            if (t.Length == 0) continue;

            // Три и больше переводов строки подряд — это пустота от разметки.
            if (res.Count > 0)
            {
                var prev = res[^1];
                var joined = prev.Text + t;
                var fixedJoin = Regex.Replace(joined, @"\n{3,}", "\n\n");
                if (fixedJoin != joined)
                {
                    res[^1] = prev with { Text = fixedJoin[..^t.TrimStart('\n').Length] };
                    t = t.TrimStart('\n');
                    if (t.Length == 0) continue;
                }
            }
            res.Add(p with { Text = t });
        }

        // Ведущие и хвостовые переводы строки не нужны.
        while (res.Count > 0 && res[0].Text.TrimStart('\n', ' ').Length == 0) res.RemoveAt(0);
        if (res.Count > 0) res[0] = res[0] with { Text = res[0].Text.TrimStart('\n', ' ') };
        while (res.Count > 0 && res[^1].Text.TrimEnd('\n', ' ').Length == 0) res.RemoveAt(res.Count - 1);
        if (res.Count > 0) res[^1] = res[^1] with { Text = res[^1].Text.TrimEnd('\n', ' ') };
        return res;
    }
}

/// Присоединяемое свойство: складывает куски описания в TextBlock.
///
/// Inlines у TextBlock не биндятся — это коллекция, а не свойство зависимости.
/// Обходим тем же способом, каким это делают все: свойство принимает данные и
/// собирает Run'ы само.
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
            // Run создаём заново на каждое присваивание: один и тот же объект
            // нельзя положить в два TextBlock, а подсказки живут по одной на слот.
            tb.Inlines.Add(new Run(p.Text)
            {
                Foreground = p.Brush,
                FontWeight = p.Bold ? FontWeights.Bold : FontWeights.Normal,
            });
        }
    }
}
