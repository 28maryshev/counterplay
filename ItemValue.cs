namespace Counterplay;

/// <summary>
/// «Ценность предметов» (п.1): штраф за стак одной уязвимости в своей команде
/// и бонус за наказание вынужденного контр-предмета врага.
/// </summary>
public static class ItemValue
{
    public enum Cat { Cc, AutoCrit, Shield, Heal }

    public static string CatName(Cat c) => Loc.T(c switch
    {
        Cat.Cc       => "cat.cc",
        Cat.AutoCrit => "cat.autoCrit",
        Cat.Shield   => "cat.shield",
        _            => "cat.heal",
    });

    // Профиль уязвимостей набора: [cc, autoCrit, shield, heal].
    private static double[] Profile(IEnumerable<int> ids)
    {
        double cc = 0, a = 0, s = 0, h = 0;
        foreach (var id in ids)
        {
            cc += ChampionTraits.CcWeight(id);
            if (ChampionTraits.AutoReliant(id))   a++;
            if (ChampionTraits.ShieldReliant(id)) s++;
            if (ChampionTraits.HealReliant(id))   h++;
        }
        return [cc, a, s, h];
    }

    // Нелинейный штраф: 0 пока ≤1 в категории, дальше круто растёт.
    private static double Concave(double x) => x <= 1 ? 0 : Math.Pow(x - 1, 1.5);

    /// Штраф за то, что кандидат усиливает уже имеющуюся уязвимость команды.
    /// Возвращает величину (≥0), категорию-виновника и итоговое число уязвимых.
    public static (double Penalty, Cat? Cat, int Count) VulnPenalty(int candidate, IReadOnlyList<int> allyIds)
    {
        var before = Profile(allyIds);
        var after  = Profile(allyIds.Append(candidate));
        double total = 0, worst = 0;
        Cat? worstCat = null;
        for (int i = 0; i < 4; i++)
        {
            var d = Concave(after[i]) - Concave(before[i]);
            total += d;
            if (d > worst) { worst = d; worstCat = (Cat)i; }
        }
        var count = worstCat is { } wc ? (int)Math.Round(after[(int)wc]) : 0;
        return (total, worst > 0.01 ? worstCat : null, count);
    }

    /// Категория, которую враг вынужден контрить предметом (если стак ≥2).
    public static Cat? EnemyForced(IReadOnlyList<int> enemyIds)
    {
        var p = Profile(enemyIds);
        int mi = 0;
        for (int i = 1; i < 4; i++) if (p[i] > p[mi]) mi = i;
        return p[mi] >= 2 ? (Cat)mi : null;
    }

    /// Бонус за наказание вынужденного предмета врага (оставляет дыру).
    /// cc-стак → враг в Mercs (нет Steelcaps) → авто-атака наказывает.
    /// крит-стак → враг в Steelcaps/Frozen Heart → AP-бёрст наказывает.
    public static (double Bonus, Cat? Forced) ExploitBonus(int candidate, IReadOnlyList<int> enemyIds)
    {
        var forced = EnemyForced(enemyIds);
        if (forced is null) return (0, null);
        return forced switch
        {
            Cat.Cc       when ChampionTraits.AutoReliant(candidate) => (1, forced),
            Cat.AutoCrit when ChampionTraits.ApBurst(candidate)     => (1, forced),
            _                                                       => (0, forced),
        };
    }
}
