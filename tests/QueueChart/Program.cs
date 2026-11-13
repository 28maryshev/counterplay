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
/// Владелец 13 ноября, второй компьютер: восемь нормалов из истории (июнь —
/// июль), а вместо графика «график появится с пятой игры». Окно графика — три
/// месяца, и все игры из него выпадали. Теперь окно не уже пяти последних игр.
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
            var old = OldPoints();
            if (view is not null && old is not null) Window(view, old);
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
                },
                [Old] = OldAccount(now)
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

    // ── 1б. Старые игры ────────────────────────────────────────────────────

    private const string Old = "TEST-queuechart-old-000000000000000000000000000000000000000000000000";
    private const long Day = 86400;

    /// <summary>
    /// Второй аккаунт — как у владельца 13 ноября: нормалы только полугодовой
    /// давности (две победы, шесть поражений). ARAM — вернулся после перерыва:
    /// шесть старых игр и одна вчерашняя.
    /// </summary>
    private static JsonObject OldAccount(long now)
    {
        JsonArray Games(bool[] wins, Func<int, long> ts) => new(wins.Select((w, i) => (JsonNode)new JsonObject
        {
            ["Ts"] = ts(i), ["ChampionId"] = 1, ["Win"] = w, ["Wr"] = 0
        }).ToArray());
        return new JsonObject
        {
            ["Nick"] = "давно",
            ["SelectedQueue"] = "normal",
            ["Queues"] = new JsonObject
            {
                ["normal"] = new JsonObject
                {
                    ["Games"] = Games([false, false, true, false, false, true, false, false],
                                      i => now - 160 * Day + i * 3600)
                },
                ["aram"] = new JsonObject
                {
                    ["Games"] = Games([true, false, true, true, false, true, false],
                                      i => i < 6 ? now - 150 * Day + i * 3600 : now - Day)
                }
            }
        };
    }

    private static SessionTracker.SessionData? OldPoints()
    {
        Console.WriteLine("\nигры старше окна графика:");
        var view = SessionTracker.StoredView(Old);
        if (view is null) { Check("журнал второго аккаунта прочитался", false, "нет вида"); return null; }
        var normal = view.Queues["normal"].WinrateHistory;
        Check("нормалы, все игры старше трёх месяцев — на графике последние пять, а не пусто",
              normal.Count == SessionTracker.MinGamesForChart, $"{normal.Count}");
        Check("нормалы — последняя точка графика это последняя игра",
              normal.Count > 0 && normal[^1].Date < DateTime.Now.AddDays(-100), normal.Count > 0 ? $"{normal[^1].Date:d}" : "—");
        var aram = view.Queues["aram"].WinrateHistory;
        Check("ARAM, одна свежая игра после перерыва — линия из пяти точек, а не одна точка",
              aram.Count == SessionTracker.MinGamesForChart, $"{aram.Count}");
        return view;
    }

    // ── 2. Окно ────────────────────────────────────────────────────────────

    /// <summary>
    /// Режим графика «рейтинг», как по умолчанию. В соло без истории —
    /// подсказка одна, под играми, а на месте графика ровная заглушка (с 24.10;
    /// в жизни при известном ранге там точка — tests/SidebarProfiles); в
    /// нормалах — линия винрейта; в ARAM подсказка под последними играми не
    /// обещает LP.
    /// </summary>
    private static void Window(SessionTracker.SessionData stored, SessionTracker.SessionData old)
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
            Check("нормалы — на месте эмблемы значок Ущелья: уголки топа и бота и стрелки мида",
                  box.Visibility == Visibility.Visible && MapFigures(map) == 6, $"{MapFigures(map)} фигур");
            Check("нормалы — без полосы LP", lpBar.Visibility == Visibility.Collapsed, $"{lpBar.Visibility}");
            var rankText = (TextBlock)w.FindName("RankText");
            Check("нормалы — название очереди под значком, а не от края",
                  rankText.Margin.Left > 0, $"{rankText.Margin.Left:F0}");
            Check("нормалы при режиме «рейтинг» — линия винрейта",
                  chart.Children.OfType<System.Windows.Shapes.Polyline>().Any(), Texts(chart));
            Check("нормалы — без подсказки про рейтинговые игры",
                  !Texts(chart).Contains(Loc.T("chart.noRating")), Texts(chart));

            Show(w, stored, "aram");
            Check("ARAM — значок Бездны: только стрелки мида",
                  box.Visibility == Visibility.Visible && MapFigures(map) == 4, $"{MapFigures(map)} фигур");
            Check("ARAM — без полосы LP", lpBar.Visibility == Visibility.Collapsed, $"{lpBar.Visibility}");
            Check("ARAM, игр мало — подсказка не обещает LP",
                  hint.Visibility == Visibility.Visible && hint.Text == Loc.T("session.needGamesWr"), hint.Text);
            // Четыре игры из пяти: пятый слот ждёт игры, а не загрузки — блик
            // там читался как «вечно грузится» (владелец, 24.10).
            var empty = EmptyGameCells(w);
            Check("ARAM, 4 игры — пустой пятый слот последних игр ровный, без блика",
                  empty.Count == 1 && empty.All(b => b is System.Windows.Media.SolidColorBrush),
                  string.Join(", ", empty.Select(b => b.GetType().Name)));

            var solo = new SessionTracker.QueueView(true, "Emerald", "II", 41, 41, 197, 186, 51.4, [], [], []);
            Show(w, new SessionTracker.SessionData("проверка", "solo",
                new Dictionary<string, SessionTracker.QueueView> { ["solo"] = solo }), "solo");
            Check("соло без истории — подсказка одна: под играми, а на месте графика заглушка без надписи",
                  hint.Visibility == Visibility.Visible && !Texts(chart).Contains(Loc.T("chart.noRating")),
                  $"{hint.Visibility}; {Texts(chart)}");
            Check("соло с рангом — эмблема, силуэта карты нет", box.Visibility == Visibility.Collapsed, "");
            Check("соло с рангом — полоса LP на месте", lpBar.Visibility == Visibility.Visible, $"{lpBar.Visibility}");
            Check("соло с рангом — название лиги от края", rankText.Margin.Left == 0, $"{rankText.Margin.Left:F0}");

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

            // Игры старше окна графика (второй компьютер, давний перерыв): линия
            // по последним играм, а не обещание «появится с пятой игры».
            AppSettings.Current.ChartDays = 30;
            Show(w, old, "normal");
            Check("нормалы, игры полугодовой давности — линия винрейта, а не заглушка",
                  Line(chart) == SessionTracker.MinGamesForChart, $"{Line(chart)} точек; {Texts(chart)}");
            Check("нормалы, игры полугодовой давности — без подсказки «появится с пятой игры»",
                  hint.Visibility == Visibility.Collapsed, hint.Text);
            Show(w, old, "aram");
            Check("ARAM, окно 30 дней и одна игра в нём — линия из пяти последних игр",
                  Line(chart) == SessionTracker.MinGamesForChart, $"{Line(chart)} точек");
            AppSettings.Current.ChartDays = 90;

            // Данных нет вовсе (только поставили программу): тот же силуэт эмблемы,
            // а не квадратная плашка — «без игр» и «без ранга» выглядели по-разному.
            w.ShowSession(null);
            for (var i = 0; i < 10; i++) Pump();
            Check("профиль без данных — тот же скелетон эмблемы", skel.Visibility == Visibility.Visible,
                  $"{skel.Visibility}");
            var loading = EmptyGameCells(w);
            Check("профиль без данных — пять слотов игр с бликом: данные ещё грузятся",
                  loading.Count == 5 && loading.All(b => b is System.Windows.Media.LinearGradientBrush),
                  string.Join(", ", loading.Select(b => b.GetType().Name)));
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
    /// Сколько фигур в значке карты: у Ущелья два уголка и две стрелки
    /// (наконечник и древко — отдельные фигуры), у Бездны — только стрелки.
    private static int MapFigures(System.Windows.Controls.Image i) =>
        i.Source is System.Windows.Media.DrawingImage { Drawing: System.Windows.Media.GeometryDrawing g }
            ? System.Windows.Media.PathGeometry.CreateFromGeometry(g.Geometry).Figures.Count : 0;

    /// Заливки пустых слотов «последних игр»: у настоящей ячейки рамка без фона,
    /// у пустой — плашка-заглушка.
    private static List<System.Windows.Media.Brush> EmptyGameCells(OverlayWindow w) =>
        ((Grid)w.FindName("Last5Panel")).Children.OfType<StackPanel>()
            .Select(sp => sp.Children.Count > 0 ? sp.Children[0] as Border : null)
            .Where(b => b?.Background is not null)
            .Select(b => b!.Background)
            .ToList();

    /// Сколько точек в линии графика (0 — линии нет).
    private static int Line(Canvas c) =>
        c.Children.OfType<System.Windows.Shapes.Polyline>().Select(p => p.Points.Count).DefaultIfEmpty(0).Max();

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
