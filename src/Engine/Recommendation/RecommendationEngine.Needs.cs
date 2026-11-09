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
    private const double NEED_RED_VALUE = 0.75;   // красный — без свечения: он у многих сразу
    private const double NEED_BONUS_MAX = 1.5;    // сумма весов закрытых дыр, потолок

    // Важность дыр (доля W_NEED). Тип урона — 0: перекос уже в счёте
    // (ItemValue.DamageBalance, W_DMGBAL), второй раз его не считаем.
    private const double GAP_STYLE  = 1.0;   // ключевая часть стиля
    private const double GAP_FRONT  = 1.0;   // фронт в любом составе
    private const double GAP_PEEL   = 0.8;
    private const double GAP_CC     = 0.6;
    private const double GAP_TANK   = 0.8;
    private const double GAP_WAVE   = 0.5;
    private const double GAP_EARLY  = 0.5;

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
    /// </summary>
    private static List<TeamGap> TeamGaps(IReadOnlyList<int> allies, IReadOnlyList<int> enemies, string myRole)
    {
        var gaps = new List<TeamGap>();
        void Add(string kind, Func<int, bool> fills, string tip, double weight)
        {
            if (!GapRoles[kind].Contains(myRole) || gaps.Any(g => g.Kind == kind)) return;
            gaps.Add(new TeamGap(kind, fills, tip, weight));
        }
        bool None(Func<int, bool> p) => !allies.Any(p);

        switch (ChampionTraits.DominantStyle(allies))
        {
            case ChampionTraits.Arch.FrontToBack when None(ChampionTraits.Frontline):
                Add("needFront", ChampionTraits.Frontline, Loc.T("reason.needFront"), GAP_STYLE); break;
            case ChampionTraits.Arch.PickPoke when None(ChampionTraits.Disengager):
                Add("needDisengage", ChampionTraits.Disengager, Loc.T("reason.needDisengage"), GAP_STYLE); break;
            case ChampionTraits.Arch.Dive when None(ChampionTraits.Initiator):
                Add("needEngage", ChampionTraits.Initiator, Loc.T("reason.needEngage"), GAP_STYLE); break;
        }
        if (allies.Count < GAP_ALLIES_MIN) return gaps;

        if (None(ChampionTraits.Frontline))
            Add("needFront", ChampionTraits.Frontline, Loc.T("reason.needFrontAny"), GAP_FRONT);

        var carry = allies.FirstOrDefault(ChampionTraits.Hypercarry);
        if (carry != 0 && None(ChampionTraits.Peeler))
            Add("needPeel", ChampionTraits.Peeler, Loc.T("reason.needPeel", DataDragon.Name(carry)), GAP_PEEL);

        if (allies.Count(ChampionTraits.HasHardCc) < 2)
            Add("needCc", ChampionTraits.HasHardCc, Loc.T("reason.needCc"), GAP_CC);

        int ad = allies.Count(DataDragon.IsAdChampion), ap = allies.Count(DataDragon.IsApChampion);
        if (ad >= 3 && ap == 0) Add("needAp", DataDragon.IsApChampion, Loc.T("reason.needAp"), 0);
        else if (ap >= 3 && ad == 0) Add("needAd", DataDragon.IsAdChampion, Loc.T("reason.needAd"), 0);

        var tanks = enemies.Where(ChampionTraits.IsTanky).ToList();
        if (tanks.Count >= 2 && None(ChampionTraits.AntiTank))
            Add("needAntiTank", ChampionTraits.AntiTank,
                Loc.T("reason.needAntiTank", string.Join(", ", tanks.Select(DataDragon.Name))), GAP_TANK);

        if (None(ChampionTraits.Waveclear))
            Add("needWaveclear", ChampionTraits.Waveclear, Loc.T("reason.needWaveclear"), GAP_WAVE);

        if (allies.All(a => ChampionTraits.EarlyPower(a) <= 1))
            Add("needEarly", a => ChampionTraits.EarlyPower(a) >= 3, Loc.T("reason.needEarly"), GAP_EARLY);

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
    /// Дыра, которая останется открытой: ты выбираешь последним (четыре союзника
    /// уже есть), а закрыть её может кто-то, кого на твою роль ещё можно взять
    /// (<paramref name="rolePool"/> — кандидаты роли, не только показанные: свой
    /// пик и пул считаются через only по одному). Берётся самая важная такая; у
    /// тех, кто её не закрывает, на карточке — красный тег того же вида. Роли
    /// закрыть нечем — молчим: выбирать не из чего, а красное у всех подряд
    /// только шумит.
    /// </summary>
    private static TeamGap? OpenGap(IEnumerable<int> rolePool, IReadOnlySet<int> taken, IReadOnlyList<TeamGap> gaps)
    {
        var free = rolePool.Where(id => !taken.Contains(id)).ToList();
        return gaps.FirstOrDefault(g => free.Any(g.Fills));
    }

    /// <summary>
    /// Теги дыр на карточке: зелёные — дыры, которые этот пик закрывает (до
    /// двух, по важности; «контроль» рядом с «инициацией» не повторяем —
    /// заход и так с контролем), красный — открытая дыра последнего пика, если
    /// он её не закрывает. Value — доля выбранных союзников (0,25…1): ярче к
    /// последнему пику; у красного — постоянная, без свечения.
    /// </summary>
    private static void NeedTags(PickFacts f, TeamGap? open, List<PickTag> tags)
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
        if (open is { } o && !o.Fills(f.ChampId))
        {
            var label = Loc.T($"pickTag.{o.Kind}");
            tags.Add(new PickTag(o.Kind, -1, label, Loc.T("reason.needOpen", label), NEED_RED_VALUE));
        }
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
