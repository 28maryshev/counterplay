using System.IO;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Трекер сессии для экрана ожидания: ранг/LP/прогресс из LCU, последние игры,
/// винрейт и его динамика. LP за игру и историю винрейта LCU напрямую не отдаёт —
/// поэтому трекер сам фиксирует снимки и хранит журнал игр между запусками
/// (%APPDATA%\Counterplay\session.json). Ключ Riot не нужен — только LCU.
/// </summary>
public static class SessionTracker
{
    public sealed record RecentGame(int ChampionId, bool Win, int? LpDelta);
    public sealed record WrPoint(DateTime Date, double Winrate);

    public sealed record SessionData(
        bool HasRank,
        string Tier, string Division, int Lp,
        int ProgressPct,              // прогресс LP до след. ранга, 0..100
        int Wins, int Losses,         // за сезон (очередь Solo/Duo)
        double Winrate,               // %
        IReadOnlyList<RecentGame> Last5,
        int SessionWins, int SessionLosses, int SessionNetLp,
        IReadOnlyList<WrPoint> WinrateHistory);

    // ── Журнал игр (персистентный) ─────────────────────────────────────────
    private sealed class GameLog
    {
        public long Ts { get; set; }            // unix seconds
        public int ChampionId { get; set; }
        public bool Win { get; set; }
        public int LpDelta { get; set; }
        public double Winrate { get; set; }     // сезонный WR на момент игры
    }

    private sealed class Store
    {
        public long SessionStart { get; set; }
        public int LastAbsLp { get; set; } = int.MinValue;
        public int LastGames { get; set; } = -1;   // wins+losses прошлого снимка
        public int LastWins { get; set; } = -1;    // wins прошлого снимка (для W/L текущей игры)
        public List<GameLog> Games { get; set; } = [];
    }

    private static string StorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Counterplay", "session.json");

    // Один Refresh за раз: в конце матча LCU шлёт несколько gameflow-событий
    // подряд, и параллельные вызовы гонялись на файле журнала — чтение ловило
    // полузаписанный JSON, парс падал и журнал затирался пустым Store.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static Store Load()
    {
        // Основной файл, затем резервная копия — журнал не теряется из-за
        // одного битого чтения.
        foreach (var path in new[] { StorePath, StorePath + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? new Store();
            }
            catch { /* пробуем следующий */ }
        }
        return new Store();
    }

    private static void Save(Store s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Атомарная запись: во временный файл + подмена. Прошлую версию
            // сохраняем как .bak — страховка от битых чтений и падений.
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            if (File.Exists(StorePath))
                File.Copy(StorePath, StorePath + ".bak", overwrite: true);
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch { /* не критично */ }
    }

    // Абсолютное значение ранга в «LP от Iron IV» — чтобы считать дельту через промо/демоушены.
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
        // Мастер+ без делений — LP идёт поверх базы напрямую.
        return t >= 2800 ? t + lp : t + d * 100 + lp;
    }

    private static readonly long SessionGapSeconds = 3 * 3600; // >3ч без игр — новая сессия

    /// Обновляет журнал из LCU и возвращает данные для экрана ожидания.
    public static async Task<SessionData?> RefreshAsync(LcuHttpClient http, CancellationToken ct)
    {
        // Сериализуем вызовы: конец матча порождает шквал gameflow-событий.
        await Gate.WaitAsync(ct);
        try { return await RefreshCoreAsync(http, ct); }
        finally { Gate.Release(); }
    }

    private static async Task<SessionData?> RefreshCoreAsync(LcuHttpClient http, CancellationToken ct)
    {
        // 1) Ранг из ranked-stats
        var (rs, rbody) = await http.GetAsync("/lol-ranked/v1/current-ranked-stats", ct);
        if (rs != 200) return null;

        string tier = "", div = "";
        int lp = 0, wins = 0, losses = 0;
        try
        {
            using var doc = JsonDocument.Parse(rbody);
            if (doc.RootElement.TryGetProperty("queueMap", out var qm) &&
                qm.TryGetProperty("RANKED_SOLO_5x5", out var solo))
            {
                tier   = solo.TryGetProperty("tier", out var t) ? t.GetString() ?? "" : "";
                div    = solo.TryGetProperty("division", out var d) ? d.GetString() ?? "" : "";
                lp     = solo.TryGetProperty("leaguePoints", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                wins   = solo.TryGetProperty("wins", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                losses = solo.TryGetProperty("losses", out var ls) && ls.ValueKind == JsonValueKind.Number ? ls.GetInt32() : 0;
            }
        }
        catch { return null; }

        bool hasRank = !string.IsNullOrEmpty(tier);
        int games = wins + losses;
        double winrate = games > 0 ? 100.0 * wins / games : 0;
        int absLp = hasRank ? AbsLp(tier, div, lp) : 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var store = Load();

        // 2) Новая игра? (счётчик игр вырос) — фиксируем LP-дельту и чемпиона из истории.
        if (hasRank && store.LastGames >= 0 && games > store.LastGames && store.LastAbsLp != int.MinValue)
        {
            int championId = await LastMatchChampionAsync(http, ct);
            // Победа — по росту счётчика побед (надёжно); LP-дельта — по абсолютному рангу.
            bool win = store.LastWins >= 0 ? wins > store.LastWins : absLp >= store.LastAbsLp;
            int lpDelta = absLp - store.LastAbsLp;
            store.Games.Add(new GameLog { Ts = now, ChampionId = championId, Win = win, LpDelta = lpDelta, Winrate = winrate });
            if (store.Games.Count > 400) store.Games.RemoveRange(0, store.Games.Count - 400);
        }

        // старт сессии на первом запуске
        if (store.SessionStart == 0) store.SessionStart = now;
        // разрыв сессии: если давно не играл — начать новую от текущего момента
        if (LastGameTs(store) != 0 && now - LastGameTs(store) > SessionGapSeconds)
            store.SessionStart = now;

        store.LastAbsLp = absLp;
        store.LastGames = games;
        store.LastWins = wins;
        Save(store);

        // 3) Последние 5 игр: из журнала (с LP-дельтой), иначе из истории матчей (без LP).
        List<RecentGame> last5;
        if (store.Games.Count > 0)
        {
            last5 = store.Games.AsEnumerable().Reverse().Take(5)
                .Select(g => new RecentGame(g.ChampionId, g.Win, g.LpDelta)).ToList();
        }
        else
        {
            last5 = await RecentFromHistoryAsync(http, ct);
        }

        // 4) Сессия: игры и net LP с момента начала сессии
        var sessionGames = store.Games.Where(g => g.Ts >= store.SessionStart).ToList();
        int sWins = sessionGames.Count(g => g.Win);
        int sLosses = sessionGames.Count - sWins;
        int sNet = sessionGames.Sum(g => g.LpDelta);

        // 5) Динамика винрейта по датам (сезонный WR на момент каждой игры)
        var history = store.Games
            .Select(g => new WrPoint(DateTimeOffset.FromUnixTimeSeconds(g.Ts).LocalDateTime, g.Winrate))
            .ToList();
        if (history.Count == 0 && games > 0)
            history.Add(new WrPoint(DateTime.Now, winrate));

        int progress = ProgressPct(tier, lp);

        return new SessionData(hasRank, Cap(tier), div, lp, progress, wins, losses, winrate,
            last5, sWins, sLosses, sNet, history);
    }

    private static long LastGameTs(Store s) => s.Games.Count > 0 ? s.Games[^1].Ts : 0;

    // Прогресс к след. рангу: обычные дивизии — LP/100; мастер+ — условно по 200 LP.
    private static int ProgressPct(string tier, int lp)
    {
        var t = tier.ToUpperInvariant();
        if (t is "MASTER" or "GRANDMASTER" or "CHALLENGER")
            return Math.Clamp((int)(lp / 200.0 * 100), 0, 100);
        return Math.Clamp(lp, 0, 100);
    }

    private static string Cap(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..].ToLowerInvariant();

    // championId последнего матча из истории.
    private static async Task<int> LastMatchChampionAsync(LcuHttpClient http, CancellationToken ct)
    {
        var games = await RecentFromHistoryAsync(http, ct);
        return games.Count > 0 ? games[0].ChampionId : 0;
    }

    // Последние игры из истории матчей LCU (championId + win), без LP-дельты.
    private static async Task<List<RecentGame>> RecentFromHistoryAsync(LcuHttpClient http, CancellationToken ct)
    {
        var result = new List<RecentGame>();
        try
        {
            var (s, body) = await http.GetAsync(
                "/lol-match-history/v1/products/lol/current-summoner/matches?begIndex=0&endIndex=5", ct);
            if (s != 200) return result;
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("games", out var wrap)) return result;
            if (!wrap.TryGetProperty("games", out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
            foreach (var g in arr.EnumerateArray())
            {
                if (!g.TryGetProperty("participants", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
                var p = parts[0];
                int champ = p.TryGetProperty("championId", out var cid) && cid.ValueKind == JsonValueKind.Number ? cid.GetInt32() : 0;
                bool win = p.TryGetProperty("stats", out var st) && st.TryGetProperty("win", out var w) && w.ValueKind == JsonValueKind.True;
                result.Add(new RecentGame(champ, win, null));
                if (result.Count >= 5) break;
            }
        }
        catch { /* история недоступна */ }
        return result;
    }
}
