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
}
