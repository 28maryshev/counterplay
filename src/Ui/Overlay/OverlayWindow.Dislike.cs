using System.Windows;

namespace Counterplay;

// Окно драфта — «не советовать»: кнопка 👎 на карточке пика и бана, кнопки
// «Баны» / «Пики» в шапке тир-листа со списком скрытых и полоса «Скрыты» над
// советами пиков. Сами правила (сколько игр скрыт, как возвращается) — в Dislikes.
public partial class OverlayWindow
{
    // Наведения своих за драфт — с ними Program считает советы банов. Нужны,
    // чтобы пересчитать советы сразу после дизлайка, не дожидаясь события драфта.
    private IReadOnlyDictionary<int, HashSet<int>>? _lastHoverHistory;

    /// Роль, на которой драфт считает советы (роль базы); нет — пусто.
    private string DraftDbRole =>
        _lastDraft?.MyPosition is { Length: > 0 } pos ? RecommendationEngine.LcuToDbRole(pos) : "";

    /// Кнопка 👎 на карточке: не советовать этого чемпиона на моей роли.
    internal void Dislike_Click(object sender, Dislikes.Kind kind)
    {
        if (sender is not FrameworkElement { Tag: int champId } || champId <= 0) return;
        var role = DraftDbRole;
        if (role.Length == 0) return;
        Dislikes.Add(kind, champId, role);
        // Выбранный кликом больше не совет — рамка и кнопка подтверждения с ним уходят.
        if (kind == Dislikes.Kind.Ban && _banHoverId == champId) _banHoverId = 0;
        if (kind == Dislikes.Kind.Pick && _pickHoverId == champId) _pickHoverId = 0;
        RefreshAdvice(kind);
    }

    /// Клик по иконке в полосе «Скрыты» или по «Вернуть» в списке скрытых:
    /// вернуть чемпиона в советы сразу. Список, если открыт, остаётся открытым.
    internal void DislikeRestore_Click(object sender)
    {
        if (sender is not FrameworkElement { Tag: HiddenChampVm vm }) return;
        Dislikes.Remove(vm.Kind, vm.ChampionId, vm.Role);
        RefreshAdvice(vm.Kind);
        if (HiddenPopup.IsOpen) FillHiddenPopup();
    }

    /// <summary>
    /// Пересчитать советы этого вида тем же движком и перерисовать. Баны —
    /// как их считает Program: по драфту из клиента и наведениям своих; пики —
    /// по драфту с ручными ролями врагов, как в UpdateRecommendations.
    /// </summary>
    private void RefreshAdvice(Dislikes.Kind kind)
    {
        if (_engine is null) return;
        try
        {
            // Банов не считали (объявление, свой бан сделан) — и пересчитывать нечего.
            if (kind == Dislikes.Kind.Ban && _lastBans is { Count: > 0 } && _lastRawDraft is not null)
                _lastBans = _engine.RecommendBans(_lastRawDraft, _lastHoverHistory);
            else if (kind == Dislikes.Kind.Pick && _lastRecs is not null && _lastDraft is { IsAram: false })
                _lastRecs = _engine.Recommend(_lastDraft, AppSettings.Current.DraftCount);
        }
        catch (Exception ex) { Log.Write($"не советовать: пересчёт не удался — {ex.Message}"); }
        RenderCurrentState();
    }

    /// <summary>
    /// Скрытые на роли драфта и сколько игр им ещё ждать. Возвращающихся (уже
    /// видны, только ниже) здесь нет.
    /// </summary>
    private List<HiddenChampVm> HiddenItems(Dislikes.Kind kind)
    {
        var role = DraftDbRole;
        return [.. Dislikes.Active(kind, role).Where(s => s.Hidden).Select(s => new HiddenChampVm
        {
            ChampionId = s.Champ,
            Kind       = kind,
            Role       = role,
            Icon       = IconCache.Get(s.Champ),
            Tip        = Loc.T("dislike.restoreTip", DataDragon.Name(s.Champ), s.GamesLeft),
        })];
    }

    /// Полоса «Скрыты» над списком советов (пики; баны — без тир-листа).
    private void ShowHiddenStrip(System.Windows.Controls.ContentControl strip, Dislikes.Kind kind)
    {
        var items = HiddenItems(kind);
        strip.Content    = items;
        strip.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Кнопки «Баны» / «Пики» в шапке тир-листа (фаза банов), рядом с
    /// переключателем «Пики | Баны»: сколько скрыто на роли драфта. Видны
    /// всегда — владелец: «где кнопки вызова окна? хотел, чтобы они были рядом
    /// с переключением». Пики здесь тоже: в фазе банов их списка на экране нет,
    /// а скрыть пик могли в прошлом драфте. Открытый список обновляется вместе
    /// с ними — события драфта идут, пока он открыт.
    /// </summary>
    private void ShowHiddenButtons()
    {
        static string Count(int n) => n > 0 ? n.ToString() : "";
        HiddenBansCount.Text  = Count(Dislikes.Active(Dislikes.Kind.Ban, DraftDbRole).Count);
        HiddenPicksCount.Text = Count(Dislikes.Active(Dislikes.Kind.Pick, DraftDbRole).Count);
        if (HiddenPopup.IsOpen) FillHiddenPopup();
    }

    // Чей список открыт: банов или пиков.
    private Dislikes.Kind _hiddenListKind;

    // Когда список закрылся. Он закрывается любым кликом мимо — и тем же
    // кликом по своей кнопке, который следом открыл бы его снова.
    private DateTime _hiddenClosedAt;

    private void HiddenPopup_Closed(object? sender, EventArgs e) => _hiddenClosedAt = DateTime.UtcNow;

    /// Тир-лист ушёл (пики, конец драфта) — список скрытых вместе с ним.
    private void TierListBar_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) HiddenPopup.IsOpen = false;
    }

    /// Клик по кнопке «Баны» / «Пики» в шапке тир-листа: открыть список скрытых
    /// под ней, повторный клик по той же — закрыть.
    private void HiddenList_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;   // не таскать окно за кнопку
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var kind = tag == "Ban" ? Dislikes.Kind.Ban : Dislikes.Kind.Pick;
        if (HiddenPopup.IsOpen && _hiddenListKind == kind) { HiddenPopup.IsOpen = false; return; }
        if (_hiddenListKind == kind && (DateTime.UtcNow - _hiddenClosedAt).TotalMilliseconds < 300) return;
        _hiddenListKind = kind;
        HiddenPopup.PlacementTarget = (UIElement)sender;
        FillHiddenPopup();
        HiddenPopup.IsOpen = true;
    }

    /// <summary>
    /// Содержимое списка скрытых: все действующие дизлайки вида на роли драфта —
    /// и скрытые («игр до возвращения: N»), и возвращающиеся (уже в советах,
    /// только ниже): вернуть на место можно любого.
    /// </summary>
    private void FillHiddenPopup()
    {
        var kind = _hiddenListKind;
        var role = DraftDbRole;
        var rows = Dislikes.Active(kind, role).Select(s => new HiddenChampVm
        {
            ChampionId = s.Champ,
            Kind       = kind,
            Role       = role,
            Icon       = IconCache.Get(s.Champ),
            Name       = DataDragon.Name(s.Champ),
            Status     = s.Hidden ? Loc.T("dislike.statusHidden", s.GamesLeft)
                                  : Loc.T("dislike.statusReturning", s.Demote),
        }).ToList();
        HiddenPopupTitle.Text = Loc.T(kind == Dislikes.Kind.Ban ? "dislike.titleBans" : "dislike.titlePicks",
                                      RoleNameDb(role));
        HiddenPopupList.ItemsSource = rows;
        HiddenPopupEmpty.Text       = Loc.T("dislike.empty", Dislikes.HideGames);
        HiddenPopupEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
