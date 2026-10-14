using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — связки и напарники: история пар, статистика с напарником, превью для песочницы.
public static partial class SessionTracker
{
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
        if (PreviewGames is { } preview) return PreviewHistory(preview, freshDays, queues);

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
        if (Fake(0) is { } fake)
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
        if (Fake(0) is { } fake)
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
        // Подменные игры песочницы время знают — окно работает и на них. Готовые
        // подменные связки (проверки) времени не знают и идут целиком.
        if (days > 0 && PreviewGames is not null)
        {
            var fake = Fake(DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds())!
                .Where(p => string.IsNullOrEmpty(allyPuuid)
                            || p.AllyPuuid.Equals(allyPuuid, StringComparison.OrdinalIgnoreCase)).ToList();
            return (fake.Sum(p => p.Games), fake.Sum(p => p.Wins));
        }
        if (Fake(0) is not null || days <= 0) return MateStats(allyPuuid);
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

    /// Одна подменная совместная игра: с кем, на чём, на каких ролях, когда и чем
    /// кончилась.
    public sealed record PreviewGame(
        string AllyPuuid, string AllyName, string Queue,
        int MyChampionId, int AllyChampionId, string MyRole, string AllyRole,
        long Ts, bool Win);

    /// <summary>
    /// Подменная ИСТОРИЯ совместных игр — для песочницы. В отличие от
    /// <see cref="Preview"/>, у каждой игры есть время, поэтому работает окно
    /// «за 30 дней», а связки складываются из игр так же, как настоящие. Задана —
    /// главнее <see cref="Preview"/>, и журнал игр (свой винрейт, наигранность,
    /// мейны для банов) тоже берётся из неё, а не из настоящего. В боевом режиме
    /// всегда null.
    /// </summary>
    public static IReadOnlyList<PreviewGame>? PreviewGames { get; set; }

    /// <summary>
    /// Журнал игр, собранный из подменной истории песочницы: в каждой записи о
    /// совместной игре уже есть мой чемпион, очередь, время и исход. Без этого
    /// песочница считала наигранность и личный винрейт по НАСТОЯЩЕМУ журналу
    /// игрока, а связки — по подменному, и подбор выходил смесью двух историй.
    /// </summary>
    internal static PlayHistory PreviewHistory(IReadOnlyList<PreviewGame> preview, int freshDays,
                                              string[] queues)
    {
        var now   = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var since = now - (long)freshDays * 86400;
        var games = preview.Where(g => g.MyChampionId != 0 && g.Ts > 0 && queues.Contains(g.Queue)).ToList();
        var recent = games.Where(g => g.Ts >= since)
            .GroupBy(g => g.MyChampionId)
            .ToDictionary(x => x.Key, x => (x.Count(), x.Count(g => g.Win)));
        var times = games.GroupBy(g => g.MyChampionId)
            .ToDictionary(x => x.Key,
                          x => x.Select(g => g.Ts).OrderByDescending(t => t).Take(PlayHistory.KeepTimes).ToArray());
        var oldest = games.Count > 0 ? games.Min(g => g.Ts) : 0;
        var span   = oldest > 0 ? Math.Max(0.0, (now - oldest) / 86400.0) : 0.0;
        return new PlayHistory(recent, times, span, now);
    }

    /// Подменные связки не раньше <paramref name="since"/>: из игр, если они
    /// заданы, иначе готовые (у тех времени нет). null — подмены нет.
    private static IReadOnlyList<PairStat>? Fake(long since)
    {
        if (PreviewGames is { } games)
            return games
                .Where(g => g.Ts >= since)
                .GroupBy(g => (g.AllyPuuid, g.Queue, g.MyChampionId, g.AllyChampionId, g.MyRole, g.AllyRole))
                .Select(x => new PairStat(x.Key.AllyPuuid, x.Last().AllyName, x.Key.Queue,
                                          x.Key.MyChampionId, x.Key.AllyChampionId,
                                          x.Count(), x.Count(g => g.Win), x.Key.MyRole, x.Key.AllyRole))
                .ToList();
        return Preview;
    }

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
        foreach (var key in PairsToForget(
                     acc.Pairs.Select(kv => (kv.Key, kv.Value.Count(0).Games, kv.Value.Last())), now))
            acc.Pairs.Remove(key);
    }

    /// <summary>
    /// Само правило чистки — отдельно от журнала, потому что его применяет и
    /// синхронизация (<see cref="SyncClient"/>): она складывает связки двух
    /// компьютеров и без этого правила возвращала бы всё, что журнал уже
    /// забыл. Так и было: 400 после чистки → 461 после слияния, после каждой
    /// игры, и копия на сервере только росла.
    ///
    /// При равной давности порядок решает ключ: оба места обязаны выбрать
    /// ОДНИ И ТЕ ЖЕ связки, иначе компьютеры перекидывались бы ими по кругу.
    /// </summary>
    internal static HashSet<string> PairsToForget(
        IEnumerable<(string Key, int Games, long Last)> pairs, long now)
    {
        const int MaxPairs = 400;          // с запасом: у постоянной пары их десятки
        const int OneOffDays = 30;

        var cutoff = now - OneOffDays * 86400L;
        var all = pairs.ToList();
        var drop = all.Where(p => p.Games <= 1 && p.Last < cutoff).Select(p => p.Key).ToHashSet();

        var left = all.Where(p => !drop.Contains(p.Key)).ToList();
        if (left.Count > MaxPairs)
            foreach (var p in left.OrderBy(p => p.Last).ThenBy(p => p.Key, StringComparer.Ordinal)
                                  .Take(left.Count - MaxPairs))
                drop.Add(p.Key);
        return drop;
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
        if (Fake(since) is { } fake)
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
    /// Разбор ключа связки. null — форма незнакомая, запись пропускаем.
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
}
