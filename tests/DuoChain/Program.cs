using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counterplay;
using Button = System.Windows.Controls.Button;

namespace Counterplay.Tests;

/// <summary>
/// Вся цепочка дуо-пула, от передачи до цифр на экране.
///
/// Звенья по отдельности уже проверены, а ломается обычно стык — поэтому здесь
/// один сквозной проход:
///
///   друг выгружает пул → я загружаю его в половину «Дуо» → пул знает, ЧЕЙ он
///   → мы играем → связки считаются по этому человеку → окно показывает их и
///   называет его по имени → чужие связки сюда не попадают.
///
/// Главное, что тут утверждается: привязка идёт по puuid, а имя — только для
/// показа. Сменилось имя — счёт тот же; другой человек — счёта нет.
///
/// Настоящие pools.json и настройки сохраняются и возвращаются на место.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Me = "ME0000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Him = "HIM000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Other = "OTH000000000000000000000000000000000000000000000000000000000000000000000";

    private const int MyChamp = 412, HisChamp = 22, AlienChamp = 64;

    private static string PoolsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "pools.json");

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var before = File.Exists(PoolsPath) ? File.ReadAllText(PoolsPath) : null;
        var wasAllTime = AppSettings.Current.DuoWinratesAllTime;
        // Окно пулов запоминает своё место и деление — чужие значения не трогаем.
        var wasSplit = AppSettings.Current.PoolSplit;
        var wasGeom = (AppSettings.Current.PoolWinLeft, AppSettings.Current.PoolWinTop,
                       AppSettings.Current.PoolWinWidth, AppSettings.Current.PoolWinHeight);
        try
        {
            if (before is not null) File.Delete(PoolsPath);
            AppSettings.Current.DuoWinratesAllTime = true;   // связки за всё время
            Run();
        }
        finally
        {
            SessionTracker.Preview = null;
            AppSettings.Current.DuoWinratesAllTime = wasAllTime;
            AppSettings.Current.PoolSplit = wasSplit;
            (AppSettings.Current.PoolWinLeft, AppSettings.Current.PoolWinTop,
             AppSettings.Current.PoolWinWidth, AppSettings.Current.PoolWinHeight) = wasGeom;
            AppSettings.SaveQuiet();
            if (before is not null) File.WriteAllText(PoolsPath, before);
            else if (File.Exists(PoolsPath)) File.Delete(PoolsPath);
            Console.WriteLine("\nсвои пулы и настройки возвращены на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: цепочка держится от передачи до цифр"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // ── 1. Друг выгружает свой личный пул ──────────────────────────────
        PoolStore.SetAccount(Him, "Harribon");
        var his = new ChampPool { Id = "h1", Name = "стрелки" };
        his.ByRole["adc"] = [HisChamp];
        var file = PoolFile.Export(his);
        Check("1. в файле уехали и имя, и человек",
              file.Contains("Harribon") && file.Contains(Him), "да");

        // ── 2. Я загружаю его в половину «Дуо» ─────────────────────────────
        PoolStore.SetAccount(Me, "я");
        var (pool, _, ownerPuuid, ownerNick) = PoolFile.ParseWithOwner(file);
        var a = PoolStore.Current();
        var mine = new ChampPool { Id = "m1", Name = "саппорты" };
        mine.ByRole["support"] = [MyChamp];
        a.Pools.Add(mine);

        var duo = new DuoPool
        {
            Id = "d1",
            FriendName = $"{mine.Name} + {pool!.Name}",
            FriendPuuid = ownerPuuid,
            FriendNick = ownerNick,
        };
        duo.Mine["support"] = [MyChamp];
        duo.Friend["adc"] = [HisChamp];
        a.DuoPools.Add(duo);
        PoolStore.Persist();
        PoolStore.SetFavourite(PoolKind.Duo, "d1");

        Check("2. пул знает, чей он", duo.FriendPuuid == Him, Tail(duo.FriendPuuid));
        Check("   и как его зовут", duo.FriendNick == "Harribon", duo.FriendNick);

        // ── 3. Мы играем: связки копятся на ЭТОГО человека ─────────────────
        // Плюс связка с посторонним — она в раздел попасть не должна.
        SessionTracker.Preview =
        [
            new(Him, "Harribon", "solo", MyChamp, HisChamp, 10, 7),
            // Тот же человек, но чемпионы НЕ из пула: играли вместе, пул не
            // использовали. В счёт связки это идти не должно — иначе цифры
            // обещают «винрейт пула», а показывают винрейт знакомства.
            new(Him, "Harribon", "solo", AlienChamp, AlienChamp, 4, 4),
            new(Other, "Посторонний", "solo", MyChamp, AlienChamp, 5, 5),
        ];

        var (g, w) = SessionTracker.PairStats(Him, MyChamp, HisChamp);
        Check("3. счёт связки читается", g == 10 && w == 7, $"{w}-{g - w}");

        // ── 4. Окно показывает его связки и называет по имени ──────────────
        var win = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        ShowHidden(win);
        Pump();
        var texts = Walk<TextBlock>(win).Select(t => t.Text).ToList();

        Check("4. заголовок называет напарника",
              texts.Any(t => t.Contains("Harribon")), "есть");
        Check("   счёт связки показан", texts.Any(t => t.Contains("7-3")),
              texts.FirstOrDefault(t => t.Contains("-")) ?? "нет");
        Check("   чужая связка не показана", !texts.Any(t => t.Contains("5-0")),
              texts.Any(t => t.Contains("5-0")) ? "показана" : "нет");
        Check("   игра с ним МИМО пула не показана", !texts.Any(t => t.Contains("4-0")),
              texts.Any(t => t.Contains("4-0")) ? "показана" : "нет");
        win.Close();
        Pump();

        // ── 5. Привязка по человеку, а не по имени ─────────────────────────
        duo.FriendNick = "Переименовался";
        PoolStore.Persist();
        var (g2, w2) = SessionTracker.PairStats(Him, MyChamp, HisChamp);
        Check("5. сменил имя — счёт тот же", g2 == 10 && w2 == 7, $"{w2}-{g2 - w2}");

        var (g3, _) = SessionTracker.PairStats(Other, MyChamp, HisChamp);
        Check("   у другого человека этой связки нет", g3 == 0, g3.ToString());

        // ── 6. В драфте пара строится вокруг него ──────────────────────────
        var team = new List<DraftPlayer>
        {
            new(0, MyChamp, 0, "support", true, Puuid: Me),
            new(1, AlienChamp, 0, "jungle", false, Puuid: Other),
            new(2, HisChamp, 0, "adc", false, Puuid: Him),
        };
        var champ = Party.MateChampion(Draft(team), duo);
        Check("6. в драфте напарник найден по пулу", champ == HisChamp,
              $"{champ} (ждали {HisChamp})");

        app.Shutdown();
    }

    private static DraftState Draft(IReadOnlyList<DraftPlayer> team) => new(
        MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
        Me: team.FirstOrDefault(p => p.IsLocalPlayer), MyPosition: "utility",
        DirectOpponent: null, ExposedToCounter: false, InBanPhase: false,
        Bench: [], IsAram: false, MyPickActionId: -1, MyPickInProgress: false,
        ActiveCells: [], FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);

    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Walk<T>(c)) yield return x;
        }
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static string Tail(string? s) =>
        string.IsNullOrEmpty(s) ? "—" : s[..3] + "…(" + s.Length + ")";

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
