namespace Counterplay;

/// <summary>
/// Строка показателя на карточке пика: подпись, число со знаком, полоса.
///
/// Порядок, цвета и шкала — в <see cref="Defs"/>, одном месте для обеих карточек
/// (обычной и пула) и подсказки у полосы чемпионов роли. Порядок — владельца
/// 20.10: против оппонента, против их команды, синергия с союзниками, сила в
/// патче, против их стиля. До того строк было четыре, каждая — отдельным
/// куском разметки со своим стилем подсветки, а «против их команды» на
/// карточку не попадало вовсе.
/// </summary>
public sealed class MetricRow
{
    public string Label     { get; init; } = "";
    public string Text      { get; init; } = "";
    public string TextColor { get; init; } = "#7C97AC";
    public double Bar       { get; init; }               // ширина полосы, px (0…214)
    public string BarColor  { get; init; } = "#4080C0";
    public bool   Strong    { get; init; }               // сильнейшая среди показанных — золотая рамка

    /// Показатели по порядку: ключ подписи, значение, цвет числа, цвет полосы и
    /// нижний порог шкалы в п.п. (лидер ниже порога полосу не растягивает — шум).
    public static readonly (string Key, Func<Recommendation, double> Value, string TextColor, string BarColor, double Floor)[] Defs =
    [
        ("metric.direct",  r => r.DirectDelta,  "#E06464", "#C84040", 1.5),
        ("metric.team",    r => r.OtherDelta,   "#C77DD0", "#9A4FA8", 1.5),
        ("metric.synergy", r => r.SynergyDelta, "#5BC487", "#40A060", 1.5),
        ("metric.base",    r => r.BaseDelta,    "#5C9BDC", "#4080C0", 2.0),
        ("metric.style",   r => r.StyleDelta,   "#E0944C", "#C87030", 1.5),
    ];

    /// Ширина полосы: по РАЗБРОСУ в текущем списке, а не по абсолютной шкале. На
    /// большой базе дельты честно малы (доли п.п.), и фиксированная шкала рисовала
    /// у всех почти пустые полосы — разницу между кандидатами было не разглядеть.
    public static double Scaled(double d, double max, double floor) =>
        d <= 0 ? 0 : Math.Max(0, Math.Min(214, d / Math.Max(max, floor) * 214));

    /// Строки показателей кандидата. max — лидер по каждому показателю среди
    /// показанных (в порядке Defs); highlight — подсвечивать ли лидера.
    public static List<MetricRow> Of(Recommendation r, double[] max, bool highlight) =>
        [.. Defs.Select((d, i) =>
        {
            var v = d.Value(r);
            return new MetricRow
            {
                Label     = Loc.T(d.Key),
                Text      = (v >= 0 ? "+" : "") + v.ToString("F1"),
                TextColor = d.TextColor,
                Bar       = Scaled(v, max[i], d.Floor),
                BarColor  = d.BarColor,
                Strong    = highlight && max[i] > 0.05 && v >= max[i] - 0.05,
            };
        })];

    /// Лидер по каждому показателю среди кандидатов (в порядке Defs).
    public static double[] MaxOf(IReadOnlyCollection<Recommendation> recs) =>
        [.. Defs.Select(d => recs.Count > 0 ? recs.Max(d.Value) : 0)];
}
