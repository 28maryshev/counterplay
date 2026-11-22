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
        Console.WriteLine("мастерство и пулы:");
        Personal(panel, engine);

        Console.WriteLine();
        Console.WriteLine("«Заново»:");
        Restart(panel, overlay);

        Console.WriteLine();
        Console.WriteLine("драфт руками — союзник после меня наводит:");
        ManualOrder(panel, overlay);

        Console.WriteLine();
        Console.WriteLine("окно драфта:");
        CloseOverlay(panel, overlay);   // последним: закрывает и панель, и программу
        mode.GetMethod("LeaveSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
        Check("выход из песочницы — подбор снова видит своё (голая база снята)",
              !RecommendationEngine.Bare, $"голая база {RecommendationEngine.Bare}");
    }

    // ── Мастерство и пулы: по умолчанию голая база ──────────────────────────

    /// Владелец (22.11): «в песочнице по умолчанию нулевое мастерство и никаких
    /// пулов, как будто подсчёт на голой базе; надо — включить в настройках
    /// песочницы». Тестовая история и пулы на месте, но подбор их не видит,
    /// пока не включат «Мастерство и пулы»; друг на голой базе свой дуо-пул не
    /// включает, а с галочкой — включает именно его.
    private static void Personal(Window panel, RecommendationEngine engine)
    {
        var box  = Get<CheckBox>(panel, "_personal")!;
        var mate = Get<ComboBox>(panel, "_mateCombo")!;
        var info = Get<TextBlock>(panel, "_mateInfo")!;
        string[] lanes = ["utility", "top", "jungle", "middle", "bottom"];
        var me = new DraftPlayer(0, 0, 0, "utility", true);
        var state = new DraftState(
            MyTeam: [me, .. Enumerable.Range(1, 4).Select(i => new DraftPlayer(i, 0, 0, lanes[i], false))],
            TheirTeam: [.. Enumerable.Range(5, 5).Select(i => new DraftPlayer(i, 0, 0, "", false))],
            MyTeamBans: [], TheirTeamBans: [], Me: me, MyPosition: "utility", DirectOpponent: null,
            ExposedToCounter: true, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
        int Comfy() => engine.Recommend(state, topN: 400).Count(r => r.ComfortDelta != 0);
        PoolKind Kind() => PoolStore.Current().ActiveKind;
        string Now() => $"голая база {RecommendationEngine.Bare}, пул {Kind()}, очков мастерства " +
                        $"{engine.Mastery.Count}, с наигранностью {Comfy()}, мейнов {RecommendationEngine.MyMains().Count}";

        Check("по умолчанию — голая база: галочка снята, пул в обычном режиме, ни мастерства, ни наигранности, ни мейнов",
              box.IsChecked != true && RecommendationEngine.Bare && Kind() == PoolKind.Normal
              && engine.Mastery.Count == 0 && Comfy() == 0 && RecommendationEngine.MyMains().Count == 0, Now());

        box.IsChecked = true;
        Pump();
        Check("«Мастерство и пулы» — тестовая история в подборе и дуо-пул «Друг» снова включён",
              !RecommendationEngine.Bare && Kind() == PoolKind.Duo && engine.Mastery.Count > 0
              && Comfy() > 0 && RecommendationEngine.MyMains().Count > 0, Now());

        box.IsChecked = false;
        Pump();
        Check("галочку сняли — снова голая база", RecommendationEngine.Bare && Kind() == PoolKind.Normal
              && engine.Mastery.Count == 0 && Comfy() == 0, Now());

        var duo = PoolStore.Current().DuoPools.FirstOrDefault();
        if (duo is null) { Check("тестовый дуо-пул есть", false, "дуо-пулов нет"); return; }
        mate.SelectedIndex = 1;
        Pump();
        Check("друг на голой базе — в пати, но его дуо-пул выключен, и строка о друге это говорит",
              Kind() == PoolKind.Normal && info.Text.Contains("голая база"), $"пул {Kind()}, «{info.Text}»");
        box.IsChecked = true;
        Pump();
        var a = PoolStore.Current();
        Check("галочка при выбранном друге включает его дуо-пул",
              a.ActiveKind == PoolKind.Duo && a.ActiveId == duo.Id && info.Text.StartsWith("пул включён"),
              $"пул {a.ActiveKind} {a.ActiveId} (ждали {duo.Id}), «{info.Text}»");
        box.IsChecked = false;
        mate.SelectedIndex = 0;
        Pump();
    }

    // ── Окно драфта закрывают — песочница живёт ─────────────────────────────

    /// Владелец (22.11): «когда я закрываю окно драфта в песочнице, она не
    /// должна закрываться полностью и сбивать данные — только когда закрою
    /// панель песочницы». Крестик окна и Alt+F4 прячут окно; «Показать окно» и
    /// запуск драфта возвращают; закрытие панели — выход из программы.
    private static void CloseOverlay(Window panel, OverlayWindow overlay)
    {
        overlay.SandboxMode = true;   // так его ставит TestMode при запуске песочницы
        bool closed = false, asked = false;
        overlay.Closed += (_, _) => closed = true;
        // Вопрос «выйти из программы?» — модальный: сразу закрываем и
        // запоминаем, что спросили, иначе проверка встала бы на нём.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) =>
            {
                if (s is Window { } w && w.GetType().Name == "ConfirmWindow") { asked = true; w.Close(); }
            }));
        void TrayOff()   // значок в трее — не показываем человеку (как GameTray)
        {
            if (overlay.GetType().GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(overlay) is System.Windows.Forms.NotifyIcon icon) icon.Visible = false;
        }
        int Cell(int c) => (int)panel.GetType().GetMethod("CellChamp", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, [c])!;
        // Выход: у приложения без Run очередь не останавливается, зато Current — null.
        var app = System.Windows.Application.Current.Dispatcher;
        bool Down() => System.Windows.Application.Current is null || app.HasShutdownStarted;

        Call(panel, "ResetDraft");
        Get<ComboBox[]>(panel, "_ally")![1].SelectedItem = DataDragon.Name(Darius);
        Pump();

        var cross = Descendants(overlay).OfType<Button>().First(b => b.Content as string == "✕");
        Click(cross);
        Pump();
        TrayOff();
        Check("крестик окна драфта — окно спрятано, программа живёт, о выходе не спрашивает, слоты целы",
              !closed && !overlay.IsVisible && !asked && !Down() && Cell(1) == Darius,
              $"закрыто {closed}, на экране {overlay.IsVisible}, спросили о выходе {asked}, " +
              $"программа закрывается {Down()}, слот {DataDragon.Name(Cell(1))}");

        Click(Get<Button>(panel, "_showBtn")!);
        Pump();
        Check("«Показать окно» — окно вернулось", overlay.IsVisible, $"на экране {overlay.IsVisible}");

        overlay.Close();   // Alt+F4
        Pump();
        TrayOff();
        Check("Alt+F4 по окну драфта — то же: окно спрятано, программа живёт",
              !closed && !overlay.IsVisible && !Down(), $"закрыто {closed}, на экране {overlay.IsVisible}");
        if (closed) return;   // закрытое окно уже не вернуть — дальше проверять нечего

        Click(Get<Button>(panel, "_simBtn")!);   // запуск драфта возвращает окно сам
        Get<System.Windows.Threading.DispatcherTimer>(panel, "_simTimer")?.Stop();
        Pump();
        Check("запуск драфта из панели возвращает спрятанное окно", overlay.IsVisible, $"на экране {overlay.IsVisible}");
        Call(panel, "ResetDraft");
        Pump();

        // Закрыли панель — выход из программы, и окно драфта закрывается, хотя
        // песочница свои закрытия отменяет: при выходе WPF отмену не слушает.
        panel.Close();
        try { Pump(); } catch (Exception) { /* очередь уже остановлена — так и надо */ }
        Check("закрыли панель песочницы — программа закрылась вместе с окном драфта",
              closed && Down(), $"окно закрыто {closed}, программа закрывается {Down()}");
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

    private const int Darius = 122, Malphite = 54, Caitlyn = 51, Braum = 201, Garen = 86, Ahri = 103;

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

    // ── Драфт руками: порядок ходов ─────────────────────────────────────────

    /// <summary>
    /// Владелец (22.11): «на стрелке и саппорте работает, а лес + мид, топ +
    /// лес, когда я показываю чемпиона и беру первым в этих связках, тегов
    /// „С показанным“ нет». Стрелок с саппортом шёл авто-драфтом, а связки —
    /// руками, и без авто-драфта песочница ставила каждого союзника взятым:
    /// подбор считал его целиком, и вместо «С показанным» стояла общая синергия.
    /// В клиенте союзник, чей ход не прошёл, только наводит. Без авто-драфта
    /// строки панели сверху вниз — порядок пиков: ниже меня — наводит, выше —
    /// взят. Саппорт последним видит стрелка взятым, как и раньше.
    /// </summary>
    private static void ManualOrder(Window panel, OverlayWindow overlay)
    {
        var ally = Get<ComboBox[]>(panel, "_ally")!;
        var meRadio = Get<RadioButton[]>(panel, "_meRadio")!;
        var stageDraft = Enum.Parse(panel.GetType().GetNestedType("TestStage", BindingFlags.NonPublic)!, "Draft");
        string Who(DraftState d) => string.Join(" | ", d.MyTeam.Where(p => p.EffectiveChampionId != 0).Select(p =>
            $"{p.CellId}:{DataDragon.Name(p.EffectiveChampionId)} {(p.ChampionId != 0 ? "взят" : "наведён")}"));
        DraftState Scene(int meCell, params (int Cell, int Champ)[] allies)
        {
            Call(panel, "ResetDraft");
            meRadio[meCell].IsChecked = true;
            Call(panel, "SetStage", stageDraft);
            foreach (var (cell, champ) in allies) ally[cell].SelectedItem = DataDragon.Name(champ);
            Pump();
            return Get<DraftState>(overlay, "_lastDraft")!;
        }
        static bool Hovered(DraftState d, int cell, int champ) =>
            d.MyTeam[cell] is { ChampionId: 0 } p && p.PickIntentId == champ;
        static bool Locked(DraftState d, int cell, int champ) =>
            d.MyTeam[cell] is { PickIntentId: 0 } p && p.ChampionId == champ;

        // Я лес: топ выше — взят; мид и саппорт ниже — наводят. Подбор видит
        // мида показанным: тег «С показанным: Ари».
        var d = Scene(1, (0, Garen), (2, Ahri), (4, Braum));
        Check("руками, я лес: топ выше меня взят, мид и саппорт ниже наводят",
              Locked(d, 0, Garen) && Hovered(d, 2, Ahri) && Hovered(d, 4, Braum), Who(d));
        var recs = Get<IReadOnlyList<Recommendation>>(overlay, "_lastRecs") ?? [];
        var tagged = recs.Count(r => r.Badges?.Tags.Any(t => t.Kind == "synergy"
                                                            && t.Text.Contains(DataDragon.Name(Ahri))) == true);
        Check("руками, я лес, мид навёл Ари — в списке есть «С показанным: Ари»",
              tagged > 0, $"с тегом {tagged} из {recs.Count}");

        // Я топ — первый пик: лесник ниже наводит.
        d = Scene(0, (1, Ahri));
        Check("руками, я топ: лесник ниже меня наводит", Hovered(d, 1, Ahri), Who(d));

        // Я стрелок: саппорт ниже наводит, мид выше взят.
        d = Scene(3, (2, Garen), (4, Braum));
        Check("руками, я стрелок: мид выше взят, саппорт ниже наводит",
              Locked(d, 2, Garen) && Hovered(d, 4, Braum), Who(d));

        // Я саппорт — последняя строка: все взяты, как до 22.11.
        d = Scene(4, (2, Garen), (3, Caitlyn));
        Check("руками, я саппорт последним: мид и стрелок взяты",
              Locked(d, 2, Garen) && Locked(d, 3, Caitlyn), Who(d));

        // Мой пик подтверждён — драфт застыл как есть, союзники взяты.
        d = Scene(1, (2, Ahri));
        ally[1].SelectedItem = DataDragon.Name(Braum);
        Pump();
        Call(panel, "LockMy", Braum);
        Pump();
        d = Get<DraftState>(overlay, "_lastDraft")!;
        Check("руками, мой пик подтверждён — союзники взяты", Locked(d, 2, Ahri), Who(d));

        // «Моментально» — драфт закончен, наведённых нет.
        Call(panel, "ResetDraft");
        meRadio[0].IsChecked = true;
        Call(panel, "InstantDraft");
        Pump();
        d = Get<DraftState>(overlay, "_lastDraft")!;
        Check("«Моментально»: драфт закончен — все взяты",
              d.MyTeam.All(p => p.ChampionId != 0 && p.PickIntentId == 0), Who(d));

        Call(panel, "ResetDraft");
        meRadio[2].IsChecked = true;
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
