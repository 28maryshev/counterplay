using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — тир-лист патча и тир-лист банов, полоса чемпионов роли.
public partial class OverlayWindow
{
    // ── Тир-лист патча (лучшие по WR на роль) — под банами ────────────────────
    private IReadOnlyList<TierRoleCol>? _tierCols;   // кэш: статичен в пределах патча

    // Цвет эмблемы по грейду — как ранги в LoL, все оттенки различимы.
    private static string GradeColor(string g) => g switch
    {
        "OP" => "#C58CFF",  // фиолетовый — над золотом, как мастер над алмазом
        "S"  => "#FFD23C",  // золото (яркое)
        "A"  => "#5CE0E6",  // диамант (бирюза)
        "B"  => "#CD7F32",  // бронза
        "C"  => "#B7C2CC",  // серебро
        _    => "#6E5140",  // тёмно-коричневый — как ранг Iron
    };

    private static string WrBrush(double wr) => WinrateColor.Hex(wr);

    private TierCell TierCellOf(RecommendationEngine.TierEntry t) => new()
    {
        ChampionId = t.ChampionId,
        Icon    = IconCache.Get(t.ChampionId),
        WrText  = $"{t.Winrate:F1}%",
        WrBrush = WrBrush(t.Winrate),
        Grade   = t.Grade,
        GradeColor = GradeColor(t.Grade),
        Tip     = $"{DataDragon.Name(t.ChampionId)} · {t.Grade}\n"
                + $"WR {t.Winrate:F1}%  ·  {Loc.T("tier.pick")} {t.PickRate:F1}%"
                + (t.BanRate > 0 ? $"  ·  {Loc.T("tier.ban")} {t.BanRate:F1}%" : "")
                + $"  ·  {t.Games} " + Loc.T("tier.games"),
    };

    // Все чемпионы, которых играют на моей роли, порядком тир-листа. Показываем
    // до пика — вместо рун, которых ещё нет. Список кэшируем по роли: при свапе
    // ролей он пересобирается, в остальное время статичен в пределах патча.
    private List<TierCell>? _rolePoolCells;

    private string _rolePoolRole = "";

    /// Состав моей половины пула, с которым собрана полоса: сменился пул —
    /// полосу надо пересобрать, иначе пометки останутся от прежнего.
    private HashSet<int> _rolePoolMine = [];

    private void RenderRolePool(DraftState? draft)
    {
        if (_engine is null || draft is null) { RolePoolBar.Visibility = Visibility.Collapsed; return; }

        var role = RecommendationEngine.LcuToDbRole(draft.MyPosition);
        if (role.Length == 0) { RolePoolBar.Visibility = Visibility.Collapsed; return; }

        // Моя половина активного пула. Для дуо это Mine, для личного — он сам.
        var mine = new HashSet<int>(PoolStore.ActiveForRole(role).Mine);

        if (role != _rolePoolRole || _rolePoolCells is null || !mine.SetEquals(_rolePoolMine))
        {
            _rolePoolRole = role;
            _rolePoolMine = mine;
            // perRole с запасом: нужен весь пул роли, а не топ-15 как в тир-листе.
            _rolePoolCells = _engine.TierList(perRole: 200)
                .Where(t => t.Role == role)
                .Select(t =>
                {
                    var cell = TierCellOf(t);
                    return new TierCell
                    {
                        ChampionId = cell.ChampionId, Icon = cell.Icon,
                        WrText = cell.WrText, WrBrush = cell.WrBrush,
                        Grade  = cell.Grade,  GradeColor = cell.GradeColor,
                        Tip    = cell.Tip, ShowGrade = true,
                        NotOwned = _ownedChamps.Count > 0 && !_ownedChamps.Contains(t.ChampionId),
                        InPool   = mine.Contains(t.ChampionId),
                    };
                })
                // Своя половина — впереди. Отметив пул, человек ждёт увидеть
                // его чемпионов сразу, а не искать среди всех на роли.
                .OrderByDescending(c => c.InPool)
                .ToList();
            RolePoolList.ItemsSource = _rolePoolCells;
            RolePoolTitle.Text = Loc.T("rolePool.title", RoleNameDb(role));
        }

        // Занятые: баны обеих команд и уже взятые чемпионы — крестом, как в тир-листе.
        var gone = new HashSet<int>(draft.MyTeamBans.Concat(draft.TheirTeamBans)
            .Concat(draft.MyTeam.Concat(draft.TheirTeam).Select(p => p.ChampionId))
            .Where(id => id != 0));
        foreach (var c in _rolePoolCells) c.Banned = gone.Contains(c.ChampionId);

        RolePoolBar.Visibility = _rolePoolCells.Count > 0 && AppSettings.Current.DraftRolePool
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderTierList(DraftState? draft = null)
    {
        if (_engine is null) { TierListBar.Visibility = Visibility.Collapsed; return; }
        if (_tierCols is null)
        {
            // Один столбец на роль — тир по силе. Второй, по сырому винрейту,
            // убран 20.10: тир сам по силе, и столбцы почти повторяли друг друга.
            var byTier = _engine.TierList(15).GroupBy(t => t.Role)
                                .ToDictionary(g => g.Key, g => g.ToList());

            var cols = new List<TierRoleCol>();
            foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
            {
                if (!byTier.TryGetValue(role, out var tierList)) continue;
                cols.Add(new TierRoleCol
                {
                    RoleLabel = RoleNameDb(role),
                    RoleIcon  = RoleIcons.Get(DbToLcuRole(role)),
                    TierCells = tierList.Select(TierCellOf).ToList(),
                });
            }
            _tierCols = cols;
            // Заголовок с именем бакета игрока: «Тир-лист · Изумруд».
            TierTitle.Text = Loc.T("tier.title", Loc.T(_engine.TierBucketLocKey));
        }
        // Тир-лист банов — по требованию: пока игрок его не открыл, не считаем.
        var byBans = (_tierModeNow ?? AppSettings.Current.BansTierMode) == "bans";
        if (byBans && _banTierCols is null)
        {
            var byRole = _engine.BanTierList(15).GroupBy(t => t.Role)
                                .ToDictionary(g => g.Key, g => g.ToList());
            _banTierCols = [.. new[] { "top", "jungle", "mid", "adc", "support" }
                .Where(byRole.ContainsKey)
                .Select(role => new TierRoleCol
                {
                    RoleLabel = RoleNameDb(role),
                    RoleIcon  = RoleIcons.Get(DbToLcuRole(role)),
                    TierCells = byRole[role].Select(BanTierCellOf).ToList(),
                })];
        }

        // Источник ставим ОДИН раз (список статичен в пределах патча) — иначе
        // переприсвоение на каждом событии драфта зря пересобирало бы 150 эмблем.
        if (!ReferenceEquals(TierList.ItemsSource, _tierCols))
            TierList.ItemsSource = _tierCols;
        if (byBans && !ReferenceEquals(BanTierList.ItemsSource, _banTierCols))
            BanTierList.ItemsSource = _banTierCols;
        TierList.Visibility    = byBans ? Visibility.Collapsed : Visibility.Visible;
        BanTierList.Visibility = byBans ? Visibility.Visible   : Visibility.Collapsed;
        TierTitle.Text = byBans
            ? Loc.T("tier.titleBans", Loc.T(_engine.TierBucketLocKey), _engine.Patch)
            : Loc.T("tier.title", Loc.T(_engine.TierBucketLocKey));
        PaintTierMode(byBans);
        TierListBar.Visibility = _tierCols.Count > 0 && AppSettings.Current.BansTierList
            ? Visibility.Visible : Visibility.Collapsed;
        ShowHiddenButton();   // скрытые «не советовать» — кнопка в шапке

        // Помечаем уже забаненных чемпионов (красный крест) — обновляется по ходу
        // банфазы, когда список банов растёт.
        var banned = draft is null ? new HashSet<int>()
            : new HashSet<int>(draft.MyTeamBans.Concat(draft.TheirTeamBans).Where(id => id != 0));
        foreach (var col in _tierCols.Concat(_banTierCols ?? []))
            foreach (var c in col.TierCells) c.Banned = banned.Contains(c.ChampionId);
    }

    // ── Тир-лист банов и переключатель «Пики / Баны» ─────────────────────────
    private IReadOnlyList<TierRoleCol>? _banTierCols;   // кэш, как у тир-листа пиков

    /// Цвет бан-рейта: чем чаще банят, тем краснее. Пороги — по живой базе:
    /// десяток самых банимых держится выше 10%, единицы — выше 20%.
    private static string BanRateBrush(double rate) =>
        rate >= 20 ? "#FF6B5E" : rate >= 10 ? "#F0A04B" : rate >= 3 ? "#E6C068" : "#8AA0B2";

    private static TierCell BanTierCellOf(RecommendationEngine.TierEntry t) => new()
    {
        ChampionId = t.ChampionId,
        Icon       = IconCache.Get(t.ChampionId),
        WrText     = $"{t.BanRate:F1}%",
        WrBrush    = BanRateBrush(t.BanRate),
        ShowGrade  = false,
        Tip        = $"{DataDragon.Name(t.ChampionId)}\n"
                   + $"{Loc.T("tier.ban")} {t.BanRate:F1}%  ·  {Loc.T("tier.pick")} {t.PickRate:F1}%"
                   + $"  ·  WR {t.Winrate:F1}%  ·  {t.Games} " + Loc.T("tier.games"),
    };

    private void PaintTierMode(bool byBans)
    {
        static void Paint(Border b, TextBlock t, bool on)
        {
            b.Background  = Brush(on ? "#33C89B3C" : "#0FFFFFFF");
            b.BorderBrush = Brush(on ? "#C89B3C"   : "#3A4B5F");
            t.Foreground  = Brush(on ? "#F0D9A0"   : "#7F93A6");
        }
        Paint(TierModePicks, TierModePicksText, !byBans);
        Paint(TierModeBans,  TierModeBansText,  byBans);
    }

    /// Вид тир-листа, выбранный кнопкой в этом драфте; null — как в настройках
    /// (BansTierMode). Раньше кнопка писала прямо в настройки, но с выбором «по
    /// умолчанию» в окне настроек они спорили бы: один щелчок в драфте молча
    /// менял бы умолчание. Сбрасывается с концом драфта.
    private string? _tierModeNow;

    private void TierMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string mode }) return;
        e.Handled = true;
        _tierModeNow = mode;
        RenderTierList(_lastDraft);
    }

    // db-роль (mid/adc/support) → lcu-позиция (middle/bottom/utility) для иконок.
    // Роль стороны связки подходит моей роли? Пустая роль — подходит любой
    // (связка сохранена до того, как роли стали задаваться).
    private static bool RoleFits(string pairRole, string myRole) =>
        string.IsNullOrEmpty(pairRole) || pairRole == myRole;

    private static string DbToLcuRole(string db) => db switch
    {
        "mid"     => "middle",
        "adc"     => "bottom",
        "support" => "utility",
        _         => db,
    };
}
