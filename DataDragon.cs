using System.Text.Json;

namespace Counterplay;

public static class DataDragon
{
    private sealed record ChampInfo(string Name, string DdId, string[] Tags, int Attack, int Magic);

    private static Dictionary<int, ChampInfo>? _champions;
    private static string _version = "14.10.1";

    public static string Version => _version;

    public static async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var vJson = await http.GetStringAsync(
                "https://ddragon.leagueoflegends.com/api/versions.json", ct);
            using var vDoc = JsonDocument.Parse(vJson);
            _version = vDoc.RootElement[0].GetString() ?? _version;

            // Русская локаль: name = локализованное имя
            var cJson = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/ru_RU/champion.json", ct);
            using var cDoc = JsonDocument.Parse(cJson);
            var data = cDoc.RootElement.GetProperty("data");

            _champions = [];
            foreach (var entry in data.EnumerateObject())
            {
                var val = entry.Value;
                if (val.TryGetProperty("key", out var keyEl) &&
                    val.TryGetProperty("name", out var nameEl) &&
                    int.TryParse(keyEl.GetString(), out var id))
                {
                    var tags = Array.Empty<string>();
                    if (val.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                        tags = tagsEl.EnumerateArray()
                                     .Where(x => x.ValueKind == JsonValueKind.String)
                                     .Select(x => x.GetString()!).ToArray();

                    int atk = 0, mag = 0;
                    if (val.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
                    {
                        if (infoEl.TryGetProperty("attack", out var aEl) && aEl.ValueKind == JsonValueKind.Number) atk = aEl.GetInt32();
                        if (infoEl.TryGetProperty("magic",  out var mEl) && mEl.ValueKind == JsonValueKind.Number) mag = mEl.GetInt32();
                    }

                    _champions[id] = new ChampInfo(
                        Name: nameEl.GetString() ?? entry.Name,
                        DdId: entry.Name,
                        Tags: tags,
                        Attack: atk,
                        Magic: mag);
                }
            }
            Console.WriteLine($"Data Dragon загружен: {_champions.Count} чемпионов, патч {_version}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Data Dragon недоступен ({ex.Message}) — показываем ID.");
        }
    }

    /// Русское имя чемпиона. Возвращает "id=N" если Data Dragon не загружен.
    public static string Name(int id)
    {
        if (id == 0) return "—";
        if (_champions is not null && _champions.TryGetValue(id, out var info)) return info.Name;
        return $"id={id}";
    }

    /// Классовые теги чемпиона из Data Dragon (Fighter/Tank/Mage/Assassin/Marksman/Support).
    public static string[] ClassTags(int id) =>
        _champions is not null && _champions.TryGetValue(id, out var info) ? info.Tags : [];

    /// Преимущественно магический урон (magic > attack по Data Dragon info).
    public static bool IsApChampion(int id) =>
        _champions is not null && _champions.TryGetValue(id, out var info) && info.Magic > info.Attack;

    /// URL иконки для оверлея.
    public static string IconUrl(int id)
    {
        if (_champions is not null && _champions.TryGetValue(id, out var info))
            return $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{info.DdId}.png";
        return "";
    }

    /// Все пары id → URL иконки (для предзагрузки).
    public static IReadOnlyDictionary<int, string> GetAllIconUrls()
    {
        if (_champions is null) return new Dictionary<int, string>();
        return _champions.ToDictionary(
            kvp => kvp.Key,
            kvp => $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{kvp.Value.DdId}.png");
    }
}
