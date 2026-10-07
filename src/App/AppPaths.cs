namespace Counterplay;

/// <summary>
/// Папка данных игрока — %APPDATA%\Counterplay — в ОДНОМ месте.
///
/// Раньше путь собирался заново в каждом хранилище: пулы, журнал игр,
/// настройки, пароль синхронизации, кэши. Подменить его проверкам было нечем,
/// и проверки писали в настоящие файлы игрока: настройки интерфейса меняла
/// PoolWindow, пароль и пулы — Sync, и однажды проверка стёрла пулы владельца.
/// Теперь каждое хранилище берёт путь отсюда, а проверки подставляют свою папку
/// (<see cref="RootOverride"/>). Проверка tests/PlayerData следит, чтобы мимо
/// этого места в папку игрока никто не ходил.
/// </summary>
public static class AppPaths
{
    /// Своя папка вместо папки игрока. Ставят ТОЛЬКО проверки.
    public static string? RootOverride { get; set; }

    public static string Root => RootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
}
