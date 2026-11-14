namespace Counterplay;

// Движок подбора — союзник быстро перебирает чемпионов (AllyWaver): подбор
// держится на том, кто стоял у него в слоте до метаний, а не на нынешнем
// наведении.
public sealed partial class RecommendationEngine
{
    /// <summary>
    /// Драфт для подбора: у союзников, на которых держим подбор
    /// (<see cref="DraftState.HeldPicks"/>), в слоте тот, под кого подбор
    /// посчитан, — наведением, как было, когда его считали. Его нынешнее
    /// наведение подбор не видит, поэтому центр не дёргается за каждым
    /// щелчком по сетке чемпионов. Залочивший (<see cref="AllyWaver.Locked"/>)
    /// считается собой.
    ///
    /// Зовёт <see cref="Recommend"/> первым делом, поэтому то же видят пул
    /// сверху, наведение мыши на карточку, «не советовать» и подсказка
    /// напарнику — все они считают через него. Бока окна (портрет союзника и
    /// иконки под ним) рисуются по настоящему драфту и идут за наведением.
    /// </summary>
    private static DraftState Held(DraftState s)
    {
        if (s.HeldPicks is not { Count: > 0 } held) return s;
        var team = s.MyTeam
            .Select(p => !p.IsLocalPlayer && !AllyWaver.Locked(s, p) && held.TryGetValue(p.CellId, out var c)
                ? p with { ChampionId = 0, PickIntentId = c }   // в свой ход наведённый стоит в ChampionId
                : p)
            .ToList();
        return s with { MyTeam = team, HeldPicks = null };
    }
}
