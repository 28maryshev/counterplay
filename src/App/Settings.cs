using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Counterplay;

/// <summary>
/// Настройки приложения (%APPDATA%\Counterplay\settings.json).
/// Общий стор ключ-значение: язык, автозапуск, служебные флаги. Раньше язык
/// писался перезаписью всего файла — любая новая настройка при смене языка
/// затиралась бы.
/// </summary>
public static class Settings
{
    private static readonly object Gate = new();

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "settings.json");

    // Что поменяли в песочнице: живёт в памяти поверх файла (см. Sandbox).
    private static readonly JsonObject SandboxValues = new();

    private static JsonObject Load()
    {
        var obj = new JsonObject();
        try
        {
            if (File.Exists(Path_) &&
                JsonNode.Parse(File.ReadAllText(Path_)) is JsonObject read)
                obj = read;
        }
        catch { /* битый файл — начинаем с чистых настроек */ }
        if (Sandbox.Active)
            foreach (var (k, v) in SandboxValues) obj[k] = v?.DeepClone();
        return obj;
    }

    public static string? GetString(string key)
    {
        lock (Gate)
        {
            try { return Load()[key]?.GetValue<string>(); }
            catch { return null; }
        }
    }

    /// null — настройка ни разу не задавалась (важно отличать от false).
    public static bool? GetBool(string key)
    {
        lock (Gate)
        {
            try { return Load()[key]?.GetValue<bool>(); }
            catch { return null; }
        }
    }

    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            if (Sandbox.Active) { SandboxValues[key] = value?.DeepClone(); return; }
            try
            {
                var obj = Load();
                obj[key] = value;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
                File.WriteAllText(Path_, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* настройки не критичны */ }
        }
    }
}
