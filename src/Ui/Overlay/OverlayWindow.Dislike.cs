using System.Windows;

namespace Counterplay;

// Окно драфта — «не советовать»: кнопка 👎 на карточке пика и бана, кнопка
// «Скрытые» в шапке тир-листа (окно DislikesWindow) и полоса «Скрыты» над
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

    /// Клик по иконке в полосе «Скрыты»: вернуть чемпиона в советы сразу.
    internal void DislikeRestore_Click(object sender)
    {
        if (sender is not FrameworkElement { Tag: HiddenChampVm vm }) return;
        Dislikes.Remove(vm.Kind, vm.ChampionId, vm.Role);
        RefreshAdvice(vm.Kind);
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
    /// Кнопка «👎 Скрытые» в шапке тир-листа (фаза банов), рядом с
    /// переключателем «Пики | Баны»: сколько скрыто на роли драфта, баны и пики
    /// вместе. Видна всегда — владелец: «где кнопки вызова окна? хотел, чтобы
    /// они были рядом с переключением». Зовёт RenderTierList, поэтому кнопка есть
    /// везде, где тир-лист: советы банов, объявление пиков, баны команд.
    /// </summary>
    private void ShowHiddenButton()
    {
        var n = Dislikes.Active(Dislikes.Kind.Ban, DraftDbRole).Count
              + Dislikes.Active(Dislikes.Kind.Pick, DraftDbRole).Count;
        HiddenCount.Text = n > 0 ? n.ToString() : "";
    }

    // Окно скрытых — одно: повторный клик поднимает уже открытое.
    private DislikesWindow? _dislikesWindow;

    private void HiddenWindow_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;   // не таскать окно драфта за кнопку
        OpenDislikes().Show();
    }

    /// <summary>
    /// Окно скрытых (слева баны, справа пики), над окном драфта. Вернули
    /// чемпиона — советы пересчитываются сразу. Возвращает окно не показанным:
    /// проверки показывают его сами, невидимым.
    /// </summary>
    public DislikesWindow OpenDislikes()
    {
        if (_dislikesWindow is { IsLoaded: true } open) { open.Activate(); return open; }
        _dislikesWindow = new DislikesWindow(DraftDbRole, kind =>
        {
            RefreshAdvice(kind);
            ShowHiddenButton();
        }) { Owner = this };
        _dislikesWindow.Closed += (_, _) => _dislikesWindow = null;
        return _dislikesWindow;
    }
}
