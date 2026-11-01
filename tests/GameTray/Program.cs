using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Трей вокруг игры: окно не мелькает на входе в игру и не вылезает поверх
/// экрана «Победа».
///
/// **Конец игры.** Владелец (01.11.2026): «сайдбар начал отображаться сразу
/// после надписи „победа“ в конце игры, ещё и по центру так, что невозможно
/// нажать Continue». В журнале программы — после каждой игры в одну секунду
/// «игра закончилась» и «вернулся из трея (автоматически)», окно — на месте
/// драфта (1042;357), почти посередине экрана 2560. Когда взрывается нексус,
/// клиент уже в WaitingForStats / PreEndOfGame, а программа считала любую
/// фазу, кроме драфта и самой игры, «меню» и сразу выходила из трея.
///
/// **Вход в игру.** В том же журнале на каждом входе: «свернулся в трей» за
/// 5 с до конца финализации, через 5 с «игра закончилась / вернулся из трея
/// (автоматически)» и в ту же секунду снова «игра началась / свернулся в
/// трей». Финализация кончается, клиент шлёт событие драфта с фазой таймера
/// GAME_STARTING, и «не финализация» значило «драфт снова идёт»: снималось
/// «идёт игра», и слежение за клиентом показывало окно, пока игра ещё не
/// открыла своё.
///
/// Проверка ставит подставные окна Win32 с классами клиента и игры
/// (прозрачные, поверх всех, сквозь них проходят клики) и проводит невидимое
/// окно программы через драфт (<see cref="DraftTray"/> — тот же, что зовёт
/// цикл событий), игру, её конец и следующий драфт. Значок в трее на миг
/// появляется — его тут же гасим.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static readonly MethodInfo Follow = typeof(OverlayWindow)
        .GetMethod("FollowClientWindow", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo Tray = typeof(OverlayWindow)
        .GetField("_tray", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private const string GameClass = "RiotWindowClass", GameTitle = "League of Legends (TM) Client";

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "GameTray");
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
        Console.WriteLine(_fails == 0 ? "ИТОГ: вокруг игры окно не мелькает и не вылезает поверх неё"
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
        Check("0. до драфта окно на экране", !w.InTray && w.IsVisible, State(w));
        var draft = new DraftTray(w, CancellationToken.None);

        Console.WriteLine("вход в игру:");
        // ── 1. Драфт идёт: объявление, баны и пики ────────────────────────
        draft.OnTimer("PLANNING", 30_000);
        draft.OnTimer("BAN_PICK", 30_000);
        Tick(w);
        Check("1. идёт драфт — окно на экране, игра не идёт", !w.InTray && w.IsVisible && !w.GameActive,
              State(w));

        // ── 2. Финализация: за 5 секунд до конца — в трей ─────────────────
        // Осталось 5,6 с — скрытие через 0,6 с. Клиент шлёт событие за
        // событием, и все с той же фазой.
        draft.OnTimer("FINALIZATION", 5_600);
        draft.OnTimer("FINALIZATION", 5_400);
        WaitFor(() => w.InTray, 3000);
        HideTrayIcon(w);
        Check("2. за 5 с до конца финализации — в трей", w.InTray && !w.IsVisible && w.GameActive, State(w));

        // ── 3. Финализация кончилась: GAME_STARTING, окна игры ещё нет ─────
        draft.OnTimer("GAME_STARTING", 0);
        Pump();
        Tick(w);
        Check("3. GAME_STARTING до окна игры — окно не мелькает", w.InTray && !w.IsVisible && w.GameActive,
              State(w));
        // Фаза, которой программа не знает (или событие без таймера), после
        // финализации — тоже не «драфт снова идёт».
        draft.OnTimer("", 0);
        Pump();
        Tick(w);
        Check("   незнакомая фаза после финализации — тоже", w.InTray && !w.IsVisible && w.GameActive,
              State(w));

        // ── 4. Игра началась: геймфлоу GameStart, драфт удалён, окно игры ──
        w.SetGameActive(true);
        w.HideToTray();
        draft.Ended();
        var game = Fakes.Create(GameClass, GameTitle, 0, 0, 2560, 1440);
        Pump(); HideTrayIcon(w);
        Tick(w);
        Check("4. идёт игра — окно в трее", w.InTray && !w.IsVisible, State(w));

        Console.WriteLine("конец игры:");
        // ── 5. Нексус взорван: «Победа», клиент уже в WaitingForStats ──────
        // Так делает обработчик геймфлоу на каждой фазе, кроме драфта и игры.
        for (var phase = 0; phase < 3; phase++)   // WaitingForStats, PreEndOfGame, EndOfGame
        {
            w.SetGameActive(false);
            w.RestoreFromTray();
            draft.Ended();
            Pump();
            Tick(w);   // слежение за клиентом тоже умеет вернуть окно из трея
        }
        Check("5. «Победа» на экране — окно не вылезает поверх игры", w.InTray && !w.IsVisible,
              State(w) + " после WaitingForStats → PreEndOfGame → EndOfGame и трёх тактов слежения");

        // ── 6. Нажали «Продолжить»: окно игры закрылось, клиент на месте ───
        Fakes.Close(game);
        Tick(w);
        Check("6. игра закрылась — окно вернулось само", !w.InTray && w.IsVisible, State(w));

        // ── 7. Значок в трее во время «Победы» — окно возвращается сразу ───
        // Окно игры — до ухода в трей: таймер слежения тикает и сам, и без
        // игры на экране он вернул бы окно раньше просьбы.
        game = Fakes.Create(GameClass, GameTitle, 0, 0, 2560, 1440);
        w.HideToTray();
        Pump(); HideTrayIcon(w);
        Tick(w);
        Check("7. игра открыта — само окно не возвращается", w.InTray && !w.IsVisible, State(w));
        w.RestoreFromTray(force: true);
        Pump();
        Check("   просьба из трея — окно на экране и при открытой игре", !w.InTray && w.IsVisible, State(w));
        Fakes.Close(game);

        Console.WriteLine("следующий драфт:");
        // ── 8. Закрыл окно крестиком в меню — новый драфт его вернёт ───────
        w.HideToTray(userInitiated: true);
        Pump(); HideTrayIcon(w);
        Tick(w);
        var closed = w.InTray;
        draft.OnTimer("BAN_PICK", 30_000);
        Tick(w);
        Check("8. закрыто крестиком — новый драфт возвращает окно", closed && !w.InTray && w.IsVisible,
              $"после крестика {(closed ? "в трее" : "НЕ в трее")}, на драфте {State(w)}");

        // ── 9. Додж на финализации — скрытие отменено, окно остаётся ──────
        draft.OnTimer("FINALIZATION", 5_600);
        draft.Ended();   // драфт удалён: кто-то вышел
        WaitFor(() => w.InTray, 1500);
        Check("9. додж на финализации — окно не уходит в трей", !w.InTray && w.IsVisible && !w.GameActive,
              State(w));
    }

    private static string State(OverlayWindow w) =>
        $"{(w.InTray ? "в трее" : "не в трее")}, окно {(w.IsVisible ? "показано" : "скрыто")}, " +
        $"{(w.GameActive ? "«идёт игра»" : "игра не идёт")}";

    private static void Tick(OverlayWindow w)
    {
        for (var i = 0; i < 3; i++) { Follow.Invoke(w, null); Pump(); }
    }

    private static void WaitFor(Func<bool> done, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < ms) Pump();
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
