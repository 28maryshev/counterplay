using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Counterplay;

/// Локализация интерфейса. Строки хранятся в assets/i18n/{code}.json (встроены в
/// сборку). Язык определяется автоматически по Windows, можно переключить вручную;
/// выбор сохраняется. Недостающие ключи берутся из английского (фолбэк).
public static class Loc
{
    public sealed record Lang(string Code, string Native, string DDragon);

    // Поддерживаемые языки. Code — ключ файла i18n; DDragon — локаль имён чемпионов.
    // Порядок = порядок в селекторе. Русский — в середине, не первым.
    // Для uk у Data Dragon нет локали → имена чемпионов берём английские (en_US).
    public static readonly IReadOnlyList<Lang> Languages =
    [
        new("en", "English",    "en_US"),
        new("es", "Español",    "es_ES"),
        new("pt", "Português",  "pt_BR"),
        new("de", "Deutsch",    "de_DE"),
        new("fr", "Français",   "fr_FR"),
        new("it", "Italiano",   "it_IT"),
        new("pl", "Polski",     "pl_PL"),
        new("ru", "Русский",    "ru_RU"),
        new("uk", "Українська", "en_US"),
        new("tr", "Türkçe",     "tr_TR"),
        new("vi", "Tiếng Việt", "vi_VN"),
        new("th", "ไทย",        "th_TH"),
        new("ja", "日本語",      "ja_JP"),
        new("ko", "한국어",      "ko_KR"),
        new("zh", "中文",        "zh_CN")
    ];

    private static JsonDocument? _doc;          // текущий язык
    private static JsonDocument? _fallbackDoc;  // английский

    public static string Current { get; private set; } = "en";
    public static event Action? LanguageChanged;

    public static Lang CurrentLang =>
        Languages.FirstOrDefault(l => l.Code == Current)
        ?? Languages.First(l => l.Code == "en");
    public static string DDragonLocale => CurrentLang.DDragon;

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "settings.json");

    /// Вызвать один раз при старте: сохранённый выбор → язык Windows → en.
    public static void Init()
    {
        _fallbackDoc = LoadDoc("en");
        var code = ReadSaved() ?? DetectOs() ?? "en";
        if (Languages.All(l => l.Code != code)) code = "en";
        Current = code;
        _doc = code == "en" ? _fallbackDoc : (LoadDoc(code) ?? _fallbackDoc);
    }

    /// Сменить язык вручную (из селектора). Сохраняет выбор и уведомляет UI.
    public static void SetLanguage(string code)
    {
        if (code == Current || Languages.All(l => l.Code != code)) return;
        Current = code;
        _doc = code == "en" ? _fallbackDoc : (LoadDoc(code) ?? _fallbackDoc);
        Save(code);
        LanguageChanged?.Invoke();
    }

    /// Строка по ключу (поддерживает вложенность через точку: "section.key").
    public static string T(string key)
    {
        if (_doc is not null && Resolve(_doc.RootElement, key) is {ValueKind: JsonValueKind.String} v)
            return v.GetString()!;
        if (_fallbackDoc is not null && Resolve(_fallbackDoc.RootElement, key) is {ValueKind: JsonValueKind.String} f)
            return f.GetString()!;
        return key;
    }

    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// Массив строк по ключу (например, советы).
    public static string[] TArray(string key)
    {
        var el = (_doc is not null ? Resolve(_doc.RootElement, key) : null)
              ?? (_fallbackDoc is not null ? Resolve(_fallbackDoc.RootElement, key) : null);
        if (el is {ValueKind: JsonValueKind.Array} arr)
            return arr.EnumerateArray()
                      .Where(x => x.ValueKind == JsonValueKind.String)
                      .Select(x => x.GetString()!).ToArray();
        return [];
    }

    // ── внутреннее ─────────────────────────────────────────────────────────

    private static JsonElement? Resolve(JsonElement root, string key)
    {
        var cur = root;
        foreach (var part in key.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next))
                return null;
            cur = next;
        }
        return cur;
    }

    private static JsonDocument? LoadDoc(string code)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith($".i18n.{code}.json", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return JsonDocument.Parse(r.ReadToEnd());
        }
        catch { return null; }
    }

    private static string? DetectOs()
    {
        var two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == two) ? two : null;
    }

    private static string? ReadSaved()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
            return doc.RootElement.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
                ? l.GetString() : null;
        }
        catch { return null; }
    }

    private static void Save(string code)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, $"{{\"language\":\"{code}\"}}");
        }
        catch { /* настройки не критичны */ }
    }
}
