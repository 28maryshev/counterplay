using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// График профиля в нормалах и ARAM.
///
/// У этих очередей нет LP, а режим графика в настройках по умолчанию —
/// «рейтинг». Владелец 14 октября: в нормалах и ARAM вместо графика стояла
/// подсказка «сыграй несколько рейтинговых игр». Там график всегда по
/// винрейту, и считается он по играм своей очереди при показе, а не по
/// сохранённым при записи числам: игры, добитые из истории на новом
/// компьютере, сохраняли «сырой» винрейт, и линия начиналась со 100%.
///
/// И на месте эмблемы ранга в этих очередях — силуэт карты (Ущелье или
/// Бездна), а не пустота.
///
/// Своя папка (<c>AppPaths.RootOverride</c>), окно — невидимое.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Me = "TEST-queuechart-00000000000000000000000000000000000000000000000000";

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "QueueChart");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        try
        {
            var view = StoredPoints();
            if (view is not null) Window(view);
        }
        finally { try { Directory.Delete(AppPaths.RootOverride, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: в нормалах и ARAM график по винрейту своей очереди"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Точки графика ───────────────────────────────────────────────────

    /// <summary>
    /// Нормалы: шесть игр с «сырым» сохранённым винрейтом, как у добитых из
    /// истории (первая победа — 100%). ARAM: четыре игры — меньше порога.
    /// </summary>
    private static SessionTracker.SessionData? StoredPoints()
    {
        Console.WriteLine("точки графика:");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        JsonArray Games(bool[] wins, double[] wr) => new(wins.Select((w, i) => (JsonNode)new JsonObject
        {
            ["Ts"] = now - (wins.Length - i) * 3600, ["ChampionId"] = 1, ["Win"] = w, ["Wr"] = wr[i]
        }).ToArray());

        File.WriteAllText(Path.Combine(AppPaths.Root, "session.json"), new JsonObject
        {
            ["LastAccount"] = Me,
            ["Accounts"] = new JsonObject
            {
                [Me] = new JsonObject
                {
                    ["Nick"] = "проверка",
                    ["SelectedQueue"] = "normal",
                    ["Queues"] = new JsonObject
                    {
                        ["normal"] = new JsonObject
                        {
                            ["Games"] = Games([true, false, true, true, false, true],
                                             [100, 50, 66.7, 75, 60, 66.7])
                        },
                        ["aram"] = new JsonObject
                        {
                            ["Games"] = Games([true, false, true, false], [100, 50, 66.7, 50])
                        }
                    }
                }
            }
        }.ToJsonString());

        var view = SessionTracker.StoredView();
        if (view is null) { Check("журнал прочитался", false, "нет вида"); return null; }
        var normal = view.Queues["normal"].WinrateHistory.Select(p => p.Winrate).ToList();
        Check("в нормалах точка на каждую игру", normal.Count == 6, $"{normal.Count}");
        Check("первая победа — не 100%: винрейт считается заново, со сглаживанием",
              normal.Count > 0 && normal[0] is > 50 and < 60, normal.Count > 0 ? $"{normal[0]:F1}" : "—");
        Check("последняя точка — четыре победы из шести, со сглаживанием",
              normal.Count == 6 && Math.Abs(normal[5] - 100.0 * (4 + 3) / (6 + 6)) < 0.01,
              normal.Count == 6 ? $"{normal[5]:F1}" : "—");
        var aram = view.Queues["aram"].WinrateHistory;
        Check("в ARAM меньше пяти игр — графика ещё нет", aram.Count == 0, $"{aram.Count}");
        return view;
    }

    // ── 2. Окно ────────────────────────────────────────────────────────────

    /// <summary>
    /// Режим графика «рейтинг», как по умолчанию. В соло без истории LP —
    /// подсказка про рейтинговые игры; в нормалах — линия винрейта; в ARAM
    /// подсказка под последними играми не обещает LP.
    /// </summary>
    private static void Window(SessionTracker.SessionData stored)
    {
        Console.WriteLine("\nокно:");
        AppSettings.Current.ChartMode = "rating";
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            w.ShowReady(Loc.T("status.readyIdle"));
            var chart = (Canvas)w.FindName("WrChart");
            var hint = (TextBlock)w.FindName("SessionHint");

            var map = (System.Windows.Controls.Image)w.FindName("QueueMap");
            var box = (FrameworkElement)w.FindName("QueueMapBox");
            var lpBar = (FrameworkElement)w.FindName("RankProgressTrack");

            Show(w, stored, "normal");
            Check("нормалы — на месте эмблемы силуэт Ущелья",
                  box.Visibility == Visibility.Visible && MapUri(map).EndsWith("/maps/rift.png")
                  && map.Source is { Width: > 0 }, MapUri(map));
            Check("нормалы — без полосы LP", lpBar.Visibility == Visibility.Collapsed, $"{lpBar.Visibility}");
            Check("нормалы при режиме «рейтинг» — линия винрейта",
                  chart.Children.OfType<System.Windows.Shapes.Polyline>().Any(), Texts(chart));
            Check("нормалы — без подсказки про рейтинговые игры",
                  !Texts(chart).Contains(Loc.T("chart.noRating")), Texts(chart));

            Show(w, stored, "aram");
            Check("ARAM — силуэт Бездны",
                  box.Visibility == Visibility.Visible && MapUri(map).EndsWith("/maps/abyss.png")
                  && map.Source is { Width: > 0 }, MapUri(map));
            Check("ARAM — без полосы LP", lpBar.Visibility == Visibility.Collapsed, $"{lpBar.Visibility}");
            Check("ARAM, игр мало — подсказка не обещает LP",
                  hint.Visibility == Visibility.Visible && hint.Text == Loc.T("session.needGamesWr"), hint.Text);

            var solo = new SessionTracker.QueueView(true, "Emerald", "II", 41, 41, 197, 186, 51.4, [], [], []);
            Show(w, new SessionTracker.SessionData("проверка", "solo",
                new Dictionary<string, SessionTracker.QueueView> { ["solo"] = solo }), "solo");
            Check("соло без истории LP — подсказка про рейтинговые игры, как и было",
                  Texts(chart).Contains(Loc.T("chart.noRating")), Texts(chart));
            Check("соло с рангом — эмблема, силуэта карты нет", box.Visibility == Visibility.Collapsed, "");
            Check("соло с рангом — полоса LP на месте", lpBar.Visibility == Visibility.Visible, $"{lpBar.Visibility}");

            // Соло без ранга (отборочные не сыграны): скелетон эмблемы, а не карта.
            var skel = (FrameworkElement)w.FindName("RankEmblemSkel");
            var none = new SessionTracker.QueueView(false, "", "", 0, 0, 0, 0, 0, [], [], []);
            Show(w, new SessionTracker.SessionData("проверка", "solo",
                new Dictionary<string, SessionTracker.QueueView> { ["solo"] = none }), "solo");
            Check("соло без ранга — скелетон эмблемы формы эмблемы",
                  skel.Visibility == Visibility.Visible && skel.GetValue(UIElement.OpacityMaskProperty) is System.Windows.Media.ImageBrush,
                  $"{skel.Visibility}");
            Check("соло без ранга — ни карты, ни полосы LP",
                  box.Visibility == Visibility.Collapsed && lpBar.Visibility == Visibility.Collapsed, "");

            // Данных нет вовсе (только поставили программу): тот же силуэт эмблемы,
            // а не квадратная плашка — «без игр» и «без ранга» выглядели по-разному.
            w.ShowSession(null);
            for (var i = 0; i < 10; i++) Pump();
            Check("профиль без данных — тот же скелетон эмблемы", skel.Visibility == Visibility.Visible,
                  $"{skel.Visibility}"); 
        }
        finally { w.Close(); }
    }

    private static void Show(OverlayWindow w, SessionTracker.SessionData d, string queue)
    {
        w.ShowSession(d with { SelectedQueue = queue });
        // Выбранная очередь живёт в окне; переключаем так же, как список очередей.
        typeof(OverlayWindow).GetField("_selectedQueue",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(w, queue);
        w.ShowSession(d with { SelectedQueue = queue });
        for (var i = 0; i < 10; i++) Pump();
    }

    /// Откуда взята картинка силуэта: пустой источник на пустой ресурс не меняем
    /// — так проверка однажды прошла, когда картинки в сборку ещё не попали.
    private static string MapUri(System.Windows.Controls.Image i) =>
        (i.Source as System.Windows.Media.Imaging.BitmapImage)?.UriSource?.ToString() ?? "нет";

    private static string Texts(Canvas c) =>
        string.Join(" | ", c.Children.OfType<TextBlock>().Select(t => t.Text));

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
