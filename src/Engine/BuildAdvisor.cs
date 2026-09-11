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

    /// Ответ целиком: варианты под состав и то, что стандартная сборка закрывает
    /// сама. Второе нужно показать: «менять нечего» без объяснения выглядит как
    /// отказ, а не как вывод.
    public sealed record Advice(
        IReadOnlyList<Adapted> Builds,
        IReadOnlyList<string> Covered);

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

    /// Щиты в умениях. Дополняет тег «shield» движка — там отмечены не все, а
    /// разрушение щитов имеет смысл считать по обоим спискам сразу.
    private static readonly HashSet<string> Shielders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lulu", "Karma", "Janna", "Orianna", "Seraphine", "Ivern", "Milio",
        "Renata", "Taric", "Shen", "Riven", "Sivir", "Nocturne", "Morgana",
        "Lux", "Yasuo", "Sett", "Rakan",
    };

    // Defense — пережить их урон, Pressure — пробить их. В сборке участвуют и то
    // и другое: игрок покупает один набор, а не два.
    private sealed record Need(string Kind, double Weight, string Reason, bool Defense);

    /// <summary>
    /// Два варианта под состав врагов: «защита» и «давление». Пустой список —
    /// нечего предложить: у чемпиона нет подходящих предметов в его же практике
    /// или состав не требует поправок.
    /// </summary>
    public static Advice Adapt(
        ChampStats stats, BuildData baseBuild, IReadOnlyList<int> enemies,
        Func<int, (double Phys, double Magic, double True)?> damageShare)
    {
        if (!ItemFacts.Loaded || stats.Items.Count == 0 || enemies.Count == 0)
            return new Advice([], []);

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
        if (dmgSum <= 0) return new Advice([], []);
        phys /= dmgSum;
        magic /= dmgSum;

        // ── Чем этот состав неудобен ──────────────────────────────────────
        int heal = 0, cc = 0, tanks = 0;
        double shield = 0;
        foreach (var e in enemies)
        {
            var dd = DataDragon.DdId(e);
            if (dd.Length > 0)
            {
                if (Healers.Contains(dd)) heal++;
                if (HardCc.Contains(dd)) cc++;
            }

            // Щит бывает не только в умениях. Бойцы вроде Олафа берут Стеракса
            // или Шилдбоу, и щит там не меньше — а режется он тем же предметом.
            // Считаем их за половину: покупают такое не все и не всегда, а вот
            // энчантер со щитом в умениях даёт его гарантированно.
            if (Shielders.Contains(dd) || ChampionTags.Has(e, "shield")) shield += 1;
            else if (DataDragon.ClassTags(e).Contains("Fighter")) shield += 0.5;

            if (DataDragon.ClassTags(e).Contains("Tank")) tanks++;
        }

        // Мой урон — им решается, какое пробивание вообще имеет смысл.
        var mine = damageShare(stats.ChampionId);
        var iAmMagic = mine is { } m ? m.Magic > m.Phys : DataDragon.IsApChampion(stats.ChampionId);

        // ── Что закрывать ─────────────────────────────────────────────────
        var defense = new List<Need>();
        // Порог 55%: ниже него состав смешанный, и одна защита погоды не сделает.
        if (phys >= 0.55)
            defense.Add(new Need("armor", phys, Loc.T("build.vsPhys", Pct(phys)), true));
        if (magic >= 0.55)
            defense.Add(new Need("mr", magic, Loc.T("build.vsMagic", Pct(magic)), true));
        if (phys is >= 0.4 and < 0.55 && magic is >= 0.4 and < 0.55)
        {
            defense.Add(new Need("armor", phys, Loc.T("build.vsMixed"), true));
            defense.Add(new Need("mr", magic, Loc.T("build.vsMixed"), true));
        }
        if (cc >= 3) defense.Add(new Need("tenacity", 1.0, Loc.T("build.vsCc", cc), true));

        var pressure = new List<Need>();
        if (heal >= 2) pressure.Add(new Need("antiheal", 2.0, Loc.T("build.vsHeal", heal), false));
        else if (heal == 1) pressure.Add(new Need("antiheal", 1.0, Loc.T("build.vsHeal", heal), false));
        if (tanks >= 2)
            pressure.Add(new Need(iAmMagic ? "magicpen" : "armorpen", 1.5,
                                  Loc.T("build.vsTank", tanks), false));
        if (shield >= 2)
            pressure.Add(new Need("antishield", 1.0,
                                  Loc.T("build.vsShield", Math.Round(shield)), false));

        // Стиль состава. Он говорит не про тип урона, а про то, КАК по тебе
        // попадают, и на это есть свои ответы: против прыжка — стазис или
        // воскрешение (успеть пережить взрыв), против дальнего размена и подлова
        // — щит от одного умения, против фронта — процентное пробивание.
        switch (ChampionTraits.DominantStyle(enemies))
        {
            case ChampionTraits.Arch.Dive:
                defense.Add(new Need("stasis", 1.3, Loc.T("build.vsDive"), true));
                break;
            case ChampionTraits.Arch.PickPoke:
                defense.Add(new Need("spellshield", 1.1, Loc.T("build.vsPoke"), true));
                break;
            case ChampionTraits.Arch.FrontToBack:
                pressure.Add(new Need(iAmMagic ? "magicpen" : "armorpen", 1.2,
                                      Loc.T("build.vsFront"), false));
                break;
        }


        // Обе задачи — в ОДНОЙ сборке: игрок покупает один набор предметов, и
        // разносить «против физического урона» и «враг лечится» по разным
        // строкам значит заставлять его выбирать между двумя половинами ответа.
        //
        // Второй вариант отличается не набором задач, а порядком: сначала
        // выжить или сначала пробить. Часто он даёт другие предметы; если тот же
        // самый — второй строки просто не будет.
        var all = defense.Concat(pressure).ToList();
        if (all.Count == 0) return new Advice([], []);

        var covered = new List<string>();
        var safeFirst = all
            .OrderByDescending(n => n.Weight + (n.Defense ? 0.6 : 0)).ToList();
        var damageFirst = all
            .OrderByDescending(n => n.Weight + (n.Defense ? 0 : 0.6)).ToList();

        var res = new List<Adapted>();
        var a = Compose(stats, baseBuild, safeFirst, phys, magic, covered);
        if (a is not null) res.Add(a);

        var b = Compose(stats, baseBuild, damageFirst, phys, magic, []);
        // Тот же набор предметов — второй строки не нужно: две одинаковые
        // сборки выглядят как ошибка, а не как выбор.
        if (b is not null && (a is null || !b.Items.SequenceEqual(a.Items))) res.Add(b);

        return new Advice(res, covered);
    }

    private static string Pct(double share) => $"{share * 100:F0}";

    /// Предмет, который в этом матче почти ничего не даёт: броня против команды,
    /// бьющей магией, и наоборот. Именно такие слоты и надо освобождать — иначе
    /// ответ приписывается в конец, а мёртвая защита остаётся в сборке.
    private static bool Wasted(int itemId, double phys, double magic)
    {
        var f = ItemFacts.Of(itemId);
        if (f is null || f.Boots) return false;   // ботинки нужны всегда
        if (magic >= 0.55 && f.Armor >= 30 && f.MagicResist < 20) return true;
        if (phys  >= 0.55 && f.MagicResist >= 30 && f.Armor < 20) return true;
        return false;
    }

    /// Собирает вариант: база, в которой слоты заменены на ответы составу.
    private static Adapted? Compose(ChampStats stats, BuildData baseBuild,
                                    List<Need> needs, double phys, double magic,
                                    List<string> covered,
                                    IReadOnlyList<int>? avoid = null)
    {
        if (needs.Count == 0) return null;

        var items = baseBuild.Items.Take(6).ToList();
        var core = baseBuild.Core.Count > 0 ? baseBuild.Core.ToHashSet() : items.Take(2).ToHashSet();
        var added = new List<int>();
        var reasons = new List<string>();

        // Порядок важен: он и отличает «сначала выжить» от «сначала пробить».
        foreach (var need in needs)
        {
            // Уже закрыто стандартной сборкой — слот второй раз не тратим, но
            // запоминаем: игроку важно видеть, что состав разобран, а ответ на
            // него уже куплен.
            if (items.Any(i => Answers(i, need.Kind)))
            {
                if (!covered.Contains(need.Reason)) covered.Add(need.Reason);
                continue;
            }

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

            // Ботинки в сборке одни: если ответ — обувь, она занимает место
            // прежней пары, а не добавляется второй. Иначе в сборке оказывалось
            // двое сапог — сразу видно, что советовала машина.
            var picked = ItemFacts.Of(pick.Id);
            int slot;
            if (picked is { Boots: true })
            {
                slot = items.FindIndex(i => ItemFacts.Of(i) is { Boots: true });
            }
            else
            {
                // Сначала — на место предмета, который против этого состава
                // бесполезен: броня против магов не станет полезнее оттого, что
                // её часто берут. Такой слот освобождаем даже в ядре — держать в
                // нём мёртвую защиту и есть та ошибка, которую мы исправляем.
                slot = items.FindIndex(i => !added.Contains(i) && Wasted(i, phys, magic)
                                            && ItemFacts.Of(i) is not { Boots: true });
                // Иначе — последний некорневой: ядро держит самого чемпиона.
                if (slot < 0)
                    slot = items.FindLastIndex(i => !core.Contains(i) && !added.Contains(i)
                                                    && ItemFacts.Of(i) is not { Boots: true });
            }
            if (slot < 0) break;

            items[slot] = pick.Id;
            added.Add(pick.Id);
            reasons.Add(need.Reason);


            // Порядок покупки важен не меньше самого предмета. Срез лечения,
            // купленный шестым, не спасает от лечения в первых же драках — его
            // берут сразу, часто даже недостроенным. Защиту тоже двигаем вперёд,
            // но мягче: без своего урона она не выигрывает.
            //
            // Ботинки не трогаем: они и так на своём месте в порядке закупки.
            if (picked is not { Boots: true })
            {
                var target = need.Kind switch
                {
                    "antiheal" => 1,
                    "armor" or "mr" => 2,
                    _ => -1,
                };
                if (target >= 0 && slot > target)
                {
                    var moved = items[slot];
                    items.RemoveAt(slot);
                    items.Insert(Math.Min(target, items.Count), moved);
                }
            }

            // Больше трёх замен — это уже не «сборка с поправкой на врага», а
            // другой билд: в такой игрок себя не узнает и не поверит ему.
            if (added.Count >= 3) break;
        }

        // Бывает, что нужное в сборке уже есть (магзащита куплена), а бесполезное
        // всё равно занимает слот — как броня против команды, бьющей магией.
        // Меняем её на лучший подходящий предмет этого чемпиона: слот, который
        // ничего не даёт, дороже любой перестановки.
        for (var i = 0; i < items.Count; i++)
        {
            if (added.Contains(items[i]) || !Wasted(items[i], phys, magic)) continue;

            var better = stats.Items
                .Where(x => !items.Contains(x.Id) && !Wasted(x.Id, phys, magic)
                            && (avoid is null || !avoid.Contains(x.Id)))
                .Where(x => ItemFacts.Of(x.Id) is { Boots: false })
                .OrderByDescending(x => 50 + (x.Winrate - 50) * x.Games / (x.Games + 100.0))
                .FirstOrDefault();
            if (better is null) continue;

            items[i] = better.Id;
            added.Add(better.Id);
            var why = magic >= 0.6 ? Loc.T("build.vsMagic", Pct(magic)) : Loc.T("build.vsPhys", Pct(phys));
            if (!reasons.Contains(why)) reasons.Add(why);
        }

        // Последняя проверка: ботинки в сборке одни. Вторая пара может прийти из
        // самой выгрузки — она добивает набор до шести слотов ходовыми
        // предметами и не знает, что обувь не складывается.
        var boots = items.Where(i => ItemFacts.Of(i) is { Boots: true }).ToList();
        for (var extra = 1; extra < boots.Count; extra++)
        {
            var idx = items.LastIndexOf(boots[extra]);
            var repl = stats.Items
                .Where(x => !items.Contains(x.Id) && ItemFacts.Of(x.Id) is { Boots: false })
                .Where(x => !Wasted(x.Id, phys, magic))
                .OrderByDescending(x => 50 + (x.Winrate - 50) * x.Games / (x.Games + 100.0))
                .FirstOrDefault();
            if (repl is null) break;
            items[idx] = repl.Id;
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
            "stasis"     => f.Stasis,
            "spellshield" => f.SpellShield,
            _            => false,
        };
    }
}
