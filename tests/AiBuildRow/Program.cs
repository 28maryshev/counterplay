using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Строка подбора сборки под врагов (владелец, 01.11): знак Counterplay AI
/// горит всегда, под ним шкала — сколько врагов из пяти уже выбрали, после
/// пятого на месте пустых рамок появляются предметы. Раньше до пятого врага
/// строка была блёклой целиком и без знака, а если менять было нечего, так и
/// оставалась пустой при известном составе.
///
/// 0. Строки по порядку: ходовая сборка, вторая — если она есть в данных
///    (владелец, 02.11: «два варианта, как руны, третьим Counterplay AI»),
///    подбор — последней. Предметы — в порядке из данных (порядок покупки), а
///    не «сначала ядро».
/// 1. Мой чемпион выбран, врагов 0…4: строка подбора — пустая, знак виден и не
///    тускнеет вместе с рамками, шкала дорастает до n/5.
/// 2. Перерисовка без нового врага (ховер, выбор строки) шкалу не сбрасывает и
///    заново не растит.
/// 3. Пятый враг — в строке подбора предметы: подбор или та же ходовая
///    сборка с подписью «уже отвечает составу».
/// 4. Новый чемпион без второй сборки — строк две, шкала снова с нуля.
///
/// Руны — макет (RunesClient.UseMock), база матчей — настоящая, только чтение;
/// настройки и пулы — в своей папке. CP_SNAP=папка — снимки окна на каждом шаге.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Shen = 98, Braum = 201;
    private static readonly int[] Enemies = [54, 64, 103, 222, 412];   // Мальфит, Ли Син, Ари, Джинкс, Треш

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AiBuildRow");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        try
        {
            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — окно проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window(engine, Environment.GetEnvironmentVariable("CP_SNAP"));
            app.Shutdown();
        }
        finally
        {
            RunesClient.UseMock = false;
            AppPaths.RootOverride = null;
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: знак подбора горит, шкала растёт с врагами, после пятого — предметы"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Window(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine("окно:");
        var s = AppSettings.Current;
        s.DraftRunes = true;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();

        RunesClient.UseMock = true;
        var stats = RunesClient.GetAsync(Shen, "support", CancellationToken.None).GetAwaiter().GetResult()!;
        Check("у макета Шена есть сборка и ходовые предметы", stats.Builds.Count > 0 && stats.Items.Count > 0,
              $"{stats.Builds.Count} сборок, {stats.Items.Count} предметов");

        // Файл: вторая сборка — только с меткой alt. В файлах до 02.11 вторыми
        // шли сборки, отличавшиеся двумя предметами, хоть с 46% побед.
        static string B(int first, string alt) =>
            $"{{\"items\":[{first},3047,3075,3084,3190,3065],\"core\":[{first},3075,3084],\"games\":1500,\"wr\":52.0{alt}}}";
        var parsed = RunesClient.Parse(
            "{\"champion\":98,\"role\":\"top\",\"patches\":[\"16.21\"],\"games\":9000,\"wr\":51.0," +
            "\"keystones\":[],\"vs\":{},\"items\":[],\"spells\":[]," +
            $"\"builds\":[{B(3068, "")},{B(6662, "")},{B(3143, ",\"alt\":true")}]}}");
        Check("из файла — ходовая и вторая с меткой alt; без метки — мимо",
              parsed.Builds.Count == 2 && parsed.Builds[0].Items[0] == 3068 && parsed.Builds[1].Items[0] == 3143,
              string.Join(" | ", parsed.Builds.Select(b => string.Join(",", b.Items))));

        void Draft(int me, int enemies)
        {
            var d = State(me, Enemies.Take(enemies).ToArray());
            w.UpdateRecommendations(engine.Recommend(d, 6), d, engine);
            Pump();
        }

        Draft(Shen, 0);
        w.ShowRunes(stats, Shen, DataDragon.Name(Shen), "support", null);
        Pump();
        Check("руны и сборка на экране", Vis(w, "RunesBar") && Vis(w, "BuildList"), "");

        // ── 0. Строки ───────────────────────────────────────────────────────
        var rows = Rows(w);
        Check("у Шена три строки: ходовая, вторая, подбор",
              stats.Builds.Count == 2 && rows.Count == 3
              && rows[0] is { Index: 0, AiVis: Visibility.Collapsed }
              && rows[1] is { Index: 1, AiVis: Visibility.Collapsed, WrVis: Visibility.Visible }
              && rows[2] is { Index: -1, AiVis: Visibility.Visible },
              $"сборок {stats.Builds.Count}, строк {rows.Count}: "
              + string.Join(", ", rows.Select(r => $"{r.Index}/{(r.AiVis == Visibility.Visible ? "AI" : r.WrText)}")));
        var names = rows.Count > 0 ? rows[0].Slots.Select(x => x.Tip.Split(" · ")[0]).ToList() : [];
        var want = stats.Builds[0].Items.Take(6).Select(ItemIcons.NameOf).ToList();
        Check("предметы ходовой сборки — в порядке из данных", names.SequenceEqual(want),
              $"{string.Join(", ", names)} (ждали {string.Join(", ", want)})");

        // ── 1. Враги по одному ──────────────────────────────────────────────
        for (var n = 0; n <= 4; n++)
        {
            if (n > 0) Draft(Shen, n);
            Settle();
            var row = AiRow(w);
            var bar = Bar(w);
            var width = 72.0 * n / 5;
            Check($"врагов {n}: строка подбора пустая, знак виден, шкала {n}/5",
                  row is { Index: -1, AiVis: Visibility.Visible, FillVis: Visibility.Visible }
                  && bar is not null && Math.Abs(bar.Width - width) < 0.5,
                  $"строка {row?.Index}, знак {row?.AiVis}, шкала {bar?.Width:0.#} из 72 (ждали {width:0.#})");
            if (n == 2)
            {
                var dim = bar is null ? 0 : Lit(bar);
                Check("знак и шкала не тускнеют вместе с пустыми рамками",
                      dim >= 0.99 && row?.RowOpacity < 0.5, $"яркость знака {dim:0.##}, рамок {row?.RowOpacity}");
                if (snap is { Length: > 0 }) Snap(w, snap, "ai-1-two-enemies.png");
            }
        }

        // ── 2. Перерисовка без нового врага ─────────────────────────────────
        Call(w, "RenderBuilds", stats);
        Pump();
        var again = Bar(w);
        Check("перерисовка (ховер) — шкала на месте и не растёт заново",
              again is not null && Math.Abs(again.Width - 72.0 * 4 / 5) < 0.5
              && !again.HasAnimatedProperties,
              $"шкала {again?.Width:0.#}, анимация {again?.HasAnimatedProperties}");

        // ── 3. Пятый враг ───────────────────────────────────────────────────
        Draft(Shen, 5);
        Settle();
        var full = AiRow(w);
        var adapted = full?.Slots.Any(x => x.Stroke is SolidColorBrush b && b.Color == System.Windows.Media.Color.FromRgb(0x36, 0xD6, 0xE7)) == true;
        Check("пятый враг — в строке подбора предметы, шкалы больше нет; она третья",
              full is { Index: 2, AiVis: Visibility.Visible, FillVis: Visibility.Collapsed, RowOpacity: 1.0 }
              && full.Slots.All(x => x.HasTip),
              $"строка {full?.Index}, шкала {full?.FillVis}, предметов с названием {full?.Slots.Count(x => x.HasTip)} из 6");
        Console.WriteLine($"  {(adapted ? "подбор заменил предметы" : "менять нечего")}: {full?.TipHead}");
        Check("подсказка говорит, что это: подбор или «уже отвечает составу»",
              full?.TipHead == Loc.T("runes.aiTipHead")
              || full?.TipHead.Equals(Loc.T("runes.aiCovered"), StringComparison.OrdinalIgnoreCase) == true,
              full?.TipHead ?? "");
        if (snap is { Length: > 0 }) Snap(w, snap, "ai-2-five-enemies.png");

        // ── 4. Новый чемпион ────────────────────────────────────────────────
        Draft(Braum, 1);
        var braum = RunesClient.GetAsync(Braum, "support", CancellationToken.None).GetAwaiter().GetResult()!;
        w.ShowRunes(braum, Braum, DataDragon.Name(Braum), "support", null);
        Pump();
        // Строка нарисована с той долей, от которой шкала растёт: у нового
        // чемпиона — ноль, а не 4/5 от прошлого.
        var from = AiRow(w)?.FillWidth;
        Check("у Браума второй сборки нет — строк две, подбор второй",
              braum.Builds.Count == 1 && Rows(w).Count == 2 && AiRow(w) is { Index: -1 },
              $"сборок {braum.Builds.Count}, строк {Rows(w).Count}");
        Settle();
        var end = Bar(w);
        Check("новый чемпион — шкала растёт с нуля до числа врагов",
              from == 0 && end is not null && Math.Abs(end.Width - 72.0 / 5) < 0.5,
              $"с {from:0.#} до {end?.Width:0.#} (ждали с 0 до {72.0 / 5:0.#})");
        w.Close();
        Pump();
    }

    /// Роль раздана, я на поддержке и уже выбрал; враги взяли первых n.
    private static DraftState State(int me, int[] enemies)
    {
        var self = new DraftPlayer(0, me, 0, "utility", true);
        var team = new List<DraftPlayer> { self };
        var their = enemies.Select((id, i) => new DraftPlayer(5 + i, id, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: team, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: self, MyPosition: "support", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static List<OverlayWindow.BuildRowVm> Rows(Window w) =>
        (((ItemsControl)w.FindName("BuildList")).ItemsSource as IEnumerable<OverlayWindow.BuildRowVm>)?
        .ToList() ?? [];

    /// Строка подбора — всегда последняя.
    private static OverlayWindow.BuildRowVm? AiRow(Window w) => Rows(w).LastOrDefault();

    /// Шкала в строке подбора — из её шаблона.
    private static FrameworkElement? Bar(Window w)
    {
        var list = (ItemsControl)w.FindName("BuildList");
        if (list.ItemContainerGenerator.ContainerFromIndex(Rows(w).Count - 1) is not ContentPresenter cp) return null;
        cp.ApplyTemplate();
        return cp.ContentTemplate?.FindName("AiFillBar", cp) as FrameworkElement;
    }

    /// Итоговая непрозрачность элемента — произведение по всем родителям до окна.
    private static double Lit(DependencyObject e)
    {
        var o = 1.0;
        for (var p = e; p is not null and not System.Windows.Window; p = VisualTreeHelper.GetParent(p))
            if (p is UIElement u) o *= u.Opacity;
        return o;
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    /// Имена, иконки и предметы. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons", "items" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemFacts.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Дождаться конца анимаций (шкала растёт 0,45 с).
    private static void Settle()
    {
        for (var i = 0; i < 12; i++) Pump();
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
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
