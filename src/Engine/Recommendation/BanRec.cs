using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record BanRec(int ChampionId, double Score, string[] Reasons)
{
    /// <summary>
    /// Очки части, которую подписывает каждая строка <see cref="Reasons"/>, в том
    /// же порядке. У тира, пикрейта и «заметного пика» — 0: это множители, а не
    /// части очков. Окно их не показывает; по ним проверка `tests/BanPhase`
    /// сверяет порядок доводов с порогом движка.
    /// </summary>
    public double[] ReasonPoints { get; init; } = [];
}
