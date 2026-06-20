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

    public static string TierToBucket(string tier) => tier switch
    {
        "IRON" or "BRONZE" or "SILVER"                    => "silver",
        "GOLD" or "PLATINUM"                               => "gold",
        "EMERALD" or "DIAMOND"                             => "emerald",
        "MASTER" or "GRANDMASTER" or "CHALLENGER"          => "master",
        _                                                   => "emerald",
    };
}
