using System.Text.Json;

namespace Counterplay;

// Журнал игр — чужие игры в журнале аккаунта: правило пачки, доказательство по
// подробной игре в клиенте и отпечатки убранного для синхронизации.
public static partial class SessionTracker
{
    /// Сколько чужих записей с одним временем записи — уже пачка, а не совпадение
    /// (см. <see cref="ForeignGames"/>).
    private const int ForeignPack = 3;

    /// Сколько подробных игр спрашивать у клиента за одно обновление журнала.
    private const int AskPerRefresh = 20;

    /// <summary>
    /// Подробные игры, уже спрошенные в этой сессии: (аккаунт, номер игры) →
    /// опора чужой игры или null (игра этого аккаунта, клиент не ответил или
    /// своего аккаунта компьютера в ней нет). Чтобы не спрашивать каждую минуту.
    /// </summary>
    private static readonly Dictionary<(string Account, long Id), GameAnchor?> Asked = new();

    /// <summary>
    /// Чужие игры в журнале аккаунта: какие записи убрать.
    ///
    /// При смене аккаунта в клиенте журнал спрашивал «кто в клиенте» и «последние
    /// игры» в разные моменты, и история одного аккаунта ложилась в журнал
    /// другого целиком — разом, одним «сейчас», без LP. Номера этих игр при этом
    /// попадали в «виденные» (<paramref name="seen"/>) того, чужого аккаунта.
    ///
    /// Правило. <paramref name="others"/> — игры ДРУГИХ аккаунтов (история
    /// клиента или записи с номером). Чужая игра — та, чей номер есть в
    /// виденных этого аккаунта, а запись с тем же чемпионом, исходом и очередью
    /// сделана после её начала. И главное: такие записи должны лечь пачкой —
    /// не меньше трёх разных чужих игр с одним и тем же временем записи. Одна
    /// совпавшая запись — не улика: в дуо номер общей игры виден обоим, и своя
    /// более поздняя игра на том же чемпионе выглядела бы так же.
    /// </summary>
    public static HashSet<(string Queue, int Index)> ForeignGames(
        IReadOnlyList<GameRef> games, IReadOnlySet<long> seen, IEnumerable<GameAnchor> others)
    {
        var anchors = others.Where(a => seen.Contains(a.Id))
                            .GroupBy(a => a.Id).Select(x => x.First()).ToList();
        var byKey = games.Where(g => g.Id is null).ToLookup(g => (g.Queue, g.ChampionId, g.Win));

        // Время записи → сколько разных чужих игр могли бы лежать под ним.
        var hits = new Dictionary<long, int>();
        foreach (var a in anchors)
            foreach (var ts in byKey[(a.Queue, a.ChampionId, a.Win)]
                         .Where(g => g.Ts >= a.Start).Select(g => g.Ts).Distinct())
                hits[ts] = hits.GetValueOrDefault(ts) + 1;

        var res = new HashSet<(string, int)>();
        foreach (var (ts, _) in hits.Where(kv => kv.Value >= ForeignPack))
            foreach (var a in anchors)
                if (a.Start <= ts && byKey[(a.Queue, a.ChampionId, a.Win)]
                        .FirstOrDefault(g => g.Ts == ts && !res.Contains((g.Queue, g.Index))) is { } r)
                    res.Add((r.Queue, r.Index));
        return res;
    }

    /// Убрать из журналов ДРУГИХ аккаунтов игры текущего (см. <see cref="ForeignGames"/>).
    /// Опоры — его история клиента и его записи с номером. Возвращает, сколько убрано.
    private static int DropForeignGames(Store store, string current, IEnumerable<HistEntry> history)
    {
        if (!store.Accounts.TryGetValue(current, out var me)) return 0;
        var mine = history.Where(h => h.CreatedSec > 0)
            .Select(h => new GameAnchor(h.GameId, h.CreatedSec, h.Queue, h.ChampionId, h.Win))
            .Concat(me.Queues.SelectMany(kv => kv.Value.Games
                .Where(g => g.Id is not null && g.Start is not null)
                .Select(g => new GameAnchor(g.Id!.Value, g.Start!.Value, kv.Key, g.ChampionId, g.Win))))
            .ToList();
        if (mine.Count == 0) return 0;

        var removed = 0;
        foreach (var (key, acc) in store.Accounts)
        {
            if (key == current || acc.SeenGames.Count == 0) continue;
            foreach (var byQueue in ForeignGames(Refs(acc), acc.SeenGames.ToHashSet(), mine).GroupBy(x => x.Queue))
            {
                var games = acc.Queues[byQueue.Key].Games;
                foreach (var i in byQueue.Select(x => x.Index).OrderByDescending(x => x))
                    games.RemoveAt(i);
                removed += byQueue.Count();
            }
        }
        return removed;
    }

    /// <summary>
    /// Доказать чужие игры по клиенту и убрать их — в журнале любого аккаунта,
    /// а не только того, кто сейчас вошёл.
    ///
    /// <see cref="DropForeignGames"/> убирает игры ТЕКУЩЕГО аккаунта из журналов
    /// других, и опоры у него — свежая история (около двадцати игр). Владелец
    /// однажды вошёл на чужом компьютере, его игры легли в журнал хозяйки — и
    /// там и остались: она входит своим аккаунтом, его история ей не видна, а сам
    /// он туда больше не заходил. Через десять дней «последние пять» в её профиле
    /// всё ещё показывали его соло-игры, «винрейт за 30 дней» — его чемпионов.
    ///
    /// Доказательство — подробная игра в клиенте: в ней puuid всех десяти
    /// участников. Номер из «виденных» аккаунта, среди участников которого
    /// этого аккаунта нет, а есть другой аккаунт этого компьютера, — его игра;
    /// опора — его чемпион, исход и начало. Дальше то же правило пачки
    /// (<see cref="ForeignGames"/>): доказанное «не своя» ещё не говорит, какая
    /// из записей журнала — от этой игры.
    ///
    /// Спрашиваем, только если в журнале аккаунта есть пачка — три записи без
    /// номера с одним временем записи: без неё правило всё равно ничего не
    /// уберёт. И только номера, у которых нет своей записи с номером. Убранное
    /// запоминается отпечатком (<see cref="Account.Foreign"/>): по нему слияние
    /// не пускает эти записи обратно с копии другого компьютера.
    /// </summary>
    private static async Task<int> ProveForeignAsync(LcuHttpClient http, Store store, string current,
        IReadOnlyCollection<HistEntry> history, CancellationToken ct)
    {
        var removed = 0;
        var budget = AskPerRefresh;
        foreach (var (key, acc) in store.Accounts)
        {
            var refs = Refs(acc);
            if (!refs.Where(g => g.Id is null).GroupBy(g => g.Ts).Any(x => x.Count() >= ForeignPack)) continue;

            var own = refs.Where(g => g.Id is not null).Select(g => g.Id!.Value).ToHashSet();
            if (key == current) own.UnionWith(history.Select(h => h.GameId));
            var others = store.Accounts.Keys.Where(k => k != key).ToHashSet();
            foreach (var id in acc.SeenGames)
            {
                if (budget == 0) break;
                if (own.Contains(id) || Asked.ContainsKey((key, id))) continue;
                budget--;
                Asked[(key, id)] = await FetchForeignAnchorAsync(http, id, key, others, ct);
            }

            var anchors = acc.SeenGames.Select(id => Asked.GetValueOrDefault((key, id)))
                                       .OfType<GameAnchor>().ToList();
            if (anchors.Count == 0) continue;
            foreach (var byQueue in ForeignGames(refs, acc.SeenGames.ToHashSet(), anchors).GroupBy(x => x.Queue))
            {
                var games = acc.Queues[byQueue.Key].Games;
                foreach (var i in byQueue.Select(x => x.Index).OrderByDescending(x => x))
                {
                    var g = games[i];
                    (acc.Foreign ??= []).Add(new ForeignMark
                        { Queue = byQueue.Key, Ts = g.Ts, ChampionId = g.ChampionId, Win = g.Win });
                    games.RemoveAt(i);
                }
                removed += byQueue.Count();
            }
        }
        return removed;
    }

    private static async Task<GameAnchor?> FetchForeignAnchorAsync(
        LcuHttpClient http, long gameId, string account, IReadOnlySet<string> others, CancellationToken ct)
    {
        try
        {
            var (s, body) = await http.GetAsync($"/lol-match-history/v1/games/{gameId}", ct);
            return s == 200 ? ForeignAnchor(gameId, body, account, others) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// Подробная игра глазами доказательства: если <paramref name="account"/>
    /// среди участников нет, а есть один из <paramref name="others"/> (другие
    /// аккаунты этого компьютера) — опора его игры: очередь, его чемпион, исход
    /// и начало. Иначе null: игра своя, участники без puuid или очередь не та,
    /// что ведёт журнал.
    /// </summary>
    public static GameAnchor? ForeignAnchor(long gameId, string json, string account, IReadOnlySet<string> others)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var queue = QueueOf(GetInt(root, "queueId"));
        if (queue is null
            || !root.TryGetProperty("participants", out var parts) || parts.ValueKind != JsonValueKind.Array
            || !root.TryGetProperty("participantIdentities", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;

        var puuidOf = new Dictionary<int, string>();
        foreach (var e in ids.EnumerateArray())
            if (e.TryGetProperty("player", out var pl) && pl.TryGetProperty("puuid", out var p)
                && p.GetString() is { Length: > 0 } pu)
                puuidOf[GetInt(e, "participantId")] = pu;
        if (puuidOf.ContainsValue(account)) return null;

        long start = root.TryGetProperty("gameCreation", out var cr) && cr.ValueKind == JsonValueKind.Number
            ? cr.GetInt64() / 1000 : 0;
        if (start <= 0) return null;
        foreach (var e in parts.EnumerateArray())
            if (puuidOf.TryGetValue(GetInt(e, "participantId"), out var pu) && others.Contains(pu))
            {
                var win = e.TryGetProperty("stats", out var st) && st.TryGetProperty("win", out var w)
                          && w.ValueKind == JsonValueKind.True;
                return new GameAnchor(gameId, start, queue, GetInt(e, "championId"), win);
            }
        return null;
    }

    /// Записи журнала аккаунта глазами правил.
    private static List<GameRef> Refs(Account acc) =>
        acc.Queues.SelectMany(kv => kv.Value.Games.Select((g, i) =>
            new GameRef(kv.Key, i, g.Ts, g.ChampionId, g.Win, g.Id, g.Start))).ToList();
}
