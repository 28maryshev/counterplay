using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Предзагружает все иконки чемпионов параллельно при старте,
/// хранит сырые байты и создаёт BitmapImage из памяти (всегда синхронно).
/// </summary>
public static class IconCache
{
    private static readonly ConcurrentDictionary<int, byte[]>  _bytes  = new();
    private static readonly Dictionary<int, ImageSource>        _images = new();

    /// Скачивает все ~170 иконок параллельно. Вызывается один раз при старте.
    public static async Task PreloadAllAsync(Action<string>? progress, CancellationToken ct)
    {
        var urls = DataDragon.GetAllIconUrls();
        if (urls.Count == 0) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var sem   = new SemaphoreSlim(16); // 16 параллельных загрузок
        var done  = 0;
        var total = urls.Count;

        var tasks = urls.Select(async kvp =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var bytes = await http.GetByteArrayAsync(kvp.Value, ct);
                _bytes[kvp.Key] = bytes;
                var n = Interlocked.Increment(ref done);
                if (n % 20 == 0 || n == total)
                    progress?.Invoke(Loc.T("status.loadingIconsN", n, total));
            }
            catch { /* пропускаем отдельные ошибки */ }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
    }

    /// Возвращает ImageSource из кэша. Должна вызываться из UI-потока.
    public static ImageSource? Get(int champId)
    {
        if (champId <= 0) return null;

        if (_images.TryGetValue(champId, out var img)) return img;

        if (!_bytes.TryRemove(champId, out var bytes)) return null;

        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource     = ms;
            bmp.DecodePixelWidth = 128; // портрет в новом дизайне 88px — берём с запасом
            bmp.CacheOption      = BitmapCacheOption.OnLoad; // байты уже в памяти → синхронно
            bmp.EndInit();
            bmp.Freeze(); // теперь можно заморозить: данные загружены
            _images[champId] = bmp;
            return bmp;
        }
        catch { return null; }
    }
}
