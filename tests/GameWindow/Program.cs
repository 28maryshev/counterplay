using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Окно игры не принимается за окно клиента.
///
/// Владелец: «после того как заканчивается игра, сайдбар сначала широкий на
/// секунду, потом сужается до нормального». В журнале программы — после
/// каждой игры одно и то же: «масштаб 1,35 … клиент ×1,35» и «вписал в экран:
/// было (2560;0)», через пару секунд «масштаб 1,00». Клиент у владельца
/// 1280×720 (масштаб 1,00), экран 2560 в ширину.
///
/// Причина: окно клиента искалось по заголовку «League of Legends» и классу
/// RCLIENT **или** RiotWindowClass. Второй — класс окна самой игры
/// («League of Legends (TM) Client»). Пока полноэкранная игра закрывается, её
/// окно ещё на экране и выше клиента — программа мерила его: ширина 2560 →
/// «клиент 1920» → ×1,35, и панель профиля вставала к его правому краю.
///
/// Проверка ставит подставные окна Win32 с этими классами — прозрачные,
/// поверх всех, сквозь них проходят клики, — и спрашивает у окна драфта,
/// где клиент и какой у него масштаб.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static readonly MethodInfo GetRect = typeof(OverlayWindow)
        .GetMethod("TryGetClientRect", BindingFlags.NonPublic | BindingFlags.Static)!;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "GameWindow");
        Try(() => Directory.Delete(root, true));
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        AppSettings.Current.ClientSize = "auto";

        // Настоящий клиент на экране — он тоже найдётся. Подставные окна стоят
        // поверх всех и перебираются раньше него; мешает он только шагу 3.
        var realClient = Rect() is not null;

        try { Run(realClient); }
        finally { Fakes.CloseAll(); Try(() => Directory.Delete(root, true)); }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: окно игры за клиент не принимается"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run(bool realClient)
    {
        // ── 1. Игра закрывается: её окно ещё на экране, клиент — под ним ────
        var client = Fakes.Create("RCLIENT", "League of Legends", 100, 100, 1280, 720);
        var game   = Fakes.Create("RiotWindowClass", "League of Legends (TM) Client", 0, 0, 2560, 1440);
        var r = Rect();
        var scale = OverlayWindow.ClientScaleFor(null);
        Check("1. окно игры поверх клиента — меряется клиент", r is { Right: 1380, Top: 100 },
              r is { } x ? $"найдено окно с правым краем {x.Right} (клиент — 1380, игра — 2560)" : "ничего");
        Check("   масштаб по клиенту 1280 — ×1,00, а не по игре", Math.Abs(scale - 1.0) < 0.01,
              $"×{scale:0.00}");

        // ── 2. Игра закрылась, клиент большой ─────────────────────────────
        Fakes.Close(game);
        Fakes.Move(client, 100, 100, 1920, 1080);
        scale = OverlayWindow.ClientScaleFor(null);
        Check("2. клиент 1920 — ×1,35", Math.Abs(scale - 1.35) < 0.01, $"×{scale:0.00}");

        // ── 3. Клиента на миг не видно (ещё не развернулся после игры) ──────
        // Масштаб — как в последний раз. «Как есть» (×1,00) у большого клиента
        // дало бы тот же прыжок в другую сторону: узкая панель, потом широкая.
        Fakes.Hide(client);
        if (realClient)
            Console.WriteLine("  [—] 3. пропущен: открыт настоящий клиент LoL, «клиента не видно» не изобразить");
        else
        {
            scale = OverlayWindow.ClientScaleFor(null);
            Check("3. клиента не видно — масштаб прежний, ×1,35", Math.Abs(scale - 1.35) < 0.01,
                  $"×{scale:0.00}");
        }
    }

    private static Fakes.RECT? Rect()
    {
        object?[] args = [null];
        var ok = (bool)GetRect.Invoke(null, args)!;
        if (!ok) return null;
        // RECT окна драфта — свой закрытый тип с теми же четырьмя полями.
        var v = args[0]!;
        int F(string n) => (int)v.GetType().GetField(n)!.GetValue(v)!;
        return new Fakes.RECT { Left = F("Left"), Top = F("Top"), Right = F("Right"), Bottom = F("Bottom") };
    }

    private static void Try(Action a) { try { a(); } catch { /* нечего чистить */ } }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}

/// <summary>
/// Подставные окна Win32 с нужным классом и заголовком. Прозрачные (альфа 0),
/// сквозь них проходят клики, не активируются и не видны в панели задач —
/// человек у экрана их не заметит. Поверх всех: перебор окон идёт сверху вниз,
/// и так они всегда находятся раньше настоящего клиента.
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
