using System.Text.Json;
using System.Text.RegularExpressions;
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
    // Что предмет делает — текстом, на языке игрока. Для подсказки при наведении:
    // по одной иконке не вспомнить, что даёт Кинжал павшего короля, а сборка из
    // шести незнакомых значков ничего не объясняет.
    private static Dictionary<int, string> _descs = new();
    private static Dictionary<int, int[]> _from = new();   // id → прямые компоненты

    private static string? _namesLocale;

    public static async Task LoadNamesAsync(string locale, CancellationToken ct)
    {
        if (_namesLocale == locale && _names is not null) return;
        _namesLocale = locale;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{DataDragon.Version}/data/{locale}/item.json", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var map = new Dictionary<int, string>();
            var descs = new Dictionary<int, string>();
            var from = new Dictionary<int, int[]>();
            foreach (var it in doc.RootElement.GetProperty("data").EnumerateObject())
            {
                if (!int.TryParse(it.Name, out var id)) continue;
                if (it.Value.TryGetProperty("name", out var n)) map[id] = n.GetString() ?? "";
                if (it.Value.TryGetProperty("description", out var d))
                    descs[id] = CleanDesc(d.GetString() ?? "");
                if (it.Value.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Array)
                    from[id] = f.EnumerateArray()
                                .Select(x => int.TryParse(x.GetString(), out var c) ? c : 0)
                                .Where(c => c > 0).ToArray();
            }
            _names = map;
            _descs = descs;
            _from = from;
        }
        catch { _names = new Dictionary<int, string>(); }
    }

    public static string NameOf(int id) => _names?.GetValueOrDefault(id) ?? $"#{id}";

    /// Описание предмета на языке игрока. Пусто — описания нет (или ещё не
    /// загрузились): подсказка тогда покажет одно название.
    public static string DescOf(int id) => _descs.GetValueOrDefault(id, "");

    /// Riot отдаёт описание разметкой: «<stats>45 <attention>Сила умений</attention>
    /// </stats><passive>Освящение</passive><br>Лечение союзника…». Переводим её в
    /// обычный текст: характеристики отдельной строкой, каждое свойство с новой,
    /// а сказку про происхождение предмета выбрасываем — в подсказке она только
    /// занимает место.
    /// Riot отдаёт описание разметкой: «&lt;stats&gt;45 Сила умений&lt;/stats&gt;
    /// &lt;passive&gt;Освящение&lt;/passive&gt;&lt;br&gt;Лечение союзника…». Переводим её в
    /// обычный текст: характеристики отдельной строкой, каждое свойство с новой,
    /// а сказку про происхождение предмета выбрасываем — в подсказке она только
    /// занимает место.
    private static string CleanDesc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var s = Regex.Replace(raw, "<flavorText>.*?</flavorText>", "",
                              RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        // Начало блока — с новой строки: иначе характеристики и свойства
        // сливаются в одно предложение.
        s = Regex.Replace(s, "<(stats|passive|active|rules)>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        // Пробелов от разметки много, и без чистки текст выглядит рваным.
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" ?\n ?", "\n");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

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
