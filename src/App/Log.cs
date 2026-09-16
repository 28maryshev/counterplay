using System.Text;

namespace Counterplay;

/// <summary>
/// Кольцевой журнал последних событий программы. Нужен для разбора жалоб:
/// человек нажимает «Копировать логи» в тестовом режиме и присылает срез — видно,
/// какие настройки он менял, какого размера был клиент, как встало окно.
///
/// Дублируется в файл <c>%APPDATA%\Counterplay\log.txt</c>. Раньше журнал жил
/// только в памяти, и когда у игрока что-то не работало, посмотреть было не на
/// что: программа собрана как оконная, консоли у неё нет, а кнопка «Логи» есть
/// только в тестовом режиме. Файл остаётся на машине и никуда не отправляется —
/// то, ради чего журнал держали в памяти, этим не нарушено.
/// </summary>
public static class Log
{
    private const int Capacity = 400;
    private static readonly Queue<string> Lines = new(Capacity);
    private static readonly object Gate = new();

    // Больше мегабайта держать незачем: журнал нужен, чтобы посмотреть, что было
    // только что. Прошлый файл оставляем один — на случай, если программа успела
    // перезапуститься до того, как на неё посмотрели.
    private const long MaxBytes = 1024 * 1024;
    private static string? _file;
    private static bool _fileReady;

    private static string? FilePath()
    {
        if (_fileReady) return _file;
        _fileReady = true;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "log.txt");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path,
                $"{Environment.NewLine}──── запуск {DateTime.Now:yyyy-MM-dd HH:mm:ss}, версия {Version}, режим {Mode} ────{Environment.NewLine}",
                Encoding.UTF8);
            _file = path;
        }
        catch { _file = null; /* некуда писать — обойдёмся памятью */ }
        return _file;
    }

    /// Заполняются при старте — попадают в шапку снимка, чтобы по присланному
    /// журналу сразу было видно сборку и в каком режиме она работала.
    public static string Version { get; set; } = "?";
    public static string Mode    { get; set; } = "боевой";

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            if (Lines.Count >= Capacity) Lines.Dequeue();
            Lines.Enqueue(line);
            var path = FilePath();
            // Запись в файл не должна ронять программу: не вышло — и ладно,
            // в памяти строка всё равно есть.
            if (path is not null)
                try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
        }
        Console.WriteLine(line);   // в dev-режиме видно сразу в консоли
    }

    /// Снимок состояния на момент копирования: версия, экран, настройки, клиент.
    /// Заполняется оверлеем при старте. Собираем ИМЕННО при копировании, а не
    /// пишем строкой в журнал: за 400 строк шапка уехала бы из хвоста, и в
    /// присланном куске не было бы главного — с какими настройками всё это было.
    public static Func<string>? Snapshot { get; set; }

    /// Последние n строк вместе со снимком состояния — для буфера обмена.
    public static string Tail(int n = 100)
    {
        var sb = new StringBuilder();
        try { sb.Append(Snapshot?.Invoke()); }
        catch (Exception ex) { sb.AppendLine($"(снимок состояния не собрался: {ex.Message})"); }

        lock (Gate)
        {
            sb.AppendLine($"── события ({Math.Min(n, Lines.Count)} из {Lines.Count}) ──");
            foreach (var l in Lines.Skip(Math.Max(0, Lines.Count - n))) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    public static int Count { get { lock (Gate) return Lines.Count; } }
}
