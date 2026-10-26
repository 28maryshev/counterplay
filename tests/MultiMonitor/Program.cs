using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Окно драфта на втором мониторе.
///
/// Жалоба: League на одном мониторе, окно программы унесли на другой — и во
/// время драфта на каждом обновлении оно возвращается на первый. После каждой
/// перерисовки окно вписывалось в рабочую область ОСНОВНОГО монитора
/// (SystemParameters.WorkArea): в драфте «шапка должна остаться на экране», и
/// всё, что за краем основного монитора, считалось «за краем экрана».
///
/// Второй монитор проверке не подключить, поэтому мониторы подставные
/// (Monitors.Override), а окно настоящее и проходит всю цепочку драфта.
/// Настройки — во временной папке: конец драфта запоминает раскладку.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Запомненная раскладка — как у игрока, который подвинул окно под себя.
    private const double W = 1452, H = 901;

    // Основной монитор и второй справа от него — самый частый стол. Основной
    // нарочно широкий: прежний код мерил НАСТОЯЩИЙ основной монитор того, кто
    // гоняет проверку, и окно на подставном втором должно лежать правее любого
    // из них — иначе на широком мониторе поломка не видна.
    private static readonly Rect Primary = new(0, 0, 3840, 2100);

    private static readonly Rect RightOf = new(3840, 0, 1920, 1040);

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "MultiMonitor");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;

        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        try
        {
            StaysOnSecond("второй монитор справа", [Primary, RightOf], 4200, 120);
            StaysOnSecond("второй монитор слева", [new(0, 0, 2560, 1400), new(-1920, 0, 1920, 1040)], -1800, 100);
            StaysOnSecond("второй монитор сверху", [Primary, new(0, -1080, 1920, 1040)], 100, -1000);
            MovedMidDraft();
            PanelOnSecond();
            Unplugged();
        }
        finally
        {
            Monitors.Override = null;
            app.Shutdown();
            AppPaths.RootOverride = null;
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "ИТОГ: окно остаётся на том мониторе, где его поставили"
            : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Раскладка запомнена на втором мониторе — окно встаёт туда и переживает
    /// обновления драфта. Раньше первое же обновление тянуло его к краю
    /// основного.
    /// </summary>
    private static void StaysOnSecond(string name, Rect[] monitors, double l, double t)
    {
        Console.WriteLine();
        Console.WriteLine(name);
        Remember(l, t);
        var w = Open();
        Use(w, monitors);

        for (var i = 0; i < 4; i++)
        {
            w.UpdateRecommendations([Rec()], Aram(), null);
            Pump();
        }
        Check($"{name}: окно осталось, где стояло", Near(w, l, t),
              $"({w.Left:0};{w.Top:0}), ждали ({l:0};{t:0})");

        w.Close();
        Pump();
    }

    /// <summary>
    /// Ровно жалоба: драфт открылся на основном мониторе, человек унёс окно на
    /// второй. Следующие обновления его не возвращают, конец драфта запоминает
    /// новое место, и следующий драфт открывается там же.
    /// </summary>
    private static void MovedMidDraft()
    {
        Console.WriteLine();
        Console.WriteLine("унесли на второй посреди драфта");
        Remember(200, 100);
        var w = Open();
        Use(w, [Primary, RightOf]);

        w.UpdateRecommendations([Rec()], Aram(), null);
        Pump();
        w.Left = 4300; w.Top = 150;                 // перетащили на второй монитор
        for (var i = 0; i < 3; i++)
        {
            w.UpdateRecommendations([Rec()], Aram(), null);
            Pump();
        }
        Check("обновления драфта не вернули окно", Near(w, 4300, 150), $"({w.Left:0};{w.Top:0})");

        w.UpdateRecommendations(null, null);         // драфт кончился
        Pump();
        var s = AppSettings.Current;
        Check("конец драфта запомнил место на втором мониторе",
              Math.Abs(s.DraftLeft - 4300) < 2 && Math.Abs(s.DraftTop - 150) < 2,
              $"запомнено ({s.DraftLeft:0};{s.DraftTop:0})");

        w.UpdateRecommendations([Rec()], Aram(), null);   // следующий драфт
        Pump();
        Check("следующий драфт открылся там же", Near(w, 4300, 150), $"({w.Left:0};{w.Top:0})");

        w.Close();
        Pump();
    }

    /// <summary>
    /// Панель профиля между играми целиком вписывается в экран — в тот, где
    /// стоит, а не в основной. Если открыт клиент League, панель приклеивается
    /// к нему, и проверять нечего.
    /// </summary>
    private static void PanelOnSecond()
    {
        Console.WriteLine();
        Console.WriteLine("панель профиля на втором мониторе");
        if (Process.GetProcessesByName("LeagueClientUx").Length > 0)
        {
            Console.WriteLine("  [—] клиент League открыт: панель приклеится к нему — пункт пропущен");
            return;
        }
        var w = Open();
        Use(w, [Primary, RightOf]);
        w.Left = 4300; w.Top = 150;
        w.UpdateRecommendations(null, null);         // драфта нет — панель профиля
        Pump();
        Check("панель осталась на втором мониторе", Near(w, 4300, 150), $"({w.Left:0};{w.Top:0})");

        w.Close();
        Pump();
    }

    /// <summary>
    /// Монитор, где запомнили окно, отключили. Защита прежняя: шапку
    /// вытягиваем на оставшийся монитор, иначе окно не достать мышью.
    /// </summary>
    private static void Unplugged()
    {
        Console.WriteLine();
        Console.WriteLine("второй монитор отключили");
        Remember(4200, 120);
        var w = Open();
        Use(w, [Primary]);

        w.UpdateRecommendations([Rec()], Aram(), null);
        Pump();
        var grab = w.Left <= Primary.Right - 219 && w.Left + w.ActualWidth >= Primary.Left + 219
                && w.Top >= Primary.Top - 1 && w.Top <= Primary.Bottom - 39;
        Check("шапка вернулась на оставшийся монитор", grab, $"({w.Left:0};{w.Top:0})");

        w.Close();
        Pump();
    }

    private static void Remember(double l, double t)
    {
        var s = AppSettings.Current;
        s.DraftPlacement = "remember";
        s.DraftLeft = l; s.DraftTop = t;
        s.DraftWidth = W; s.DraftHeight = H;
    }

    /// Подставить мониторы. Задаём в координатах окна, программа ждёт пиксели
    /// устройства — умножаем на масштаб экрана, на котором идёт проверка.
    private static void Use(Window w, Rect[] dip)
    {
        var dpi = VisualTreeHelper.GetDpi(w);
        Monitors.Override = dip.Select((r, i) => new MonitorArea(
            new Rect(r.X * dpi.DpiScaleX, r.Y * dpi.DpiScaleY,
                     r.Width * dpi.DpiScaleX, r.Height * dpi.DpiScaleY), i == 0)).ToList();
    }

    private static bool Near(Window w, double l, double t) =>
        Math.Abs(w.Left - l) < 2 && Math.Abs(w.Top - t) < 2;

    private static OverlayWindow Open()
    {
        var w = new OverlayWindow();
        ShowHidden(w);
        Pump();
        return w;
    }

    /// Окно невидимым: проверка не мигает им на рабочем столе.
    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        // Попиксельная прозрачность обязательна: без неё Opacity=0 красит окно
        // в чёрный. Менять можно только до Show.
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    private static Recommendation Rec() => new(
        ChampionId: 64, Score: 1.0, BaseDelta: 1.0, DirectDelta: 0, OtherDelta: 0,
        SynergyDelta: 0, ComfortDelta: 0, StyleDelta: 0, Reasons: ["проверка"]);

    /// Снимок драфта с кандидатами: ARAM проще всего — роли и враги не нужны,
    /// а путь до перерисовки и удержания окна на экране тот же.
    private static DraftState Aram()
    {
        var me = new DraftPlayer(0, 0, 0, "", true);
        return new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [64, 103, 1], IsAram: true,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    /// Прокрутить очередь WPF: UpdateRecommendations кладёт работу через Dispatcher.
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
