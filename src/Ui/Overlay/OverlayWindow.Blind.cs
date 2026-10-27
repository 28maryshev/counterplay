using System.Windows;

namespace Counterplay;

// Окно драфта — драфт без роли (вслепую, своя игра): мой пул под показанных союзников.
public partial class OverlayWindow
{
    // Вид «без роли» на экране. Панель «мои чемпионы» тогда его, а не
    // объявления, и нижнюю строку центра держат руны моего чемпиона, а без
    // них — тир-лист (CenterNotPicks, RunesGoneRow). Снимается в начале
    // каждой отрисовки, как и заглушка.
    private bool _blindView;

    // Синергия по модулю меньше этой — шум: заметка без цвета. Порог тот же,
    // что у кружка связки на портрете союзника.
    private const double BlindSynColor = 0.3;

    // Место под плашку «Выбрать» под панелью. Плашка плавает внизу верхней
    // строки центра, а та здесь по содержимому — без зазора легла бы на
    // последнюю строку пула.
    private const double BlindPickBarRoom = 56;

    private static readonly string[] BlindRoles = ["top", "jungle", "mid", "adc", "support"];

    /// Драфт без роли: пики идут, а подбора не будет — линии нет, считать не на что.
    private static bool IsBlind(DraftState draft) =>
        !draft.IsAram && !draft.InBanPhase && RecommendationEngine.LcuToDbRole(draft.MyPosition).Length == 0;

    /// <summary>
    /// Драфт без роли (вслепую, своя игра). Подбору нужна линия, и центр стоял
    /// заглушкой до конца драфта. Теперь там «мои чемпионы» — той же панелью,
    /// что в объявлении пиков: союзники показали чемпионов — мой пул по
    /// синергии с ними; не показал никто — самые игранные за месяц (просьба
    /// владельца, 15.10). Внизу руны моего чемпиона, пока их нет — тир-лист.
    /// </summary>
    private void ShowBlindPool(DraftState draft)
    {
        RestoreModeSize();
        _blindView = true;

        var allies = draft.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
            .Select(p => (Id: p.EffectiveChampionId, Role: RecommendationEngine.LcuToDbRole(p.Position)))
            .ToList();
        var bySynergy = allies.Count > 0;
        PickHint.Visibility = Visibility.Collapsed;

        ShowPlanPanel(BlindRows(draft, allies),
                      Loc.T(bySynergy ? "blind.title" : "blind.titleMonth"),
                      Loc.T("plan.colGames"), Loc.T("blind.empty"));
        // Взять чемпиона вслепую можно (в объявлении — нет): плашку «Выбрать»
        // ставит UpdatePickBar, здесь — только место под неё.
        if (PickBar.Visibility == Visibility.Visible)
            PlanPanel.Margin = new Thickness(0, 6, 0, BlindPickBarRoom);

        // Нижняя строка: руны, пришедшие при другом виде, ждут — встают сейчас.
        if (_runesWaiting && _runeStats is { Keystones.Count: > 0 } && AppSettings.Current.DraftRunes)
            RunesBar.Visibility = Visibility.Visible;
        _runesWaiting = false;
        if (RunesBar.Visibility == Visibility.Visible) TierListBar.Visibility = Visibility.Collapsed;
        else RenderTierList(draft);

        RenderTeams(draft);
        ShowDraftView();
    }

    /// <summary>
    /// Строки «мои чемпионы» без роли. Союзники ещё никого не показали — самые
    /// игранные за месяц на всех ролях, а без игр — пул. Показали — мой
    /// активный пул на всех ролях (пула нет — сыгранные за месяц) по синергии
    /// с ними: тот же расчёт, что у подбора (<see cref="RecommendationEngine.BlindSynergy"/>),
    /// моя роль — та, на которой чемпион в пуле, иначе на которой я его играл.
    /// Взятые другими и забаненные не предлагаются, свой наведённый — да.
    /// </summary>
    private List<PlanRow> BlindRows(DraftState draft, IReadOnlyList<(int Id, string Role)> allies)
    {
        var engine   = _engine!;
        var selected = _pickHoverId > 0 ? _pickHoverId : draft.Me?.EffectiveChampionId ?? 0;
        var gone = draft.MyTeam.Concat(draft.TheirTeam).Where(p => !p.IsLocalPlayer)
            .Select(p => p.EffectiveChampionId).Concat(draft.MyTeamBans).Concat(draft.TheirTeamBans)
            .Where(id => id > 0).ToHashSet();

        var month  = SessionTracker.RecentChamps(engine.RoleShare).Where(c => !gone.Contains(c.ChampionId)).ToList();
        var played = month.ToDictionary(c => c.ChampionId);
        var pool   = new List<(int Id, string Role)>();
        foreach (var r in BlindRoles)
            foreach (var id in PoolStore.ActiveForRole(r).Mine)
                if (id > 0 && !gone.Contains(id) && pool.All(p => p.Id != id)) pool.Add((id, r));

        if (allies.Count == 0)
            return month.Count > 0
                ? [.. month.Take(PlanRowCount).Select(c => BlindRowOf(c.ChampionId, c, c.Role, selected, null))]
                : [.. pool.Take(PlanRowCount).Select(p => BlindRowOf(p.Id, null, p.Role, selected, null))];

        var mine = pool.Count > 0 ? pool : [.. month.Select(c => (Id: c.ChampionId, c.Role))];
        return [.. mine
            .Select(m => (m.Id, m.Role, Syn: engine.BlindSynergy(m.Id, m.Role, allies)))
            .OrderByDescending(x => x.Syn.Delta)
            .ThenByDescending(x => played.GetValueOrDefault(x.Id)?.Games ?? 0)
            .Take(PlanRowCount)
            .Select(x => BlindRowOf(x.Id, played.GetValueOrDefault(x.Id), x.Role, selected, x.Syn))];
    }

    /// <summary>
    /// Строка без роли: столбцы как в объявлении (винрейт в патче — на моей роли
    /// для этого чемпиона), а заметка — роль и, если союзники показаны,
    /// синергия с ними и тот, с кем она лучше (или хуже) всего; вклад каждого
    /// союзника — в подсказке.
    /// </summary>
    private PlanRow BlindRowOf(int id, SessionTracker.LaneChamp? c, string role, int selected,
                               (double Delta, List<(int Id, double Delta)> ByAlly)? syn)
    {
        var roleName = RoleNameDb(role);
        if (syn is not { } s)
        {
            var text = c is null ? $"{roleName} · {Loc.T("plan.fromPool")}" : roleName;
            return PlanRowOf(id, c, role, selected, new PlanNote(text, "#6E8598", []));
        }

        var tip = s.ByAlly.Select(a => Loc.T("blind.tipAlly", DataDragon.Name(a.Id), Signed(a.Delta))).ToList();
        // Кто больше всех тянет итог в его сторону: при плюсе — лучший союзник, при минусе — худший.
        var key = s.ByAlly.OrderByDescending(a => s.Delta >= 0 ? a.Delta : -a.Delta).FirstOrDefault();
        // Союзник один — «лучше всего с ним» звучит странно: просто «с ним».
        var note = key.Id == 0 || Math.Abs(s.Delta) < 0.05 ? roleName
            : Loc.T(s.ByAlly.Count == 1 ? "blind.synOne" : s.Delta >= 0 ? "blind.synGood" : "blind.synBad",
                    Signed(s.Delta), DataDragon.Name(key.Id));
        var brush = s.Delta >= BlindSynColor ? "#57C98A" : s.Delta <= -BlindSynColor ? "#E05B5B" : "#6E8598";
        return PlanRowOf(id, c, role, selected, new PlanNote(note, brush, [roleName, .. tip]));
    }
}
