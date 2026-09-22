using System.IO;

namespace Counterplay;

/// <summary>
/// Передача «открой этот файл» уже работающей копии программы.
///
/// Экземпляр у нас один на пользователя: второй запуск будит первый сигналом и
/// выходит. Сигнал — голое событие, данных в нём нет, а при двойном клике по
/// файлу пула путь донести надо. Кладём его в маленький файл рядом с настройками
/// и там же забираем.
///
/// Именно файл, а не именованный канал: запрос переживёт секунду, пока первый
/// экземпляр разворачивается из трея, и не потребует держать сервер на приём.
/// </summary>
public static class OpenRequest
{
    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "open-request.txt");

    /// Оставить запрос (вызывает второй экземпляр перед тем, как выйти).
    public static void Put(string file)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, file);
        }
        catch { /* не записалось — просто развернём окно без импорта */ }
    }

    /// Забрать запрос и удалить его. null — запроса нет.
    /// Удаляем СРАЗУ: иначе один файл предлагался бы к загрузке при каждом
    /// следующем пробуждении окна.
    public static string? Take()
    {
        try
        {
            if (!File.Exists(Path_)) return null;
            var file = File.ReadAllText(Path_).Trim();
            File.Delete(Path_);
            return file.Length > 0 && File.Exists(file) ? file : null;
        }
        catch { return null; }
    }
}
