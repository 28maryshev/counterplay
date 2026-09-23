using System.Text.Json;

namespace Counterplay;

public static class PlayerInfo
{
    // Читает ранг текущего игрока из LCU и возвращает бакет для data.db.
    /// <summary>
    /// Бакет данных по соло-рангу. **null — прочитать не вышло**, и это НЕ то же
    /// самое, что «нет ранга».
    ///
    /// Раньше на любой осечке возвращался «изумруд». Клиент после запуска отдаёт
    /// ранговую статистику не сразу, и мы принимали молчание за изумруд: качали
    /// изумрудную базу (112 МБ), а через минуту, когда клиент отвечал по-честному,
    /// — свою (81 МБ). Двести мегабайт вместо восьмидесяти и двойное ожидание.
    ///
    /// Настоящее отсутствие ранга (новый аккаунт, сброс сезона) — это УДАЧНОЕ
    /// чтение с пустым тиром; на него по-прежнему отвечаем «изумруд» как
    /// серединой шкалы.
    /// </summary>
    public static async Task<string?> GetTierBucketAsync(LcuHttpClient http, CancellationToken ct)
    {
        var (status, body) = await http.GetAsync("/lol-ranked/v1/current-ranked-stats", ct);
        if (status != 200) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("queueMap", out var queueMap)) return null;
            if (!queueMap.TryGetProperty("RANKED_SOLO_5x5", out var solo)) return null;
            if (!solo.TryGetProperty("tier", out var tierEl)) return null;

            var tier = tierEl.GetString()?.ToUpperInvariant() ?? "";
            return TierToBucket(tier);
        }
        catch
        {
            return null;
        }
    }

    /// Чемпионы, которыми ВЛАДЕЕТ текущий аккаунт (championId). Ключ Riot не нужен —
    /// берём из LCU. Пусто, если недоступно (тогда предупреждение «нет чемпиона»
    /// не показываем — не пугаем ложно).
    public static async Task<HashSet<int>> GetOwnedChampionsAsync(LcuHttpClient http, CancellationToken ct)
    {
        var owned = new HashSet<int>();
        try
        {
            var (status, body) = await http.GetAsync("/lol-champions/v1/owned-champions-minimal", ct);
            if (status != 200) return owned;

            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return owned;

            foreach (var c in doc.RootElement.EnumerateArray())
            {
                if (!c.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                    continue;
                var id = idEl.GetInt32();
                if (id <= 0) continue;
                // Если есть флаг владения — уважаем его (эндпоинт может отдавать и
                // free-to-play); иначе считаем владением.
                bool ownedFlag = true;
                if (c.TryGetProperty("ownership", out var own)
                    && own.TryGetProperty("owned", out var o)
                    && o.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ownedFlag = o.GetBoolean();
                if (ownedFlag) owned.Add(id);
            }
        }
        catch { /* недоступно — пустой набор */ }
        return owned;
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

    /// Аккаунт клиента: (puuid, ник). puuid стабилен и переживает переименование —
    /// им ключуем пулы чемпионов игрока. Пусто, если недоступно.
    public static async Task<(string? Puuid, string? Name)> GetAccountAsync(LcuHttpClient http, CancellationToken ct)
    {
        try
        {
            var (s, body) = await http.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (s != 200) return (null, null);
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            string? puuid = r.TryGetProperty("puuid", out var p) ? p.GetString() : null;
            string? name = r.TryGetProperty("gameName", out var gn) && !string.IsNullOrEmpty(gn.GetString())
                ? gn.GetString()
                : r.TryGetProperty("displayName", out var dn) ? dn.GetString() : null;
            return (puuid, name);
        }
        catch { return (null, null); }
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
