using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Ширина окна после экрана ожидания.
///
/// В ARAM первые снимки champ select приходят без скамейки и без розданного
/// чемпиона — подбирать не из чего, и оверлей показывает экран ожидания. Тот
/// ужимает окно до ширины сайдбара. Когда скамейка приходит и кандидаты
/// появляются, окно обязано вернуться к раскладке драфта, а оно оставалось
/// узким: восстановление размера пропускалось, если раскладка уже применена.
///
/// Проверка гоняет НАСТОЯЩЕЕ окно оверлея по тем же двум шагам.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // WPF нужен Application: без него окно не создать.
        var app = new System.Windows.Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        // Окно А — как в ARAM: сперва подбирать не из чего, затем кандидаты есть.
        var a = Open();
        a.UpdateRecommendations([], Aram(bench: []), null);
        Pump();
        var idle = a.Width;
        Console.WriteLine($"через экран ожидания: сайдбар {idle:0} px");

        a.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        var viaIdle = a.Width;
        Console.WriteLine($"                      после пиков {viaIdle:0} px");

        // Окно Б — контроль: те же пики сразу, без экрана ожидания. Точную ширину
        // задаёт раскладка из настроек игрока, поэтому сверяем не с числом, а с
        // контрольным окном: проход через ожидание не должен менять итог.
        var b = Open();
        b.UpdateRecommendations([Rec()], Aram(bench: [64, 103, 1]), null);
        Pump();
        var direct = b.Width;
        Console.WriteLine($"сразу пики:           {direct:0} px");

        Check("экран ожидания сужает окно", idle < direct - 1, $"{idle:0} против {direct:0}");
        Check("окно не осталось сайдбаром", viaIdle > idle + 1, $"{viaIdle:0} против {idle:0}");
        Check("итог тот же, что без ожидания", Math.Abs(viaIdle - direct) < 2,
              $"{viaIdle:0} против {direct:0}");

        a.Close(); b.Close();
        Pump();
        app.Shutdown();

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "ИТОГ: окно возвращается из экрана ожидания"
            : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Окно за пределами экрана: проверка не мигает им на рабочем столе.
    private static OverlayWindow Open()
    {
        var w = new OverlayWindow { Left = -4000, Top = -4000 };
        w.Show();
        Pump();
        return w;
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
