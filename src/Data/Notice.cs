using System.Net.Http;
using System.Text.Json;

namespace Counterplay;

/// Сообщения от нас игроку — то, что видно на плашке «BETA» в окне ожидания.
///
/// Живут на сайте, а не в сборке: если что-то случилось (встал сбор, вышел
/// патч, нужна помощь с багом), текст меняется в админке и доезжает до людей
/// сам — без обновления программы. Спрашиваем при запуске и дальше раз в
/// полчаса; последний ответ храним на диске, чтобы плашка не пустовала, пока
/// сеть недоступна.
///
/// Сообщений может быть несколько — тогда окно показывает их по очереди.
/// Неполадки важнее новостей: если есть хоть одна, показываем только их, иначе
/// сообщение о сбое потеряется в очереди с объявлениями.
public static class Notice
{
    private const string Url = "https://counterplays.com/api/notice";

    private static readonly string CachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "notice.json");

    /// Готовое к показу сообщение: текст на языке интерфейса, ссылка (если есть)
    /// и вид — "info" (обычная плашка) или "alert" (красная, о неполадке).
    public sealed record Item(string Text, string? Link, string Kind, string Head);

    private static JsonDocument? _doc;

    /// Через сколько секунд сменить сообщение, если их несколько.
    public static int RotateSeconds
    {
        get
        {
            var root = _doc?.RootElement;
            if (root is { ValueKind: JsonValueKind.Object }
                && root.Value.TryGetProperty("rotateSec", out var v)
                && v.TryGetInt32(out var sec) && sec >= 5) return sec;
            return 45;
        }
    }

    /// Что показывать сейчас, в порядке очереди. Пусто — обычный текст плашки.
    public static IReadOnlyList<Item> Active()
    {
        var root = _doc?.RootElement;
        if (root is not { ValueKind: JsonValueKind.Object }) return [];

        // Новый ответ — список; старый (одно сообщение) читаем как список из одного.
        var raw = root.Value.TryGetProperty("items", out var items)
                  && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : [root.Value];

        var list = raw.Select(Read).OfType<Item>().ToList();

        // Неполадки вытесняют новости.
        var alerts = list.Where(i => i.Kind == "alert").ToList();
        return alerts.Count > 0 ? alerts : list;
    }

    private static Item? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        // Срок мог истечь, пока программа была открыта, — проверяем при показе.
        if (e.TryGetProperty("until", out var until)
            && until.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(until.GetString(), out var till)
            && till < DateTimeOffset.UtcNow) return null;

        if (!e.TryGetProperty("text", out var texts) || texts.ValueKind != JsonValueKind.Object)
            return null;

        // Сначала — язык интерфейса: сообщения переводятся на все языки
        // программы. Нет перевода (старое сообщение или перевод не сделали) —
        // английский, затем русский. Пустое поле пропускаем: лучше текст на
        // другом языке, чем пустая плашка.
        var order = new[] { Loc.Current, "en", "ru" }.Distinct().ToArray();
        var text = order
            .Select(k => texts.TryGetProperty(k, out var v) ? v.GetString() : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(text)) return null;

        var link = e.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() : null;
        var kind = e.TryGetProperty("kind", out var k) && k.GetString() == "alert" ? "alert" : "info";
        // Заголовок приходит ключом — слово подставляем на языке интерфейса.
        // Незнакомый ключ переводить нечем, поэтому проверяем по списку: иначе
        // на плашке оказалось бы служебное «notice.head.что-то».
        var head = e.TryGetProperty("head", out var h) ? h.GetString() ?? "beta" : "beta";
        if (head is not ("beta" or "new" or "important" or "tip" or "update" or "question"))
            head = "beta";

        return new Item(text!.Trim(), string.IsNullOrWhiteSpace(link) ? null : link, kind, head);
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

    /// Спрашивает сайт. Возвращает true, если показывать нужно что-то другое.
    public static async Task<bool> RefreshAsync()
    {
        try
        {
            var before = Signature();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = await http.GetStringAsync(Url);
            Parse(json);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, json);
            }
            catch { /* не записалось — не беда, в следующий раз спросим снова */ }

            var after = Signature();
            if (before != after)
            {
                var now = Active();
                Log.Write(now.Count == 0
                    ? "сообщений с сайта нет"
                    : $"сообщений с сайта: {now.Count} ({now[0].Kind}) — {Cut(now[0].Text)}");
            }
            return before != after;
        }
        catch (Exception ex)
        {
            Log.Write($"сообщение с сайта не получено: {ex.Message}");
            return false;
        }
    }

    // Чем отличается один набор сообщений от другого — чтобы зря не перерисовывать.
    private static string Signature() =>
        string.Join("¦", Active().Select(i => $"{i.Kind}|{i.Head}|{i.Text}|{i.Link}"));

    private static void Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        _doc?.Dispose();
        _doc = doc;
    }

    private static string Cut(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
