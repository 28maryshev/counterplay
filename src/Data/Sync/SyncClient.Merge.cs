using System.IO;
using System.Text.Json.Nodes;

namespace Counterplay;

// Синхронизация — сбор своих файлов, слияние со снятым с сервера, запись на диск
// и подсчёт того, что пришло с другого компьютера.
public static partial class SyncClient
{
    /// Снимок этого компьютера: содержимое файлов и когда их меняли.
    private static JsonObject Collect()
    {
        var o = new JsonObject();
        foreach (var name in Files)
        {
            var path = Path.Combine(Dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                o[name] = new JsonObject
                {
                    ["at"] = new FileInfo(path).LastWriteTimeUtc.ToUnixTimeSecondsSafe(),
                    ["body"] = File.ReadAllText(path)
                };
            }
            catch { /* занят или битый — пропускаем, остальное поедет */ }
        }
        return o;
    }

    /// <summary>
    /// Слияние. Настройки и пулы берём те, что новее: их правят целиком, и
    /// «победил свежий» здесь верно. Журнал сессий сливаем по-настоящему —
    /// см. <see cref="MergeSession"/>.
    /// </summary>
    private static JsonObject Merge(JsonObject mine, JsonObject theirs)
    {
        var res = new JsonObject();
        foreach (var name in Files)
        {
            var a = mine[name]?.AsObject();
            var b = theirs[name]?.AsObject();
            if (a is null && b is null) continue;
            if (a is null) { res[name] = b!.DeepClone(); continue; }
            if (b is null) { res[name] = a.DeepClone(); continue; }

            if (name == "session.json")
            {
                var merged = MergeSession((string)a["body"]!, (string)b["body"]!);
                res[name] = new JsonObject
                {
                    ["at"] = Math.Max((long)a["at"]!, (long)b["at"]!),
                    ["body"] = merged
                };
                continue;
            }

            // Пулы: пустое НИКОГДА не побеждает непустое, как бы свежо оно ни
            // было. Иначе выходит так: локальный файл обнулился (сбой чтения,
            // падение посреди записи), он же свежее — и пустота уезжает на
            // сервер, добивая последнюю копию. Владелец на это и указал:
            // «нельзя делать пустой», «если ничего нет и нажать
            // синхронизировать — должно выкачивать».
            if (name == "pools.json")
            {
                bool mineEmpty = PoolsEmpty(a), theirsEmpty = PoolsEmpty(b);
                if (mineEmpty != theirsEmpty)
                {
                    res[name] = (mineEmpty ? b : a).DeepClone();
                    continue;
                }
            }

            // Настройки с другого компьютера — но положение окон своё.
            if (name == "ui.json" && (long)b["at"]! > (long)a["at"]!)
            {
                res[name] = new JsonObject
                {
                    ["at"] = b["at"]!.DeepClone(),
                    ["body"] = KeepOwnWindows((string)a["body"]!, (string)b["body"]!)
                };
                continue;
            }

            res[name] = ((long)a["at"]! >= (long)b["at"]! ? a : b).DeepClone();
        }
        return res;
    }

    /// <summary>
    /// Пришедшие настройки, но места и размеры окон — с этого компьютера.
    ///
    /// Мониторы у компьютеров разные: окно драфта, поставленное на втором
    /// мониторе у 2560-пиксельного экрана, на ноутбуке оказалось бы за краем.
    /// Пока пришедшие настройки не подхватывались до перезапуска, это почти не
    /// проявлялось; теперь они применяются сразу, и без этого окна прыгали бы
    /// после каждой игры на другом компьютере.
    /// </summary>
    private static string KeepOwnWindows(string mine, string theirs)
    {
        try
        {
            var m = JsonNode.Parse(mine)?.AsObject();
            var t = JsonNode.Parse(theirs)?.AsObject();
            if (m is null || t is null) return theirs;
            foreach (var key in AppSettings.MachineKeys)
            {
                if (m[key] is { } v) t[key] = v.DeepClone();
                else t.Remove(key);
            }
            return t.ToJsonString(AppSettings.FileJson);
        }
        catch { return theirs; }
    }

    /// <summary>
    /// Нет ли в этой копии пулов вовсе.
    ///
    /// Файл с одними пустыми аккаунтами — это не «человек всё удалил», а почти
    /// всегда сбой: удаляют пулы по одному, а не все пять аккаунтов разом.
    /// Разобрать не вышло — считаем непустым: на догадке затирать нельзя.
    /// </summary>
    private static bool PoolsEmpty(JsonObject side)
    {
        try
        {
            var body = (string?)side["body"];
            if (string.IsNullOrWhiteSpace(body)) return true;
            var root = JsonNode.Parse(body)?.AsObject();
            if (root is null || root.Count == 0) return true;
            foreach (var acc in root)
            {
                var o = acc.Value?.AsObject();
                if (o is null) continue;
                if ((o["Pools"]?.AsArray()?.Count ?? 0) > 0) return false;
                if ((o["DuoPools"]?.AsArray()?.Count ?? 0) > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Журнал сессий: «победил свежий» здесь не годится.
    ///
    /// На двух компьютерах играют РАЗНЫЕ игры, и взять файл целиком означает
    /// потерять половину истории. Поэтому сливаем по составу: игры — по номеру,
    /// связки — объединением отметок времени. После этого связки чистятся по
    /// тому же правилу, что и в журнале (<see cref="ForgetPairs"/>), а копии одних
    /// и тех же игр сводятся (<see cref="FoldSameGames"/>), а чужие игры из
    /// журналов других аккаунтов убираются (<see cref="DropForeignGames"/>,
    /// <see cref="DropMarked"/>).
    ///
    /// Ничего не пришло и нечего чистить — возвращается свой текст байт в байт:
    /// проход без новостей не должен переписывать журнал и считаться «забравшим».
    /// </summary>
    private static string MergeSession(string mine, string theirs)
    {
        try
        {
            var a = JsonNode.Parse(mine)!.AsObject();
            var b = JsonNode.Parse(theirs)!.AsObject();
            var accА = a["Accounts"]?.AsObject();
            var accB = b["Accounts"]?.AsObject();
            if (accА is null || accB is null) return mine;

            foreach (var (puuid, their) in accB)
            {
                if (their is not JsonObject t) continue;
                if (accА[puuid] is not JsonObject m) { accА[puuid] = t.DeepClone(); continue; }

                MergeQueues(m, t);
                MergePairs(m, t);
                MergeMarks(m, t);
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var (_, acc) in accА)
                if (acc is JsonObject o)
                {
                    DropMarked(o);
                    FoldSameGames(o);
                    ForgetPairs(o, now);
                }
            DropForeignGames(accА);

            return JsonNode.DeepEquals(a, JsonNode.Parse(mine)) ? mine : a.ToJsonString();
        }
        catch
        {
            // Чужой журнал не разобрался — свой не трогаем. Потерять историю
            // хуже, чем не получить чужую.
            Log.Write("синхронизация: журнал не разобрался — оставляю свой");
            return mine;
        }
    }

    /// <summary>
    /// Игры по очередям: объединяем по времени начала, повторы отбрасываем.
    ///
    /// Пришедшие ставятся по времени, а не в конец: по порядку списка считаются
    /// «последние 5» и график рейтинга (он идёт от текущего ранга назад,
    /// вычитая очки игр по одной). Игры двух компьютеров, дописанные в конец,
    /// перемешали бы и то и другое.
    /// </summary>
    private static void MergeQueues(JsonObject mine, JsonObject theirs)
    {
        var qm = mine["Queues"]?.AsObject();
        var qt = theirs["Queues"]?.AsObject();
        if (qt is null) return;
        if (qm is null) { mine["Queues"] = qt.DeepClone(); return; }

        foreach (var (queue, node) in qt)
        {
            if (node is not JsonObject t) continue;
            if (qm[queue] is not JsonObject m) { qm[queue] = t.DeepClone(); continue; }

            var gm = m["Games"]?.AsArray();
            var gt = t["Games"]?.AsArray();
            if (gt is null) continue;
            if (gm is null) { m["Games"] = gt.DeepClone(); continue; }

            var byTs = new Dictionary<long, JsonObject>();
            foreach (var g in gm)
                if (g is JsonObject o && o["Ts"] is { } ts) byTs.TryAdd((long)ts, o);
            var added = false;
            foreach (var g in gt)
            {
                if (g?["Ts"] is not { } ts) continue;
                if (!byTs.TryGetValue((long)ts, out var own))
                {
                    var copy = g.DeepClone();
                    gm.Add(copy);
                    added = true;
                    if (copy is JsonObject c) byTs[(long)ts] = c;
                }
                // Та же игра: роль (её пишут с 1.3.61), номер и начало (с 1.3.63)
                // берём оттуда, где они есть.
                else
                    foreach (var field in new[] { "Role", "Id", "Start" })
                        if (own[field] is null && g[field] is { } v)
                            own[field] = v.DeepClone();
            }
            if (added) Reorder(gm, g => (long?)g?["Ts"] ?? 0);
        }
    }

    /// <summary>
    /// Связки: у каждой два списка отметок времени — складываем, чистим повторы,
    /// ставим по времени и держим последние <see cref="SessionTracker.PairKeep"/>,
    /// как сам журнал.
    /// </summary>
    private static void MergePairs(JsonObject mine, JsonObject theirs)
    {
        var pm = mine["Pairs"]?.AsObject();
        var pt = theirs["Pairs"]?.AsObject();
        if (pt is null) return;
        if (pm is null) { mine["Pairs"] = pt.DeepClone(); return; }

        foreach (var (key, node) in pt)
        {
            if (node is not JsonObject t) continue;
            if (pm[key] is not JsonObject m) { pm[key] = t.DeepClone(); continue; }
            foreach (var side in new[] { "Won", "Lost" })
            {
                var am = m[side]?.AsArray();
                var at = t[side]?.AsArray();
                if (at is null) continue;
                if (am is null) { m[side] = at.DeepClone(); continue; }

                var all = am.Concat(at).Where(x => x is not null).Select(x => (long)x!)
                            .Distinct().Order().TakeLast(SessionTracker.PairKeep).ToList();
                if (all.SequenceEqual(am.Select(x => (long)x!))) continue;
                am.Clear();
                foreach (var x in all) am.Add(x);
            }
        }
    }

    /// <summary>
    /// Свести копии одних и тех же игр — по тому же правилу, что журнал
    /// (<see cref="SessionTracker.SameGames"/>).
    ///
    /// Сведение по времени записи их не узнаёт: компьютер, где играли, пишет
    /// игру по её концу и с LP, новый компьютер берёт её из истории клиента со
    /// временем начала, и у владельца после синхронизации каждая игра лежала
    /// дважды. Опоры здесь — записи, где номер и начало уже есть; записи без
    /// них журнал сводит сам, по истории клиента.
    /// </summary>
    private static void FoldSameGames(JsonObject account)
    {
        if (account["Queues"] is not JsonObject queues) return;
        foreach (var byQueue in SessionTracker.SameGames(GameRefs(queues), []).GroupBy(s => s.Queue))
        {
            var games = queues[byQueue.Key]!["Games"]!.AsArray();
            var drop = new HashSet<int>();
            foreach (var s in byQueue)
            {
                var keep = games[s.Keep]!.AsObject();
                keep["Id"] = s.Id;
                keep["Start"] = s.Start;
                foreach (var d in s.Drop)
                {
                    if (keep["Role"] is null && games[d]?["Role"] is { } role)
                        keep["Role"] = role.DeepClone();
                    drop.Add(d);
                }
            }
            foreach (var d in drop.OrderByDescending(x => x)) games.RemoveAt(d);
            Reorder(games, g => (long?)g?["Ts"] ?? 0);
        }
    }

    /// <summary>
    /// Убрать из журнала каждого аккаунта игры других аккаунтов — по тому же
    /// правилу, что журнал (<see cref="SessionTracker.ForeignGames"/>). Опоры —
    /// записи других аккаунтов с номером и началом.
    ///
    /// Здесь нужно так же, как в журнале: иначе убранное на одном компьютере
    /// слияние вернуло бы с копии другого.
    /// </summary>
    private static void DropForeignGames(JsonObject accounts)
    {
        var refs = new Dictionary<string, List<SessionTracker.GameRef>>();
        foreach (var (puuid, node) in accounts)
            refs[puuid] = node?["Queues"] is JsonObject q ? GameRefs(q) : [];

        foreach (var (puuid, node) in accounts)
        {
            if (node is not JsonObject acc || acc["Queues"] is not JsonObject queues) continue;
            var seen = acc["SeenGames"]?.AsArray().Where(x => x is not null).Select(x => (long)x!).ToHashSet() ?? [];
            if (seen.Count == 0) continue;
            var others = refs.Where(kv => kv.Key != puuid).SelectMany(kv => kv.Value)
                .Where(g => g.Id is not null && g.Start is not null)
                .Select(g => new SessionTracker.GameAnchor(g.Id!.Value, g.Start!.Value, g.Queue, g.ChampionId, g.Win));
            foreach (var byQueue in SessionTracker.ForeignGames(refs[puuid], seen, others).GroupBy(x => x.Queue))
            {
                var games = queues[byQueue.Key]!["Games"]!.AsArray();
                foreach (var i in byQueue.Select(x => x.Index).OrderByDescending(x => x))
                    games.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Отпечатки доказанно чужих записей (<c>Foreign</c>, см.
    /// <c>SessionTracker.ProveForeignAsync</c>): объединяем. Доказал один
    /// компьютер — убирают оба: у второго их аккаунта может и не быть в клиенте.
    /// </summary>
    private static void MergeMarks(JsonObject mine, JsonObject theirs)
    {
        if (theirs["Foreign"] is not JsonArray ft) return;
        if (mine["Foreign"] is not JsonArray fm) { mine["Foreign"] = ft.DeepClone(); return; }
        var have = fm.Select(Mark).ToHashSet();
        foreach (var x in ft)
            if (have.Add(Mark(x))) fm.Add(x!.DeepClone());
    }

    /// <summary>
    /// Убрать записи по отпечаткам чужих. Журнал убрал их у себя, а копия
    /// другого компьютера хранит, и сведение по времени записи вернуло бы их
    /// каждым проходом: правилу пачки (<see cref="DropForeignGames"/>) здесь
    /// опереться не на что — номеров чужих игр в записях нет, клиента нет.
    /// </summary>
    private static void DropMarked(JsonObject account)
    {
        if (account["Foreign"] is not JsonArray marks || account["Queues"] is not JsonObject queues) return;
        var gone = marks.Select(Mark).ToHashSet();
        foreach (var (queue, node) in queues)
        {
            if (node?["Games"] is not JsonArray games) continue;
            for (var i = games.Count - 1; i >= 0; i--)
                if (games[i] is JsonObject g && gone.Contains(Mark(queue, g)))
                    games.RemoveAt(i);
        }
    }

    private static (string?, long?, int?, bool?) Mark(JsonNode? m) =>
        ((string?)m?["Queue"], (long?)m?["Ts"], (int?)m?["ChampionId"], (bool?)m?["Win"]);

    private static (string?, long?, int?, bool?) Mark(string queue, JsonObject g) =>
        (queue, (long?)g["Ts"], (int?)g["ChampionId"], (bool?)g["Win"]);

    /// Записи журнала аккаунта глазами правил <see cref="SessionTracker"/>.
    private static List<SessionTracker.GameRef> GameRefs(JsonObject queues)
    {
        var refs = new List<SessionTracker.GameRef>();
        foreach (var (queue, node) in queues)
        {
            if (node?["Games"] is not JsonArray games) continue;
            for (var i = 0; i < games.Count; i++)
                if (games[i] is JsonObject g && g["Ts"] is { } ts && g["ChampionId"] is { } champ
                    && g["Win"] is { } win)
                    refs.Add(new(queue, i, (long)ts, (int)champ, (bool)win, (long?)g["Id"], (long?)g["Start"]));
        }
        return refs;
    }

    /// <summary>
    /// Забыть связки, которые забыл бы и журнал (<see cref="SessionTracker.PairsToForget"/>).
    ///
    /// Без этого слияние отменяло чистку: журнал после каждой игры оставляет
    /// 400 связок, а слияние складывало их с копией на сервере, где лежали все
    /// прежние, и возвращало 461. Копия на сервере при этом только росла.
    /// </summary>
    private static void ForgetPairs(JsonObject account, long now)
    {
        if (account["Pairs"] is not JsonObject pairs) return;
        var drop = SessionTracker.PairsToForget(pairs.Select(kv =>
        {
            var times = Times(kv.Value, "Won").Concat(Times(kv.Value, "Lost")).ToList();
            return (kv.Key, times.Count, times.Count > 0 ? times.Max() : 0L);
        }), now);
        foreach (var key in drop) pairs.Remove(key);
    }

    private static IEnumerable<long> Times(JsonNode? pair, string side) =>
        pair?[side]?.AsArray().Where(x => x is not null).Select(x => (long)x!) ?? [];

    /// Переставить элементы массива по ключу (порядок равных сохраняется).
    private static void Reorder(JsonArray arr, Func<JsonNode?, long> key)
    {
        var items = arr.ToList();
        arr.Clear();
        foreach (var x in items.OrderBy(key)) arr.Add(x);
    }

    /// Записать слитое на диск. Возвращает имена изменившихся файлов.
    /// <param name="only">Проход под чужим для пароля аккаунтом
    /// (<see cref="OwnerOf"/>): в <paramref name="data"/> только его журнал, и
    /// в своём журнале заменяется только его запись.</param>
    private static List<string> Apply(JsonObject data, string? only = null)
    {
        var changed = new List<string>();
        foreach (var name in Files)
        {
            var body = (string?)data[name]?["body"];
            if (body is null) continue;
            var path = Path.Combine(Dir, name);
            try
            {
                if (only is not null)
                {
                    if (name != "session.json") continue;
                    body = WithJournal(File.Exists(path) ? File.ReadAllText(path) : null, body, only);
                    if (body is null) continue;
                }
                if (File.Exists(path) && File.ReadAllText(path) == body) continue;
                // Копия рядом: если слияние ошиблось, прежнее не потеряно.
                if (File.Exists(path)) File.Copy(path, path + ".before-sync", overwrite: true);
                File.WriteAllText(path, body);
                changed.Add(name);
            }
            catch (Exception e) { Log.Write($"синхронизация: {name} не записался ({e.Message})"); }
        }
        return changed;
    }

    // ── что пришло с другого компьютера ────────────────────────────────────

    /// <summary>
    /// Сравнить своё до слияния и после: сколько игр прибавилось, сменились ли
    /// пулы и настройки.
    ///
    /// Игры считаются разницей числа, а не по ходу слияния: слияние только
    /// добавляет, и разница — ровно пришедшее. Пулы и настройки сравниваются по
    /// содержимому, а не по тексту: тот же файл, записанный другим способом, —
    /// не новость.
    /// </summary>
    private static Incoming Came(JsonObject mine, JsonObject merged) =>
        new(Math.Max(0, CountGames(merged) - CountGames(mine)),
            Differs(mine, merged, "pools.json"),
            Differs(mine, merged, "ui.json"));

    private static bool Differs(JsonObject mine, JsonObject merged, string name)
    {
        var a = (string?)mine[name]?["body"];
        var b = (string?)merged[name]?["body"];
        if (b is null || a == b) return false;
        if (a is null) return true;
        try { return !JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b)); }
        catch { return true; }
    }

    private static int CountGames(JsonObject side)
    {
        try
        {
            var body = (string?)side["session.json"]?["body"];
            if (body is null) return 0;
            var n = 0;
            foreach (var (_, acc) in JsonNode.Parse(body)?["Accounts"]?.AsObject() ?? [])
                foreach (var (_, q) in acc?["Queues"]?.AsObject() ?? [])
                    n += q?["Games"]?.AsArray().Count ?? 0;
            return n;
        }
        catch { return 0; }
    }
}

internal static class TimeExt
{
    public static long ToUnixTimeSecondsSafe(this DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
