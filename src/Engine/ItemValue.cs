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

    // Баланс типа урона: штраф за стак одного типа заметен с 3-го чемпиона.
    // 3.5 — по статистике команды с >80% одного типа урона заметно теряют WR;
    // прежние 2.2 не перебивали даже среднюю контру, и в 3-AP команду лезли AP-пики.
    private const double STACK_STEP = 3.5;
    private const double BALANCE_BONUS = 1.0;

    /// Баланс типа урона команды (AD/AP). Команде нужен смешанный урон: стакать один
    /// тип плохо — враг возьмёт броню/МР («один предмет рубит полкоманды»), особенно
    /// против танков. Возвращает дельту к скору (+ разбавляет / − усугубляет перекос),
    /// флаг «это стак» и тип кандидата (Ad=true — физ., false — маг.) для причины.
    public static (double Delta, bool Stack, bool Ad) DamageBalance(
        int candidate, IReadOnlyList<int> allyIds, IReadOnlyList<int> enemyIds)
    {
        bool candAp = DataDragon.IsApChampion(candidate);
        bool candAd = DataDragon.IsAdChampion(candidate);
        if (candAp == candAd) return (0, false, false); // смешанный/неизвестный — нейтрально

        int ad = 0, ap = 0;
        foreach (var a in allyIds)
        {
            if (DataDragon.IsAdChampion(a)) ad++;
            else if (DataDragon.IsApChampion(a)) ap++;
        }
        int same = candAp ? ap : ad;   // столько союзников уже того же типа
        int other = candAp ? ad : ap;
        int newSame = same + 1;

        double stackPen = newSame >= 3 ? (newSame - 2) * STACK_STEP : 0;
        if (stackPen > 0)
        {
            int tanks = 0;
            foreach (var e in enemyIds) if (ChampionTraits.IsTanky(e)) tanks++;
            stackPen *= tanks >= 2 ? 1.0 : tanks == 1 ? 0.8 : 0.5; // нет танков — мягче
        }
        double balBonus = same < other ? BALANCE_BONUS : 0;
        return (balBonus - stackPen, stackPen > 0.01, candAd);
    }

    // ID предметов Data Dragon.
    private const int MercTreads = 3111, Morello = 3165, Thornmail = 3075,
                      RanduinOmen = 3143, FrozenHeart = 3110, SpiritVisage = 3065, KaenicRookern = 2504;

    /// Предметы, которыми враг накажет команду, ЕСЛИ взять этот пик. Показываем строкой
    /// иконок под описанием. Смысл — предупреждение о СТАКЕ: одним предметом враг рубит
    /// полкоманды. Поэтому предмет показывается только пику, который ДОБАВЛЯЕТ к перекосу
    /// (ещё один AD в АД-команду), а не тому, кто её разбавляет.
    public static IReadOnlyList<int> CounterItems(int candidate, IReadOnlyList<int> allyIds)
    {
        int ad = 0, ap = 0, cc = 0, heal = 0;
        foreach (var t in allyIds.Append(candidate))
        {
            if (DataDragon.IsAdChampion(t)) ad++;
            else if (DataDragon.IsApChampion(t)) ap++;
            if (ChampionTraits.HardCc(t) >= 1)   cc++;
            if (ChampionTraits.HealReliant(t))   heal++;
        }
        bool candAd = DataDragon.IsAdChampion(candidate);
        bool candAp = DataDragon.IsApChampion(candidate);
        var items = new List<int>();

        // Перекос типа урона: предупреждаем ТОЛЬКО пик того же типа, что стак, и только
        // когда команда почти вся одного типа (враг возьмёт броню/МР на всех).
        if (candAd && ad >= 3 && ad - ap >= 2) { items.Add(Thornmail); items.Add(RanduinOmen); items.Add(FrozenHeart); } // броня + скорость атаки
        else if (candAp && ap >= 3 && ap - ad >= 2) { items.Add(SpiritVisage); items.Add(KaenicRookern); }               // МР

        // Стак контроля: пик добавляет CC и его в команде уже ≥2 → враг возьмёт тенасити.
        if (ChampionTraits.HardCc(candidate) >= 1 && cc >= 2) items.Add(MercTreads);

        // Стак хила: пик хилит и хила в команде уже ≥2 → враг возьмёт гривус.
        if (ChampionTraits.HealReliant(candidate) && heal >= 2) items.Add(candAp ? Morello : Thornmail);

        return items.Distinct().Take(5).ToList();
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
