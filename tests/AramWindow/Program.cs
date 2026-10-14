using System.IO;
using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Ширина окна драфта, когда центру показать нечего.
///
/// В ARAM первые снимки champ select приходят без скамейки и без розданного
/// чемпиона — подбирать не из чего; в банфазе советы ещё не посчитаны; в
/// драфте без роли подбора не будет совсем. Раньше во всех этих случаях
/// оверлей показывал экран ожидания, а тот ужимает окно до ширины сайдбара.
/// Окно надо было возвращать (ARAM оставался узким), а конец такого драфта
/// запоминал узкое как раскладку — и следующий драфт открывался в 340 px:
/// одна колонка своих, центр и враги за краем (скриншот владельца, 15.10).
/// Теперь центр — заглушка, и окно остаётся видом драфта.
///
/// Проверка гоняет НАСТОЯЩЕЕ окно оверлея. Настройки — во временной папке:
/// конец драфта записывает раскладку, и с настоящим ui.json проверка
/// переписывала бы окно игрока (а синхронизация везла бы это дальше).
/// </summary>
internal static class Program
{
    private static int _fails;

    // Уже этого запомненное окно — не раскладка драфта (RememberMinW в коде окна).
    private const double DraftMinW = 800;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AramWindow");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;

        // WPF нужен Application: без него окно не создать.
        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        try
        {
            EmptyCenter();
            Remembered();
            NarrowRemembered();
            EmptyDraftEnd();
        }
        finally
        {
            app.Shutdown();
            AppPaths.RootOverride = null;
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "ИТОГ: пустой центр — заглушка, окно остаётся драфтом и помнит размер"
            : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Пустой подбор посреди драфта — заглушка в центре, а не экран ожидания.
    ///
    /// Запомненного размера здесь нет: проверка про то, что окно НЕ сжимается,
    /// то есть про размер по раскладке, а запомненная раскладка задала бы его
    /// насильно, и все окна вышли бы одинаковыми.
    /// </summary>
    private static void EmptyCenter()
    {
        var s = AppSettings.Current;
        s.DraftPlacement = "remember";
        s.DraftWidth = 0; s.DraftHeight = 0;

        // Окно А — как в ARAM: сперва подбирать не из чего, затем кандидаты есть.
        var a = Open();
        a.UpdateRecommendations([], Aram(bench: []), null);
        Pump();
        var empty = a.Width;
        var skel  = Vis(a, "CenterSkeleton") && Vis(a, "FullView") && !Vis(a, "IdlePanel");
        Console.WriteLine($"скамейки нет:     {empty:0} px, в центре {(skel ? "заглушка" : "НЕ заглушка")}");

        a.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        var after = a.Width;
        Console.WriteLine($"скамейка есть:    {after:0} px");

        // Окно Б — контроль: те же пики сразу. Точную ширину задаёт раскладка,
        // поэтому сверяем не с числом, а с контрольным окном.
        var b = Open();
        b.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        var direct = b.Width;
        Console.WriteLine($"сразу пики:       {direct:0} px");

        // Окно В — банфаза, советов ещё нет.
        var c = Open();
        c.UpdateBans([], BanPhase(), null);
        Pump();
        var bans = c.Width;
        var banSkel = Vis(c, "CenterSkeleton") && !Vis(c, "IdlePanel");
        Console.WriteLine($"баны без советов: {bans:0} px, в центре {(banSkel ? "заглушка" : "НЕ заглушка")}");

        Check("пустой подбор — заглушка в центре, а не экран ожидания", skel, "");
        Check("пустой подбор не сужает окно", Math.Abs(empty - direct) < 2, $"{empty:0} против {direct:0}");
        Check("кандидаты пришли — заглушка ушла, пики на месте",
              !Vis(a, "CenterSkeleton") && Vis(a, "RecScroll"), "");
        Check("итог тот же, что без пустого подбора", Math.Abs(after - direct) < 2, $"{after:0} против {direct:0}");
        Check("баны без советов — заглушка, окно шириной с драфт",
              banSkel && Math.Abs(bans - direct) < 2, $"{bans:0} против {direct:0}");

        a.Close(); b.Close(); c.Close();
        Pump();
    }

    /// <summary>
    /// Запомненная раскладка переживает пустой подбор.
    ///
    /// Восстановленный размер надо положить ещё и в «сохранённый полный»:
    /// обновляет его только обработчик изменения размера, а во время
    /// восстановления он нарочно заглушён — иначе затёр бы то, что ставим.
    /// Без этого там оставался размер ПО УМОЛЧАНИЮ, и возврат режима
    /// подсовывал его вместо запомненного, а конец драфта записывал поверх. В
    /// журнале владельца это выглядело как 1452×901 три драфта подряд, а потом
    /// вдруг 1320×848.
    /// </summary>
    private static void Remembered()
    {
        Console.WriteLine();
        var s = AppSettings.Current;
        // Размер нарочно не круглый и не совпадающий с умолчаниями.
        s.DraftPlacement = "remember";
        s.DraftLeft = 220; s.DraftTop = 140;
        s.DraftWidth = 1452; s.DraftHeight = 901;

        var w = Open();

        // Драфт первый: раскладка встаёт из настроек.
        w.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        Check("первый драфт: раскладка встала",
              Math.Abs(w.Width - 1452) < 2 && Math.Abs(w.Height - 901) < 2,
              $"{w.Width:0}×{w.Height:0}");
        w.UpdateRecommendations(null, null);       // драфт кончился
        Pump();

        // Раскладку поменяли между драфтами. «Сохранённый размер режима» при
        // этом остался прежним: его ведёт только изменение размера окна, а про
        // раскладку он не знает. Вот на этом расхождении всё и ломалось —
        // возврат режима подставлял старое, НЕ трогая положение.
        s.DraftWidth = 1500; s.DraftHeight = 950;

        w.UpdateRecommendations([], Aram(bench: []), null);   // подбирать не из чего
        Pump();
        w.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        Check("второй драфт: размер из раскладки, а не из прошлого",
              Math.Abs(w.Width - 1500) < 2 && Math.Abs(w.Height - 950) < 2,
              $"{w.Width:0}×{w.Height:0} (ждали 1500×950)");

        w.Close();
        Pump();
    }

    /// <summary>
    /// Узкая запомненная раскладка не применяется. У владельца в настройках
    /// лежало 340×784 — панель профиля, снятая концом драфта, — и каждый драфт
    /// открывался в ней. Такие значения уже есть у игроков и ездят
    /// синхронизацией, поэтому мало перестать их записывать — надо не верить
    /// уже записанным.
    /// </summary>
    private static void NarrowRemembered()
    {
        Console.WriteLine();
        var s = AppSettings.Current;
        s.DraftPlacement = "remember";
        s.DraftLeft = 220; s.DraftTop = 140;
        s.DraftWidth = 340; s.DraftHeight = 784;

        var w = Open();
        w.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        Check("запомненные 340×784 не встают — окно шириной с драфт",
              w.Width >= DraftMinW, $"{w.Width:0}×{w.Height:0}");

        w.Close();
        Pump();
    }

    /// <summary>
    /// Драфт, в котором подбирать было нечего, не запоминает панель профиля.
    /// Так 340×784 у владельца и записалось (журнал программы, 14.10 18:36):
    /// драфт без роли, кандидатов 0 — окно ужалось до сайдбара; началась игра
    /// — конец драфта запомнил узкое как раскладку.
    /// </summary>
    private static void EmptyDraftEnd()
    {
        Console.WriteLine();
        var s = AppSettings.Current;
        s.DraftPlacement = "remember";
        s.DraftLeft = 220; s.DraftTop = 140;
        s.DraftWidth = 1452; s.DraftHeight = 901;

        var w = Open();
        w.UpdateRecommendations([], NoRole(), null);   // роль не раздана — подбора нет
        Pump();
        w.UpdateRecommendations(null, null);           // игра началась — драфт кончился
        Pump();
        Check("драфт без подбора не запомнил узкое окно",
              s.DraftWidth >= DraftMinW, $"запомнено {s.DraftWidth:0}×{s.DraftHeight:0} (было 1452×901)");

        w.Close();
        Pump();
    }

    /// Окно за пределами экрана: проверка не мигает им на рабочем столе.
    private static OverlayWindow Open()
    {
        var w = new OverlayWindow { Left = -4000, Top = -4000 };
        ShowHidden(w);
        Pump();
        return w;
    }

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    /// Обычный снимок драфта (не ARAM) в банфазе: свой бан ещё не сделан.
    private static DraftState BanPhase()
    {
        var me = new DraftPlayer(0, 0, 0, "utility", true);
        return new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: true);
    }

    /// Драфт без роли (обычная игра вслепую, своя игра): пики идут, подбора нет.
    private static DraftState NoRole()
    {
        var me = new DraftPlayer(0, 0, 0, "", true);
        return new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    /// Снимок champ select ARAM: чемпион не роздан, роли отсутствуют, врагов не видно.
    private static DraftState Aram(IReadOnlyList<int> bench)
    {
        var me = new DraftPlayer(0, 0, 0, "", true);
        return new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: bench, IsAram: true,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static Recommendation Rec() => new(
        ChampionId: 64, Score: 1.0, BaseDelta: 1.0, DirectDelta: 0, OtherDelta: 0,
        SynergyDelta: 0, ComfortDelta: 0, StyleDelta: 0, Reasons: ["проверка"]);

    /// Прокрутить очередь WPF: UpdateRecommendations кладёт работу через Dispatcher.
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// <summary>
    /// Показать окно так, чтобы человек его не увидел.
    ///
    /// Одной прозрачности мало: она работает только у окон с
    /// AllowsTransparency, а у окна пулов его нет — там Opacity=0 давал чёрный
    /// прямоугольник посреди экрана. Поэтому ещё и уводим за край, отключив
    /// центрирование: WindowStartupLocation по умолчанию перебивает Left/Top,
    /// выставленные до Show.
    ///
    /// Раскладка, размеры и обход дерева при этом работают как обычно.
    /// </summary>
    private static void ShowHidden(System.Windows.Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;

        // Попиксельная прозрачность ОБЯЗАТЕЛЬНА: без неё Opacity=0 не прячет
        // окно, а красит его в чёрный — окно пулов так и вылезало чёрным
        // прямоугольником. Оба свойства можно менять только до Show, здесь это
        // и делается. У оверлея они уже такие, ему не повредит.
        //
        // Одного выноса за край тоже мало: окна возвращают себя на экран сами —
        // оверлей «вписывается в экран», окно пулов встаёт по запомненной
        // раскладке.
        w.WindowStyle = System.Windows.WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;

        w.Show();
        // ПОСЛЕ показа не двигаем: запомненную раскладку окно ставит себе само,
        // и проверки геометрии меряют именно её. Тем, у кого раскладки нет,
        // хватает вынесенного старта.
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
