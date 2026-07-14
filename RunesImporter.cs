using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Импорт рун и набора предметов прямо в клиент LoL (через LCU).
///
/// Руны: у клиента лимит страниц — если места нет, удаляем СВОЮ прошлую
/// страницу (с нашей меткой), чужие не трогаем.
/// Предметы: набор появляется в магазине во время игры (вкладка «Наборы»).
/// </summary>
public static class RunesImporter
{
    // По этой метке узнаём свои страницы (и переиспользуем их вместо засорения).
    private const string PageMark = "Counterplay";

    /// <summary>Применить страницу рун. true — получилось.</summary>
    public static async Task<bool> ApplyRunesAsync(
        LcuHttpClient http, RunePage page, string championName, CancellationToken ct)
    {
        try
        {
            // 1. Свои прошлые страницы убираем — иначе после нескольких драфтов
            //    у человека будет свалка из наших страниц.
            var (ls, body) = await http.GetAsync("/lol-perks/v1/pages", ct);
            if (ls == 200)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var deletable = !p.TryGetProperty("isDeletable", out var d) || d.GetBoolean();
                    if (deletable && name.StartsWith(PageMark, StringComparison.Ordinal)
                        && p.TryGetProperty("id", out var idEl))
                        await http.DeleteAsync($"/lol-perks/v1/pages/{idEl.GetInt64()}", ct);
                }
            }

            // 2. Порядок важен: 4 основных (первый — кейстоун), 2 вторичных, 3 осколка.
            var selected = page.Perks.Concat(page.Secondary).Concat(page.Shards).ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                name = $"{PageMark}: {championName}",
                primaryStyleId = page.Primary,
                subStyleId = page.Sub,
                selectedPerkIds = selected,
                current = true,   // сразу делаем активной
            });

            var (s, _) = await http.PostAsync("/lol-perks/v1/pages", payload, ct);
            return s is >= 200 and < 300;
        }
        catch { return false; }
    }

    /// <summary>Экспортировать набор предметов (виден в магазине в игре).</summary>
    public static async Task<bool> ExportItemSetAsync(
        LcuHttpClient http, IReadOnlyList<int> items, int championId, string championName,
        CancellationToken ct)
    {
        try
        {
            var (ss, sbody) = await http.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (ss != 200) return false;
            using var sdoc = JsonDocument.Parse(sbody);
            var summonerId = sdoc.RootElement.GetProperty("summonerId").GetInt64();

            // Забираем существующие наборы, чтобы не затереть чужие.
            var (gs, gbody) = await http.GetAsync($"/lol-item-sets/v1/item-sets/{summonerId}/sets", ct);
            var existing = new List<JsonElement>();
            if (gs == 200)
            {
                using var gdoc = JsonDocument.Parse(gbody);
                if (gdoc.RootElement.TryGetProperty("itemSets", out var arr))
                    foreach (var set in arr.EnumerateArray())
                    {
                        var title = set.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        if (!title.StartsWith(PageMark, StringComparison.Ordinal))
                            existing.Add(set.Clone());   // свои старые наборы заменяем
                    }
            }

            var mySet = new
            {
                associatedChampions = new[] { championId },
                associatedMaps = new[] { 11 },
                title = $"{PageMark}: {championName}",
                type = "custom",
                map = "any",
                mode = "any",
                sortrank = 1,
                startedFrom = "blank",
                preferredItemSlots = Array.Empty<object>(),
                uid = Guid.NewGuid().ToString(),
                blocks = new[]
                {
                    new
                    {
                        type = Loc.T("runes.buildBlock"),
                        showIfSummonerSpell = "",
                        hideIfSummonerSpell = "",
                        items = items.Select(i => new { id = i.ToString(), count = 1 }).ToArray(),
                    }
                }
            };

            var sets = new List<object>();
            foreach (var e in existing) sets.Add(JsonSerializer.Deserialize<object>(e.GetRawText())!);
            sets.Add(mySet);

            var payload = JsonSerializer.Serialize(new { accountId = 0, itemSets = sets, timestamp = 0 });
            var (ps, _) = await http.PutAsync($"/lol-item-sets/v1/item-sets/{summonerId}/sets", payload, ct);
            return ps is >= 200 and < 300;
        }
        catch { return false; }
    }
}
