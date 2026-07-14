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

    private static JsonObject Load()
    {
        try
        {
            if (File.Exists(Path_) &&
                JsonNode.Parse(File.ReadAllText(Path_)) is JsonObject obj)
                return obj;
        }
        catch { /* битый файл — начинаем с чистых настроек */ }
        return new JsonObject();
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
