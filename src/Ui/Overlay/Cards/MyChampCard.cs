using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

/// <summary>Колонка роли в тир-листе: два вертикальных списка — по WR и по тиру.</summary>
/// Чемпион в ленте «мои за 30 дней» на ready-экране.
public sealed class MyChampCard
{
    public Brush?  Fill  { get; init; }          // иконка чемпиона или заливка пустого слота
    public string  Wr    { get; init; } = "";
    public Brush   Frame { get; init; } = System.Windows.Media.Brushes.Gray;
    public Brush   Tint  { get; init; } = System.Windows.Media.Brushes.Transparent;
    public string  Tip   { get; init; } = "";
    public Visibility WrSkeleton { get; init; } = Visibility.Collapsed;

    public static MyChampCard FromChampion(ImageSource? icon, string wr, Brush frame, Brush tint, string tip)
        => new()
        {
            // Иконки ещё нет (Data Dragon не докачал) — скелетон вместо дыры.
            Fill  = icon != null
                ? new ImageBrush(icon) { Stretch = Stretch.UniformToFill }
                : Skeleton.Shimmer,
            Wr = wr, Frame = frame, Tint = tint, Tip = tip,
        };

    /// <summary>
    /// Карточка СВЯЗКИ: два чемпиона в одном слоте.
    ///
    /// Слот в ленте — квадрат 30×30, и шесть таких стоят в ряд; поставить рядом
    /// две полноразмерные иконки там негде. Поэтому рисуем их одной кистью:
    /// каждому достаётся своя половина слота, а между ними щель, сквозь которую
    /// видно подложку — так две половины читаются как одна связка, а не как
    /// слипшиеся картинки.
    ///
    /// Берём у иконки центральную половину по ширине, а не сжимаем её целиком:
    /// у половины слота пропорции 1:2, у целой иконки 1:1, и сжатие расплющило
    /// бы лица.
    /// </summary>
    public static MyChampCard FromPair(ImageSource? mine, ImageSource? theirs,
                                       string wr, Brush frame, Brush tint, string tip)
        => new()
        {
            Fill  = mine is null && theirs is null ? Skeleton.Shimmer : PairFill(mine, theirs),
            Wr = wr, Tip = tip,
            // Цвет — по винрейту, как у остальных карточек. Помечать «из пула»
            // тут нечем и незачем: лента показывает только связки с напарником
            // по пулу, то есть из пула они все, а краска съела бы единственный
            // полезный сигнал — как эта пара играет.
            Frame = frame, Tint = tint,
        };

    private static Brush PairFill(ImageSource? mine, ImageSource? theirs)
    {
        var group = new DrawingGroup();
        Half(mine,   0.00);
        Half(theirs, 0.52);

        void Half(ImageSource? src, double x)
        {
            if (src is null) return;
            var brush = new ImageBrush(src)
            {
                Stretch      = Stretch.Fill,
                Viewbox      = new Rect(0.25, 0, 0.5, 1),   // центральная половина иконки
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            };
            group.Children.Add(new GeometryDrawing(
                brush, null, new RectangleGeometry(new Rect(x, 0, 0.48, 1), 0.06, 0.06)));
        }

        return new DrawingBrush(group)
        {
            Stretch      = Stretch.Fill,
            Viewbox      = new Rect(0, 0, 1, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
    }

    // Скелетон слота: размеры настоящей карточки, вместо иконки и цифр — плашки
    // с бликом, чтобы место под статистику читалось как «скоро заполнится».
    public static MyChampCard Placeholder(string tip) => new()
    {
        Fill       = Skeleton.Shimmer,
        Wr         = "",
        WrSkeleton = Visibility.Visible,
        Frame      = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
        Tint       = System.Windows.Media.Brushes.Transparent,
        Tip        = tip,
    };
}
