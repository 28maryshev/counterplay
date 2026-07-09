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

    /// Статистика одной очереди для панели трекера.
    public sealed record QueueView(
        bool HasRank, string Tier, string Division, int Lp, int ProgressPct,
        int Wins, int Losses, double Winrate,
        IReadOnlyList<RecentGame> Last5,
        IReadOnlyList<WrPoint> WinrateHistory);

    public sealed record SessionData(string Nick, IReadOnlyDictionary<string, QueueView> Queues);

    /// Ключи очередей в порядке выпадающего списка.
    public static readonly string[] QueueKeys = ["solo", "flex", "normal", "aram"];

    // queueId LCU → наш ключ очереди (400 драфт / 430 блайнд / 490 квикплей = normal).
    private static string? QueueOf(int queueId) => queueId switch
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

    private sealed class Store
    {
        public string SelectedQueue { get; set; } = "solo";
        public string? Nick { get; set; }
        public long LastGameId { get; set; }
        public Dictionary<string, QueueLog> Queues { get; set; } = new();
        // Последний успешный снимок рангов: LCU после рестарта/обновления может
        // не ответить — панель всё равно рендерится из кэша, а не пустой.
        public Dictionary<string, RankedCache> Ranked { get; set; } = new();

        // Наследие одноочередного формата — мигрирует в Queues["solo"] при загрузке.
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

    private static Store Load()
    {
        // Основной файл, затем резервная копия — журнал не теряется из-за
        // одного битого чтения.
        foreach (var path in new[] { StorePath, StorePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? new Store();
                Migrate(s);
                return s;
            }
            catch { /* пробуем следующий */ }
        }
        return new Store();
    }

    // Старый формат (один журнал без очередей) переезжает в solo.
    private static void Migrate(Store s)
    {
        if (s.Games is not { Count: > 0 }) { s.Games = null; return; }
        var q = GetQueue(s, "solo");
        if (q.Games.Count == 0)
        {
            q.Games.AddRange(s.Games.Select(g => new GameLog
            { Ts = g.Ts, ChampionId = g.ChampionId, Win = g.Win, Lp = g.LpDelta, Wr = g.Winrate }));
            q.LastAbsLp = s.LastAbsLp;
        }
        s.Games = null;
    }

    private static QueueLog GetQueue(Store s, string key)
    {
        if (!s.Queues.TryGetValue(key, out var q)) s.Queues[key] = q = new QueueLog();
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

    public static string GetSelectedQueue()
    {
        Gate.Wait();
        try { var q = Load().SelectedQueue; return QueueKeys.Contains(q) ? q : "solo"; }
        finally { Gate.Release(); }
    }

    public static void SetSelectedQueue(string queue)
    {
        if (!QueueKeys.Contains(queue)) return;
        Gate.Wait();
        try { var s = Load(); s.SelectedQueue = queue; Save(s); }
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

    private static string? _nick; // кэш на процесс

    public static async Task<SessionData?> RefreshAsync(LcuHttpClient http, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try { return await RefreshCoreAsync(http, ct); }
        finally { Gate.Release(); }
    }

    private static async Task<SessionData?> RefreshCoreAsync(LcuHttpClient http, CancellationToken ct)
    {
        var store = Load();

        // 1) Ник (Riot ID): из LCU, иначе из кэша журнала.
        _nick ??= await FetchNickAsync(http, ct) ?? store.Nick;
        if (!string.IsNullOrEmpty(_nick)) store.Nick = _nick;

        // 2) Ранги обеих ранкед-очередей. Если LCU не ответил (рестарт после
        // обновления и т.п.) — берём последний снимок из журнала: панель никогда
        // не пустеет, пока на диске есть данные.
        var fetched = await FetchRankedAsync(http, ct);
        var ranked = fetched ?? new Dictionary<string, Ranked>
        {
            ["solo"] = FromCache(store.Ranked.GetValueOrDefault("solo")),
            ["flex"] = FromCache(store.Ranked.GetValueOrDefault("flex")),
        };
        if (fetched != null)
            foreach (var (k, r) in fetched)
                store.Ranked[k] = new RankedCache
                { HasRank = r.HasRank, Tier = r.Tier, Div = r.Div, Lp = r.Lp, Wins = r.Wins, Losses = r.Losses };

        // 3) Последние игры из истории (все очереди).
        var history = await FetchHistoryAsync(http, ct);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 4) Новые игры (по gameId) — раскладываем по журналам очередей.
        //    Первый запуск: журнал не наполняем задним числом, только ставим отметку.
        var appended = new Dictionary<string, GameLog>(); // очередь → последняя добавленная игра
        if (store.LastGameId == 0)
        {
            store.LastGameId = history.Count > 0 ? history.Max(h => h.GameId) : 0;
        }
        else
        {
            foreach (var h in history.Where(h => h.GameId > store.LastGameId).OrderBy(h => h.GameId))
            {
                var q = GetQueue(store, h.Queue);
                var log = new GameLog { Ts = now, ChampionId = h.ChampionId, Win = h.Win };
                q.Games.Add(log);
                // Журнал держим на весь сезон: 3000 игр на очередь хватает даже
                // самым активным; страховка от бесконечного роста файла.
                if (q.Games.Count > 3000) q.Games.RemoveRange(0, q.Games.Count - 3000);
                appended[h.Queue] = log;
                store.LastGameId = Math.Max(store.LastGameId, h.GameId);
            }
        }

        // 4b) Пустые журналы добиваем ПРОШЛЫМИ играми из истории лаунчера:
        //     первый запуск или очередь, в которую раньше не играли с программой.
        //     LP прошлых игр LCU не отдаёт — дельты начнутся с новых игр.
        foreach (var key in QueueKeys)
        {
            var q = GetQueue(store, key);
            if (q.Games.Count > 0) continue;
            var past = history.Where(h => h.Queue == key && h.GameId <= store.LastGameId)
                              .OrderBy(h => h.GameId).ToList();
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
            var q = GetQueue(store, key);
            var abs = AbsLp(r.Tier, r.Div, r.Lp);
            if (q.LastAbsLp == int.MinValue) { q.LastAbsLp = abs; continue; }

            var delta = abs - q.LastAbsLp;
            if (appended.TryGetValue(key, out var game))
            {
                game.Lp = delta;
                q.LastAbsLp = abs;
            }
            else if (delta != 0)
            {
                // Обратный порядок: игра уже догнана историей (без дельты или с
                // нулевой), а LP доехал только сейчас — вешаем дельту на неё.
                var lastGame = q.Games.Count > 0 ? q.Games[^1] : null;
                if (lastGame is not null && lastGame.Lp is null or 0 && now - lastGame.Ts < 900)
                {
                    lastGame.Lp = delta;
                    q.LastAbsLp = abs;
                }
                // Иначе ждём: дельту получит игра, когда появится в истории.
            }
        }

        // 6) Винрейт на момент игры: ранкед — сезонный, нормал/ARAM — по журналу.
        foreach (var (key, game) in appended)
        {
            var q = GetQueue(store, key);
            if (key is "solo" or "flex")
            {
                var r = ranked[key];
                var g = r.Wins + r.Losses;
                game.Wr = g > 0 ? 100.0 * r.Wins / g : 0;
            }
            else
            {
                game.Wr = 100.0 * q.Games.Count(x => x.Win) / q.Games.Count;
            }
        }

        Save(store);

        // 7) Представления по очередям.
        var views = new Dictionary<string, QueueView>();
        foreach (var key in QueueKeys)
        {
            var q = GetQueue(store, key);
            var last5 = q.Games.AsEnumerable().Reverse().Take(5)
                .Select(g => new RecentGame(g.ChampionId, g.Win, g.Lp)).ToList();
            if (last5.Count == 0) // свежая установка — показать хотя бы иконки из истории
                last5 = history.Where(h => h.Queue == key).Take(5)
                    .Select(h => new RecentGame(h.ChampionId, h.Win, null)).ToList();
            var points = q.Games
                .Select(g => new WrPoint(DateTimeOffset.FromUnixTimeSeconds(g.Ts).LocalDateTime, g.Wr))
                .ToList();

            if (key is "solo" or "flex")
            {
                var r = ranked[key];
                var g = r.Wins + r.Losses;
                views[key] = new QueueView(
                    r.HasRank, Cap(r.Tier), r.Div, r.Lp,
                    r.HasRank ? ProgressPct(r.Tier, r.Lp) : 0,
                    r.Wins, r.Losses, g > 0 ? 100.0 * r.Wins / g : 0,
                    last5, points);
            }
            else
            {
                int w = q.Games.Count(x => x.Win), l = q.Games.Count - w;
                views[key] = new QueueView(
                    false, "", "", 0, 0, w, l,
                    q.Games.Count > 0 ? 100.0 * w / q.Games.Count : 0,
                    last5, points);
            }
        }

        return new SessionData(_nick ?? "", views);
    }

    private static Ranked FromCache(RankedCache? c) =>
        c is null ? new Ranked(false, "", "", 0, 0, 0)
                  : new Ranked(c.HasRank, c.Tier, c.Div, c.Lp, c.Wins, c.Losses);

    // Ник игрока (Riot ID gameName, иначе displayName).
    private static async Task<string?> FetchNickAsync(LcuHttpClient http, CancellationToken ct)
    {
        try
        {
            var (s, body) = await http.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (s != 200) return null;
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var game = root.TryGetProperty("gameName", out var gn) ? gn.GetString() : null;
            if (!string.IsNullOrEmpty(game)) return game;
            return root.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
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
