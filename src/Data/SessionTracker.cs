using System.IO;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Трекер сессии для экрана ожидания: ник, ранги, последние игры и винрейт —
/// РАЗДЕЛЬНО по четырём очередям (Solo/Duo, Flex, Normal, ARAM): кто-то играет
/// только нормалы или только ARAM. LP за игру и историю винрейта LCU не отдаёт —
/// трекер ведёт собственный журнал игр между запусками
/// (%APPDATA%\Counterplay\session.json). Ключ Riot не нужен — только LCU.
/// </summary>
public static class SessionTracker
{
    public sealed record RecentGame(int ChampionId, bool Win, int? LpDelta);
    public sealed record WrPoint(DateTime Date, double Winrate);

    /// Точка графика рейтинга: абсолютный LP (тир*400 + дивизион*100 + очки) —
    /// в такой шкале дивизионы ложатся ровными полосами по 100.
    public sealed record LpPoint(DateTime Date, int AbsLp);

    /// Статистика одной очереди для панели трекера.
    public sealed record QueueView(
        bool HasRank, string Tier, string Division, int Lp, int ProgressPct,
        int Wins, int Losses, double Winrate,
        IReadOnlyList<RecentGame> Last5,
        IReadOnlyList<WrPoint> WinrateHistory,
        IReadOnlyList<LpPoint> RatingHistory);

    public sealed record SessionData(string Nick, string SelectedQueue, IReadOnlyDictionary<string, QueueView> Queues);

    /// Ключи очередей в порядке выпадающего списка.
    public static readonly string[] QueueKeys = ["solo", "flex", "normal", "aram"];

    /// Больше этого за одну игру LP не меняется (даже с промо/демоушеном).
    /// Всё, что выше — признак смены аккаунта или сброса сезона, а не результата.
    private const int MaxLpPerGame = 100;

    /// Сколько игр нужно, чтобы показывать винрейт и график. По одной-двум играм
    /// «100%» и «0%» — не статистика, а шум, который вдобавок портит масштаб.
    public const int MinGamesForChart = 5;

    /// Окно графика: три месяца. Прошлогодняя форма ничего не говорит о
    /// сегодняшней, а журнал не растёт без предела.
    /// Сколько истории рейтинга отдаём наружу. Окно показа (30 или 90 дней)
    /// выбирается в настройках, поэтому здесь держим максимум — иначе переключатель
    /// «90 дней» не смог бы показать больше, чем ему дали.
    private const int RatingDays = 90;

    public const int ChartDays = 90;

    /// Сколько «виртуальных» игр по 50% подмешивать в винрейт для графика.
    /// Это не подгонка цифры, а признание того, что по трём играм винрейт
    /// неизвестен: точка стартует у середины и расходится по мере реальных игр.
    private const int PriorGames = 6;

    /// Винрейт со сглаживанием к 50%.
    private static double SmoothWr(int wins, int games) =>
        100.0 * (wins + PriorGames * 0.5) / (games + PriorGames);

    // queueId LCU → наш ключ очереди (400 драфт / 430 блайнд / 490 квикплей = normal).
    internal static string? QueueOf(int queueId) => queueId switch
    {
        420                => "solo",
        440                => "flex",
        400 or 430 or 490  => "normal",
        450                => "aram",
        _                  => null,
    };

    // ── Журнал игр (персистентный) ─────────────────────────────────────────
    private sealed class GameLog
    {
        public long Ts { get; set; }            // unix seconds
        public int ChampionId { get; set; }
        public bool Win { get; set; }
        public int? Lp { get; set; }            // LP-дельта (только ранкед-очереди)
        public double Wr { get; set; }          // винрейт очереди на момент игры
    }

    private sealed class QueueLog
    {
        public int LastAbsLp { get; set; } = int.MinValue;
        public List<GameLog> Games { get; set; } = [];

        // Опорная точка графика для ранговых очередей: сезонный винрейт на
        // момент первого подключения. Клиент знает его сразу, поэтому линии не
        // нужно ждать пять игр — она начинается с реального значения игрока.
        public long AnchorTs { get; set; }
        public double AnchorWr { get; set; }
    }

    private sealed class RankedCache
    {
        public bool HasRank { get; set; }
        public string Tier { get; set; } = "";
        public string Div { get; set; } = "";
        public int Lp { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
    }

    /// <summary>
    /// Данные ОДНОГО аккаунта. Раньше всё лежало в общей куче, и при смене
    /// аккаунта: ник не обновлялся, а LP-дельта считалась между рангами разных
    /// аккаунтов (эмеральд → золото давало «−492 LP» за первую же игру).
    /// </summary>
    private sealed class Account
    {
        public string? Nick { get; set; }
        public string SelectedQueue { get; set; } = "solo";
        public bool QueueChosen { get; set; }
        public long LastGameId { get; set; }
        /// <summary>
        /// Номера уже засчитанных игр. Раньше хватало одного «последнего»
        /// номера: считалось, что новая игра — это та, у которой номер больше.
        /// Оказалось, номера НЕ растут со временем. 22.09 игра, начатая в 10:27,
        /// получила номер 7991423371, а предыдущая, в 09:51, — 7991430692:
        /// победа просто не засчиталась, и перезапуск не помогал, потому что
        /// отметка лежит на диске. Поэтому помним сами номера, а порядок берём
        /// по времени начала игры.
        /// </summary>
        public List<long> SeenGames { get; set; } = new();
        public Dictionary<string, QueueLog> Queues { get; set; } = new();
        // Последний успешный снимок рангов: LCU после рестарта/обновления может
        // не ответить — панель всё равно рендерится из кэша, а не пустой.
        public Dictionary<string, RankedCache> Ranked { get; set; } = new();

        /// <summary>
        /// Винрейт связки: сколько игр на КОНКРЕТНОЙ паре «мой чемпион + его
        /// чемпион» сыграно именно с этим человеком и сколько из них выиграно.
        ///
        /// Ключ — <c>puuid|мой чемпион|его чемпион</c>. Напарник опознаётся по
        /// puuid, а не по нику: ник меняется, puuid — нет.
        ///
        /// Копится только вперёд. История LCU отдаёт около двадцати последних
        /// игр и глубже не пускает (проверено: запросы с begIndex 100 и 200
        /// возвращают тот же список), так что задним числом взять неоткуда.
        /// </summary>
        public Dictionary<string, PairRec> Pairs { get; set; } = new();

        /// <summary>
        /// Игры, уже посчитанные в связки. Отдельно от <see cref="SeenGames"/>
        /// нарочно: связки появились позже, и у тех, кто играл с программой
        /// раньше, все игры в истории уже «виденные». Считая по SeenGames, мы
        /// не дали бы им ни одной связки — а пул с другом у них уже настроен.
        /// Свой список позволяет разово пройти по тому, что клиент ещё помнит.
        /// </summary>
        public List<long> PairGames { get; set; } = new();

        /// <summary>
        /// С кем ты СТОЯЛ В ОЧЕРЕДИ соло/дуо — puuid → ник и время последнего раза.
        ///
        /// Связки копятся на всех союзников подряд: в флексе и нормалах их
        /// четверо, и список «напарников» разрастался до всех, с кем вообще
        /// доводилось играть. Напарник по дуо-пулу — это человек, с которым
        /// ВСТАЁШЬ В ОЧЕРЕДЬ, и знает это только лобби.
        ///
        /// Пишем лишь из лобби соло/дуо (queueId 420): там в пати может быть
        /// ровно один человек, и это он и есть.
        /// </summary>
        public Dictionary<string, QueuedMate> Queued { get; set; } = new();
    }

    /// Человек, с которым стояли в очереди соло/дуо.
    public sealed class QueuedMate
    {
        public string Name { get; set; } = "";
        /// Unix-секунды последнего раза — по ним список стареет.
        public long Seen { get; set; }
    }

    /// <summary>
    /// Одна связка. Хранит не счёт, а ВРЕМЯ каждой совместной игры — отдельно
    /// выигранных и проигранных.
    ///
    /// Счётчиков было мало: из «11 игр, 8 побед» не вычесть прошлый месяц, а
    /// период в окне переключается. Два массива чисел в json занимают немного,
    /// и из них считается и общий счёт, и любое окно.
    /// </summary>
    private sealed class PairRec
    {
        public List<long> Won  { get; set; } = new();
        public List<long> Lost { get; set; } = new();
        /// Ник напарника, каким он был в последней совместной игре. Только для
        /// показа: опознаём человека по puuid, ник может смениться.
        public string Name { get; set; } = "";

        /// Сколько сыграно и выиграно не раньше указанного времени (0 — всё).
        public (int Games, int Wins) Count(long since)
        {
            var w = Won.Count(t => t >= since);
            var l = Lost.Count(t => t >= since);
            return (w + l, w);
        }

        /// Время последней совместной игры; 0 — игр нет.
        public long Last() => Math.Max(Won.Count > 0 ? Won[^1] : 0,
                                       Lost.Count > 0 ? Lost[^1] : 0);

        /// Связка живёт годами, а нужны из неё последние игры: держим последние
        /// PairKeep с каждой стороны, чтобы файл не рос без конца.
        public void Trim()
        {
            const int PairKeep = 200;
            if (Won.Count  > PairKeep) Won.RemoveRange(0, Won.Count - PairKeep);
            if (Lost.Count > PairKeep) Lost.RemoveRange(0, Lost.Count - PairKeep);
        }
    }

    /// Одна связка наружу: с кем, на ком, с каким счётом.
    public sealed record PairStat(
        string AllyPuuid, string AllyName, string Queue,
        int MyChampionId, int AllyChampionId, int Games, int Wins,
        string MyRole = "", string AllyRole = "")
    {
        /// Роли известны у обеих сторон — значит по ним можно и отбирать.
        public bool HasRoles => MyRole.Length > 0 && AllyRole.Length > 0;

        public double WinRate => Games > 0 ? 100.0 * Wins / Games : 0;
    }

    private sealed class Store
    {
        // Ключ — puuid (стабилен и не меняется при переименовании).
        public Dictionary<string, Account> Accounts { get; set; } = new();
        // Кем играли в прошлый раз — чтобы панель не пустела до ответа LCU.
        public string? LastAccount { get; set; }

        // ── Наследие одноаккаунтного формата (мигрирует при первом запуске) ──
        public string? SelectedQueue { get; set; }
        public bool QueueChosen { get; set; }
        public string? Nick { get; set; }
        public long LastGameId { get; set; }
        public Dictionary<string, QueueLog>? Queues { get; set; }
        public Dictionary<string, RankedCache>? Ranked { get; set; }
        public List<LegacyGame>? Games { get; set; }
        public int LastAbsLp { get; set; } = int.MinValue;
    }

    private sealed class LegacyGame
    {
        public long Ts { get; set; }
        public int ChampionId { get; set; }
        public bool Win { get; set; }
        public int LpDelta { get; set; }
        public double Winrate { get; set; }
    }

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Counterplay", "session.json");

    // Один Refresh за раз: конец матча порождает шквал gameflow-событий, и
    // параллельные вызовы гонялись на файле журнала (затирали его пустым).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // ── Личная статистика по чемпионам ───────────────────────────────────────
    // Группы очередей для «моего винрейта»: ранкед = соло+флекс, обычные =
    // нормалы. ARAM намеренно не входит — там пик случайный и WR ни о чём не
    // говорит.
    public static readonly string[] QueuesRanked = ["solo", "flex"];
    public static readonly string[] QueuesNormal = ["normal"];

    // Журнал лежит в файле; для пула его читают по десяткам чемпионов сразу,
    // поэтому карту держим в коротком кэше.
    private static readonly Dictionary<string, (DateTime At, Dictionary<int, (int G, int W)> Map)> StatsCache = new();

    /// <summary>
    /// Забыть накопленное. Нужно ПРОВЕРКАМ: карта живёт в кэше двадцать секунд,
    /// и подменённый файл истории без сброса просто не заметят.
    /// В работе кэш протухает сам.
    /// </summary>
    public static void DropCache()
    {
        lock (StatsCache) StatsCache.Clear();
        _histCache = null;
    }

    /// Окно «свежей формы». Журнал держится за весь сезон, но для подбора важно,
    /// как человек играет СЕЙЧАС: 600 игр с 50% за сезон не говорят ни о чём, а
    /// 30 игр с 60% за месяц — говорят.
    public const int RecentDays = 30;

    /// Личная статистика по чемпионам за последние RecentDays дней.
    public static IReadOnlyDictionary<int, (int Games, int Wins)> ChampStatsMap(params string[] queues)
        => ChampStatsMap(RecentDays, queues);

    /// То же с явным окном в днях (0 или меньше — за всё время).
    public static IReadOnlyDictionary<int, (int Games, int Wins)> ChampStatsMap(int days, params string[] queues)
    {
        var since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0;
        var key = days + "|" + string.Join(",", queues);
        lock (StatsCache)
        {
            if (StatsCache.TryGetValue(key, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < 20)
                return c.Map.ToDictionary(kv => kv.Key, kv => (kv.Value.G, kv.Value.W));
        }

        var map = new Dictionary<int, (int G, int W)>();
        try
        {
            var s = Load();
            var acc = s.LastAccount is { Length: > 0 } k && s.Accounts.TryGetValue(k, out var a)
                ? a : s.Accounts.Values.FirstOrDefault();
            if (acc != null)
                foreach (var q in queues)
                    if (acc.Queues.TryGetValue(q, out var ql))
                        foreach (var g in ql.Games)
                        {
                            if (g.ChampionId == 0) continue;
                            if (since > 0 && g.Ts > 0 && g.Ts < since) continue;   // вне окна
                            var cur = map.GetValueOrDefault(g.ChampionId);
                            map[g.ChampionId] = (cur.G + 1, cur.W + (g.Win ? 1 : 0));
                        }
        }
        catch { /* журнала нет или он битый — считаем, что статистики нет */ }

        lock (StatsCache) StatsCache[key] = (DateTime.UtcNow, map);
        return map.ToDictionary(kv => kv.Key, kv => (kv.Value.G, kv.Value.W));
    }

    /// Личная статистика по одному чемпиону за последние RecentDays дней.
    public static (int Games, int Wins) ChampStats(int championId, params string[] queues)
    {
        var m = ChampStatsMap(queues);
        return m.TryGetValue(championId, out var v) ? v : (0, 0);
    }

    // ── Свежесть наигранности ────────────────────────────────────────────────

    /// <summary>
    /// Чем игрок играет СЕЙЧАС, а чем играл когда-то.
    ///
    /// Нужна движку, чтобы «комфорт» не держался на пожизненных очках мастерства
    /// Riot: они не убывают никогда, и чемпион, которого забросили полгода назад,
    /// оставался в подборе наверху вечно.
    ///
    /// <see cref="DaysSince"/> ограничен ГЛУБИНОЙ журнала: если программа ведёт
    /// его двадцать дней, сказать «не играл сто дней» нельзя — максимум «не играл
    /// при нас». Поэтому у новичка затухания нет вовсе, а с ростом журнала оно
    /// набирает силу само.
    /// </summary>
    public sealed class PlayHistory
    {
        /// Сколько последних игр на чемпиона помним по времени. Больше горстки
        /// не нужно: дальше третьей-четвёртой с конца вопросов к свежести нет.
        public const int KeepTimes = 10;

        private readonly IReadOnlyDictionary<int, (int Games, int Wins)> _recent;
        private readonly IReadOnlyDictionary<int, long[]> _times;
        private readonly long _now;

        /// <param name="times">Времена последних игр по чемпионам, от свежей к
        /// старой. Не одна отметка, а несколько: по одной нельзя отличить «играю»
        /// от «взял разок».</param>
        public PlayHistory(IReadOnlyDictionary<int, (int Games, int Wins)> recent,
                           IReadOnlyDictionary<int, long[]> times,
                           double spanDays, long now)
        {
            _recent = recent; _times = times; SpanDays = spanDays; _now = now;
        }

        /// Сколько дней покрывает журнал: от самой старой записи до сегодня.
        public double SpanDays { get; }

        /// Игр и побед на чемпионе за окно свежести.
        public (int Games, int Wins) Recent(int championId) => _recent.GetValueOrDefault(championId);

        /// Все чемпионы, которых игрок брал за окно. Нужны банам: там список
        /// строится перебором, а не запросом по одному.
        public IEnumerable<(int Id, int Games, int Wins)> Played =>
            _recent.Select(kv => (kv.Key, kv.Value.Games, kv.Value.Wins));

        /// Сколько дней прошло с последней игры на чемпионе. Не играл ни разу —
        /// глубина журнала: дольше, чем мы смотрим, «не играл» не бывает.
        public double DaysSince(int championId) => DaysSinceNth(championId, 1);

        /// <summary>
        /// Сколько дней прошло с <paramref name="n"/>-й игры С КОНЦА.
        ///
        /// По последней игре судить о свежести нельзя: ОДНА игра воскрешала
        /// затухание целиком. У владельца Зилеан — одна игра за месяц, зато три
        /// дня назад, и мастерство держалось в полном весе, хотя человек этого
        /// чемпиона уже не играет. Третья с конца на такое не ведётся: чтобы
        /// считаться действующим, чемпиона надо брать не разово.
        ///
        /// Игр меньше, чем <paramref name="n"/> — отвечаем глубиной журнала: за
        /// всё, что мы видели, регулярной игры не было.
        /// </summary>
        public double DaysSinceNth(int championId, int n)
        {
            if (n < 1) n = 1;
            if (!_times.TryGetValue(championId, out var ts) || ts.Length < n) return SpanDays;
            return Math.Max(0.0, (_now - ts[n - 1]) / 86400.0);
        }
    }

    /// <summary>
    /// Подменная история для проверок: настоящая копится только по сыгранным
    /// играм, и подогнать её под замер нечем. В боевом режиме всегда null.
    /// </summary>
    public static PlayHistory? HistoryOverride { get; set; }

    private static (DateTime At, string Key, PlayHistory H)? _histCache;

    /// История игр за окно <paramref name="freshDays"/> по указанным очередям.
    public static PlayHistory History(int freshDays, params string[] queues)
    {
        if (HistoryOverride is { } fake) return fake;

        var key = freshDays + "|" + string.Join(",", queues);
        if (_histCache is { } c && c.Key == key && (DateTime.UtcNow - c.At).TotalSeconds < 20)
            return c.H;

        var now    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since  = now - (long)freshDays * 86400;
        var recent = new Dictionary<int, (int Games, int Wins)>();
        var times  = new Dictionary<int, List<long>>();
        var oldest = 0L;
        try
        {
            var s = Load();
            var acc = s.LastAccount is { Length: > 0 } k && s.Accounts.TryGetValue(k, out var a)
                ? a : s.Accounts.Values.FirstOrDefault();
            if (acc != null)
                foreach (var q in queues)
                    if (acc.Queues.TryGetValue(q, out var ql))
                        foreach (var g in ql.Games)
                        {
                            // Игру без отметки времени пропускаем целиком: когда
                            // она была — неизвестно, а здесь всё считается от
                            // времени. Такие записи остались только от старой
                            // раскладки журнала и давно вне любого окна.
                            if (g.ChampionId == 0 || g.Ts <= 0) continue;
                            if (oldest == 0 || g.Ts < oldest) oldest = g.Ts;
                            if (g.Ts >= since)
                            {
                                var cur = recent.GetValueOrDefault(g.ChampionId);
                                recent[g.ChampionId] = (cur.Games + 1, cur.Wins + (g.Win ? 1 : 0));
                            }
                            if (!times.TryGetValue(g.ChampionId, out var lst))
                                times[g.ChampionId] = lst = [];
                            lst.Add(g.Ts);
                        }
        }
        catch { /* журнала нет или он битый — истории нет */ }

        var span = oldest > 0 ? Math.Max(0.0, (now - oldest) / 86400.0) : 0.0;
        // От свежей к старой, и дальше KeepTimes не храним.
        var byTime = times.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderByDescending(t => t).Take(PlayHistory.KeepTimes).ToArray());
        var h = new PlayHistory(recent, byTime, span, now);
        _histCache = (DateTime.UtcNow, key, h);
        return h;
    }

    /// <summary>
    /// Винрейт КОНКРЕТНОЙ связки: сколько игр сыграно вдвоём с этим человеком
    /// именно на этой паре чемпионов и сколько из них выиграно.
    ///
    /// Это личная статистика игрока, а не общая синергия из базы: «мы вдвоём
    /// на этой паре 7-3», а не «эта пара в среднем выигрывает 52%».
    /// </summary>
    public static (int Games, int Wins) PairStats(string? allyPuuid, int myChampion, int allyChampion)
    {
        if (myChampion == 0 || allyChampion == 0) return (0, 0);
        if (Preview is { } fake)
        {
            var p = fake.FirstOrDefault(x => x.MyChampionId == myChampion
                                             && x.AllyChampionId == allyChampion
                                             && (string.IsNullOrEmpty(allyPuuid)
                                                 || x.AllyPuuid.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase)));
            return p is null ? (0, 0) : (p.Games, p.Wins);
        }
        if (string.IsNullOrEmpty(allyPuuid)) return (0, 0);
        var acc = CurrentAccount();
        if (acc is null) return (0, 0);
        // Ключ теперь несёт очередь, а спрашивают про пару целиком — складываем
        // по всем очередям, кроме ARAM (его в связки не пишем вовсе).
        var g = 0; var w = 0;
        foreach (var p in TopPairs(allyPuuid, int.MaxValue))
            if (p.MyChampionId == myChampion && p.AllyChampionId == allyChampion)
            { g += p.Games; w += p.Wins; }
        return (g, w);
    }

    /// <summary>
    /// Сколько всего игр сыграно с этим человеком (по всем парам чемпионов).
    /// Нужно, чтобы отличить «связки не было» от «связка есть, но новая».
    /// </summary>
    public static (int Games, int Wins) MateStats(string? allyPuuid)
    {
        if (Preview is { } fake)
        {
            var mine = fake.Where(p => string.IsNullOrEmpty(allyPuuid)
                                       || p.AllyPuuid.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase)).ToList();
            return (mine.Sum(p => p.Games), mine.Sum(p => p.Wins));
        }
        if (string.IsNullOrEmpty(allyPuuid)) return (0, 0);
        var acc = CurrentAccount();
        if (acc is null) return (0, 0);
        var prefix = allyPuuid + "|";
        var g = 0; var w = 0;
        foreach (var (k, r) in acc.Pairs)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
            {
                var (pg, pw) = r.Count(0);
                g += pg; w += pw;
            }
        return (g, w);
    }

    /// <summary>
    /// То же, но за окно в днях: «сколько мы играем вдвоём СЕЙЧАС». Нужно, чтобы
    /// дуо-вес в оценке набирался свежей совместной игрой, а не однажды сыгранной
    /// парой игр год назад. Подменные связки (песочница, проверки) окна не знают —
    /// у них нет меток времени, и они возвращаются целиком.
    /// </summary>
    public static (int Games, int Wins) MateStats(string? allyPuuid, int days)
    {
        if (Preview is not null || days <= 0) return MateStats(allyPuuid);
        if (string.IsNullOrEmpty(allyPuuid)) return (0, 0);
        var acc = CurrentAccount();
        if (acc is null) return (0, 0);
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        var prefix = allyPuuid + "|";
        var g = 0; var w = 0;
        foreach (var (k, r) in acc.Pairs)
            if (k.StartsWith(prefix, StringComparison.Ordinal))
            {
                var (pg, pw) = r.Count(since);
                g += pg; w += pw;
            }
        return (g, w);
    }

    /// <summary>
    /// Подменные связки для песочницы. Настоящие копятся только по сыгранным
    /// играм, и без них раздел в песочнице всегда пуст — посмотреть, как он
    /// выглядит с данными, было нельзя. В боевом режиме всегда null.
    /// </summary>
    public static IReadOnlyList<PairStat>? Preview { get; set; }

    /// <summary>
    /// Убрать связки, которые ничего не значат.
    ///
    /// Каждая игра добавляет до четырёх ключей, и почти все — случайные
    /// союзники на один раз: из 21 игры вышло 68 связок с 63 людьми. Без чистки
    /// session.json растёт весь сезон.
    ///
    /// Смысл связки в повторах. Одну игру, которой больше месяца, забываем:
    /// «сыграли раз полгода назад» не говорит ни о чём. Настоящие связки —
    /// те, что повторяются, — не трогаем, пока их не станет слишком много;
    /// тогда срежем по давности последней совместной игры.
    /// </summary>
    private static void PrunePairs(Account acc, long now)
    {
        const int MaxPairs = 400;          // с запасом: у постоянной пары их десятки
        const int OneOffDays = 30;

        var cutoff = now - OneOffDays * 86400L;
        foreach (var key in acc.Pairs
                     .Where(kv => kv.Value.Count(0).Games <= 1 && kv.Value.Last() < cutoff)
                     .Select(kv => kv.Key).ToList())
            acc.Pairs.Remove(key);

        if (acc.Pairs.Count <= MaxPairs) return;
        foreach (var key in acc.Pairs.OrderBy(kv => kv.Value.Last())
                                     .Take(acc.Pairs.Count - MaxPairs)
                                     .Select(kv => kv.Key).ToList())
            acc.Pairs.Remove(key);
    }

    /// <summary>
    /// Связки, сыгранные с этим человеком, — самые частые первыми.
    /// Пустой <paramref name="allyPuuid"/> — все связки со всеми.
    /// </summary>
    /// <param name="days">Окно в днях; 0 — за всё время.</param>
    public static IReadOnlyList<PairStat> TopPairs(string? allyPuuid = null, int take = 30,
                                                  int days = 0, params string[] queues)
    {
        var since = days > 0
            ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0L;
        if (Preview is { } fake)
            return fake.Where(p => (string.IsNullOrEmpty(allyPuuid)
                                    || p.AllyPuuid.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase))
                                   && (queues is not { Length: > 0 } || queues.Contains(p.Queue)))
                       .OrderByDescending(p => p.Games).ThenByDescending(p => p.WinRate)
                       .Take(take).ToList();

        var acc = CurrentAccount();
        if (acc is null) return [];
        var res = new List<PairStat>();
        foreach (var (k, r) in acc.Pairs)
        {
            if (ParsePairKey(k) is not { } p) continue;
            var (pu, queue, mine, his, myRole, hisRole) = p;
            if (!string.IsNullOrEmpty(allyPuuid)
                && !pu.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (queues is { Length: > 0 } && !queues.Contains(queue)) continue;
            var (g, w) = r.Count(since);
            if (g == 0) continue;          // в выбранное окно связка не попала
            res.Add(new PairStat(pu, r.Name, queue, mine, his, g, w, myRole, hisRole));
        }
        return res.OrderByDescending(p => p.Games).ThenByDescending(p => p.WinRate)
                  .Take(take).ToList();
    }

    /// Аккаунт, под которым сейчас сидят. null — клиент ещё не отдал игрока.
    private static Account? CurrentAccount()
    {
        if (_account is null) return null;
        var store = Load();
        return store.Accounts.GetValueOrDefault(_account);
    }

    /// Правит журнал после ошибки в привязке LP: победа отдавала свои очки
    /// предыдущему поражению, и в истории оставалась пара «поражение +23» и
    /// «победа 0». Раз известно, чьи это очки, возвращаем их победе; если
    /// подходящей игры рядом нет — просто стираем, прочерк честнее чужого числа.
    private static void FixImpossibleLp(Store store)
    {
        foreach (var acc in store.Accounts.Values)
            foreach (var q in acc.Queues.Values)
            {
                var games = q.Games;
                for (int i = 0; i < games.Count; i++)
                {
                    if (games[i].Lp is not int lp || lp == 0 || games[i].Win == lp > 0) continue;

                    var next = i + 1 < games.Count ? games[i + 1] : null;
                    if (next is not null && next.Lp is null or 0 && next.Win == lp > 0)
                        next.Lp = lp;      // очки нашли своего владельца
                    games[i].Lp = null;
                }
            }
    }

    private static Store Load()
    {
        // Основной файл, затем резервная копия — журнал не теряется из-за
        // одного битого чтения.
        foreach (var path in new[] { StorePath, StorePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? new Store();
                FixImpossibleLp(store);
                return store;
            }
            catch { /* пробуем следующий */ }
        }
        return new Store();
    }

    /// <summary>
    /// Аккаунт по ключу; создаёт при первом появлении. Заодно разово переносит
    /// данные старого (одноаккаунтного) формата — но ТОЛЬКО если ник совпадает:
    /// иначе история эмеральд-аккаунта прилипла бы к свежему золотому.
    /// </summary>
    private static Account GetAccount(Store s, string key, string? nick)
    {
        if (s.Accounts.TryGetValue(key, out var acc)) return acc;

        acc = new Account { Nick = nick };

        var hasLegacy = s.Queues is { Count: > 0 } || s.Games is { Count: > 0 };

        // Наследие принадлежит тому, чей ник в нём записан. Если ник совпал —
        // переносим, даже если аккаунтов уже несколько (человек мог сначала
        // зайти на смурф, а потом вернуться на основной — история его ждёт).
        // Если ника в наследии нет вовсе (совсем старый файл) — отдаём первому.
        var namedOwner = !string.IsNullOrEmpty(s.Nick) && !string.IsNullOrEmpty(nick) &&
                         string.Equals(s.Nick, nick, StringComparison.OrdinalIgnoreCase);
        var anonymousLegacy = string.IsNullOrEmpty(s.Nick) && s.Accounts.Count == 0;

        if (hasLegacy && (namedOwner || anonymousLegacy))
        {
            acc.Queues       = s.Queues ?? new();
            acc.Ranked       = s.Ranked ?? new();
            acc.LastGameId   = s.LastGameId;
            acc.SelectedQueue = s.SelectedQueue ?? "solo";
            acc.QueueChosen  = s.QueueChosen || (s.SelectedQueue is not null and not "solo");

            // Совсем старый формат: один журнал без очередей → solo.
            if (s.Games is { Count: > 0 })
            {
                var q = GetQueue(acc, "solo");
                if (q.Games.Count == 0)
                {
                    q.Games.AddRange(s.Games.Select(g => new GameLog
                    { Ts = g.Ts, ChampionId = g.ChampionId, Win = g.Win, Lp = g.LpDelta, Wr = g.Winrate }));
                    q.LastAbsLp = s.LastAbsLp;
                }
            }
            // Наследие перенесено — чистим, чтобы не прилипло ко второму аккаунту.
            s.Queues = null; s.Ranked = null; s.Games = null;
            s.Nick = null; s.LastGameId = 0; s.LastAbsLp = int.MinValue;
        }

        // Лечим записи, испорченные до раздельного учёта аккаунтов: дельта в
        // сотни LP — это разница рангов двух аккаунтов, а не результат игры.
        foreach (var q in acc.Queues.Values)
            foreach (var g in q.Games)
                if (g.Lp is { } lp && Math.Abs(lp) > MaxLpPerGame)
                    g.Lp = null;

        s.Accounts[key] = acc;
        return acc;
    }

    private static QueueLog GetQueue(Account a, string key)
    {
        if (!a.Queues.TryGetValue(key, out var q)) a.Queues[key] = q = new QueueLog();
        return q;
    }

    private static void Save(Store s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Атомарная запись: tmp + подмена; прошлая версия остаётся как .bak.
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            if (File.Exists(StorePath))
                File.Copy(StorePath, StorePath + ".bak", overwrite: true);
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch { /* не критично */ }
    }

    // ── Выбранная очередь (переключатель в панели) ──────────────────────────
    // Настройка своя у каждого аккаунта: на смурфе можно смотреть нормалы, на
    // основном — соло/дуо.

    private static string? _account;   // puuid текущего аккаунта (ставится при refresh)

    public static string GetSelectedQueue()
    {
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            var q = key != null && s.Accounts.TryGetValue(key, out var a)
                ? a.SelectedQueue : "solo";
            return QueueKeys.Contains(q) ? q : "solo";
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Запомнить, с кем стоим в очереди соло/дуо. Зовётся из <see cref="Party"/>
    /// при каждом обновлении лобби — поэтому пишем на диск, только если состав
    /// правда изменился: лобби присылает себя часто.
    /// </summary>
    public static void NoteQueued(IEnumerable<(string Puuid, string Name)> mates)
    {
        var list = mates.Where(m => m.Puuid.Length > 0).ToList();
        if (list.Count == 0) return;
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            if (key is null) return;
            var a = GetAccount(s, key, null);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var changed = false;
            foreach (var (puuid, name) in list)
            {
                if (!a.Queued.TryGetValue(puuid, out var rec))
                {
                    a.Queued[puuid] = rec = new QueuedMate();
                    changed = true;
                    Log.Write($"очередь соло/дуо: впервые встаём с «{(name.Length > 0 ? name : puuid[..8])}»");
                }
                if (name.Length > 0 && rec.Name != name) { rec.Name = name; changed = true; }
                // Отметку времени двигаем не чаще раза в час: иначе каждое
                // обновление лобби — запись файла.
                if (now - rec.Seen > 3600) { rec.Seen = now; changed = true; }
            }
            // Список живёт долго, но не бесконечно: держим последних QueuedKeep.
            const int QueuedKeep = 50;
            if (a.Queued.Count > QueuedKeep)
            {
                foreach (var old in a.Queued.OrderByDescending(x => x.Value.Seen)
                                            .Skip(QueuedKeep).Select(x => x.Key).ToList())
                    a.Queued.Remove(old);
                changed = true;
            }
            if (changed) Save(s);
        }
        finally { Gate.Release(); }
    }

    /// С кем стояли в очереди соло/дуо — от недавних к давним.
    public static IReadOnlyList<(string Puuid, string Name, long Seen)> QueuedMates()
    {
        Gate.Wait();
        try
        {
            var a = CurrentAccount();
            if (a is null) return [];
            return a.Queued.OrderByDescending(x => x.Value.Seen)
                           .Select(x => (x.Key, x.Value.Name, x.Value.Seen)).ToList();
        }
        finally { Gate.Release(); }
    }

    public static void SetSelectedQueue(string queue)
    {
        if (!QueueKeys.Contains(queue)) return;
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            if (key is null) return;
            var a = GetAccount(s, key, null);
            a.SelectedQueue = queue;
            a.QueueChosen = true;
            Save(s);
        }
        finally { Gate.Release(); }
    }

    // ── Абсолютный LP (сквозь промо/демоушены) ──────────────────────────────

    private static readonly Dictionary<string, int> TierBase = new()
    {
        ["IRON"] = 0, ["BRONZE"] = 400, ["SILVER"] = 800, ["GOLD"] = 1200,
        ["PLATINUM"] = 1600, ["EMERALD"] = 2000, ["DIAMOND"] = 2400,
        ["MASTER"] = 2800, ["GRANDMASTER"] = 2800, ["CHALLENGER"] = 2800
    };
    private static readonly Dictionary<string, int> DivIndex = new()
    {
        ["IV"] = 0, ["III"] = 1, ["II"] = 2, ["I"] = 3
    };

    /// История рейтинга за месяц. Абсолютных значений мы не храним — только
    /// дельты за игру, поэтому идём от ТЕКУЩЕГО ранга назад, вычитая дельты: так
    /// последняя точка всегда совпадает с тем, что показывает клиент, а
    /// накопленная погрешность (игры без дельты) остаётся в прошлом.
    private static List<LpPoint> RatingPoints(QueueLog q, Ranked r)
    {
        if (!r.HasRank) return [];
        var from = DateTimeOffset.UtcNow.AddDays(-RatingDays).ToUnixTimeSeconds();
        var games = q.Games.Where(g => g.Ts >= from && g.Lp is not null).ToList();
        if (games.Count == 0) return [];

        var now = AbsLp(r.Tier, r.Div, r.Lp);
        var pts = new List<LpPoint>(games.Count + 1)
            { new(DateTimeOffset.FromUnixTimeSeconds(games[^1].Ts).LocalDateTime, now) };

        var lp = now;
        for (int i = games.Count - 1; i >= 0; i--)
        {
            lp -= games[i].Lp ?? 0;
            pts.Add(new LpPoint(DateTimeOffset.FromUnixTimeSeconds(games[i].Ts).LocalDateTime, lp));
        }
        pts.Reverse();
        return pts;
    }

    private static int AbsLp(string tier, string div, int lp)
    {
        var t = TierBase.GetValueOrDefault(tier.ToUpperInvariant(), 2000);
        var d = DivIndex.GetValueOrDefault(div.ToUpperInvariant(), 0);
        return t >= 2800 ? t + lp : t + d * 100 + lp;
    }

    private static int ProgressPct(string tier, int lp)
    {
        var t = tier.ToUpperInvariant();
        if (t is "MASTER" or "GRANDMASTER" or "CHALLENGER")
            return Math.Clamp((int)(lp / 200.0 * 100), 0, 100);
        return Math.Clamp(lp, 0, 100);
    }

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLowerInvariant();

    // ── Обновление ──────────────────────────────────────────────────────────

    private sealed record Ranked(bool HasRank, string Tier, string Div, int Lp, int Wins, int Losses);
    private sealed record HistEntry(long GameId, string Queue, int ChampionId, bool Win, long CreatedSec);

    /// <param name="roleShare">
    /// Доля игр чемпиона на роли — ею достраиваются роли в прошлых играх там,
    /// где клиент врёт (везде, кроме бот-линии). Без неё известной останется
    /// только бот-линия: приблизительно, но честно.
    /// </param>
    public static async Task<SessionData?> RefreshAsync(
        LcuHttpClient http, CancellationToken ct, Func<int, string, double>? roleShare = null)
    {
        await Gate.WaitAsync(ct);
        try { return await RefreshCoreAsync(http, roleShare, ct); }
        finally { Gate.Release(); }
    }

    private static async Task<SessionData?> RefreshCoreAsync(
        LcuHttpClient http, Func<int, string, double>? roleShare, CancellationToken ct)
    {
        var store = Load();

        // 1) КТО играет. Спрашиваем каждый раз (запрос локальный, дешёвый): при
        //    смене аккаунта в клиенте панель должна переключиться сама.
        //    Ключ — puuid: он стабилен и переживает смену ника.
        var who = await FetchSummonerAsync(http, ct);
        var accountKey = who?.Puuid ?? store.LastAccount;
        if (accountKey is null) return null;   // клиент ещё не отдал игрока

        _account = accountKey;
        store.LastAccount = accountKey;

        var acc = GetAccount(store, accountKey, who?.Nick);
        if (!string.IsNullOrEmpty(who?.Nick)) acc.Nick = who.Nick;
        var nick = acc.Nick ?? "";

        // 2) Ранги обеих ранкед-очередей. Если LCU не ответил (рестарт после
        // обновления и т.п.) — берём последний снимок аккаунта: панель никогда
        // не пустеет, пока на диске есть данные.
        var fetched = await FetchRankedAsync(http, ct);
        var ranked = fetched ?? new Dictionary<string, Ranked>
        {
            ["solo"] = FromCache(acc.Ranked.GetValueOrDefault("solo")),
            ["flex"] = FromCache(acc.Ranked.GetValueOrDefault("flex")),
        };
        if (fetched != null)
            foreach (var (k, r) in fetched)
                acc.Ranked[k] = new RankedCache
                { HasRank = r.HasRank, Tier = r.Tier, Div = r.Div, Lp = r.Lp, Wins = r.Wins, Losses = r.Losses };

        // 3) Последние игры из истории (все очереди).
        var history = await FetchHistoryAsync(http, ct);

        // 3b) Очередь по умолчанию: пока пользователь не выбрал сам — та, где
        //     больше всего игр за последнее время (история лаунчера, ~20 игр).
        //     Кто играет только нормалы/ARAM — сразу видит свою статистику.
        if (!acc.QueueChosen && history.Count > 0)
        {
            acc.SelectedQueue = history.GroupBy(h => h.Queue)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(h => h.GameId)) // ничья → где игра свежее
                .First().Key;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 4) Новые игры — те, чьих номеров мы ещё не видели. Порядок берём по
        //    времени начала: номера игр по времени не упорядочены (см. SeenGames).
        //    Первый запуск: журнал не наполняем задним числом, только запоминаем,
        //    что всё это уже было.
        var appended = new Dictionary<string, GameLog>(); // очередь → последняя добавленная игра
        var seen = new HashSet<long>(acc.SeenGames);

        if (seen.Count == 0)
        {
            // Переход со старого формата, где помнился один номер. Считаем
            // виденным всё, что началось не позже последней записи в журнале:
            // так и дубли не появятся, и игра, потерянная из-за номеров, всё-таки
            // догонится. Пустой журнал (первый запуск) — виденным считаем всё.
            long lastLogged = acc.Queues.Values
                .SelectMany(q => q.Games)
                .Select(g => g.Ts)
                .DefaultIfEmpty(0)
                .Max();
            bool fresh = acc.LastGameId == 0 && lastLogged == 0;
            foreach (var h in history)
                if (fresh || h.CreatedSec <= 0 || h.CreatedSec <= lastLogged)
                    seen.Add(h.GameId);
        }

        foreach (var h in history.Where(h => !seen.Contains(h.GameId)).OrderBy(h => h.CreatedSec))
        {
            var q = GetQueue(acc, h.Queue);
            var log = new GameLog { Ts = now, ChampionId = h.ChampionId, Win = h.Win };
            q.Games.Add(log);
            // Журнал держим на весь сезон: 3000 игр на очередь хватает даже
            // самым активным; страховка от бесконечного роста файла.
            if (q.Games.Count > 3000) q.Games.RemoveRange(0, q.Games.Count - 3000);
            appended[h.Queue] = log;
            seen.Add(h.GameId);
        }

        // 4a) Винрейт связок. Идём по ВСЕЙ истории, а не только по новым играм:
        //     у того, кто уже давно играет с программой, все игры «виденные», и
        //     по ним связок не набралось бы ни одной — при том что пул с другом
        //     у него настроен. Свой список посчитанных игр это разводит: первый
        //     раз проходим по всему, что клиент помнит (около двадцати игр),
        //     дальше — только по новым.
        //
        //     Состав команды есть лишь в подробной игре, и это отдельный запрос
        //     на игру. Поэтому и нужен список: иначе двадцать запросов уходили
        //     бы на каждое обновление.
        if (!string.IsNullOrEmpty(who?.Puuid))
        {
            var counted = new HashSet<long>(acc.PairGames);
            foreach (var h in history)
            {
                if (!counted.Add(h.GameId)) continue;
                // ARAM в связки НЕ идёт: чемпионы там раздаются случайно, и пара
                // сложилась сама, а не была выбрана. Её винрейт не говорит ни о
                // чём — ровно по той же причине ARAM не входит и в винрейт по
                // чемпионам. Заодно экономим запрос подробной игры.
                if (h.Queue == "aram") continue;
                var (mates, myRole) = await FetchAlliesAsync(http, h.GameId, who!.Puuid, roleShare, ct);
                foreach (var a in mates)
                {
                    var key = PairKey(a.Puuid, h.Queue, h.ChampionId, a.ChampionId, myRole, a.Role);
                    if (!acc.Pairs.TryGetValue(key, out var rec))
                        acc.Pairs[key] = rec = new PairRec();
                    // Время игры, а не «сейчас»: наполнение задним числом идёт по
                    // старым играм, и по «сейчас» они все попали бы в этот месяц.
                    (h.Win ? rec.Won : rec.Lost).Add(h.CreatedSec);
                    rec.Trim();
                    if (a.Name.Length > 0) rec.Name = a.Name;   // ник мог смениться
                }
            }
            // Помним столько же, сколько и виденных игр: выпавшая из истории
            // игра второй раз оттуда не придёт, копить бесконечно незачем.
            acc.PairGames = history.Select(h => h.GameId).Concat(counted)
                                   .Distinct().Take(100).ToList();

            PrunePairs(acc, now);
        }

        // Помним столько, сколько отдаёт история (20 игр), с запасом на случай
        // если LCU вернёт больше обычного. Бесконечно копить незачем: игра,
        // выпавшая из истории, второй раз оттуда не придёт.
        acc.SeenGames = history.Select(h => h.GameId)
                               .Concat(seen)
                               .Distinct()
                               .Take(100)
                               .ToList();
        if (history.Count > 0)
            acc.LastGameId = Math.Max(acc.LastGameId, history.Max(h => h.GameId));

        // 4b) Пустые журналы добиваем ПРОШЛЫМИ играми из истории лаунчера:
        //     первый запуск или очередь, в которую раньше не играли с программой.
        //     LP прошлых игр LCU не отдаёт — дельты начнутся с новых игр.
        foreach (var key in QueueKeys)
        {
            var q = GetQueue(acc, key);
            if (q.Games.Count > 0) continue;
            // Журнал этой очереди пуст — значит ни одна её игра ещё не записана,
            // и берём из истории все. По времени начала, а не по номеру: номера
            // не упорядочены (см. SeenGames), и «прошлые» игры ложились бы
            // вперемешку.
            var past = history.Where(h => h.Queue == key)
                              .OrderBy(h => h.CreatedSec).ToList();
            int wins = 0;
            for (int i = 0; i < past.Count; i++)
            {
                if (past[i].Win) wins++;
                q.Games.Add(new GameLog
                {
                    Ts = past[i].CreatedSec > 0 ? past[i].CreatedSec : now,
                    ChampionId = past[i].ChampionId,
                    Win = past[i].Win,
                    Wr = 100.0 * wins / (i + 1),
                });
            }
        }

        // 5) LP-дельты ранкед-очередей: разница абсолютного LP с прошлого снимка
        //    вешается на самую свежую добавленную игру этой очереди.
        //    Только при СВЕЖЕМ ответе LCU — по кэшу дельты не считаем.
        //    ВАЖНО: LP в клиенте обновляется РАНЬШЕ истории матчей. Если LP уже
        //    сменился, а игры в истории ещё нет — LastAbsLp не трогаем, иначе
        //    дельта «съедается» и догнанная игра получает +0.
        foreach (var key in fetched is null ? [] : new[] { "solo", "flex" })
        {
            var r = ranked[key];
            if (!r.HasRank) continue;
            var q = GetQueue(acc, key);
            var abs = AbsLp(r.Tier, r.Div, r.Lp);
            if (q.LastAbsLp == int.MinValue) { q.LastAbsLp = abs; continue; }

            var delta = abs - q.LastAbsLp;

            // Абсурдная дельта = не игра, а смена контекста: другой аккаунт,
            // сброс ранга в новом сезоне, ручная правка Riot. За одну игру
            // столько LP не теряют. Отметку двигаем, но в журнал не пишем —
            // иначе в серии появляется «−492».
            if (Math.Abs(delta) > MaxLpPerGame)
            {
                q.LastAbsLp = abs;
                continue;
            }

            // Игра только что появилась в истории — дельта её, если знак сходится
            // с результатом. Ноль допустим у обоих: поражение на дне дивизиона
            // ничего не отнимает, а победа в редких случаях ничего не даёт.
            if (appended.TryGetValue(key, out var game)
                && (delta == 0 || game.Win == delta > 0))
            {
                game.Lp = delta;
                q.LastAbsLp = abs;
            }
            else if (delta != 0)
            {
                // Обратный порядок: игра уже догнана историей (дельта ещё не
                // назначена), а LP доехал только сейчас — вешаем дельту на неё.
                //
                // Lp == 0 — это НЕ «дельты нет»: так выглядит честный ноль,
                // когда поражение на 0 LP съел запас перед вылетом из дивизиона.
                // Раньше такой ноль считался пустым местом, и следующая победа
                // отдавала свои +23 проигранной игре, а сама получала 0.
                //
                // Знак тоже обязан совпадать с результатом: за победу LP не
                // отнимают, за поражение не начисляют. Не совпал — дельта
                // относится к другой игре, ждём её (LastAbsLp не двигаем,
                // поэтому разница не потеряется).
                var lastGame = q.Games.Count > 0 ? q.Games[^1] : null;
                if (lastGame is not null && lastGame.Lp is null
                    && lastGame.Win == delta > 0
                    && now - lastGame.Ts < 900)
                {
                    lastGame.Lp = delta;
                    q.LastAbsLp = abs;
                }
                // Иначе ждём: дельту получит игра, когда появится в истории.
            }
        }

        // 6a) Опорная точка графика для ранговых: ставим один раз, как только
        //     клиент отдал сезонную статистику. Дальше линия растёт от неё.
        foreach (var key in new[] {"solo", "flex"})
        {
            var r = ranked[key];
            var played = r.Wins + r.Losses;
            if (!r.HasRank || played == 0) continue;
            var q = GetQueue(acc, key);
            if (q.AnchorTs != 0) continue;
            q.AnchorTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            q.AnchorWr = 100.0 * r.Wins / played;
        }

        // 6) Винрейт на момент игры: ранкед — сезонный, нормал/ARAM — по журналу.
        foreach (var (key, game) in appended)
        {
            var q = GetQueue(acc, key);
            if (key is "solo" or "flex")
            {
                var r = ranked[key];
                var g = r.Wins + r.Losses;
                game.Wr = g > 0 ? 100.0 * r.Wins / g : 0;
            }
            else
            {
                // Тянем к 50%: без этого первая победа даёт точку на 100%, и
                // график начинается с потолка, а потом «падает» до нормальных
                // значений — как будто игрок сдал, хотя он просто сыграл вторую
                // игру. Приор в PriorGames игр по 50% рассасывается сам: к
                // двадцатой игре он уже почти не влияет.
                var wins = q.Games.Count(x => x.Win);
                game.Wr = SmoothWr(wins, q.Games.Count);
            }
        }

        Save(store);

        return BuildView(acc, nick, ranked, history);
    }

    /// 7) Представления по очередям — из журнала аккаунта, ранга и истории
    /// клиента. Отдельно от обновления, потому что тот же экран нужен песочнице
    /// без клиента (<see cref="StoredView"/>).
    private static SessionData BuildView(Account acc, string nick,
        IReadOnlyDictionary<string, Ranked> ranked, IReadOnlyList<HistEntry> history)
    {
        var views = new Dictionary<string, QueueView>();
        foreach (var key in QueueKeys)
        {
            var q = GetQueue(acc, key);
            var last5 = q.Games.AsEnumerable().Reverse().Take(5)
                .Select(g => new RecentGame(g.ChampionId, g.Win, g.Lp)).ToList();
            if (last5.Count == 0) // свежая установка — показать хотя бы иконки из истории
                last5 = history.Where(h => h.Queue == key).Take(5)
                    .Select(h => new RecentGame(h.ChampionId, h.Win, null)).ToList();
            // Окно графика — три месяца: старое отваливается само.
            var chartFrom = DateTimeOffset.UtcNow.AddDays(-ChartDays).ToUnixTimeSeconds();
            var ranked2 = key is "solo" or "flex";

            // Порог «не рисуем, пока мало игр» нужен только там, где винрейт
            // считаем сами (нормал/ARAM): на первых матчах он скачет от 0 до
            // 100 и навсегда растягивает шкалу. У ранговых значения приходят из
            // клиента и достоверны с первой секунды.
            var points = !ranked2 && q.Games.Count < MinGamesForChart
                ? []
                : q.Games
                    .Where(g => g.Ts >= chartFrom)
                    .Select(g => new WrPoint(DateTimeOffset.FromUnixTimeSeconds(g.Ts).LocalDateTime, g.Wr))
                    .ToList();

            // Опора ранговых — первой точкой, чтобы линия шла от того винрейта,
            // с которым человек пришёл, а не от первой сыгранной игры.
            if (ranked2 && q.AnchorTs >= chartFrom && points.Count > 0)
                points.Insert(0, new WrPoint(
                    DateTimeOffset.FromUnixTimeSeconds(q.AnchorTs).LocalDateTime, q.AnchorWr));

            if (key is "solo" or "flex")
            {
                var r = ranked[key];
                var g = r.Wins + r.Losses;
                views[key] = new QueueView(
                    r.HasRank, Cap(r.Tier), r.Div, r.Lp,
                    r.HasRank ? ProgressPct(r.Tier, r.Lp) : 0,
                    r.Wins, r.Losses, g > 0 ? 100.0 * r.Wins / g : 0,
                    last5, points, RatingPoints(q, r));
            }
            else
            {
                int w = q.Games.Count(x => x.Win), l = q.Games.Count - w;
                views[key] = new QueueView(
                    false, "", "", 0, 0, w, l,
                    q.Games.Count > 0 ? 100.0 * w / q.Games.Count : 0,
                    last5, points, []);
            }
        }

        var selected = QueueKeys.Contains(acc.SelectedQueue) ? acc.SelectedQueue : "solo";
        return new SessionData(nick, selected, views);
    }

    // ── Песочница: мои настоящие данные ─────────────────────────────────────
    //
    // Песочница работает без клиента, поэтому аккаунта у трекера в ней нет:
    // журнал игр и личный винрейт читаются с последнего аккаунта сами, а
    // связки и экран профиля — нет. Ниже — как дать ей и то и другое, ничего
    // не записывая.

    /// Аккаунт, с которым программа работала в последний раз (puuid).
    public static string? LastAccountKey
    {
        get
        {
            Gate.Wait();
            try { return Load().LastAccount; }
            finally { Gate.Release(); }
        }
    }

    /// Песочница: связки читать с последнего аккаунта (true) или снова
    /// считать, что клиента нет (false).
    public static void UseStoredAccount(bool on)
    {
        _account = on ? LastAccountKey : null;
        DropCache();
    }

    /// <summary>
    /// Экран профиля по тому, что лежит на диске: ник, журнал игр, график и
    /// ранг из последнего снимка. Клиента не спрашивает и ничего не пишет.
    /// null — на диске нет ни одного аккаунта.
    /// </summary>
    public static SessionData? StoredView()
    {
        Gate.Wait();
        try
        {
            var store = Load();
            if (store.LastAccount is not { Length: > 0 } key
                || !store.Accounts.TryGetValue(key, out var acc)) return null;
            var ranked = new Dictionary<string, Ranked>
            {
                ["solo"] = FromCache(acc.Ranked.GetValueOrDefault("solo")),
                ["flex"] = FromCache(acc.Ranked.GetValueOrDefault("flex")),
            };
            return BuildView(acc, acc.Nick ?? "", ranked, []);
        }
        finally { Gate.Release(); }
    }

    private static Ranked FromCache(RankedCache? c) =>
        c is null ? new Ranked(false, "", "", 0, 0, 0)
                  : new Ranked(c.HasRank, c.Tier, c.Div, c.Lp, c.Wins, c.Losses);

    private sealed record Summoner(string Puuid, string Nick);

    // Кто сейчас в клиенте: puuid (ключ аккаунта) + ник (Riot ID gameName).
    // Спрашиваем на каждом обновлении — иначе смена аккаунта осталась бы незамеченной.
    private static async Task<Summoner?> FetchSummonerAsync(LcuHttpClient http, CancellationToken ct)
    {
        try
        {
            var (s, body) = await http.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (s != 200) return null;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var puuid = root.TryGetProperty("puuid", out var p) ? p.GetString() : null;
            if (string.IsNullOrEmpty(puuid)) return null;

            var nick = root.TryGetProperty("gameName", out var gn) ? gn.GetString() : null;
            if (string.IsNullOrEmpty(nick))
                nick = root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;

            return new Summoner(puuid, nick ?? "");
        }
        catch { return null; }
    }

    private static async Task<Dictionary<string, Ranked>?> FetchRankedAsync(LcuHttpClient http, CancellationToken ct)
    {
        var (rs, body) = await http.GetAsync("/lol-ranked/v1/current-ranked-stats", ct);
        if (rs != 200) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var result = new Dictionary<string, Ranked>
            {
                ["solo"] = ParseQueue(doc.RootElement, "RANKED_SOLO_5x5"),
                ["flex"] = ParseQueue(doc.RootElement, "RANKED_FLEX_SR"),
            };
            return result;
        }
        catch { return null; }

        static Ranked ParseQueue(JsonElement root, string key)
        {
            if (!root.TryGetProperty("queueMap", out var qm) || !qm.TryGetProperty(key, out var q))
                return new Ranked(false, "", "", 0, 0, 0);
            var tier = q.TryGetProperty("tier", out var t) ? t.GetString() ?? "" : "";
            var div  = q.TryGetProperty("division", out var d) ? d.GetString() ?? "" : "";
            int Get(string name) =>
                q.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            // У LCU дивизион "NA" у безранговых/мастера+.
            if (div == "NA") div = "";
            return new Ranked(!string.IsNullOrEmpty(tier), tier, div, Get("leaguePoints"), Get("wins"), Get("losses"));
        }
    }

    /// Союзник в прошлой игре: кто, на ком и НА КАКОЙ РОЛИ. Роль нужна связкам:
    /// дуо-пул собирают под конкретную пару линий, а без роли все совместные
    /// игры валились в одну кучу — мы вдвоём на боте и мы же в лесу с мидом.
    /// Пусто — роль установить не удалось.
    private sealed record Ally(string Puuid, int ChampionId, string Name, string Role);

    /// Роли своей пятёрки в прошлой игре: participantId → роль базы.
    ///
    /// Клиент отдаёт `timeline.lane`/`role`, но верить им можно не везде. Замер
    /// на живой истории (19 игр, 38 команд): все пять ролей различимы лишь в
    /// **16%** случаев — топ-лейнеров он сплошь метит джунглерами. Надёжна одна
    /// бот-линия: **66%**. Лучшего источника у клиента нет — `teamPosition`
    /// отсутствует, `/timeline` отдаёт 404, в `stats` роли тоже нет.
    ///
    /// Поэтому берём у него то, что он знает точно (кэрри и саппорт на боте), а
    /// остальные три роли достраиваем по доле роли чемпиона — той же
    /// механикой, что раскладывает роли вражеской команде в драфте.
    /// Публична ради проверки: путь «прошлая игра → роли» иначе достижим только
    /// через живой клиент и запись в файл игрока.
    public static Dictionary<int, string> RolesOfTeam(
        IReadOnlyList<(int Pid, int Champ, string Lane, string Role)> team,
        Func<int, string, double>? roleShare)
    {
        var res = new Dictionary<int, string>();

        // 1. Бот-линия — прямо из ответа клиента, но только если она читается
        //    однозначно: ровно один кэрри и ровно один саппорт.
        var carry = team.Where(t => t.Lane == "BOTTOM" && t.Role == "CARRY").ToList();
        var sup   = team.Where(t => t.Lane == "BOTTOM" && t.Role == "SUPPORT").ToList();
        if (carry.Count == 1) res[carry[0].Pid] = "adc";
        if (sup.Count == 1)   res[sup[0].Pid]   = "support";

        // 2. Остальные — жадно по доле роли: сперва самые однозначные пары.
        if (roleShare is null) return res;
        var free  = team.Where(t => !res.ContainsKey(t.Pid)).ToList();
        var roles = new[] { "top", "jungle", "mid", "adc", "support" }
                    .Where(r => !res.ContainsValue(r)).ToList();
        var pairs = free.SelectMany(t => roles.Select(r => (t.Pid, Role: r, Share: roleShare(t.Champ, r))))
                        .Where(x => x.Share > 0)
                        .OrderByDescending(x => x.Share).ToList();
        foreach (var (pid, role, _) in pairs)
            if (!res.ContainsKey(pid) && !res.ContainsValue(role)) res[pid] = role;
        return res;
    }

    /// <summary>
    /// Состав СВОЕЙ команды в одной прошлой игре.
    ///
    /// Сводка истории кладёт в participants только меня — состава там нет. Он
    /// есть в подробной игре, вместе с participantIdentities, где у каждого
    /// лежит puuid. Отсюда и берём напарника.
    ///
    /// Ремейки отдают одного участника вместо десяти (проверено на игре в 90
    /// секунд) — тогда просто вернётся пустой список, и связок не прибавится.
    /// </summary>
    private static async Task<(List<Ally> Allies, string MyRole)> FetchAlliesAsync(
        LcuHttpClient http, long gameId, string myPuuid,
        Func<int, string, double>? roleShare, CancellationToken ct)
    {
        var allies = new List<Ally>();
        var myRole = "";
        try
        {
            var (s, body) = await http.GetAsync($"/lol-match-history/v1/games/{gameId}", ct);
            if (s != 200) return (allies, myRole);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("participants", out var parts)
                || !root.TryGetProperty("participantIdentities", out var ids)
                || parts.ValueKind != JsonValueKind.Array
                || ids.ValueKind != JsonValueKind.Array) return (allies, myRole);

            // participantId → puuid и ник. Ник берём riot-овский (gameName), а
            // если его нет — старое имя призывателя.
            var puuidOf = new Dictionary<int, string>();
            var nameOf = new Dictionary<int, string>();
            foreach (var e in ids.EnumerateArray())
            {
                if (!e.TryGetProperty("player", out var pl)) continue;
                var pu = pl.TryGetProperty("puuid", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(pu)) continue;
                var pid = GetInt(e, "participantId");
                puuidOf[pid] = pu;
                var nm = pl.TryGetProperty("gameName", out var gn) ? gn.GetString() : null;
                if (string.IsNullOrEmpty(nm))
                    nm = pl.TryGetProperty("summonerName", out var sn) ? sn.GetString() : null;
                nameOf[pid] = nm ?? "";
            }

            // Моя команда — по моему же participantId.
            int myTeam = -1;
            foreach (var e in parts.EnumerateArray())
                if (puuidOf.GetValueOrDefault(GetInt(e, "participantId")) == myPuuid)
                    myTeam = GetInt(e, "teamId", -1);
            if (myTeam < 0) return (allies, myRole);

            // Роли своей пятёрки: бот-линия из ответа клиента, остальные по
            // доле роли (см. RolesOfTeam — метки клиента верны только на боте).
            var mine = new List<(int Pid, int Champ, string Lane, string Role)>();
            foreach (var e in parts.EnumerateArray())
            {
                if (GetInt(e, "teamId", -1) != myTeam) continue;
                string lane = "", role = "";
                if (e.TryGetProperty("timeline", out var tl))
                {
                    if (tl.TryGetProperty("lane", out var l)) lane = l.GetString() ?? "";
                    if (tl.TryGetProperty("role", out var r)) role = r.GetString() ?? "";
                }
                mine.Add((GetInt(e, "participantId"), GetInt(e, "championId"), lane, role));
            }
            var roleOf = RolesOfTeam(mine, roleShare);

            foreach (var e in parts.EnumerateArray())
            {
                if (GetInt(e, "teamId", -1) != myTeam) continue;
                var pid = GetInt(e, "participantId");
                var pu = puuidOf.GetValueOrDefault(pid);
                if (pu == myPuuid) { myRole = roleOf.GetValueOrDefault(pid, ""); continue; }
                if (string.IsNullOrEmpty(pu)) continue;
                var champ = GetInt(e, "championId");
                if (champ > 0)
                    allies.Add(new Ally(pu, champ, nameOf.GetValueOrDefault(pid, ""),
                                        roleOf.GetValueOrDefault(pid, "")));
            }
        }
        catch { /* подробная игра недоступна — связки просто не пополнятся */ }
        return (allies, myRole);
    }

    private static int GetInt(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

    /// <summary>
    /// Ключ связки: кто, в какой очереди, на какой паре.
    ///
    /// Очередь в ключе нужна, чтобы флекс не смешивался с нормалами: дуо-пул
    /// собирают под конкретную очередь, и общий котёл смазывает картину. Слева
    /// в окне винрейты по чемпионам разделены ровно по той же причине.
    ///
    /// Порядок частей фиксирован, мой чемпион всегда первым.
    /// </summary>
    /// <summary>
    /// Разбор ключа связки. null —形а незнакомая, запись пропускаем.
    ///
    /// Публичен ради проверки: формат ключа — то, чем связка хранится годами, и
    /// ошибка в нём тихо теряет накопленный счёт.
    /// </summary>
    public static (string Puuid, string Queue, int Mine, int His, string MyRole, string HisRole)?
        ParsePairKey(string key)
    {
        // puuid '|' не содержит, поэтому делим целиком. Частей четыре или шесть:
        // записи, сделанные до того, как роли начали храниться, короче, и
        // переразметить их нечем — роли в истории не сохранялись.
        var part = key.Split('|');
        if (part.Length is not (4 or 6)) return null;
        if (!int.TryParse(part[2], out var mine)) return null;
        if (!int.TryParse(part[3], out var his)) return null;
        return (part[0], part[1], mine, his,
                part.Length == 6 ? part[4] : "",
                part.Length == 6 ? part[5] : "");
    }

    /// Роли — в конце и только если известны обе: старые записи (без ролей)
    /// остаются рабочими, их ключ просто короче. Переразметить их нечем — роли
    /// в истории не хранились, — поэтому они и дальше считаются «роль неизвестна».
    public static string PairKey(string allyPuuid, string queue, int myChampion, int allyChampion,
                                  string myRole = "", string allyRole = "") =>
        myRole.Length > 0 && allyRole.Length > 0
            ? $"{allyPuuid}|{queue}|{myChampion}|{allyChampion}|{myRole}|{allyRole}"
            : $"{allyPuuid}|{queue}|{myChampion}|{allyChampion}";

    // Последние игры из истории LCU: gameId, очередь, чемпион, победа.
    private static async Task<List<HistEntry>> FetchHistoryAsync(LcuHttpClient http, CancellationToken ct)
    {
        var result = new List<HistEntry>();
        try
        {
            var (s, body) = await http.GetAsync(
                "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=20", ct);
            if (s != 200) return result;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("games", out var wrap)) return result;
            if (!wrap.TryGetProperty("games", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var g in arr.EnumerateArray())
            {
                var queue = QueueOf(g.TryGetProperty("queueId", out var qid) && qid.ValueKind == JsonValueKind.Number ? qid.GetInt32() : 0);
                if (queue is null) continue;
                long gameId = g.TryGetProperty("gameId", out var gid) && gid.ValueKind == JsonValueKind.Number ? gid.GetInt64() : 0;
                if (gameId == 0) continue;
                if (!g.TryGetProperty("participants", out var parts) || parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0) continue;
                var p = parts[0];
                int champ = p.TryGetProperty("championId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : 0;
                bool win = p.TryGetProperty("stats", out var st) && st.TryGetProperty("win", out var w) && w.ValueKind == JsonValueKind.True;
                long created = g.TryGetProperty("gameCreation", out var cr) && cr.ValueKind == JsonValueKind.Number
                    ? cr.GetInt64() / 1000 : 0;
                // Ремейки (< 5 минут) не считаем — они не влияют на статистику.
                long duration = g.TryGetProperty("gameDuration", out var du) && du.ValueKind == JsonValueKind.Number
                    ? du.GetInt64() : long.MaxValue;
                if (duration < 300) continue;
                result.Add(new HistEntry(gameId, queue, champ, win, created));
            }
            // Новейшие первыми (как отдаёт LCU) — гарантируем сортировкой.
            result.Sort((a, b) => b.GameId.CompareTo(a.GameId));
        }
        catch { /* история недоступна */ }
        return result;
    }
}
