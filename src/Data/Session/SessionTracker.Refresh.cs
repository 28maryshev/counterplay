using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — обновление из клиента: призыватель, ранги, союзники, история матчей.
public static partial class SessionTracker
{
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

        // 3) Последние игры из истории (все очереди) — только этого аккаунта.
        var history = await FetchHistoryAsync(http, accountKey, ct);

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

        // 3c) Копии одних и тех же игр — свести, пока история под рукой (см.
        //     SameGames): синхронизация до 1.3.63 удваивала последние игры.
        var folded = FoldSameGames(acc, history);
        if (folded > 0) Log.Write($"журнал игр: убрано копий одних и тех же игр — {folded}");
        var foreign = DropForeignGames(store, accountKey, history);
        if (foreign > 0) Log.Write($"журнал игр: убрано чужих игр из журналов других аккаунтов — {foreign}");

        // 4) Новые игры — те, чьих номеров мы ещё не видели. Порядок берём по
        //    времени начала: номера игр по времени не упорядочены (см. SeenGames).
        //    Первый запуск: журнал не наполняем задним числом, только запоминаем,
        //    что всё это уже было.
        var appended = new Dictionary<string, GameLog>(); // очередь → последняя добавленная игра
        var added    = new Dictionary<long, GameLog>();   // номер игры → её новая запись (для роли)
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
            // Эту игру уже записал другой компьютер, и она приехала синхронизацией.
            // Своя запись была бы копией — без LP и с нашим «сейчас».
            if (q.Games.Any(g => g.Id == h.GameId)) { seen.Add(h.GameId); continue; }
            var log = new GameLog { Ts = now, ChampionId = h.ChampionId, Win = h.Win,
                                    Role = DraftRoleOf(h), Id = h.GameId,
                                    Start = h.CreatedSec > 0 ? h.CreatedSec : null };
            q.Games.Add(log);
            added[h.GameId] = log;
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
                // Роль из драфта главнее: там она выдана, а здесь угадана по
                // доле ролей (клиент путает топ с лесом).
                if (added.TryGetValue(h.GameId, out var fresh) && string.IsNullOrEmpty(fresh.Role)
                    && myRole.Length > 0)
                    fresh.Role = myRole;
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
                    Id = past[i].GameId,
                    Start = past[i].CreatedSec > 0 ? past[i].CreatedSec : null,
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
    /// Последние игры из истории LCU: gameId, очередь, чемпион, победа — только
    /// игры <paramref name="owner"/>.
    ///
    /// Владельца сверяем по каждой игре: при смене аккаунта клиент отдаёт
    /// историю нового, когда «кто в клиенте» ещё отвечает прежним (или не
    /// отвечает, и журнал берёт последний аккаунт). Так 14 октября восемнадцать
    /// игр владельца легли в журнал другого аккаунта, все одним временем и без LP.
    /// </summary>
    private static async Task<List<HistEntry>> FetchHistoryAsync(
        LcuHttpClient http, string owner, CancellationToken ct)
    {
        var result = new List<HistEntry>();
        var alien = 0;
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
                if (g.TryGetProperty("participantIdentities", out var ids) && ids.ValueKind == JsonValueKind.Array
                    && ids.GetArrayLength() > 0 && ids[0].TryGetProperty("player", out var pl)
                    && pl.TryGetProperty("puuid", out var pu) && pu.GetString() is { Length: > 0 } puuid
                    && puuid != owner)
                {
                    alien++;
                    continue;
                }
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
            if (alien > 0) Log.Write($"история клиента не этого аккаунта — пропущено игр: {alien}");
        }
        catch { /* история недоступна */ }
        return result;
    }
}
