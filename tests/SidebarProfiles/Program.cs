using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Counterplay;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;
using Panel = System.Windows.Controls.Panel;
using Path = System.IO.Path;
using Rank = Counterplay.Tests.FakeLcu.Rank;

namespace Counterplay.Tests;

/// <summary>
/// Экран готовности (сайдбар) у разных игроков.
///
/// Владелец 24 октября: пройти профили — кто играет ARAM, нормалы, только
/// ранкед и так далее — и найти, где человеку мешает панель, которой он не
/// пользуется, да ещё и мерцает. Каждый игрок идёт настоящим путём первого
/// запуска: экран готовности, затем журнал из истории подставного клиента
/// (те же номера очередей, что в живом клиенте 24.10) и показ профиля.
///
/// Печатает по каждому игроку, какие блоки видны, что в них и что мерцает.
/// Держит то, о чём уже договорились: после прихода профиля пустые слоты
/// последних игр и полоса «мои чемпионы» не мерцают; полосы нет, если игр с
/// драфтом меньше трёх из последних двадцати. Снимки — в папку из CP_SNAP
/// (по игроку и общий лист <c>all.png</c>).
/// </summary>
internal static class Program
{
    private static int _fails;
    private static readonly List<(string Name, BitmapSource Shot)> Shots = [];

    // Номера очередей из живого клиента (lol-game-queues, 24.10.2026).
    private const int Draft = 400, Solo = 420, Flex = 440, Aram = 450, Swift = 480,
                      Arena = 1750, Mayhem = 2400;

    private sealed record Player(
        string Name, string? Puuid,
        (int Queue, int Count, double DaysAgo)[] Games,
        Dictionary<string, Rank>? Ranks = null);

    private static readonly Rank NoRank = new("", "", 0, 0, 0);

    private static readonly Player[] Players =
    [
        new("не вошёл в аккаунт", null, []),
        new("соло, Золото II", "p-solo", [(Solo, 20, 0.2)],
            new() { ["RANKED_SOLO_5x5"] = new("GOLD", "II", 45, 60, 55) }),
        new("соло и флекс, Изумруд", "p-soloflex", [(Solo, 12, 0.2), (Flex, 8, 3)],
            new() { ["RANKED_SOLO_5x5"] = new("EMERALD", "III", 12, 80, 74),
                    ["RANKED_FLEX_SR"] = new("PLATINUM", "I", 70, 30, 22) }),
        new("только нормалы (Draft)", "p-draft", [(Draft, 20, 0.2)]),
        new("только Swiftplay", "p-swift", [(Swift, 20, 0.2)]),
        new("Swiftplay и немного соло", "p-swiftsolo", [(Swift, 15, 0.2), (Solo, 5, 2)],
            new() { ["RANKED_SOLO_5x5"] = new("SILVER", "I", 88, 20, 21) }),
        new("только ARAM", "p-aram", [(Aram, 20, 0.2)]),
        new("только ARAM: Mayhem", "p-mayhem", [(Mayhem, 20, 0.2)]),
        new("только Арена", "p-arena", [(Arena, 20, 0.2)]),
        new("ARAM 15 + Draft 5", "p-aramdraft", [(Aram, 15, 0.2), (Draft, 5, 2)]),
        new("отборочные: 3 соло + Swiftplay", "p-placements", [(Swift, 17, 0.2), (Solo, 3, 1)],
            new() { ["RANKED_SOLO_5x5"] = NoRank }),
        new("2 игры соло, новый аккаунт", "p-two", [(Solo, 2, 0.2)],
            new() { ["RANKED_SOLO_5x5"] = NoRank }),
        new("вернулся после перерыва", "p-break", [(Solo, 20, 50)],
            new() { ["RANKED_SOLO_5x5"] = new("GOLD", "IV", 0, 40, 44) }),
    ];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var baseDir = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SidebarProfiles");
        try { Directory.Delete(baseDir, true); } catch { }
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        var snap = Environment.GetEnvironmentVariable("CP_SNAP");

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            // Справочник и портреты — копией из папки игрока (только чтение), как в
            // дымовой: без них карточки стоят с бликом загрузки, и отчёт путает
            // «иконка не докачалась» с «слот мерцает зря». Загружаются один раз.
            var player = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
            AppPaths.RootOverride = Path.Combine(baseDir, "assets");
            CopyAssets(player, AppPaths.RootOverride);
            if (!DataDragon.LoadFromCache(Loc.DDragonLocale))
                DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
            IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            QueueRules();
            var i = 0;
            foreach (var p in Players)
            {
                AppPaths.RootOverride = Path.Combine(baseDir, $"{++i:00}");
                Directory.CreateDirectory(AppPaths.RootOverride);
                SessionTracker.DropCache();
                Run(p, i, snap);
            }
            if (snap is { Length: > 0 }) Sheet(snap);
        }
        finally { try { Directory.Delete(baseDir, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? $"ИТОГ: все правила сайдбара держатся у всех {Players.Length} игроков"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run(Player p, int n, string? snap)
    {
        Console.WriteLine($"\n── {n:00}. {p.Name}");
        var now = DateTimeOffset.UtcNow;
        var games = new List<(int, int, bool, DateTimeOffset)>();
        int[] champs = [86, 86, 86, 103, 103, 99, 22, 22, 37, 161, 412, 157];
        foreach (var (queue, count, daysAgo) in p.Games)
            for (var k = 0; k < count; k++)
                games.Add((queue, champs[(k + queue) % champs.Length], (k + queue) % 3 != 0,
                           now.AddDays(-daysAgo).AddMinutes(-40 * k - queue % 7)));

        using var lcu = new FakeLcu(p.Puuid, "игрок", games, p.Ranks);
        using var http = new LcuHttpClient(lcu.Credentials);

        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            w.ShowReady();
            Pump();
            var d = Task.Run(() => SessionTracker.RefreshAsync(http, CancellationToken.None)).GetAwaiter().GetResult();
            w.ShowSession(d);
            Pump();

            Report(w, d, p, main: true);
            if (snap is { Length: > 0 }) Shots.Add(($"{n:00}. {p.Name}", Shot(w, snap, $"{n:00}.png")));

            // Человек может переключить очередь — правила держатся в каждой.
            if (d is not null)
                foreach (var key in SessionTracker.QueueKeys)
                {
                    SelectedQueue.SetValue(w, key);
                    w.ShowSession(d);
                    Pump();
                    Report(w, d, p, main: false);
                }
        }
        finally { w.Close(); Pump(); }
    }

    // ── Отчёт по блокам ───────────────────────────────────────────────────

    /// Видно ли: сам и все предки до окна не скрыты. Само окно не в счёт: оно
    /// прячется и показывается вслед за окном клиента League, если тот открыт
    /// на этом компьютере, — к раскладке сайдбара это отношения не имеет.
    private static bool Seen(DependencyObject? d)
    {
        for (; d is not null and not Window; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private static readonly System.Reflection.FieldInfo SelectedQueue = typeof(OverlayWindow).GetField("_selectedQueue",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    /// main — вид, который игрок видит сам (очередь выбрана программой): его
    /// печатаем; остальные очереди — только правила.
    private static void Report(OverlayWindow w, SessionTracker.SessionData? d, Player p, bool main)
    {
        var selected = (string)SelectedQueue.GetValue(w)!;
        var where = main ? "" : $" [{selected}]";
        void Line(string text) { if (main) Console.WriteLine(text); }
        void Rule(Player? pl, string what, bool ok, string detail) => Program.Rule(pl, what + where, ok, detail);
        T Find<T>(string name) where T : class => (T)w.FindName(name);
        bool Shown(string name) => Seen(Find<FrameworkElement>(name));

        var queue = Find<TextBlock>("QueueText").Text.TrimEnd(' ', '▾');
        var rankArea = Shown("RankEmblem") ? "эмблема" : Shown("QueueMapBox") ? "значок карты"
                     : Shown("RankEmblemSkel") ? "силуэт эмблемы" : "пусто";
        Line($"  очередь: {queue}; ранг: {Find<TextBlock>("RankText").Text} {Find<TextBlock>("RankLpText").Text}".TrimEnd()
                          + $"; на месте эмблемы — {rankArea}; полоса LP {(Shown("RankProgressTrack") ? "есть" : "нет")}");

        var cells = Find<Grid>("Last5Panel").Children.OfType<StackPanel>()
            .Select(sp => sp.Children.Count > 0 ? sp.Children[0] as Border : null).ToList();
        var empty = cells.Count(b => b?.Background is not null);
        Line($"  последние игры: {cells.Count - empty} игр, {empty} пустых");
        var hintBlock = Find<TextBlock>("SessionHint");
        var hint = Seen(hintBlock) ? hintBlock.Text : null;
        if (hint is not null) Line($"  подсказка: «{hint}»");

        var chart = Find<Canvas>("WrChart");
        var chartTexts = chart.Children.OfType<TextBlock>().Select(t => t.Text).ToList();
        // Подсказка на месте графика — не подписи осей и дат, а надпись вместо линии.
        var chartHint = chartTexts.FirstOrDefault(t => t == Loc.T("chart.noRating"));
        var chartWhat = chart.Children.OfType<Polyline>().Any() ? "линия"
                      : chart.Children.OfType<Ellipse>().Any() ? "точка"
                      : chartHint is { } t ? $"надпись «{t}»"
                      : chart.Children.Count > 0 ? "заглушка" : "пусто";
        Line($"  винрейт: {Find<TextBlock>("SeasonWlText").Text} {Find<TextBlock>("WinrateBig").Text}; график — {chartWhat}");

        var pool = Shown("PoolButtonsRow");
        var strip = Shown("MyChampsBox");
        var stripCards = (Find<ItemsControl>("MyChampsStrip").ItemsSource as IEnumerable<MyChampCard>)?.ToList() ?? [];
        Line($"  кнопки пула {(pool ? "есть" : "нет")}; полоса чемпионов "
                          + (strip ? $"{stripCards.Count(c => c.WrSkeleton != Visibility.Visible)} + {stripCards.Count(c => c.WrSkeleton == Visibility.Visible)} пустых" : "скрыта"));

        var shimmer = Shimmering(w);
        var shimmerText = shimmer.Count == 0 ? "ничего" : string.Join(", ", shimmer.Select(kv => $"{kv.Key} ×{kv.Value}"));
        Line($"  мерцает: {shimmerText}");

        // ── Правила: обязаны выполняться у каждого игрока ──────────────────
        Rule(p, "подсказка одна: не под играми и на месте графика сразу",
             hint is null || chartHint is null, $"{hint} | {chartHint}");

        if (d is null)
        {
            Rule(p, "не вошёл в аккаунт — подсказка «войди в клиент»", hint == Loc.T("session.signIn"), hint ?? "нет подсказки");
            Rule(p, "не вошёл в аккаунт — ничего не мерцает: ждём человека, а не загрузку", shimmer.Count == 0, shimmerText);
            return;
        }

        Rule(p, "после прихода профиля пустые слоты последних игр не мерцают", !shimmer.ContainsKey("Last5Panel"), shimmerText);
        // Блик у карточки чемпиона — его портрет в загрузке; пустые слоты — без блика.
        Rule(p, "после прихода профиля пустые слоты полосы чемпионов не мерцают",
             stripCards.Where(c => c.WrSkeleton == Visibility.Visible).All(c => !ReferenceEquals(c.Fill, Skeleton.Shimmer)), "");

        var drafted = SessionTracker.RecentGamesIn(20, [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal]);
        Rule(p, "кнопки пула — только у играющих с драфтом (от 3 из последних 20)", pool == drafted >= 3, $"{drafted} игр с драфтом");
        if (drafted < 3) Rule(p, "без игр с драфтом полосы чемпионов нет", !strip, $"{drafted} игр с драфтом");

        if (main && p.Games.Any(g => SessionTracker.QueueOf(g.Queue) is not null))
            Rule(p, "есть игры в записываемых очередях — профиль показывает их, а не пустую очередь",
                 cells.Count - empty > 0, $"{cells.Count - empty} игр на экране");

        if (selected is "solo" or "flex" && d.Queues.GetValueOrDefault(selected) is { } v)
        {
            if (v.HasRank)
                Rule(p, "ранговый с рангом — сразу график ранга, а не надпись",
                     chartHint is null && chart.Children.OfType<Ellipse>().Any(), chartWhat);
            else
                Rule(p, "без ранга силуэт мерцает, только пока в очереди нет игр",
                     shimmer.ContainsKey("RankEmblemSkel") == (v.Last5.Count == 0),
                     $"игр в очереди {v.Last5.Count}, мерцает: {shimmerText}");
        }

        // Винрейт ранговых при переключении графика на винрейт: по времени и
        // около сезонного, а не со 100% или 0% у игр, добитых из истории. Это
        // данные профиля, а не вид, — хватит одного раза.
        if (!main) return;
        foreach (var key in new[] { "solo", "flex" })
        {
            if (d.Queues.GetValueOrDefault(key) is not { WinrateHistory.Count: > 0 } q) continue;
            var pts = q.WinrateHistory;
            Rule(p, $"винрейт {key} идёт по времени", pts.Zip(pts.Skip(1)).All(x => x.First.Date <= x.Second.Date),
                 string.Join(", ", pts.Take(4).Select(x => x.Date.ToString("dd.MM HH:mm"))));
            if (q.Wins + q.Losses >= 20)
                Rule(p, $"винрейт {key} — около сезонного ({q.Winrate:0}%)",
                     pts.All(x => Math.Abs(x.Winrate - q.Winrate) <= 15),
                     string.Join(", ", pts.Take(5).Select(x => $"{x.Winrate:0}")));
        }
    }

    /// Сопоставление очередей клиента (номера из живого клиента, 24.10).
    private static void QueueRules()
    {
        Console.WriteLine("── очереди");
        Rule(null, "Swiftplay пишется как нормалы", SessionTracker.QueueOf(Swift) == "normal", SessionTracker.QueueOf(Swift) ?? "не пишется");
        Rule(null, "в Swiftplay подбор в драфте не показывается", !SessionTracker.DraftHelp(Swift), "показывается");
        Rule(null, "ARAM: Mayhem пишется как ARAM", SessionTracker.QueueOf(Mayhem) == "aram", SessionTracker.QueueOf(Mayhem) ?? "не пишется");
        Rule(null, "в ARAM: Mayhem подбор есть, как в ARAM", SessionTracker.DraftHelp(Mayhem) && SessionTracker.DraftHelp(Aram), "");
        Rule(null, "нормалы Draft, соло, флекс, ARAM — как были",
             SessionTracker.QueueOf(Draft) == "normal" && SessionTracker.QueueOf(Solo) == "solo"
             && SessionTracker.QueueOf(Flex) == "flex" && SessionTracker.QueueOf(Aram) == "aram" && SessionTracker.DraftHelp(Draft), "");
        Rule(null, "Арена не пишется", SessionTracker.QueueOf(Arena) is null, SessionTracker.QueueOf(Arena) ?? "");
    }

    /// Видимые элементы с бегущим бликом — по ближайшему именованному предку.
    private static Dictionary<string, int> Shimmering(OverlayWindow w)
    {
        var result = new Dictionary<string, int>();
        var stack = new Stack<DependencyObject>([(DependencyObject)w.Content]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
            if (d is not FrameworkElement fe || !Seen(fe)) continue;
            var brush = fe switch
            {
                Border b => b.Background, Panel pn => pn.Background, Shape s => s.Fill, Control c => c.Background, _ => null,
            };
            if (!ReferenceEquals(brush, Skeleton.Shimmer)) continue;
            var named = (DependencyObject)fe;
            while (named is not null && (named is not FrameworkElement f || f.Name.Length == 0))
                named = VisualTreeHelper.GetParent(named);
            var key = (named as FrameworkElement)?.Name ?? "?";
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    // ── Снимки ────────────────────────────────────────────────────────────

    private static BitmapSource Shot(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        Save(bmp, Path.Combine(dir, file));
        return bmp;
    }

    /// Все снимки одним листом: подпись над каждым, по пять в ряд.
    private static void Sheet(string dir)
    {
        if (Shots.Count == 0) return;
        const int cols = 5, label = 26, gap = 12;
        int cw = Shots.Max(s => s.Shot.PixelWidth), ch = Shots.Max(s => s.Shot.PixelHeight);
        var rows = (Shots.Count + cols - 1) / cols;
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x10, 0x16)), null,
                new Rect(0, 0, cols * (cw + gap) + gap, rows * (ch + label + gap) + gap));
            for (var i = 0; i < Shots.Count; i++)
            {
                double x = gap + i % cols * (cw + gap), y = gap + i / cols * (ch + label + gap);
                dc.DrawText(new FormattedText(Shots[i].Name, System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.White, 1.0),
                    new System.Windows.Point(x, y));
                dc.DrawImage(Shots[i].Shot, new Rect(x, y + label, Shots[i].Shot.PixelWidth, Shots[i].Shot.PixelHeight));
            }
        }
        var bmp = new RenderTargetBitmap(cols * (cw + gap) + gap, rows * (ch + label + gap) + gap, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        Save(bmp, Path.Combine(dir, "all.png"));
        Console.WriteLine($"\nлист со всеми: {Path.Combine(dir, "all.png")}");
    }

    private static void CopyAssets(string player, string root)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(player, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(root, Path.GetRelativePath(player, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, overwrite: true);
            }
        }
    }

    private static void Save(BitmapSource bmp, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static void Pump()
    {
        for (var i = 0; i < 12; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
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

    /// Правило для игрока (или общее, если игрока нет). Печатаем только
    /// провалы — отчёт выше и так показывает всё, что видно.
    private static void Rule(Player? p, string what, bool ok, string detail)
    {
        if (ok) return;
        Console.WriteLine($"  [ПЛОХО] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        _fails++;
    }
}
