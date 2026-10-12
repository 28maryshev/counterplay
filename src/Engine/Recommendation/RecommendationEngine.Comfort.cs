using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — личная история игрока: комфорт, мейны, свежесть, защита от контры,
// личная дельта.
public sealed partial class RecommendationEngine
{
    // Очки мастерства игрока (championId → points) из LCU. Пусто = без учёта пула.
    public IReadOnlyDictionary<int, long> Mastery { get; set; } =
        new Dictionary<int, long>();

    // Чемпионы активного пула игрока (по текущей роли) — для «комфорта» пула.
    // Ставится в начале Recommend; пусто = пул не активен.
    private HashSet<int> _comfortPool = [];

    // «Комфорт» за чемпиона В ПУЛЕ: сигнал, что игрок им владеет, даже если на
    // ЭТОМ аккаунте нет наигранности (второй аккаунт). «Свой», но не одно-трик.
    // Замер на живой базе: между 2-м и 8-м местом подбора укладывается ~2.5
    // очка, поэтому прибавка 3.0 перебрасывала любого чемпиона пула через
    // полсписка. 1.2 — сдвиг на пару позиций: заметно при прочих равных, но не
    // перебивает контрпик.
    private const double POOL_COMFORT = 1.2;

    // ── Наигранность: что я играю СЕЙЧАС, а не что играл когда-то ────────────
    //
    // Жалоба игрока (1 октября): «я много играл Нилу, пока она была сильной; её
    // занерфили, а программа всё равно держит её в топ-4». Так и было: комфорт
    // целиком стоял на пожизненных очках мастерства Riot, а они НЕ УБЫВАЮТ. При
    // 150k очков это давало +5.2 к оценке навсегда — больше, чем весь разрыв
    // между 2-м и 8-м местом подбора (~2.5 очка по замеру PoolBias). Нерф двигает
    // базовый винрейт на 1.5–3 очка и перебить такое не мог в принципе.
    //
    // Теперь комфорт — сумма двух слагаемых, и главное из них свежее.

    /// Окно «играю сейчас». Месяц — как и у личного винрейта, и у защитных банов.
    public const int FreshDays = 30;

    /// Потолок прибавки за игры в этом окне.
    private const double RECENT_MAX = 3.0;

    /// Сколько игр за месяц дают половину потолка. Четыре: одна-две игры — это
    /// «попробовал», а к десяти кривая уже у потолка и дальше растить нечего.
    private const double RECENT_K = 4.0;

    /// Потолок прибавки за пожизненные очки мастерства. Было 8.0 — столько эта
    /// одна величина весить не должна: подбор превращался в «играй, что привык».
    private const double MASTERY_MAX = 3.0;

    /// Половина потолка мастерства, в очках Riot.
    private const double MASTERY_HALF = 80_000.0;

    /// Когда затухание мастерства доходит до дна. Четыре месяца: за месяц-другой
    /// перерыва чемпион из рук не уходит, а через сезон — уходит.
    private const double StaleDays = 120;

    /// По какой игре С КОНЦА считаем давность. Не по последней: ОДНА игра
    /// воскрешала затухание целиком, и чемпион, которого берут раз в месяц,
    /// держал полный вес мастерства вечно — почти тот же баг, от которого
    /// лечились. Замер на живом пуле владельца: Зилеан, одна игра за тридцать
    /// дней, свежесть 1.00. Третья с конца на разовый заход не ведётся.
    public const int RegularGames = 3;

    /// Ниже этой доли мастерство не падает. Не ноль: даже забытый чемпион
    /// вспоминается быстрее незнакомого.
    private const double STALE_FLOOR = 0.25;

    /// Сейчас считаем пик НАПАРНИКА, а не свой: личные факторы выключены.
    /// Поле, а не параметр, — чтобы не тащить его через весь скоринг; вызовы
    /// последовательные (оверлей однопоточный), и флаг снимается в finally.
    private bool _forMate;

    /// <summary>
    /// Наигранность напарника по чемпионам: его числа, приехавшие от него.
    /// Ставится снаружи; пусто — считаем его половину без личных факторов, как
    /// до обмена. Старый снимок лучше никакого: он берётся с диска сразу, а
    /// свежий подтягивается фоном (как база).
    /// </summary>
    public IReadOnlyDictionary<int, MateComfort>? MateComfortByChamp { get; set; }

    /// <summary>
    /// Сама формула наигранности — от ЧИСЕЛ, без источника.
    ///
    /// Числа бывают не только мои: наигранность напарника приезжает от него
    /// (<see cref="MateComfort"/>), а формула обязана остаться ОДНА — иначе при
    /// её правке у двоих в одном драфте окажутся разные комфорты.
    /// </summary>
    private static double Comfort(int recentGames, long masteryPts, double idleDays, bool inPool)
    {
        var recent = recentGames > 0
            ? RECENT_MAX * recentGames / (recentGames + RECENT_K)
            : 0.0;

        var mastery = masteryPts > 0 ? MASTERY_MAX * masteryPts / (masteryPts + MASTERY_HALF) : 0.0;
        mastery *= Freshness(idleDays);

        if (inPool) mastery = Math.Max(mastery, POOL_COMFORT);
        return recent + mastery;
    }

    /// Та же развязка для личного винрейта: процент к 50%, умноженный на
    /// доверие по числу игр (полное с PERSONAL_FULL_GAMES), источник чисел
    /// снаружи. Одна формула на мои игры и на игры напарника.
    private static double Personal(int games, int wins)
    {
        if (games <= 0) return 0.0;
        var trust = Math.Min(1.0, games / PERSONAL_FULL_GAMES);
        var delta = PERSONAL_SCALE * 100.0 * ((double)wins / games - 0.5) * trust * trust;
        return Math.Clamp(delta, -PERSONAL_CAP, PERSONAL_CAP);
    }

    /// <summary>
    /// Насколько это ТВОЙ чемпион прямо сейчас.
    ///
    /// Слагаемое первое — игры за последний месяц. Слагаемое второе — пожизненные
    /// очки мастерства, умноженные на свежесть: играл в этом месяце — полный вес,
    /// не брал четыре месяца — четверть. Свежесть считается по RegularGames-й
    /// игре с конца, иначе один заход возвращал бы полный вес целиком.
    ///
    /// Чемпион из активного пула получает флор POOL_COMFORT. С мастерством он НЕ
    /// складывается — берётся максимум: пул говорит ровно то же самое («этим я
    /// владею»), просто про второй аккаунт, где очков нет.
    /// </summary>
    private double ComfortDelta(int champId)
    {
        // Считаем за НАПАРНИКА: своей истории тут быть не может, она про мои
        // чемпионы. Берём ЕГО числа, если он ими поделился, — иначе ноль, как
        // было до обмена наигранностью.
        if (_forMate)
            return MateComfortByChamp is { } mc && mc.TryGetValue(champId, out var m)
                ? Comfort(m.Games, m.Mastery, m.IdleDays, inPool: false)
                : 0.0;

        var hist = MyHistory();
        var (recentGames, _) = hist.Recent(champId);
        var pts = Mastery.TryGetValue(champId, out var p) && p > 0 ? p : 0L;
        return Comfort(recentGames, pts, hist.DaysSinceNth(champId, RegularGames),
                       _comfortPool.Contains(champId));
    }

    /// Мой журнал за окно свежести: игры, победы, давность. ОДИН источник на
    /// наигранность, личный винрейт и защитные баны — окно у всех трёх месяц,
    /// а сам вызов кэширован, так что файл читается один раз на драфт.
    private static SessionTracker.PlayHistory MyHistory() =>
        SessionTracker.History(FreshDays,
            [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal]);

    // Мейн за месяц: не меньше MAIN_MIN_GAMES игр и не меньше MAIN_MIN_SHARE
    // всех моих игр за то же окно. Доля — чтобы мейн был и у того, кто играет
    // двадцать игр в месяц; минимум игр — чтобы три игры из десяти не делали
    // мейном. У владельца: Сона 32, Браум 19, Эш 18, Вел'Коз 17 (из ~130).
    private const int    MAIN_MIN_GAMES = 5;

    private const double MAIN_MIN_SHARE = 0.10;

    /// <summary>
    /// Мои мейны за последний месяц: чемпион → игр. Окно банов по ним
    /// помечает, что забанили именно моего чемпиона.
    /// </summary>
    public static IReadOnlyDictionary<int, int> MyMains()
    {
        var played = MyHistory().Played.Where(p => p.Games > 0).ToList();
        var total  = played.Sum(p => p.Games);
        return played.Where(p => p.Games >= MAIN_MIN_GAMES && p.Games >= MAIN_MIN_SHARE * total)
                     .ToDictionary(p => p.Id, p => p.Games);
    }

    // ── Против контры комфорт не советчик ───────────────────────────────────
    //
    // Замер на живом пуле владельца показал границу: его личная прибавка на
    // Соне (наигранность 4.52 + винрейт 3.42 = 7.94) при весе контры 2.0
    // перебивала 4 пп матчапа. Соню показывало вторым номером против Леоны при
    // её 22-м месте по чистой мете.
    //
    // Рассуждение, по которому это гасим. «Я играю на нём хорошо» намеряно по
    // ВСЕМ матчапам сразу. Против чемпиона, который этого конкретно обыгрывает,
    // личное преимущество переносится хуже: жёсткая контра ограничивает как раз
    // то, что умеет игрок. Поэтому прибавку ужимаем тем сильнее, чем хуже
    // прямой матчап.
    //
    // Это НЕ двойной счёт с wDirect: тот отвечает за среднего игрока, а здесь
    // ужимается личная надбавка сверх него.
    //
    // Величины выбранные, а не измеренные — проверить их по данным нельзя, в
    // матчах Riot нет отметки «насколько игрок хорош на чемпионе». Выбраны по
    // замеру на пуле владельца: граница вылета из видимой шестёрки съезжает
    // с −3.2 пп примерно до −2 пп, а мягкие матчапы (до −1 пп) не трогаются.

    /// Матчап, до которого комфорт работает в полную силу.
    private const double GUARD_START = -1.0;

    /// ...и при котором он ужат до дна.
    private const double GUARD_FULL  = -4.0;

    /// Дно: совсем не выключаем. Даже в плохом матчапе знакомый чемпион
    /// остаётся знакомым — руки помнят, незнакомый хуже.
    private const double GUARD_FLOOR = 0.3;

    /// Во сколько раз ужать личную прибавку при таком прямом матчапе (в пп).
    private static double CounterGuard(double vsOpponent)
    {
        if (vsOpponent >= GUARD_START) return 1.0;
        if (vsOpponent <= GUARD_FULL)  return GUARD_FLOOR;
        return 1.0 - (1.0 - GUARD_FLOOR) * (GUARD_START - vsOpponent) / (GUARD_START - GUARD_FULL);
    }

    /// Доля мастерства, которая ещё в силе: 1.0 первый месяц, дальше вниз по
    /// прямой до STALE_FLOOR к StaleDays.
    private static double Freshness(double daysSince)
    {
        if (daysSince <= FreshDays) return 1.0;
        if (daysSince >= StaleDays) return STALE_FLOOR;
        return 1.0 - (1.0 - STALE_FLOOR) * (daysSince - FreshDays) / (StaleDays - FreshDays);
    }

    // Драфт-фичи: нейтральный пик (при неопределённости), трифекта композиций
    // и анти-стиль (инструменты против доминирующего архетипа врага).
    // Возвращает суммарный взвешенный бонус к score и причины для UI.
    // StyleScore — отдельно вклад «против стиля врага» (трифекта+анти-стиль), для показа в карточке.
    private static (double Bonus, double StyleScore, List<string> Reasons) DraftFit(
        int champId, ChampionTraits.Arch? enemyDom, double uncertainty)
    {
        double bonus = 0, styleScore = 0;
        var reasons = new List<string>();

        // 5. Нейтральный пик — безопасен при неизвестном составе.
        if (ChampionTraits.IsNeutral(champId) && uncertainty > 0.1)
        {
            bonus += W_NEUTRAL * uncertainty;
            if (uncertainty >= 0.5)
                reasons.Add(Loc.T("reason.neutral"));
        }

        if (enemyDom is { } dom)
        {
            var (f2b, dive, pick) = ChampionTraits.Archetype(champId);

            // 2. Трифекта: что нужно взять, чтобы побить доминанту врага.
            //    dive ← frontToBack, pickPoke ← dive, frontToBack ← pickPoke.
            var want = dom switch
            {
                ChampionTraits.Arch.Dive       => f2b,
                ChampionTraits.Arch.PickPoke   => dive,
                _                              => pick, // FrontToBack ← pickPoke
            };

            // 3. Анти-стиль: конкретные инструменты против стиля врага.
            double style = dom switch
            {
                ChampionTraits.Arch.PickPoke =>
                    ChampionTraits.Gapclose(champId) + ChampionTraits.Engage(champId),
                ChampionTraits.Arch.Dive =>
                    ChampionTraits.Peel(champId) + ChampionTraits.Disengage(champId),
                _ /* FrontToBack */ =>
                    (ChampionTraits.LongRange(champId) ? 2 : 0) + ChampionTraits.Burst(champId),
            };

            styleScore = W_TRIFECTA * want + W_STYLE * style;
            bonus += styleScore;

            if (style >= 3)
                reasons.Add(Loc.T(dom switch
                {
                    ChampionTraits.Arch.PickPoke   => "reason.antiPickPoke",
                    ChampionTraits.Arch.Dive       => "reason.antiDive",
                    _                              => "reason.antiFront",
                }));
        }

        return (bonus, styleScore, reasons);
    }

    /// Личный винрейт игрока на чемпионе как дельта к 50%: «у меня на нём идёт».
    /// Считается по своим играм за FreshDays (соло+флекс+нормалы, ARAM не берём),
    /// с доверием по числу игр — вес процента растёт с играми до тридцати, и
    /// 57% на тридцати весят больше, чем 66% на девяти.
    ///
    /// Источник тот же, что у наигранности (<see cref="MyHistory"/>): это один и
    /// тот же журнал за одно и то же окно, и читать его двумя путями значило бы
    /// однажды разъехаться на ровном месте.
    public double PersonalDelta(int champId)
    {
        // См. ComfortDelta: за напарника идут ЕГО победы, не мои.
        if (_forMate)
            return MateComfortByChamp is { } mc && mc.TryGetValue(champId, out var m)
                ? Personal(m.Games, m.Wins)
                : 0.0;

        var (g, w) = MyHistory().Recent(champId);
        return Personal(g, w);
    }
}
