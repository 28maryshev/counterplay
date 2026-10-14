namespace Counterplay;

// Журнал игр — одна игра, записанная дважды, и чужие игры в журнале: опознание
// по номеру игры и времени её начала. Правила общие для журнала (по истории
// клиента) и для синхронизации (по записям другого компьютера).
public static partial class SessionTracker
{
    /// <summary>
    /// Запись журнала глазами правила: где лежит (очередь и место в списке) и что
    /// о ней известно. <paramref name="Id"/> и <paramref name="Start"/> —
    /// номер игры в клиенте и время её начала, пишутся с 1.3.63.
    /// </summary>
    public sealed record GameRef(string Queue, int Index, long Ts, int ChampionId, bool Win,
                                 long? Id, long? Start);

    /// Игра, о которой точно известны номер и время начала: из истории клиента
    /// или из записи, где они уже есть.
    public sealed record GameAnchor(long Id, long Start, string Queue, int ChampionId, bool Win);

    /// <summary>
    /// Что сделать с одной игрой: записи <paramref name="Keep"/> дать номер и
    /// время начала, записи <paramref name="Drop"/> (та же игра) убрать.
    /// </summary>
    public sealed record SameGame(string Queue, int Keep, long Id, long Start, IReadOnlyList<int> Drop);

    /// Дольше этого от начала игры запись к ней не притягиваем.
    private const long SameGameWindow = 3 * 3600;

    /// <summary>
    /// Найти записи одной и той же игры.
    ///
    /// Одна игра попадала в журнал дважды, потому что записывают её по-разному.
    /// Компьютер, на котором играли, пишет её, когда увидел конец (время записи
    /// ≈ конец игры, и с LP). Новый компьютер берёт прошлые игры из истории
    /// клиента со временем НАЧАЛА и без LP. А компьютер, где в это время не
    /// играли, видит игру в истории позже и пишет её своим «сейчас». Слияние
    /// сводило игры по времени записи — у трёх записей одной игры оно разное, и
    /// после синхронизации у владельца каждая игра лежала дважды, а «последние
    /// пять» показывали копии без LP.
    ///
    /// Правило. У каждой опорной игры (номер и начало известны) есть окно: от
    /// её начала до начала следующей игры (любой очереди — играют по одной), но
    /// не дольше <see cref="SameGameWindow"/>. Ей принадлежат: записи с её
    /// номером; запись без номера ровно с её временем начала (взята из
    /// истории); и одна — самая ранняя — запись без номера внутри окна (видел
    /// конец игры), если такой записи с номером ещё нет. Всё — только с тем же
    /// чемпионом, исходом и очередью. Остаётся запись очевидца — самая ранняя
    /// после начала: она и с LP; если такой нет — первая.
    ///
    /// По одной записи на окно, а не все подряд: если программа была закрыта две
    /// игры, обе записи получат одно «сейчас» после начала второй — и вторая
    /// игра не должна съесть первую. Лучше оставить лишнюю запись, чем потерять
    /// игру.
    /// </summary>
    public static List<SameGame> SameGames(IReadOnlyList<GameRef> games, IEnumerable<GameAnchor> extra)
    {
        var anchors = extra
            .Concat(games.Where(g => g.Id is not null && g.Start is not null)
                         .Select(g => new GameAnchor(g.Id!.Value, g.Start!.Value, g.Queue, g.ChampionId, g.Win)))
            .GroupBy(a => a.Id).Select(x => x.First())
            .OrderBy(a => a.Start).ToList();

        // Корзины вместо перебора всего журнала на каждую опору: журнал растёт
        // весь сезон, а обновляется раз в минуту.
        var byId = games.Where(g => g.Id is not null).ToLookup(g => g.Id!.Value);
        var byKey = games.Where(g => g.Id is null)
                         .ToLookup(g => (g.Queue, g.ChampionId, g.Win));

        var taken = new HashSet<(string, int)>();
        var res = new List<SameGame>();
        for (var i = 0; i < anchors.Count; i++)
        {
            var a = anchors[i];
            var end = Math.Min(i + 1 < anchors.Count ? anchors[i + 1].Start : long.MaxValue,
                               a.Start + SameGameWindow);
            bool Free(GameRef g) => !taken.Contains((g.Queue, g.Index));
            var bucket = byKey[(a.Queue, a.ChampionId, a.Win)];

            var mine = byId[a.Id].Where(g => g.Queue == a.Queue && g.ChampionId == a.ChampionId
                                             && g.Win == a.Win && Free(g)).ToList();
            if (!mine.Any(g => g.Ts == a.Start)
                && bucket.FirstOrDefault(g => g.Ts == a.Start && Free(g)) is { } fromHistory)
                mine.Add(fromHistory);
            if (!mine.Any(g => g.Ts > a.Start)
                && bucket.Where(g => g.Ts > a.Start && g.Ts < end && Free(g))
                         .OrderBy(g => g.Ts).FirstOrDefault() is { } witness)
                mine.Add(witness);
            if (mine.Count == 0) continue;

            foreach (var g in mine) taken.Add((g.Queue, g.Index));
            var keep = mine.Where(g => g.Ts > a.Start).OrderBy(g => g.Ts).FirstOrDefault() ?? mine[0];
            var drop = mine.Where(g => g != keep).Select(g => g.Index).ToList();
            if (drop.Count > 0 || keep.Id != a.Id || keep.Start != a.Start)
                res.Add(new SameGame(a.Queue, keep.Index, a.Id, a.Start, drop));
        }
        return res;
    }

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
        foreach (var (ts, _) in hits.Where(kv => kv.Value >= 3))
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
            var refs = acc.Queues.SelectMany(kv => kv.Value.Games.Select((g, i) =>
                new GameRef(kv.Key, i, g.Ts, g.ChampionId, g.Win, g.Id, g.Start))).ToList();
            foreach (var byQueue in ForeignGames(refs, acc.SeenGames.ToHashSet(), mine).GroupBy(x => x.Queue))
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
    /// Свести копии одних и тех же игр в журнале аккаунта. Опоры — история
    /// клиента (около двадцати последних игр) и записи, где номер уже есть.
    /// Так чинятся и записи до 1.3.63, у которых номера нет: все копии,
    /// которые наплодила синхронизация, — из последних игр, а их история знает.
    /// Возвращает, сколько записей убрано.
    /// </summary>
    private static int FoldSameGames(Account acc, IEnumerable<HistEntry> history)
    {
        var refs = acc.Queues.SelectMany(kv => kv.Value.Games.Select((g, i) =>
            new GameRef(kv.Key, i, g.Ts, g.ChampionId, g.Win, g.Id, g.Start))).ToList();
        var anchors = history.Where(h => h.CreatedSec > 0)
            .Select(h => new GameAnchor(h.GameId, h.CreatedSec, h.Queue, h.ChampionId, h.Win));

        var removed = 0;
        foreach (var byQueue in SameGames(refs, anchors).GroupBy(s => s.Queue))
        {
            var games = acc.Queues[byQueue.Key].Games;
            var drop = new HashSet<int>();
            foreach (var s in byQueue)
            {
                var keep = games[s.Keep];
                keep.Id = s.Id;
                keep.Start = s.Start;
                foreach (var d in s.Drop)
                {
                    keep.Role ??= games[d].Role;
                    drop.Add(d);
                }
            }
            foreach (var d in drop.OrderByDescending(x => x)) games.RemoveAt(d);
            removed += drop.Count;
            // Копии дописывались в конец — вернуть порядок по времени: по нему
            // считаются «последние пять» и график. Порядок равных сохраняется.
            var sorted = games.OrderBy(g => g.Ts).ToList();
            games.Clear();
            games.AddRange(sorted);
        }
        return removed;
    }
}
