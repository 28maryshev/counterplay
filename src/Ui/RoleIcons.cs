using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Иконки ролей (позиций) из ассетов клиента через Community Dragon.
/// Грузятся один раз при старте; если недоступны — Get вернёт null и слот
/// просто останется без глифа (подсветка слота всё равно работает).
/// </summary>
public static class RoleIcons
{
    // LCU position → ImageSource. Пишется фоном, читается из UI — поэтому
    // потокобезопасный словарь.
    private static readonly ConcurrentDictionary<string, ImageSource> _icons = new();

    // Позиции LCU точно совпадают с именами файлов в ассетах.
    private static readonly string[] Positions = ["top", "jungle", "middle", "bottom", "utility"];

    private static string Url(string pos) =>
        "https://raw.communitydragon.org/latest/plugins/rcp-fe-lol-clash/global/default/" +
        $"assets/images/position-selector/positions/icon-position-{pos}.png";

    /// <summary>
    /// Пять иконок — с диска, а каких нет, те из сети, все сразу.
    ///
    /// Раньше качались по одной с таймаутом 15 с. 7 октября Community Dragon
    /// отвечал за 19.5 с: все пять запросов падали по таймауту, кэш не
    /// наполнялся, и каждый запуск ждал 75 секунд. В бою на это время вставал
    /// весь прогрев — руны и сборка появлялись через минуту с лишним, — а
    /// песочница стояла на «загрузке иконок». Хуже того, кэш пустел не только
    /// из-за сети: чистка старых патчей в IconCache сносила папку roles целиком
    /// при каждом запуске. Её вылечили там; здесь — параллельно и с запасом по
    /// времени, а звать это надо так, чтобы никого не держать.
    /// </summary>
    public static async Task PreloadAsync(CancellationToken ct)
    {
        // Дисковый кэш: %APPDATA%\Counterplay\icons\roles\{pos}.png (иконки статичны).
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Counterplay", "icons", "roles");
        try { Directory.CreateDirectory(cacheDir); } catch { }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        await Task.WhenAll(Positions.Select(async pos =>
        {
            try
            {
                var path = Path.Combine(cacheDir, $"{pos}.png");
                byte[] bytes;
                if (File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path, ct);
                }
                else
                {
                    bytes = await http.GetByteArrayAsync(Url(pos), ct);
                    try { await File.WriteAllBytesAsync(path, bytes, ct); } catch { }
                }
                using var ms = new MemoryStream(bytes);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource     = ms;
                bmp.DecodePixelWidth = 64;
                bmp.CacheOption      = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                _icons[pos] = bmp;
            }
            catch { /* нет иконки — не критично */ }
        }));
    }

    /// Иконка по позиции LCU (top/jungle/middle/bottom/utility). null если нет.
    public static ImageSource? Get(string position)
    {
        if (string.IsNullOrEmpty(position)) return null;
        return _icons.TryGetValue(position.ToLowerInvariant(), out var img) ? img : null;
    }
}
