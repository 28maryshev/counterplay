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
/// Важно, что «много физического урона» — ещё не вся задача. Гайды разбирают
/// её тоньше, и эти случаи разобраны здесь тоже:
///   • двое на автоатаках — постоянный ДПС в упор, против него берут не броню
///     вообще, а предметы против критов и скорости атаки;
///   • двое убийц — тебя пытаются убрать за секунду, и спасает не защита, а
///     стазис или щит на низком здоровье (у бойца и стрелка стазиса нет);
///   • толстая цель — это не только процентное пробивание: урон от здоровья
///     цели и сбитая броня закрывают ту же задачу, а часто только они и есть;
///   • свой отхил — повод выбрать ту защиту, которая его усиливает.
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
        IReadOnlyList<string> Reasons)
    {
        /// Почему стоит каждый предмет набора — для подсказки над иконкой.
        /// Общие причины строки отвечают «зачем подбор», а игрок, наведя на
        /// предмет, спрашивает про этот предмет: ради чего он, вместо чего и
        /// почему именно он, а не соседний такой же.
        public IReadOnlyDictionary<int, ItemWhy> Why { get; init; } = new Dictionary<int, ItemWhy>();
    }

    /// Ответ целиком: варианты под состав и то, что стандартная сборка закрывает
    /// сама. Второе нужно показать: «менять нечего» без объяснения выглядит как
    /// отказ, а не как вывод.
    public sealed record Advice(
        IReadOnlyList<Adapted> Builds,
        IReadOnlyList<string> Covered)
    {
        /// Что из состава закрывает каждый предмет ходовой сборки. Нужно, когда
        /// менять нечего: строка подбора — та же ходовая, и у её предметов
        /// подсказка говорит, на что из состава они уже отвечают.
        public IReadOnlyDictionary<int, ItemWhy> BaseWhy { get; init; } = new Dictionary<int, ItemWhy>();
    }

    /// <summary>
    /// Почему предмет стоит в сборке под состав.
    /// </summary>
    /// <param name="Answers">На что в составе он отвечает: сначала то, ради
    /// чего поставлен, затем что закрывает попутно. Пусто — ни на что.</param>
    /// <param name="Added">Поставлен подбором; иначе взят из ходовой сборки.</param>
    public sealed record ItemWhy(IReadOnlyList<string> Answers, bool Added)
    {
        /// Какой предмет ходовой сборки он сменил; 0 — ничей.
        public int Replaced { get; init; }
        /// Чем сменённый был плох («броня, а они бьют магией»); пусто — тот
        /// просто занимал некорневой слот.
        public string ReplacedWhy { get; init; } = "";
        /// Взят лучшим из прочих предметов чемпиона, а не из отвечающих на
        /// задачу: так закрывают слот бесполезной защиты или лишней обуви.
        public bool BestOfAll { get; init; }
        /// Чего среди предметов чемпиона нет вовсе («магзащиту на этом чемпионе
        /// не собирают») — поэтому и взят лучший из остальных; пусто — было.
        /// Без этой пометки подсказка приписывала такому предмету чужую задачу:
        /// Мандат императора стоял под «против маг. урона».
        public string NoFit { get; init; } = "";
        /// Следующий по выигрышу из тех же кандидатов; 0 — других не было.
        public int RunnerUp { get; init; }
    }

    // Defense — пережить их урон, Pressure — пробить их. В сборке участвуют и то
    // и другое: игрок покупает один набор, а не два. Чем неудобен состав —
    // общий разбор EnemyComp (его же читает подбор рун).
    private sealed record Need(string Kind, double Weight, string Reason, bool Defense);

    /// <summary>
    /// Два варианта под состав врагов: «защита» и «давление». Пустой список —
    /// нечего предложить: у чемпиона нет подходящих предметов в его же практике
    /// или состав не требует поправок.
    /// </summary>
    public static Advice Adapt(
        ChampStats stats, BuildData baseBuild, IReadOnlyList<int> enemies,
        IReadOnlyList<int> allies,
        Func<int, (double Phys, double Magic, double True)?> damageShare)
    {
        if (!ItemFacts.Loaded || stats.Items.Count == 0 || enemies.Count == 0)
            return new Advice([], []);

        if (EnemyComp.Read(enemies, damageShare) is not { } comp) return new Advice([], []);
        var (phys, magic) = (comp.Phys, comp.Magic);
        int heal = comp.Heal, cc = comp.Cc, tanks = comp.Tanks, longCc = comp.LongCc,
            autos = comp.Autos, burst = comp.Burst;
        var shield = comp.Shield;

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
        // Ближнему бою стойкость нужна от двух таких врагов, дальнему — от трёх
        // (EnemyComp.ManyCc).
        if (comp.ManyCc(stats.ChampionId))
            defense.Add(new Need("tenacity", 1.0, Loc.T("build.vsCc", cc), true));
        // Хватит и одного такого врага: провокация или подавление выключают из
        // драки целиком, и сокращать их на треть бессмысленно — нужно снимать.
        if (longCc >= 1)
            defense.Add(new Need("cleanse", 1.4, Loc.T("build.vsLongCc", longCc), true));

        // Двое и больше на автоатаках — задача не про «много физического урона»,
        // а про постоянный ДПС в упор: он не кончается после одного размена.
        // Ответ на него отдельный: меньше урона от ударов и критов, сбитая
        // скорость атаки вокруг. Нужен он не только танку — набойки берут все.
        if (autos >= 2 && phys >= 0.5)
            defense.Add(new Need("autoattack", 1.1, Loc.T("build.vsAuto", autos), true));

        // Свой отхил — повод выбрать ту защиту, которая его усиливает: для
        // чемпиона, который живёт лечением, это больше, чем лишние цифры.
        var iHeal = EnemyComp.IsHealer(stats.ChampionId)
                    || ChampionTags.Has(stats.ChampionId, "heal")
                    || ChampionTags.Has(stats.ChampionId, "sustain");
        if (iHeal && magic >= 0.4)
            defense.Add(new Need("healamp", 1.0, Loc.T("build.selfHeal"), true));

        var pressure = new List<Need>();
        if (heal >= 2) pressure.Add(new Need("antiheal", 2.0, Loc.T("build.vsHeal", heal), false));
        else if (heal == 1) pressure.Add(new Need("antiheal", 1.0, Loc.T("build.vsHeal", heal), false));
        if (tanks >= 2)
            pressure.Add(new Need(iAmMagic ? "magicpen" : "armorpen", 1.5,
                                  Loc.T("build.vsTank", tanks), false));
        if (shield >= 2)
            pressure.Add(new Need("antishield", 1.0,
                                  Loc.T("build.vsShield", Math.Round(shield)), false));

        // Своя команда тоже решает покупку — у поддержки особенно. Есть кому
        // усиливать: магу пригодится сила умений от щита, керри на автоатаках —
        // скорость атаки. Предметы эти есть только у энчантеров, поэтому лишнего
        // никому не предложат: выбор всё равно идёт из того, что чемпион собирает.
        foreach (var ally in allies)
        {
            if (ally == stats.ChampionId) continue;
            var tags = DataDragon.ClassTags(ally);

            // «Маг» — это основная роль, а не второй тег. У Смолдера, Серафины и
            // многих стрелков с саппортами Mage стоит довеском, и по нему команда
            // получала предмет для мага там, где мага нет вовсе.
            //
            // Доли урона тут тоже не помощники: Джакс бьёт магией, но силу умений
            // не покупает — усиливать его этим предметом бессмысленно.
            var carry = tags.Contains("Marksman") || ChampionTags.Has(ally, "hypercarry");
            var mage = tags.Contains("Mage") && !carry && !tags.Contains("Support");

            if (mage && pressure.All(n => n.Kind != "allyPower"))
                pressure.Add(new Need("allyPower", 0.9, Loc.T("build.allyMage"), false));
            if (carry && pressure.All(n => n.Kind != "allyAttack"))
                pressure.Add(new Need("allyAttack", 0.9, Loc.T("build.allyCarry"), false));
        }

        // Свой состав решает покупку не меньше вражеского. Если почти весь урон
        // команды одного типа, враг оденется именно против него: против магов
        // возьмут магзащиту, против физического — броню, и тем охотнее, чем
        // очевиднее перекос. Пробивание тогда возвращает то, что они себе
        // добавили, и стоит дороже лишних цифр урона.
        double teamPhys = 0, teamMagic = 0;
        foreach (var mate in allies)
        {
            var share = damageShare(mate);
            if (share is { } s && s.Phys + s.Magic > 0) { teamPhys += s.Phys; teamMagic += s.Magic; }
            else if (DataDragon.IsApChampion(mate)) teamMagic += 0.7;
            else teamPhys += 0.7;
        }
        var teamSum = teamPhys + teamMagic;
        if (teamSum > 0)
        {
            // Порог высокий: при 60/40 враг всё равно берёт обе защиты, и
            // пробивание не окупается. Перекос начинается дальше.
            var same = (iAmMagic ? teamMagic : teamPhys) / teamSum;
            if (same >= 0.65)
                pressure.Add(new Need(iAmMagic ? "penMagic" : "penArmor", 1.0,
                                      Loc.T(iAmMagic ? "build.teamAp" : "build.teamAd", Pct(same)),
                                      false));
        }

        // Стиль состава. Он говорит не про тип урона, а про то, КАК по тебе
        // попадают, и на это есть свои ответы: против прыжка — стазис или
        // воскрешение (успеть пережить взрыв), против дальнего размена и подлова
        // — щит от одного умения, против фронта — процентное пробивание.
        switch (comp.Style)
        {
            case ChampionTraits.Arch.Dive:
                defense.Add(new Need("survive", 1.3, Loc.T("build.vsDive"), true));
                break;
            case ChampionTraits.Arch.PickPoke:
                defense.Add(new Need("poke", 1.1, Loc.T("build.vsPoke"), true));
                break;
            case ChampionTraits.Arch.FrontToBack:
                pressure.Add(new Need(iAmMagic ? "magicpen" : "armorpen", 1.2,
                                      Loc.T("build.vsFront"), false));
                break;
        }

        // Двое убийц — это про наскок, даже если по голосованию состав вышел не
        // «дайвом»: одного прыжка хватает, чтобы тебя убрали до первой драки.
        // Если стиль уже дал ту же задачу, второй раз её не ставим.
        if (burst >= 2 && defense.All(n => n.Kind != "survive"))
            defense.Add(new Need("survive", 1.2, Loc.T("build.vsBurst", burst), true));

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

        var baseWhy = baseBuild.Items.Take(6).Distinct()
            .ToDictionary(i => i, i => new ItemWhy(AnswersOf(i, all), false));
        return new Advice(res, covered) { BaseWhy = baseWhy };
    }

    private static string Pct(double share) => $"{share * 100:F0}";

    /// На что из задач состава отвечает предмет — подписи задач без повторов
    /// (у смешанного урона подпись одна на броню и магзащиту).
    private static List<string> AnswersOf(int itemId, IEnumerable<Need> needs) =>
        needs.Where(n => Answers(itemId, n.Kind)).Select(n => n.Reason).Distinct().ToList();

    /// Чем предмет бесполезен в этом матче: броня против команды, бьющей
    /// магией (Armor), магзащита против физического урона (Mr); null — не
    /// бесполезен. Именно такие слоты и надо освобождать — иначе ответ
    /// приписывается в конец, а мёртвая защита остаётся в сборке.
    ///
    /// Одна проверка и на замену, и на её подпись: до 20.11 подпись выбиралась
    /// своим порогом (60% магии вместо 55%), и при магии 57% броню меняли с
    /// причиной «против физ. урона (43%)».
    private enum Waste { Armor, Mr }

    private static Waste? WasteOf(int itemId, double phys, double magic)
    {
        var f = ItemFacts.Of(itemId);
        if (f is null || f.Boots) return null;   // ботинки нужны всегда
        if (magic >= 0.55 && f.Armor >= 30 && f.MagicResist < 20) return Waste.Armor;
        if (phys  >= 0.55 && f.MagicResist >= 30 && f.Armor < 20) return Waste.Mr;
        return null;
    }

    private static bool Wasted(int itemId, double phys, double magic) =>
        WasteOf(itemId, phys, magic) is not null;

    /// Подписи бесполезного предмета: задача для строки («против маг. урона»)
    /// и чем плох сам предмет («броня, а они бьют магией») — для подсказки
    /// того, кто встал на его место.
    private static (string Need, string Item) WasteWhy(Waste w, double phys, double magic) =>
        w == Waste.Armor
            ? (Loc.T("build.vsMagic", Pct(magic)), Loc.T("build.wastedArmor", Pct(magic)))
            : (Loc.T("build.vsPhys", Pct(phys)), Loc.T("build.wastedMr", Pct(phys)));

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
        // Почему встал каждый новый предмет; остальным причину допишем в конце —
        // что из состава они закрывают.
        var why = new Dictionary<int, ItemWhy>();

        // Кого предмет сменил. Если на этом месте уже стоял наш же новый
        // предмет (обувь под другую задачу), сменённым считается то, что было
        // в ходовой сборке: игрок сравнивает с ней, а не с промежуточным шагом.
        int Original(int id) => why.TryGetValue(id, out var w) && w.Replaced != 0 ? w.Replaced : id;

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

            // Порог популярности: в выгрузку попадает всё, что набрало 40 игр, и
            // без отсечки в ответ пролезала экзотика — предмет, который на этом
            // чемпионе берут раз в сотню игр. Совет должен оставаться тем, что
            // на нём действительно собирают.
            // Считаем долю не от игр чемпиона, а от его САМОГО ходового предмета:
            // списки предметов собираются по другой выборке, и процент от игр
            // чемпиона отсекал вообще всё.
            var topGames = stats.Items.Max(i => i.Games);
            var minGames = Math.Max(40, topGames * 0.02);

            // Чистый танковый предмет предлагаем только тем, кто его носит.
            // Иначе энчантеру прилетает Jak'Sho: в выгрузке он есть (кто-то
            // собрал), но советовать такое нельзя.
            var myTags = DataDragon.ClassTags(stats.ChampionId);
            var iAmBeefy = myTags.Contains("Tank") || myTags.Contains("Fighter");

            // Обувь, которую подбор уже поставил под задачу поважнее, другая
            // задача не перебивает: иначе Ртутные под контроль сменялись
            // Бронированными сапогами под броню, ответ на контроль молча
            // пропадал, а в причинах оставался — а если в ходовой и были эти
            // сапоги, выходило «Бронированные сапоги вместо Бронированных
            // сапог». Такой задаче — предмет, не обувь.
            var bootsTaken = items.Any(i => added.Contains(i) && ItemFacts.Of(i) is { Boots: true });

            var fits = stats.Items
                .Where(i => !items.Contains(i.Id) && (avoid is null || !avoid.Contains(i.Id)))
                .Where(i => Answers(i.Id, need.Kind))
                .Where(i => !bootsTaken || ItemFacts.Of(i.Id) is not { Boots: true })
                .Where(i => i.Games >= minGames)
                .Where(i => iAmBeefy || ItemFacts.Of(i.Id) is not { PureTank: true })
                // И не проигрывающий: отклонение от 50% приглушаем объёмом, иначе
                // редкий предмет с плохим винрейтом выглядит хуже, чем он есть.
                .Where(i => 50 + (i.Winrate - 50) * i.Games / (i.Games + 100.0) >= 49.5)
                // Среди подходящих берём тот, что чаще ВЫИГРЫВАЕТ на этом
                // чемпионе. Сырой винрейт брать нельзя: предмет с 60% на полусотне
                // игр — это чаще всего выбор тех, кто и так выигрывал. Поэтому
                // отклонение от 50% приглушается объёмом выборки, как и везде в
                // движке: на 700 играх оно засчитывается почти целиком, на 60 —
                // примерно на треть.
                .OrderByDescending(i => 50 + (i.Winrate - 50) * i.Games / (i.Games + 100.0))
                .ThenByDescending(i => i.Games)
                .ToList();
            if (fits.Count == 0) continue;
            var pick = fits[0];
            // Запасной вариант — того же вида: ботинки встают на место обуви, а
            // не в слот предмета, и «запасной к Ледяному сердцу — сапоги»
            // читается как ошибка.
            var pickBoots = ItemFacts.Of(pick.Id) is { Boots: true };
            var runnerUp = fits.Skip(1).FirstOrDefault(i => (ItemFacts.Of(i.Id) is { Boots: true }) == pickBoots);

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

            var old = items[slot];
            var waste = WasteOf(old, phys, magic);
            why[pick.Id] = new ItemWhy([need.Reason], true)
            {
                Replaced = Original(old),
                ReplacedWhy = waste is { } w ? WasteWhy(w, phys, magic).Item : "",
                RunnerUp = runnerUp?.Id ?? 0,
            };
            items[slot] = pick.Id;
            added.Add(pick.Id);
            if (!reasons.Contains(need.Reason)) reasons.Add(need.Reason);

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
        // Слот, который ничего не даёт, дороже любой перестановки.
        //
        // На его место — прежде всего защита от того урона, которым бьют
        // (магзащита вместо брони): игрок ждёт именно её, а при 70% магии и
        // вторая магзащита не лишняя. Такой у чемпиона нет — лучший из
        // остальных его предметов, и подсказка так и говорит (NoFit). До 20.11
        // здесь брался лучший вообще, а подпись строки была «против маг.
        // урона»: у Соны на место брони вставал Мандат императора, и владелец
        // спросил, при чём тут он.
        var topAll = stats.Items.Max(x => x.Games);
        var beefy = DataDragon.ClassTags(stats.ChampionId) is var tags
                    && (tags.Contains("Tank") || tags.Contains("Fighter"));
        for (var i = 0; i < items.Count; i++)
        {
            if (added.Contains(items[i]) || WasteOf(items[i], phys, magic) is not { } waste) continue;

            var pool = stats.Items
                .Where(x => !items.Contains(x.Id) && !Wasted(x.Id, phys, magic)
                            && (avoid is null || !avoid.Contains(x.Id)))
                .Where(x => ItemFacts.Of(x.Id) is { Boots: false })
                .Where(x => x.Games >= Math.Max(40, topAll * 0.02))
                .Where(x => beefy || ItemFacts.Of(x.Id) is not { PureTank: true })
                .OrderByDescending(x => 50 + (x.Winrate - 50) * x.Games / (x.Games + 100.0))
                .ToList();
            if (pool.Count == 0) continue;

            // Защита — с тем же отсевом, что у ответов на задачи: не проигрывающая.
            var defense = waste == Waste.Armor ? "mr" : "armor";
            var fit = pool
                .Where(x => Answers(x.Id, defense)
                            && 50 + (x.Winrate - 50) * x.Games / (x.Games + 100.0) >= 49.5)
                .ToList();
            var noFit = fit.Count == 0;
            var from = noFit ? pool : fit;

            var (need, itemWhy) = WasteWhy(waste, phys, magic);
            why[from[0].Id] = new ItemWhy(noFit ? [] : [need], true)
            {
                Replaced = items[i],
                ReplacedWhy = itemWhy,
                BestOfAll = noFit,
                NoFit = noFit ? Loc.T(waste == Waste.Armor ? "build.noFitMr" : "build.noFitArmor") : "",
                RunnerUp = from.Count > 1 ? from[1].Id : 0,
            };
            items[i] = from[0].Id;
            added.Add(from[0].Id);
            // Задачу в причины строки — только если предмет на неё отвечает.
            if (!noFit && !reasons.Contains(need)) reasons.Add(need);
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
            why[repl.Id] = new ItemWhy([], true)
            {
                Replaced = Original(boots[extra]),
                ReplacedWhy = Loc.T("build.oneBoots"),
                BestOfAll = true,
            };
            items[idx] = repl.Id;
        }

        if (added.Count == 0) return null;

        // Причина у каждого предмета набора. Новому — то, ради чего он встал, и
        // что он закрывает попутно (броня с срезом лечения отвечает на обе
        // задачи); взятому из ходовой — что из состава он уже закрывает.
        var whyAll = new Dictionary<int, ItemWhy>();
        foreach (var id in items.Distinct())
        {
            var also = AnswersOf(id, needs);
            whyAll[id] = why.TryGetValue(id, out var w)
                ? w with { Answers = w.Answers.Concat(also).Distinct().ToList() }
                : new ItemWhy(also, false);
        }
        return new Adapted(items, added, reasons) { Why = whyAll };
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
            // Против толстых целей работает процентное пробивание, сбитая защита
            // и урон, считающийся от здоровья цели; летальность бьёт хрупких и
            // здесь бесполезна. Тип урона у предмета берём по силе умений: у
            // мага и стрелка ответ на танка разный, хотя задача одна.
            "armorpen"   => (f.ArmorPen && f.PercentPen) || f.ShredsArmor
                            || (f.TargetHealth && !f.Magical),
            "magicpen"   => (f.MagicPen && f.PercentPen) || f.ShredsMr
                            || (f.TargetHealth && f.Magical),
            // Против НАБРАННЫХ резистов работает только пробивание: урон от
            // здоровья цели их не обходит — он тоже режется бронёй и магзащитой.
            // Ботинки с пробиванием считаем: они занимают место прежней обуви,
            // а не слот под предмет.
            "penArmor"   => (f.ArmorPen && f.PercentPen) || f.ShredsArmor,
            "penMagic"   => (f.MagicPen && f.PercentPen) || f.ShredsMr
                            || (f.Boots && f.MagicPen),
            "antishield" => f.AntiShield,
            // Пережить наскок можно двумя способами: выпасть из боя (стазис,
            // воскрешение) или получить щит на низком здоровье. Второе — ответ
            // бойцов и стрелков, у которых стазиса нет и не будет.
            "survive"    => f.Stasis || f.Lifeline,
            // Против размена на дистанции — не пропустить ключевое умение или
            // отхиливать размен обратно; и то и другое ломает их план.
            "poke"       => f.SpellShield || f.Lifesteal,
            "autoattack" => f.AutoDefense || f.SlowsAttack,
            "healamp"    => f.SelfHealAmp,
            "cleanse"    => f.Cleanse,
            "allyPower"  => f.BuffsAllyPower,
            "allyAttack" => f.BuffsAllyAttack,
            _            => false,
        };
    }
}
