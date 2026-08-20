using System.Windows.Media;
// В проекте подключён и WinForms — уточняем, что цвета/кисти именно WPF-овские.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Counterplay;

/// <summary>
/// Единая шкала цвета винрейта — одна на всё приложение (лобби, график, тир-лист,
/// настройки пула), чтобы одна и та же цифра везде выглядела одинаково.
///
/// до 45 %   — серый (провал)
/// 45–48 %   — серо-белый
/// 48–54 %   — белый (норма)
/// 55–65 %   — оранжевый (хорошо)
/// 65 % и выше — красный (выдающийся)
///
/// Между опорными точками цвет ПЛАВНО переливается, поэтому 53.9 % и 54.1 % не
/// отличаются рывком — граница не выглядит ступенькой.
/// </summary>
public static class WinrateColor
{
    // Опорные точки шкалы (винрейт → цвет). Между ними — линейная интерполяция.
    private static readonly (double Wr, Color C)[] Stops =
    [
        (40.0, Color.FromRgb(0x6E, 0x7B, 0x87)),  // серый
        (45.0, Color.FromRgb(0x8C, 0x9A, 0xA8)),  // серый (граница зоны)
        (48.0, Color.FromRgb(0xC4, 0xCF, 0xDA)),  // серо-белый
        (50.0, Color.FromRgb(0xE6, 0xED, 0xF3)),  // белый
        (54.0, Color.FromRgb(0xE6, 0xED, 0xF3)),  // белый (конец нормы)
        (57.0, Color.FromRgb(0xF0, 0xBE, 0x5E)),  // светлый оранжевый
        (61.0, Color.FromRgb(0xF0, 0xA9, 0x3C)),  // оранжевый
        (65.0, Color.FromRgb(0xF2, 0x80, 0x2E)),  // насыщенный оранжевый
        (69.0, Color.FromRgb(0xE2, 0x4C, 0x4C)),  // красный
    ];

    /// Цвет для винрейта в процентах (0..100).
    public static Color Of(double wr)
    {
        if (wr <= Stops[0].Wr) return Stops[0].C;
        if (wr >= Stops[^1].Wr) return Stops[^1].C;

        for (int i = 1; i < Stops.Length; i++)
        {
            var (w1, c1) = Stops[i];
            if (wr > w1) continue;
            var (w0, c0) = Stops[i - 1];
            var t = (wr - w0) / (w1 - w0);          // 0..1 внутри отрезка
            return Color.FromRgb(Mix(c0.R, c1.R, t), Mix(c0.G, c1.G, t), Mix(c0.B, c1.B, t));
        }
        return Stops[^1].C;
    }

    private static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    public static Brush Brush(double wr) => new SolidColorBrush(Of(wr));

    /// Для привязок в XAML, где цвет задаётся строкой.
    public static string Hex(double wr)
    {
        var c = Of(wr);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// Цвет с оговоркой на объём выборки: на 1–4 играх винрейт ещё ничего не
    /// значит, поэтому такие цифры показываем нейтрально-серыми.
    public static Color ColorForSample(double wr, int games) =>
        games < 5 ? Color.FromRgb(0x8A, 0xA0, 0xB2) : Of(wr);

    public static Brush BrushForSample(double wr, int games) =>
        new SolidColorBrush(ColorForSample(wr, games));

    /// Тот же цвет еле заметной заливкой — подложка слота под рамку в тон.
    public static Brush TintForSample(double wr, int games, byte alpha = 0x1F)
    {
        var c = ColorForSample(wr, games);
        return new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
    }
}
