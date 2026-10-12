using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record Recommendation(
    int    ChampionId,
    double Score,
    double BaseDelta,     // %пп vs 50%
    double DirectDelta,   // %пп матчап vs прямой оппонент
    double OtherDelta,    // %пп средний vs прочие враги
    double SynergyDelta,  // %пп средняя синергия с союзниками
    double ComfortDelta,  // бонус за «комфорт»: часто наигранный чемпион игрока
    double StyleDelta,    // вклад «против стиля врага» (трифекта + анти-стиль)
    string[] Reasons,
    int    Rank   = 0,    // место в полном списке (1 = лучший)
    bool   IsMyPick = false); // это мой уже выбранный/наведённый чемпион
