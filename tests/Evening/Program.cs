using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Вечер на экране профиля (владелец 14.11, по разбору подкаста про «losers
/// queue»: игроки сливают очки длинными вечерами, а винят подбор).
///
/// 1. Счёт: вечер — игры с перерывами меньше часа; первые три игры вечера
///    против остальных; игры сразу после двух поражений подряд в том же вечере
///    против прочих; идёт ли вечер сейчас, какая игра следующая, сколько
///    поражений подряд в его конце.
/// 2. Блок: виден, только если включён и в обеих частях вечера по 20 игр;
///    совет — по тому, где игрок сейчас: после серии поражений и правда
///    проигрывает чаще — «сделай перерыв», не проигрывает — спокойная строка
///    «дело не в подборе»; пятая игра вечера при слабой второй половине —
///    «может, перерыв?».
///
/// Своя папка, окно настоящее, но невидимое.
/// </summary>
internal static class Program
{
    private static int _fails;
    private const long H = 3600;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "Evening");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        try
        {
            Count();
            Window();
        }
        finally { try { Directory.Delete(AppPaths.RootOverride, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: вечер считается по журналу и честно подсказывает"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Счёт ────────────────────────────────────────────────────────────

    /// Вечер из исходов: игры по 30 минут, между ними 10 минут.
    private static List<(long Start, long End, bool Win)> Night(long from, params bool[] wins) =>
        [.. wins.Select((w, i) => (from + i * 40 * 60, from + i * 40 * 60 + 30 * 60, w))];

    private static void Count()
    {
        Console.WriteLine("счёт:");
        var day = 1_790_000_000L;
        // Вечер 1: В П П В П (после двух поражений — 4-я игра, победа).
        // Вечер 2, через сутки: П П П В (после двух — 3-я и 4-я: поражение, победа).
        var games = Night(day, true, false, false, true, false)
            .Concat(Night(day + 24 * H, false, false, false, true)).ToList();
        var e = SessionTracker.EveningOf(games, day + 30 * 24 * H);
        Check("первые три игры каждого вечера — 6, остальные — 3",
              e.Early.Games == 6 && e.Late.Games == 3, $"{e.Early} / {e.Late}");
        Check("побед в начале вечера 1 (В в первом, ни одной во втором), дальше 2",
              e.Early.Wins == 1 && e.Late.Wins == 2, $"{e.Early.Wins} / {e.Late.Wins}");
        Check("после двух поражений подряд — 3 игры, из них 2 победы",
              e.AfterTwo == (3, 2), $"{e.AfterTwo}");
        Check("прочие — 6 игр, 1 победа", e.Rest == (6, 1), $"{e.Rest}");
        Check("месяц спустя вечер не идёт: следующая игра — первая, счёт 0–0",
              e.Game == 1 && e.Wins == 0 && e.Losses == 0 && e.LossStreak == 0, $"{e}");

        // Перерыв ровно в час — тот же вечер, больше часа — новый. Три игры подряд,
        // четвёртая — после перерыва: в том же вечере она «дальше», в новом — «начало».
        List<(long, long, bool)> After(long gap)
        {
            var three = Night(day, true, true, true);
            var start = three[^1].End + gap;
            return [.. three, (start, start + 30 * 60, true)];
        }
        var hour = SessionTracker.EveningOf(After(H), day + 10 * H);
        Check("перерыв ровно час — тот же вечер (четвёртая игра — «дальше»)",
              hour.Early.Games == 3 && hour.Late.Games == 1, $"{hour.Early} / {hour.Late}");
        var more = SessionTracker.EveningOf(After(H + 60), day + 10 * H);
        Check("перерыв больше часа — новый вечер (четвёртая — снова «начало»)",
              more.Early.Games == 4 && more.Late.Games == 0, $"{more.Early} / {more.Late}");

        // Вечер идёт: последняя игра кончилась 20 минут назад.
        var now = Night(day, true, false, false);
        var going = SessionTracker.EveningOf(now, now[^1].End + 20 * 60);
        Check("идёт вечер: следующая — 4-я, счёт 1–2, два поражения подряд",
              going.Game == 4 && going.Wins == 1 && going.Losses == 2 && going.LossStreak == 2, $"{going}");
    }

    // ── 2. Блок на экране профиля ─────────────────────────────────────────

    private static SessionTracker.SessionData Data(SessionTracker.Evening e) =>
        new("я", "solo", new Dictionary<string, SessionTracker.QueueView>(), e);

    private static void Window()
    {
        Console.WriteLine("\nблок:");
        AppSettings.Current.ReadyEvening = true;
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            var box = (FrameworkElement)w.FindName("EveningBox");
            var now = (TextBlock)w.FindName("EveningNow");
            var split = (TextBlock)w.FindName("EveningSplit");
            var after = (TextBlock)w.FindName("EveningAfter");
            var advice = (TextBlock)w.FindName("EveningAdvice");
            string Shown() => $"{now.Text} | {split.Text} | {after.Text} | {(advice.Visibility == Visibility.Visible ? advice.Text : "—")}";

            // Мало игр: в части «дальше» 12 — блока нет.
            Show(w, new(1, 0, 0, 0, (60, 30), (12, 6), (20, 10), (52, 26)));
            Check("в одной из частей меньше 20 игр — блока нет", box.Visibility == Visibility.Collapsed, $"{box.Visibility}");

            // После двух поражений игрок и правда проигрывает чаще: 35% против 52%.
            Show(w, new(4, 1, 2, 2, (90, 47), (40, 20), (40, 14), (90, 53)));
            Check("блок виден", box.Visibility == Visibility.Visible, Shown());
            Check("строка вечера: игра 4 · 1–2", now.Text == "Вечер: игра 4 · 1–2", now.Text);
            Check("доли: начало 52%, дальше 50%", split.Text.Contains("52%") && split.Text.Contains("50%"), split.Text);
            Check("после двух поражений — 35% (обычно 59%)", after.Text.Contains("35%") && after.Text.Contains("59%"), after.Text);
            Check("сейчас серия, и после неё он проигрывает — «сделай перерыв»",
                  advice.Visibility == Visibility.Visible && advice.Text.Contains("перерыв") && advice.Text.Contains("35%"), Shown());

            // После двух поражений — как обычно: 50% против 51%.
            Show(w, new(3, 0, 2, 2, (90, 46), (40, 20), (40, 20), (90, 46)));
            Check("серия, но после неё он играет как обычно — спокойная строка «дело не в подборе»",
                  advice.Visibility == Visibility.Visible && advice.Text.Contains("подбор"), Shown());

            // Пятая игра вечера, а дальше в вечер он выигрывает реже: 40% против 56%.
            Show(w, new(5, 2, 2, 1, (90, 50), (40, 16), (30, 15), (100, 52)));
            Check("пятая игра при слабой второй половине вечера — «может, перерыв?»",
                  advice.Visibility == Visibility.Visible && advice.Text.Contains("реже") && advice.Text.Contains("40%"), Shown());

            // Начало вечера — советовать нечего.
            Show(w, new(2, 1, 0, 0, (90, 50), (40, 16), (30, 15), (100, 52)));
            Check("вторая игра вечера — без совета", advice.Visibility == Visibility.Collapsed, Shown());

            // Снимок для глаз: CP_SNAP — папка для PNG (экран профиля с блоком).
            if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 } snap)
            {
                w.ShowReady(Loc.T("status.readyIdle"));
                Show(w, new(4, 1, 2, 2, (90, 47), (40, 20), (40, 14), (90, 53)));
                w.Opacity = 1;
                for (var i = 0; i < 10; i++) Pump();
                Snap(w, snap, "evening.png");
                w.Opacity = 0;
            }

            // Выключено в настройках — блока нет, хоть игр и хватает.
            AppSettings.Current.ReadyEvening = false;
            Show(w, new(5, 2, 2, 1, (90, 50), (40, 16), (30, 15), (100, 52)));
            Check("выключено в настройках — блока нет", box.Visibility == Visibility.Collapsed, $"{box.Visibility}");
            AppSettings.Current.ReadyEvening = true;
        }
        finally { w.Close(); }
    }

    private static void Show(OverlayWindow w, SessionTracker.Evening e)
    {
        w.ShowSession(Data(e));
        for (var i = 0; i < 10; i++) Pump();
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96,
                                                                     System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
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
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
