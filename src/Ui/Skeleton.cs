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
    private static TranslateTransform? _sweepT;

    /// Кисть с бликом. Взять её — значит поставить заглушку на экран: блик
    /// бежит ещё <see cref="ShimmerFor"/> с последнего такого раза и замирает
    /// за краем.
    public static Brush Shimmer
    {
        get
        {
            _shimmer ??= CreateShimmer();
            Wake();
            return _shimmer;
        }
    }

    /// Сколько бежит блик после последнего показа заглушки. Вечный блик рисовал
    /// окно 60 раз в секунду до закрытия программы — и за скрытыми заглушками
    /// тоже (игрок: «10% видеокарты», 17.11.2026). Загрузка, которая идёт
    /// дольше, уже ничего не обещает: заглушка остаётся просто плашкой.
    /// Проверка ставит короче, чтобы не ждать.
    public static TimeSpan ShimmerFor { get; set; } = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan Cycle = TimeSpan.FromSeconds(5.4);
    private static DateTime _shimmerUntil;
    private static bool _sweeping;

    /// Та же плашка без блика — для места, которое заполнится не загрузкой, а
    /// сыгранными играми: блик обещает «сейчас появится», а ждать тут нечего.
    public static Brush Still { get; } = Frozen(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

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
        // не дёргает глаз (проезд ~1,4 с, следующий — через ~4 с). Стоит блик
        // за краем же: X = 1,2 — ровная плашка.
        _sweepT = new TranslateTransform(1.2, 0);
        b.RelativeTransform = _sweepT;
        return b;
    }

    /// Продлить блик: показали заглушку. Идёт — пусть идёт дольше; стоит —
    /// запустить. Проезды целые: обрыв посреди плашки дёрнул бы глаз.
    private static void Wake()
    {
        _shimmerUntil = DateTime.UtcNow + ShimmerFor;
        if (_sweeping) return;
        _sweeping = true;
        Sweep();
    }

    private static void Sweep()
    {
        var left = _shimmerUntil - DateTime.UtcNow;
        var sweep = new DoubleAnimationUsingKeyFrames
        {
            Duration       = new Duration(Cycle),
            RepeatBehavior = new RepeatBehavior(Math.Max(1, Math.Ceiling(left / Cycle))),
        };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1.2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.4))));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1.2, KeyTime.FromTimeSpan(Cycle)));
        // Пока бежали, заглушку показали снова — ещё круг; нет — блик замер за
        // краем (последний кадр держится) и часы анимаций стоят.
        sweep.Completed += (_, _) =>
        {
            if (DateTime.UtcNow < _shimmerUntil) Sweep();
            else _sweeping = false;
        };
        _sweepT!.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    /// Прямоугольная плашка-заглушка заданного размера. still — без блика
    /// (<see cref="Still"/>): место ждёт игр, а не загрузки.
    public static Border Block(double width, double height, double radius = 4, bool still = false)
        => new()
        {
            Width  = width,
            Height = height,
            CornerRadius = new CornerRadius(radius),
            Background   = still ? Still : Shimmer,
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
