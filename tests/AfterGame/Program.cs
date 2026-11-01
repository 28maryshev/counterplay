using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// После игры окно программы не вылезает поверх экрана «Победа».
///
/// Владелец (01.11.2026): «сайдбар начал отображаться сразу после надписи
/// „победа“ в конце игры, ещё и по центру так, что невозможно нажать Continue».
/// В журнале программы — после каждой игры в одну секунду «игра закончилась» и
/// «вернулся из трея (автоматически)», а окно стоит там, где его оставили
/// (1042;357), — почти посередине экрана 2560.
///
/// Причина: когда взрывается нексус, клиент уже переходит в фазу
/// WaitingForStats / PreEndOfGame, а программа считала любую фазу, кроме
/// драфта и самой игры, «меню» и сразу выходила из трея. Окно игры с кнопкой
/// «Продолжить» в этот момент ещё на экране, и окно программы — поверх него.
///
/// Проверка ставит подставные окна Win32 с классами клиента и игры
/// (прозрачные, поверх всех, сквозь них проходят клики), проводит невидимое
/// окно программы через вход в игру и её конец и смотрит, когда оно вернулось
/// из трея. Значок в трее на миг появляется — его тут же убираем.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static readonly MethodInfo Follow = typeof(OverlayWindow)
        .GetMethod("FollowClientWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo Tray = typeof(OverlayWindow)
        .GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AfterGame");
        Try(() => Directory.Delete(root, true));
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        DataDb.DirOverride = Path.Combine(root, "db");
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        // Настоящая игра на экране — её окно не отличить от подставного, а
        // закрыть его проверка не может.
        if (Process.GetProcessesByName("League of Legends").Length > 0)
        {
            Console.WriteLine("идёт настоящая игра — проверять нечем");
            return 0;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new OverlayWindow();
        try { Run(w); }
        finally { w.Close(); Fakes.CloseAll(); app.Shutdown(); Try(() => Directory.Delete(root, true)); }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: после игры окно ждёт, пока закроется игра"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run(OverlayWindow w)
    {
        // Клиент открыт и подключён, окно программы — рядом с ним.
        ShowHidden(w);
        w.SetLcuReady(true);
        Fakes.Create("RCLIENT", "League of Legends", 100, 100, 1280, 720);
        Tick(w);
        Check("0. до игры окно на экране", !w.InTray && w.IsVisible, State(w));

        // ── 1. Игра началась: окно в трее, поверх клиента — окно игры ──────
        w.SetGameActive(true);
        w.HideToTray();
        Pump(); HideTrayIcon(w);
        var game = Fakes.Create("RiotWindowClass", "League of Legends (TM) Client", 0, 0, 2560, 1440);
        Tick(w);
        Check("1. идёт игра — окно в трее", w.InTray && !w.IsVisible, State(w));

        // ── 2. Нексус взорван: «Победа», клиент уже в WaitingForStats ──────
        // Так делает обработчик геймфлоу на каждой фазе, кроме драфта и игры.
        for (var phase = 0; phase < 3; phase++)   // WaitingForStats, PreEndOfGame, EndOfGame
        {
            w.SetGameActive(false);
            w.RestoreFromTray();
            Pump();
            Tick(w);   // слежение за клиентом тоже умеет вернуть окно из трея
        }
        Check("2. «Победа» на экране — окно не вылезает поверх игры", w.InTray && !w.IsVisible,
              State(w) + " после WaitingForStats → PreEndOfGame → EndOfGame и трёх тактов слежения");

        // ── 3. Нажали «Продолжить»: окно игры закрылось, клиент на месте ───
        Fakes.Close(game);
        Tick(w);
        Check("3. игра закрылась — окно вернулось само", !w.InTray && w.IsVisible, State(w));

        // ── 4. Значок в трее во время «Победы» — окно возвращается сразу ───
        // Окно игры — до ухода в трей: таймер слежения тикает и сам, и без
        // игры на экране он вернул бы окно раньше просьбы.
        game = Fakes.Create("RiotWindowClass", "League of Legends (TM) Client", 0, 0, 2560, 1440);
        w.HideToTray();
        Pump(); HideTrayIcon(w);
        Tick(w);
        Check("4. игра открыта — само окно не возвращается", w.InTray && !w.IsVisible, State(w));
        w.RestoreFromTray(force: true);
        Pump();
        Check("   просьба из трея — окно на экране и при открытой игре", !w.InTray && w.IsVisible, State(w));
        Fakes.Close(game);
    }

    private static string State(OverlayWindow w) =>
        $"{(w.InTray ? "в трее" : "не в трее")}, окно {(w.IsVisible ? "показано" : "скрыто")}";

    private static void Tick(OverlayWindow w)
    {
        for (var i = 0; i < 3; i++) { Follow.Invoke(w, null); Pump(); }
    }

    private static void HideTrayIcon(OverlayWindow w)
    {
        if (Tray.GetValue(w) is System.Windows.Forms.NotifyIcon icon) icon.Visible = false;
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

    private static void Try(Action a) { try { a(); } catch { /* нечего чистить */ } }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what} — {detail}");
        if (!ok) _fails++;
    }
}
