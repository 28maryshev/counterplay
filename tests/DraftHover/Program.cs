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

        var w = new OverlayWindow();
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
