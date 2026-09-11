namespace Counterplay;

/// Подбор сборки под конкретных врагов.
///
/// Первый вариант в панели — самый ходовой: так собирают и так выигрывают вообще,
/// без оглядки на соперника. Он и остаётся первым. Два следующих — ответ именно
/// этому составу: их считает этот класс, когда все пять врагов известны. Раньше
/// знать состав целиком нельзя: пик пятого врага способен перевернуть решение
/// (один хилер — терпимо, два — уже обязательный срез лечения).
///
/// Логика собрана из того, как билды описывают в гайдах: сначала смотрят, каким
/// уроном их будут бить (броня против физического, сопротивление магии против
/// магического), потом — что мешает этот урон пережить или нанести: лечение
/// врага, щиты, толстые цели, долгий контроль. Дальше берут предмет, который
/// закрывает главное.
///
/// Важное ограничение: выбираем ТОЛЬКО из предметов, которые на этом чемпионе
/// реально собирают (данные из матчей). Иначе легко выдать броню на чемпиона,
/// который её не носит, — совет, который сразу видно как машинный.
public static class BuildAdvisor
{
    /// Сборка под состав: что купить и почему.
    public sealed record Adapted(
        IReadOnlyList<int> Items,
        IReadOnlyList<int> Changed,        // что добавлено против стандартной сборки
        IReadOnlyList<string> Reasons);

    // ── Черты, которых нет в данных Riot ──────────────────────────────────
    //
    // Доли урона и классы чемпионов берём из матчей и Data Dragon, а «много
    // лечится», «много контролит», «раздаёт щиты» там не выражены никак. Списки
    // ручные и намеренно короткие: сюда попадают только те, чья черта решает
    // покупку, а не любой, у кого в ките есть хил или станище.

    /// Заметное лечение или вампиризм — против них берут срез лечения.
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Soraka", "Yuumi", "Nami", "Sona", "Seraphine", "Senna", "Milio", "Taric",
        "Vladimir", "Aatrox", "DrMundo", "Warwick", "Swain", "Sylas", "Nasus",
        "Fiora", "Sett", "Olaf", "Maokai", "Briar", "Gwen", "Zac", "Ivern",
        "Mundo", "Volibear", "Trundle", "Yorick", "Illaoi", "Katarina",
    };

    /// Долгий и жёсткий контроль — против него берут стойкость.
    private static readonly HashSet<string> HardCc = new(StringComparer.OrdinalIgnoreCase)
    {
        "Leona", "Nautilus", "Malphite", "Amumu", "Sejuani", "Rell", "Alistar",
        "Morgana", "Lissandra", "Veigar", "Thresh", "Blitzcrank", "Skarner",
        "Ornn", "Sion", "JarvanIV", "Vi", "Ashe", "Rakan", "Maokai", "Nunu",
        "Warwick", "Camille", "Galio", "Neeko", "Zac", "Poppy", "Taric",
    };

    /// Щиты, которые съедают наш урон, — против них берут разрушение щитов.
    private static readonly HashSet<string> Shielders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lulu", "Karma", "Janna", "Orianna", "Seraphine", "Ivern", "Milio",
        "Renata", "Taric", "Shen", "Riven", "Sivir", "Nocturne", "Morgana",
        "Lux", "Yasuo", "Sett", "Rakan",
    };

    private sealed record Need(string Kind, double Weight, string Reason);

    /// <summary>
    /// Два варианта под состав врагов: «защита» и «давление». Пустой список —
    /// нечего предложить: у чемпиона нет подходящих предметов в его же практике
    /// или состав не требует поправок.
    /// </summary>
    public static IReadOnlyList<Adapted> Adapt(
        ChampStats stats, BuildData baseBuild, IReadOnlyList<int> enemies,
        Func<int, (double Phys, double Magic, double True)?> damageShare)
    {
        if (!ItemFacts.Loaded || stats.Items.Count == 0 || enemies.Count == 0) return [];

        // ── Каким уроном нас будут бить ───────────────────────────────────
        double phys = 0, magic = 0;
        foreach (var e in enemies)
        {
            var d = damageShare(e);
            if (d is { } v && v.Phys + v.Magic > 0)
            {
                phys += v.Phys;
                magic += v.Magic;
            }
            else
            {
                // Данных по чемпиону нет — грубая оценка по Data Dragon.
                if (DataDragon.IsApChampion(e)) magic += 0.7; else phys += 0.7;
            }
        }
        var dmgSum = phys + magic;
        if (dmgSum <= 0) return [];
        phys /= dmgSum;
        magic /= dmgSum;

        // ── Чем этот состав неудобен ──────────────────────────────────────
        int heal = 0, cc = 0, shield = 0, tanks = 0;
        foreach (var e in enemies)
        {
            var dd = DataDragon.DdId(e);
            if (dd.Length > 0)
            {
                if (Healers.Contains(dd)) heal++;
                if (HardCc.Contains(dd)) cc++;
                if (Shielders.Contains(dd)) shield++;
            }
            if (DataDragon.ClassTags(e).Contains("Tank")) tanks++;
        }

        // Мой урон — им решается, какое пробивание вообще имеет смысл.
        var mine = damageShare(stats.ChampionId);
        var iAmMagic = mine is { } m ? m.Magic > m.Phys : DataDragon.IsApChampion(stats.ChampionId);

        // ── Что закрывать ─────────────────────────────────────────────────
        var defense = new List<Need>();
        // Порог 55%: ниже него состав смешанный, и одна защита погоды не сделает.
        if (phys >= 0.55)
            defense.Add(new Need("armor", phys, Loc.T("build.vsPhys", Pct(phys))));
        if (magic >= 0.55)
            defense.Add(new Need("mr", magic, Loc.T("build.vsMagic", Pct(magic))));
        if (phys is >= 0.4 and < 0.55 && magic is >= 0.4 and < 0.55)
        {
            defense.Add(new Need("armor", phys, Loc.T("build.vsMixed")));
            defense.Add(new Need("mr", magic, Loc.T("build.vsMixed")));
        }
        if (cc >= 3) defense.Add(new Need("tenacity", 1.0, Loc.T("build.vsCc", cc)));

        var pressure = new List<Need>();
        if (heal >= 2) pressure.Add(new Need("antiheal", 2.0, Loc.T("build.vsHeal", heal)));
        else if (heal == 1) pressure.Add(new Need("antiheal", 1.0, Loc.T("build.vsHeal", heal)));
        if (tanks >= 2)
            pressure.Add(new Need(iAmMagic ? "magicpen" : "armorpen", 1.5, Loc.T("build.vsTank", tanks)));
        if (shield >= 2) pressure.Add(new Need("antishield", 1.0, Loc.T("build.vsShield", shield)));

        var res = new List<Adapted>();
        var d1 = Compose(stats, baseBuild, defense);
        if (d1 is not null) res.Add(d1);
        var p1 = Compose(stats, baseBuild, pressure, avoid: d1?.Changed);
        if (p1 is not null) res.Add(p1);
        return res;
    }

    private static string Pct(double share) => $"{share * 100:F0}";

    /// Собирает вариант: база, в которой некорневые слоты заменены на ответы.
    private static Adapted? Compose(ChampStats stats, BuildData baseBuild,
                                    List<Need> needs, IReadOnlyList<int>? avoid = null)
    {
        if (needs.Count == 0) return null;

        var items = baseBuild.Items.Take(6).ToList();
        var core = baseBuild.Core.Count > 0 ? baseBuild.Core.ToHashSet() : items.Take(2).ToHashSet();
        var added = new List<int>();
        var reasons = new List<string>();

        foreach (var need in needs.OrderByDescending(n => n.Weight))
        {
            // Уже закрыто стандартной сборкой — не тратим слот второй раз.
            if (items.Any(i => Answers(i, need.Kind))) continue;

            var pick = stats.Items
                .Where(i => !items.Contains(i.Id) && (avoid is null || !avoid.Contains(i.Id)))
                .Where(i => Answers(i.Id, need.Kind))
                // Среди подходящих берём тот, что чаще ВЫИГРЫВАЕТ на этом
                // чемпионе. Сырой винрейт брать нельзя: предмет с 60% на полусотне
                // игр — это чаще всего выбор тех, кто и так выигрывал. Поэтому
                // отклонение от 50% приглушается объёмом выборки, как и везде в
                // движке: на 700 играх оно засчитывается почти целиком, на 60 —
                // примерно на треть.
                .OrderByDescending(i => 50 + (i.Winrate - 50) * i.Games / (i.Games + 100.0))
                .ThenByDescending(i => i.Games)
                .FirstOrDefault();
            if (pick is null) continue;

            // Меняем последний НЕкорневой слот: ядро сборки трогать нельзя, на нём
            // держится сам чемпион.
            var slot = items.FindLastIndex(i => !core.Contains(i) && !added.Contains(i));
            if (slot < 0) break;

            items[slot] = pick.Id;
            added.Add(pick.Id);
            reasons.Add(need.Reason);
        }

        return added.Count == 0 ? null : new Adapted(items, added, reasons);
    }

    /// Закрывает ли предмет эту потребность.
    private static bool Answers(int itemId, string kind)
    {
        var f = ItemFacts.Of(itemId);
        if (f is null) return false;
        return kind switch
        {
            // Ботинки — полноценный ответ на урон нужного типа, хотя цифры у
            // них меньше: их и берут ради этого.
            "armor"      => f.Armor >= 30 || (f.Boots && f.Armor >= 15),
            "mr"         => f.MagicResist >= 30 || (f.Boots && f.MagicResist >= 15),
            "tenacity"   => f.Tenacity,
            "antiheal"   => f.AntiHeal,
            // Против толстых целей работает процентное пробивание и урон по
            // запасу здоровья; летальность бьёт хрупких и здесь бесполезна.
            "armorpen"   => f.ArmorPen && f.PercentPen,
            "magicpen"   => f.MagicPen && f.PercentPen,
            "antishield" => f.AntiShield,
            _            => false,
        };
    }
}
