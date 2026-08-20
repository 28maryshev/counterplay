using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Counterplay;

/// Сегмент строки обоснования: текст + опциональный цвет (имя чемпиона по архетипу)
/// либо перевод строки.
public sealed class ReasonSeg
{
    public string Text { get; init; } = "";
    public string Color { get; init; } = ""; // пусто = цвет по умолчанию (наследуется)
    public bool Break { get; init; }          // перевод строки между причинами
}

/// Attached-property: заполняет Inlines у TextBlock цветными Run'ами. Имена
/// чемпионов красятся в цвет их архетипа, остальной текст — цветом TextBlock.
public static class ReasonInlines
{
    public static readonly DependencyProperty SegmentsProperty =
        DependencyProperty.RegisterAttached(
            "Segments", typeof(IEnumerable<ReasonSeg>), typeof(ReasonInlines),
            new PropertyMetadata(null, OnChanged));

    public static void SetSegments(DependencyObject o, IEnumerable<ReasonSeg>? v) =>
        o.SetValue(SegmentsProperty, v);
    public static IEnumerable<ReasonSeg>? GetSegments(DependencyObject o) =>
        (IEnumerable<ReasonSeg>?)o.GetValue(SegmentsProperty);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is not IEnumerable<ReasonSeg> segs) return;

        foreach (var s in segs)
        {
            if (s.Break) { tb.Inlines.Add(new LineBreak()); continue; }
            var run = new Run(s.Text);
            if (!string.IsNullOrEmpty(s.Color))
                try
                {
                    run.Foreground = new SolidColorBrush(
                        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(s.Color));
                    run.FontWeight = FontWeights.SemiBold;
                }
                catch { /* некорректный цвет — оставляем по умолчанию */ }
            tb.Inlines.Add(run);
        }
    }
}
