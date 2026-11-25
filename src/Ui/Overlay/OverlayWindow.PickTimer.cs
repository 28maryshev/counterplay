using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Counterplay;

// Таймер моего пика: отсчёт с кольцом в моём слоте состава (PickClock) и
// мигающая рамка вокруг центра окна за 5 секунд до конца хода.
public partial class OverlayWindow
{
    /// С какого остатка хода центр мигает рамкой, а отсчёт краснеет.
    public const double PickAlarmMs = 5000;

    private readonly PickClock _pickClock = new();
    private DispatcherTimer? _pickTick;
    private DateTime? _pickDeadline;   // конец моего хода, UTC; null — отсчёта нет
    private double _pickTotalMs;       // длина хода — для доли кольца
    private bool _pickAlarm;

    /// <summary>
    /// Срок моего хода пикать (UTC) и длина хода; null — отсчёта нет (ход не
    /// мой, пик сделан). Цикл клиента зовёт на каждое событие драфта: остаток
    /// в каждом событии свежий, и отсчёт по нему выравнивается; между
    /// событиями окно считает само. Песочница — на каждую перерисовку.
    /// </summary>
    public void SetPickTimer(DateTime? deadlineUtc, double totalMs) =>
        Dispatcher.InvokeAsync(() => ApplyPickTimer(deadlineUtc, totalMs));

    private void ApplyPickTimer(DateTime? deadline, double totalMs)
    {
        if (deadline is null) { StopPickTimer(); return; }
        _pickDeadline = deadline;
        _pickTotalMs = totalMs;
        if (_pickTick is null)
        {
            // 100 мс — чтобы рамка включалась вовремя; перерисовка — только
            // когда в отсчёте меняется видимое (PickClock).
            _pickTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _pickTick.Tick += (_, _) => PickTimerTick();
        }
        _pickTick.Start();
        PickTimerTick();
    }

    private void PickTimerTick()
    {
        var left = _pickDeadline is { } end ? (end - DateTime.UtcNow).TotalMilliseconds : 0;
        // Время вышло — клиент сам возьмёт наведённого или закроет драфт;
        // нулём висеть незачем.
        if (left <= 0) { StopPickTimer(); return; }
        var urgent = left <= PickAlarmMs;
        _pickClock.Show(left, Math.Max(_pickTotalMs, left), urgent);
        SetPickAlarm(urgent);
    }

    private void StopPickTimer()
    {
        _pickTick?.Stop();
        _pickDeadline = null;
        _pickClock.Hide();
        SetPickAlarm(false);
    }

    // Рамка центра: мигание — анимацией прозрачности на эти секунды, снимается
    // вместе с рамкой (в покое окно не должно рисовать кадров).
    private void SetPickAlarm(bool on)
    {
        if (on == _pickAlarm) return;
        _pickAlarm = on;
        if (on)
        {
            PickAlarmFrame.Visibility = Visibility.Visible;
            PickAlarmFrame.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, 0.15, TimeSpan.FromSeconds(0.45))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
                });
        }
        else
        {
            PickAlarmFrame.BeginAnimation(OpacityProperty, null);
            PickAlarmFrame.Visibility = Visibility.Collapsed;
        }
    }
}
