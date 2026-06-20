using System.Text.Json;

namespace Counterplay;

public static class DataDragon
{
    private sealed record ChampInfo(string Name, string DdId);

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
                    _champions[id] = new ChampInfo(
                        Name: nameEl.GetString() ?? entry.Name,
                        DdId: entry.Name);
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

    /// URL иконки для оверлея.
    public static string IconUrl(int id)
    {
        if (_champions is not null && _champions.TryGetValue(id, out var info))
            return $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{info.DdId}.png";
        return "";
    }
}
