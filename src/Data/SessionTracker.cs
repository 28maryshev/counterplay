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
    }

    /// Счёт по одной связке. Отдельный класс, а не кортеж: лежит в json.
    private sealed class PairRec
    {
        public int Games { get; set; }
        public int Wins { get; set; }
        /// Ник напарника, каким он был в последней совместной игре. Только для
        /// показа: опознаём человека по puuid, ник может смениться.
        public string Name { get; set; } = "";
    }

    /// Одна связка наружу: с кем, на ком, с каким счётом.
    public sealed record PairStat(
        string AllyPuuid, string AllyName, int MyChampionId, int AllyChampionId,
        int Games, int Wins)
    {
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

    /// <summary>
    /// Винрейт КОНКРЕТНОЙ связки: сколько игр сыграно вдвоём с этим человеком
    /// именно на этой паре чемпионов и сколько из них выиграно.
    ///
    /// Это личная статистика игрока, а не общая синергия из базы: «мы вдвоём
    /// на этой паре 7-3», а не «эта пара в среднем выигрывает 52%».
    /// </summary>
    public static (int Games, int Wins) PairStats(string? allyPuuid, int myChampion, int allyChampion)
    {
        if (string.IsNullOrEmpty(allyPuuid) || myChampion == 0 || allyChampion == 0) return (0, 0);
        var acc = CurrentAccount();
        if (acc is null) return (0, 0);
        return acc.Pairs.TryGetValue(PairKey(allyPuuid, myChampion, allyChampion), out var r)
            ? (r.Games, r.Wins) : (0, 0);
    }

    /// <summary>
    /// Сколько всего игр сыграно с этим человеком (по всем парам чемпионов).
    /// Нужно, чтобы отличить «связки не было» от «связка есть, но новая».
    /// </summary>
    public static (int Games, int Wins) MateStats(string? allyPuuid)
    {
        if (string.IsNullOrEmpty(allyPuuid)) return (0, 0);
        var acc = CurrentAccount();
        if (acc is null) return (0, 0);
        var prefix = allyPuuid + "|";
        var g = 0; var w = 0;
        foreach (var (k, r) in acc.Pairs)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) { g += r.Games; w += r.Wins; }
        return (g, w);
    }

    /// <summary>
    /// Связки, сыгранные с этим человеком, — самые частые первыми.
    /// Пустой <paramref name="allyPuuid"/> — все связки со всеми.
    /// </summary>
    public static IReadOnlyList<PairStat> TopPairs(string? allyPuuid = null, int take = 30)
    {
        var acc = CurrentAccount();
        if (acc is null) return [];
        var res = new List<PairStat>();
        foreach (var (k, r) in acc.Pairs)
        {
            // Ключ: puuid|мой чемпион|его чемпион. puuid сам по себе '|' не содержит.
            var i = k.LastIndexOf('|');
            if (i <= 0) continue;
            var j = k.LastIndexOf('|', i - 1);
            if (j <= 0) continue;
            var pu = k[..j];
            if (!string.IsNullOrEmpty(allyPuuid)
                && !pu.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(k[(j + 1)..i], out var mine)) continue;
            if (!int.TryParse(k[(i + 1)..], out var his)) continue;
            res.Add(new PairStat(pu, r.Name, mine, his, r.Games, r.Wins));
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

    public static async Task<SessionData?> RefreshAsync(LcuHttpClient http, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await RefreshCoreAsync(http, ct); }
        finally { Gate.Release(); }
    }

    private static async Task<SessionData?> RefreshCoreAsync(LcuHttpClient http, CancellationToken ct)
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
                foreach (var a in await FetchAlliesAsync(http, h.GameId, who!.Puuid, ct))
                {
                    var key = PairKey(a.Puuid, h.ChampionId, a.ChampionId);
                    if (!acc.Pairs.TryGetValue(key, out var rec))
                        acc.Pairs[key] = rec = new PairRec();
                    rec.Games++;
                    if (h.Win) rec.Wins++;
                    if (a.Name.Length > 0) rec.Name = a.Name;   // ник мог смениться
                }
            }
            // Помним столько же, сколько и виденных игр: выпавшая из истории
            // игра второй раз оттуда не придёт, копить бесконечно незачем.
            acc.PairGames = history.Select(h => h.GameId).Concat(counted)
                                   .Distinct().Take(100).ToList();
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

        // 7) Представления по очередям.
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

    /// Союзник в прошлой игре: кто и на ком. Для винрейта связки.
    private sealed record Ally(string Puuid, int ChampionId, string Name);

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
    private static async Task<List<Ally>> FetchAlliesAsync(
        LcuHttpClient http, long gameId, string myPuuid, CancellationToken ct)
    {
        var allies = new List<Ally>();
        try
        {
            var (s, body) = await http.GetAsync($"/lol-match-history/v1/games/{gameId}", ct);
            if (s != 200) return allies;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("participants", out var parts)
                || !root.TryGetProperty("participantIdentities", out var ids)
                || parts.ValueKind != JsonValueKind.Array
                || ids.ValueKind != JsonValueKind.Array) return allies;

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
            if (myTeam < 0) return allies;

            foreach (var e in parts.EnumerateArray())
            {
                if (GetInt(e, "teamId", -1) != myTeam) continue;
                var pid = GetInt(e, "participantId");
                var pu = puuidOf.GetValueOrDefault(pid);
                if (string.IsNullOrEmpty(pu) || pu == myPuuid) continue;
                var champ = GetInt(e, "championId");
                if (champ > 0) allies.Add(new Ally(pu, champ, nameOf.GetValueOrDefault(pid, "")));
            }
        }
        catch { /* подробная игра недоступна — связки просто не пополнятся */ }
        return allies;
    }

    private static int GetInt(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

    /// Ключ связки. Порядок частей фиксирован: мой чемпион всегда первым.
    private static string PairKey(string allyPuuid, int myChampion, int allyChampion) =>
        $"{allyPuuid}|{myChampion}|{allyChampion}";

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
