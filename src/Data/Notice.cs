using System.Net.Http;
using System.Text.Json;

namespace Counterplay;

/// Сообщение от нас игроку — то, что видно на плашке «BETA» в окне ожидания.
///
/// Живёт на сайте, а не в сборке: если что-то случилось (встал сбор, вышел
/// патч, нужна помощь с багом), текст меняется в админке и доезжает до людей
/// сам — без обновления программы. Спрашиваем при запуске и дальше раз в
/// полчаса; последний ответ храним на диске, чтобы плашка не пустовала, пока
/// сеть недоступна.
public static class Notice
{
    private const string Url = "https://counterplays.com/api/notice";

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "notice.json");

    /// Готовое к показу сообщение: текст на языке интерфейса, ссылка (если есть)
    /// и вид — "info" (обычная плашка) или "alert" (красная, о неполадке).
    public sealed record Item(string Text, string? Link, string Kind);

    private static JsonDocument? _doc;

    /// Сообщение под текущий язык интерфейса; null — показывать обычный текст.
    public static Item? Current()
    {
        var root = _doc?.RootElement;
        if (root is null || root.Value.ValueKind != JsonValueKind.Object) return null;

        // Срок мог истечь, пока программа была открыта, — проверяем при каждом показе.
        if (root.Value.TryGetProperty("until", out var until)
            && until.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(until.GetString(), out var till)
            && till < DateTimeOffset.UtcNow) return null;

        if (!root.Value.TryGetProperty("text", out var texts)
            || texts.ValueKind != JsonValueKind.Object) return null;

        // Русский интерфейс — русский текст, остальные — английский. Пустое поле
        // не показываем: лучше текст на другом языке, чем пустая плашка.
        var order = Loc.Current == "ru" ? new[] { "ru", "en" } : new[] { "en", "ru" };
        var text = order
            .Select(k => texts.TryGetProperty(k, out var v) ? v.GetString() : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(text)) return null;

        var link = root.Value.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() : null;
        var kind = root.Value.TryGetProperty("kind", out var k) && k.GetString() == "alert"
            ? "alert" : "info";
        return new Item(text!.Trim(), string.IsNullOrWhiteSpace(link) ? null : link, kind);
    }

    /// Читает сохранённый ответ — чтобы плашка была заполнена ещё до сети.
    public static void LoadCached()
    {
        try
        {
            if (File.Exists(CachePath)) Parse(File.ReadAllText(CachePath));
        }
        catch (Exception ex) { Log.Write($"сообщение: кэш не прочитался — {ex.Message}"); }
    }

    /// Спрашивает сайт. Возвращает true, если текст изменился.
    public static async Task<bool> RefreshAsync()
    {
        try
        {
            var before = Current()?.Text;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(Url);
            Parse(json);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, json);
            }
            catch { /* не записалось — не беда, в следующий раз спросим снова */ }

            var after = Current()?.Text;
            if (before != after)
                Log.Write($"сообщение с сайта: {(after is null ? "нет" : Cut(after))}");
            return before != after;
        }
        catch (Exception ex)
        {
            Log.Write($"сообщение с сайта не получено: {ex.Message}");
            return false;
        }
    }

    private static void Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        _doc?.Dispose();
        _doc = doc;
    }

    private static string Cut(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
