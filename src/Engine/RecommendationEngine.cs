using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record BanRec(int ChampionId, double Score, string[] Reasons);

public sealed record Recommendation(
    int    ChampionId,
    double Score,
    double BaseDelta,     // %пп vs 50%
    double DirectDelta,   // %пп матчап vs прямой оппонент
    double OtherDelta,    // %пп средний vs прочие враги
    double SynergyDelta,  // %пп средняя синергия с союзниками
    double ComfortDelta,  // бонус за «комфорт»: часто наигранный чемпион игрока
    double StyleDelta,    // вклад «против стиля врага» (трифекта + анти-стиль)
    string[] Reasons,
    int    Rank   = 0,    // место в полном списке (1 = лучший)
    bool   IsMyPick = false); // это мой уже выбранный/наведённый чемпион

/// <summary>
/// Наигранность напарника на одном чемпионе — его числа, приехавшие от него.
///
/// Возим ИСХОДНЫЕ величины, а не готовый комфорт: формула менялась (2 октября
/// мастерство стало затухать с простоем), и при следующей правке у двоих в
/// одном драфте получились бы числа от разных формул. Сырые величины считает
/// принимающая сторона, формула остаётся одна.
/// </summary>
public sealed record MateComfort(int Games, int Wins, long Mastery, double IdleDays);

public sealed class RecommendationEngine : IDisposable
{
    // Знак причины — невидимым префиксом в самой строке: так он доезжает до
    // интерфейса через все слои (Recommendation, BanRec, тесты), не ломая их
    // сигнатуры. Оверлей по нему красит маркер строки: зелёный — довод «за»,
    // красный — «против», серый — нейтральный факт.
    public const char SIGN_GOOD = '';
    public const char SIGN_BAD  = '';
    /// <summary>
    /// Довод, который нельзя выбрасывать как «общий». Такова связка с конкретным
    /// союзником: «Ясуо ультует в твой подброс» одинакова у всех подкидывающих
    /// кандидатов — и отсев общих строк убирал её у ВСЕХ разом, хотя ради неё
    /// пик и берут. Знак читается как «за», но помечен отдельно.
    /// </summary>
    public const char SIGN_KEY  = '';
    private static string Good(string s) => SIGN_GOOD + s;
    private static string Bad(string s)  => SIGN_BAD + s;
    private static string Key(string s)  => SIGN_KEY + s;

    // Лаплас-сглаживание: при малом числе игр тянем к 50%.
    private const double K      = 50.0; // для базового WR (данных много)
    private const double K_PAIR = 20.0; // для парных таблиц (синергия/матчап) — данных мало
    private const double PRIOR  = 0.5;
    private const double CONF_GAMES = 60.0;  // темпер синергии по объёму выборки
    private const double BASE_CONF  = 250.0; // темпер базового WR по объёму выборки
    private const double MATCHUP_CONF = 50.0; // темпер парных матчапов: мало игр в паре → дельте нет доверия

    // Приор по всем дивизионам для разреженных пар. В своём бакете у половины пар
    // меньше 15 взвешенных игр, и темпер выше режет их дельту до ~22%. База везёт
    // сводку той же пары по всем дивизионам (tier_bucket='all', patch='all') —
    // подмешиваем её как POOL_PRIOR «псевдо-игр» с винрейтом сводки.
    //
    // Почему приором, а не сложением игр: у соседних эло та же пара играется иначе,
    // и их объём не должен спорить со своим бакетом. Пара с 500 своими играми
    // сдвинется на сотые доли процента, пара с нулём получит осмысленную оценку
    // вместо молчания.
    private const double POOL_PRIOR = 25.0;  // сколько «псевдо-игр» даёт сводка
    private const double POOL_MIN   = 30.0;  // ниже этого сводка шумит сама

    // Взвешивание патчей по свежести: текущий патч — полный вес, предыдущие затухают.
    // Все агрегаты считают SUM(games*вес): мета следует за актуальным патчем, не
    // теряя объёма старых данных (эффективная выборка ≈76% от плоской суммы).
    // Вес патча по свежести. Третий слот остаётся на случай, если патчей в базе
    // меньше и @p3 совпадает с @p2 (см. Create) — тогда вес просто не сработает.
    private const string PW = "CASE patch WHEN @p1 THEN 1.0 WHEN @p2 THEN 0.6 ELSE 0.3 END";

    // Вес прямой контры зависит от роли (по исследованиям влияния контрпиков):
    // топ изолирован — контра решает больше всего; джангл — сильное влияние;
    // мид — короткие трейды/роумы смягчают матчап; бот — это 2v2, личная контра
    // АДК значит меньше (добирается фактором botlane); саппорт — среднее.
    private static double DirectWeight(string role) => role switch
    {
        "top"     => 2.5,
        "jungle"  => 2.2,
        "mid"     => 2.0,
        "support" => 2.0,
        "adc"     => 1.8,
        _         => 2.2,
    };

    private const double W_BASE    = 1.0;
    // «Прочие враги» через same-role таблицу: контекст кривой (матчап «как если
    // бы он был моим лейн-оппонентом»), покрытие ~22% — держим вес низким.
    // Основной канал для прочих врагов — кросс-ролевые матчапы (W_CROSS).
    private const double W_OTHER   = 0.6;
    private const double W_SYNERGY = 1.2;
    // Напарник по дуо-пулу — не случайный союзник. Таблицы синергии собраны по
    // НЕсогласованному соло-кью и занижают связку, которую играют осознанно: пара
    // созванивается, размены и заходы у неё получаются чаще. Поэтому вклад
    // напарника в синергию считаем с множителем.
    //
    // 1.5 — величина выбранная, а не измеренная: проверить её по данным нельзя,
    // в матчах Riot нет отметки, кто был в пати. Выбрана по замеру (tests/PoolBias):
    // на наигранной связке хвосты прибавки ±2.6 очка — около четырёх шагов между
    // соседями в топ-10, и общий лидер меняется в 18% драфтов. На 2.0 и выше —
    // 39–56%: синергия начинает перебивать контрпик, а этого мы избегаем и в
    // W_SYN_MAX. Выше 1.5 не поднимать — решение владельца, и его держит сторож
    // в PoolBias, читающий эту строку. Надбавка ДВУСТОРОННЯЯ: плохую связку она
    // так же уводит вниз, и это половина её пользы.
    private const double DUO_MATE_MULT = 1.5;

    // Насколько громко эта надбавка звучит — по тому, сколько вы РЕАЛЬНО играете
    // вдвоём за окно свежести. Случайный сопартиец из нормала не должен гнуть
    // совет так же, как напарник, с которым за месяц сыграно двадцать игр.
    //
    // Считаем по играм с ЧЕЛОВЕКОМ, а не по паре чемпионов. Замер на живой
    // истории: из 251 записи связок у 250 ровно ОДНА игра, с тремя играми нет ни
    // одной записи. Порог на паре чемпионов не сработал бы никогда, а винрейт
    // такой пары — это 0% или 100%, то есть шум.
    //
    // Половина веса набирается к шести играм: 1 → 0.14, 3 → 0.33, 6 → 0.50,
    // 20 → 0.77. Сглаживание, а не обрыв, — как у наигранности (RECENT_K).
    private const double MATE_CONF = 6.0;
    private const double W_POOL     = 1.0; // вес «комфорта» (наигранность чемпиона)
    // Личный винрейт игрока на чемпионе за последний месяц (соло+флекс+нормалы,
    // ARAM не в счёт). Был 0.5 — намеренно ниже мета-факторов. Поднят до 1.0
    // вместе с урезанием пожизненного мастерства: «на нём у меня СЕЙЧАС идёт» —
    // сигнал более свежий и более честный, чем «я когда-то его много играл».
    private const double W_PERSONAL = 1.0;
    // С какого числа игр проценту на чемпионе верим полностью. Окно — месяц, и
    // за месяц на одном чемпионе набирается около тридцати игр: это и есть
    // «доказанный» результат. До тридцати доверие растёт как КВАДРАТ доли:
    // 10 игр — 1/9 веса, 15 — четверть, 20 — 0.44, 30 и дальше — целиком.
    //
    // Квадрат, а не прямая, нарочно. На прямой серия 6–0 (+50 пп при пятой
    // части доверия) весила бы столько же, сколько 60% на тридцати играх. Владелец
    // хочет, чтобы объём решал: 56% на сотне лучше, чем 66% на девяти, и то же в
    // масштабе месяца — 57% на 30 играх (+2.33) против 66% на 9 (+0.52), серии
    // 6–0 / 8–2 / 9–1 (+0.70 / +1.17 / +1.56) против 60% на 30 (+3.50).
    //
    // История формулы. Сглаживание на 12 игр × g/(g+15) упиралось в потолок уже
    // на сериях: 6–0, 8–2 и 56% на сотне получали одинаковые +4. Сглаживание на
    // 100 игр развело их, но было рассчитано на сотню игр, которой за месяц не
    // бывает: на тридцати играх 60% давали всего +2.3, а 9–1 — больше (+3.6).
    private const double PERSONAL_FULL_GAMES = 30.0;
    // Сколько очков даёт один пункт процента при полном доверии: 60% на 30
    // играх — +3.5, 57% — +2.3, 53% — +1.2.
    private const double PERSONAL_SCALE = 0.35;
    // Потолок личной дельты. Без него 70% на 30 играх давали бы +7 — больше, чем
    // прямой контрпик, и подбор превращался бы в «играй что привык». При весе 1.0
    // потолок 4.0 даёт максимум +4 очка — вровень с сильным контрпиком, но не
    // выше: личный винрейт подсказывает выбор, а не отменяет матчап. Достаётся
    // он только доказанному: от ~61.5% на тридцати играх.
    private const double PERSONAL_CAP = 4.0;
    private const double W_NEUTRAL  = 1.0; // нейтральный пик при неопределённости
    private const double W_TRIFECTA = 0.8; //архетип-контра (камень-ножницы-бумага)
    private const double W_STYLE    = 0.6; // анти-стиль: инструменты против компы врага
    private const double W_VULN     = 2.0; // штраф за стак одной уязвимости в команде
    private const double W_EXPLOIT  = 1.0; // бонус за наказание вынужденного предмета врага
    private const double W_STRUCT   = 1.6; // структурная синергия с ключевым тиммейтом (jg↔линия, адк↔сапп)
    private const double W_DMGBAL   = 1.2; // баланс типа урона (AD/AP): не стакать один тип

    // ARAM (врагов нет; максимизируем сочетание команды).
    private const double W_ARAM_SYN  = 1.8; // синергия с союзниками — главное
    private const double W_ARAM_DMG  = 2.0; // смешанный урон крайне важен
    private const double W_ARAM_GAP  = 1.0; // закрытие «дыр» композиции (фронт/саст/поук)
    private const double W_ARAM_BASE = 1.0; // сила чемпиона (WR без роли)
    private const double W_BOTLANE      = 1.5; // контрпик против вражеского дуо на боте (2v2)
    private const double W_BOTLANE_BOTH = 1.8; // когда виден весь вражеский бот (адк+сапп)
    // Кросс-ролевые матчапы против ПРОЧИХ врагов (не лейн-оппонента и не дуо):
    // замер по базе — чистый сигнал ~1.8 пп, сильнее большинства синергий
    // (~1.5 пп), поэтому вес на уровне синергии. collect.py пишет все пары
    // ролей; пока пары нет в базе — фактор по ней молчит (0) с фолбэком на
    // same-role таблицу (W_OTHER).
    private const double W_CROSS = 1.2;

    /// За сколько дней считаем «чем игрок играет сейчас» для защитных банов —
    /// то же окно, что у наигранности и личного винрейта (см. FreshDays), и
    /// по той же причине: короче — и после недельного перерыва список опустеет,
    /// длиннее — и туда снова полезут давно брошенные чемпионы. Окно одно на
    /// всех, поэтому отдельной константы у банов больше нет.
    /// Сколько мейнов защищаем. Больше пяти — и бан начинает защищать случайное.
    private const int MainsTake = 5;
    // Порог включения бот-матчапов: сумма игр в botlane_matchup. ~20k записей —
    // это примерно столько же матчей с собранной бот-статистикой (по ~1 на пару).
    private const double BOTLANE_MIN_GAMES = 20000;

    // Веса рекомендации банов.
    private const double W_BAN_META    = 1.0; // сила чемпиона в патче (WR)
    private const double W_BAN_POP     = 0.4; // популярность (часто пикается) — раньше давила всё
    private const double W_BAN_COUNTER = 2.0; // насколько контрит твой пул
    private const double W_BAN_MYPICK  = 3.2; // контрит мой наведённый/взятый пик (макс. приоритет)
    private const double W_BAN_ALLY    = 2.6; // контрит наведённый/взятый пик союзника (защита команды)
    private const double W_BAN_HOVER   = 1.8; // контрит чемпиона из ИСТОРИИ ховеров (показанный пул)
    // Слоты пятёрки банов: моей линии — не меньше трёх (и они первыми), защите
    // союзников — не больше двух. Бан в соло-очереди у каждого свой.
    private const int LANE_SLOTS = 3;
    private const int TEAM_SLOTS = 2;
    // Мейны для банов: журнал за месяц (вес — игры) и активный пул на роль из
    // профиля. Чемпион пула без игр весит как три игры; берём восемь самых весомых.
    private const double POOL_MAIN_GAMES = 3;
    private const int MAINS_FOR_BANS = 8;
    // Бонус за ширину: кандидат контрит 2+ показанных чемпионов команды. Растёт
    // нелинейно — бан, который бьёт троих, ценнее полутора банов против одного:
    // перепикнуть одного легко, а сразу нескольких — нет.
    private const double W_BAN_BREADTH = 2.2;
    // «Угроза для всей команды»: чемпион, который стабильно обыгрывает НАШИХ
    // вообще — не обязательно будучи топ-контрой кого-то одного. Такой бан
    // закрывает проблему всей команде, поэтому вес заметный.
    private const double W_BAN_TEAM = 2.4;

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

    // Минимум игр на роли суммарно по всем агрегируемым патчам. 150 отсекает
    // «мем-роли» (Нидали-саппорт с 139 играми = ~0.1% пикрейта) из кандидатов.
    private const int MIN_GAMES = 150;
    // Плюс минимальная ДОЛЯ игр роли: 0.5% отсекает редкие офф-мета пики
    // (Амуму/Фиддл-саппорт с 0.3%), даже когда абсолютных игр набралось; порог
    // сам масштабируется с размером базы.
    private const double MIN_ROLE_SHARE = 0.005;

    // Боковые подсказки (контры у врагов / синергия у союзников) ранжируем по
    // нижней границе Уилсона и показываем только при достаточной выборке. Иначе
    // всплывал шум: пара на 10–15 играх со случайным перевесом выдавалась за
    // «контру» (напр. Сион «контрил» Кейл на 16 играх, хотя реально проигрывает).
    private const int    HINT_MIN_GAMES = 30;   // минимум совместных игр на пару (контры)
    private const int    HINT_FILL_GAMES = 10;  // при доборе до трёх порог ниже
    private const double HINT_EVEN_WR   = 0.49; // ровная пара: годится третьей, проигрышная — нет
    private const int    HINT_MIN_SYN   = 12;   // ниже порог для синергии — чтобы заполнять 3
    private const double HINT_MIN_EDGE  = 0.50; // контру показываем, только если LB Уилсона > 50%
    private const double WILSON_Z       = 1.28; // ~80% односторонняя уверенность

    // Количество патчей для агрегации (берём последние N).
    // Два патча, а не три. Патчи выходят раз в ~2 недели, и третий по счёту —
    // это данные полуторамесячной давности: мета за это время успевает смениться,
    // а объёмом (миллионы игр) они перевешивали свежие. Замер по базе: отказ от
    // третьего патча срезает покрытие матчапов лишь на ~16 %, зато решение
    // перестаёт опираться на позапрошлую мету.
    private const int PATCH_WINDOW = 2;

    private readonly SqliteConnection _db;
    private readonly string _p1, _p2, _p3; // последние 3 патча (могут совпадать если патчей меньше)
    private readonly bool _botlaneReady;   // достаточно ли бот-данных, чтобы их учитывать

    public string TierBucket  { get; }
    public string PatchDisplay { get; } // "16.12, 16.11, 16.10" для вывода

    private RecommendationEngine(SqliteConnection db, string tierBucket, string[] patches)
    {
        _db          = db;
        TierBucket   = tierBucket;
        PatchDisplay = string.Join(", ", patches);
        // Заполняем до 3 слотов: если патчей меньше — дублируем последний (IN без эффекта)
        _p1 = patches.Length > 0 ? patches[0] : "0.0";
        _p2 = patches.Length > 1 ? patches[1] : _p1;
        _p3 = patches.Length > 2 ? patches[2] : _p2;

        // Бот-матчапы (#4) включаем только когда накоплено достаточно бот-данных.
        // Считаем по объёму записей botlane_matchup, а НЕ по общему числу матчей:
        // старые матчи бот-статистику не содержат, их учитывать нельзя.
        _botlaneReady = BotlaneTotalGames(db) >= BOTLANE_MIN_GAMES;
    }

    // Сумма игр в botlane_matchup (0, если таблицы нет — старая база).
    private static double BotlaneTotalGames(SqliteConnection db)
    {
        try
        {
            var cmd = db.CreateCommand();
            // Сводные строки пропускаем: это та же статистика второй раз.
            cmd.CommandText = "SELECT COALESCE(SUM(games),0) FROM botlane_matchup WHERE patch <> 'all'";
            var r = cmd.ExecuteScalar();
            return r is null or DBNull ? 0 : Convert.ToDouble(r);
        }
        catch { return 0; }
    }

    // Порядок фолбэка: если в базе нет данных для нужного бакета, берём ближайший.
    private static readonly string[] BucketFallback = ["silver", "gold", "emerald", "master"];

    public static RecommendationEngine Create(string dbPath, string tierBucket)
    {
        // Не используем Mode=ReadOnly — WAL-режим требует доступа к -shm файлу даже для чтения.
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();

        // Берём последние PATCH_WINDOW патчей (корректная версионная сортировка).
        var patchCmd = db.CreateCommand();
        patchCmd.CommandText = @"
            SELECT DISTINCT patch FROM base_wr
            ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
                     CAST(SUBSTR(patch, INSTR(patch, '.') + 1)     AS INTEGER) DESC
            LIMIT @n";
        // +1: берём с запасом, чтобы после возможного удержания новейшего осталось PATCH_WINDOW.
        patchCmd.Parameters.AddWithValue("@n", PATCH_WINDOW + 1);
        var patches = new List<string>();
        using (var rd = patchCmd.ExecuteReader())
            while (rd.Read()) patches.Add(rd.GetString(0));

        // Удержание патча: новейший патч не берём, пока он не набрал данных — иначе
        // первые дни нового патча рекомендации шумят. Держим предыдущий (полный)
        // патч, пока новый не покроет >= HOLD_FRACTION пар. Синхронно с
        // pipeline/freshness.py и ботом (bot/lib/freshness.js).
        // Бакет, в котором можно мерить готовность патча. Побакетная база содержит
        // ТОЛЬКО свой бакет, а проверка раньше всегда смотрела в «emerald» — и у
        // всех, кроме изумруда, читала нули. Ноль у ПРОШЛОГО патча означает «делить
        // не на что», то есть «новый готов», и удержание молча выключалось: движок
        // брал только что вышедший патч, где данных ещё нет. У изумруда всё
        // работало, поэтому беда всплыла, когда человек перешёл в золото.
        var holdBucket = BucketWithData(db, tierBucket);
        if (patches.Count >= 2 && !PatchReady(db, patches[0], patches[1], holdBucket))
            patches.RemoveAt(0);
        if (patches.Count > PATCH_WINDOW) patches = patches.Take(PATCH_WINDOW).ToList();
        if (patches.Count == 0) patches.Add("0.0");

        // Проверяем бакет по последнему патчу; если нет данных — ищем ближайший.
        var effectiveBucket = tierBucket;
        var checkCmd = db.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM base_wr WHERE tier_bucket=@t AND patch=@p";
        checkCmd.Parameters.AddWithValue("@t", tierBucket);
        checkCmd.Parameters.AddWithValue("@p", patches[0]);
        var count = (long)(checkCmd.ExecuteScalar() ?? 0L);

        if (count == 0)
        {
            foreach (var b in BucketFallback)
            {
                checkCmd.Parameters["@t"].Value = b;
                var n = (long)(checkCmd.ExecuteScalar() ?? 0L);
                if (n > 0) { effectiveBucket = b; break; }
            }
            Console.WriteLine($"  [предупреждение] Нет данных для бакета '{tierBucket}' — использую '{effectiveBucket}'.");
        }

        var engine = new RecommendationEngine(db, effectiveBucket, [.. patches]);
        Log.Write($"движок: бакет {effectiveBucket}, патчи {engine.PatchDisplay}, " +
                  $"сводка по дивизионам {(engine.HasPool ? "есть" : "нет")}, " +
                  $"готовность мерили по «{holdBucket}»");
        return engine;
    }

    // ── Политика удержания патча (синхронно с pipeline/freshness.py, bot/lib/freshness.js) ──
    private const int HOLD_MIN_GAMES = 150;      // игр на «чемпион+роль», чтобы считать пару набранной
    private const double HOLD_FRACTION = 0.7;    // доля пар прошлого патча, покрытых новым → «готов»
    private const string HOLD_BUCKET = "emerald"; // основной бакет для оценки готовности

    // Сколько «чемпион+роль» в заданном бакете набрали >= HOLD_MIN_GAMES игр на патче.
    private static int PatchCoverage(SqliteConnection db, string patch, string bucket)
    {
        var c = db.CreateCommand();
        c.CommandText = @"SELECT COUNT(*) FROM (
            SELECT champion_id, role FROM base_wr
            WHERE patch=@p AND tier_bucket=@b
            GROUP BY champion_id, role HAVING SUM(games) >= @min)";
        c.Parameters.AddWithValue("@p", patch);
        c.Parameters.AddWithValue("@b", bucket);
        c.Parameters.AddWithValue("@min", HOLD_MIN_GAMES);
        return (int)(long)(c.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Бакет, по которому МОЖНО судить о готовности патча в этом файле. Сперва
    /// свой, затем любой из запасных, затем основной. Общая база содержит все
    /// бакеты, побакетная — один: мерить надо тот, который есть.
    /// </summary>
    private static string BucketWithData(SqliteConnection db, string wanted)
    {
        var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM base_wr WHERE tier_bucket=@b LIMIT 1";
        c.Parameters.AddWithValue("@b", wanted);
        foreach (var b in new[] { wanted }.Concat(BucketFallback).Append(HOLD_BUCKET))
        {
            c.Parameters["@b"].Value = b;
            if ((long)(c.ExecuteScalar() ?? 0L) > 0) return b;
        }
        return HOLD_BUCKET;
    }

    // Готов ли новейший патч: покрывает ли >= HOLD_FRACTION пар предыдущего патча.
    private static bool PatchReady(SqliteConnection db, string newest, string prev, string bucket)
    {
        int cn = PatchCoverage(db, newest, bucket), cp = PatchCoverage(db, prev, bucket);
        return cp <= 0 || (double)cn / cp >= HOLD_FRACTION;
    }

    // Ищет data.db рядом с exe, потом в pipeline/.
    // Пропускает пустые/неинициализированные файлы (без таблицы base_wr) —
    // иначе пустой data.db в рабочей папке перекрывал бы настоящую базу.
    public static string? FindDb()
    {
        // Скачанная база в профиле пользователя (её ведёт DataDb.EnsureAsync) —
        // тем же путём, что у DataDb: у базы свой шов, у данных игрока свой.
        var downloaded = DataDb.LocalPath;

        // По умолчанию — скачанная база (как у всех). Dev-базы рядом с проектом
        // берём в приоритет ТОЛЬКО в dev-режиме (COUNTERPLAY_DEV_DB=1), иначе они
        // идут в хвост — чтобы у разработчика не «побеждала» устаревшая локальная.
        var order = DataDb.DevDbEnabled
            ? DataDb.DevCandidates.Append(downloaded)
            : new[] { downloaded }.Concat(DataDb.DevCandidates);
        return order.FirstOrDefault(p => File.Exists(p) && HasData(p));
    }

    // Валиден ли файл БД: ненулевой размер и есть таблица base_wr с данными.
    public static bool HasData(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return false;
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='base_wr')";
            return (long)(cmd.ExecuteScalar() ?? 0L) == 1;
        }
        catch { return false; }
    }

    // LCU position → ключ роли в БД
    public static string LcuToDbRole(string pos) => pos.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "middle"  => "mid",
        "bottom"  => "adc",
        "utility" => "support",
        _         => pos
    };

    /// Обратно: db-роль → позиция в терминах LCU. Нужно, чтобы посчитать пик
    /// НАПАРНИКА его же ролью — движок берёт роль из DraftState.MyPosition.
    public static string DbToLcuRole(string role) => role.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "mid"     => "middle",
        "adc"     => "bottom",
        "support" => "utility",
        _         => role
    };

    // ── Доли урона чемпиона (физ/маг/чистый) ────────────────────────────────
    // Замеры из матчей (таблица champion_damage). Пока её нет в скачанной базе —
    // возвращаем null, и оверлей падает на грубую оценку Data Dragon.
    private bool? _dmgTable;
    private readonly Dictionary<int, (double P, double M, double T)?> _dmgCache = new();

    public (double Phys, double Magic, double True)? DamageShare(int champId)
    {
        if (_dmgTable == false) return null;
        if (_dmgCache.TryGetValue(champId, out var hit)) return hit;

        (double, double, double)? res = null;
        try
        {
            if (_dmgTable is null)
            {
                var chk = _db.CreateCommand();
                chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='champion_damage'";
                _dmgTable = chk.ExecuteScalar() is not null;
                if (_dmgTable == false) return null;
            }

            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(phys),0), COALESCE(SUM(magic),0), COALESCE(SUM(trued),0)
                FROM champion_damage
                WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                double p = rd.GetDouble(0), m = rd.GetDouble(1), t = rd.GetDouble(2);
                var sum = p + m + t;
                if (sum > 0) res = (p / sum, m / sum, t / sum);
            }
        }
        catch { _dmgTable = false; }

        _dmgCache[champId] = res;
        return res;
    }

    /// <summary>
    /// Доверие к связке: сколько мы играем вдвоём за окно свежести, сглаженно
    /// (см. <see cref="MATE_CONF"/>). Одно место на оба применения — вес
    /// напарника в МОЕЙ оценке и надбавка в подсказке ему
    /// (<see cref="BestPartner"/>), — иначе они однажды разъедутся.
    /// </summary>
    private static double MateConfidence()
    {
        var games = SessionTracker.MateStats(PoolStore.ActiveDuo()?.FriendPuuid, FreshDays).Games;
        return games / (games + MATE_CONF);
    }

    public IReadOnlyList<Recommendation> Recommend(DraftState state, int topN = 6,
                                                   IReadOnlyCollection<int>? only = null)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];
        var wDirect = DirectWeight(myRole); // вес прямой контры зависит от роли

        // Чемпионы моего активного пула на эту роль — им «комфорт» пула (см.
        // ComfortDelta). Normal/пул не активен → пусто, флора нет.
        _comfortPool = [.. PoolStore.ActiveForRole(myRole).Mine];

        // only — переопределение кандидатов (пул игрока): считаем ЛУЧШЕГО из пула
        // той же логикой, даже если он не проходит порог общего списка.
        var candidates = only?.ToList() ?? GetCandidates(myRole);
        Console.WriteLine($"  [диаг] role={myRole}  tier={TierBucket}  патчи={PatchDisplay}  кандидатов={candidates.Count}");

        // Исключаем из кандидатов: залоченные пики обеих команд, баны, а также
        // чемпионов, наведённых (ховер) ЧУЖИМИ слотами — их уже не запикать.
        // СВОЙ пик/ховер НЕ исключаем: хочу видеть его в списке (насколько «угадал»).
        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            if (p.IsLocalPlayer) continue;            // мой слот — оставляем в подборе
            if (p.ChampionId != 0) taken.Add(p.ChampionId);
            else if (p.PickIntentId != 0) taken.Add(p.PickIntentId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        // Мой уже выбранный (или наведённый) чемпион — чтобы пометить и гарантированно показать.
        var myPickId = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;

        // Учитываем ховеры (EffectiveChampionId): рекомендации обновляются ещё
        // на этапе наведения чемпиона союзником/врагом, не дожидаясь лока.
        var directOppId = state.DirectOpponent?.EffectiveChampionId ?? 0;
        // Роли врагов скрыты (Blind/Solo-Duo) → прямого оппонента по позиции нет.
        // Определяем его эвристикой: враг, у которого частая роль = моей.
        if (directOppId == 0)
            directOppId = InferDirectOpponent(state, myRole);

        var allyData = state.MyTeam
            .Where(p => p.EffectiveChampionId != 0 && !p.IsLocalPlayer)
            .Select(p => (Id: p.EffectiveChampionId, Role: LcuToDbRole(p.Position))).ToList();

        // Напарник по активному дуо-пулу — ЧЕЛОВЕК из моей пати (см. Party).
        // По чемпиону его больше не ищем: у друга с широким пулом случайный
        // союзник, взявший оттуда чемпиона, сходил за напарника.
        var mateId = Party.MateChampion(state, PoolStore.ActiveDuo());

        // Сколько мы играем вдвоём СЕЙЧАС: от этого зависит, насколько громко
        // звучит дуо-вес (MATE_CONF). Считается ОДИН раз на драфт, а не на
        // чемпиона: журнал один и тот же, а кандидатов четыре сотни.
        var mateConf = mateId == 0 ? 0.0 : MateConfidence();

        // Бот — это 2v2: при адк/саппорте контрим и вражеского дуо-партнёра.
        // Пример: вражеский Эзреаль (адк) контрит Блицкранга (саппорт) — он сблинкуется
        // с хука, поэтому Блиц получит штраф против такого бота.
        var duoRole = myRole == "adc" ? "support" : myRole == "support" ? "adc" : null;
        var enemyDuoId = duoRole == null ? 0 : state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0 && LcuToDbRole(p.Position) == duoRole)
            .Select(p => p.EffectiveChampionId).FirstOrDefault();
        // Если виден ВЕСЬ вражеский бот (и прямой оппонент, и дуо) — даём боту
        // чуть больший вес: пик можно подогнать под известный 2v2.
        var wBotlane = (duoRole != null && enemyDuoId != 0 && directOppId != 0)
            ? W_BOTLANE_BOTH : W_BOTLANE;

        // ПРОЧИЕ враги (не лейн-оппонент и не дуо-партнёр — те покрыты своими
        // факторами): каждому определяем роль (позиция, иначе частая роль по
        // базе). В скоре на каждого сперва пробуем кросс-ролевой матчап (реальная
        // пара «моя роль против его роли», сигнал ~1.8 пп), а если такой пары в
        // базе ещё нет — фолбэк на same-role таблицу с меньшим весом.
        var otherEnemies = new List<(int Id, string Role)>();
        foreach (var p in state.TheirTeam)
        {
            var id = p.EffectiveChampionId;
            if (id == 0 || id == directOppId) continue;
            if (duoRole != null && id == enemyDuoId) continue;   // покрыт botlane-фактором
            var r = LcuToDbRole(p.Position);
            if (string.IsNullOrEmpty(r)) r = InferPrimaryRole(id);
            otherEnemies.Add((id, r));
        }

        // Динамический вес синергии: без информации о врагах синергия с союзниками
        // важнее, но потолок умеренный (1.5): по замерам сигнал синергии (~1.5 пп)
        // слабее вражеских матчапов, и раздутый вес делал подбор «глухим» к пикам
        // врагов в начале драфта (плюс чемпион на десятке совместных игр всплывал
        // в топ при пустой вражеской команде).
        const double W_SYN_MAX = 1.5;
        var knownEnemies = state.TheirTeam.Count(p => p.EffectiveChampionId != 0);
        var wSynergy = W_SYNERGY + (W_SYN_MAX - W_SYNERGY) * Math.Max(0.0, 1.0 - knownEnemies / 5.0);

        // ── Драфт-фичи (архетип/нейтральность) ──────────────────────────────
        var allEnemyIds = state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0).Select(p => p.EffectiveChampionId).ToList();
        // Неопределённость драфта: 1.0 если врагов не видно, 0 если все 5 + есть оппонент.
        var uncertainty = Math.Clamp(
            1.0 - knownEnemies / 5.0 + (directOppId == 0 ? 0.2 : 0.0), 0.0, 1.0);
        // Доминирующий архетип врага определяем при 2+ известных пиках.
        ChampionTraits.Arch? enemyDom = allEnemyIds.Count >= 2
            ? ChampionTraits.Dominant(allEnemyIds) : null;

        // Item value (п.1): союзники (без меня) и враги для профиля уязвимостей.
        var vulnAllyIds = allyData.Select(a => a.Id).ToList();

        // Структурная синергия (п.4): id союзных джанглера и АДК.
        var jungleAllyId = allyData.FirstOrDefault(a => a.Role == "jungle").Id;
        var adcAllyId    = allyData.FirstOrDefault(a => a.Role == "adc").Id;

        var ordered = candidates
            .Where(id => !taken.Contains(id))
            .Select(champId =>
            {
                // Базовый WR + темпер по выборке: мало игр → WR ближе к 50%
                // (иначе «68% на 100 играх» раздувает оценку и обоснование).
                var (bg, bw)  = RawBase(champId, myRole);
                var rawBase   = Delta(bg, bw, K);   // сила чемпиона без темпера — база для чистых дельт
                var baseDelta = rawBase * (bg / (bg + BASE_CONF));

                // ЧИСТАЯ контра: матчап-WR минус собственный базовый WR — иначе сила
                // чемпиона считалась бы дважды (в базе ×1.0 и внутри контры ×W_DIRECT),
                // и мета-чемпионы возглавляли бы список «как контрпики». Плюс темпер
                // по объёму пары: 4 игры 4 победы больше не дают фантомных +8пп.
                double PureVs(double g, double w) =>
                    g > 0 ? (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF)) : 0.0;

                // Прямой оппонент — отдельный матчап
                var (dg, dw)    = directOppId != 0 ? RawMatchup(champId, myRole, directOppId) : (0, 0);
                var directDelta = PureVs(dg, dw);

                // Прочие враги: по каждому сперва кросс-ролевой матчап (реальный
                // контекст пары, сигнал ~1.8 пп), если его в базе нет — same-role
                // фолбэк. Каждый пул складывает игры/победы и сглаживается один
                // раз — редкие пары не обнуляются делением на количество врагов.
                double oxg = 0, oxw = 0;                      // пул кросс-матчапов
                double omg = 0, omw = 0;                      // пул same-role фолбэка
                var otherByEnemy = new List<(int Id, double Delta)>();
                foreach (var (eid, erole) in otherEnemies)
                {
                    var (cg, cw) = erole.Length > 0
                        ? RawBotlane(champId, myRole, eid, erole) : (0.0, 0.0);
                    if (cg > 0)
                    {
                        oxg += cg; oxw += cw;
                        otherByEnemy.Add((eid, PureVs(cg, cw)));
                    }
                    else
                    {
                        var (mg, mw) = RawMatchup(champId, myRole, eid);
                        omg += mg; omw += mw;
                        otherByEnemy.Add((eid, PureVs(mg, mw)));
                    }
                }
                var otherDelta = PureVs(omg, omw);
                var crossDelta = PureVs(oxg, oxw);

                // Синергия с союзниками: то же — дельта по каждому + ПУЛ для скора.
                // Тоже ЧИСТАЯ (минус собственная база): «пара играет лучше, чем этот
                // чемпион в среднем», а не «сильный чемпион хорош с кем угодно».
                var synRaw = allyData
                    .Select(a => { var (g, w) = RawSynergy(champId, myRole, a.Id, a.Role); return (a.Id, a.Role, G: g, W: w); })
                    .ToList();
                var synByAlly = synRaw.Select(x => (x.Id, x.Role, Delta: PureVs(x.G, x.W))).ToList();
                var synGames = synRaw.Sum(x => x.G);
                // Темпер по выборке: мало совместных игр → синергии меньше доверия,
                // чтобы «чемпионы на 8 играх» не доминировали при высоком весе без врагов.
                var synConf  = synGames / (synGames + CONF_GAMES);
                var synDelta = synRaw.Count > 0 && synGames > 0
                    ? (Delta(synGames, synRaw.Sum(x => x.W), K_PAIR) - rawBase) * synConf : 0.0;

                // Надбавка за напарника: его связка со мной весит больше, чем связка
                // со случайным союзником. Кладём в ТУ ЖЕ полосу синергии, а не в скор
                // отдельным слагаемым, — иначе оценка росла бы, а объяснение в
                // карточке осталось прежним. Дельта напарника уже сглажена по объёму
                // выборки (PureVs), второй раз не темперим.
                if (mateId != 0)
                {
                    var mate = synByAlly.FirstOrDefault(x => x.Id == mateId);
                    // Умножаем на доверие к связке: пока вы вдвоём не наиграли,
                    // надбавка почти не слышна, и напарник весит как обычный
                    // союзник. Это и есть ответ на «случайный сопартиец гнёт
                    // совет не хуже настоящего напарника».
                    if (mate.Id != 0)
                        synDelta += (DUO_MATE_MULT - 1.0) * mate.Delta * mateConf;
                }

                var comfortDelta = ComfortDelta(champId); // наигранность игрока

                // Драфт-фичи: нейтральность, трифекта, анти-стиль.
                var (draftBonus, styleScore, draftReasons) =
                    DraftFit(champId, enemyDom, uncertainty);

                // Item value (п.1) + баланс типа урона (AD/AP): влияют на СКОР (штраф за
                // стак уязвимости/типа урона, бонус за наказание врага), но в тексте
                // причин НЕ выводятся — уязвимости показываем строкой итем-иконок внизу.
                var (vulnPen, _, _)   = ItemValue.VulnPenalty(champId, vulnAllyIds);
                var (exploit, _)      = ItemValue.ExploitBonus(champId, allEnemyIds);
                var (dmgDelta, _, _)  = ItemValue.DamageBalance(champId, vulnAllyIds, allEnemyIds);

                // Структурная синергия джангл↔саппорт (п.4).
                var (structBonus, structReasons) = StructuralBonus(champId, myRole, jungleAllyId, adcAllyId, vulnAllyIds);
                draftReasons.AddRange(structReasons);

                // Бот 2v2: матчап против вражеского дуо-партнёра (кросс-роль).
                var (btg, btw)   = _botlaneReady && enemyDuoId != 0 && duoRole != null
                    ? RawBotlane(champId, myRole, enemyDuoId, duoRole) : (0.0, 0.0);
                var botlaneDelta = PureVs(btg, btw);

                if (botlaneDelta >= 0.8)
                    draftReasons.Add(Good(Loc.T("reason.botlaneGood", DataDragon.Name(enemyDuoId))));
                else if (botlaneDelta <= -1.2)
                    draftReasons.Add(Bad(Loc.T("reason.botlaneBad", DataDragon.Name(enemyDuoId))));

                var personalDelta = PersonalDelta(champId);   // «у меня на нём идёт»

                // Против того, кто тебя контрит, личная прибавка ужимается.
                // Берём ту же величину, что показана в карточке строкой «против
                // оппонента» (матчап + бот 2v2): объяснение и счёт должны
                // говорить об одном и том же. Обе величины правим ЗДЕСЬ, до
                // причин и до записи в карточку, — иначе бар комфорта обещал бы
                // то, чего в оценке уже нет.
                var guard = CounterGuard(directDelta + botlaneDelta);
                comfortDelta  *= guard;
                personalDelta *= guard;

                var score   = W_BASE * baseDelta + wDirect * directDelta + W_OTHER * otherDelta
                            + wSynergy * synDelta + W_POOL * comfortDelta + draftBonus
                            - W_VULN * vulnPen + W_EXPLOIT * exploit + W_STRUCT * structBonus
                            + wBotlane * botlaneDelta + W_CROSS * crossDelta + W_DMGBAL * dmgDelta
                            + W_PERSONAL * personalDelta;
                var reasonList = BuildReasons(champId, directDelta, directOppId, synDelta, synByAlly,
                                              otherDelta, otherByEnemy, baseDelta, comfortDelta)
                                   .Concat(draftReasons).ToList();
                // Личный винрейт показываем только когда он заметен — иначе строка
                // «твой винрейт» висела бы у каждого второго кандидата.
                if (personalDelta >= 1.5)
                {
                    var (pg, pw) = MyHistory().Recent(champId);
                    if (pg > 0)
                        reasonList.Add(Good(Loc.T("reason.personalWr", $"{100.0 * pw / pg:F0}", pg)));
                }
                var reasons = reasonList.ToArray();

                // «Против оппонента» на нижней линии — это вся вражеская пара, а не
                // один визави. Саппорту важнее всего, как он играется против ЧУЖОГО
                // АДК (и наоборот), но раньше полоска показывала только матчап со
                // своей же ролью: пока вражеский саппорт не выбран, там стоял ноль,
                // хотя счёт уже учитывал контру против их адк.
                var shownDirect = directDelta + botlaneDelta;
                return new Recommendation(champId, score, baseDelta, shownDirect, otherDelta, synDelta, comfortDelta, styleScore, reasons);
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        // Топ-6: мой уже выбранный чемпион остаётся в подборе наравне со всеми и
        // помечается, но НЕ пиннится — если из-за новых пиков он вышел из топа,
        // он выпадает из списка так же, как любой другой кандидат.
        return ordered.Take(topN)
            .Select((r, i) => r with { Rank = i + 1, IsMyPick = r.ChampionId == myPickId })
            .ToList();
    }

    /// До max ВЫГОДНЫХ пиков из пула (Score > 0), отсортированы по силе. Если ни
    /// один не выгоден против врагов — возвращаем ОДИН лучший (пул всегда что-то
    /// предлагает). Общий подбор идёт ниже этих карточек.
    public IReadOnlyList<Recommendation> TopFromPool(DraftState state, IReadOnlyCollection<int> poolChamps, int max = 3)
    {
        if (poolChamps.Count == 0) return [];
        var scored = Recommend(state, poolChamps.Count, poolChamps);
        if (scored.Count == 0) return [];
        var good = scored.Where(r => r.Score > 0).Take(max).ToList();
        return good.Count > 0 ? good : [scored[0]];
    }

    /// Чистая контра кандидата ПРОТИВ КОНКРЕТНОГО врага (%пп) — та же формула, что
    /// внутри скоринга: матчап минус собственная база, темпер по объёму пары.
    /// Для подсветки врагов при наведении на карточку.
    public double VersusDelta(int champId, string myRole, int enemyId, string enemyRole)
    {
        if (champId == 0 || enemyId == 0) return 0.0;
        var (bg, bw) = RawBase(champId, myRole);
        var rawBase  = Delta(bg, bw, K);

        var (g, w) = enemyRole.Length > 0 ? RawBotlane(champId, myRole, enemyId, enemyRole) : (0.0, 0.0);
        if (g <= 0) (g, w) = RawMatchup(champId, myRole, enemyId);
        return g > 0 ? (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF)) : 0.0;
    }

    /// Вклад «против стиля» в расчёте на ОДНОГО врага: та же трифекта и тот же
    /// анти-стиль, что в DraftFit, но по архетипу конкретного чемпиона.
    public static double StyleVsArch(int champId, ChampionTraits.Arch enemyArch)
    {
        var (f2b, dive, pick) = ChampionTraits.Archetype(champId);
        var want = enemyArch switch
        {
            ChampionTraits.Arch.Dive     => f2b,
            ChampionTraits.Arch.PickPoke => dive,
            _                            => pick,
        };
        double style = enemyArch switch
        {
            ChampionTraits.Arch.PickPoke =>
                ChampionTraits.Gapclose(champId) + ChampionTraits.Engage(champId),
            ChampionTraits.Arch.Dive =>
                ChampionTraits.Peel(champId) + ChampionTraits.Disengage(champId),
            _ =>
                (ChampionTraits.LongRange(champId) ? 2 : 0) + ChampionTraits.Burst(champId),
        };
        return W_TRIFECTA * want + W_STYLE * style;
    }

    /// Чистая синергия ПАРЫ (m ↔ f), как дельта: WR пары минус база m, темпер по
    /// объёму. Роли маржинализуем (пара может быть любых линий).
    public double PairSynergy(int m, string mRole, int f, string? fRole = null)
    {
        // ПО РОЛИ, а не по всем сразу. Связка живёт на конкретной линии:
        // Мальфит с Ясуо на топе — 8034 игры и чистые +1,26, а если досыпать
        // его же лес и саппорт, где пара проигрывает, выходит −0,22. Кружок у
        // союзника зажигается от +0,3 — и настоящая связка до него не
        // доезжала, хотя в карточке та же пара стояла с плюсом.
        // Линия союзника учитывается там же (RawSynergy): «Ясуо на миде» и
        // «Ясуо в боте» — разные пары.
        var (g, w) = RawSynergy(m, mRole, f, fRole);
        // Нет данных по роли (редкий чемпион, свежий патч) — берём что есть:
        // приблизительно, но лучше, чем промолчать.
        if (g <= 0) (g, w) = RawSynergyAny(m, f);
        if (g <= 0) return 0.0;

        var (bg, bw) = RawBase(m, mRole);
        if (bg <= 0) return 0.0;
        var rawBase = Delta(bg, bw, K);
        // Темпер тот же, что у строки в карточке (PureVs): кружок и подпись
        // обязаны показывать одно и то же число, иначе они спорят друг с другом.
        return (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF));
    }

    /// Статистика фиксированной связки для показа в настройках:
    ///   Games — сколько совместных игр в данных (сила выборки),
    ///   Wr    — сырой винрейт связки (wins/games),
    ///   Delta — насколько связка сильнее, чем эти двое по отдельности
    ///           (темпер. WR пары минус средний базовый WR обоих чемпионов).
    /// Роли заданы → берём роль-специфичные данные; при нуле игр откат на маржинал.
    public (int Games, double Wr, double Delta) PairStats(int m, string? mRole, int f, string? fRole)
    {
        (double g, double w) syn = (0, 0);
        if (!string.IsNullOrEmpty(mRole) && !string.IsNullOrEmpty(fRole))
            syn = RawSynergyRoles(m, mRole!, f, fRole!);
        if (syn.g <= 0) syn = RawSynergyAny(m, f);   // откат: пары по ролям редки
        if (syn.g <= 0) return (0, 0, 0);

        var (mg, mw) = !string.IsNullOrEmpty(mRole) ? RawBase(m, mRole!) : RawBaseAny(m);
        var (fg, fw) = !string.IsNullOrEmpty(fRole) ? RawBase(f, fRole!) : RawBaseAny(f);
        var baseM = mg > 0 ? Delta(mg, mw, K) : 0.0;
        var baseF = fg > 0 ? Delta(fg, fw, K) : 0.0;
        var synDelta = Delta(syn.g, syn.w, K_PAIR) - (baseM + baseF) / 2.0;
        return ((int)Math.Round(syn.g), 100.0 * syn.w / syn.g, synDelta);
    }

    /// <summary>
    /// Насколько хорош КАЖДЫЙ чемпион из половины друга в ЭТОМ драфте — тем же
    /// скорингом, которым считается мой пик, только в роли напарника.
    ///
    /// Без этого подсказка напарнику была бы «кто лучше всех сочетается с моим
    /// пиком» и не зависела бы ни от врагов, ни от остальных союзников: саппорт,
    /// которого вражеский бот разбирает, шёл бы наравне с удобным.
    ///
    /// Роль берём ту, под которую чемпион положен в половину, и считаем роль
    /// одной пачкой: позиция у движка приходит из состояния, поэтому на каждую
    /// роль своё подменённое состояние, где локальный игрок сидит в слоте
    /// напарника. <c>DirectOpponent</c> там снимаем намеренно — движок выведет
    /// оппонента ПО ЭТОЙ роли сам (<see cref="InferDirectOpponent"/>), а
    /// готовый указывал бы на моего.
    ///
    /// Личные факторы на это время выключены (<see cref="_forMate"/>):
    /// наигранность и личный винрейт — моя история, не его.
    ///
    /// Мой слот остаётся в команде со своим чемпионом — иначе у его кандидатов
    /// пропала бы структурная связка со мной (адк↔саппорт, лес↔линия). Обычную
    /// синергию со мной его оценка поэтому уже содержит, и
    /// <see cref="BestPartner"/> добавляет только надбавку над ней.
    /// </summary>
    public IReadOnlyDictionary<int, double> PartnerScores(
        DraftState state, IReadOnlyDictionary<string, List<int>> friendHalf,
        string mateRole = "")
    {
        var res = new Dictionary<int, double>();
        if (friendHalf.Count == 0) return res;

        foreach (var (role, champs) in friendHalf)
        {
            // Роль напарника видна в драфте — считаем ТОЛЬКО её. Половина
            // разложена по линиям, а на какой он стоит сейчас, раньше никто не
            // спрашивал: человеку на миде предлагали чемпиона из его
            // джангл-набора. Роль не раскрыта — считаем всю половину, как раньше.
            if (mateRole.Length > 0 && role != mateRole) continue;

            var ids = champs.Where(c => c != 0).Distinct().ToList();
            if (ids.Count == 0) continue;

            var lcu  = DbToLcuRole(role);
            var seat = new DraftPlayer(CellId: -100, ChampionId: 0, PickIntentId: 0,
                                       Position: lcu, IsLocalPlayer: true);
            // Мой слот ОСТАЁТСЯ союзником со своим чемпионом: иначе у его
            // кандидатов пропадает структурная связка со мной (адк↔саппорт,
            // лес↔линия), а она часть той же формулы. Пару со мной BestPartner
            // добавляет не целиком, а НАДБАВКОЙ над обычным союзником — ровно
            // так, как её добавляет движок в моей собственной оценке.
            var team = state.MyTeam
                .Select(p => p.IsLocalPlayer ? p with { IsLocalPlayer = false } : p)
                .Append(seat)
                .ToList();

            var swapped = state with
            {
                MyTeam = team, Me = seat, MyPosition = lcu, DirectOpponent = null,
            };

            _forMate = true;
            try
            {
                foreach (var r in Recommend(swapped, ids.Count, ids))
                    res[r.ChampionId] = r.Score;
            }
            finally { _forMate = false; }
        }
        return res;
    }

    /// <summary>
    /// Кого из половины друга брать НАПАРНИКУ под мой пик.
    ///
    /// Нужно, чтобы дуо-пул предлагал пару СРАЗУ, как кончились баны, а не ждал
    /// чужого хода: напарник чаще всего пикает после меня, и к моему ходу у него
    /// пусто — а подсказка «что брать обоим» нужна как раз заранее.
    ///
    /// Складываем две вещи: его собственную силу в этом драфте
    /// (<see cref="PartnerScores"/> — враги, союзники, структурные связки,
    /// баланс урона; я там уже союзник с моим чемпионом) и НАДБАВКУ за то, что
    /// он не случайный союзник, а напарник: <c>W_SYNERGY · (DUO_MATE_MULT−1) ·
    /// доверие · дельта пары</c>. Ровно тем же слагаемым и с тем же порогом по
    /// совместным играм движок считает напарника в МОЕЙ оценке, так что формула
    /// у подсказки и у подбора одна, и своего веса тут не выдумано.
    ///
    /// Занятых и забаненных не предлагаем — их уже не взять.
    /// </summary>
    public int BestPartner(DraftState state, int mineId, string myRole,
                           IReadOnlyDictionary<string, List<int>> friendHalf,
                           IReadOnlyDictionary<int, double>? partnerScores = null,
                           string mateRole = "")
    {
        if (mineId == 0 || friendHalf.Count == 0) return 0;
        var scores = partnerScores ?? PartnerScores(state, friendHalf, mateRole);
        var conf   = MateConfidence();

        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            // Свой слот не исключаем из занятых только для СЕБЯ; напарнику мой
            // ховер брать нельзя — он занят так же, как чужой.
            if (p.EffectiveChampionId != 0) taken.Add(p.EffectiveChampionId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        var best = 0;
        var bestTotal = double.NegativeInfinity;
        foreach (var (role, champs) in friendHalf)
        {
            // Та же калитка, что в PartnerScores: кандидаты вне его роли там не
            // считались вовсе, и сюда они приходили бы с нулём — а ненулевая
            // надбавка за пару всё равно могла бы их вытащить вперёд.
            if (mateRole.Length > 0 && role != mateRole) continue;

            foreach (var f in champs)
            {
                if (f == 0 || f == mineId || taken.Contains(f)) continue;
                var own   = scores.TryGetValue(f, out var s) ? s : 0.0;
                var pair  = PairStats(mineId, myRole, f, role).Delta;
                // Надбавка, а не вся пара: обычную синергию со мной его оценка
                // уже посчитала — я в его команде союзником. Добавляем ровно то,
                // чем дуо-напарник отличается от случайного, и так же ужимаем по
                // совместным играм.
                var total = own + W_SYNERGY * (DUO_MATE_MULT - 1.0) * conf * pair;
                if (total > bestTotal) { bestTotal = total; best = f; }
            }
        }
        return best;
    }

    // ---------- ARAM: подбор по скамейке ----------

    /// ARAM: врагов не видно, но есть скамейка. Из [мой текущий + скамейка] выбираем
    /// пик, лучше всего дополняющий команду: синергия, смешанный урон, закрытие «дыр»
    /// композиции (фронт/саст/поук), сила чемпиона, комфорт. ARAM-данных нет —
    /// синергию/WR берём из SR как слабый сигнал, остальное эвристикой по трейтам.
    public IReadOnlyList<Recommendation> RecommendAram(DraftState state, int topN = 6)
    {
        _comfortPool = [];   // в ARAM пулов нет — только наигранность
        var me = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer);
        var myChamp = me?.EffectiveChampionId ?? 0;

        var cands = new List<int>();
        if (myChamp != 0) cands.Add(myChamp);
        cands.AddRange(state.Bench);
        cands = cands.Where(c => c != 0).Distinct().ToList();
        if (cands.Count == 0) return [];

        var allyIds = state.MyTeam
            .Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
            .Select(p => p.EffectiveChampionId).ToList();

        // «Дыры» композиции команды (без меня). Фронт = танк/бойец/заход (не просто cc).
        bool hasFront = allyIds.Any(a => ChampionTraits.IsTanky(a) || ChampionTags.Has(a, "engage"));
        bool hasHeal  = allyIds.Any(ChampionTraits.HealReliant);

        var scored = cands.Select(champId =>
        {
            var (bg, bw)  = RawBaseAny(champId);
            var rawBase   = Delta(bg, bw, K);
            var baseDelta = rawBase * (bg / (bg + BASE_CONF));

            // Чистая синергия (минус собственная база) — как в основном подборе.
            var syn = allyIds.Select(a => RawSynergyAny(champId, a)).ToList();
            var synGames = syn.Sum(x => x.g);
            var synDelta = syn.Count > 0 && synGames > 0
                ? (Delta(synGames, syn.Sum(x => x.w), K_PAIR) - rawBase) * (synGames / (synGames + CONF_GAMES)) : 0.0;

            // Баланс типа урона (в ARAM смешанный урон особенно важен). Врагов нет.
            var (dmgDelta, _, _) = ItemValue.DamageBalance(champId, allyIds, []); // влияет на скор, без текста

            double gap = 0;
            var reasons = new List<string>();
            if (!hasFront && (ChampionTraits.IsTanky(champId) || ChampionTags.Has(champId, "engage")))
            { gap += 2.5; reasons.Add(Good(Loc.T("reason.aramFront"))); }
            if (!hasHeal && ChampionTraits.HealReliant(champId))
            { gap += 1.5; reasons.Add(Good(Loc.T("reason.aramHeal"))); }
            if (ChampionTraits.LongRange(champId)) gap += 0.8;

            var comfort = ComfortDelta(champId);
            if      (comfort >= 3.8) reasons.Add(Good(Loc.T("reason.comfortHigh")));
            else if (comfort >= 1.9) reasons.Add(Good(Loc.T("reason.comfortMid")));

            if (synDelta > 0.5) reasons.Add(Good(Loc.T("reason.fitsTeam")));
            if      (baseDelta >= 2.5) reasons.Add(Good(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}")));
            else if (baseDelta >= 1.0) reasons.Add(Good(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}")));
            if (reasons.Count == 0) reasons.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));

            var score = W_ARAM_BASE * baseDelta + W_ARAM_SYN * synDelta
                      + W_ARAM_DMG * dmgDelta + W_ARAM_GAP * gap + W_POOL * comfort;
            return new Recommendation(champId, score, baseDelta, 0, 0, synDelta, comfort, 0, [.. reasons]);
        })
        .OrderByDescending(r => r.Score)
        .ToList();

        return scored.Take(topN)
            .Select((r, i) => r with { Rank = i + 1, IsMyPick = r.ChampionId == myChamp })
            .ToList();
    }

    // ARAM: базовый WR чемпиона без учёта роли (по всем ролям).
    private (double g, double w) RawBaseAny(int champId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM base_wr
            WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // ARAM: синергия пары без учёта роли (сумма по всем ролям, обе стороны пары).
    private (double g, double w) RawSynergyAny(int champId, int allyId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@c AND ally_id=@a AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@a AND ally_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // Матчап без учёта роли (сумма по всем ролям) — для банов, когда роль своя или
    // союзника ещё не назначена (ranked solo/duo, блайнд): защиту всё равно считаем.
    private (double g, double w) RawMatchupAny(int champId, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM matchup
            WHERE champion_id=@c AND vs_champion_id=@v AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // ---------- Рекомендация банов ----------

    /// Кого банить: сильные/популярные в патче чемпионы твоей роли, которые ещё и
    /// плохи лично для тебя (контрят твой пул). Возвращает топ-N.
    /// hoverHistory: cellId → чемпионы, показанные игроком за драфт (пул команды).
    /// Бан, контрящий сразу нескольких из показанных, получает бонус за широту.
    public IReadOnlyList<BanRec> RecommendBans(
        DraftState state, IReadOnlyDictionary<int, HashSet<int>>? hoverHistory = null, int top = 5)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var stats = RoleStats(myRole);            // champId → (games, wins)
        if (stats.Count == 0) return [];
        var maxGames = stats.Values.Max(v => v.Games);
        var myPicks  = RolePicks(myRole);         // пикрейт моей роли за последние патчи

        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
            if (p.EffectiveChampionId != 0) taken.Add(p.EffectiveChampionId);
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);
        // Кого свои уже наводили за драфт — это их запасные пики, и банить их
        // нельзя: отнимешь вариант у союзника. Раньше исключались только
        // текущие наведения, и Вуконг, которого союзник навёл и сменил на Каина,
        // стоял четвёртым баном и тут же — в «контрит ваших: Каин, Вуконг».
        if (hoverHistory != null)
            foreach (var p in state.MyTeam)
                if (hoverHistory.TryGetValue(p.CellId, out var shownBy))
                    taken.UnionWith(shownBy.Where(id => id != 0));

        // Мои мейны на этой роли — чтобы банить их контр-пики.
        //
        // Сперва те, кем игрок ДЕЙСТВИТЕЛЬНО играет в последнее время. Раньше
        // брали только мастерство, а оно копится годами и не забывается: в
        // банах всплывало «контрит Зилеана» на чемпионе, которого человек не
        // трогал полгода. Совет про него — потраченный бан.
        //
        // Вторым — мой активный пул на эту роль из профиля: «этих я готов
        // взять». Раньше баны пул не читали вовсе, и у игрока с собранным пулом
        // саппортов, но короткой историей, контрить было почти нечего.
        //
        // Вес мейна — сколько я на нём играю: контра чемпиону с 32 играми за
        // месяц важнее контры тому, кто просто лежит в пуле.
        //
        // Мастерством добираем, только если нет ни истории, ни пула: у свежей
        // установки без него список был бы пустым.
        var mainW = new Dictionary<int, double>();
        foreach (var p in MyHistory().Played.Where(p => p.Games > 0 && stats.ContainsKey(p.Id)))
            mainW[p.Id] = p.Games;
        foreach (var id in PoolStore.ActiveForRole(myRole).Mine.Where(stats.ContainsKey))
            mainW[id] = Math.Max(mainW.GetValueOrDefault(id), POOL_MAIN_GAMES);
        if (mainW.Count == 0)
            foreach (var id in Mastery.Where(kv => stats.ContainsKey(kv.Key))
                                      .OrderByDescending(kv => kv.Value).Take(MainsTake).Select(kv => kv.Key))
                mainW[id] = POOL_MAIN_GAMES;
        var myMains = mainW.OrderByDescending(kv => kv.Value).Take(MAINS_FOR_BANS).ToList();
        // Своих не баним: на ком я играл за месяц на этой роли — того бан
        // отнимает у меня. С пулом в счёте Браум с 19 играми за месяц стоял
        // третьим баном у самого владельца — «контрит твой пул (Эш, Сона)».
        // Пул целиком не исключаем (решение владельца): в нём бывает двадцать
        // саппортов, и советы лишились бы Треша, Лулу, Нами. Чемпион пула без
        // игр остаётся мейном — против него ищем контры, — но забанить его можно.
        taken.UnionWith(MyHistory().Played.Where(p => p.Games > 0 && stats.ContainsKey(p.Id)).Select(p => p.Id));

        var scores  = new Dictionary<int, double>();
        // Доводы с приоритетом: карточка показывает первые два, и первыми
        // должны идти те, ради которых бан и выбран, — защита моего пика, пиков
        // союзников, угроза команде, — а «сильный в патче» уже потом.
        // Моя линия — впереди защиты союзников: бан прежде всего мой.
        const int R_MINE = 0, R_POOL = 1, R_ALLY = 2, R_TEAM = 3, R_META = 4, R_PICK = 5;
        var reasons = new Dictionary<int, List<(int Prio, string Text)>>();
        void AddReason(int id, string r, int prio)
        {
            if (!reasons.TryGetValue(id, out var l)) reasons[id] = l = [];
            if (!l.Any(x => x.Text == r)) l.Add((prio, r));
        }
        // Пикрейт кандидата на роли, где он встречает того, кого контрит, — в
        // подпись: видно, что бан не на того, кого почти не берут.
        var pickOn = new Dictionary<(int Champ, string Role), double>();
        // Сколько очков кандидат набрал за МОЮ линию (мета роли, контра пулу,
        // защита моего пика) — по ним делятся слоты, см. выбор ниже.
        var laneScore = new Dictionary<int, double>();

        // 1. Сила в патче + популярность + контр-пики моего пула (кандидаты моей роли).
        foreach (var x in stats.Keys)
        {
            if (taken.Contains(x)) continue;
            // Офф-мета на этой роли — не кандидат в бан вовсе: Зилеан на топе
            // (0,09% игр) стоял первым баном за то, что «бьёт Дариуса», хотя
            // встретить его там почти невозможно.
            var pick = myPicks.Pick.GetValueOrDefault(x);
            if (pick < MIN_ROLE_SHARE * 100) continue;
            var (g, w) = stats[x];
            var metaWr = ((w + K / 2.0) / (g + K) - PRIOR) * 100 * (g / (g + BASE_CONF));
            var pop    = maxGames > 0 ? g / maxGames : 0;
            var meet   = Meet(pick, myPicks.Ref);

            // Насколько x бьёт мой пул: средняя контра по мейнам с весом по
            // играм. Раньше брали самый контрящий мейн — но с пулом из профиля
            // мейнов стало много, и «бьёт хоть кого-то» сказать можно почти про
            // любого. Средняя по тому, что я реально беру, — это и есть «как
            // часто он испортит мне игру».
            double counterMe = 0, wSum = 0;
            var beaten = new List<(int Id, double Hit)>();
            foreach (var (m, wm) in myMains)
            {
                if (m == x) continue;   // сам себе не контра (чемпион пула без игр — тоже кандидат)
                var (mg, mw) = RawMatchup(m, myRole, x);
                if (mg <= 0) continue;
                // Чистая контра моего мейна: его WR в паре ниже его же среднего,
                // с темпером по объёму пары (редкие пары — не доказательство).
                var (mbg, mbw) = RawBase(m, myRole);
                var s = (Delta(mbg, mbw, K) - Delta(mg, mw, K_PAIR)) * (mg / (mg + MATCHUP_CONF));
                wSum += wm;
                if (s <= 0) continue;
                counterMe += wm * s;
                if (s >= 1.5) beaten.Add((m, wm * s));
            }
            counterMe = wSum > 0 ? counterMe / wSum : 0;

            // Контра весит столько, насколько вероятно её встретить: та же сила
            // против моего мейна у чемпиона, которого берут в 7% игр, стоит
            // бана, а у взятого в 0,6% — почти нет.
            var score = W_BAN_META * metaWr + W_BAN_POP * (pop * 10) + W_BAN_COUNTER * counterMe * meet;
            if (score <= 0) continue;
            scores[x] = score;
            laneScore[x] = score;

            pickOn[(x, myRole)] = pick;
            // Называем тех, кого он бьёт сильнее всего с учётом игр, — до двух.
            if (beaten.Count > 0 && counterMe >= 0.5)
                AddReason(x, Loc.T("reason.countersPoolPick",
                    string.Join(", ", beaten.OrderByDescending(b => b.Hit).Take(2).Select(b => DataDragon.Name(b.Id))),
                    $"{pick:F1}"), R_POOL);
            if (metaWr >= 1.5) AddReason(x, Good(Loc.T("reason.strongPatch", $"{50 + metaWr:F1}")), R_META);
            if (pick >= myPicks.Ref * 1.5) AddReason(x, Loc.T("reason.pickRate", $"{pick:F1}"), R_PICK);
        }

        // 2. Защита заявленных пиков: банить тех, кто контрит уже наведённых/взятых
        //    чемпионов. Приоритет — мой пик, затем союзники. Роль часто не назначена
        //    (ranked solo/duo, блайнд), поэтому матчап при пустой роли берём по всем ролям.
        var protectees = new List<(int Id, string? Role, double Weight, bool Mine)>();
        var me = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer);
        if (me is { EffectiveChampionId: not 0 })
            protectees.Add((me.EffectiveChampionId, string.IsNullOrEmpty(myRole) ? null : myRole, W_BAN_MYPICK, true));
        foreach (var ally in state.MyTeam)
        {
            if (ally.IsLocalPlayer || ally.EffectiveChampionId == 0) continue;
            var ar = LcuToDbRole(ally.Position);
            protectees.Add((ally.EffectiveChampionId, string.IsNullOrEmpty(ar) ? null : ar, W_BAN_ALLY, false));
        }

        // История ховеров: игроки в фазе банов перебирают чемпионов — защищаем
        // весь показанный пул (меньшим весом: наведение сейчас важнее прошлого).
        if (hoverHistory != null)
        {
            var seen = protectees.Select(p => p.Id).ToHashSet();
            foreach (var player in state.MyTeam)
                if (hoverHistory.TryGetValue(player.CellId, out var shown))
                {
                    var hr = LcuToDbRole(player.Position);
                    foreach (var hid in shown)
                        if (hid != 0 && seen.Add(hid))
                            protectees.Add((hid, string.IsNullOrEmpty(hr) ? null : hr,
                                            player.IsLocalPlayer ? W_BAN_MYPICK * 0.75 : W_BAN_HOVER,
                                            player.IsLocalPlayer));
                }
        }

        // Самый жёсткий контрпик каждого защищаемого — ему гарантируется место
        // в топе банов (см. ниже): игрок навёл чемпиона в фазе банов — значит
        // хочет увидеть, от кого его защищать.
        var bestByProtectee = new Dictionary<int, (int Champ, double Strength)>();
        var victimsOf = new Dictionary<int, HashSet<int>>(); // бан-кандидат → кого он контрит ЗАМЕТНО (для бонуса ширины)
        // Все, кого кандидат контрит хоть сколько-то: из этого строим подпись
        // «контрит Сону, Люкс и Морgану» — игрок должен видеть ВЕСЬ список.
        var hitsOf = new Dictionary<int, List<(int Id, bool Mine, string? Role)>>();

        // Пикрейт на роли защищаемого: контру на линии берут именно туда. Роль
        // неизвестна — встречаемость не учитываем (множитель 1).
        var picksByRole = new Dictionary<string, (Dictionary<int, double> Pick, double Ref)>
            { [myRole] = myPicks };

        foreach (var (pid, prole, weight, mine) in protectees)
            foreach (var c in TopCounters(pid, prole, 8, minGames: 20, minEdge: 0.48))
            {
                if (taken.Contains(c)) continue;
                var meet = 1.0;
                if (prole != null && DbRoles.Contains(prole))
                {
                    if (!picksByRole.TryGetValue(prole, out var rp)) picksByRole[prole] = rp = RolePicks(prole);
                    var pick = rp.Pick.GetValueOrDefault(c);
                    if (pick < MIN_ROLE_SHARE * 100) continue;   // на этой линии его почти не берут
                    meet = Meet(pick, rp.Ref);
                    pickOn[(c, prole)] = pick;
                }
                var (mg, mw) = prole != null ? RawMatchup(pid, prole, c) : RawMatchupAny(pid, c);
                if (mg <= 0) continue;
                // Насколько c бьёт нашего чемпиона: чистая дельта против его же
                // среднего WR, с темпером по объёму пары. Порог мягкий: чистые
                // темперированные дельты малы, а защиту показать важно.
                var (pbg, pbw) = prole != null ? RawBase(pid, prole) : RawBaseAny(pid);
                var strength = (Delta(pbg, pbw, K) - Delta(mg, mw, K_PAIR)) * (mg / (mg + MATCHUP_CONF));
                if (strength < 0.15) continue;
                // В счёт и в «самую жёсткую контру» — с поправкой на то, как
                // часто её берут; пороги «контрит ли вообще» — по чистой силе.
                var likely = strength * meet;
                scores[c] = scores.GetValueOrDefault(c) + weight * likely;
                if (mine) laneScore[c] = laneScore.GetValueOrDefault(c) + weight * likely;
                if (!hitsOf.TryGetValue(c, out var hits)) hitsOf[c] = hits = [];
                if (!hits.Any(h => h.Id == pid)) hits.Add((pid, mine, prole));
                if (!bestByProtectee.TryGetValue(pid, out var cur) || likely > cur.Strength)
                    bestByProtectee[pid] = (c, likely);
                if (strength >= 0.3)
                {
                    if (!victimsOf.TryGetValue(c, out var vs)) victimsOf[c] = vs = [];
                    vs.Add(pid);
                }
            }

        // Угроза для всей команды: чемпион, который обыгрывает НАШИХ вообще, даже
        // не будучи чьей-то персональной топ-контрой. Считаем по всем показанным
        // пикам сразу — один бан снимает проблему всей команде.
        // С РОЛЯМИ: matchup хранит только противостояние на линии, и без роли
        // угроза складывалась по всем ролям сразу — лесной матчап приписывался
        // топ-лейнеру.
        var teamPicks = protectees
            .Select(p => (p.Id, p.Role))
            .Where(p => p.Id > 0)
            .Distinct()
            .ToList();

        // В счёте участвует весь показанный пул, включая прошлые наведения, — так
        // бан выбирается точнее. А в подписи называем только то, что игрок видит
        // на экране прямо сейчас: иначе «бьёт 5 твоих пиков» при двух пиках в
        // окне выглядит выдумкой.
        var onScreen = state.MyTeam
            .Where(p => p.EffectiveChampionId != 0)
            .Select(p => p.EffectiveChampionId)
            .ToHashSet();

        foreach (var (c, t) in TeamThreats(teamPicks))
        {
            if (taken.Contains(c)) continue;
            scores[c] = scores.GetValueOrDefault(c) + W_BAN_TEAM * t.Threat;

            // Называем поимённо и с цифрами: «силён против Экко (+0,5),
            // Синджед (+3,4)». Прежнее «силён против 2 ваших пиков» не давало
            // ни проверить, ни выбрать между двумя кандидатами.
            //
            // Сильные — первыми: если пострадавших много, обрежется хвост, а не
            // то, ради чего бан и нужен.
            var named = t.Victims
                .Where(onScreen.Contains)
                // Только те, против кого он ДЕЙСТВИТЕЛЬНО силён. Общая угроза
                // складывается из всех матчапов, и среди них попадаются
                // отрицательные — назвать их под словом «силён против» значило
                // бы соврать. Проверка это и поймала: в списке стояло «(-…)».
                .Where(v => t.Edge.GetValueOrDefault(v) > 0)
                .OrderByDescending(v => t.Edge.GetValueOrDefault(v))
                .Select(v => $"{DataDragon.Name(v)} ({Points(t.Edge.GetValueOrDefault(v))})")
                .Take(3)
                .ToList();
            if (t.Threat >= 0.5 && named.Count >= 2)
                AddReason(c, Loc.T("reason.teamThreat", string.Join(", ", named)), R_TEAM);
        }

        // Бонус за ширину: кандидат, контрящий 2+ РАЗНЫХ наших чемпионов,
        // отсекает сразу несколько вариантов пула — именно его стоит банить.
        foreach (var (c, vs) in victimsOf)
            if (vs.Count >= 2)
                // Нелинейно (count^1.4): двое — заметно, трое и больше — сильно.
                scores[c] = scores.GetValueOrDefault(c) + W_BAN_BREADTH * Math.Pow(vs.Count, 1.4);

        // Подписи «кого контрит этот бан». Одного называем как раньше, нескольких
        // перечисляем поимённо — видно, что одним баном закрывается пол-команды.
        // С пикрейтом, как у контры моему пулу; на нескольких линиях — больший.
        string WithPick(int c, IEnumerable<string?> roles, string text)
        {
            var pk = roles.Where(r => r != null).Select(r => pickOn.GetValueOrDefault((c, r!))).DefaultIfEmpty(0).Max();
            return pk > 0 ? Loc.T("reason.withPick", text, $"{pk:F1}") : text;
        }
        foreach (var (c, hits) in hitsOf)
        {
            var roles = hits.Select(h => h.Role);
            if (hits.Count == 1)
            {
                var (id, mine, _) = hits[0];
                AddReason(c, WithPick(c, roles,
                    Loc.T(mine ? "reason.countersMyPick" : "reason.countersAlly", DataDragon.Name(id))),
                    mine ? R_MINE : R_ALLY);
                continue;
            }
            // Свой пик — первым в списке: он важнее чужих.
            var names = hits.OrderByDescending(h => h.Mine).Select(h => DataDragon.Name(h.Id));
            AddReason(c, WithPick(c, roles, Loc.T("reason.countersMany", string.Join(", ", names))),
                      hits.Any(h => h.Mine) ? R_MINE : R_ALLY);
        }

        // Выбор пяти. Бан прежде всего МОЙ: моей линии (контра моему пику и
        // пулу, сила роли) — LANE_SLOTS мест, и стоят они первыми. Защите
        // союзников — не больше TEAM_SLOTS. Раньше «широкие» баны и по контре
        // на каждого наведённого занимали до четырёх мест из пяти: у
        // владельца-саппорта четыре бана были про Ирелию и Мастера Йи и один —
        // про его линию.
        var lane = new List<int>();
        var team = new List<int>();
        bool Chosen(int k) => lane.Contains(k) || team.Contains(k);

        // 1. Моя линия: самая жёсткая контра моему пику (если навёл), затем по очкам линии.
        var laneCap = Math.Min(LANE_SLOTS, top);
        if (me is { EffectiveChampionId: not 0 }
            && bestByProtectee.TryGetValue(me.EffectiveChampionId, out var myBest))
            lane.Add(myBest.Champ);
        foreach (var k in laneScore.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            if (lane.Count >= laneCap) break;
            if (!lane.Contains(k)) lane.Add(k);
        }

        // 2. Команда: сначала «широкие» баны — бьют сразу нескольких наших, один
        //    такой закрывает пол-команды; затем самая жёсткая контра каждому
        //    союзнику по порядку; затем по очкам защиты.
        double TeamPart(int k) => scores.GetValueOrDefault(k) - laneScore.GetValueOrDefault(k);
        var teamOrder = victimsOf
            .Where(v => v.Value.Count >= 2)
            .OrderByDescending(v => v.Value.Count)
            .ThenByDescending(v => scores.GetValueOrDefault(v.Key))
            .Select(v => v.Key)
            .Concat(protectees.Where(p => !p.Mine)
                              .Select(p => bestByProtectee.TryGetValue(p.Id, out var b) ? b.Champ : 0)
                              .Where(c => c != 0))
            .Concat(scores.Keys.Where(k => TeamPart(k) > 0).OrderByDescending(TeamPart));
        var teamCap = Math.Min(TEAM_SLOTS, top - lane.Count);
        foreach (var k in teamOrder)
        {
            if (team.Count >= teamCap) break;
            if (!Chosen(k)) team.Add(k);
        }

        // 3. Добор по общему счёту, если какой-то части не хватило кандидатов, —
        //    но не больше двух банов на одного нашего чемпиона.
        const int maxPerProtectee = 2;
        var perProtectee = new Dictionary<int, int>();
        foreach (var k in lane.Concat(team))
            if (hitsOf.TryGetValue(k, out var hs0))
                foreach (var h in hs0) perProtectee[h.Id] = perProtectee.GetValueOrDefault(h.Id) + 1;
        foreach (var k in scores.Keys.Where(k => !Chosen(k)).OrderByDescending(k => scores[k]))
        {
            if (lane.Count + team.Count >= top) break;
            var hits = hitsOf.GetValueOrDefault(k);
            if (hits is { Count: > 0 } && hits.All(h => perProtectee.GetValueOrDefault(h.Id) >= maxPerProtectee))
                continue;
            (laneScore.GetValueOrDefault(k) >= TeamPart(k) ? lane : team).Add(k);
            if (hits != null)
                foreach (var h in hits) perProtectee[h.Id] = perProtectee.GetValueOrDefault(h.Id) + 1;
        }

        // На экране — сначала моя линия, потом команда; внутри — по очкам.
        return lane.OrderByDescending(k => scores.GetValueOrDefault(k))
            .Concat(team.OrderByDescending(k => scores.GetValueOrDefault(k)))
            .Select(k =>
            {
                // Порядок — по приоритету, внутри него — как добавлялись.
                var rs = (reasons.GetValueOrDefault(k) ?? [])
                    .Select((r, i) => (r.Prio, i, r.Text))
                    .OrderBy(r => r.Prio).ThenBy(r => r.i)
                    .Select(r => r.Text).ToList();
                if (rs.Count == 0) rs.Add(Loc.T("reason.notablePick"));
                return new BanRec(k, scores.GetValueOrDefault(k), [.. rs]);
            })
            .ToList();
    }

    /// <summary>
    /// Встречаемость кандидата в бан: корень из отношения его пикрейта на роли
    /// к среднему пикрейту регулярного пика этой роли, в пределах 0,3…2.
    ///
    /// Корень, а не прямая пропорция: популярный в семь раз не должен в семь
    /// раз перевешивать силу контры — иначе список банов превращается в
    /// список популярных. Средний пикрейт свой у каждой роли (у стрелков пиков
    /// меньше и каждый берут чаще: 3,3% против 1,8% на топе).
    /// </summary>
    private static double Meet(double pickPct, double refPct) =>
        refPct > 0 ? Math.Clamp(Math.Sqrt(pickPct / refPct), 0.3, 2.0) : 1.0;

    /// Пикрейт чемпионов роли за последние патчи (% игр роли, с весами патчей)
    /// и средний пикрейт регулярного пика — тех, кого берут хотя бы в
    /// MIN_ROLE_SHARE игр.
    private (Dictionary<int, double> Pick, double Ref) RolePicks(string role)
    {
        var stats = RoleStats(role);
        var total = stats.Values.Sum(v => v.Games);
        var pick  = stats.ToDictionary(kv => kv.Key, kv => total > 0 ? 100.0 * kv.Value.Games / total : 0.0);
        var regular = pick.Values.Where(p => p >= MIN_ROLE_SHARE * 100).ToList();
        return (pick, regular.Count > 0 ? regular.Average() : 0.0);
    }

    /// Пикрейт чемпионов роли за последние патчи, % игр роли. Песочнице — кого
    /// союзнику «навести» в фазе банов: популярных чаще, как в жизни.
    public IReadOnlyDictionary<int, double> PickRates(string role) => RolePicks(role).Pick;

    // Суммарные (games, wins) по всем кандидатам роли за окно патчей.
    private Dictionary<int, (double Games, double Wins)> RoleStats(string role)
    {
        var result = new Dictionary<int, (double, double)>();
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT champion_id, SUM(games*{PW}), SUM(wins*{PW}) FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id HAVING SUM(games*{PW}) >= @min";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            result[rd.GetInt32(0)] = (rd.GetDouble(1), rd.GetDouble(2));
        return result;
    }

    // Структурные правила синергии джангл↔саппорт (п.4): не из статистики, а из
    // логики команды («агро-джанглеру нужен сетап», «оллин-АДК нужен инициатор»).
    private static (double Bonus, List<string> Reasons) StructuralBonus(
        int champId, string myRole, int jungleAllyId, int adcAllyId, IReadOnlyList<int> allyIds)
    {
        double b = 0;
        var reasons = new List<string>();

        // Тимфайт-вомбо: у кандидата свой мощный AoE/канал-ульт, а у команды уже есть
        // ядро таких же ультов И кто-то умеет собрать/зафиксировать пачку → его ульт
        // добивает всю сгруппированную команду. Классика «стакай ульты на пачку».
        if (TeamSynergies.HasAoeUlt(champId))
        {
            int aoeAllies = allyIds.Count(TeamSynergies.HasAoeUlt);
            if (aoeAllies >= 2 && allyIds.Any(TeamSynergies.IsGrouper))
            {
                b += Math.Min(aoeAllies, 3) * 0.7; // +1.4 … +2.1 (×W_STRUCT)
                reasons.Add(Good(Loc.T("reason.womboAoe")));
            }
        }

        // Союзный джанглер агрессивен, но без своего жёсткого контроля → ему нужен
        // лейнер с CC-сетапом и приоритетом; слабый ранний рушит его план.
        if (jungleAllyId != 0 &&
            ChampionTraits.EarlyPower(jungleAllyId) >= 2 && ChampionTraits.HardCc(jungleAllyId) == 0)
        {
            if (ChampionTraits.HardCc(champId) >= 1)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.jgSetup", DataDragon.Name(jungleAllyId))));
            }
            if (ChampionTraits.EarlyPower(champId) >= 2) b += 0.5;
            if (ChampionTraits.EarlyPower(champId) == 0) b -= 1;
        }

        // Саппорт под стиль АДК.
        if (myRole == "support" && adcAllyId != 0)
        {
            if (ChampionTraits.EngageDependentAdc(adcAllyId) && ChampionTraits.Engage(champId) >= 2)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.adcEngage", DataDragon.Name(adcAllyId))));
            }
            if (ChampionTraits.ScaleAdcCarry(adcAllyId) && ChampionTraits.Peel(champId) >= 2)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.adcScale", DataDragon.Name(adcAllyId))));
            }
        }

        return (b, reasons);
    }

    // Прямой оппонент, когда роли врагов скрыты: враг с НАИБОЛЬШЕЙ долей игр на
    // моей роли (порог 15% — роль явно играбельна для него). Раньше требовалось
    // строгое совпадение с самой частой ролью — у флекс-пиков (Вел'Коз мид/сапп,
    // Пантеон топ/сапп) она другая, и «Vs opponent» молчал нулём.
    private int InferDirectOpponent(DraftState state, string myRole)
    {
        int best = 0;
        double bestShare = 0.15;
        foreach (var p in state.TheirTeam)
        {
            var id = p.EffectiveChampionId;
            if (id == 0) continue;
            // Роль слота известна (позиция из LCU или ручная метка игрока) и это
            // не моя роль — такой враг точно не мой оппонент по линии.
            var known = LcuToDbRole(p.Position);
            if (!string.IsNullOrEmpty(known) && known != myRole) continue;
            var share = RoleShare(id, myRole);
            if (share > bestShare) { bestShare = share; best = id; }
        }
        return best;
    }

    private readonly Dictionary<int, Dictionary<string, double>> _roleShareCache = new();

    /// Доля игр чемпиона на роли (по base_wr, взвешенно по патчам), с кэшем.
    /// Публична: оверлей использует её для авто-раскладки ролей вражеской команды.
    public double RoleShare(int champId, string role)
    {
        if (!_roleShareCache.TryGetValue(champId, out var shares))
        {
            shares = new Dictionary<string, double>();
            try
            {
                var cmd = _db.CreateCommand();
                cmd.CommandText = $@"
                    SELECT role, SUM(games*{PW}) FROM base_wr
                    WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                    GROUP BY role";
                cmd.Parameters.AddWithValue("@c",  champId);
                cmd.Parameters.AddWithValue("@t",  TierBucket);
                cmd.Parameters.AddWithValue("@p1", _p1);
                cmd.Parameters.AddWithValue("@p2", _p2);
                cmd.Parameters.AddWithValue("@p3", _p3);
                double total = 0;
                var raw = new Dictionary<string, double>();
                using var rd = cmd.ExecuteReader();
                while (rd.Read())
                {
                    var g = rd.GetDouble(1);
                    raw[rd.GetString(0)] = g;
                    total += g;
                }
                if (total > 0)
                    foreach (var (r, g) in raw) shares[r] = g / total;
            }
            catch { /* нет данных — пустые доли */ }
            _roleShareCache[champId] = shares;
        }
        return shares.GetValueOrDefault(role, 0.0);
    }

    private readonly Dictionary<int, string> _roleCache = new();

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

    // Самая частая роль чемпиона по числу игр (base_wr), с кэшем.
    // Публична: настройки пула дефолтят роль чемпиона при выборе в связку.
    public string PrimaryRole(int champId) => InferPrimaryRole(champId);

    private string InferPrimaryRole(int champId)
    {
        if (_roleCache.TryGetValue(champId, out var cached)) return cached;
        var role = "";
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT role FROM base_wr
                WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY role ORDER BY SUM(games*{PW}) DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            role = cmd.ExecuteScalar() as string ?? "";
        }
        catch { /* нет данных — пустая роль */ }
        _roleCache[champId] = role;
        return role;
    }

    /// <summary>Строка тир-листа: чемпион, роль, винрейт, игры, пик/бан-рейт, грейд.</summary>
    public readonly record struct TierEntry(
        int ChampionId, string Role, double Winrate, int Games,
        double PickRate, double BanRate, char Grade);

    // Тир-лист показываем только на достаточной выборке (иначе метрики шумят).
    private const int TIER_MIN_GAMES = 250;

    // Грейд не по чистому винрейту, а по META-SCORE — сила (нижняя граница
    // Уилсона) + ПРИСУТСТВИЕ (пик- и бан-рейт). Иначе наверх лезут низкопикрейтные
    // однотрики с раздутым WR, а реальные мета-пики (их все пикают/банят при
    // ~49-51% WR) проваливаются. Веса откалиброваны по нашим данным.
    private const double W_PICK = 1.2;   // вклад пик-рейта в meta (откалибровано по данным)
    private const double W_BAN  = 0.4;   // вклад бан-рейта (бан-рейты крупнее пик-рейтов)
    private const double BAN_CAP = 25.0; // потолок бан-рейта в meta: 79%-бан нового
                                         // чемпиона не должен один тащить его в топ
    private const double TIER_MIN_SHARE = 0.01; // мин. пик-рейт роли (1%) — отсекаем
                                         // почти не пикаемых (новые/офф-мета)

    // Грейд — по РАНГУ meta-score внутри роли: доля чемпионов роли выше данного.
    // Ранговая шкала устойчива к масштабу meta (с банами или без) и всегда даёт
    // спред S..D. Пороги дают «пирамиду»: S ≈ верх 5%, дальше шире.
    private static char GradeOfRank(double frac) =>
        frac < 0.05 ? 'S' : frac < 0.14 ? 'A' : frac < 0.30 ? 'B' : frac < 0.55 ? 'C' : 'D';

    /// <summary>
    /// Тир-лист патча для текущего бакета: топ-N чемпионов на каждой роли,
    /// грейд S..D по рангу. Два режима ранжирования:
    ///   byWinrate=false — по meta-score (сила + присутствие пик/бан) — «тир»;
    ///   byWinrate=true  — по чистому винрейту (нижняя граница Уилсона).
    /// Роли — top→jgl→mid→adc→sup.
    /// </summary>
    public IReadOnlyList<TierEntry> TierList(int perRole = 15, bool byWinrate = false)
    {
        var roles = new[] { "top", "jungle", "mid", "adc", "support" };
        var result = new List<TierEntry>();

        // Тир-лист считаем по ТЕКУЩЕМУ патчу (@p1), без смеси 3 патчей — чтобы WR
        // совпадал с тем, что видят игроки на других сайтах. Смесь остаётся у
        // движка рекомендаций (там выборки тоньше); удержание патча (см. Create)
        // гарантирует, что @p1 уже набрал достаточно данных.
        var banRates = BanRates();

        foreach (var role in roles)
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games) g, SUM(wins) w FROM base_wr
                WHERE role=@r AND tier_bucket=@t AND patch=@p1
                GROUP BY champion_id
                HAVING g >= @min
                   AND g >= @share * (
                       SELECT SUM(games) FROM base_wr
                       WHERE role=@r AND tier_bucket=@t AND patch=@p1)";
            cmd.Parameters.AddWithValue("@r",   role);
            cmd.Parameters.AddWithValue("@t",   TierBucket);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            cmd.Parameters.AddWithValue("@min", TIER_MIN_GAMES);
            cmd.Parameters.AddWithValue("@share", TIER_MIN_SHARE);

            var rows = new List<(int Id, double G, double W)>();
            double roleTotal = 0;
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var g = rd.GetDouble(1);
                    rows.Add((rd.GetInt32(0), g, rd.GetDouble(2)));
                    roleTotal += g;
                }
            if (roleTotal <= 0) continue;

            // Метрики каждого чемпиона роли: сила (LB), присутствие, meta-score.
            var scored = rows.Select(x =>
            {
                double wr   = 100.0 * x.W / x.G;
                double lb   = WilsonLower(x.W, x.G) * 100.0;   // сила (штраф за малую выборку)
                double pick = 100.0 * x.G / roleTotal;          // пик-рейт роли, %
                double ban  = banRates.GetValueOrDefault(x.Id);
                double meta = (lb - 50.0) + W_PICK * pick + W_BAN * Math.Min(ban, BAN_CAP);
                return (x.Id, wr, Games: (int)Math.Round(x.G), pick, ban, lb, meta);
            })
            // Ключ ранжирования = то, что показываем: в WR-столбце сортируем по
            // сырому винрейту (иначе выше по %-у стоит ниже — путаница), в тире
            // по meta-score. Мелкие выборки уже отсечены порогом TIER_MIN_GAMES.
            .OrderByDescending(s => byWinrate ? s.wr : s.meta)
            .ToList();

            // Грейд — по рангу в роли; показываем top-N (уже отсортированы).
            for (int i = 0; i < scored.Count && i < perRole; i++)
            {
                var s = scored[i];
                char grade = GradeOfRank((double)i / scored.Count);
                result.Add(new TierEntry(s.Id, role, s.wr, s.Games, s.pick, s.ban, grade));
            }
        }
        return result;
    }

    /// Патч, по которому считаются тир-листы и бан-рейт (с учётом удержания).
    public string Patch => _p1;

    private Dictionary<int, double>? _banRates;

    /// <summary>
    /// Бан-рейт в текущем патче: в скольких процентах матчей бакета чемпиона
    /// банят. Знаменатель — число матчей, сумма участников base_wr / 10.
    ///
    /// Кэш на жизнь движка: движок пересобирается при смене базы и бакета.
    /// </summary>
    public IReadOnlyDictionary<int, double> BanRates()
    {
        if (_banRates is { } cached) return cached;
        var matches = ScalarD(@"
            SELECT COALESCE(SUM(games),0)/10.0 FROM base_wr
            WHERE tier_bucket=@t AND patch=@p1");
        var bans = BansByChampion();
        return _banRates = matches > 0
            ? bans.ToDictionary(kv => kv.Key, kv => 100.0 * kv.Value / matches)
            : new Dictionary<int, double>();
    }

    /// Бан-рейт одного чемпиона в текущем патче, %. Нет данных — 0.
    public double BanRate(int champId) => BanRates().GetValueOrDefault(champId);

    // Чемпион попадает в роль тир-листа банов, если на ней четверть его игр и больше.
    private const double BAN_ROLE_SHARE = 0.25;

    /// <summary>
    /// Тир-лист банов: на каждой роли — кого банят чаще всего в текущем патче.
    ///
    /// Банят чемпиона, а не чемпиона на роли, поэтому роль ему даём по играм:
    /// он стоит там, где играется хотя бы в четверти своих игр, и с ПОЛНЫМ
    /// бан-рейтом — тем, что видит игрок. Делить баны по долям ролей значило бы
    /// показывать «Ясуо 6%» на топе при 25% на деле. Без порога вышло бы как в
    /// мета-тир-листе, где Ясуо с 1% игр на боте стоит в топе стрелков.
    /// </summary>
    public IReadOnlyList<TierEntry> BanTierList(int perRole = 15)
    {
        var rates = BanRates();
        var rows  = new List<(int Id, string Role, double G, double W)>();
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT champion_id, role, SUM(games), SUM(wins) FROM base_wr
                WHERE tier_bucket=@t AND patch=@p1
                GROUP BY champion_id, role";
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                rows.Add((rd.GetInt32(0), rd.GetString(1), rd.GetDouble(2), rd.GetDouble(3)));
        }
        catch { return []; }

        var champTotal = rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Sum(r => r.G));
        var roleTotal  = rows.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.Sum(r => r.G));

        var result = new List<TierEntry>();
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            var list = rows
                .Where(r => r.Role == role && r.G >= TIER_MIN_GAMES
                            && r.G >= BAN_ROLE_SHARE * champTotal[r.Id]
                            && rates.GetValueOrDefault(r.Id) > 0)
                .OrderByDescending(r => rates.GetValueOrDefault(r.Id))
                .ToList();
            for (int i = 0; i < list.Count && i < perRole; i++)
            {
                var r = list[i];
                result.Add(new TierEntry(r.Id, role, 100.0 * r.W / r.G, (int)Math.Round(r.G),
                                         100.0 * r.G / roleTotal[role], rates.GetValueOrDefault(r.Id),
                                         GradeOfRank((double)i / list.Count)));
            }
        }
        return result;
    }

    // Взвешенные баны по чемпиону (для presence). Таблицы может не быть в старой
    // базе — тогда пусто, бан-рейт = 0 (тир-лист опирается на пик-рейт).
    private Dictionary<int, double> BansByChampion()
    {
        var map = new Dictionary<int, double>();
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(bans) FROM champion_bans
                WHERE tier_bucket=@t AND patch=@p1 GROUP BY champion_id";
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) map[rd.GetInt32(0)] = rd.GetDouble(1);
        }
        catch { /* нет таблицы — presence только по пик-рейту */ }
        return map;
    }

    private double ScalarD(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        var v = cmd.ExecuteScalar();
        return v is null or DBNull ? 0.0 : Convert.ToDouble(v);
    }

    /// <summary>Ключ локализации имени бакета (silver/gold/emerald/master).</summary>
    public string TierBucketLocKey => $"bucket.{TierBucket}";

    // ---------- Запросы к БД — агрегируют по 3 патчам ----------

    private List<int> GetCandidates(string role)
    {
        var cmd = _db.CreateCommand();
        // Взвешенная сумма игр по 3 патчам: кандидат проходит по абсолютному порогу
        // И по доле игр роли (отсекает редкие офф-мета пики вроде Амуму-саппорта).
        cmd.CommandText = $@"
            SELECT champion_id FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id
            HAVING SUM(games*{PW}) >= @min
               AND SUM(games*{PW}) >= @share * (
                   SELECT SUM(games*{PW}) FROM base_wr
                   WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3))";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        cmd.Parameters.AddWithValue("@share", MIN_ROLE_SHARE);
        var list = new List<int>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetInt32(0));
        return list;
    }

    private (double g, double w) RawBase(int champId, string role)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM base_wr
            WHERE champion_id=@c AND role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // Сырые (games, wins) матчапа — направленно: мой чемпион против vsId.
    // Есть ли в базе сводка по дивизионам (старые базы её не знают).
    private bool? _hasPool;
    private bool HasPool
    {
        get
        {
            if (_hasPool is { } v) return v;
            try
            {
                var c = _db.CreateCommand();
                c.CommandText = "SELECT 1 FROM matchup WHERE tier_bucket='all' LIMIT 1";
                _hasPool = c.ExecuteScalar() is not null;
            }
            catch { _hasPool = false; }
            return _hasPool.Value;
        }
    }

    // Сводка спрашивается по разу на пару за драфт, поэтому держим её в памяти:
    // кандидатов до десяти, врагов до пяти, и каждый пересчёт лез бы в базу заново.
    private readonly Dictionary<string, (double g, double w)> _poolCache = new();

    private (double g, double w) PoolRow(string table, string where, params (string, object)[] args)
    {
        var key = table + "|" + string.Join(",", args.Select(a => a.Item2));
        if (_poolCache.TryGetValue(key, out var hit)) return hit;
        var res = (0.0, 0.0);
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM {table}
                                 WHERE {where} AND tier_bucket='all' AND patch='all'";
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            res = RawAgg(cmd);
        }
        catch { /* нет сводки — работаем как раньше */ }
        _poolCache[key] = res;
        return res;
    }

    /// Подмешивает сводку по дивизионам к паре из своего бакета.
    private (double g, double w) WithPool((double g, double w) own, (double g, double w) pool)
    {
        if (pool.g < POOL_MIN) return own;         // сводки нет или она сама мала
        var rate = pool.w / pool.g;
        return (own.g + POOL_PRIOR, own.w + POOL_PRIOR * rate);
    }

    private (double g, double w) RawMatchup(int champId, string role, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM matchup
            WHERE champion_id=@c AND role=@r AND vs_champion_id=@v
              AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        var own = RawAgg(cmd);
        if (!HasPool) return own;
        return WithPool(own, PoolRow("matchup",
            "champion_id=@c AND role=@r AND vs_champion_id=@v",
            ("@c", champId), ("@r", role), ("@v", vsId)));
    }

    // Кросс-ролевой матчап на боте: мой чемпион (role) против вражеского дуо-партнёра
    // (vsRole). Защищено try/catch — в старых базах таблицы может не быть.
    private (double g, double w) RawBotlane(int champId, string role, int vsId, string vsRole)
    {
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM botlane_matchup
                WHERE champion_id=@c AND role=@r AND vs_champion_id=@v AND vs_role=@vr
                  AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@v",  vsId);
            cmd.Parameters.AddWithValue("@vr", vsRole);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            var own = RawAgg(cmd);
            if (!HasPool) return own;
            return WithPool(own, PoolRow("botlane_matchup",
                "champion_id=@c AND role=@r AND vs_champion_id=@v AND vs_role=@vr",
                ("@c", champId), ("@r", role), ("@v", vsId), ("@vr", vsRole)));
        }
        catch { return (0, 0); }
    }

    /// <summary>
    /// Сырые (games, wins) синергии. Обе стороны пары суммируются (champ+ally и
    /// ally+champ) — синергия симметрична, а записана она одной строкой.
    ///
    /// Роль союзника важна не меньше своей: «Ясуо на миде» и «Ясуо в боте» —
    /// разные пары, и складывать их значит усреднять то, что вместе не
    /// встречается. Замер по базе: у 11% пар число переезжает через порог
    /// кружка (+0,3), в крайних случаях разница доходит до 11 пп. Объём при
    /// этом почти не страдает — в медиане маржинал шире всего в 1,13 раза.
    ///
    /// Роль союзника неизвестна (ARAM, блайнд) или по ней пусто — откат на
    /// сумму по всем его ролям: приблизительно, но лучше, чем промолчать.
    /// </summary>
    private (double g, double w) RawSynergy(int champId, string role, int allyId,
                                            string? allyRole = null)
    {
        // Откат решаем по СВОИМ играм, а не по итогу с приором: приор добавляет
        // 25 псевдо-игр и сам по себе делает выборку «непустой». Пара, у
        // которой на этой паре линий не сыграно ничего, должна уходить на сумму
        // по ролям, а не жить на одной ставке.
        if (!string.IsNullOrEmpty(allyRole) && allyRole != role
            && SynergyOwn(champId, role, allyId, allyRole).g > 0)
            return SynergyRows(champId, role, allyId, allyRole);
        return SynergyRows(champId, role, allyId, null);
    }

    private (double g, double w) SynergyRows(int champId, string role, int allyId, string? allyRole)
    {
        var own = SynergyOwn(champId, role, allyId, allyRole);
        if (!HasPool) return own;

        // Сводка по дивизионам — это ПРИОР, то есть ставка на исход пары.
        // Берём её по той же паре ролей; если такой сводки мало (роль редкая),
        // ставку делает более широкая — лучше грубый приор, чем никакого.
        var pool = PoolPair(champId, role, allyId, allyRole);
        if (allyRole is not null && pool.g < POOL_MIN) pool = PoolPair(champId, role, allyId, null);
        return WithPool(own, pool);
    }

    // Игры пары в СВОЁМ бакете, без сводки по дивизионам.
    private (double g, double w) SynergyOwn(int champId, string role, int allyId, string? allyRole)
    {
        var byAlly = allyRole is not null;
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@c AND role=@r AND ally_id=@a
                    {(byAlly ? "AND ally_role=@ar" : "")}
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@a AND ally_id=@c AND ally_role=@r
                    {(byAlly ? "AND role=@ar" : "")}
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        if (byAlly) cmd.Parameters.AddWithValue("@ar", allyRole);
        return RawAgg(cmd);
    }

    // Сводка по дивизионам для пары — обе стороны записи, как и сам запрос выше.
    private (double g, double w) PoolPair(int champId, string role, int allyId, string? allyRole)
    {
        var byAlly = allyRole is not null;
        var a = byAlly
            ? PoolRow("synergy", "champion_id=@c AND role=@r AND ally_id=@a AND ally_role=@ar",
                      ("@c", champId), ("@r", role), ("@a", allyId), ("@ar", allyRole!))
            : PoolRow("synergy", "champion_id=@c AND role=@r AND ally_id=@a",
                      ("@c", champId), ("@r", role), ("@a", allyId));
        var b = byAlly
            ? PoolRow("synergy", "champion_id=@a AND ally_id=@c AND ally_role=@r AND role=@ar",
                      ("@a", allyId), ("@c", champId), ("@r", role), ("@ar", allyRole!))
            : PoolRow("synergy", "champion_id=@a AND ally_id=@c AND ally_role=@r",
                      ("@a", allyId), ("@c", champId), ("@r", role));
        return (a.g + b.g, a.w + b.w);
    }

    // Синергия пары с ОБЕИМИ известными ролями (обе стороны записи). Точнее, чем
    // RawSynergyAny, но выборка меньше — вызывающий делает откат при нуле игр.
    private (double g, double w) RawSynergyRoles(int m, string mRole, int f, string fRole)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@m AND role=@mr AND ally_id=@f AND ally_role=@fr
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@f AND role=@fr AND ally_id=@m AND ally_role=@mr
                    AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@m",  m);
        cmd.Parameters.AddWithValue("@mr", mRole);
        cmd.Parameters.AddWithValue("@f",  f);
        cmd.Parameters.AddWithValue("@fr", fRole);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    private static (double g, double w) RawAgg(SqliteCommand cmd)
    {
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return (0, 0);
        return (rd.GetDouble(0), rd.GetDouble(1));
    }

    // Лаплас-дельта в процентных пунктах относительно 50%.
    /// Роли в том виде, в каком они лежат в базе. Нужны, чтобы подставлять роль
    /// в запрос можно было без опаски: список закрытый, чужого сюда не попадёт.
    private static readonly HashSet<string> DbRoles =
        new(StringComparer.Ordinal) { "top", "jungle", "mid", "adc", "support" };

    private static double Delta(double g, double w, double k) => ((w + k / 2.0) / (g + k) - PRIOR) * 100;

    /// Очки перевеса со знаком — в том же виде, что на карточках драфта.
    private static string Points(double v) => (v >= 0 ? "+" : "") + v.ToString("F1");

    // Нижняя граница доверительного интервала Уилсона для доли побед: штрафует
    // малые выборки, поэтому редкие пары не всплывают как «контра»/«синергия».
    private static double WilsonLower(double wins, double games)
    {
        if (games <= 0) return 0;
        double p = wins / games, z = WILSON_Z, z2 = z * z;
        double centre = p + z2 / (2 * games);
        double margin = z * Math.Sqrt(p * (1 - p) / games + z2 / (4 * games * games));
        return (centre - margin) / (1 + z2 / games);
    }

    // (champion_id, games, wins) → ранг по нижней границе Уилсона. minEdge отсекает
    // «неуверенные» пары (для контр); для синергии minEdge<0 — просто топ-N по LB.
    /// «Кто бьёт нашу команду в целом»: для каждого чемпиона — насколько он играет
    /// против НАШИХ лучше, чем против всех подряд, и скольких наших это покрывает.
    /// Отличается от списка контрпиков конкретного игрока: чемпион может не быть
    /// топ-контрой ни для кого по отдельности, но стабильно обыгрывать всю
    /// команду — такой бан помогает всем сразу.
    // Возвращает по каждому кандидату: насколько он опасен нашей команде и КОГО
    // именно из неё бьёт. Список нужен подписи: в счёт идут не только пики на
    // экране, но и чемпионы, которых союзники наводили раньше, — писать про них
    // «твои пики» нельзя, игрок их уже не видит.
    /// <summary>
    /// Чем кандидат опасен для нашей команды: общая угроза, список пострадавших
    /// и насколько он бьёт КАЖДОГО из них.
    ///
    /// Разбивка по именам нужна подписи в банах: «силён против 2 ваших пиков» —
    /// число без имён, по нему нельзя ни проверить, ни решить. Сила указана в
    /// тех же очках, что на карточках драфта.
    /// </summary>
    private sealed record TeamThreat(double Threat, List<int> Victims, Dictionary<int, double> Edge);

    private Dictionary<int, TeamThreat> TeamThreats(
        IReadOnlyCollection<(int Id, string? Role)> allies)
    {
        var res = new Dictionary<int, TeamThreat>();
        var allyIds = allies.Select(a => a.Id).Where(x => x > 0).Distinct().ToList();
        if (allyIds.Count < 2) return res;   // «командная» угроза начинается с двоих
        try
        {
            // Роль союзника сужает выборку до его линии. Роли может не быть
            // (ranked solo/duo, блайнд) — тогда берём по всем, как раньше:
            // приблизительно, но лучше, чем молчать.
            var parts = new List<string>();
            foreach (var (id, role) in allies)
            {
                if (id <= 0) continue;
                if (!string.IsNullOrEmpty(role) && DbRoles.Contains(role))
                    parts.Add($"(vs_champion_id = {id} AND role = '{role}')");
                else
                    parts.Add($"vs_champion_id = {id}");
            }
            if (parts.Count == 0) return res;
            var ids = string.Join(" OR ", parts);

            // Строка на КАЖДУЮ пару, а не одна на кандидата: сумму обратно по
            // именам не разложить, а подпись именно этого и требует. Складываем
            // сами, тем же способом, чтобы порядок банов не изменился.
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, vs_champion_id,
                       SUM(games*{PW}) AS g,
                       SUM(wins*{PW})  AS w
                FROM   matchup
                WHERE  ({ids}) AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id, vs_champion_id";
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);

            var rows = new Dictionary<int, List<(int Vs, double G, double W)>>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var id = rd.GetInt32(0);
                    var vs = rd.GetInt32(1);
                    if (vs <= 0) continue;
                    if (!rows.TryGetValue(id, out var list)) rows[id] = list = [];
                    list.Add((vs, Convert.ToDouble(rd.GetValue(2)), Convert.ToDouble(rd.GetValue(3))));
                }

            foreach (var (id, list) in rows)
            {
                var victims = list.Select(r => r.Vs).Distinct().ToList();
                var n = victims.Count;
                var g = list.Sum(r => r.G);
                var w = list.Sum(r => r.W);
                if (n < 2 || g < 150) continue;      // прежние HAVING, теперь у нас

                // Чистая угроза: WR против наших минус собственный средний WR —
                // иначе наверх лезли бы просто сильные чемпионы патча.
                var (bg, bw) = RawBaseAny(id);
                if (bg <= 0) continue;
                var mine = Delta(bg, bw, K);
                var threat = (Delta(g, w, K_PAIR) - mine) * (g / (g + MATCHUP_CONF));
                if (threat <= 0) continue;

                // По каждому пострадавшему — тот же перевес, только по его паре.
                var edge = new Dictionary<int, double>();
                foreach (var r in list)
                    if (r.G > 0) edge[r.Vs] = Delta(r.G, r.W, K_PAIR) - mine;

                // Масштабируем по охвату: бьёт троих — весомее, чем двоих.
                res[id] = new TeamThreat(threat * n / allyIds.Count, victims, edge);
            }
        }
        catch { /* нет данных — фактор молчит */ }
        return res;
    }

    // Одна строка подсказки: кандидат, объём пары и её винрейт.
    private readonly record struct HintRow(int Id, double Games, double Wins)
    {
        public double Wr => Games > 0 ? Wins / Games : 0;
    }

    private static List<HintRow> ReadHintRows(SqliteCommand cmd)
    {
        var rows = new List<HintRow>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new HintRow(rd.GetInt32(0),
                                 Convert.ToDouble(rd.GetValue(1)),
                                 Convert.ToDouble(rd.GetValue(2))));
        return rows;
    }

    private static List<int> RankByWilson(SqliteCommand cmd, int top, double minEdge = HINT_MIN_EDGE) =>
        ReadHintRows(cmd)
            .Select(r => (r.Id, Lb: WilsonLower(r.Wins, r.Games)))
            .Where(x => x.Lb > minEdge)
            .OrderByDescending(x => x.Lb)
            .Take(top).Select(x => x.Id).ToList();

    /// <summary>
    /// Подзапрос «кто эту роль реально играет»: тот же отбор, что у кандидатов
    /// подбора (абсолютный порог игр И доля роли). Парные таблицы его не знают
    /// и хранят что угодно, включая Сивир на миде на пятнадцати играх, — а
    /// такой «контрпик» ни выбрать нельзя, ни проверить.
    /// </summary>
    private string RolePlayersSql => $@"
        SELECT champion_id FROM base_wr
        WHERE  role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
        GROUP  BY champion_id
        HAVING SUM(games*{PW}) >= @roleMin
           AND SUM(games*{PW}) >= @roleShare * (
               SELECT SUM(games*{PW}) FROM base_wr
               WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3))";

    private void AddRolePlayerParams(SqliteCommand cmd)
    {
        cmd.Parameters.AddWithValue("@t",         TierBucket);
        cmd.Parameters.AddWithValue("@roleMin",   MIN_GAMES);
        cmd.Parameters.AddWithValue("@roleShare", MIN_ROLE_SHARE);
    }

    /// <summary>
    /// Топ N чемпионов той же роли, лучше всего контрящих данного врага.
    /// Если роль неизвестна — запрос по всем ролям. minGames/minEdge можно
    /// ослабить (защитные баны: данные по паре разрежены, а показать надо).
    /// </summary>
    /// <param name="fill">
    /// Добрать до N, если по строгим порогам набралось меньше. Нужно боковым
    /// подсказкам: там место под три иконки, и «одна вместо трёх» читается как
    /// поломка. Добор берёт следующих по Уилсону, но только с настоящим
    /// перевесом (WR выше половины) — иначе это был бы уже не контрпик.
    /// </param>
    public IReadOnlyList<int> TopCounters(int enemyId, string? enemyRole = null, int top = 3,
                                          int? minGames = null, double? minEdge = null,
                                          bool fill = false)
    {
        if (enemyId == 0) return [];
        try
        {
            var byRole = !string.IsNullOrEmpty(enemyRole);
            var cmd = _db.CreateCommand();
            // По всем дивизионам сразу: данные по парам разрежены, фильтр по бакету
            // добил бы выборку. Ранжируем/фильтруем по Уилсону уже в C#.
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}) AS g, SUM(wins*{PW}) AS w
                FROM   matchup
                WHERE  vs_champion_id = @v AND patch IN (@p1, @p2, @p3)
                       {(byRole ? "AND role = @r AND champion_id IN (" + RolePlayersSql + ")" : "")}
                GROUP  BY champion_id";
            cmd.Parameters.AddWithValue("@v",   enemyId);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            if (byRole) { cmd.Parameters.AddWithValue("@r", enemyRole); AddRolePlayerParams(cmd); }

            var rows = ReadHintRows(cmd);
            double gMin = minGames ?? HINT_MIN_GAMES, eMin = minEdge ?? HINT_MIN_EDGE;
            var strict = rows.Where(r => r.Games >= gMin && WilsonLower(r.Wins, r.Games) > eMin)
                             .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                             .Select(r => r.Id);
            if (!fill) return strict.Take(top).ToList();

            // Сначала те, у кого перевес настоящий…
            var winning = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr > 0.50)
                              .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                              .Select(r => r.Id);
            // …и только потом ровные пары. Против чемпиона вроде Джинкс роль
            // может не знать НИ ОДНОГО победного матчапа: лучший ответ там —
            // тот, кто не проигрывает. Ниже этой границы уже проигрыш, и
            // называть его контрой нельзя даже ради третьей иконки.
            var even = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr >= HINT_EVEN_WR)
                           .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                           .Select(r => r.Id);
            return strict.Concat(winning).Concat(even).Distinct().Take(top).ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Топ N чемпионов МОЕЙ роли с наилучшей синергией с данным союзником.
    /// </summary>
    /// <param name="allyRole">
    /// Роль союзника, если известна. Без неё считается сумма по всем его ролям,
    /// а это разные пары: у Владимира на топе в лучших напарниках-саппортах
    /// Бард и Тарик, у него же на миде — Блицкранк и Рената.
    /// </param>
    public IReadOnlyList<int> TopSynergies(int allyId, string myRole, int top = 3,
                                           string? allyRole = null)
    {
        if (allyId == 0 || string.IsNullOrEmpty(myRole)) return [];
        // Своя роль союзнику не подходит: двоих на одной линии не бывает, и
        // сумма по такой паре собрана из случайных флексов.
        if (allyRole == myRole) allyRole = null;

        var byRole = ByAllyRole(allyId, myRole, top, allyRole);
        if (allyRole is null || byRole.Count >= top) return byRole;
        // Роль союзника известна, но пары по ней разрежены — добираем общими.
        return byRole.Concat(ByAllyRole(allyId, myRole, top, null))
                     .Distinct().Take(top).ToList();
    }

    private List<int> ByAllyRole(int allyId, string myRole, int top, string? allyRole)
    {
        try
        {
            var cmd = _db.CreateCommand();
            // Синергия по всем дивизионам (данные разрежены), порог игр ниже и без
            // фильтра edge — чтобы стабильно заполнять 3 лучших партнёра по LB.
            // Кандидаты — только те, кто мою роль правда играет: без этого в
            // напарники к Фиддлстиксу на топ выходил Дрэйвен на двенадцати играх.
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}) AS g, SUM(wins*{PW}) AS w
                FROM   synergy
                WHERE  ally_id = @a AND role = @r AND patch IN (@p1, @p2, @p3)
                       {(allyRole is null ? "" : "AND ally_role = @ar")}
                       AND champion_id IN ({RolePlayersSql})
                GROUP  BY champion_id
                HAVING SUM(games*{PW}) >= @min";
            cmd.Parameters.AddWithValue("@a",   allyId);
            cmd.Parameters.AddWithValue("@min", HINT_MIN_SYN);
            cmd.Parameters.AddWithValue("@r",   myRole);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            if (allyRole is not null) cmd.Parameters.AddWithValue("@ar", allyRole);
            AddRolePlayerParams(cmd);
            return RankByWilson(cmd, top, minEdge: -1.0);
        }
        catch { return []; }
    }

    // ---------- Обоснование ----------

    private static string[] BuildReasons(
        int    champId,
        double directDelta, int    directOppId,
        double synDelta,    IReadOnlyList<(int Id, string Role, double Delta)> synByAlly,
        double otherDelta,  IReadOnlyList<(int Id, double Delta)> otherByEnemy,
        double baseDelta,   double comfortDelta)
    {
        var lines = new List<string>();

        // 0. Комфорт: часто наигранный чемпион игрока — упоминаем первым.
        if      (comfortDelta >= 3.8) lines.Add(Good(Loc.T("reason.comfortHigh")));
        else if (comfortDelta >= 1.9) lines.Add(Good(Loc.T("reason.comfortMid")));

        // 1. Развёрнутые объяснения синергии с конкретными союзниками.
        // Сначала пары с наибольшей статистической синергией. Дедупим по ТИПУ
        // связки — чтобы не повторять одинаковые по смыслу фразы. До 2 объяснений.
        var seenKinds = new HashSet<string>();
        foreach (var a in synByAlly.OrderByDescending(x => x.Delta))
        {
            var ex = TeamSynergies.ExplainPair(champId, a.Id, a.Role);
            if (ex is null) continue;
            if (!seenKinds.Add(ex.Value.Kind)) continue; // тот же тип связки уже показан
            // Помечаем как довод «за» и как несменяемый: без знака интерфейс
            // считал связку нейтральной и выбрасывал её первой же строкой
            // отбора. Владелец на это и наткнулся: Мальфит с Ясуо в команде,
            // а про подброс с ультом в карточке ни слова.
            // Со ЦИФРОЙ: сколько связка даёт на самом деле. Без неё строка
            // обещает выгоду, а насколько — не говорит; остальные доводы в
            // карточке числа приводят, и связка выглядела голословной.
            //
            // Цифру показываем, только когда она заметна. У пары может не
            // оказаться игр (свежий чемпион) или перевес может быть нулевым:
            // комбо при этом настоящее, просто данные его не выделяют, и
            // приписывать «+0,0%» значило бы делать вид, что померили точнее,
            // чем померили. Отрицательную не пишем тем более — довод «за» с
            // минусом читается как ошибка.
            lines.Add(Key(a.Delta >= 0.3
                ? $"{ex.Value.Text} ({Points(a.Delta)}%)"
                : ex.Value.Text));
            if (seenKinds.Count >= 2) break;
        }
        var explained = seenKinds.Count;

        // 2. Матчап с прямым оппонентом. Пороги под ЧИСТУЮ дельту (матчап минус
        // собственная база): типичная сильная контра теперь +2..+5пп, не +5..+10.
        if (directOppId != 0)
        {
            var oppName = DataDragon.Name(directOppId);
            if      (directDelta >=  2.0) lines.Add(Good(Loc.T("reason.lineWinStrong", oppName, $"{directDelta:F1}")));
            else if (directDelta >=  1.0) lines.Add(Good(Loc.T("reason.lineWin", oppName, $"{directDelta:F1}")));
            else if (directDelta >=  0.3) lines.Add(Good(Loc.T("reason.lineEdge", oppName)));
            else if (directDelta <= -1.5) lines.Add(Bad(Loc.T("reason.lineHard", oppName, $"{directDelta:F1}")));
            else                          lines.Add(Loc.T("reason.lineNeutral", oppName));
        }

        // 3. Выгодные матчапы против конкретных врагов (чистые дельты — порог ниже)
        if (otherByEnemy.Count > 0)
        {
            var good = otherByEnemy.Where(x => x.Delta >= 1.0)
                                   .OrderByDescending(x => x.Delta).Take(2).ToList();
            if (good.Count > 0)
            {
                var names = string.Join(", ", good.Select(x => DataDragon.Name(x.Id)));
                lines.Add(Good(Loc.T("reason.favMatchups", names)));
            }
        }

        // 4. Если понятных объяснений не нашлось, но статистика хорошая —
        // мягкая общая фраза вместо сухих процентов.
        if (explained == 0 && synByAlly.Count > 0 && synDelta > 0.5)
            lines.Add(Good(Loc.T("reason.fitsTeam")));

        // 5. Базовый WR
        if      (baseDelta >=  2.5) lines.Add(Good(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}")));
        else if (baseDelta >=  1.0) lines.Add(Good(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}")));
        else if (baseDelta <= -1.5) lines.Add(Bad(Loc.T("reason.baseLow", $"{50 + baseDelta:F1}")));

        if (lines.Count == 0) lines.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));
        return [.. lines];
    }

    public void Dispose() => _db.Dispose();
}
