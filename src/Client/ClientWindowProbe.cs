using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Counterplay;

/// <summary>
/// Какие окна открыты у процесса клиента и узнаёт ли их программа.
///
/// Окно драфта встаёт к клиенту и прячется вместе с ним по окну с заголовком
/// «League of Legends» и классом `RCLIENT` (`OverlayWindow.TryGetClientRect`).
/// Заголовок у китайского клиента (WeGame) не проверен: если он другой, окно
/// драфта будет жить отдельно. Проба смотрит окна самого процесса
/// `LeagueClientUx` и отвечает, узнаётся ли среди них клиент. Если нет —
/// отдаёт класс и заголовок первого окна. Это строки самого клиента, не
/// игрока: по ним станет видно, что добавить в правило.
/// </summary>
public static class ClientWindowProbe
{
    public readonly record struct Result(bool Matched, string? Other);

    /// Правило то же, что у окна драфта: класс RCLIENT, заголовок с «League of Legends».
    public static bool Recognized(string cls, string title) =>
        cls == "RCLIENT" && title.StartsWith("League of Legends", StringComparison.Ordinal);

    public static Result Probe()
    {
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("LeagueClientUx"))
        {
            pids.Add((uint)p.Id);
            p.Dispose();
        }
        if (pids.Count == 0) return new(false, null);

        var matched = false;
        string? other = null;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var title = new StringBuilder(256);
            GetWindowText(h, title, 256);
            var cls = new StringBuilder(256);
            GetClassName(h, cls, 256);
            if (Recognized(cls.ToString(), title.ToString())) { matched = true; return false; }
            other ??= Cut($"{cls}|{title}", 120);
            return true;
        }, IntPtr.Zero);
        return new(matched, matched ? null : other);
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int count);
}
