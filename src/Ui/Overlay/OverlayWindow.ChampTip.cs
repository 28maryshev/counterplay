using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Counterplay;

// Окно драфта — карточка чемпиона при наведении на слот состава: винрейт и
// бан-рейт в патче, место в тир-листе, контрпики и синергия с винрейтами —
// общие и с моей роли.
public partial class OverlayWindow
{
    // Строк в списках карточки: контрпиков и напарников по пять.
    private const int ChampTipRows = 5;

    // Карточки считаются по наведению (до девяти запросов к базе), а наводят
    // весь драфт на тех же десятерых — кэш. Моя роль в ключе: от неё блок
    // «моя роль». Новый движок или язык — с чистого листа.
    private readonly Dictionary<(int Champ, string Role, bool Ally, string Mine), ChampTipVm> _champTips = new();
    private RecommendationEngine? _champTipsEngine;
    private string _champTipsLang = "";

    private static readonly HashSet<string> TipRoles = ["top", "jungle", "mid", "adc", "support"];

    /// <summary>
    /// Подсказка слота вот-вот откроется: кладём в неё карточку. Нечего
    /// показать (пустой слот, ARAM, база не загружена, выключено в настройках)
    /// — подсказку отменяем. Союзникам карточка встаёт справа, врагам — слева:
    /// обе ложатся на центр, а не за край окна.
    /// </summary>
    internal void TeamSlot_TipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChampSlotCard card, ToolTip: ToolTip tip } slot
            || ChampTipFor(card) is not { } vm)
        {
            e.Handled = true;
            return;
        }
        tip.Content          = vm;
        tip.PlacementTarget  = slot;
        tip.Placement        = card.IsAlly ? PlacementMode.Right : PlacementMode.Left;
        tip.HorizontalOffset = card.IsAlly ? 8 : -8;
        tip.VerticalOffset   = 0;
    }

    private ChampTipVm? ChampTipFor(ChampSlotCard card)
    {
        if (!AppSettings.Current.DraftChampTip || card.ChampionId <= 0
            || _engine is not { } engine || _lastDraft is { IsAram: true }) return null;
        if (!ReferenceEquals(engine, _champTipsEngine) || _champTipsLang != Loc.Current)
        {
            _champTips.Clear();
            _champTipsEngine = engine;
            _champTipsLang   = Loc.Current;
        }

        // Роль — та, что у слота (у врага — ручная или угаданная); вслепую её
        // нет, тогда главная роль чемпиона.
        var role = TipRoles.Contains(card.StatsRole) ? card.StatsRole : engine.PrimaryRole(card.ChampionId);
        if (!TipRoles.Contains(role)) return null;

        // Моя роль — выданная в драфте. Нет её (вслепую) или она та же, что у
        // него, — блока «моя роль» нет: общие контрпики уже с моей линии.
        var mine = RecommendationEngine.LcuToDbRole(_lastDraft?.MyPosition ?? "");
        if (!TipRoles.Contains(mine) || mine == role) mine = "";

        var key = (card.ChampionId, role, card.IsAlly, mine);
        if (_champTips.TryGetValue(key, out var cached)) return cached;
        try
        {
            return _champTips[key] = BuildChampTip(engine, card.ChampionId, role, card.IsAlly, mine);
        }
        catch (Exception ex)
        {
            Log.Write($"карточка чемпиона {card.ChampionId} ({role}): {ex.Message}");
            return null;
        }
    }

    private static ChampTipVm BuildChampTip(RecommendationEngine engine, int champId, string role,
                                            bool ally, string mine)
    {
        var patch = engine.PatchWr(champId, role);
        var delta = patch?.Delta;
        var ban   = engine.BanRate(champId);
        var spot  = engine.TierSpot(champId, role);

        return new ChampTipVm
        {
            Icon       = IconCache.Get(champId),
            Name       = DataDragon.Name(champId).ToUpperInvariant(),
            NameColor  = ally ? "White" : "#E07070",
            RingColor  = ally ? "#C89B3C" : "#C84040",
            RoleIcon   = RoleIcons.Get(RecommendationEngine.DbToLcuRole(role)),
            RoleText   = RoleNameDb(role),
            WrText     = patch is { } p ? $"{p.Wr:F1}%" : "—",
            WrBrush    = patch is { } p2 ? WinrateColor.Hex(p2.Wr) : "#4E6275",
            DeltaText  = delta is { } d ? (d >= 0 ? "▲" : "▼") + Math.Abs(d).ToString("F1") : "",
            DeltaBrush = delta >= PlanDeltaColor ? "#57C98A" : delta <= -PlanDeltaColor ? "#E05B5B" : "#6E8598",
            WrNote     = patch is null ? Loc.T("champTip.fewGames") : "",
            BanText    = $"{ban:F1}%",
            BanBrush   = BanRateBrush(ban),
            Grade      = spot?.Grade ?? "",
            GradeColor = GradeColor(spot?.Grade ?? ""),
            PlaceText  = spot is { } s ? Loc.T("champTip.place", s.Place, s.Of) : Loc.T("champTip.noTier"),
            // Контрпики — кто бьёт его на его линии; напарники — лучшие на
            // прочих ролях, с их значком роли.
            General    = new ChampTipLists
            {
                Counters  = TipRows(engine.CountersWithWr(champId, role, ChampTipRows), withRole: false),
                Synergies = TipRows(engine.PartnersWithWr(champId, role, ChampTipRows), withRole: true),
            },
            // С моей линии: кто из моей роли бьёт его (пары разных ролей в одной
            // игре) и кто из моей роли с ним лучше всех играет. Роль у всех
            // строк одна — значок только в шапке.
            Mine       = mine.Length == 0 ? null : new ChampTipLists
            {
                Header     = Loc.T("champTip.myRole", RoleNameDb(mine)),
                HeaderIcon = RoleIcons.Get(RecommendationEngine.DbToLcuRole(mine)),
                Counters   = TipRows(engine.CountersWithWr(champId, role, ChampTipRows, fromRole: mine), withRole: false),
                Synergies  = TipRows(engine.PartnersWithWr(champId, role, ChampTipRows, onRole: mine), withRole: false),
            },
            Footer     = Loc.T("champTip.footer", engine.Patch, Loc.T(engine.TierBucketLocKey)),
        };
    }

    // Отбирает движок по Уилсону (малые выборки не всплывают), а показываем по
    // винрейту: столбец чисел вразнобой читался бы как ошибка.
    private static List<ChampTipRow> TipRows(IEnumerable<RecommendationEngine.HintPick> picks, bool withRole) =>
        [.. picks.OrderByDescending(h => h.Wr).Select(h => TipRow(h, withRole))];

    private static ChampTipRow TipRow(RecommendationEngine.HintPick h, bool withRole) => new()
    {
        Icon     = IconCache.Get(h.Id),
        Name     = DataDragon.Name(h.Id),
        RoleIcon = withRole ? RoleIcons.Get(RecommendationEngine.DbToLcuRole(h.Role)) : null,
        WrText   = $"{100 * h.Wr:F1}%",
        WrBrush  = WinrateColor.Hex(100 * h.Wr),
    };
}
