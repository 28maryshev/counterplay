using System.IO;
using Microsoft.Win32;

namespace Counterplay;

/// <summary>
/// Автозапуск вместе с Windows: ключ в HKCU\...\Run (прав администратора не
/// требует, пишется в профиль пользователя).
///
/// Ссылаемся на РЕАЛЬНЫЙ exe (%LOCALAPPDATA%\Counterplay\current\Counterplay.exe),
/// а НЕ на стаб в корне. Стаб запускает приложение, но НЕ передаёт ему аргументы:
/// в реестре стоит «…\Counterplay.exe --autostart», а процесс поднимался как
/// «…\current\Counterplay.exe» без флага — программа не понимала, что это
/// автозапуск, и разворачивала окно «ожидание клиента» при каждом включении ПК.
///
/// Путь безопасен: у Velopack папка называется именно `current` и не меняется
/// между версиями — при обновлении подменяется её содержимое, а не имя.
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

    /// Реальный exe приложения — его и прописываем в автозагрузку, т.к. стаб
    /// теряет аргументы. Берём путь запущенного процесса: он и есть
    /// ...\current\Counterplay.exe, а `current` стабильна между обновлениями.
    public static string? AppExePath
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath;
                return exe is not null && File.Exists(exe) ? exe : null;
            }
            catch { return null; }
        }
    }

    /// Значение, которое ДОЛЖНО быть в реестре (реальный exe + флаг тихого старта).
    /// Фолбэк на стаб — если путь процесса недоступен: без флага окно всплывёт,
    /// но автозапуск хотя бы будет работать.
    private static string? DesiredValue =>
        AppExePath is { } exe ? $"\"{exe}\" --autostart"
        : StubPath is { } stub ? $"\"{stub}\" --autostart"
        : null;

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

    /// Ключ есть, но записан по-старому (без --autostart или со сменившимся путём).
    private static bool NeedsRefresh
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                var current = key?.GetValue(ValueName) as string;
                return current is { Length: > 0 } && current != DesiredValue;
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
                // --autostart: приложение стартует свёрнутым в трей (иначе при входе
                // в Windows выскакивало бы окно «LCU is starting…»).
                if (DesiredValue is null) return false; // не установка — включать нечего
                key.SetValue(ValueName, DesiredValue);
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

        // Ключ мог остаться от прошлой версии (без --autostart) или указывать на
        // старый путь — тогда перезаписываем. И лечим, если его удалил чистильщик.
        if (wanted.Value != IsEnabled || (wanted.Value && NeedsRefresh)) Set(wanted.Value);
        return false;
    }
}
