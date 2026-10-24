using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — мои чемпионы: статистика по чемпионам и чемпионы на линии за месяц.
public static partial class SessionTracker
{
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
        lock (StatsCache) { StatsCache.Clear(); _draftTimes = null; }
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

        // Песочница: свой винрейт — по её подменной истории игр (см. PreviewGames).
        if (PreviewGames is { } preview)
            return preview
                .Where(g => g.MyChampionId != 0 && queues.Contains(g.Queue) && g.Ts >= since)
                .GroupBy(g => g.MyChampionId)
                .ToDictionary(x => x.Key, x => (x.Count(), x.Count(g => g.Win)));

        var key = days + "|" + string.Join(",", queues);
        lock (StatsCache)
        {
            if (StatsCache.TryGetValue(key, out var c) && (DateTime.UtcNow - c.At).TotalSeconds < 20)
                return c.Map.ToDictionary(kv => kv.Key, kv => (kv.Value.G, kv.Value.W));
        }

        var map = new Dictionary<int, (int G, int W)>();
        try
        {
            var acc = JournalAccount(Load());
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

    /// Чей журнал читать: последний аккаунт в клиенте, иначе первый в файле.
    private static Account? JournalAccount(Store s) =>
        s.LastAccount is { Length: > 0 } k && s.Accounts.TryGetValue(k, out var a)
            ? a : s.Accounts.Values.FirstOrDefault();

    /// <summary>
    /// Сколько из последних <paramref name="last"/> игр журнала (все очереди,
    /// без окна по дням) сыграно в <paramref name="queues"/>. По нему лента
    /// «мои чемпионы» решает, есть ли что показывать: у того, кто играет ARAM,
    /// две случайные нормалки за месяц ничего не говорят о его чемпионах.
    /// </summary>
    public static int RecentGamesIn(int last, params string[] queues)
    {
        if (PreviewGames is { } preview)
            return preview.OrderByDescending(g => g.Ts).Take(last).Count(g => queues.Contains(g.Queue));
        try
        {
            var acc = JournalAccount(Load());
            if (acc is null) return 0;
            return acc.Queues
                .SelectMany(q => q.Value.Games.Select(g => (Queue: q.Key, g.Ts)))
                .OrderByDescending(g => g.Ts)
                .Take(last)
                .Count(g => queues.Contains(g.Queue));
        }
        catch { return 0; }   // журнала нет или он битый — игр нет
    }

    /// <summary>
    /// Мои чемпионы в ранкеде и нормалах за окно (0 и меньше — за всё время):
    /// больше игр — выше, при равенстве — кто сыгран позже. Порядок один для
    /// полосы на экране готовности и для винрейтов в окне пулов. Раньше при
    /// равенстве ранговые стояли впереди нормалов просто по порядку чтения, и
    /// вчерашние нормалы уезжали в конец.
    /// </summary>
    public static List<int> PlayedChamps(int days)
    {
        var since = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds() : 0;
        string[] queues = [.. QueuesRanked, .. QueuesNormal];
        var games = new List<(int Champ, long Ts)>();
        if (PreviewGames is { } preview)
            games.AddRange(preview.Where(g => queues.Contains(g.Queue)).Select(g => (g.MyChampionId, g.Ts)));
        else
            try
            {
                var acc = JournalAccount(Load());
                if (acc != null)
                    foreach (var q in queues)
                        if (acc.Queues.TryGetValue(q, out var ql))
                            games.AddRange(ql.Games.Select(g => (g.ChampionId, g.Ts)));
            }
            catch { /* журнала нет или он битый — чемпионов нет */ }

        return games
            .Where(g => g.Champ != 0 && (since == 0 || g.Ts <= 0 || g.Ts >= since))
            .GroupBy(g => g.Champ)
            .OrderByDescending(x => x.Count())
            .ThenByDescending(x => x.Max(g => g.Ts))
            .Select(x => x.Key)
            .ToList();
    }

    /// Личная статистика по одному чемпиону за последние RecentDays дней.
    public static (int Games, int Wins) ChampStats(int championId, params string[] queues)
    {
        var m = ChampStatsMap(queues);
        return m.TryGetValue(championId, out var v) ? v : (0, 0);
    }

    // Времена моих игр с драфтом, по возрастанию, — в том же коротком кэше.
    private static (DateTime At, long[] Ts)? _draftTimes;

    /// <summary>
    /// Сколько игр с драфтом (соло, флекс, нормалы — без ARAM) сыграно после
    /// отметки времени, unix-секунды. Этим счётом «не советовать» (Dislikes)
    /// отмеряет, сколько игр чемпион скрыт.
    /// </summary>
    public static int GamesSince(long since)
    {
        if (PreviewGames is { } preview)
            return preview.Count(g => LaneQueues.Contains(g.Queue) && g.Ts > since);

        long[] ts;
        lock (StatsCache)
        {
            if (_draftTimes is { } c && (DateTime.UtcNow - c.At).TotalSeconds < 20) ts = c.Ts;
            else
            {
                var list = new List<long>();
                try
                {
                    var acc = JournalAccount(Load());
                    if (acc != null)
                        foreach (var q in LaneQueues)
                            if (acc.Queues.TryGetValue(q, out var ql))
                                list.AddRange(ql.Games.Where(g => g.Ts > 0).Select(g => g.Ts));
                }
                catch { /* журнала нет или он битый — игр нет */ }
                list.Sort();
                ts = [.. list];
                _draftTimes = (DateTime.UtcNow, ts);
            }
        }
        // Первая игра позже отметки: всё правее неё — сыграно после.
        int lo = 0, hi = ts.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (ts[mid] > since) hi = mid; else lo = mid + 1;
        }
        return ts.Length - lo;
    }

    // ── Мои чемпионы на линии (фаза планирования) ───────────────────────────

    /// Очереди для «моих чемпионов на линии» — те же, что у винрейта за 30 дней:
    /// ранкед и нормалы, без ARAM (там чемпион случайный).
    private static readonly string[] LaneQueues = [.. QueuesRanked, .. QueuesNormal];

    /// Сколько последних исходов показывать полосой формы.
    public const int FormGames = 8;

    /// <summary>
    /// Чемпион на линии за окно: игры, победы, сумма LP в ранкеде (null — ни
    /// одной ранкед-игры с известным LP), форма — исходы последних
    /// <see cref="FormGames"/> игр от старых к новым, время последней игры,
    /// роль, на которой я играл его чаще всего за окно.
    /// </summary>
    public sealed record LaneChamp(int ChampionId, int Games, int Wins, int? Lp,
                                   IReadOnlyList<bool> Form, long LastTs, string Role = "")
    {
        public double WinRate => Games > 0 ? 100.0 * Wins / Games : 0;
    }

    /// <summary>
    /// Мои чемпионы на роли за <paramref name="days"/> дней, самые игранные
    /// первыми. Для фазы планирования: показываются только игры ВЫДАННОЙ роли —
    /// если меня выкинуло в лес, саппорты, которыми я играю, там ни к чему.
    ///
    /// Роль игры: записанная в журнал (с 1.3.61) → из связок той же игры (в них
    /// роль есть с 30.09) → по составу той же игры: в связках до 30.09 ролей
    /// нет, но есть чемпионы союзников, и пятёрка раскладывается по доле ролей
    /// (<see cref="RolesOfTeam"/>) → на какой роли я чаще играю этого чемпиона
    /// по связкам → его главная роль в базе. Одной главной роли мало: у
    /// владельца Эш — саппорт (8 игр по связкам), а база числит её стрелком;
    /// Зерат шёл в мид, хотя с ним были Кассадин, Мастер Йи, Зери и Атрокс.
    /// <paramref name="roleShare"/> — доля игр чемпиона на роли (из базы).
    /// </summary>
    public static IReadOnlyList<LaneChamp> LaneChamps(string dbRole, Func<int, string, double>? roleShare,
                                                      int days = RecentDays)
    {
        if (dbRole.Length == 0) return [];
        return Summarize(RecentLaneGames(roleShare, days).Where(g => g.Role == dbRole));
    }

    /// <summary>
    /// Мои чемпионы за <paramref name="days"/> дней на ВСЕХ ролях, самые
    /// игранные первыми, у каждого — роль, на которой я играл его чаще всего.
    /// Для драфта без роли (вслепую, своя игра): линию там не выдают, и
    /// показываются все мои чемпионы. Роли игр — как у <see cref="LaneChamps"/>.
    /// </summary>
    public static IReadOnlyList<LaneChamp> RecentChamps(Func<int, string, double>? roleShare,
                                                        int days = RecentDays)
        => Summarize(RecentLaneGames(roleShare, days));

    // Игры за окно, каждая с моей ролью: из подменной истории песочницы или из журнала.
    private static List<(long Ts, int Champ, bool Win, int? Lp, string Role)> RecentLaneGames(
        Func<int, string, double>? roleShare, int days)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();
        var games = new List<(long Ts, int Champ, bool Win, int? Lp, string Role)>();

        if (PreviewGames is { } preview)
        {
            // Песочница: в подменной истории у каждой игры уже есть моя роль.
            games.AddRange(preview
                .Where(g => g.MyChampionId != 0 && g.Ts >= since && LaneQueues.Contains(g.Queue))
                .Select(g => (g.Ts, g.MyChampionId, g.Win, (int?)null, g.MyRole)));
        }
        else
        {
            try
            {
                var s = Load();
                var acc = s.LastAccount is { Length: > 0 } k && s.Accounts.TryGetValue(k, out var a)
                    ? a : s.Accounts.Values.FirstOrDefault();
                if (acc != null) games.AddRange(LaneGames(acc, since, roleShare));
            }
            catch { /* журнала нет или он битый — показывать нечего */ }
        }
        return games;
    }

    // Игры → чемпионы: самые игранные первыми, при равенстве — недавние.
    private static IReadOnlyList<LaneChamp> Summarize(
        IEnumerable<(long Ts, int Champ, bool Win, int? Lp, string Role)> games) =>
        games.GroupBy(g => g.Champ)
            .Select(x =>
            {
                var ordered = x.OrderBy(g => g.Ts).ToList();
                var lps = ordered.Where(g => g.Lp is not null).Select(g => g.Lp!.Value).ToList();
                return new LaneChamp(x.Key, ordered.Count, ordered.Count(g => g.Win),
                                     lps.Count > 0 ? lps.Sum() : null,
                                     ordered.TakeLast(FormGames).Select(g => g.Win).ToList(),
                                     ordered[^1].Ts,
                                     ordered.GroupBy(g => g.Role).MaxBy(r => r.Count())!.Key);
            })
            .OrderByDescending(c => c.Games).ThenByDescending(c => c.LastTs)
            .ToList();

    // Игры журнала за окно — каждая со своей ролью (см. LaneChamps).
    //
    // По шагам, по ВСЕМ играм журнала, не только за окно:
    // 1) записанная роль (из драфта, с 1.3.61) — окончательная;
    // 2) остальным — по составу той же игры из связок (`MyRoleByTeam`): вся
    //    пятёрка раскладывается по долям ролей. Роль, записанная в связке
    //    (с 30.09), — только перевес (`Team.Hint`);
    // 3) где состав не решает уверенно (UnsureRatio) — пересчёт с учётом того,
    //    на каких ролях я уверенно играл ЭТОГО чемпиона: Трандл 28.09 по одному
    //    составу — лесник, а 2.10 он ясно саппорт;
    // 4) игры без связок — как я обычно играю этого чемпиона, иначе его главная
    //    роль в базе.
    //
    // Склонности игрока «вообще» (саппорт-мейн — значит, всё саппорт) не
    // учитываются намеренно: у того, кто берёт нишевые пики на разных линиях,
    // они ломали бы ровно эти игры (владелец, 12.10). Только сама игра и сам
    // чемпион.
    private static IEnumerable<(long Ts, int Champ, bool Win, int? Lp, string Role)> LaneGames(
        Account acc, long since, Func<int, string, double>? roleShare)
    {
        // Состав игр из связок: (очередь, мой чемпион) → (время начала игры,
        // чемпион союзника, моя роль из связки или "").
        var comps = new Dictionary<(string Queue, int Champ), List<(long Ts, int Ally, string Mine)>>();
        foreach (var (key, rec) in acc.Pairs)
        {
            if (ParsePairKey(key) is not { } p) continue;
            if (!comps.TryGetValue((p.Queue, p.Mine), out var list)) comps[(p.Queue, p.Mine)] = list = [];
            foreach (var ts in rec.Won.Concat(rec.Lost)) list.Add((ts, p.His, p.MyRole));
        }

        var all = new List<Lane>();
        foreach (var queue in LaneQueues)
        {
            if (!acc.Queues.TryGetValue(queue, out var ql)) continue;
            foreach (var g in ql.Games)
            {
                if (g.ChampionId == 0 || g.Ts <= 0) continue;
                var game = new Lane(g, queue);
                if (g.Role is { Length: > 0 } r) game.Role = r;
                else if (TeamOf(comps.GetValueOrDefault((queue, g.ChampionId)), g.Ts) is { } team)
                {
                    game.Team = team;
                    if (roleShare is not null
                        && MyRoleByTeam(team.Champs(g.ChampionId), roleShare, team.Hint) is { } t)
                        (game.Role, game.Sure) = (t.Role, t.Confidence >= UnsureRatio);
                }
                all.Add(game);
            }
        }

        // Неуверенные по составу — ещё раз, с тем, на каких ролях я уверенно
        // играл этого чемпиона (доли сглажены: одна игра — подсказка, не приговор).
        var sure = all.Where(x => x.Role is not null && (x.G.Role is { Length: > 0 } || x.Sure))
            .GroupBy(x => x.G.ChampionId)
            .ToDictionary(g => g.Key, g => Roles.ToDictionary(
                r => r, r => (g.Count(x => x.Role == r) + 1.0) / (g.Count() + Roles.Length)));
        foreach (var x in all)
        {
            if (x.Team is not { } team || x.Sure || roleShare is null) continue;
            if (!sure.TryGetValue(x.G.ChampionId, out var played)) continue;
            if (MyRoleByTeam(team.Champs(x.G.ChampionId), roleShare, r => team.Hint(r) * played[r]) is { } t)
                x.Role = t.Role;
        }

        // Как я обычно играю чемпиона — по играм, где роль нашлась.
        var usual = all.Where(x => x.Role is not null).GroupBy(x => x.G.ChampionId)
            .ToDictionary(x => x.Key, x => x.GroupBy(y => y.Role!).MaxBy(y => y.Count())!.Key);

        foreach (var x in all)
        {
            if (x.G.Ts < since) continue;
            var r = x.Role ?? usual.GetValueOrDefault(x.G.ChampionId) ?? PrimaryRole(x.G.ChampionId, roleShare) ?? "";
            yield return (x.G.Ts, x.G.ChampionId, x.G.Win, QueuesRanked.Contains(x.Queue) ? x.G.Lp : null, r);
        }
    }
}
