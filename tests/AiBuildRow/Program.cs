using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counterplay;
using Application = System.Windows.Application;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Counterplay.Tests;

/// <summary>
/// Строка подбора сборки под состав (владелец, 01.11): знак Counterplay AI
/// горит всегда, под ним шкала — сколько игроков из десяти уже выбрали
/// окончательно, после последнего пика на месте пустых рамок появляются
/// предметы. Раньше до пятого врага строка была блёклой целиком и без знака, а
/// если менять было нечего, так и оставалась пустой при известном составе.
///
/// С 21.11 предметы ждут, как и руны, всех: подбор смотрит и на своих
/// (предметы поддержки, перекос урона команды), а на красной стороне после
/// пятого врага ещё выбирает наш пятый — подбор приходил по его наведению или
/// без него и на его пик не пересчитывался.
///
/// 0. Строки по порядку: ходовая сборка, вторая — если она есть в данных
///    (владелец, 02.11: «два варианта, как руны, третьим Counterplay AI»),
///    подбор — последней. Предметы — в порядке из данных (порядок покупки), а
///    не «сначала ядро».
/// 1. Красная сторона: я и трое своих выбрали, последний ещё нет, врагов 0…4 —
///    строка подбора пустая, знак виден и не тускнеет вместе с рамками, шкала
///    дорастает до (4+n)/10.
/// 2. Перерисовка без нового пика (ховер, выбор строки) шкалу не сбрасывает и
///    заново не растит.
/// 3. Пятый враг, а наш последний в свой ход навёлся — подбор ждёт, шкала 9/10.
///    Взял другого — без касания панели в строке предметы, посчитанные по
///    взятому, а не по наведённому: подбор или та же ходовая сборка с
///    подписью «уже отвечает составу».
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
    private static readonly int[] Allies = [86, 254, 99];               // Гарен, Вай, Люкс — взяли
    private static readonly string[] AllyRoles = ["top", "jungle", "middle"];
    private const int Syndra = 134, Caitlyn = 51;                       // последний пик: навёл, взял

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
        // CP_LANG=th — подписи и зазор на другом языке (шрифт для тайского и
        // японского подставляется другой).
        Loc.SetLanguage(Environment.GetEnvironmentVariable("CP_LANG") is { Length: > 0 } lang ? lang : "ru");

        try
        {
            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — окно проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // Для снимков — тема программы: без неё подсказка выходит системной
            // жёлто-белой плашкой, и по снимку не понять, как её увидит игрок.
            var snap = Environment.GetEnvironmentVariable("CP_SNAP");
            if (snap is { Length: > 0 })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("/Counterplay;component/src/Ui/Theme.xaml", UriKind.Relative),
                });
            Window(engine, snap);
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
        Console.WriteLine(_fails == 0 ? "ИТОГ: знак подбора горит, шкала растёт с пиками, после последнего — предметы"
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

        void Draft(int me, int enemies, int last = 0, bool picking = false)
        {
            var d = State(me, Enemies.Take(enemies).ToArray(), last, picking);
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
        double waitRow = 0, waitRune = 0;
        for (var n = 0; n <= 4; n++)
        {
            if (n > 0) Draft(Shen, n);
            Settle();
            var row = AiRow(w);
            var bar = Bar(w);
            var width = 72.0 * (4 + n) / 10;
            Check($"своих 4, врагов {n}: строка подбора пустая, знак виден, шкала {4 + n}/10",
                  row is { Index: -1, AiVis: Visibility.Visible, FillVis: Visibility.Visible }
                  && bar is not null && Math.Abs(bar.Width - width) < 0.5,
                  $"строка {row?.Index}, знак {row?.AiVis}, шкала {bar?.Width:0.#} из 72 (ждали {width:0.#})");
            if (n == 2)
            {
                var dim = bar is null ? 0 : Lit(bar);
                Check("знак и шкала не тускнеют вместе с пустыми рамками",
                      dim >= 0.99 && row?.RowOpacity < 0.5, $"яркость знака {dim:0.##}, рамок {row?.RowOpacity}");
                // Владелец, 21.11: «под строкой состояния сделай подписи Items и
                // Runes», затем «по центру полосы, отступ 1 пиксель, переводы на
                // все языки».
                Check($"под шкалами подписи на языке программы: «{ItemsLabel}» у строки подбора предметов, «{RunesLabel}» у карточки подбора рун",
                      Caption(RowBox(w), ItemsLabel) && Caption(RuneBox(w), RunesLabel),
                      $"{ItemsLabel} {Caption(RowBox(w), ItemsLabel)}, {RunesLabel} {Caption(RuneBox(w), RunesLabel)}");
                void Placement()
                {
                    foreach (var (box, label) in new[] { (RowBox(w), ItemsLabel), (RuneBox(w), RunesLabel) })
                    {
                        // Буквы стоят на целых пикселях (Display), ширина каждой
                        // округлена — слово уходит от центра до пикселя.
                        var (shift, _) = Under(box, label);
                        Check($"«{label}» — по центру полосы",
                              Math.Abs(shift) <= 1.0, $"центр букв от центра полосы {shift:+0.##;-0.##;0}");
                        // Зазор — в пикселях экрана, как его видит игрок: разметка
                        // не округляется, и где полоса и буквы лягут на сетку,
                        // зависит от размера окна. Сдвигаем блок на долю пикселя.
                        var gaps = ScreenGaps(w, box, label);
                        Check($"«{label}» — между полосой и буквами 1 px экрана при любом сдвиге окна",
                              gaps.Count == 4 && gaps.All(g => g == 1),
                              $"пустых строк при сдвиге 0 / ¼ / ½ / ¾: {string.Join(" / ", gaps)}");
                    }
                }
                Placement();

                // Язык меняется без перезапуска: подпись и её отступ
                // (runes.aiLabelMargin) — вместе с ним. Китайский — с добавкой.
                var was = Loc.Current;
                var other = was is "ko" or "zh" or "th" ? "ru" : "zh";
                Loc.SetLanguage(other);
                Pump();
                Check($"сменили язык на {other} без перезапуска — подписи «{ItemsLabel}» и «{RunesLabel}»",
                      Caption(RowBox(w), ItemsLabel) && Caption(RuneBox(w), RunesLabel), "");
                Placement();
                Loc.SetLanguage(was);
                Pump();
                waitRow = RowBox(w)?.DesiredSize.Height ?? 0;
                waitRune = RuneBox(w)?.DesiredSize.Height ?? 0;
                if (snap is { Length: > 0 }) Snap(w, snap, "ai-1-two-enemies.png");
            }
        }

        // ── 2. Перерисовка без нового врага ─────────────────────────────────
        Call(w, "RenderBuilds", stats);
        Pump();
        var again = Bar(w);
        Check("перерисовка (ховер) — шкала на месте и не растёт заново",
              again is not null && Math.Abs(again.Width - 72.0 * 8 / 10) < 0.5
              && !again.HasAnimatedProperties,
              $"шкала {again?.Width:0.#}, анимация {again?.HasAnimatedProperties}");

        // ── 3. Пятый враг, наш последний ещё выбирает ───────────────────────
        // Красная сторона: B5 взял, R5 — наш — в свой ход навёлся на Синдру.
        // Наведённый в свой ход стоит в championId, и лока это не значит
        // (AllyWaver.Locked).
        Draft(Shen, 5, Syndra, picking: true);
        Settle();
        var wait = AiRow(w);
        var waitBar = Bar(w);
        Check("пятеро врагов, наш последний навёлся в свой ход — подбор ждёт, шкала 9/10",
              wait is { Index: -1, FillVis: Visibility.Visible }
              && waitBar is not null && Math.Abs(waitBar.Width - 72.0 * 9 / 10) < 0.5,
              $"строка {wait?.Index}, шкала {waitBar?.Width:0.#} из 72 (ждали {72.0 * 9 / 10:0.#})");

        // Взял другого. Панель никто не трогал — пересчитать должен сам пик.
        Draft(Shen, 5, Caitlyn);
        Settle();
        var full = AiRow(w);
        List<int> Expect(int last)
        {
            var a = BuildAdvisor.Adapt(stats, stats.Builds[0], Enemies, [Shen, .. Allies, last],
                                       id => engine.DamageShare(id)).Builds;
            return [.. (a.Count > 0 ? a[0].Items : stats.Builds[0].Items).Take(6)];
        }
        // Подпись — только при шкале, и пустое место с ней не выше того, что
        // на нём появилось: панель не дёргается, когда приходит подбор.
        var readyRow = RowBox(w)?.DesiredSize.Height ?? 0;
        Check("строка подбора предметов: пустая с подписью той же высоты, что с предметами; подписи больше нет",
              waitRow > 0 && Math.Abs(waitRow - readyRow) < 0.5 && !Caption(RowBox(w), ItemsLabel),
              $"пустая {waitRow:0.#}, с предметами {readyRow:0.#}, подпись {Caption(RowBox(w), ItemsLabel)}");
        if (RuneBox(w)?.Content is OverlayWindow.RuneOptionVm { Index: >= 0 })
        {
            var readyRune = RuneBox(w)!.DesiredSize.Height;
            Check("карточка подбора рун: пустая с подписью той же высоты, что со страницей; подписи больше нет",
                  waitRune > 0 && Math.Abs(waitRune - readyRune) < 0.5 && !Caption(RuneBox(w), RunesLabel),
                  $"пустая {waitRune:0.#}, со страницей {readyRune:0.#}, подпись {Caption(RuneBox(w), RunesLabel)}");
        }
        else Console.WriteLine("  подбор рун у макета не пришёл — высоту карточки сравнить не с чем");

        var got = full?.Slots.Select(x => x.Tip.Split(" · ")[0]).ToList() ?? [];
        var byLocked = Expect(Caitlyn).Select(ItemIcons.NameOf).ToList();
        Check("наш последний взял другого — без касания панели в строке подбора предметы, посчитанные по взятому",
              full is { Index: >= 0 } && got.SequenceEqual(byLocked),
              $"строка {full?.Index}: {string.Join(", ", got)} (ждали {string.Join(", ", byLocked)})");
        Console.WriteLine($"  по наведённой Синдре подбор был бы {(Expect(Syndra).SequenceEqual(Expect(Caitlyn)) ? "тем же" : "другим")}");

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
        // Владелец, 20.11: «в итемах при наведении должен объяснять, почему
        // выбрал именно эти итемы» — у каждого предмета подбора своя причина.
        Check("у каждого предмета строки подбора в подсказке — почему он здесь",
              full?.Slots.All(x => x.WhyText.Length > 0) == true,
              string.Join(" | ", full?.Slots.Select(x => x.WhyText.Replace("\n", " / ")) ?? []));
        // Владелец, 20.11, о подсказке строки: «название предмета и почему
        // именно он» — список предметов вместо общих задач состава.
        var listed = full?.WhyItems ?? [];
        Check("в подсказке строки подбора — предметы: название и почему именно он, общих задач нет",
              listed.Count > 0 && listed.All(x => x.Name.Length > 0 && x.Text.Length > 0)
              && full!.ReasonVis == Visibility.Collapsed,
              string.Join(" | ", listed.Select(x => $"{x.Name}: {x.Text.Replace("\n", " / ")}")));
        if (snap is { Length: > 0 })
        {
            Snap(w, snap, "ai-2-five-enemies.png");
            SnapTip(w, snap, "ai-3-tip-row.png", Rows(w).Count - 1, -1);
            for (var k = 0; k < 6; k++) SnapTip(w, snap, $"ai-3-tip-{k + 1}.png", Rows(w).Count - 1, k);
            SnapTip(w, snap, "ai-4-tip-popular.png", 0, 0);
        }

        // ── 4. Новый чемпион ────────────────────────────────────────────────
        Draft(Braum, 1);
        var braum = RunesClient.GetAsync(Braum, "support", CancellationToken.None).GetAwaiter().GetResult()!;
        w.ShowRunes(braum, Braum, DataDragon.Name(Braum), "support", null);
        Pump();
        // Строка нарисована с той долей, от которой шкала растёт: у нового
        // чемпиона — ноль, а не 10/10 от прошлого.
        var from = AiRow(w)?.FillWidth;
        Check("у Браума второй сборки нет — строк две, подбор второй",
              braum.Builds.Count == 1 && Rows(w).Count == 2 && AiRow(w) is { Index: -1 },
              $"сборок {braum.Builds.Count}, строк {Rows(w).Count}");
        Settle();
        var end = Bar(w);
        Check("новый чемпион — шкала растёт с нуля до доли сделанных пиков",
              from == 0 && end is not null && Math.Abs(end.Width - 72.0 * 5 / 10) < 0.5,
              $"с {from:0.#} до {end?.Width:0.#} (ждали с 0 до {72.0 * 5 / 10:0.#})");
        w.Close();
        Pump();
    }

    /// Красная сторона, роли розданы: я на поддержке и уже выбрал, трое своих
    /// тоже; последний пик драфта — наш стрелок (ячейка 4): last — его чемпион
    /// (0 — ещё не наводил), picking — ход его, чемпион только наведён. Враги
    /// взяли первых n.
    private static DraftState State(int me, int[] enemies, int last, bool picking)
    {
        var self = new DraftPlayer(0, me, 0, "utility", true);
        var team = new List<DraftPlayer> { self };
        team.AddRange(Allies.Select((id, i) => new DraftPlayer(1 + i, id, 0, AllyRoles[i], false)));
        team.Add(new DraftPlayer(4, last, 0, "bottom", false));
        var their = enemies.Select((id, i) => new DraftPlayer(5 + i, id, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: team, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: self, MyPosition: "support", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: false, ActiveCells: picking ? [4] : [],
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

    /// Контейнер строки подбора сборки (последней).
    private static ContentPresenter? RowBox(Window w) =>
        ((ItemsControl)w.FindName("BuildList")).ItemContainerGenerator
            .ContainerFromIndex(Rows(w).Count - 1) as ContentPresenter;

    /// Карточка подбора рун — в сетке панели рун.
    private static ContentPresenter? RuneBox(Window w) =>
        ((Grid)w.FindName("RuneGrid")).Children.OfType<ContentPresenter>()
            .FirstOrDefault(c => c.Content is OverlayWindow.RuneOptionVm { IsAi: true });

    private static string ItemsLabel => Loc.T("runes.aiItemsLabel");
    private static string RunesLabel => Loc.T("runes.aiRunesLabel");

    /// Видна ли внутри подпись с таким текстом.
    private static bool Caption(DependencyObject? root, string text) => FindCaption(root, text) is not null;

    private static TextBlock? FindCaption(DependencyObject? root, string text)
    {
        if (root is null) return null;
        if (root is TextBlock { IsVisible: true } t && t.Text == text) return t;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindCaption(VisualTreeHelper.GetChild(root, i), text) is { } found) return found;
        return null;
    }

    /// Подпись под полосой шкалы: насколько центр букв ушёл от центра полосы
    /// и сколько от низа полосы до верха букв — по краске, а не по рамкам
    /// элементов: у строки текста свой запас сверху.
    private static (double Shift, double Gap) Under(ContentPresenter? box, string text)
    {
        if (box is null || FindCaption(box, text) is not { } tb) return (double.NaN, double.NaN);
        box.ApplyTemplate();
        if (box.ContentTemplate?.FindName("AiFillBar", box) is not FrameworkElement fill
            || VisualTreeHelper.GetParent(fill) is not FrameworkElement track) return (double.NaN, double.NaN);
        var bar = track.TransformToAncestor(box).TransformBounds(new Rect(track.RenderSize));
        var at = tb.TransformToAncestor(box).Transform(new System.Windows.Point(0, 0));
        var ink = Ink(tb);
        if (ink.IsEmpty) return (double.NaN, double.NaN);
        var center = at.X + ink.X + ink.Width / 2;
        return (center - (bar.Left + bar.Width / 2), at.Y + ink.Y - bar.Bottom);
    }

    /// Сколько пустых строк экрана между полосой шкалы и буквами подписи —
    /// окно рисуется в 96 точек на дюйм, сдвинутым на 0, ¼, ½ и ¾ пикселя.
    /// Полоса — строки, где залитая часть заметно отличается от фона;
    /// буквы — первая строка под ней, где хоть одна точка отличается.
    private static List<int> ScreenGaps(Window w, ContentPresenter? box, string text)
    {
        var gaps = new List<int>();
        if (box is null || FindCaption(box, text) is not { } tb) return gaps;
        box.ApplyTemplate();
        if (box.ContentTemplate?.FindName("AiFillBar", box) is not FrameworkElement fill) return gaps;
        // Сдвиг — отступом блока «знак, шкала, подпись»: у игрока с другим
        // размером окна он ложится на другую долю пикселя. Блок по центру
        // строки, поэтому отступ 2·d сдвигает его на d. Высота окна для этого
        // не годится — округляется до целых; RenderTransform корня
        // RenderTargetBitmap не применяет, а окно в проверке прозрачное —
        // рисуем содержимое.
        var root = (FrameworkElement)w.Content;
        var panel = (FrameworkElement)VisualTreeHelper.GetParent(tb);
        var keep = panel.Margin;
        var dbg = Environment.GetEnvironmentVariable("CP_GAPDBG") == "1";
        try
        {
            foreach (var dy in new[] { 0.0, 0.25, 0.5, 0.75 })
            {
                panel.Margin = new Thickness(keep.Left, keep.Top + 2 * dy, keep.Right, keep.Bottom);
                w.UpdateLayout();
                var bar = fill.TransformToAncestor(root).TransformBounds(new Rect(fill.RenderSize));
                var cap = tb.TransformToAncestor(root).TransformBounds(new Rect(tb.RenderSize));
                if (dbg) Console.WriteLine($"    {text} сдвиг {dy}: полоса {bar.Top:0.###}…{bar.Bottom:0.###}, подпись с {cap.Top:0.###}");
                int W = (int)Math.Ceiling(root.ActualWidth), H = (int)Math.Ceiling(root.ActualHeight);
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(root);
                var px = new byte[W * H * 4];
                bmp.CopyPixels(px, W * 4, 0);
                int Dist(int x, int y, (int R, int G, int B) bg)
                {
                    var i = (y * W + x) * 4;
                    return Math.Abs(px[i + 2] - bg.R) + Math.Abs(px[i + 1] - bg.G) + Math.Abs(px[i] - bg.B);
                }
                int x0 = (int)Math.Ceiling(bar.Left) + 1, x1 = (int)Math.Floor(bar.Right) - 1;
                int c0 = (int)Math.Floor(cap.Left), c1 = (int)Math.Ceiling(cap.Right);
                var top = (int)Math.Floor(bar.Top);
                var bi = ((top - 3) * W + x0) * 4;                 // фон — в просвете между знаком и полосой
                var bg = (px[bi + 2], px[bi + 1], px[bi]);
                // Полоса — строки подряд с первой; буквы — первая строка с
                // краской после неё. Буквы, густо легшие в столбцы полосы, за
                // её продолжение не считаются.
                int lastBar = -1, firstInk = -1;
                var barDone = false;
                for (var y = top - 1; y < (int)Math.Ceiling(cap.Bottom) + 2 && y < H; y++)
                {
                    var barAvg = Enumerable.Range(x0, Math.Max(1, x1 - x0)).Average(x => Dist(x, y, bg));
                    var inkMax = Enumerable.Range(c0, c1 - c0).Max(x => Dist(x, y, bg));
                    if (dbg) Console.WriteLine($"    {text} сдвиг {dy} строка {y}: полоса {barAvg:0}, буквы {inkMax}");
                    if (!barDone && barAvg > 40) { lastBar = y; continue; }
                    if (lastBar >= 0) barDone = true;
                    if (barDone && inkMax > 40) { firstInk = y; break; }
                }
                gaps.Add(lastBar < 0 || firstInk < 0 ? -1 : firstInk - lastBar - 1);
            }
        }
        finally
        {
            panel.Margin = keep;
            w.UpdateLayout();
        }
        return gaps;
    }

    /// Где на подписи краска (от её угла). Рисуем копию — тот же текст и
    /// стиль того же размера, но корнем своего дерева: у вложенного элемента
    /// RenderTargetBitmap сдвигает картинку на его место в родителе, а
    /// VisualBrush в нём рисует пусто. Вчетверо крупнее — точность до четверти
    /// пикселя.
    private static Rect Ink(TextBlock e)
    {
        const int k = 4;
        int w = (int)Math.Ceiling(e.ActualWidth * k), h = (int)Math.Ceiling(e.ActualHeight * k);
        var copy = new TextBlock { Text = e.Text, Style = e.Style, Margin = new Thickness(0) };
        copy.Measure(new Size(e.ActualWidth, e.ActualHeight));
        copy.Arrange(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        copy.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * k, 96 * k, PixelFormats.Pbgra32);
        bmp.Render(copy);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int top = -1, left = w, right = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 64)
                {
                    if (top < 0) top = y;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }
        return top < 0 ? Rect.Empty : new Rect((double)left / k, (double)top / k, (double)(right + 1 - left) / k, 1);
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

    /// Подсказка предмета (slot 0…5) или всей строки (slot −1) — снимком. Саму
    /// всплывашку не открываем (это окно поверх экрана): рисуем её ToolTip
    /// отдельно, с данными слота или строки.
    private static void SnapTip(Window w, string dir, string file, int row, int slot)
    {
        var list = (ItemsControl)w.FindName("BuildList");
        if (list.ItemContainerGenerator.ContainerFromIndex(row) is not DependencyObject cp) return;
        var slots = new List<Border>();
        void Walk(DependencyObject d)
        {
            if (slot >= 0 ? d is Border { ToolTip: ToolTip, DataContext: OverlayWindow.SlotVm }
                          : d is Border { ToolTip: ToolTip, DataContext: OverlayWindow.BuildRowVm })
                slots.Add((Border)d);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(cp);
        slot = Math.Max(slot, 0);
        if (slot >= slots.Count) return;

        var tip = (ToolTip)slots[slot].ToolTip;
        tip.DataContext = slots[slot].DataContext;
        if (Application.Current.TryFindResource(typeof(ToolTip)) is Style st) tip.Style = st;
        tip.ApplyTemplate();
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tip.Arrange(new Rect(tip.DesiredSize));
        tip.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tip);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок подсказки: {Path.Combine(dir, file)}");
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
