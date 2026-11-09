namespace Counterplay;

// Движок подбора — чего не хватает составу: дыры команды, теги на карточке
// пика и надбавка в счёт тому, кто дыру закрывает.
//
// Владелец, 09.11: «надо добавить дизэнгейдж и фронтлайн теги в случае если не
// хватает этого», «если собирается определённый стиль — добавить то, чего не
// хватает, чтобы дособрать», и следом — все варианты из поиска по гайдам
// (общий фронт, защита керри, контроль, тип урона, урон по танкам, чистка
// волн, ранняя игра, красный тег у последнего пика, вес в счёте).
public sealed partial class RecommendationEngine
{
    // Сколько союзников (без меня) должно быть выбрано, чтобы о дыре без стиля
    // говорить всерьёз: с одним-двумя пиками «танка нет» значит только «ещё не
    // выбрали». Дыры стиля — раньше, со стилем (DominantStyle — от двух).
    private const int GAP_ALLIES_MIN = 3;
    private const int NEED_TAGS_MAX = 2;          // зелёных тегов дыр на карточке
    private const double NEED_BONUS_MAX = 1.5;    // сумма цен закрытых дыр, потолок, пп

    /// <summary>
    /// Цена закрытой дыры, настоящих пунктов победы (в счёт — × W_NEED =
    /// W_COUNTER_PP). Замер 09.11.2026 по исходам: изумруд 16.21, 807 тыс.
    /// драфтов (StyleFeatures → draft_gaps, pipeline/calibrate.py); сколько
    /// теряет команда, у которой дыра осталась открытой к концу драфта, при
    /// силе и матчапах в той же регрессии. Правило переноса: оценка больше
    /// двух погрешностей — как есть, меньше — половина, отрицательная — 0.
    ///   волны      −0,89 ± 0,11 → 0,89     пил        −0,51 ± 0,10 → 0,51
    ///   танки      −0,39 ± 0,11 → 0,39     инициация  −0,36 ± 0,17 → 0,36
    ///   контроль   −1,09 ± 0,68 → 0,55     фронт      −0,11 ± 0,10 → 0,05
    ///   отход      +0,19 ± 0,23 → 0        ранняя     5 драфтов — не измерить → 0
    ///   тип урона  −3,68 ± 0,30 → 0: перекос уже в счёте (DamageBalance, W_DMGBAL)
    /// До замера стояли на глаз 1,0 у фронта и отхода — по исходам они почти
    /// ничего не стоят; теги остаются (владелец просил их видеть), вес — нет.
    /// </summary>
    private static readonly Dictionary<string, double> GapWeight = new()
    {
        ["needFront"] = 0.05, ["needDisengage"] = 0, ["needEngage"] = 0.36,
        ["needPeel"] = 0.51, ["needCc"] = 0.55, ["needAp"] = 0, ["needAd"] = 0,
        ["needAntiTank"] = 0.39, ["needWaveclear"] = 0.89, ["needEarly"] = 0,
    };

    /// <summary>
    /// Чья это работа: дыру закрывает тот, чья роль за неё отвечает, — у прочих
    /// ни тега (зелёного или красного), ни надбавки. Владелец (09.11, по снимку:
    /// мид последним к Зери без прикрытия, у всех мидеров красная «Защита
    /// керри») — «у мидеров светится "защита керри", "фронтлайн", у них этого не
    /// должно быть». Галио и Карма на миде метки танка и пила носят, но стоять
    /// во фронте и прикрывать стрелка — работа топа, леса и саппорта. Стрелок
    /// не начинает и не срывает заход; саппорт не чистит волны, не пробивает
    /// танков и тип урона команды не делает (его урон мал); лесник волн не
    /// чистит. Контроль и ранняя сила — с любой роли.
    /// </summary>
    private static readonly Dictionary<string, string[]> GapRoles = new()
    {
        ["needFront"]     = ["top", "jungle", "support"],
        ["needPeel"]      = ["top", "jungle", "support"],
        ["needDisengage"] = ["top", "jungle", "mid", "support"],
        ["needEngage"]    = ["top", "jungle", "mid", "support"],
        ["needCc"]        = ["top", "jungle", "mid", "adc", "support"],
        ["needAp"]        = ["top", "jungle", "mid", "adc"],
        ["needAd"]        = ["top", "jungle", "mid", "adc"],
        ["needAntiTank"]  = ["top", "jungle", "mid", "adc"],
        ["needWaveclear"] = ["top", "mid", "adc"],
        ["needEarly"]     = ["top", "jungle", "mid", "adc", "support"],
    };

    /// <summary>
    /// Дыры союзников без меня — по важности. Сначала ключевая часть стиля, если
    /// стиль явный (<see cref="ChampionTraits.DominantStyle"/>, тот же порог, что
    /// у подсветки стиля команды): фронт-ту-бэк без танка — фронт, пик/пок
    /// без отхода — дизэнгейдж, дайв без инициатора — инициация. Дальше — то,
    /// что гайды по составам считают обязательным в любом составе (с трёх
    /// союзников): танк, защита гиперкерри, жёсткий контроль хотя бы у двоих,
    /// оба типа урона, урон по танкам, если у врагов их двое, чистка волн,
    /// ранняя сила, если весь состав поздний. Вид не повторяется: фронт стиля
    /// и общий фронт — одна дыра. Только дыры, за которые отвечает моя роль
    /// (<see cref="GapRoles"/>): прочие этой роли не видны вовсе.
    /// <paramref name="myRole"/> = null — все роли и без порога союзников: так
    /// дыры полной пятёрки считают замер (StyleFeatures) и полоса состава в окне.
    /// </summary>
    internal static List<TeamGap> TeamGaps(IReadOnlyList<int> allies, IReadOnlyList<int> enemies, string? myRole)
    {
        var gaps = new List<TeamGap>();
        void Add(string kind, Func<int, bool> fills, string tip)
        {
            if (myRole is not null && !GapRoles[kind].Contains(myRole)) return;
            if (!gaps.Any(g => g.Kind == kind)) gaps.Add(new TeamGap(kind, fills, tip, GapWeight[kind]));
        }
        bool None(Func<int, bool> p) => !allies.Any(p);

        switch (ChampionTraits.DominantStyle(allies))
        {
            case ChampionTraits.Arch.FrontToBack when None(ChampionTraits.Frontline):
                Add("needFront", ChampionTraits.Frontline, Loc.T("reason.needFront")); break;
            case ChampionTraits.Arch.PickPoke when None(ChampionTraits.Disengager):
                Add("needDisengage", ChampionTraits.Disengager, Loc.T("reason.needDisengage")); break;
            case ChampionTraits.Arch.Dive when None(ChampionTraits.Initiator):
                Add("needEngage", ChampionTraits.Initiator, Loc.T("reason.needEngage")); break;
        }
        if (myRole is not null && allies.Count < GAP_ALLIES_MIN) return gaps;

        if (None(ChampionTraits.Frontline))
            Add("needFront", ChampionTraits.Frontline, Loc.T("reason.needFrontAny"));

        var carry = allies.FirstOrDefault(ChampionTraits.Hypercarry);
        if (carry != 0 && None(ChampionTraits.Peeler))
            Add("needPeel", ChampionTraits.Peeler, Loc.T("reason.needPeel", DataDragon.Name(carry)));

        if (allies.Count(ChampionTraits.HasHardCc) < 2)
            Add("needCc", ChampionTraits.HasHardCc, Loc.T("reason.needCc"));

        int ad = allies.Count(DataDragon.IsAdChampion), ap = allies.Count(DataDragon.IsApChampion);
        if (ad >= 3 && ap == 0) Add("needAp", DataDragon.IsApChampion, Loc.T("reason.needAp"));
        else if (ap >= 3 && ad == 0) Add("needAd", DataDragon.IsAdChampion, Loc.T("reason.needAd"));

        var tanks = enemies.Where(ChampionTraits.IsTanky).ToList();
        if (tanks.Count >= 2 && None(ChampionTraits.AntiTank))
            Add("needAntiTank", ChampionTraits.AntiTank,
                Loc.T("reason.needAntiTank", string.Join(", ", tanks.Select(DataDragon.Name))));

        if (None(ChampionTraits.Waveclear))
            Add("needWaveclear", ChampionTraits.Waveclear, Loc.T("reason.needWaveclear"));

        if (allies.All(a => ChampionTraits.EarlyPower(a) <= 1))
            Add("needEarly", a => ChampionTraits.EarlyPower(a) >= 3, Loc.T("reason.needEarly"));

        return gaps;
    }

    /// <summary>
    /// Надбавка в счёт за закрытые дыры: сумма их весов (не больше
    /// <c>NEED_BONUS_MAX</c>) × доля выбранных союзников — чем меньше пиков
    /// осталось, тем вернее, что дыру больше никто не закроет. В счёт идёт с
    /// <c>W_NEED</c>. Вес на глаз, до замера по исходам (как когда-то стиль).
    /// </summary>
    private static double NeedBonus(int champId, IReadOnlyList<TeamGap> gaps, int allies) =>
        Math.Min(NEED_BONUS_MAX, gaps.Where(g => g.Fills(champId)).Sum(g => g.Weight)) * allies / 4.0;

    /// <summary>
    /// Теги дыр на карточке — зелёные: дыры, которые этот пик закрывает (до
    /// двух, по важности; «контроль» рядом с «инициацией» не повторяем —
    /// заход и так с контролем). Value — доля выбранных союзников (0,25…1):
    /// ярче к последнему пику. Красных тегов «дыра так и останется» на карточке
    /// больше нет (владелец, 09.11, по предложению: карточки перегружены, а
    /// дыра — свойство команды): что у команды есть и чего нет, показывает
    /// полоса состава под колонкой команды (OverlayWindow.Comp.cs).
    /// </summary>
    private static void NeedTags(PickFacts f, List<PickTag> tags)
    {
        var urgency = f.AllyIds.Count / 4.0;
        var shown = new List<string>();
        foreach (var g in f.Gaps.Where(g => g.Fills(f.ChampId)))
        {
            if (shown.Count >= NEED_TAGS_MAX) break;
            if (g.Kind == "needCc" && shown.Contains("needEngage")) continue;
            shown.Add(g.Kind);
            tags.Add(new PickTag(g.Kind, 1, Loc.T($"pickTag.{g.Kind}"), g.Tip, urgency));
        }
    }

    /// <summary>
    /// Их слабость — зелёные иконки тем, кто её наказывает (владелец, 09.11:
    /// «дыры врага — наказать»). С трёх известных врагов: нет фронта — бьющим
    /// издалека и взрывом; их гиперкерри некому прикрыть — ныряющим; нечем
    /// сорвать заход — инициаторам; мало контроля — гиперкерри; урон один тип —
    /// танкам; нечем пробивать танков — танкам; все поздние — ранним. Только
    /// показ: в счёт свои дыры уже идут (W_NEED), а их дыры мерятся тем же
    /// замером с другим знаком.
    /// </summary>
    private static void PunishIcons(PickFacts f, List<PickIcon> plus)
    {
        var e = f.EnemyIds;
        if (e.Count < GAP_ALLIES_MIN) return;
        var id = f.ChampId;
        bool None(Func<int, bool> p) => !e.Any(p);
        void Add(string kind, string tip) => plus.Add(Icon(kind, 1, tip));

        if (None(ChampionTraits.Frontline) && (ChampionTraits.LongRange(id) || ChampionTraits.BurstThreat(id)))
            Add("punishNoFront", Loc.T("reason.punishNoFront"));
        var carry = e.FirstOrDefault(ChampionTraits.Hypercarry);
        if (carry != 0 && None(ChampionTraits.Peeler) && (ChampionTraits.Gapclose(id) >= 2 || ChampionTraits.BurstThreat(id)))
            Add("punishNoPeel", Loc.T("reason.punishNoPeel", DataDragon.Name(carry)));
        if (None(ChampionTraits.Disengager) && ChampionTraits.Initiator(id))
            Add("punishNoDisengage", Loc.T("reason.punishNoDisengage"));
        if (e.Count(ChampionTraits.HasHardCc) < 2 && ChampionTraits.Hypercarry(id))
            Add("punishNoCc", Loc.T("reason.punishNoCc"));
        int ad = e.Count(DataDragon.IsAdChampion), ap = e.Count(DataDragon.IsApChampion);
        if (ChampionTraits.Frontline(id) && ((ad >= 3 && ap == 0) || (ap >= 3 && ad == 0)))
            Add("punishOneDamage", Loc.T("reason.punishOneDamage", Loc.T(ad >= 3 ? "cat.physical" : "cat.magic")));
        if (ChampionTraits.Frontline(id) && None(ChampionTraits.AntiTank))
            Add("punishNoAntiTank", Loc.T("reason.punishNoAntiTank"));
        if (e.All(x => ChampionTraits.EarlyPower(x) <= 1) && ChampionTraits.EarlyPower(id) >= 3)
            Add("punishLate", Loc.T("reason.punishLate"));
    }

    /// <summary>
    /// Не тот темп (гайды: «ранних агрессоров вперемешку с гиперкерри» — частая
    /// ошибка драфта): команда явно ранняя — двое и больше сильны с первых минут
    /// (<see cref="ChampionTraits.EarlyPower"/> 3), поздних нет, — а кандидат
    /// раскрывается поздно (гиперкерри, 0). Только показ, в счёт не идёт.
    /// </summary>
    private static void TempoIcon(PickFacts f, List<PickIcon> minus)
    {
        var allies = f.AllyIds;
        if (allies.Count(a => ChampionTraits.EarlyPower(a) >= 3) < 2) return;
        if (allies.Any(a => ChampionTraits.EarlyPower(a) <= 1)) return;
        if (ChampionTraits.EarlyPower(f.ChampId) != 0) return;
        minus.Add(Icon("offTempo", -1, Loc.T("reason.offTempo")));
    }

    /// <summary>
    /// Иконки, которые тег дыры повторяет: «разбавляет урон» рядом с тегом
    /// «Магический/Физический урон» и «защита для стрелка» рядом с «Защитой
    /// керри» говорят то же самое, тег громче.
    /// </summary>
    private static void DropRepeats(List<PickTag> tags, List<PickIcon> plus, List<PickIcon> links)
    {
        bool Green(string kind) => tags.Any(t => t.Kind == kind && t.Sign > 0);
        if (Green("needAp") || Green("needAd")) plus.RemoveAll(i => i.Kind == "dmgBalance");
        if (Green("needPeel")) links.RemoveAll(i => i.Kind == "adcScale");
    }
}
