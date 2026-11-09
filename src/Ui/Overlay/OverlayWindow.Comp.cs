using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Окно драфта — полоса состава под колонками команд: что у команды есть и чего
// нет (дыры состава, те же, что у подбора), и план на игру своей команды.
//
// Владелец, 09.11 («делай всё» по предложениям): дыра — свойство команды, а не
// кандидата, поэтому красные теги «дыра так и останется» ушли с карточек сюда;
// у врагов та же полоса — видно, что у них можно наказать.
public partial class OverlayWindow
{
    // С двух выбранных — раньше «чего нет» значит только «ещё не выбрали».
    private const int COMP_MIN_PICKS = 2;

    private static readonly Brush CompPendingFg = Frozen(Color.FromRgb(0x7C, 0x97, 0xAC), 0xFF);
    private static readonly Brush CompPendingLine = Frozen(Color.FromRgb(0x7C, 0x97, 0xAC), 0x50);

    /// <summary>
    /// Полосы обеих команд и план своей. Зовётся из RenderTeams на каждое
    /// событие драфта — составы там уже собраны.
    /// </summary>
    private void RenderComp(IReadOnlyList<int> allyIds, IReadOnlyList<int> enemyIds)
    {
        var mine = CompChecks(allyIds, enemyIds);
        var theirs = CompChecks(enemyIds, allyIds);
        MyComp.ItemsSource = mine;
        EnemyComp.ItemsSource = theirs;
        MyCompHeader.Visibility = mine.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyCompHeader.Visibility = theirs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var plan = PlanText(allyIds);
        MyPlan.Text = plan;
        MyPlan.Visibility = plan.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Проверки состава команды против соперника. Всегда — фронт, контроль,
    /// чистка волн; ключевая часть стиля (отход у пик/пока, инициация у дайва) —
    /// при явном стиле; защита керри — если есть гиперкерри; урон по танкам —
    /// если у соперника их двое; ранняя игра — только когда её нет. Чего нет —
    /// ровно то, что видит подбор (<see cref="RecommendationEngine.TeamGaps"/>
    /// без роли); тип урона здесь не повторяем — его показывает полоса урона
    /// над колонкой.
    /// </summary>
    public static List<CompCheckVm> CompChecks(IReadOnlyList<int> team, IReadOnlyList<int> foes)
    {
        if (team.Count < COMP_MIN_PICKS) return [];
        var missing = RecommendationEngine.TeamGaps(team, foes, null).ToDictionary(g => g.Kind);
        var complete = team.Count >= 5;

        var kinds = new List<(string Kind, Func<int, bool> Fills)> { ("needFront", ChampionTraits.Frontline) };
        switch (ChampionTraits.DominantStyle(team))
        {
            case ChampionTraits.Arch.PickPoke: kinds.Add(("needDisengage", ChampionTraits.Disengager)); break;
            case ChampionTraits.Arch.Dive:     kinds.Add(("needEngage", ChampionTraits.Initiator)); break;
        }
        if (team.Any(ChampionTraits.Hypercarry)) kinds.Add(("needPeel", ChampionTraits.Peeler));
        kinds.Add(("needCc", ChampionTraits.HasHardCc));
        if (foes.Count(ChampionTraits.IsTanky) >= 2) kinds.Add(("needAntiTank", ChampionTraits.AntiTank));
        kinds.Add(("needWaveclear", ChampionTraits.Waveclear));
        if (missing.ContainsKey("needEarly")) kinds.Add(("needEarly", a => ChampionTraits.EarlyPower(a) >= 3));

        var list = new List<CompCheckVm>();
        foreach (var (kind, fills) in kinds)
        {
            var has = team.Where(fills).Select(DataDragon.Name).ToList();
            var gap = missing.GetValueOrDefault(kind);
            // Контроль считается «есть» с двоих: один — ещё дыра.
            var state = gap is null ? 1 : complete ? -1 : 0;
            var tip = state > 0 ? Loc.T("comp.has", string.Join(", ", has))
                    : has.Count > 0 ? Loc.T("comp.few", string.Join(", ", has))
                    : Loc.T("comp.none");
            var (fg, line) = state switch
            {
                > 0 => (Frozen(PlusColor, 0xFF), Frozen(PlusColor, 0x60)),
                < 0 => (Frozen(MinusColor, 0xFF), Frozen(MinusColor, 0x70)),
                _   => (CompPendingFg, CompPendingLine),
            };
            list.Add(new CompCheckVm(kind, state, Glyphs.Of(kind), Loc.T($"pickTag.{kind}"), tip, fg, line));
        }
        return list;
    }

    /// <summary>
    /// План на игру своей команды одной-двумя фразами: от явного стиля (как
    /// драться) и от темпа (когда). С трёх выбранных; нет ни стиля, ни темпа —
    /// пусто.
    /// </summary>
    public static string PlanText(IReadOnlyList<int> team)
    {
        if (team.Count < 3) return "";
        var parts = new List<string>();
        switch (ChampionTraits.DominantStyle(team))
        {
            case ChampionTraits.Arch.FrontToBack: parts.Add(Loc.T("comp.planF2b")); break;
            case ChampionTraits.Arch.Dive:        parts.Add(Loc.T("comp.planDive")); break;
            case ChampionTraits.Arch.PickPoke:    parts.Add(Loc.T("comp.planPoke")); break;
        }
        int early = team.Count(a => ChampionTraits.EarlyPower(a) >= 3);
        int late = team.Count(a => ChampionTraits.EarlyPower(a) <= 1);
        if (early >= 2 && late == 0) parts.Add(Loc.T("comp.planEarly"));
        else if (late >= 2 && early == 0) parts.Add(Loc.T("comp.planLate"));
        return parts.Count == 0 ? "" : Loc.T("comp.planLine", string.Join(" ", parts));
    }
}
