using System.IO;
using Microsoft.Win32;

namespace Counterplay;

/// <summary>
/// Автозапуск вместе с Windows: ключ в HKCU\...\Run (прав администратора не
/// требует, пишется в профиль пользователя).
///
/// Ссылаемся на СТАБ Velopack в корне установки (%LOCALAPPDATA%\Counterplay\
/// Counterplay.exe), а не на current\Counterplay.exe: папка версии меняется при
/// каждом обновлении, и прямой путь сломался бы на следующем релизе. Стаб всегда
/// запускает актуальную версию.
///
/// Программа и так стартует свёрнутой в трей и разворачивается только на драфте,
/// поэтому отдельный «тихий» режим не нужен.
/// </summary>
public static class Autostart
{
    private const string RunKey     = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "Counterplay";

    /// Путь к стабу. null — приложение запущено не из установки Velopack
    /// (например, dotnet run в разработке): автозапуск недоступен.
    public static string? StubPath
    {
        get
        {
            try
            {
                // Исполняемся из ...\Counterplay\current\ → корень установки на уровень выше.
                var root = new DirectoryInfo(AppContext.BaseDirectory).Parent;
                if (root is null) return null;
                var stub   = Path.Combine(root.FullName, "Counterplay.exe");
                var update = Path.Combine(root.FullName, "Update.exe"); // признак установки Velopack
                return File.Exists(stub) && File.Exists(update) ? stub : null;
            }
            catch { return null; }
        }
    }

    /// Поддерживается ли автозапуск в этой сборке (установлена через инсталлятор).
    public static bool Supported => StubPath != null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string s && s.Length > 0;
            }
            catch { return false; }
        }
    }

    /// Включить/выключить. Возвращает фактическое состояние после попытки.
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return IsEnabled;

            if (enabled)
            {
                var stub = StubPath;
                if (stub is null) return false;                    // не установка — включать нечего
                // --autostart: приложение стартует свёрнутым в трей (иначе при входе
                // в Windows выскакивало бы окно «LCU is starting…»).
                key.SetValue(ValueName, $"\"{stub}\" --autostart"); // кавычки: в пути бывают пробелы
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* реестр недоступен — состояние не меняем */ }

        return IsEnabled;
    }

    /// <summary>
    /// Применить настройку при старте.
    /// Первый запуск после обновления (настройка ни разу не задавалась) —
    /// включаем и просим показать разовое уведомление. Дальше — уважаем выбор
    /// пользователя и лечим ключ, если он пропал (переустановка, чистильщики).
    /// Возвращает true, если нужно показать уведомление о включении.
    /// </summary>
    public static bool ApplyOnStartup()
    {
        if (!Supported) return false;

        var wanted = Settings.GetBool("autostart");
        if (wanted is null)
        {
            Set(true);
            Settings.Set("autostart", true);
            return true; // первый раз — уведомляем
        }

        if (wanted.Value != IsEnabled) Set(wanted.Value);
        return false;
    }
}
