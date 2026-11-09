namespace Counterplay;

/// <summary>
/// Слой качественных атрибутов чемпионов (архетип, инструменты).
/// Авто-сид: выводится из тегов <see cref="ChampionTags"/> (ручная разметка) и
/// классов Data Dragon (Fighter/Tank/Mage/…). Используется для драфт-фич:
/// трифекта композиций, анти-стиль.
/// </summary>
public static class ChampionTraits
{
    public enum Arch { FrontToBack, Dive, PickPoke }

    // championId → вектор архетипа (frontToBack, dive, pickPoke), нормированный.
    public static (double F2B, double Dive, double Pick) Archetype(int id)
    {
        double f = 0, d = 0, p = 0;
        var t = ChampionTags.Get(id);

        void Add(string tag, double F, double D, double P)
        {
            if (t.Contains(tag)) { f += F; d += D; p += P; }
        }

        // Фронт-ту-бэк: танки/фронт/защита/скейл-керри сзади
        Add("tank",         0.7, 0.0, 0.0);
        Add("juggernaut",   0.6, 0.0, 0.0);
        Add("peel",         0.4, 0.0, 0.0);
        Add("shield",       0.3, 0.0, 0.0);
        Add("heal",         0.3, 0.0, 0.0);
        Add("scale",        0.4, 0.0, 0.0);
        Add("hypercarry",   0.5, 0.0, 0.0);
        // Дайв: заход/бёрст/мобильность по бэклайну
        Add("dive",         0.0, 0.6, 0.0);
        Add("burst",        0.0, 0.4, 0.1);
        Add("mobility",     0.0, 0.3, 0.0);
        Add("stealth",      0.0, 0.3, 0.0);
        Add("aggressive",   0.0, 0.3, 0.0);
        Add("kill_lane",    0.0, 0.2, 0.0);
        Add("engage",       0.2, 0.4, 0.0);
        Add("hard_cc",      0.2, 0.1, 0.2);
        // Пик/пок: дальний рейндж, ловля, простел
        Add("poke",         0.0, 0.0, 0.6);
        Add("range_poke",   0.0, 0.0, 0.5);
        Add("zone_control", 0.0, 0.0, 0.4);
        Add("hook",         0.0, 0.0, 0.5);
        Add("cc",           0.0, 0.0, 0.2);
        Add("channels_ult", 0.0, 0.0, 0.2);

        // Фолбэк по классу Data Dragon, если тегов нет.
        if (f + d + p == 0)
        {
            foreach (var c in DataDragon.ClassTags(id))
                switch (c)
                {
                    case "Tank":     f += 1.0; break;
                    case "Fighter":  f += 0.6; d += 0.4; break;
                    case "Assassin": d += 1.0; break;
                    case "Mage":     p += 0.7; f += 0.3; break;
                    case "Marksman": f += 0.6; p += 0.4; break;
                    case "Support":  f += 0.5; p += 0.5; break;
                }
        }

        var s = f + d + p;
        return s <= 0 ? (0, 0, 0) : (f / s, d / s, p / s);
    }

    public static (double F2B, double Dive, double Pick) TeamArchetype(IEnumerable<int> ids)
    {
        double f = 0, d = 0, p = 0;
        foreach (var id in ids) { var (a, b, c) = Archetype(id); f += a; d += b; p += c; }
        var s = f + d + p;
        return s <= 0 ? (0, 0, 0) : (f / s, d / s, p / s);
    }

    public static Arch Dominant(IEnumerable<int> ids)
    {
        var (f, d, p) = TeamArchetype(ids);
        if (f >= d && f >= p) return Arch.FrontToBack;
        return d >= p ? Arch.Dive : Arch.PickPoke;
    }

    /// Доминирующий архетип одного чемпиона (для значка). null — неизвестен.
    public static Arch? ChampArch(int id)
    {
        var (f, d, p) = Archetype(id);
        if (f + d + p <= 0) return null;
        if (f >= d && f >= p) return Arch.FrontToBack;
        return d >= p ? Arch.Dive : Arch.PickPoke;
    }

    /// Выраженный стиль команды — или null, если состав смешанный. По нему
    /// подсвечиваем значки тех чемпионов, которые этот стиль и задают.
    public static Arch? DominantStyle(IReadOnlyList<int> ids)
    {
        if (ids.Count < 2) return null;
        var (f, d, p) = TeamArchetype(ids);
        if (f + d + p <= 0) return null;

        var max = Math.Max(f, Math.Max(d, p));
        // Второй по величине — чтобы отличить «явный» стиль от «смешанного».
        var second = (f == max ? Math.Max(d, p) : d == max ? Math.Max(f, p) : Math.Max(f, d));
        if (max < 0.45 || max - second < 0.12) return null;

        return f == max ? Arch.FrontToBack : d == max ? Arch.Dive : Arch.PickPoke;
    }

    /// Человекочитаемый ярлык стиля команды по составу. Пусто, если чемпионов < 2.
    public static string StyleLabel(IReadOnlyList<int> ids)
    {
        if (ids.Count < 2) return "";
        var (f, d, p) = TeamArchetype(ids);
        if (f + d + p <= 0) return "";

        return DominantStyle(ids) switch
        {
            Arch.FrontToBack => Loc.T("arch.frontToBack"),
            Arch.Dive        => Loc.T("arch.dive"),
            Arch.PickPoke    => Loc.T("arch.pickPoke"),
            _                => Loc.T("style.mixed"),
        };
    }

    // Инструменты (грубые 0..2) — из тегов ChampionTags.
    public static int Engage(int id)   => ChampionTags.Has(id, "engage") ? 2 : ChampionTags.Has(id, "hard_cc") ? 1 : 0;
    public static int Gapclose(int id) => ChampionTags.Has(id, "dive") ? 2 : ChampionTags.Has(id, "mobility") ? 1 : 0;
    public static int Peel(int id)     => ChampionTags.Has(id, "peel") ? 2 : ChampionTags.Has(id, "shield") || ChampionTags.Has(id, "heal") ? 1 : 0;
    public static int Disengage(int id)=> ChampionTags.Has(id, "disengage") ? 2 : ChampionTags.Has(id, "shield") ? 1 : 0;
    public static int Burst(int id)    => ChampionTags.Has(id, "burst") ? 2 : 0;
    public static bool LongRange(int id)=> ChampionTags.Has(id, "poke") || ChampionTags.Has(id, "range_poke") || ChampionTags.Has(id, "zone_control");

    // ── Уязвимости (для item value, п.1) ────────────────────────────────────
    // Вклад чемпиона в «CC-уязвимость» 0..1 (стак → враг берёт Mercs/тенасити).
    public static double CcWeight(int id) =>
        ChampionTags.Has(id, "hard_cc") ? 1.0 : ChampionTags.Has(id, "cc") ? 0.5 : 0.0;

    // Завязан на автоатаки/крит/он-хит (бьётся Steelcaps/Frozen Heart/Randuin's).
    private static readonly HashSet<int> AutoExtra =
        [157, 777, 11, 23, 24, 10, 114, 164, 2, 5]; // Ясуо, Йоне, Йи, Тринд, Джакс, Кайл, Фиора, Камилла, Олаф, Син Чжао
    private static readonly HashSet<int> CasterAdc = [81, 110, 161, 202]; // Эзреаль, Варус, ВелКоз, Джин — не крит/авто
    public static bool AutoReliant(int id) =>
        !CasterAdc.Contains(id) &&
        (DataDragon.ClassTags(id).Contains("Marksman") || AutoExtra.Contains(id));

    // Даёт командные щиты как ключевую ценность (бьётся Serpent's Fang).
    public static bool ShieldReliant(int id) => ChampionTags.Has(id, "shield");

    // Даёт хил/реген (свой или команде) — бьётся Grievous Wounds.
    //
    // Список про то, КОГО режет гривус, а не про то, кто лечит команду: своё
    // лечение он режет так же. Поэтому здесь и Сорака, и Мундо.
    //
    // Триндамир сюда напрашивался давно: его Q лечит на полполоски, и гривус
    // против него — первое, что покупают. Признак идёт не только в предметы, но
    // и в оценку (штраф за стак хила в драфте, добавка «в команде нет хила» в
    // ARAM), поэтому добавлен по решению владельца, а не молча.
    private static readonly HashSet<int> HealExtra =
        [266, 8, 36, 19, 50, 517, 421, 223, 23]; // Аатрокс, Влад, Мундо, Варвик, Свейн, Сайлас, РекСай, Тамкенч, Триндамир
    public static bool HealReliant(int id) =>
        ChampionTags.Has(id, "heal") || HealExtra.Contains(id);

    public static bool ApBurst(int id) => DataDragon.IsApChampion(id) && Burst(id) >= 2;

    // «Толстый» фронт: враг с таким составом ощутимее наказывает моно-урон
    // (одна броня/МР рубит полкоманды), поэтому смешанный урон важнее.
    public static bool IsTanky(int id) =>
        ChampionTags.Has(id, "tank") || ChampionTags.Has(id, "juggernaut") ||
        DataDragon.ClassTags(id).Contains("Tank");

    // ── Структурная синергия jg↔sup (п.4) ───────────────────────────────────
    // Сила в ранней игре 0..3.
    public static int EarlyPower(int id)
    {
        if (ChampionTags.Has(id, "aggressive") || ChampionTags.Has(id, "kill_lane")) return 3;
        if (ChampionTags.Has(id, "hypercarry")) return 0; // гиперкэрри слабы рано
        if (ChampionTags.Has(id, "scale"))      return 1;
        return 2;
    }

    // Сила жёсткого контроля 0..2.
    public static int HardCc(int id) =>
        ChampionTags.Has(id, "hard_cc") ? 2 : ChampionTags.Has(id, "cc") ? 1 : 0;

    // ── Какой контроль чем снимается (для контр-предметов) ──────────────────
    // Микаэль снимает всё, КРОМЕ подброса и подавления — так написано у самого
    // Riot в описании предмета. Значит тем, у кого весь жёсткий контроль
    // держится на отбрасывании, притяжении или подавлении, он не ответ, и
    // предлагать его врагу против них было бы враньём.
    private static readonly HashSet<int> MikaelProof =
    [
        5,    // Синь Чжао — ульт отбрасывает
        6,    // Ургот — переворот E и подавление ульта
        54,   // Мальфит — ульт подбрасывает
        59,   // Джарван — Q+E подброс, ульт это стены
        62,   // Вуконг — ульт подбрасывает
        131,  // Диана — ульт стягивает
        154,  // Зак — все зацепы подбрасывают
        240,  // Кледд — ульт отбрасывает, Q притягивает
        254,  // Вай — Q и ульт подбрасывают
        268,  // Азир — ульт отталкивает стеной
        421,  // Рек'Сай — выныривание подбрасывает
        516,  // Орн — E и ульт подбрасывают
        90,   // Малзахар — подавление (тегов у него нет, но пусть лежит рядом)
    ];

    // Подавление снимает Ртуть (она не берёт только подброс).
    private static readonly HashSet<int> Suppressors = [6, 19, 72, 90, 223];

    /// Жёсткий контроль, который союзник может снять Микаэлем.
    public static bool CleansableCc(int id) => HardCc(id) >= 2 && !MikaelProof.Contains(id);

    /// Подавление: Микаэлем не снять, Ртутью — снять.
    public static bool Suppresses(int id) => Suppressors.Contains(id);

    /// <summary>
    /// Жёсткий контроль держится на подбросе/притяжении. Его не снимает ничто и
    /// не укорачивает стойкость — остаётся только не дать умению сработать,
    /// то есть щит от заклинания.
    /// </summary>
    public static bool DisplacementCc(int id) =>
        HardCc(id) >= 2 && MikaelProof.Contains(id) && !Suppressors.Contains(id);

    /// <summary>
    /// Взрывной урон — для подбора контр-предметов.
    ///
    /// Тега «burst» мало: размечены 143 чемпиона из 173, и среди неразмеченных
    /// Зед, Катарина, ЛеБлан, Экко — те самые, против кого Ангел-хранитель и
    /// Песочные часы и покупают. Добираем классом из справочника Riot. Отдельно
    /// от <see cref="Burst"/> нарочно: тот участвует в скоринге, и менять его
    /// значило бы заодно двигать сам подбор.
    /// </summary>
    /// Класс «Assassin» Riot вешает и на тех, кто убивает автоатаками
    /// (Триндамир, Мастер Йи) — им ответ не Ангел-хранитель, а броня и
    /// ботинки, поэтому их отсюда исключаем.
    public static bool BurstThreat(int id) =>
        Burst(id) >= 2 || (DataDragon.ClassTags(id).Contains("Assassin") && !AutoReliant(id));

    // ── Дыры состава (RecommendationEngine.Needs.cs) ─────────────────────────
    // Кто какую дыру закрывает: по ним тег на карточке пика и небольшая
    // надбавка в счёт (W_NEED). Отдельно от Engage, Disengage, HardCc нарочно —
    // те участвуют в анти-стиле и прочих слагаемых, и править их значило бы
    // двигать подбор шире, чем задумано (так же сделано с BurstThreat).

    /// Стоит впереди и принимает урон: танк или джаггернаут.
    public static bool Frontline(int id) => IsTanky(id);

    // Чем сорвать чужой заход и отойти: отбрасывание, стена, отмена рывков,
    // контроль в зоне, спасение союзника. Метка «disengage» в ChampionTags
    // стоит у одной Жанны, а «щит» (его Disengage считает за единицу) врага не
    // оттолкнёт. Список проверен по всем 173 чемпионам 09.11; новые (Локк,
    // Юнара, Заахен) — убийца, стрелок и боец, дизэнгейджа у них нет.
    private static readonly HashSet<int> DisengageKit =
    [
        40,   // Жанна — смерч и ульт отбрасывают
        79,   // Грагас — таран и бочка-ульт отбрасывают
        12,   // Алистар — удар головой
        78,   // Поппи — W гасит рывки, ульт отбрасывает
        201,  // Браум — щит-стена, ульт подбрасывает
        223,  // Таам Кенч — проглотить союзника и увести
        34,   // Анивия — стена
        48,   // Трандл — столп
        268,  // Азир — стена солдат отталкивает
        117,  // Лулу — превращение, ульт подбрасывает вокруг союзника
        163,  // Талия — минное поле и отбрасывание
        115,  // Зиггс — сумка отбрасывает
        412,  // Треш — хлыст отбрасывает, фонарь уводит
        147,  // Серафина — ульт очаровывает, E корнит
        26,   // Зилеан — замедления и ульт-воскрешение
        25,   // Моргана — щит от контроля, корень Q
        888,  // Рената — ульт обращает заход против своих
        69,   // Кассиопея — миазмы запрещают рывки
        143,  // Зайра — растения держат зону, ульт подбрасывает
        432,  // Бард — ульт останавливает всех в зоне
        427,  // Айверн — корень, Дейзи
        37,   // Сона — ульт оглушает толпу
        74,   // Хеймердингер — турели и оглушение держат зону
        43,   // Карма — ускорение и щит всей команде
        267,  // Нами — пузырь и волна
        902,  // Милио — отбрасывание Q, ульт снимает контроль
        // Добавлены 09.11 (владелец: «проверь ещё варианты»).
        16,   // Сорака — E молчанием и корнем срывает заход
        44,   // Тарик — оглушение E, ульт гасит их заход неуязвимостью
        3,    // Галио — насмешка W, ульт приземляется к союзнику под дайвом
        18,   // Тристана — ульт отбрасывает
        150,  // Гнар — мега-ульт отбрасывает в стену
        711,  // Векс — пугает тех, кто прыгает
        112,  // Виктор — гравитационное поле оглушает в зоне
        910,  // Хвэй — страх, корень в зоне, щит
    ];
    public static bool Disengager(int id) => DisengageKit.Contains(id);

    // Начинает драку жёстким заходом: метка «engage» — и те, у кого её нет,
    // хотя заход — их работа (у Вай — только «dive» и «hard_cc», а ульт у неё —
    // заход в одну цель с подбросом). Алистар был здесь, пока его не было в
    // ChampionTags; с 09.11 у него своя метка «engage».
    private static readonly HashSet<int> EngageExtra = [254];
    public static bool Initiator(int id) => Engage(id) >= 2 || EngageExtra.Contains(id);

    /// Гиперкерри, которого надо прикрывать: метки «hypercarry» и «needs_peel».
    public static bool Hypercarry(int id) =>
        ChampionTags.Has(id, "hypercarry") || ChampionTags.Has(id, "needs_peel");

    /// Прикрывает керри от ныряющих: метка «peel» (Peel = 2).
    public static bool Peeler(int id) => Peel(id) >= 2;

    // Жёсткого контроля нет (ни оглушения, ни корня, ни подброса, ни
    // притяжения). Список обратный нарочно: метки «hard_cc» в ChampionTags
    // дырявые — у Треша, Блицкранка, Лулу, Нами стоит просто «cc», у Джанны
    // нет и его, — и «мало контроля» по меткам врало бы на каждом втором
    // составе. Без контроля чемпионов мало, их проще перечислить. Сверено по
    // всем 173 чемпионам 09.11; новые Локк, Юнара, Заахен — считаются с
    // контролем, пока не проверены.
    private static readonly HashSet<int> NoHardCcKit =
    [
        11, 23, 55, 238, 91, 84, 38, 8, 166, 950,   // Йи, Триндамир, Катарина, Зед, Талон, Акали, Кассадин, Влад, Акшан, Наафири
        96, 29, 145, 21, 81, 42, 236, 360, 15, 901, 221,   // Ког'Мао, Твич, Кай'Са, Мисс Фортуна, Эзреаль, Корки, Люциан, Самира, Сивир, Смолдер, Зери
        10, 887, 17, 41, 75, 36, 420, 68,           // Кейл, Гвен, Тимо, Гангпланк, Насус, Мундо, Иллаой, Рамбл
        2, 203, 121, 104, 76, 30,                   // Олаф, Киндред, Ка'Зикс, Грейвз, Нидали, Картус
    ];
    public static bool HasHardCc(int id) => !NoHardCcKit.Contains(id);

    // Пробивает танков: урон от здоровья цели или чистый урон в наборе умений,
    // а не только через предметы (их купит кто угодно).
    private static readonly HashSet<int> AntiTankKit =
    [
        67,   // Вейн — серебряные болты, чистый урон от здоровья
        114,  // Фиора — уязвимости, чистый урон от здоровья
        887,  // Гвен — урон от здоровья с каждого удара
        96,   // Ког'Мао — W, урон от здоровья
        63,   // Брэнд — пассивка, урон от здоровья
        31,   // Чо'Гат — ульт, чистый урон
        164,  // Камилла — Q, чистый урон
        145,  // Кай'Са — пассивка и Q от недостающего здоровья
        110,  // Варус — гниль, урон от здоровья
        254,  // Вай — W, урон от здоровья и снятие брони
        876,  // Лиллия — пассивка, урон от здоровья
        102,  // Шивана — E, урон от здоровья
        10,   // Кейл — E, урон от недостающего здоровья
        11,   // Мастер Йи — E, чистый урон
        48,   // Трандл — ульт отнимает броню и сопротивление
        82,   // Мордекайзер — пассивка, урон от здоровья
        122,  // Дариус — кровотечение и ульт, чистый урон
        2,    // Олаф — E, чистый урон
        19,   // Варвик — Q, урон от здоровья
        36,   // Мундо — Q, урон от текущего здоровья
        203,  // Киндред — метки, урон от текущего здоровья
        234,  // Виего — удары от текущего здоровья
    ];
    public static bool AntiTank(int id) => AntiTankKit.Contains(id);

    // Быстро чистит волну издалека, чтобы стоять под башней против осады:
    // маги с площадными умениями и стрелки с волночисткой в наборе.
    private static readonly HashSet<int> WaveclearKit =
    [
        34, 268, 115, 101, 99, 134, 61, 112, 90, 30,   // Анивия, Азир, Зиггс, Ксерат, Люкс, Синдра, Орианна, Виктор, Мальзахар, Картус
        163, 63, 161, 74, 69, 136, 4, 13, 147, 800,    // Талия, Брэнд, Вел'Коз, Хеймердингер, Кассиопея, Аурелион Сол, ТФ, Райз, Серафина, Мэл
        910, 711, 127, 1, 45, 518, 103, 893, 245, 517, // Хвэй, Векс, Лиссандра, Энни, Вейгар, Нико, Ари, Аврора, Экко, Сайлас
        38, 131, 3, 8, 50, 25, 143,                    // Кассадин, Диана, Галио, Влад, Свейн, Моргана, Зайра
        15, 222, 21, 96, 523, 901, 221, 110, 42, 18,   // Сивир, Джинкс, Мисс Фортуна, Ког'Мао, Афелий, Смолдер, Зери, Варус, Корки, Тристана
        126, 41, 85, 68, 420,                          // Джейс, Гангпланк, Кеннен, Рамбл, Иллаой
    ];
    public static bool Waveclear(int id) => WaveclearKit.Contains(id);

    // АДК, которым нужен инициатор-саппорт (агрессивные оллин-керри).
    private static readonly HashSet<int> EngageAdc = [145, 360, 119, 236, 895, 429, 18];
    // АДК-скейлеры/авто, которым лучше энчантер/пил (нейтрализовать линию, дать скейлить).
    private static readonly HashSet<int> ScaleAdc  = [901, 15, 222, 523, 96, 67, 29, 202];
    public static bool EngageDependentAdc(int id) => EngageAdc.Contains(id);
    public static bool ScaleAdcCarry(int id)      => ScaleAdc.Contains(id);
}
