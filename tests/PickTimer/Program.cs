using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Таймер хода пика (владелец, 25.11.2026): в слоте — сколько осталось до
/// конца хода; пикнул — отсчёт пропадает; в мой ход последние 5 секунд центр
/// окна мигает рамкой по краю. Отсчёт — у всех, чей ход, союзников и врагов
/// («таймер должен быть у всех»), рамка — только мой ход.
///
/// Две половины. Срок хода — из события клиента: разбор таймера, «чей ход» и
/// «мой ли» по настоящему разбору сессии (лок завершает действие — у него
/// отсчёта больше нет). Окно — невидимое, по шагам хода: отсчёт идёт сам
/// между событиями, строка слота при этом не пересоздаётся (иначе волна
/// «сейчас пикает» проигрывалась бы каждую секунду), рамка включается на 5
/// секундах моего хода и снимается вместе с анимацией — по локу, по концу
/// времени, по концу драфта; в ход врагов — отсчёт у них, рамки нет.
/// Нет базы — окно не проверяется.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PickTimer");
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

        Console.WriteLine("срок хода из события клиента:");
        Deadlines();

        var db = RecommendationEngine.FindDb();
        if (db is null) Console.WriteLine("базы нет — окно не проверялось");
        else
        {
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Console.WriteLine("окно:");
            Window(engine);
            app.Shutdown();
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: таймер хода стоит у тех, чей ход, и пропадает с пиком"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── Срок хода ──────────────────────────────────────────────────────────

    private static void Deadlines()
    {
        var now = new DateTime(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        const string myTurn = """{"phase":"BAN_PICK","adjustedTimeLeftInPhase":27400,"timeLeftInPhase":28000,"totalTimeInPhase":30000,"isInfinite":false}""";

        var (d, t) = Session(myTurn, inProgress: true, completed: false);
        var at = t.PickTurnDeadline(d, now);
        Check("мой ход: срок — сейчас + adjustedTimeLeftInPhase, ход мой",
              at == now.AddMilliseconds(27400) && d.MyPickInProgress,
              at is null ? "срока нет" : $"+{(at.Value - now).TotalMilliseconds} мс, мой {d.MyPickInProgress}");
        Check("длина хода — totalTimeInPhase", t.TotalMs == 30000, $"{t.TotalMs}");

        // Пикают союзник и враг (парный ход), мой ход впереди: срок тот же,
        // отсчёт у обоих, ход не мой.
        (d, t) = Session(myTurn, inProgress: false, completed: false, others: "2,7");
        at = t.PickTurnDeadline(d, now);
        Check("ход союзника и врага: срок есть, у обоих, ход не мой",
              at == now.AddMilliseconds(27400) && !d.MyPickInProgress && d.ActiveCells.SequenceEqual([2, 7]),
              $"{Show(at, now)}, чей ход {string.Join(",", d.ActiveCells)}, мой {d.MyPickInProgress}");

        (d, t) = Session(myTurn, inProgress: false, completed: false);
        Check("пиков никто не делает (баны, объявление) — срока нет", t.PickTurnDeadline(d, now) is null,
              Show(t.PickTurnDeadline(d, now), now));

        (d, t) = Session(myTurn, inProgress: true, completed: true);
        Check("пик залочен (действие завершено) — срока нет", t.PickTurnDeadline(d, now) is null,
              Show(t.PickTurnDeadline(d, now), now));

        (d, t) = Session("""{"phase":"BAN_PICK","adjustedTimeLeftInPhase":27400,"isInfinite":true}""",
                         inProgress: true, completed: false);
        Check("драфт без срока — отсчёта нет", t.PickTurnDeadline(d, now) is null, Show(t.PickTurnDeadline(d, now), now));

        (d, t) = Session(myTurn, inProgress: true, completed: false);
        Check("ARAM — отсчёта нет", t.PickTurnDeadline(d with { IsAram = true }, now) is null, "");

        (d, t) = Session("""{"phase":"BAN_PICK","timeLeftInPhase":12000}""", inProgress: true, completed: false);
        Check("без adjustedTimeLeftInPhase — по timeLeftInPhase",
              t.PickTurnDeadline(d, now) == now.AddMilliseconds(12000), Show(t.PickTurnDeadline(d, now), now));

        (d, t) = Session(null, inProgress: true, completed: false);
        Check("таймера в событии нет — отсчёта нет", t.PickTurnDeadline(d, now) is null, Show(t.PickTurnDeadline(d, now), now));
    }

    // Сессия чемп-селекта, как её присылает клиент: я — саппорт, клетка 3, мой
    // пик — одно действие; others — клетки, чей пик идёт сейчас (по действию в
    // одной группе, как парный ход).
    private static (DraftState, DraftTimer) Session(string? timer, bool inProgress, bool completed, string others = "")
    {
        var other = string.Join("", others.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(c =>
            $$""",{"id":{{20 + int.Parse(c)}},"actorCellId":{{c}},"type":"pick","championId":0,"isInProgress":true,"completed":false}"""));
        var json = $$"""
        {
          "localPlayerCellId": 3,
          "myTeam": [{"cellId":3,"championId":{{(completed ? 201 : 0)}},"championPickIntent":201,"assignedPosition":"utility"}],
          "theirTeam": [],
          "actions": [[{"id":7,"actorCellId":3,"type":"pick","championId":201,
                        "isInProgress":{{(inProgress ? "true" : "false")}},"completed":{{(completed ? "true" : "false")}}}{{other}}]]
          {{(timer is null ? "" : $",\"timer\": {timer}")}}
        }
        """;
        var s = JsonDocument.Parse(json).RootElement;
        return (ChampSelectParser.Parse(s), DraftTimer.Parse(s));
    }

    private static string Show(DateTime? at, DateTime now) =>
        at is null ? "срока нет" : $"срок +{(at.Value - now).TotalMilliseconds} мс";

    // ── Окно ───────────────────────────────────────────────────────────────

    private static void Window(RecommendationEngine engine)
    {
        var w = new OverlayWindow { FollowClient = false };   // настоящий клиент LoL проверке не указ
        ShowHidden(w);
        w.SetEngine(engine);
        var d = Scene();
        w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
        Pump();

        var list  = (ItemsControl)w.FindName("MyTeamList");
        var frame = (FrameworkElement)w.FindName("PickAlarmFrame");
        PickClock Clock() => ((ChampSlotCard)list.Items[3]).Clock;

        Check("до хода отсчёта нет", Clock().Visibility != Visibility.Visible, Clock().Visibility.ToString());

        // Ход начался: 12,5 с.
        var deadline = DateTime.UtcNow.AddMilliseconds(12500);
        w.SetPickTimer(deadline, 30000, mine: true);
        Pump();
        Check("в моём слоте — отсчёт", Clock().Visibility == Visibility.Visible && Clock().Seconds == "13",
              $"{Clock().Visibility}, «{Clock().Seconds}»");
        Check("цифры видны на экране", ShownText(list, 3).Contains("13"), string.Join(" ", ShownText(list, 3)));
        // Парный ход: топ ниже меня пикает со мной — отсчёт и у него, тот же.
        // Взявшие и враги (не их ход) — без отсчёта.
        var enemies = (ItemsControl)w.FindName("EnemyTeamList");
        ChampSlotCard Slot(ItemsControl l, int i) => (ChampSlotCard)l.Items[i];
        Check("отсчёт у всех, чей ход: у меня и у союзника в паре, тот же",
              ReferenceEquals(Slot(list, 4).Clock, Clock()) && ShownText(list, 4).Contains("13"),
              string.Join(" ", ShownText(list, 4)));
        Check("у взявших и у врагов не в их ход отсчёта нет",
              new[] { 0, 1, 2 }.All(i => ReferenceEquals(Slot(list, i).Clock, PickClock.None))
              && Enumerable.Range(0, 5).All(i => ReferenceEquals(Slot(enemies, i).Clock, PickClock.None)), "");
        Check("до последних 5 секунд рамки нет", frame.Visibility != Visibility.Visible, frame.Visibility.ToString());
        Check("цвет — свой, золотой", !Clock().Urgent, Clock().Color);
        Snap(w, "calm");
        var row = list.ItemContainerGenerator.ContainerFromIndex(3);
        var arc = Clock().Arc;

        // Событий нет — окно считает само.
        // Сверка с настоящим остатком: машина под нагрузкой ждёт дольше, чем
        // просили. Такт отсчёта — 100 мс, отсюда «или на секунду больше».
        PumpFor(1300);
        var due = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalMilliseconds / 1000);
        Check("между событиями отсчёт идёт сам",
              Clock().Seconds != "13" && (Clock().Seconds == $"{due}" || Clock().Seconds == $"{due + 1}"),
              $"«{Clock().Seconds}», по часам {due}");
        Check("кольцо убывает", !ReferenceEquals(arc, Clock().Arc) && Clock().Arc.Bounds.Height > 0, "");
        Check("строка моего слота не пересоздана", ReferenceEquals(row, list.ItemContainerGenerator.ContainerFromIndex(3)), "");

        // Событие драфта посреди хода (союзник навёл другого) — строка та же.
        var d2 = d with { MyTeam = d.MyTeam.Select(p => p.CellId == 1 ? p with { ChampionId = 0, PickIntentId = 64 } : p).ToList() };
        // Срок в событии заметно дальше счёта окна (~11 с): видно, что отсчёт
        // встал по событию. Пересчёт подбора на шаге — до секунды.
        w.UpdateRecommendations(engine.Recommend(d2, AppSettings.Current.DraftCount), d2, engine);
        w.SetPickTimer(DateTime.UtcNow.AddMilliseconds(20500), 30000, mine: true);
        Pump();
        Check("событие посреди хода: строка моего слота та же",
              ReferenceEquals(row, list.ItemContainerGenerator.ContainerFromIndex(3)), "");
        Check("событие выровняло отсчёт", Clock().Seconds is "21" or "20", $"«{Clock().Seconds}»");

        // Последние секунды.
        w.SetPickTimer(DateTime.UtcNow.AddMilliseconds(4500), 30000, mine: true);
        Pump();
        Check("5 секунд: рамка центра мигает", frame.Visibility == Visibility.Visible && frame.HasAnimatedProperties,
              $"{frame.Visibility}, анимация {(frame.HasAnimatedProperties ? "есть" : "нет")}");
        Check("5 секунд: отсчёт красный", Clock().Urgent && Clock().Seconds == "5", $"{Clock().Color}, «{Clock().Seconds}»");
        Snap(w, "alarm");

        // Пик залочен — следующее событие приносит «срока нет».
        w.SetPickTimer(null, 0, mine: false);
        Pump();
        Check("пикнул — отсчёт пропал", Clock().Visibility != Visibility.Visible, Clock().Visibility.ToString());
        Check("пикнул — рамки нет и не мигает", frame.Visibility != Visibility.Visible && !frame.HasAnimatedProperties,
              $"{frame.Visibility}, анимация {(frame.HasAnimatedProperties ? "есть" : "нет")}");

        // Время вышло без события: клиент сам берёт наведённого или закрывает драфт.
        w.SetPickTimer(DateTime.UtcNow.AddMilliseconds(400), 30000, mine: true);
        Pump();
        var onAtEnd = frame.Visibility == Visibility.Visible;
        PumpFor(700);
        Check("время вышло — отсчёт и рамка ушли сами",
              onAtEnd && Clock().Visibility != Visibility.Visible && frame.Visibility != Visibility.Visible
              && !frame.HasAnimatedProperties,
              $"рамка в конце хода {(onAtEnd ? "была" : "НЕ была")}, отсчёт {Clock().Visibility}, рамка {frame.Visibility}");

        // Ход врагов: я и топ уже взяли, пикают двое последних врагов.
        var d3 = d with
        {
            MyTeam = d.MyTeam.Select(p => p.CellId is 3 or 4 ? p with { ChampionId = p.PickIntentId, PickIntentId = 0 } : p).ToList(),
            MyPickActionId = -1, MyPickInProgress = false, ActiveCells = [8, 9],
        };
        d3 = d3 with { Me = d3.MyTeam[3] };
        w.UpdateRecommendations(engine.Recommend(d3, AppSettings.Current.DraftCount), d3, engine);
        w.SetPickTimer(DateTime.UtcNow.AddMilliseconds(4500), 30000, mine: false);
        Pump();
        Check("ход врагов: отсчёт у обоих врагов, у меня нет",
              Slot(enemies, 3).Clock.Visibility == Visibility.Visible && ReferenceEquals(Slot(enemies, 4).Clock, Slot(enemies, 3).Clock)
              && ShownText(enemies, 3).Contains("5") && ReferenceEquals(Clock(), PickClock.None),
              $"«{Slot(enemies, 3).Clock.Seconds}», на экране {string.Join(" ", ShownText(enemies, 3))}");
        Check("ход не мой: последние секунды красные, но рамки центра нет",
              Slot(enemies, 3).Clock.Urgent && frame.Visibility != Visibility.Visible,
              $"{Slot(enemies, 3).Clock.Color}, рамка {frame.Visibility}");
        Snap(w, "enemy");
        w.SetPickTimer(null, 0, mine: false);
        w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
        Pump();

        // Додж посреди моего хода — драфт кончился.
        w.SetPickTimer(DateTime.UtcNow.AddMilliseconds(3000), 30000, mine: true);
        Pump();
        w.UpdateRecommendations(null, null);
        w.DraftEnded();
        Pump();
        Check("драфт кончился посреди хода — отсчёта и рамки нет",
              Clock().Visibility != Visibility.Visible && frame.Visibility != Visibility.Visible,
              $"отсчёт {Clock().Visibility}, рамка {frame.Visibility}");

        w.Close();
    }

    // Соло/дуо, я — Браум в саппорте и сейчас мой ход: навёл, не взял. Союзники
    // выше меня взяли, топ ниже — наводит.
    private static DraftState Scene()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 126, 0, "middle", false),
            new(1, 17,  0, "jungle", false),
            new(2, 222, 0, "bottom", false),
            new(3, 0, 201, "utility", true),
            new(4, 0, 106, "top", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, 238, 0, "", false), new(6, 157, 0, "", false),
            new(7, 893, 0, "", false), new(8, 0, 0, "", false), new(9, 0, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: true, ActiveCells: [3, 4],
            FirstPickCell: 0, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
    }

    // Снимок окна для глаз: PICKTIMER_SNAP=<папка> — туда pick-timer-<что>.png.
    // Рисуется содержимое окна (RootGrid), а не само окно: у невидимого окна
    // прозрачность 0. Рамка снимается на вершине мигания.
    private static void Snap(OverlayWindow w, string what)
    {
        if (Environment.GetEnvironmentVariable("PICKTIMER_SNAP") is not { Length: > 0 } dir) return;
        var frame = (FrameworkElement)w.FindName("PickAlarmFrame");
        var anim = frame.HasAnimatedProperties;
        if (anim) { frame.BeginAnimation(UIElement.OpacityProperty, null); frame.Opacity = 1; }
        var view = (FrameworkElement)w.FindName("RootGrid");
        var size = new Rect(0, 0, view.ActualWidth, view.ActualHeight);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0B, 0x16, 0x21)), null, size);
            dc.DrawRectangle(new VisualBrush(view) { Viewbox = size, ViewboxUnits = BrushMappingMode.Absolute,
                                                     Stretch = Stretch.Fill }, null, size);
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using (var f = File.Create(Path.Combine(dir, $"pick-timer-{what}.png"))) enc.Save(f);
        if (anim)
            frame.BeginAnimation(UIElement.OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(1, 0.15, TimeSpan.FromSeconds(0.45))
                { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
    }

    // Тексты, которые сейчас нарисованы в строке слота.
    private static List<string> ShownText(ItemsControl list, int i)
    {
        var found = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement { IsVisible: false }) return;
            if (d is TextBlock tb && tb.Text.Length > 0) found.Add(tb.Text);
            for (var k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++) Walk(VisualTreeHelper.GetChild(d, k));
        }
        if (list.ItemContainerGenerator.ContainerFromIndex(i) is { } row) Walk(row);
        return found;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
        }
    }

    // Ждёт, не останавливая окно: такты таймера отсчёта идут в его потоке.
    private static void PumpFor(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _fails++;
    }
}
