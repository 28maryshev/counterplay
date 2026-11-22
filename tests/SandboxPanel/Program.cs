using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Панель песочницы (владелец, 22.11: «добавилось очень много всего в
/// песочницу — сделай нормальный удобный UI»; «добавь кнопку „Заново“»).
/// Панель и окно невидимые; база матчей настоящая, только чтение; данные
/// песочницы — в своей папке. CP_SNAP=папка — снимок панели.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SandboxPanel");
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
            if (db is null) { Console.WriteLine("базы нет — песочницу проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Run(engine);
            app.Shutdown();
        }
        finally
        {
            AppPaths.RootOverride = null;
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: панель песочницы разложена, «Заново» запускает драфт с тем же составом"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run(RecommendationEngine engine)
    {
        var asm = typeof(OverlayWindow).Assembly;
        Counterplay.Sandbox.Active = true;
        var mode = asm.GetType("Counterplay.TestMode")!;
        mode.GetMethod("EnterSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [engine]);

        var s = AppSettings.Current;
        s.DraftLeft = -32000; s.DraftTop = -32000; s.DraftPlacement = "remember";
        var overlay = new OverlayWindow();
        ShowHidden(overlay);
        overlay.SetEngine(engine);
        var panelType = asm.GetType("Counterplay.TestPanel")!;
        var panel = (Window)Activator.CreateInstance(panelType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [overlay, engine, DataDragon.GetAllIconUrls().Keys.ToList(), false, false, false], null)!;
        ShowHidden(panel);
        // Окно без рамки (так оно невидимо) — клиентская часть настоящей
        // панели меньше на рамку и заголовок Windows.
        double width = panel.Width, height = panel.Height;
        panel.MinWidth -= FrameW; panel.MinHeight -= FrameH;   // наименьшее окно — тоже с рамкой
        panel.Width = width - FrameW; panel.Height = height - FrameH;
        Pump();

        Console.WriteLine("раскладка:");
        Layout(panel, "по умолчанию");
        var snap = Environment.GetEnvironmentVariable("CP_SNAP");
        if (snap is { Length: > 0 }) Snap(panel, snap, "panel.png");
        panel.Width = panel.MinWidth; panel.Height = panel.MinHeight;
        Pump();
        Layout(panel, "наименьшее окно");
        panel.Width = width - FrameW; panel.Height = height - FrameH;
        Pump();

        Console.WriteLine();
        Console.WriteLine("«Заново»:");
        Restart(panel, overlay);

        panelType.GetProperty("SwitchingToLive")!.SetValue(panel, true);   // закрытие — не выход
        panel.Close();
        overlay.Close();
        Pump();
        mode.GetMethod("LeaveSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    }

    // Рамка и заголовок обычного окна Windows при 96 dpi.
    private const double FrameW = 16, FrameH = 39;

    // ── Раскладка ────────────────────────────────────────────────────────────

    /// Всё управление — внутри окна, ничего не наезжает друг на друга, подписи
    /// кнопок помещаются (и те, что кнопки показывают по ходу драфта), а
    /// разбору рун остаётся место.
    private static void Layout(Window panel, string size)
    {
        var client = new Rect(0, 0, panel.ActualWidth, panel.ActualHeight);
        var controls = Descendants(panel)
            .Where(e => e is Button or ComboBox or CheckBox or RadioButton && e.IsVisible && !IsInsideCombo(e))
            .ToList();
        Rect Box(FrameworkElement e) =>
            e.TransformToAncestor(panel).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        string Name(FrameworkElement e) => e switch
        {
            Button b => $"кнопка «{b.Content}»",
            CheckBox c => $"галочка «{c.Content}»",
            ComboBox c => $"список ({c.SelectedItem ?? c.Text})",
            _ => e.GetType().Name,
        };

        var outside = controls.Where(e => !Contains(client, Box(e))).Select(e => $"{Name(e)} {Box(e)}").ToList();
        Check($"{size}: всё управление внутри окна ({client.Width:0}×{client.Height:0})",
              controls.Count > 20 && outside.Count == 0,
              outside.Count == 0 ? $"элементов {controls.Count}" : string.Join("; ", outside));

        var overlaps = new List<string>();
        for (int i = 0; i < controls.Count; i++)
            for (int j = i + 1; j < controls.Count; j++)
            {
                var r = Rect.Intersect(Box(controls[i]), Box(controls[j]));
                if (!r.IsEmpty && r.Width > 0.5 && r.Height > 0.5)
                    overlaps.Add($"{Name(controls[i])} × {Name(controls[j])}");
            }
        Check($"{size}: ничего не наезжает друг на друга", overlaps.Count == 0,
              overlaps.Count == 0 ? "пересечений нет" : string.Join("; ", overlaps));

        // Подписи кнопок: и нынешняя, и те, что кнопка покажет по ходу.
        var later = new Dictionary<string, string[]>
        {
            ["_simBtn"] = ["■ Стоп", "⏸ Ваш пик…", "⏸ Ваш бан…", "▶ Авто-драфт"],
            ["_searchBtn"] = ["■ Поиск · 15 с"],
        };
        var cut = new List<string>();
        foreach (var b in controls.OfType<Button>())
        {
            var texts = new List<string> { b.Content as string ?? "" };
            foreach (var (field, more) in later)
                if (ReferenceEquals(Get<Button>(panel, field), b)) texts.AddRange(more);
            // Кнопки запуска делят строку поровну — ширина не растёт под
            // подпись. Пока запуск идёт, подпись полужирная: меряем её.
            var weight = VisualTreeHelper.GetParent(b) is UniformGrid ? FontWeights.SemiBold : b.FontWeight;
            foreach (var t in texts)
            {
                var need = TextWidth(b, t, weight) + b.Padding.Left + b.Padding.Right
                         + b.BorderThickness.Left + b.BorderThickness.Right;
                if (need > b.ActualWidth + 0.5) cut.Add($"«{t}» нужно {need:0}, есть {b.ActualWidth:0}");
            }
        }
        Check($"{size}: подписи кнопок помещаются целиком", cut.Count == 0,
              cut.Count == 0 ? $"кнопок {controls.OfType<Button>().Count()}" : string.Join("; ", cut));

        var runes = Get<TextBlock>(panel, "_runeAiText")!;
        var view = FindAncestor<ScrollViewer>(runes);
        var status = Get<TextBlock>(panel, "_status")!;
        Check($"{size}: разбору рун остаётся место, строка состояния видна",
              view is { ActualHeight: >= 60 } && status.IsVisible && status.Text.Length > 0,
              $"разбор рун {view?.ActualHeight:0} px, состояние «{status.Text}»");
    }

    private static bool Contains(Rect outer, Rect inner) =>
        inner.Left >= outer.Left - 0.5 && inner.Top >= outer.Top - 0.5
        && inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5;

    // Кнопка раскрытия и поле ввода внутри списка — его часть, не отдельный элемент.
    private static bool IsInsideCombo(FrameworkElement e) => FindAncestor<ComboBox>(e) is not null;

    private static T? FindAncestor<T>(DependencyObject e) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(e); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is T t) return t;
        return null;
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe) yield return fe;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static double TextWidth(System.Windows.Controls.Control c, string text, FontWeight? weight = null) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
            new Typeface(c.FontFamily, c.FontStyle, weight ?? c.FontWeight, c.FontStretch), c.FontSize,
            System.Windows.Media.Brushes.White, 1.0).WidthIncludingTrailingWhitespace;

    // ── «Заново» ─────────────────────────────────────────────────────────────

    private const int Darius = 122, Malphite = 54, Caitlyn = 51, Braum = 201, Garen = 86;

    /// «Заново» — как «Сброс», но выбранные до драфта чемпионы остаются, и тот
    /// же запуск идёт снова; монетка «кто первый» — заново.
    private static void Restart(Window panel, OverlayWindow overlay)
    {
        var ally  = Get<ComboBox[]>(panel, "_ally")!;
        var enemy = Get<ComboBox[]>(panel, "_enemy")!;
        var restart = Get<Button>(panel, "_restartBtn")!;
        int Cell(int c) => (int)panel.GetType().GetMethod("CellChamp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [c])!;
        T Field<T>(string name) =>
            (T)panel.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        void StopTimers()
        {
            Get<System.Windows.Threading.DispatcherTimer>(panel, "_simTimer")?.Stop();
            Get<System.Windows.Threading.DispatcherTimer>(panel, "_banTimer")?.Stop();
            Get<System.Windows.Threading.DispatcherTimer>(panel, "_searchTimer")?.Stop();
        }
        void Press(Button b) { Click(b); StopTimers(); Pump(); }
        string Slots() => string.Join(" ", Enumerable.Range(0, 10)
            .Select(c => Cell(c) == 0 ? "·" : DataDragon.Name(Cell(c))));
        string Setup() => Field<Dictionary<int, int>?>("_setup") is { } d
            ? string.Join(",", d.OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}")) : "null";
        var want = $"1:{Darius},7:{Malphite}";
        int Filled() => Enumerable.Range(0, 10).Count(c => Cell(c) != 0);

        // 1. Авто-драфт: до драфта выбраны союзник и враг; по ходу — боты,
        // мой ховер из окна и союзник руками. «Заново» — пики с начала, состав
        // до драфта на месте, остальное ушло.
        Call(panel, "ResetDraft");
        ally[1].SelectedItem = DataDragon.Name(Darius);
        enemy[2].SelectedItem = DataDragon.Name(Malphite);
        Pump();
        Press(Get<Button>(panel, "_simBtn")!);
        Call(panel, "AutoPick", 0);
        Call(panel, "AutoPick", 5);
        Call(panel, "SetMy", Braum);
        ally[3].SelectedItem = DataDragon.Name(Caitlyn);
        Pump();
        Press(restart);
        var d = Get<DraftState>(overlay, "_lastDraft");
        Check("авто-драфт: «Заново» — пики с первого хода, выбранные до драфта на месте, остальное ушло",
              Field<int>("_simTurn") == 0 && Setup() == want
              && Cell(1) == Darius && Cell(7) == Malphite && Filled() == 2
              && d?.MyTeam[1] is { ChampionId: 0, PickIntentId: Darius },
              $"ход {Field<int>("_simTurn")}, до драфта {Setup()}, слоты {Slots()}");

        // 2. Монетка «кто пикает первым» — заново на каждый раз.
        var firsts = new HashSet<bool>();
        for (int k = 0; k < 16; k++)
        {
            Press(restart);
            firsts.Add(Field<int[][]>("_simGroups")[0][0] < 5);
        }
        var status = Get<TextBlock>(panel, "_status")!.Text;
        Check("первый пик — монетка на каждое «Заново», строка состояния говорит, кто первый",
              firsts.Count == 2 && status.Contains("первыми пикают"),
              $"первыми были: {string.Join(" и ", firsts.Select(f => f ? "мы" : "враги"))}; состояние «{status}»");

        // 3. Баны: «Заново» — снова объявление, план тот же.
        Press(Get<Button>(panel, "_stageBans")!);
        Press(restart);
        var planned = Field<Dictionary<int, int>>("_planned");
        Check("баны: «Заново» — снова объявление, выбранные до драфта — план",
              Field<bool>("_banSim") && Setup() == want
              && planned.GetValueOrDefault(1) == Darius && planned.GetValueOrDefault(7) == Malphite,
              $"баны {Field<bool>("_banSim")}, до драфта {Setup()}, " +
              $"план {string.Join(",", planned.Select(p => $"{p.Key}:{p.Value}"))}");
        Press(Get<Button>(panel, "_simBtn")!);   // стоп банов — план обратно в слоты
        Check("стоп банов — спрятанный план снова в слотах",
              !Field<bool>("_banSim") && Cell(1) == Darius && Cell(7) == Malphite,
              $"слоты {Slots()}");

        // 4. Поиск: «Заново» — снова поиск, слоты — состав до драфта.
        Press(Get<Button>(panel, "_searchBtn")!);
        Press(restart);
        Check("поиск: «Заново» — снова поиск игры, в слотах состав до драфта",
              Field<DateTime?>("_searchEndAt") is not null && Setup() == want && Filled() == 2
              && Cell(1) == Darius && Cell(7) == Malphite,
              $"поиск {Field<DateTime?>("_searchEndAt") is not null}, до драфта {Setup()}, слоты {Slots()}");

        // 5. «Моментально» из лобби: окно — на драфт; «Заново» — снова полный состав.
        Call(panel, "ResetDraft");
        ally[1].SelectedItem = DataDragon.Name(Darius);
        enemy[2].SelectedItem = DataDragon.Name(Malphite);
        Pump();
        Call(panel, "InstantDraft");
        Pump();
        var stage = Field<object>("_stage").ToString();
        Check("«Моментально» из лобби — окно на драфте, все десять",
              stage == "Draft" && Filled() == 10 && Get<DraftState>(overlay, "_lastDraft") is not null,
              $"этап {stage}, заполнено {Filled()}");
        Press(restart);
        d = Get<DraftState>(overlay, "_lastDraft");
        Check("моментально: «Заново» — снова все десять, выбранные до драфта на месте, мой взят",
              Filled() == 10 && Setup() == want && Cell(1) == Darius && Cell(7) == Malphite
              && d?.Me is { ChampionId: > 0, PickIntentId: 0 },
              $"заполнено {Filled()}, до драфта {Setup()}, мой: взят {d?.Me?.ChampionId}, наведён {d?.Me?.PickIntentId}");

        // 6. «Сброс» забывает состав до драфта: дальше «Заново» берёт то, что в слотах.
        Call(panel, "ResetDraft");
        ally[0].SelectedItem = DataDragon.Name(Garen);
        Pump();
        Press(restart);
        Check("после «Сброса» «Заново» берёт новый состав из слотов",
              Setup() == $"0:{Garen}" && Cell(0) == Garen && Filled() == 10,
              $"до драфта {Setup()}, слоты {Slots()}");

        Call(panel, "ResetDraft");
        Pump();
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    private static void Check(string what, bool ok, string details)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {details}");
        if (!ok) _fails++;
    }

    /// Снимок панели на её фоне — посмотреть глазами.
    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        // Окно без рамки: его содержимое — от левого верхнего угла, фон — у окна.
        var content = (FrameworkElement)w.Content;
        var host = (Visual)VisualTreeHelper.GetChild(w, 0);
        int width = (int)Math.Ceiling(w.ActualWidth);
        int height = (int)Math.Ceiling(w.ActualHeight);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(bg);
        bmp.Render(host);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    /// Имена и иконки чемпионов. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
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
    }

    private static T? Get<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static void Click(Button b) =>
        b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
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
}
