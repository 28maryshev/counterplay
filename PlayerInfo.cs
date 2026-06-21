using System.Text.Json;

namespace Counterplay;

public static class PlayerInfo
{
    // Читает ранг текущего игрока из LCU и возвращает бакет для data.db.
    public static async Task<string> GetTierBucketAsync(LcuHttpClient http, CancellationToken ct)
    {
        var (status, body) = await http.GetAsync("/lol-ranked/v1/current-ranked-stats", ct);
        if (status != 200) return "emerald";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("queueMap", out var queueMap)) return "emerald";
            if (!queueMap.TryGetProperty("RANKED_SOLO_5x5", out var solo)) return "emerald";
            if (!solo.TryGetProperty("tier", out var tierEl)) return "emerald";

            var tier = tierEl.GetString()?.ToUpperInvariant() ?? "";
            return TierToBucket(tier);
        }
        catch
        {
            return "emerald";
        }
    }

    /// Очки мастерства текущего игрока по чемпионам (championId → points).
    /// Берётся из LCU, ключ Riot не нужен. Пусто, если недоступно.
    public static async Task<Dictionary<int, long>> GetMasteryAsync(LcuHttpClient http, CancellationToken ct)
    {
        var result = new Dictionary<int, long>();
        try
        {
            var (status, body) = await http.GetAsync(
                "/lol-champion-mastery/v1/local-player/champion-mastery", ct);
            if (status != 200) return result;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var m in doc.RootElement.EnumerateArray())
            {
                if (m.TryGetProperty("championId", out var idEl) &&
                    m.TryGetProperty("championPoints", out var ptsEl) &&
                    idEl.ValueKind == JsonValueKind.Number)
                {
                    var id  = idEl.GetInt32();
                    var pts = ptsEl.ValueKind == JsonValueKind.Number ? ptsEl.GetInt64() : 0;
                    if (id > 0 && pts > 0) result[id] = pts;
                }
            }
        }
        catch { /* недоступно — пустой пул */ }
        return result;
    }

    public static string TierToBucket(string tier) => tier switch
    {
        "IRON" or "BRONZE" or "SILVER"                    => "silver",
        "GOLD" or "PLATINUM"                               => "gold",
        "EMERALD" or "DIAMOND"                             => "emerald",
        "MASTER" or "GRANDMASTER" or "CHALLENGER"          => "master",
        _                                                   => "emerald",
    };
}
