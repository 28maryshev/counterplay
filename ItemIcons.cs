using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Иконки контр-предметов (фикс. набор из ItemValue.CounterItems). Грузятся один
/// раз при старте с Data Dragon, кэшируются на диск (%APPDATA%\Counterplay\items).
/// </summary>
public static class ItemIcons
{
    // Набор = все предметы, что может вернуть ItemValue.CounterItems.
    private static readonly int[] Ids = [3111, 3165, 3075, 3143, 3110, 3065, 2504];
    private static readonly Dictionary<int, ImageSource> _icons = new();

    public static async Task PreloadAsync(CancellationToken ct)
    {
        var ver = DataDragon.Version;
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "items");
        try { Directory.CreateDirectory(cacheDir); } catch { }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        foreach (var id in Ids)
        {
            try
            {
                var path = Path.Combine(cacheDir, $"{id}.png");
                byte[] bytes;
                if (File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path, ct);
                }
                else
                {
                    bytes = await http.GetByteArrayAsync(
                        $"https://ddragon.leagueoflegends.com/cdn/{ver}/img/item/{id}.png", ct);
                    try { await File.WriteAllBytesAsync(path, bytes, ct); } catch { }
                }
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.DecodePixelWidth = 48;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _icons[id] = bmp;
            }
            catch { /* нет иконки — не критично */ }
        }
    }

    public static ImageSource? Get(int id) => _icons.TryGetValue(id, out var img) ? img : null;

    // Названия предметов + карта компонентов (из чего собирается). Грузим один раз.
    private static Dictionary<int, string>? _names;
    private static Dictionary<int, int[]> _from = new();   // id → прямые компоненты

    public static async Task LoadNamesAsync(string locale, CancellationToken ct)
    {
        if (_names is not null) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{DataDragon.Version}/data/{locale}/item.json", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var map = new Dictionary<int, string>();
            var from = new Dictionary<int, int[]>();
            foreach (var it in doc.RootElement.GetProperty("data").EnumerateObject())
            {
                if (!int.TryParse(it.Name, out var id)) continue;
                if (it.Value.TryGetProperty("name", out var n)) map[id] = n.GetString() ?? "";
                if (it.Value.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Array)
                    from[id] = f.EnumerateArray()
                                .Select(x => int.TryParse(x.GetString(), out var c) ? c : 0)
                                .Where(c => c > 0).ToArray();
            }
            _names = map;
            _from = from;
        }
        catch { _names = new Dictionary<int, string>(); }
    }

    public static string NameOf(int id) => _names?.GetValueOrDefault(id) ?? $"#{id}";

    /// <summary>
    /// Предмет со всеми компонентами по порядку сборки: базовые → готовый.
    /// Для «поэтапной» покупки в наборе (Слеза → … → Манамьюн).
    /// </summary>
    public static IReadOnlyList<int> WithComponents(int id)
    {
        var acc = new List<int>();
        var seen = new HashSet<int>();
        void Add(int x)
        {
            if (!seen.Add(x)) return;
            if (_from.TryGetValue(x, out var parts))
                foreach (var c in parts) Add(c);
            acc.Add(x);   // компоненты идут перед готовым предметом
        }
        Add(id);
        return acc;
    }

    /// <summary>
    /// Иконка любого предмета — грузим по требованию (билд из статистики может
    /// содержать что угодно, заранее весь список не выкачаешь). Кэш на диске.
    /// </summary>
    public static ImageSource? GetOrLoad(int id)
    {
        if (_icons.TryGetValue(id, out var cached)) return cached;
        try
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "items");
            Directory.CreateDirectory(cacheDir);
            var path = Path.Combine(cacheDir, $"{id}.png");

            byte[] bytes;
            if (File.Exists(path))
            {
                bytes = File.ReadAllBytes(path);
            }
            else
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                bytes = http.GetByteArrayAsync(
                    $"https://ddragon.leagueoflegends.com/cdn/{DataDragon.Version}/img/item/{id}.png")
                    .GetAwaiter().GetResult();
                try { File.WriteAllBytes(path, bytes); } catch { }
            }

            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = 48;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _icons[id] = bmp;
            return bmp;
        }
        catch { return null; }
    }
}
