using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Вход в драфт из сайдбара.
///
/// Владелец снял на видео (30.10.2026, 180 кадров в секунду): игра найдена,
/// окно растягивается до размера драфта, и почти полсекунды в большом окне
/// висит панель профиля — «Игра найдена — подтверди», остальное пустое. Потом
/// появляется драфт.
///
/// Причина: окно меняет размер мгновенно, и WPF тут же перерисовывает его тем,
/// что в нём сейчас стоит. А размер менялся первым — до того, как сайдбар
/// уступил место драфту, и до долгой сборки подбора.
///
/// Проверка ловит каждое изменение размера окна (WM_SIZE) на входе в драфт и
/// смотрит, что в этот момент на экране. Нет базы — проверять нечего.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "DraftEnter");
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечем"); return 0; }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        // Раскладка как у владельца: запомненная, 1390×923.
        var s = AppSettings.Current;
        s.DraftPlacement = "remember";
        s.DraftWidth = 1390; s.DraftHeight = 923;
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Enter("пики", engine, (w, d) => w.UpdateRecommendations(engine.Recommend(d, s.DraftCount), d, engine),
              Scene(banPhase: false));
        Enter("баны", engine, (w, d) => w.UpdateBans(engine.RecommendBans(d), d, engine),
              Scene(banPhase: true));

        app.Shutdown();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: в окне драфтового размера панели профиля нет"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private sealed record Frame(int Width, int Height, bool Idle, bool Draft);

    private static void Enter(string what, RecommendationEngine engine,
                              Action<OverlayWindow, DraftState> start, DraftState draft)
    {
        Console.WriteLine($"вход в драфт ({what}):");
        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        w.DraftEnded();   // клиент в лобби — окно в виде сайдбара
        Pump();

        var idle  = (UIElement)w.FindName("IdlePanel");
        var full  = (UIElement)w.FindName("FullView");
        Check("до драфта — панель профиля", idle.IsVisible, $"ширина {w.ActualWidth:0}");
        var sidebarW = w.ActualWidth;

        // Каждое изменение размера: что видно в окне в этот момент.
        var frames = new List<Frame>();
        var src = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)!;
        src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0005 /* WM_SIZE */)
            {
                var v = lp.ToInt64();
                frames.Add(new Frame((int)(v & 0xFFFF), (int)((v >> 16) & 0xFFFF),
                                     idle.Visibility == Visibility.Visible,
                                     full.Visibility == Visibility.Visible));
            }
            return IntPtr.Zero;
        });

        start(w, draft);
        Pump();

        var dpi = VisualTreeHelperDpi(w);
        var grown = frames.Where(f => f.Width / dpi > sidebarW + 50).ToList();
        Check("окно выросло до драфта", grown.Count > 0,
              $"изменений размера {frames.Count}, ширина {w.ActualWidth:0}");
        var bad = grown.FirstOrDefault(f => f.Idle || !f.Draft);
        Check("в большом окне ни разу не было панели профиля или пустоты", bad is null,
              bad is null ? $"{grown.Count} изменений размера, всё время вид драфта"
                          : $"{bad.Width}×{bad.Height}: панель профиля {(bad.Idle ? "видна" : "нет")}, " +
                            $"вид драфта {(bad.Draft ? "виден" : "нет")}");
        Check("после — вид драфта", full.IsVisible && !idle.IsVisible,
              $"драфт {(full.IsVisible ? "виден" : "нет")}, профиль {(idle.IsVisible ? "виден" : "нет")}");
        w.Close();
    }

    private static double VisualTreeHelperDpi(Window w) =>
        PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;

    // Соло/дуо, я — Браум в саппорте. В банах никто ещё не выбран.
    private static DraftState Scene(bool banPhase)
    {
        var my = new List<DraftPlayer>
        {
            new(0, banPhase ? 0 : 126, 0, "middle", false),
            new(1, banPhase ? 0 : 17,  0, "jungle", false),
            new(2, banPhase ? 0 : 222, 0, "bottom", false),
            new(3, banPhase ? 0 : 201, 0, "utility", true),
            new(4, banPhase ? 0 : 106, 0, "top", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, banPhase ? 0 : 238, 0, "", false), new(6, banPhase ? 0 : 157, 0, "", false),
            new(7, banPhase ? 0 : 893, 0, "", false), new(8, banPhase ? 0 : 203, 0, "", false),
            new(9, banPhase ? 0 : 555, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: banPhase, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: !banPhase);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
        }
    }

    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what} — {detail}");
        if (!ok) _fails++;
    }
}
