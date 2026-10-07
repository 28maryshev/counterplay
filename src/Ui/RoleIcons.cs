using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Иконки ролей (позиций) — встроены в программу (assets/roles/{pos}.png), как
/// эмблемы рангов. Грузятся при первом обращении, без сети.
///
/// Раньше качались с Community Dragon при каждом запуске. 7 октября он отвечал
/// по 20–40 секунд при таймауте 15: иконки не приезжали вовсе, а в бою пять
/// запросов подряд держали весь прогрев — руны и сборка появлялись через минуту
/// с лишним после запуска. Дисковый кэш не спасал: чистка старых патчей в
/// IconCache стирала папку roles при каждом запуске. Пять картинок по
/// килобайту — не повод зависеть от чужого сервера.
/// </summary>
public static class RoleIcons
{
    // LCU position → ImageSource. null в значении — картинки в ресурсах нет.
    private static readonly ConcurrentDictionary<string, ImageSource?> _icons = new();

    // Позиции LCU точно совпадают с именами файлов в ресурсах.
    private static readonly string[] Positions = ["top", "jungle", "middle", "bottom", "utility"];

    /// Иконка по позиции LCU (top/jungle/middle/bottom/utility). null если нет.
    public static ImageSource? Get(string position)
    {
        if (string.IsNullOrEmpty(position)) return null;
        var pos = position.ToLowerInvariant();
        return Positions.Contains(pos) ? _icons.GetOrAdd(pos, Load) : null;
    }

    private static ImageSource? Load(string pos)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = new Uri($"pack://application:,,,/assets/roles/{pos}.png");
            bmp.DecodePixelWidth = 64;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
