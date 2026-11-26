using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Окно в покое не рисует кадров.
///
/// Игрок написал (17.11.2026): программа ест 10% видеокарты. Окно драфта —
/// прозрачное (AllowsTransparency, layered window) и с тенью на всём
/// содержимом. Любая бесконечная анимация внутри — дымка на фоне, мигающая
/// версия, точки ожидания — заставляет WPF 60 раз в секунду заново размывать
/// тень и перекладывать всё окно из видеокарты в память окна. Человек сидит
/// с открытым окном в лобби и в очереди часами — всё это время оно рисует
/// кадры, хотя на экране ничего не меняется.
///
/// Проверка проводит невидимое окно по экранам, где человек проводит время, и
/// на каждом ждёт, пока разовые анимации доиграют. Потом спрашивает у WPF,
/// нужен ли ему следующий кадр (часы анимаций: TimeManager.GetNextTickNeeded,
/// внутренний — публичного способа нет; подписка на CompositionTarget.Rendering
/// сама заставила бы рисовать каждый кадр). Если нужен — перечисляет, что
/// именно на экране продолжает меняться.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Разовые анимации доигрывают: вход ранга, раскрытие банов — за 2–3 с,
    // проезд блика — 5,4 с, мигание «обновление готово» — 7,2 с. Запас на
    // медленную машину и набор под нагрузкой перед выпуском.
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(12);

    // Замер видеокарты руками: IDLEFRAMES_HOLD=15 — после проверки каждый экран
    // стоит ещё 15 с, а снаружи меряется нагрузка процесса (tests/README.md).
    private static readonly int Hold =
        int.TryParse(Environment.GetEnvironmentVariable("IDLEFRAMES_HOLD"), out var h) ? h : 0;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "IdleFrames");
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

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var nextTick = NextTickProbe();

        // Блик заглушек бежит 20 с после показа — проверке хватит одного проезда.
        Skeleton.ShimmerFor = TimeSpan.FromSeconds(1);

        var w = new OverlayWindow { FollowClient = false };   // настоящий клиент LoL проверке не указ
        ShowHidden(w);
        ShadowApart(w);

        // Ждём клиента: точки мигают по делу — экран может висеть, пока человек
        // не запустит лигу. Но больше ничего, и в трее — ничего.
        Scene(w, nextTick, "ожидание клиента", () => w.ShowStatus(Loc.T("status.waitClient")),
              allowed: ["Dot1", "Dot2", "Dot3"]);
        Scene(w, nextTick, "ожидание клиента, окно в трее", w.Hide);
        w.Show();
        // Качается база: по полосе бежит блик — по делу, пока идёт загрузка.
        // Загрузка кончилась — блик стоит, и часы его анимации тоже.
        Scene(w, nextTick, "загрузка базы", () => w.ShowProgress("загрузка базы", 0.4),
              allowed: ["DlBarPulse", "Dot1", "Dot2", "Dot3"]);
        Scene(w, nextTick, "экран готовности после загрузки", () => w.ShowReady(Loc.T("status.readyIdle")));
        Scene(w, nextTick, "профиль в лобби", () =>
        {
            w.ShowSession(Session());
            w.ShowReadyPhase("Lobby");
        });
        // Обновление скачано, а программа живёт в трее сутками: мигание
        // версии должно поймать взгляд и стихнуть, а не мигать до перезапуска.
        Scene(w, nextTick, "обновление скачано", () => w.ShowUpdateReady("9.9.9"));

        var db = RecommendationEngine.FindDb();
        if (db is null) Console.WriteLine("базы нет — драфт не проверялся");
        else
        {
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();
            w.SetEngine(engine);
            Scene(w, nextTick, "драфт: баны", () =>
            {
                var d = Draft(ban: true);
                w.UpdateBans(engine.RecommendBans(d), d, engine);
            });
            Scene(w, nextTick, "драфт: пики", () => ShowDraft(w, engine, Draft(ban: false)));
            DamageWarning(w, nextTick, engine);
            PickTurn(w, nextTick, engine);
            Scene(w, nextTick, "после драфта", () => w.DraftEnded());
            // Драфт кончился, пока полоса урона мигала: окно ушло на экран
            // ожидания, а мигание на скрытой полосе продолжалось бы до
            // следующего драфта.
            Scene(w, nextTick, "после драфта с перекосом урона на экране", () =>
            {
                ShowDraft(w, engine, Skewed());
                w.DraftEnded();
            });
        }

        w.Close();
        app.Shutdown();
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: в покое окно кадров не рисует"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Scene(OverlayWindow w, Func<TimeSpan> nextTick, string what, Action enter,
                              string[]? allowed = null)
    {
        enter();
        try { SceneCore(w, nextTick, what, allowed); }
        finally
        {
            if (Hold > 0)
            {
                Console.WriteLine($"    {DateTime.Now:HH:mm:ss} держу {Hold} с: {what}");
                Wait(TimeSpan.FromSeconds(Hold));
            }
        }
    }

    private static void SceneCore(OverlayWindow w, Func<TimeSpan> nextTick, string what, string[]? allowed)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < Settle)
        {
            Wait(TimeSpan.FromMilliseconds(100));
            if (nextTick() < TimeSpan.Zero) break;
        }
        if (nextTick() < TimeSpan.Zero)
        {
            Check(what, true, $"затихло через {sw.Elapsed.TotalSeconds:0.0} с");
            return;
        }
        var moving = Moving(w);
        var ok = allowed is not null && moving.All(m => allowed.Any(a => m.StartsWith(a + ".")));
        Check(what, ok, ok
            ? $"крутится только положенное: {string.Join("; ", moving)}"
            : $"через {Settle.TotalSeconds:0} с WPF всё ещё рисует кадр за кадром: {string.Join("; ", moving)}");
    }

    /// <summary>
    /// Таймер хода пика (25–26.11, владелец: «проверь, чтобы анимации не
    /// съедали много GPU, как у нас было до этого»). Отсчёт в слотах — не
    /// анимация, а смена картинки: WPF кадров сам не просит, окно
    /// перерисовывается, когда меняется кольцо или цифра, — не чаще 4 раз в
    /// секунду. Мигание рамки центра в последние 8 секунд моего хода —
    /// единственное, что крутится, и около 24 кадров в секунду. В чужой ход
    /// не мигает ничего. Ход кончился — тишина, такт отсчёта стоит.
    /// </summary>
    private static void PickTurn(OverlayWindow w, Func<TimeSpan> nextTick, RecommendationEngine engine)
    {
        const BindingFlags inst = BindingFlags.Instance | BindingFlags.NonPublic;
        var clock = (PickClock)typeof(OverlayWindow).GetField("_pickClock", inst)!.GetValue(w)!;
        DispatcherTimer? Tick() => typeof(OverlayWindow).GetField("_pickTick", inst)!.GetValue(w) as DispatcherTimer;
        var frame = (FrameworkElement)w.FindName("PickAlarmFrame");

        // Мой ход: навёл, не взял; топ ниже пикает со мной в паре.
        var full = Draft(ban: false);
        var team = full.MyTeam.Select(p => p.CellId is 3 or 4 ? p with { ChampionId = 0, PickIntentId = p.ChampionId } : p).ToList();
        var d = full with { MyTeam = team, Me = team[3], MyPickActionId = 7, MyPickInProgress = true, ActiveCells = [3, 4] };
        Scene(w, nextTick, "драфт: идёт мой ход, отсчёт в слотах", () =>
        {
            w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
            w.SetPickTimer(DateTime.UtcNow.AddSeconds(29), 30000, mine: true);
        });

        // Сколько раз в секунду меняется картинка отсчёта: изменения одного
        // такта (цифра и кольцо разом) — одна перерисовка.
        var redraws = new HashSet<long>();
        void Count(object? s, PropertyChangedEventArgs e) => redraws.Add(Environment.TickCount64 / 20);
        clock.PropertyChanged += Count;
        Wait(TimeSpan.FromSeconds(3));
        clock.PropertyChanged -= Count;
        var perSec = redraws.Count / 3.0;
        Check("отсчёт меняет картинку не чаще 4–5 раз в секунду", clock.Visibility == Visibility.Visible && perSec <= 5,
              $"{perSec:0.0} перерисовок в секунду");

        // Последние 8 секунд моего хода: крутится только рамка, около 24 кадров в секунду.
        w.SetPickTimer(DateTime.UtcNow.AddSeconds(6), 30000, mine: true);
        Wait(TimeSpan.FromMilliseconds(400));
        var moving = Moving(w);
        Check("последние секунды моего хода: крутится только рамка центра",
              frame.IsVisible && moving.All(m => m.StartsWith("PickAlarmFrame.")), string.Join("; ", moving));
        var fps = FramesPerSecond(frame);
        Check($"мигание рамки — около {Loops.BlinkFps} кадров в секунду, не 60 и не рвётся",
              fps is > Loops.BlinkFps / 2 and <= Loops.BlinkFps + 4, $"{fps} кадров за секунду");

        // Ход кончился сам: рамка снята, кадров не просит, такт стоит.
        Wait(TimeSpan.FromSeconds(6));
        Check("ход кончился: рамки нет, кадров не просит, такт отсчёта стоит",
              !frame.IsVisible && nextTick() < TimeSpan.Zero && Tick()?.IsEnabled != true,
              $"рамка {frame.Visibility}, следующий кадр {(nextTick() < TimeSpan.Zero ? "не нужен" : "нужен")}, " +
              $"такт {(Tick()?.IsEnabled == true ? "идёт" : "стоит")}");

        // Чужой ход, последние секунды: отсчёт красный, но не мигает ничего.
        w.SetPickTimer(DateTime.UtcNow.AddSeconds(6), 30000, mine: false);
        Wait(TimeSpan.FromMilliseconds(500));
        Check("последние секунды чужого хода: ничего не крутится",
              clock.Urgent && !frame.IsVisible && nextTick() < TimeSpan.Zero,
              nextTick() < TimeSpan.Zero ? "кадров не просит" : string.Join("; ", Moving(w)));
        w.SetPickTimer(null, 0, mine: false);
        Wait(TimeSpan.FromMilliseconds(200));
    }

    /// <summary>
    /// Перекос урона (26.11): у врагов одни физики — полоса урона и подпись
    /// «чем закрыться» мигают, пока перекос есть, и не чаще 24 кадров в
    /// секунду (мигание часами в драфте, а каждый кадр — всё окно). Перекос
    /// ушёл — тишина: мигание снималось BeginAnimation(…, null), и его часы
    /// тикали до сборки мусора.
    /// </summary>
    private static void DamageWarning(OverlayWindow w, Func<TimeSpan> nextTick, RecommendationEngine engine)
    {
        var warn = (FrameworkElement)w.FindName("EnemyDmgWarn");
        Scene(w, nextTick, "драфт: перекос урона у врагов", () => ShowDraft(w, engine, Skewed()),
              allowed: ["EnemyDmgBar", "EnemyDmgWarn", "MyDmgBar", "MyDmgWarn"]);
        Check("перекос урона на экране", warn.IsVisible,
              warn.IsVisible ? "подпись видна" : "подписи нет — в базе нет замеров урона или состав не тот");
        var fps = FramesPerSecond(warn);
        Check($"мигание перекоса — около {Loops.BlinkFps} кадров в секунду, не 60",
              fps is > Loops.BlinkFps / 2 and <= Loops.BlinkFps + 4, $"{fps} кадров за секунду");
        Scene(w, nextTick, "драфт: перекос урона ушёл", () => ShowDraft(w, engine, Draft(ban: false)));
    }

    /// Сколько раз за секунду сменилась прозрачность элемента — столько кадров
    /// анимации WPF ей дал. Считаем смены, а не разные значения: кадры
    /// выровнены по частоте экрана, и у мигания туда-обратно значения на спаде
    /// и на подъёме совпадают — разных выходит меньше, чем кадров.
    private static int FramesPerSecond(UIElement e)
    {
        var changes = 0;
        var last = e.Opacity;
        var sampler = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(1) };
        sampler.Tick += (_, _) => { if (e.Opacity != last) { changes++; last = e.Opacity; } };
        sampler.Start();
        Wait(TimeSpan.FromSeconds(1));
        sampler.Stop();
        return changes;
    }

    private static void ShowDraft(OverlayWindow w, RecommendationEngine engine, DraftState d) =>
        w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);

    /// Тень окна — на подложке рядом с содержимым, не на его предке. Эффект
    /// на предке размывается заново на каждый кадр любой анимации внутри — по
    /// всему окну. Тогда даже разовая анимация на драфте стоит целого окна.
    private static void ShadowApart(OverlayWindow w)
    {
        var bad = new List<string>();
        for (DependencyObject? p = (DependencyObject)w.FindName("RootGrid"); p is not null;
             p = VisualTreeHelper.GetParent(p))
            if (p is UIElement { Effect: { } fx } e)
                bad.Add($"{Describe(e)} ({e.GetType().Name}): {fx.GetType().Name}");
        Check("тень не на предке содержимого", bad.Count == 0,
              bad.Count == 0 ? "эффектов от RootGrid до окна нет" : string.Join("; ", bad));
    }

    // ── Часы анимаций WPF ─────────────────────────────────────────────────────

    /// Сколько ждать до следующего кадра, который понадобился анимациям: ноль —
    /// нужен прямо сейчас (что-то крутится), отрицательное — не нужен вовсе.
    /// Так же решает сам WPF, ставить ли следующий проход отрисовки.
    private static Func<TimeSpan> NextTickProbe()
    {
        const BindingFlags any = BindingFlags.Instance | BindingFlags.Static |
                                 BindingFlags.Public | BindingFlags.NonPublic;
        var asm = typeof(Visual).Assembly;
        var mcType = asm.GetType("System.Windows.Media.MediaContext")
                     ?? throw new InvalidOperationException("в WPF нет MediaContext — сменилась версия, проверку переписать");
        var mc = mcType.GetMethod("From", any, [typeof(Dispatcher)])?.Invoke(null, [Dispatcher.CurrentDispatcher])
                 ?? throw new InvalidOperationException("MediaContext.From не нашёлся");
        var tm = mcType.GetProperty("TimeManager", any)?.GetValue(mc)
                 ?? throw new InvalidOperationException("MediaContext.TimeManager не нашёлся");
        var m = tm.GetType().GetMethod("GetNextTickNeeded", any)
                ?? throw new InvalidOperationException("TimeManager.GetNextTickNeeded не нашёлся");
        return () => (TimeSpan)m.Invoke(tm, null)!;
    }

    // ── Что именно меняется ───────────────────────────────────────────────────

    /// Всё в дереве окна, у чего анимированное свойство за 0,3 с поменяло
    /// значение. Анимация, доигравшая и застывшая на последнем кадре, тоже
    /// «анимирована», но не меняется — её не называем.
    private static List<string> Moving(Window w)
    {
        var probes = new List<(string Who, DependencyObject Obj, DependencyProperty Dp, object? Was)>();
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);

        void Inspect(DependencyObject o, string who, int depth)
        {
            if (depth > 3 || !seen.Add(o)) return;
            if (o is Animatable { HasAnimatedProperties: true } or UIElement { HasAnimatedProperties: true })
                foreach (var dp in Props(o))
                    if (DependencyPropertyHelper.GetValueSource(o, dp).IsAnimated)
                        probes.Add((who, o, dp, o.GetValue(dp)));
            // Вложенные кисти, трансформы и эффекты: их анимации — тоже кадры окна.
            foreach (var dp in Props(o))
                if (o.GetValue(dp) is Animatable sub and not { IsFrozen: true })
                    Inspect(sub, $"{who}.{dp.Name}", depth + 1);
        }

        void Walk(DependencyObject v)
        {
            Inspect(v, Describe(v), 0);
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(v); i++)
                Walk(VisualTreeHelper.GetChild(v, i));
        }

        Walk(w);
        Wait(TimeSpan.FromMilliseconds(300));
        var moving = probes.Where(p => !Equals(p.Obj.GetValue(p.Dp), p.Was))
                           .Select(p => $"{p.Who}.{p.Dp.Name}")
                           .Distinct().ToList();
        return moving.Count > 0 ? moving : ["что именно — не нашлось (анимация вне дерева окна?)"];
    }

    private static IEnumerable<DependencyProperty> Props(DependencyObject o) =>
        TypeDescriptor.GetProperties(o, [new PropertyFilterAttribute(PropertyFilterOptions.All)])
            .Cast<PropertyDescriptor>()
            .Select(DependencyPropertyDescriptor.FromProperty)
            .Where(d => d is not null && !d.IsReadOnly)
            .Select(d => d!.DependencyProperty);

    /// Имя элемента, а у безымянного — тип и ближайший именованный предок.
    private static string Describe(DependencyObject v)
    {
        if (v is FrameworkElement { Name.Length: > 0 } named) return named.Name;
        for (var p = VisualTreeHelper.GetParent(v); p is not null; p = VisualTreeHelper.GetParent(p))
            if (p is FrameworkElement { Name.Length: > 0 } f) return $"{v.GetType().Name} в {f.Name}";
        return v.GetType().Name;
    }

    // ── Сцены ─────────────────────────────────────────────────────────────────

    private static SessionTracker.SessionData Session()
    {
        var now = DateTime.Now;
        var last5 = new List<SessionTracker.RecentGame>
        {
            new(201, true, 21), new(412, false, -18), new(201, true, 20),
            new(117, true, 22), new(412, false, -19),
        };
        var wr = Enumerable.Range(0, 20).Select(i => new SessionTracker.WrPoint(now.AddDays(-20 + i), 50 + i % 5)).ToList();
        var lp = Enumerable.Range(0, 20).Select(i => new SessionTracker.LpPoint(now.AddDays(-20 + i), 1900 + i * 9)).ToList();
        return new SessionTracker.SessionData("IdleSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    HasRank: true, Tier: "EMERALD", Division: "II", Lp: 73, ProgressPct: 73,
                    Wins: 63, Losses: 55, Winrate: 53.4, Last5: last5,
                    WinrateHistory: wr, RatingHistory: lp),
            });
    }

    // Соло/дуо, я — Браум в саппорте.
    private static DraftState Draft(bool ban)
    {
        var my = new List<DraftPlayer>
        {
            new(0, ban ? 0 : 126, 0, "middle", false),
            new(1, ban ? 0 : 17,  0, "jungle", false),
            new(2, ban ? 0 : 222, 0, "bottom", false),
            new(3, ban ? 0 : 201, 0, "utility", true),
            new(4, ban ? 0 : 106, 0, "top", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, ban ? 0 : 238, 0, "", false), new(6, ban ? 0 : 157, 0, "", false),
            new(7, ban ? 0 : 893, 0, "", false), new(8, ban ? 0 : 203, 0, "", false),
            new(9, ban ? 0 : 555, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: ban, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: ban ? 0 : -1, MyBanInProgress: ban, MyBanDone: !ban);
    }

    // Тот же драфт, у врагов одни физики: Зед, Ясуо, Дрейвен, Киндред, Пайк.
    private static DraftState Skewed() => Draft(ban: false) with
    {
        TheirTeam =
        [
            new(5, 238, 0, "", false), new(6, 157, 0, "", false), new(7, 119, 0, "", false),
            new(8, 203, 0, "", false), new(9, 555, 0, "", false),
        ],
    };

    // ── Служебное ─────────────────────────────────────────────────────────────

    /// Подождать, не останавливая диспетчер: часы анимаций и отрисовка идут.
    private static void Wait(TimeSpan t)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = t };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
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
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what} — {detail}");
        if (!ok) _fails++;
    }
}
