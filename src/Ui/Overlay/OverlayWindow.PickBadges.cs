using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Окно драфта — теги и иконки на карточке пика (вместо строк доводов).
public partial class OverlayWindow
{
    // Зелёный «за» и красный «против» — те же, что у точек доводов были раньше,
    // чуть мягче: на плашке цвет занимает больше места, чем точка.
    private static readonly Color PlusColor  = Color.FromRgb(0x5B, 0xD0, 0x8F);
    private static readonly Color MinusColor = Color.FromRgb(0xF0, 0x6A, 0x5E);

    private static readonly Brush PlusFg     = Frozen(PlusColor, 0xFF);
    private static readonly Brush MinusFg    = Frozen(MinusColor, 0xFF);

    // Яркость тега — от его силы среди показанных того же вида и цвета (владелец,
    // 07.11: «линия у Кейтлин ярче всех — её тег самый яркий»). Альфа текста,
    // рамки и заливки от слабого к сильнейшему; нижний край — чтобы слабый тег
    // оставался читаемым (и его же владелец попросил опустить ещё немного).
    private const byte TAG_FG_MIN = 0x88, TAG_FG_MAX = 0xFF;
    private const byte TAG_LINE_MIN = 0x35, TAG_LINE_MAX = 0xE0;
    private const byte TAG_BG_MIN = 0x0C, TAG_BG_MAX = 0x30;

    // Лидер вида и цвета во всю силу — с мягким свечением своего цвета. Только
    // полная сила: слабый лидер (у всех линия около +0,3) не светится.
    private static readonly System.Windows.Media.Effects.Effect PlusGlow  = Glow(PlusColor);
    private static readonly System.Windows.Media.Effects.Effect MinusGlow = Glow(MinusColor);

    private static System.Windows.Media.Effects.Effect Glow(Color c)
    {
        var e = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = c, BlurRadius = 10, ShadowDepth = 0, Opacity = 0.65,
        };
        e.Freeze();
        return e;
    }

    /// Нижний край шкалы силы, пп: лидер слабее — тоже не во всю яркость (у всех
    /// линия около +0,3 — ярких нет). Те же пороги, что у полосок справа
    /// (MetricRow.Defs); у «вслепую» — отрыв шанса контры от обычного.
    private static double TagFloor(string kind) => kind switch
    {
        "lane"    => MetricRow.Defs.First(d => d.Key == "metric.direct").Floor,
        "team"    => MetricRow.Defs.First(d => d.Key == "metric.team").Floor,
        "synergy" => MetricRow.Defs.First(d => d.Key == "metric.synergy").Floor,
        // Нужда стиля: Value — доля выбранных союзников, во всю силу — у последнего пика.
        var k when k.StartsWith("need") => 1.0,
        _         => 15.0,
    };

    /// Иконка, которая есть у такой доли кандидатов, бледнеет: «сетап под ганки»
    /// у всех саппортов подряд выбрать между ними не помогает. Та же доля, что
    /// раньше у отсева общих доводов.
    private const double ICON_COMMON_SHARE = 0.6;

    private static Brush Frozen(Color c, byte a)
    {
        var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    /// Сильнейший тег каждого вида и цвета среди показанных — мерка яркости.
    private static Dictionary<(string Kind, int Sign), double> TagLeaders(IEnumerable<Recommendation> recs) =>
        recs.SelectMany(r => r.Badges?.Tags ?? [])
            .GroupBy(t => (t.Kind, t.Sign))
            .ToDictionary(g => g.Key, g => g.Max(t => Math.Abs(t.Value)));

    private static List<TagVm> TagsOf(Recommendation r, IReadOnlyDictionary<(string Kind, int Sign), double> leaders) =>
        r.Badges is not { } b ? []
        : [.. b.Tags.Select(t =>
        {
            var top = Math.Max(leaders.GetValueOrDefault((t.Kind, t.Sign)), TagFloor(t.Kind));
            var s = Math.Clamp(Math.Abs(t.Value) / top, 0, 1);
            var c = t.Sign >= 0 ? PlusColor : MinusColor;
            var lead = s >= 1 - 1e-9;
            return new TagVm(t.Kind, t.Sign, Glyphs.Of(t.Kind), t.Text, t.Tip,
                Frozen(c, Lerp(TAG_FG_MIN, TAG_FG_MAX, s)), Frozen(c, Lerp(TAG_BG_MIN, TAG_BG_MAX, s)),
                Frozen(c, Lerp(TAG_LINE_MIN, TAG_LINE_MAX, s)), t.Value, s,
                lead ? (t.Sign >= 0 ? PlusGlow : MinusGlow) : null);
        })];

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

    /// <summary>
    /// Иконки карточки: от движка (уже по порядку: плюсы, связки, минусы) и
    /// окна — чего движок не знает: чемпиона нет на аккаунте, враги собирают
    /// предметы против него, стиль не тот, что у команды. Общие бледнеют.
    /// </summary>
    private static List<IconVm> IconsOf(IEnumerable<PickIcon> icons, IReadOnlySet<string> common)
    {
        var note = Loc.T("pickIcon.common");
        return [.. icons.Select(i =>
        {
            var faded = common.Contains(i.Kind);
            return new IconVm(Glyphs.Of(i.Kind), i.Sign >= 0 ? PlusFg : MinusFg,
                              i.Title, i.Tip, faded, faded ? note : "");
        })];
    }

    /// Виды иконок, которые есть у ICON_COMMON_SHARE кандидатов и больше. Меньше
    /// трёх кандидатов — сравнивать не с кем, бледных нет.
    private static HashSet<string> CommonIcons(IReadOnlyList<IReadOnlyList<PickIcon>> all)
    {
        if (all.Count < 3) return [];
        var need = Math.Max(2, (int)Math.Ceiling(all.Count * ICON_COMMON_SHARE));
        return [.. all.SelectMany(x => x.Select(i => i.Kind).Distinct())
                      .GroupBy(k => k).Where(g => g.Count() >= need).Select(g => g.Key)];
    }

    /// Иконки окна — минусы, которых движок не видит. «Не в стиле команды» не
    /// ставится тому, кто закрывает дыру этого стиля (тег нужды): Жанна в
    /// пик/поке сама по себе фронт-ту-бэк, но поуку она и нужна.
    private static IEnumerable<PickIcon> WindowIcons(bool notOwned, IReadOnlyList<int> counterItems,
                                                     ChampionTraits.Arch? cand, ChampionTraits.Arch? team,
                                                     bool fillsNeed)
    {
        if (cand is { } c && team is { } t && c != t && !fillsNeed)
            yield return new PickIcon("offStyle", -1, Loc.T("pickIcon.offStyle"),
                Loc.T("reason.offStyle", ArchName(t), ArchName(c)));
        if (counterItems.Count > 0)
            yield return new PickIcon("counterItems", -1, Loc.T("pickIcon.counterItems"),
                Loc.T("reason.counterItems", string.Join(", ", counterItems.Select(ItemIcons.NameOf))));
        if (notOwned)
            yield return new PickIcon("notOwned", -1, Loc.T("pickIcon.notOwned"), Loc.T("reason.notOwned"));
    }

    private static string ArchName(ChampionTraits.Arch a) => Loc.T(a switch
    {
        ChampionTraits.Arch.Dive     => "arch.dive",
        ChampionTraits.Arch.PickPoke => "arch.pickPoke",
        _                            => "arch.frontToBack",
    });
}
