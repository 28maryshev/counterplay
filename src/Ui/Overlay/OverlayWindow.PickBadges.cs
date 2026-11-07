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
    private static readonly Brush PlusBg     = Frozen(PlusColor, 0x1F);
    private static readonly Brush PlusLine   = Frozen(PlusColor, 0x8C);
    private static readonly Brush MinusFg    = Frozen(MinusColor, 0xFF);
    private static readonly Brush MinusBg    = Frozen(MinusColor, 0x1F);
    private static readonly Brush MinusLine  = Frozen(MinusColor, 0x8C);

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

    private static List<TagVm> TagsOf(Recommendation r) =>
        r.Badges is not { } b ? []
        : [.. b.Tags.Select(t => t.Sign >= 0
            ? new TagVm(Glyphs.Of(t.Kind), t.Text, t.Tip, PlusFg, PlusBg, PlusLine)
            : new TagVm(Glyphs.Of(t.Kind), t.Text, t.Tip, MinusFg, MinusBg, MinusLine))];

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

    /// Иконки окна — минусы, которых движок не видит.
    private static IEnumerable<PickIcon> WindowIcons(bool notOwned, IReadOnlyList<int> counterItems,
                                                     ChampionTraits.Arch? cand, ChampionTraits.Arch? team)
    {
        if (cand is { } c && team is { } t && c != t)
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
