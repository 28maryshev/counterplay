using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Counterplay;

// Таймер хода пика: отсчёт с кольцом в слотах состава тех, чей ход сейчас
// (PickClock), и — когда ход мой — мигающая рамка вокруг центра окна за
// 8 секунд до конца.
public partial class OverlayWindow
{
    /// С какого остатка хода отсчёт краснеет, а в мой ход центр мигает рамкой.
    /// 8 секунд — владелец, 26.11 (сначала было 5: поздно, не успеть).
    public const double PickAlarmMs = 8000;

    // Один на окно: его держат слоты всех, чей ход (таймер у хода общий).
    private readonly PickClock _pickClock = new();
    private DispatcherTimer? _pickTick;
    private DateTime? _pickDeadline;   // конец хода, UTC; null — отсчёта нет
    private double _pickTotalMs;       // длина хода — для доли кольца
    private bool _pickMine;            // ход мой — рамка центра в последние секунды
    private bool _pickAlarm;

    /// <summary>
    /// Срок нынешнего хода пика (UTC), длина хода и мой ли он; null — отсчёта
    /// нет (пиков сейчас никто не делает). Отсчёт видят слоты тех, чей ход
    /// (<c>ActiveCells</c>), рамку центра — только мой ход. Цикл клиента зовёт
    /// на каждое событие драфта: остаток в каждом событии свежий, и отсчёт по
    /// нему выравнивается; между событиями окно считает само. Песочница — на
    /// каждую перерисовку.
    /// </summary>
    public void SetPickTimer(DateTime? deadlineUtc, double totalMs, bool mine) =>
        Dispatcher.InvokeAsync(() => ApplyPickTimer(deadlineUtc, totalMs, mine));

    private void ApplyPickTimer(DateTime? deadline, double totalMs, bool mine)
    {
        if (deadline is null) { StopPickTimer(); return; }
        _pickDeadline = deadline;
        _pickTotalMs = totalMs;
        _pickMine = mine;
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
        // Время вышло — клиент сам возьмёт наведённого или пропустит ход;
        // нулём висеть незачем.
        if (left <= 0) { StopPickTimer(); return; }
        var urgent = left <= PickAlarmMs;
        _pickClock.Show(left, Math.Max(_pickTotalMs, left), urgent);
        SetPickAlarm(urgent && _pickMine);
    }

    private void StopPickTimer()
    {
        _pickTick?.Stop();
        _pickDeadline = null;
        _pickMine = false;
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
