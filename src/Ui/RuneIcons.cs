using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Иконки и названия рун (Data Dragon runesReforged.json). Кэш на диске —
/// иконки не меняются, качать их каждый запуск незачем.
/// </summary>
public static class RuneIcons
{
    private sealed record RuneInfo(int Id, string Name, string IconPath, string Desc);

    // Короткое описание руны из Data Dragon приходит с HTML-разметкой
    // (<lol-uikit-tooltipped-keyword>, <br>) — чистим до человеческого текста.
    private static string StripHtml(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "<br\\s*/?>", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = System.Net.WebUtility.HtmlDecode(s);
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// Удаляет картинки прошлых патчей: их больше никто не попросит.
    private static void SweepOldCaches()
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Counterplay", "runes");
            if (!Directory.Exists(root)) return;
            foreach (var dir in Directory.GetDirectories(root))
                if (Path.GetFileName(dir) != _loadedVersion)
                    try { Directory.Delete(dir, recursive: true); } catch { }
            // Картинки, лежавшие в корне до разделения по патчам.
            foreach (var old in Directory.GetFiles(root, "*.png"))
                try { File.Delete(old); } catch { }
        }
        catch { /* не убралось — не беда, место копеечное */ }
    }

    private static readonly Dictionary<int, RuneInfo> Runes = new();
    private static readonly Dictionary<int, string> StyleNames = new();
    private static readonly Dictionary<int, BitmapImage> Cache = new();

    // Картинки кэшируем ПО ПАТЧУ: Riot их перерисовывает, и кэш без номера
    // патча держал бы старую картинку вечно. Папки прошлых патчей подчищаются
    // при загрузке — иначе они копились бы каждые две недели.
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Counterplay", "runes", _loadedVersion ?? "0");

    private static string? _loadedLocale;
    private static string? _loadedVersion;

    /// Загрузить справочник рун. Повторный вызов с другой локалью или после
    /// выхода патча перезагружает названия и описания: Riot правит их вместе с
    /// балансом, и держать прошлый текст — врать игроку.
    public static async Task LoadAsync(string locale, CancellationToken ct)
    {
        if (_loadedLocale == locale && _loadedVersion == DataDragon.Version && Runes.Count > 0)
            return;
        _loadedLocale = locale;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Номер патча берём общий, а не спрашиваем его ещё раз: он уже
            // получен при старте и обновляется вместе с остальными данными.
            var version = DataDragon.Version;
            _loadedVersion = version;
            SweepOldCaches();

            var json = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{version}/data/{locale}/runesReforged.json", ct);

            using var doc = JsonDocument.Parse(json);
            foreach (var style in doc.RootElement.EnumerateArray())
            {
                var styleId = style.GetProperty("id").GetInt32();
                StyleNames[styleId] = style.GetProperty("name").GetString() ?? "";
                foreach (var slot in style.GetProperty("slots").EnumerateArray())
                    foreach (var rune in slot.GetProperty("runes").EnumerateArray())
                    {
                        var id = rune.GetProperty("id").GetInt32();
                        Runes[id] = new RuneInfo(
                            id,
                            rune.GetProperty("name").GetString() ?? "",
                            rune.GetProperty("icon").GetString() ?? "",
                            StripHtml(rune.TryGetProperty("shortDesc", out var d) ? d.GetString() ?? "" : ""));
                    }
            }
        }
        catch { /* офлайн — иконок не будет, панель обойдётся текстом */ }
    }

    public static string NameOf(int runeId) => Runes.GetValueOrDefault(runeId)?.Name ?? $"#{runeId}";

    /// Короткое описание руны своими словами (из Data Dragon, на языке интерфейса).
    public static string DescOf(int runeId) => Runes.GetValueOrDefault(runeId)?.Desc ?? "";

    /// Знаем ли мы такую руну. Riot убирает руны между сезонами (напр. Eyeball
    /// Collection) — старые id из статистики не должны светиться как «#8138».
    public static bool Known(int runeId) => Runes.ContainsKey(runeId);

    public static string StyleName(int styleId) => StyleNames.GetValueOrDefault(styleId, "");

    /// Иконка руны (с диска, иначе качаем и кэшируем).
    public static BitmapImage? Icon(int runeId)
    {
        if (Cache.TryGetValue(runeId, out var img)) return img;
        if (!Runes.TryGetValue(runeId, out var info) || info.IconPath.Length == 0) return null;

        try
        {
            Directory.CreateDirectory(CacheDir);
            var file = Path.Combine(CacheDir, $"{runeId}.png");
            if (!File.Exists(file))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = http.GetByteArrayAsync(
                    $"https://ddragon.leagueoflegends.com/cdn/img/{info.IconPath}").GetAwaiter().GetResult();
                File.WriteAllBytes(file, bytes);
            }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(file);
            bmp.EndInit();
            bmp.Freeze();
            Cache[runeId] = bmp;
            return bmp;
        }
        catch { return null; }
    }
}
