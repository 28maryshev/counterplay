using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — ранг и LP: абсолютные очки, прогресс в дивизионе, вид для экрана.
public static partial class SessionTracker
{
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
}
