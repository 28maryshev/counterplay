using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using System.Windows.Media.Imaging;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Слот связки в ленте сайдбара.
///
/// Слот — квадрат 30×30, шесть в ряд; двух полноразмерных иконок туда не
/// поставить, поэтому они рисуются одной кистью: каждому чемпиону своя
/// половина, между ними щель. Место тихое: ошибись в системе координат кисти —
/// и слот выйдет пустым или расплющенным, а сборка об этом не скажет.
///
/// Поэтому рисуем слот в картинку и смотрим пиксели: обе половины закрашены,
/// между ними просвет, и половины не совпадают друг с другом (иначе значит
/// нарисовали одного и того же дважды).
/// </summary>
internal static class Program
{
    private static int _fails;
    private const int Size = 120;   // крупнее слота: пиксели считать надёжнее

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        // Две заметно разные картинки: сплошной синий и сплошной красный. На
        // настоящих иконках половины тоже различались бы, но проверять надо
        // геометрию, а не умение Data Dragon отдавать файлы.
        var left  = Solid(Colors.DodgerBlue);
        var right = Solid(Colors.Firebrick);

        var card = MyChampCard.FromPair(left, right, "73%",
                                        Brushes.Gray, Brushes.Transparent, "проверка");
        var px = Render(card.Fill!);

        // Столбцы: 15% — левая половина, 50% — щель, 85% — правая.
        var l = px(Size * 15 / 100, Size / 2);
        var gap = px(Size / 2, Size / 2);
        var r = px(Size * 85 / 100, Size / 2);

        Console.WriteLine($"  левая  {l}");
        Console.WriteLine($"  щель   {gap}");
        Console.WriteLine($"  правая {r}");

        Check("левая половина закрашена", l.A > 200, l.ToString());
        Check("правая половина закрашена", r.A > 200, r.ToString());
        Check("между ними просвет", gap.A < 60, gap.ToString());
        Check("половины разные", Far(l, r), $"{l} против {r}");

        // Одного чемпиона хватает: вторая половина просто пустует, слот не рушится.
        var one = MyChampCard.FromPair(left, null, "50%", Brushes.Gray, Brushes.Transparent, "");
        var p1 = Render(one.Fill!);
        Check("с одним чемпионом слот не пустой", p1(Size * 15 / 100, Size / 2).A > 200, "да");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: слот связки рисуется двумя половинами"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Сплошная картинка 64×64 — заготовка вместо иконки чемпиона.
    private static ImageSource Solid(Color c)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(0, 0, 64, 64));
        var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    /// Рисует кисть в квадрат и возвращает «дай пиксель».
    private static Func<int, int, Color> Render(Brush fill)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(fill, null, new Rect(0, 0, Size, Size));
        var bmp = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);

        var stride = Size * 4;
        var buf = new byte[stride * Size];
        bmp.CopyPixels(buf, stride, 0);
        return (x, y) =>
        {
            var i = y * stride + x * 4;
            return Color.FromArgb(buf[i + 3], buf[i + 2], buf[i + 1], buf[i]);
        };
    }

    private static bool Far(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) > 60;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
