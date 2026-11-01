using System.Runtime.InteropServices;

namespace Counterplay.Tests;

/// <summary>
/// Подставные окна Win32 с нужным классом и заголовком. Прозрачные (альфа 0),
/// сквозь них проходят клики, не активируются и не видны в панели задач —
/// человек у экрана их не заметит. Поверх всех: перебор окон идёт сверху вниз,
/// и так они всегда находятся раньше настоящего клиента.
///
/// Подключается в проект проверки строкой
/// <c>&lt;Compile Include="..\Shared\FakeWindows.cs" /&gt;</c>.
/// </summary>
internal static class Fakes
{
    public struct RECT { public int Left, Top, Right, Bottom; }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private static readonly WndProc Proc = DefWindowProc;   // держим, чтобы сборщик не забрал
    private static readonly HashSet<string> Registered = [];
    private static readonly List<IntPtr> Open = [];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize; public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(
        int ex, string cls, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);

    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                      WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const uint WS_POPUP = 0x80000000;

    public static IntPtr Create(string cls, string title, int x, int y, int w, int h)
    {
        var inst = GetModuleHandle(null);
        if (Registered.Add(cls))
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Proc,
                hInstance = inst, lpszClassName = cls,
            };
            RegisterClassEx(ref wc);
        }
        var hwnd = CreateWindowEx(
            WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
            cls, title, WS_POPUP, x, y, w, h, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
        SetLayeredWindowAttributes(hwnd, 0, 0, 0x2 /*LWA_ALPHA*/);
        ShowWindow(hwnd, 4 /*SW_SHOWNOACTIVATE*/);
        Open.Add(hwnd);
        return hwnd;
    }

    public static void Move(IntPtr h, int x, int y, int w, int hh) => MoveWindow(h, x, y, w, hh, false);
    public static void Hide(IntPtr h) => ShowWindow(h, 0 /*SW_HIDE*/);
    public static void Close(IntPtr h) { DestroyWindow(h); Open.Remove(h); }

    public static void CloseAll()
    {
        foreach (var h in Open.ToList()) DestroyWindow(h);
        Open.Clear();
    }
}
