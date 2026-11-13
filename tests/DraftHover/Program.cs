using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Подсветка по наведению на карточку пика.
///
/// Владелец: «опять через одного персонажа показывает кружочки синергии и
/// контрпика: на одного наводишь — показывает, на следующем нет».
///
/// Причина: колесо у дна списка команды запоминает позицию ниже дна. Кандидат,
/// который создаёт связку, добавляет в этот список её бледную карточку, список
/// вырастает и доезжает до запомненной позиции. Сдвиг списка стирал подсветку
/// (иначе она повисла бы не на тех портретах), поэтому у Жанны со связкой
/// подсветка мигала и пропадала, а у Тарика без связки — держалась.
///
/// Сцена со скриншота: я — Браум в саппорте, союзники Джейс, Тимо, Джинкс,
/// Волибир; враги Зед, Ясуо, Аврора, Киндред, Пайк. Нет базы — проверять нечего.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "DraftHover");
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечем"); return 0; }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Assets(playerDir);

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Hover(engine);
        BanHover(engine);
        app.Shutdown();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: подсветка держится у каждого кандидата"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Hover(RecommendationEngine engine)
    {
        var s = AppSettings.Current;
        s.DraftHover = true;
        // Низкое окно: состав обязан не влезать, иначе прокручивать нечего.
        s.DraftWidth = 1432; s.DraftHeight = 560; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow { FollowClient = false };   // настоящий клиент LoL проверке не указ
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();

        var draft = Scene();
        w.UpdateRecommendations(engine.Recommend(draft, s.DraftCount), draft, engine);
        Pump();

        var list   = (ItemsControl)w.FindName("FullRecList");
        var art    = (Canvas)w.FindName("SynLinks");
        var combos = (ItemsControl)w.FindName("MyTeamCombos");
        var team   = (ScrollViewer)w.FindName("TeamScroll");
        var cards  = Enumerable.Range(0, list.Items.Count)
            .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i) as DependencyObject)
            .Select(c => c is null ? null : Card(c)).OfType<Border>().ToList();
        Console.WriteLine($"кандидатов: {cards.Count}; список команды {team.ExtentHeight:F0} при окне {team.ViewportHeight:F0}");
        Check("состав не влезает — есть что прокручивать", team.ExtentHeight > team.ViewportHeight + 1,
              $"{team.ExtentHeight:F0} / {team.ViewportHeight:F0}");
        // Стиль команды — под составом, вне прокрутки: длинный состав его не
        // уносит. Строки «Роль: … — контрпик + синергия» в шапке нет (28.10).
        var style = (FrameworkElement)w.FindName("MyTeamStyle");
        var styleTop = style.TranslatePoint(new System.Windows.Point(0, 0), team).Y;
        Check("стиль команды — под составом", style.Visibility == Visibility.Visible
              && ((TextBlock)style).Text.Length > 0 && styleTop >= team.ActualHeight,
              $"«{((TextBlock)style).Text}» сверху {styleTop:F0} при высоте списка {team.ActualHeight:F0}");
        Check("строки статуса в шапке нет", w.FindName("StatusText") is null, "");

        // Колесо у дна: запомненная позиция уходит ниже дна (так ведёт себя
        // ScrollViewer — колесо прибавляет к позиции, а не к пределу).
        team.ScrollToVerticalOffset(team.ExtentHeight * 10);
        Pump();
        var bottom = team.VerticalOffset;
        var baseCombos = combos.Items.Count;
        Console.WriteLine($"прокручен до дна: {bottom:F0}");

        var withCombo = new List<string>();
        var without   = new List<string>();
        var lost      = new List<string>();
        var jumped    = new List<string>();
        Border? prev = null;
        for (var round = 0; round < 2; round++)
            foreach (var b in cards)
            {
                if (prev is not null) Raise(prev, Mouse.MouseLeaveEvent);
                Raise(b, Mouse.MouseEnterEvent);
                var drawn = art.Children.Count;
                var grew  = combos.Items.Count > baseCombos;
                Pump();
                var name = DataDragon.Name((int)b.Tag);
                Console.WriteLine($"  {name,-14} нарисовано {drawn,2}, после раскладки {art.Children.Count,2}" +
                                  $"{(grew ? ", +связка" : "")}, позиция {team.VerticalOffset:F0}");
                if (round == 0) (grew ? withCombo : without).Add(name);
                if (drawn > 0 && art.Children.Count == 0) lost.Add(name);
                if (Math.Abs(team.VerticalOffset - bottom) > 0.5) jumped.Add($"{name} {team.VerticalOffset:F0}");
                prev = b;
            }

        Check("есть кандидаты со связкой и без — оба случая проверены",
              withCombo.Count > 0 && without.Count > 0,
              $"со связкой: {string.Join(", ", withCombo)}; без: {string.Join(", ", without)}");
        Check("подсветка не пропадает ни у кого", lost.Count == 0, string.Join(", ", lost.Distinct()));
        Check("список команды не прыгает от карточки связки", jumped.Count == 0, string.Join(", ", jumped.Distinct()));

        // Список сдвинулся под наведённой карточкой — подсветка переезжает
        // вместе с портретами, а не пропадает.
        var withArt = cards.FirstOrDefault(c => DataDragon.Name((int)c.Tag) == withCombo.FirstOrDefault()) ?? cards[0];
        if (prev is not null) Raise(prev, Mouse.MouseLeaveEvent);
        Raise(withArt, Mouse.MouseEnterEvent);
        Pump();
        var before = art.Children.Count;
        var ringY  = FirstRingY(art);
        team.ScrollToVerticalOffset(0);
        Pump();
        var after = FirstRingY(art);
        Check("прокрутка под наведением: подсветка на месте", art.Children.Count == before && before > 0,
              $"{before} → {art.Children.Count}");
        Check("…и переехала вместе с портретами",
              ringY is { } y1 && after is { } y2 && Math.Abs(y2 - y1) > 1,
              $"{ringY:F0} → {after:F0}");

        Raise(withArt, Mouse.MouseLeaveEvent);
        Pump();
        Check("курсор ушёл — подсветки нет, связки как в драфте",
              art.Children.Count == 0 && combos.Items.Count == baseCombos,
              $"на канвасе {art.Children.Count}, связок {combos.Items.Count}");
        w.Close();
    }

    /// <summary>
    /// Наведение на бан. Владелец 13.11: «при наведении на баны видно, насколько
    /// они мешают союзникам, вокруг их иконки — красный кружок, как у нас на
    /// этапе пиков».
    ///
    /// Следом (13.11): «сверху над банами — топ наигранных за последнее время,
    /// как на прошлом этапе, и при наведении — какой бан против них тоже плох,
    /// красным свечением».
    ///
    /// Сцена: фаза банов, мой бан не сделан, вся команда навела пики (Гарен,
    /// Ли Син, Ари, Джинкс, я — Браум), союзник уже забанил Нами; за месяц на
    /// саппорте я играл Сону, Хвэя, Юми, Нами, Эш, Лулу и Карму. Меряет: полоса «мои
    /// чемпионы» над советами — шестеро самых игранных (Карма седьмая — не
    /// влезла), Нами перечёркнута; у
    /// каждого совета кольца стоят ровно у тех наших и тех моих из полосы, у
    /// кого движок видит заметную разницу (|BanThreat| ≥ 0,3), числа — эта
    /// разница («vs» у состава), цвет по знаку (минус — красное); у Нами
    /// кольца нет; примерки в мой слот нет; хоть один совет мешает и составу, и
    /// моим; названный в подписи («контрит союзника X», «мешает и твоему пулу
    /// (X)») — в минусе; тир-лист в банах рисует то же, курсор ушёл — пусто; в
    /// объявлении пиков тир-лист не рисует ничего.
    /// </summary>
    private static void BanHover(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("— наведение на бан —");
        var s = AppSettings.Current;
        s.DraftHover = true; s.BansTierList = true;
        s.DraftWidth = 1432; s.DraftHeight = 900;
        SessionTracker.PreviewGames = MyMonth();

        var w = new OverlayWindow { FollowClient = false };
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();

        var draft = BanScene();
        var bans = engine.RecommendBans(draft);
        w.UpdateBans(bans, draft, engine);
        Pump();

        var art  = (Canvas)w.FindName("SynLinks");
        var list = (ItemsControl)w.FindName("BanFullList");
        var cards = Enumerable.Range(0, list.Items.Count)
            .Select(i => list.ItemContainerGenerator.ContainerFromIndex(i) as DependencyObject)
            .Select(c => c is null ? null : Card(c)).OfType<Border>().ToList();
        Check("советы банов на экране", cards.Count > 0, $"{cards.Count}");

        var mineList = (ItemsControl)w.FindName("BanMineList");
        var chips = mineList.Items.OfType<BanMineChip>().ToList();
        Check("над советами — шесть моих по числу игр (седьмая Карма не влезла), забаненная Нами перечёркнута",
              ((FrameworkElement)w.FindName("BanMinePanel")).Visibility == Visibility.Visible
              && chips.Select(c => c.ChampionId).SequenceEqual([Sona, Hwei, Yuumi, Nami, Ashe, Lulu])
              && chips.Single(c => c.ChampionId == Nami).Gone && chips.Count(c => c.Gone) == 1,
              string.Join(", ", chips.Select(c => DataDragon.Name(c.ChampionId) + (c.Gone ? " ✕" : ""))));

        var red = System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x5E);
        var green = System.Windows.Media.Color.FromRgb(0x4C, 0xE3, 0x8B);
        string Txt(double v) => (v >= 0 ? "+" : "") + v.ToString("F1");
        // Кольца у портретов состава — дугой с разрывом под значок стиля, у
        // иконок полосы — сплошные: по ним и различаем.
        List<System.Windows.Shapes.Path> Rings(bool mine) => [.. art.Children.OfType<System.Windows.Shapes.Path>()
            .Where(r => r.Data is EllipseGeometry == mine)];
        List<string> TeamDrawn() => [.. art.Children.OfType<StackPanel>()
            .Select(sp => string.Join(" ", sp.Children.OfType<TextBlock>().Select(t => t.Text))).OrderBy(t => t)];
        List<string> MineDrawn() => [.. art.Children.OfType<TextBlock>().Select(t => t.Text).OrderBy(t => t)];
        List<(string Name, double Vs)> TeamWant(int id) => [.. draft.MyTeam
            .Select(p => (DataDragon.Name(p.EffectiveChampionId),
                          engine.BanThreat(id, p.EffectiveChampionId, RecommendationEngine.LcuToDbRole(p.Position))))
            .Where(x => Math.Abs(x.Item2) >= 0.3)];
        List<(string Name, double Vs)> MineWant(int id) => [.. chips.Where(c => !c.Gone && c.ChampionId != id)
            .Select(c => (DataDragon.Name(c.ChampionId), engine.BanThreat(id, c.ChampionId, "support")))
            .Where(x => Math.Abs(x.Item2) >= 0.3)];
        // Всё нарисованное — ровно по движку: кольца, их цвета и числа.
        string? Mismatch(int id)
        {
            var team = TeamWant(id); var mine = MineWant(id);
            var teamText = team.Select(x => "vs " + Txt(x.Vs)).OrderBy(t => t).ToList();
            var mineText = mine.Select(x => Txt(x.Vs)).OrderBy(t => t).ToList();
            var all = Rings(false).Concat(Rings(true)).ToList();
            var reds = all.Count(r => r.Stroke is SolidColorBrush sb && sb.Color == red);
            var greens = all.Count(r => r.Stroke is SolidColorBrush sb && sb.Color == green);
            var both = team.Concat(mine).ToList();
            var previews = art.Children.OfType<System.Windows.Shapes.Ellipse>().Count();
            if (Rings(false).Count == team.Count && Rings(true).Count == mine.Count
                && reds == both.Count(x => x.Vs < 0) && greens == both.Count(x => x.Vs >= 0)
                && TeamDrawn().SequenceEqual(teamText) && MineDrawn().SequenceEqual(mineText) && previews == 0)
                return null;
            return $"{DataDragon.Name(id)}: состав [{string.Join("; ", TeamDrawn())}] ждали [{string.Join("; ", teamText)}], " +
                   $"мои [{string.Join("; ", MineDrawn())}] ждали [{string.Join("; ", mineText)}], примерок {previews}";
        }

        var wrong = new List<string>();
        var anyRed = false;
        var anyMine = false;
        foreach (var b in cards)
        {
            var id = (int)b.Tag;
            Raise(b, Mouse.MouseEnterEvent);
            Pump();
            var team = TeamWant(id); var mine = MineWant(id);
            Console.WriteLine($"  {DataDragon.Name(id),-12} состав: {string.Join(", ", team.Select(x => $"{x.Name} {x.Vs:+0.0;-0.0}"))}" +
                              $"   мои: {string.Join(", ", mine.Select(x => $"{x.Name} {x.Vs:+0.0;-0.0}"))}");
            if (Mismatch(id) is { } m) wrong.Add(m);
            anyRed  |= Rings(false).Any(r => r.Stroke is SolidColorBrush sb && sb.Color == red);
            anyMine |= Rings(true).Any(r => r.Stroke is SolidColorBrush sb && sb.Color == red);

            // Назван в подписи — значит против него в минусе.
            var reason = ((RecCard)b.DataContext).Reason;
            foreach (var (champ, role) in draft.MyTeam
                         .Select(p => (p.EffectiveChampionId, RecommendationEngine.LcuToDbRole(p.Position)))
                         .Concat(chips.Where(c => !c.Gone).Select(c => (c.ChampionId, "support"))))
            {
                var name = DataDragon.Name(champ);
                if (!reason.Contains(name)) continue;
                var vs = engine.BanThreat(id, champ, role);
                if (vs >= 0) wrong.Add($"{DataDragon.Name(id)}: в подписи «{name}», а разница {vs:+0.00;-0.00}");
            }
            Raise(b, Mouse.MouseLeaveEvent);
            Pump();
        }
        Check("у каждого совета — кольца и числа у состава и у моих ровно по движку, без примерки",
              wrong.Count == 0, string.Join(" | ", wrong));
        Check("хоть один совет мешает кому-то из наших — красное кольцо", anyRed, "");
        Check("хоть один совет мешает кому-то из моих в полосе — красное кольцо", anyMine, "");
        Check("курсор ушёл с карточки — подсветки нет", art.Children.Count == 0, $"{art.Children.Count}");

        // Тир-лист в банах: клик там банит, значит и наведение — про бан.
        var tier = (ItemsControl)w.FindName("TierList");
        var cells = Cells(tier);
        var cell = cells.FirstOrDefault(c => TeamWant((int)c.Tag).Count > 0 && MineWant((int)c.Tag).Count > 0);
        Check("в тир-листе есть мешающий и составу, и моим", cell is not null, $"ячеек {cells.Count}");
        if (cell is not null)
        {
            Raise(cell, Mouse.MouseEnterEvent);
            Pump();
            var m = Mismatch((int)cell.Tag);
            Check($"тир-лист в банах ({DataDragon.Name((int)cell.Tag)}): кольца по движку", m is null, m ?? "");
            Raise(cell, Mouse.MouseLeaveEvent);
            Pump();
            Check("курсор ушёл с тир-листа — подсветки нет", art.Children.Count == 0, $"{art.Children.Count}");
        }

        // Объявление пиков: тир-лист тот же, но клик там наводит пик, а не бан —
        // колец угрозы быть не должно.
        w.UpdateBans(null, draft with { InPlanning = true }, engine);
        Pump();
        var planCell = Cells(tier).FirstOrDefault(c => TeamWant((int)c.Tag).Count > 0);
        if (planCell is not null)
        {
            Raise(planCell, Mouse.MouseEnterEvent);
            Pump();
            Check("объявление пиков: наведение на тир-лист ничего не рисует",
                  art.Children.Count == 0, $"{art.Children.Count}");
            Raise(planCell, Mouse.MouseLeaveEvent);
        }
        else Check("объявление пиков: тир-лист на экране", false, "ячеек нет");
        w.Close();
        SessionTracker.PreviewGames = null;
    }

    // Ячейки тир-листа, по которым можно навести: не забаненные.
    private static List<FrameworkElement> Cells(DependencyObject d)
    {
        var res = new List<FrameworkElement>();
        void Walk(DependencyObject x)
        {
            if (x is FrameworkElement { Name: "TierCellRoot", DataContext: TierCell { Banned: false } } fe) res.Add(fe);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(x); i++) Walk(VisualTreeHelper.GetChild(x, i));
        }
        Walk(d);
        return res;
    }

    private const int Sona = 37, Hwei = 910, Yuumi = 350, Nami = 267, Ashe = 22, Lulu = 117, Karma = 43;

    // Мой месяц на саппорте: больше всех Сона; у Лулу и Кармы по игре, Лулу
    // свежее — шестая она, Карма седьмая и в полосу не влезает.
    private static List<SessionTracker.PreviewGame> MyMonth()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();
        foreach (var (champ, n) in new[] { (Sona, 6), (Hwei, 5), (Yuumi, 4), (Nami, 3), (Ashe, 2), (Lulu, 1), (Karma, 1) })
            for (var i = 0; i < n; i++)
                games.Add(new("", "", "solo", champ, 0, "support", "", now - 3600 * (games.Count + 1), i % 2 == 0));
        return games;
    }

    // Фаза банов, мой бан впереди, команда навела пики, союзник забанил Нами; враги скрыты.
    private static DraftState BanScene()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 0, 86,  "top", false),      // Гарен
            new(1, 0, 64,  "jungle", false),   // Ли Син
            new(2, 0, 103, "middle", false),   // Ари
            new(3, 0, 222, "bottom", false),   // Джинкс
            new(4, 0, 201, "utility", true),   // Браум — я
        };
        var their = Enumerable.Range(5, 5).Select(c => new DraftPlayer(c, 0, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [Nami], TheirTeamBans: [],
            Me: my[4], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [4],
            FirstPickCell: -1, MyBanActionId: 1, MyBanInProgress: true, MyBanDone: false);
    }

    private static DraftState Scene()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 126, 0, "middle", false),   // Джейс
            new(1, 17,  0, "jungle", false),   // Тимо
            new(2, 222, 0, "bottom", false),   // Джинкс
            new(3, 201, 0, "utility", true),   // Браум — я, залочен
            new(4, 106, 0, "top", false),      // Волибир
        };
        // Соло/дуо: роли врагов клиент не раскрывает.
        var their = new List<DraftPlayer>
        {
            new(5, 238, 0, "", false), new(6, 157, 0, "", false), new(7, 893, 0, "", false),
            new(8, 203, 0, "", false), new(9, 555, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
    }

    /// Верх первого кольца на канвасе — по нему видно, переехала ли подсветка.
    private static double? FirstRingY(Canvas art) =>
        art.Children.OfType<System.Windows.Shapes.Path>().Select(p => (double?)p.Data.Bounds.Top).FirstOrDefault();

    private static Border? Card(DependencyObject d)
    {
        if (d is Border b && b.Tag is int) return b;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Card(VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    private static void Raise(UIElement e, RoutedEvent ev) =>
        e.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = ev });

    /// Имена и иконки из папки игрока — копией в свою папку.
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

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
