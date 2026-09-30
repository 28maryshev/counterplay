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

    // ID предметов Data Dragon. Каждый сверен с item.json патча 16.19.1: есть на
    // Ущелье, финальный (не заготовка), и делает ровно то, что написано рядом.
    // Сверка нужна не для порядка: на ней отвалились два предположения по
    // памяти — Микаэль не снимает подбросы, а Ртуть снимает подавление.
    private const int
        // ── Сопротивление: их берёт передняя линия ──────────────────────────
        Thornmail     = 3075, // броня + шипы + гривус по бьющему
        RanduinOmen   = 3143, // броня, −30% урона от критов
        FrozenHeart   = 3110, // броня, −20% скорости атаки вокруг
        SpiritVisage  = 3065, // МР + усиление лечения на себе
        KaenicRookern = 2504, // МР + магический щит, если давно не получал магии
        ForceOfNature = 4401, // МР стаком за полученную магию
        Steelcaps     = 3047, // ботинки: −10% урона от автоатак
        MercTreads    = 3111, // ботинки: 30% стойкости

        // ── Снять контроль ──────────────────────────────────────────────────
        Mikael        = 3222, // с союзника — КРОМЕ подброса и подавления
        Mercurial     = 3139, // с себя — кроме подброса (подавление снимает)
        Sterak        = 3053, // 20% стойкости + щит на низком ХП, для бойца

        // ── Заблокировать заклинание ────────────────────────────────────────
        Banshee       = 3102, // щит от одного заклинания, AP-цена
        EdgeOfNight   = 3814, // тот же щит, AD-цена с летальностью

        // ── Пережить взрыв ──────────────────────────────────────────────────
        Zhonya        = 3157, // стазис 2,5 с
        GuardianAngel = 3026, // воскрешение
        Shieldbow     = 6673, // щит на низком ХП, для крит-стрелка
        Maw           = 3156, // щит от магического урона на низком ХП
        WitsEnd       = 3091, // МР + стойкость + магический он-хит

        // ── Гривус: у каждого покупателя свой ───────────────────────────────
        Morello       = 3165, // магу и энчантеру
        MortalReminder= 3033, // крит-стрелку
        Chempunk      = 6609, // бойцу/убийце

        // ── Прочее ──────────────────────────────────────────────────────────
        SerpentFang   = 6695; // срезает щиты

    /// <summary>
    /// Все предметы, какие может вернуть <see cref="CounterItems"/>.
    /// Единственный источник для предзагрузки иконок: пока списки жили порознь,
    /// новый предмет попадал в подбор, но иконки к нему не было — и он молча
    /// пропадал из строки.
    ///
    /// Список сверен со сборками всех 668 связок «чемпион+роль»: то, чего не
    /// покупает НИКТО, отсюда убрано. Так ушли Объятья серафима, Химтех-гнилушка
    /// и Цепи проклятия (0 связок из 668), а Ртутный кушак заменён Ртутным
    /// ятаганом: в сборках лежат только готовые предметы, заготовок там нет.
    /// </summary>
    public static readonly int[] All =
    [
        Thornmail, RanduinOmen, FrozenHeart, SpiritVisage, KaenicRookern, ForceOfNature,
        Steelcaps, MercTreads, Mikael, Mercurial, Sterak, Banshee, EdgeOfNight,
        Zhonya, GuardianAngel, Shieldbow, Maw, WitsEnd,
        Morello, MortalReminder, Chempunk, SerpentFang,
    ];

    /// Предметы, которыми враг накажет команду, ЕСЛИ взять этот пик. Показываем строкой
    /// иконок под описанием. Смысл — предупреждение о СТАКЕ: одним предметом враг рубит
    /// полкоманды. Поэтому предмет показывается только пику, который ДОБАВЛЯЕТ к перекосу
    /// (ещё один AD в АД-команду), а не тому, кто её разбавляет.
    /// <param name="enemyIds">Состав врага. Без него предупреждение висело в
    /// пустоте: показывали броню и МР, кто бы их ни собирал. Владелец поймал на
    /// живом драфте — у врага из танков один Наутилус, а на топе Триндамир,
    /// который идёт в керри; собирать Терновник там просто некому.</param>
    /// <param name="buildOf">
    /// Ходовые предметы конкретного врага — то, что на нём реально собирают в
    /// матчах. null для чемпиона означает «не знаем» (данные не подъехали), и
    /// тогда решает только класс, как раньше. Владелец: «будут возмущаться, что
    /// Посох серафима на ЛеБлан или Зайру никто не собирает, — почему он там».
    /// И правда: класс говорит, кому предмет ПО РУКЕ, а не кто его берёт.
    /// </param>
    public static IReadOnlyList<int> CounterItems(int candidate, IReadOnlyList<int> allyIds,
                                                  IReadOnlyList<int>? enemyIds = null,
                                                  Func<int, IReadOnlyCollection<int>?>? buildOf = null)
    {
        var enemies = enemyIds ?? [];
        var foes = enemies.Select(e => Describe(e, buildOf)).ToList();

        // Кто у врага купит ИМЕННО ЭТОТ предмет: подходит по роли в бою И правда
        // его собирает. Про кого сборок нет — судим по роли, как раньше.
        //
        // Врагов ещё не показали — не показываем НИЧЕГО. Сначала тут стояло
        // «судить не о чем, лучше предупредить», и на пустом составе вылезал
        // весь список. Владелец: «врагов ещё не выбрало, а итемы уже
        // показывает — мы же договорились, что только те, которые смогут
        // собрать враги». Договорённость сильнее: предупреждение без того, кто
        // его исполнит, — это не осторожность, а выдумка.
        bool Buys(int item, Func<Foe, bool> who) =>
            foes.Any(f => who(f) && (f.Build is null || f.Build.Contains(item)));

        int frontline = foes.Count(f => f.Frontline);

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
        var stack = new List<int>();

        // Один танк такие предметы возьмёт разве что случайно, и «одним предметом
        // рубит полкоманды» про него уже неправда. Тот же порог, по которому
        // смягчается штраф за перекос урона, — признак один.
        bool twoTanks = frontline >= 2;
        void Resist(int item) { if (twoTanks && Buys(item, f => f.Frontline)) stack.Add(item); }

        // Перекос типа урона: предупреждаем ТОЛЬКО пик того же типа, что стак, и только
        // когда команда почти вся одного типа (враг возьмёт броню/МР на всех).
        if (candAd && ad >= 3 && ad - ap >= 2)
            { Resist(Thornmail); Resist(RanduinOmen); Resist(FrozenHeart); }   // броня + скорость атаки
        else if (candAp && ap >= 3 && ap - ad >= 2)
            { Resist(KaenicRookern); Resist(ForceOfNature); Resist(SpiritVisage); } // МР

        // Стак контроля: пик добавляет CC и его в команде уже ≥2 → враг возьмёт тенасити.
        if (ChampionTraits.HardCc(candidate) >= 1 && cc >= 2
            && Buys(MercTreads, _ => true)) stack.Add(MercTreads);

        // Стак хила: пик хилит и хила в команде уже ≥2 → враг возьмёт гривус.
        // Гривус: Морелло покупает маг, Терновник — передняя линия. Нет ни
        // того, ни другого — и предупреждать не о чем.
        if (ChampionTraits.HealReliant(candidate) && heal >= 2)
        {
            if (candAp) { if (Buys(Morello, f => f.Mage)) stack.Add(Morello); }
            else        { Resist(Thornmail); }
        }

        // Сначала ответы на САМ пик — карточка про него и есть, — но два места
        // остаются за предупреждением о перекосе команды: оно сюда и ставилось.
        return stack.Take(2)
                    .Concat(AgainstThisPick(candidate, foes, Buys))
                    .Concat(stack.Skip(2))
                    .Distinct().Take(5).ToList();
    }

    /// <summary>
    /// Враг в разрезе «кто что покупает»: роль в бою плюс его настоящая сборка.
    /// Один предмет редко подходит всем — Морелло носит маг, Напоминание о
    /// смерти крит-стрелок, Бензопилу боец, Химтех-гнилушку энчантер.
    /// </summary>
    private readonly record struct Foe(
        int Id, bool Enchanter, bool Mage, bool Marksman, bool Fighter,
        bool Assassin, bool Frontline, IReadOnlyCollection<int>? Build)
    {
        public bool Lethality => Fighter || Assassin;   // кому по руке летальность
    }

    private static Foe Describe(int id, Func<int, IReadOnlyCollection<int>?>? buildOf)
    {
        bool tanky = ChampionTraits.IsTanky(id);
        bool mage = false, marksman = false, fighter = false, assassin = false;
        if (!tanky)   // танк своё уже получил
        {
            if (DataDragon.IsApChampion(id)) mage = true;
            else if (ChampionTraits.AutoReliant(id)) marksman = true;
            else if (DataDragon.IsAdChampion(id))
            {
                if (DataDragon.ClassTags(id).Contains("Assassin")) assassin = true;
                else fighter = true;
            }
        }
        return new Foe(id, ChampionTraits.Peel(id) >= 1, mage, marksman, fighter,
                       assassin, tanky, buildOf?.Invoke(id));
    }

    /// <summary>
    /// Предметы против САМОГО пика, а не против перекоса команды.
    ///
    /// Прежняя таблица знала только про стаки — урон одного типа, контроль,
    /// хил. Но чаще всего враг покупает ответ на КОНКРЕТНОГО чемпиона: на
    /// провокацию Раммуса берут Микаэля, на хук — Банши. Владелец это и просил
    /// добавить.
    ///
    /// Каждое правило — пара условий: что в пике приглашает предмет и КТО у
    /// врага его купит. Без второго условия список снова повиснет в пустоте.
    /// Действие предметов сверено с их описаниями в item.json, а не взято по
    /// памяти: Микаэль, например, НЕ снимает подбросы и подавление, поэтому
    /// подбрасывающим он не ответ.
    /// </summary>
    private static List<int> AgainstThisPick(
        int candidate, IReadOnlyList<Foe> foes, Func<int, Func<Foe, bool>, bool> Buys)
    {
        var it = new List<int>();
        void Add(bool when, int item) { if (when) it.Add(item); }
        int Count(Func<Foe, bool> who) => foes.Count(who);

        bool candAp   = DataDragon.IsApChampion(candidate);
        bool suppress = ChampionTraits.Suppresses(candidate);

        // ── Снять контроль ─────────────────────────────────────────────────
        // Микаэль снимает станы, корни, провокации — но НЕ подбросы и НЕ
        // подавление (так в описании предмета). Раммусу с его провокацией он
        // ответ, Мальфиту с подбросом — нет, и предлагать его там было бы
        // враньём.
        Add(ChampionTraits.CleansableCc(candidate) && Buys(Mikael, f => f.Enchanter), Mikael);
        // Подавление Микаэлем не снять, Ртутью — снять. Её берёт тот, на кого
        // ульт и нацелен: керри.
        Add(suppress && Buys(Mercurial, f => !f.Frontline), Mercurial);

        // ── Гривус ─────────────────────────────────────────────────────────
        // Самая однозначная покупка в игре: хил на той стороне — и предмет
        // берут в первые же минуты. Поэтому идёт вперёд остальных.
        // Показываем ДВА — те, под чьих покупателей во вражеском составе людей
        // больше: одним гривусом обходятся редко, четырьмя — никогда.
        if (ChampionTraits.HealReliant(candidate))
        {
            (int Item, Func<Foe, bool> Who)[] grievous =
            [
                (Morello,        f => f.Mage),        // магу и энчантеру: он тоже AP
                (MortalReminder, f => f.Marksman),    // крит-стрелку
                (Chempunk,       f => f.Fighter),     // бойцу
            ];
            it.AddRange(grievous.Where(g => Buys(g.Item, g.Who))
                                .OrderByDescending(g => Count(g.Who))
                                .Take(2).Select(g => g.Item));
            // Терновник вешает гривус сам, но только на того, кто его бьёт.
            Add(ChampionTraits.AutoReliant(candidate) && Buys(Thornmail, f => f.Frontline), Thornmail);
        }

        // ── Заблокировать заклинание ───────────────────────────────────────
        // Щит от заклинания гасит ОДНО умение — значит он ответ тем, у кого с
        // одного умения всё и начинается: хук, подавление, стан под взрыв. И
        // он же единственный ответ на подброс: его не снять и не укоротить.
        bool oneSpell = ChampionTags.Has(candidate, "hook") || suppress
                        || ChampionTraits.DisplacementCc(candidate)
                        || (ChampionTraits.HardCc(candidate) >= 2 && ChampionTraits.BurstThreat(candidate));
        Add(oneSpell && Buys(Banshee, f => f.Mage), Banshee);           // магу по руке Банши
        Add(oneSpell && Buys(EdgeOfNight, f => f.Lethality), EdgeOfNight); // убийце и бойцу

        // ── Пережить взрыв ─────────────────────────────────────────────────
        if (ChampionTraits.BurstThreat(candidate))
        {
            if (candAp)
            {
                Add(Buys(Zhonya, f => f.Mage), Zhonya);              // маг уходит в стазис
                Add(Buys(WitsEnd, f => f.Marksman), WitsEnd);        // стрелку — МР и стойкость
                Add(Buys(Maw, f => f.Lethality), Maw);               // бойцу — щит от магии
                Add(Buys(KaenicRookern, f => f.Frontline), KaenicRookern);
            }
            else
            {
                Add(Buys(GuardianAngel, f => f.Marksman), GuardianAngel);  // стрелку — воскрешение
                Add(Buys(Shieldbow, f => f.Marksman), Shieldbow);
                Add(Buys(Zhonya, f => f.Mage), Zhonya);
            }
        }

        // ── Пережить автоатаки ─────────────────────────────────────────────
        if (ChampionTraits.AutoReliant(candidate))
        {
            Add(Buys(Steelcaps, _ => true), Steelcaps);               // ботинки берут почти все
            Add(Buys(RanduinOmen, f => f.Frontline), RanduinOmen);    // −30% урона от критов
            Add(Buys(FrozenHeart, f => f.Frontline), FrozenHeart);    // −20% скорости атаки
        }

        // ── Срезать щиты ───────────────────────────────────────────────────
        // Клык — предмет летальности: его носят убийцы и бойцы, не танки.
        Add(ChampionTraits.ShieldReliant(candidate) && Buys(SerpentFang, f => f.Lethality), SerpentFang);

        // ── Пересидеть контроль ────────────────────────────────────────────
        // Стойкость укорачивает стан и корень, но НЕ подброс, — поэтому только
        // снимаемый контроль. Боец не убегает, он переживает.
        Add(ChampionTraits.CleansableCc(candidate) && Buys(Sterak, f => f.Fighter), Sterak);


        return it;
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
