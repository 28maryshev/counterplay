using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Animation;

using Brush  = System.Windows.Media.Brush;
using Color  = System.Windows.Media.Color;
using Point  = System.Windows.Point;

namespace Counterplay;

/// <summary>
/// Скелетон-заглушки (skeleton screen): пока данных нет, на их месте стоят
/// плашки той же формы с бегущим бликом. Человек сразу видит структуру экрана
/// и понимает, что здесь появится, вместо пустоты или прочерков.
/// </summary>
public static class Skeleton
{
    // Кисть одна на всё приложение: блик у всех заглушек бежит синхронно
    // (так спокойнее для глаза) и стоит одну анимацию вместо десятков.
    private static LinearGradientBrush? _shimmer;

    public static Brush Shimmer => _shimmer ??= CreateShimmer();

    private static LinearGradientBrush CreateShimmer()
    {
        var b = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.35),
                new GradientStop(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), 0.5),
                new GradientStop(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 0.65),
                new GradientStop(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF), 1.0),
            },
        };

        // Блик гоняем сдвигом самой кисти: градиент проезжает по плашке слева
        // направо, уходит за край и там ЖДЁТ — из-за паузы мерцание редкое и
        // не дёргает глаз (проезд ~1,4 с, следующий — через ~4 с).
        var t = new TranslateTransform();
        b.RelativeTransform = t;

        var sweep = new DoubleAnimationUsingKeyFrames
        {
            Duration       = new Duration(TimeSpan.FromSeconds(5.4)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1.2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(5.4))));
        t.BeginAnimation(TranslateTransform.XProperty, sweep);
        return b;
    }

    /// Прямоугольная плашка-заглушка заданного размера.
    public static Border Block(double width, double height, double radius = 4)
        => new()
        {
            Width  = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background   = Shimmer,
            BorderThickness = new Thickness(1),
            BorderBrush  = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            SnapsToDevicePixels = true,
        };

    /// Плашка на всю доступную ширину (высота фиксирована) — для строк текста.
    public static Border Line(double height, double radius = 3)
        => new()
        {
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background   = Shimmer,
            BorderThickness = new Thickness(1),
            BorderBrush  = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            SnapsToDevicePixels = true,
        };
}
