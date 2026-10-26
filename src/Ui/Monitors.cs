using System.Runtime.InteropServices;
using System.Windows;
using Point = System.Windows.Point;

namespace Counterplay;

/// <summary>Монитор: рабочая область (без панели задач) в пикселях устройства.</summary>
public sealed record MonitorArea(Rect Work, bool Primary);

/// <summary>
/// Рабочие области ВСЕХ мониторов.
///
/// <see cref="SystemParameters.WorkArea"/> — это только основной монитор. Окно
/// драфта вписывалось в него на каждом обновлении, и человек, унёсший окно на
/// второй монитор, видел, как оно на каждом пике прыгает обратно к краю
/// основного. Вписывать окно надо в тот монитор, на котором оно стоит.
/// </summary>
public static class Monitors
{
    /// Проверкам: свои мониторы вместо настоящих (пиксели устройства). Второй
    /// монитор у проверки не подключишь, а поведение окна на нём проверять надо.
    public static IReadOnlyList<MonitorArea>? Override { get; set; }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private struct RECT { public int Left, Top, Right, Bottom; }

    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;    // 1 — основной монитор
    }

    /// Все мониторы в пикселях устройства, основной первым. Спрашиваем Windows
    /// каждый раз, без кэша: монитор могут подключить посреди драфта.
    public static IReadOnlyList<MonitorArea> All()
    {
        if (Override is { Count: > 0 } o) return o;
        var list = new List<MonitorArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
            {
                var w = mi.rcWork;
                list.Add(new MonitorArea(new Rect(w.Left, w.Top, w.Right - w.Left, w.Bottom - w.Top),
                                         (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        // Windows не ответил — хотя бы основной монитор, как было раньше.
        if (list.Count == 0)
        {
            var wa = SystemParameters.WorkArea;
            return [new MonitorArea(wa, true)];
        }
        return list.OrderByDescending(m => m.Primary).ToList();
    }

    /// Рабочие области в координатах окна (DIP): пиксели делим на масштаб
    /// экрана окна — так же, как в остальном коде, что меряет окно клиента.
    public static IReadOnlyList<Rect> WorkAreas(double dpiX, double dpiY) =>
        All().Select(m => new Rect(m.Work.X / dpiX, m.Work.Y / dpiY,
                                   m.Work.Width / dpiX, m.Work.Height / dpiY)).ToList();

    /// Монитор окна: тот, на который приходится большая часть прямоугольника.
    /// Не попадает ни на один (монитор отключили) — ближайший к его центру.
    public static Rect Nearest(Rect r, IReadOnlyList<Rect> areas)
    {
        if (areas.Count == 0) return SystemParameters.WorkArea;
        var best = areas.MaxBy(a => Overlap(r, a));
        if (Overlap(r, best) > 0) return best;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        return areas.MinBy(a => Distance(c, a));
    }

    /// Номер монитора окна для журнала (с 1, основной — первый).
    public static int IndexOf(Rect r, IReadOnlyList<Rect> areas)
    {
        if (areas.Count == 0) return 0;
        var n = Nearest(r, areas);
        for (var i = 0; i < areas.Count; i++)
            if (areas[i] == n) return i + 1;
        return 0;
    }

    /// Строка для журнала: «1) 1920×1040 в (0;0) основной; 2) 1920×1040 в (1920;0)».
    public static string Describe(IReadOnlyList<Rect> areas) =>
        string.Join("; ", areas.Select((a, i) =>
            $"{i + 1}) {a.Width:0}×{a.Height:0} в ({a.X:0};{a.Y:0}){(i == 0 ? " основной" : "")}"));

    private static double Overlap(Rect a, Rect b)
    {
        var x = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var y = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return x > 0 && y > 0 ? x * y : 0;
    }

    private static double Distance(Point p, Rect a)
    {
        var dx = Math.Max(0, Math.Max(a.Left - p.X, p.X - a.Right));
        var dy = Math.Max(0, Math.Max(a.Top - p.Y, p.Y - a.Bottom));
        return dx * dx + dy * dy;
    }
}
