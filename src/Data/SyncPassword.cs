using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Counterplay;

/// <summary>
/// Пароль синхронизации настроек и пулов между компьютерами.
///
/// Зачем пароль, а не просто puuid: puuid НЕ секрет — он лежит в каждой игре,
/// и его знает любой, с кем ты играл. Отдавать по нему чужие пулы нельзя.
/// Пароль знает только владелец, и он же превращается в ключ шифрования: на
/// сервер уезжает уже зашифрованное, так что и утечка базы сервера ничего не
/// открывает.
///
/// Храним не сам пароль, а защищённый средствами Windows блок (DPAPI, область
/// — текущий пользователь). Файл, унесённый на другую машину или открытый под
/// другой учётной записью, бесполезен.
///
/// Здесь только хранение и ключ. Обмен с сервером — отдельно.
/// </summary>
public static class SyncPassword
{
    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "sync.dat");

    /// Пароль задан на этом компьютере.
    public static bool IsSet => File.Exists(Path_) && new FileInfo(Path_).Length > 0;

    /// <summary>
    /// Задать пароль. Пустая строка — забыть его (синхронизация выключается).
    /// Возвращает false, если сохранить не вышло.
    /// </summary>
    public static bool Set(string? password)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            if (string.IsNullOrEmpty(password))
            {
                File.Delete(Path_);
                Log.Write("пароль синхронизации убран");
                return true;
            }

            var blob = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(password), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(Path_, blob);
            Log.Write("пароль синхронизации задан");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"пароль синхронизации не сохранился: {e.Message}");
            return false;
        }
    }

    /// Пароль в открытом виде — только для получения ключа. null, если не задан.
    private static string? Read()
    {
        try
        {
            if (!IsSet) return null;
            var blob = ProtectedData.Unprotect(
                File.ReadAllBytes(Path_), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(blob);
        }
        catch
        {
            // Файл унесли с другой машины или сменился пользователь Windows —
            // расшифровать нечем. Это не поломка: пароль просто надо ввести заново.
            return null;
        }
    }

    /// <summary>
    /// Ключ шифрования из пароля. null — пароль не задан.
    ///
    /// Соль — не случайная, а выведенная из puuid владельца: у одного человека
    /// ключ обязан совпасть на ДВУХ компьютерах, иначе второй не прочитает то,
    /// что выложил первый, а случайную соль пришлось бы возить вместе с данными.
    /// Подбор это не облегчает: соль разная у разных людей, а против перебора
    /// работает число проходов.
    /// </summary>
    public static byte[]? KeyFor(string? puuid)
    {
        var pass = Read();
        if (pass is null || string.IsNullOrEmpty(puuid)) return null;

        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("counterplay-sync:" + puuid));
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pass), salt, iterations: 200_000,
            HashAlgorithmName.SHA256, outputLength: 32);
    }
}
