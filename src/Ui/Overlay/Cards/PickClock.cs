using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

/// <summary>
/// Отсчёт моего хода пикать — цифры и кольцо в моём слоте состава
/// (TeamSlotTemplate). Один на окно: модель моего слота держит ссылку на него,
/// и секунды меняются без пересоздания строки. Секунды в самой модели слота
/// (ChampSlotCard) нельзя: новая секунда — новая модель, UpdateSlots
/// вставляет строку заново, и волна «сейчас пикает» проигрывалась бы каждую
/// секунду.
///
/// Свойства меняются только когда меняется видимое: секунда, шаг кольца
/// (1/240 круга), «срочно». Окно прозрачное, и каждое изменение — перерисовка
/// всего окна.
/// </summary>
public sealed class PickClock : INotifyPropertyChanged
{
    /// У чужих слотов: никогда не показывается.
    public static readonly PickClock None = new();

    /// Размер кольца и толщина линии — под них сверстан шаблон.
    public const double Diameter = 46, Stroke = 3.5;
    private const int ArcSteps = 240;
    private const string Calm = "#F5D77A", Alarm = "#FF5A4D";

    public Visibility Visibility { get; private set; } = Visibility.Collapsed;
    public string     Seconds    { get; private set; } = "";
    public Geometry   Arc        { get; private set; } = Geometry.Empty;
    public bool       Urgent     { get; private set; }
    public string     Color      => Urgent ? Alarm : Calm;

    private int _step = -1;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// Осталось leftMs из totalMs; urgent — пошли последние секунды.
    internal void Show(double leftMs, double totalMs, bool urgent)
    {
        if (Visibility != Visibility.Visible) { Visibility = Visibility.Visible; Raise(nameof(Visibility)); }
        // Вверх: «1» — пока идёт последняя секунда, ноль не показываем вовсе.
        var sec = ((int)Math.Ceiling(leftMs / 1000)).ToString();
        if (sec != Seconds) { Seconds = sec; Raise(nameof(Seconds)); }
        var step = (int)Math.Ceiling(Math.Clamp(leftMs / Math.Max(totalMs, 1), 0, 1) * ArcSteps);
        if (step != _step) { _step = step; Arc = ArcOf((double)step / ArcSteps); Raise(nameof(Arc)); }
        SetUrgent(urgent);
    }

    internal void Hide()
    {
        if (Visibility == Visibility.Collapsed) return;
        Visibility = Visibility.Collapsed;
        Raise(nameof(Visibility));
        _step = -1;
        SetUrgent(false);
    }

    private void SetUrgent(bool on)
    {
        if (on == Urgent) return;
        Urgent = on;
        Raise(nameof(Urgent));
        Raise(nameof(Color));
    }

    // Оставшаяся доля хода — дугой от верха по часовой стрелке. Окружность —
    // та же, что у подложки-кольца в шаблоне (Ellipse рисует линию внутри
    // своих границ).
    private static Geometry ArcOf(double frac)
    {
        if (frac <= 0) return Geometry.Empty;
        var c = Diameter / 2;
        var r = (Diameter - Stroke) / 2;
        Geometry g;
        if (frac >= 0.999) g = new EllipseGeometry(new Point(c, c), r, r);
        else
        {
            var a = frac * 2 * Math.PI;
            var s = new StreamGeometry();
            using (var ctx = s.Open())
            {
                ctx.BeginFigure(new Point(c, c - r), isFilled: false, isClosed: false);
                ctx.ArcTo(new Point(c + r * Math.Sin(a), c - r * Math.Cos(a)), new Size(r, r), 0,
                          isLargeArc: frac > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
            }
            g = s;
        }
        g.Freeze();
        return g;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
