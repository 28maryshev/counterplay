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

    /// Готовит все ~170 иконок. Дисковый кэш по версии патча: первый раз качает из
    /// сети и сохраняет на диск, дальше читает с диска (быстро, без сети). Вызывается
    /// один раз при старте.
    /// Папка кэша под текущий патч: %APPDATA%\Counterplay\icons\{version}\{id}.png
    private static string CacheDirFor(string version) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "icons", version);

    /// <summary>
    /// Все ли портреты уже лежат на диске.
    ///
    /// От этого зависит, ждать их на запуске или тянуть фоном. С диска 170
    /// маленьких файлов читаются за доли секунды — ждать дешевле, чем городить
    /// дорисовку: вид, собранный до готовности иконок, остаётся с пустыми
    /// слотами, и каждый такой вид приходится вспоминать и обновлять отдельно.
    /// Из сети — дело другое, там ждать нельзя.
    /// </summary>
    public static bool AllCached()
    {
        try
        {
            var urls = DataDragon.GetAllIconUrls();
            if (urls.Count == 0) return false;
            var dir = CacheDirFor(DataDragon.Version);
            if (!Directory.Exists(dir)) return false;
            return urls.Keys.All(id => File.Exists(Path.Combine(dir, $"{id}.png")));
        }
        catch { return false; }
    }

    public static async Task PreloadAllAsync(Action<string>? progress, CancellationToken ct)
    {
        var urls = DataDragon.GetAllIconUrls();
        if (urls.Count == 0) return;

        var cacheDir = CacheDirFor(DataDragon.Version);
        try
        {
            Directory.CreateDirectory(cacheDir);
            // Чистим кэши старых патчей, чтобы не копить мусор.
            var parent = Directory.GetParent(cacheDir)?.FullName;
            if (parent is not null)
                foreach (var d in Directory.GetDirectories(parent))
                    if (!string.Equals(Path.GetFileName(d), DataDragon.Version, StringComparison.Ordinal))
                        try { Directory.Delete(d, recursive: true); } catch { }
        }
        catch { /* кэш не критичен */ }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var sem   = new SemaphoreSlim(16); // 16 параллельных загрузок
        var done  = 0;
        var total = urls.Count;

        var tasks = urls.Select(async kvp =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var path = Path.Combine(cacheDir, $"{kvp.Key}.png");
                byte[] bytes;
                if (File.Exists(path))
                {
                    bytes = await File.ReadAllBytesAsync(path, ct); // из кэша, без сети
                }
                else
                {
                    bytes = await http.GetByteArrayAsync(kvp.Value, ct);
                    try { await File.WriteAllBytesAsync(path, bytes, ct); } catch { /* диск недоступен — просто не кэшируем */ }
                }
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
