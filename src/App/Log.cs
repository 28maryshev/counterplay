using System.Text;

namespace Counterplay;

/// <summary>
/// Кольцевой журнал последних событий программы. Нужен для разбора жалоб:
/// человек нажимает «Копировать логи» в тестовом режиме и присылает срез — видно,
/// какие настройки он менял, какого размера был клиент, как встало окно.
///
/// Хранится только в памяти: на диск не пишем, чтобы не плодить файлы и не
/// собирать ничего без ведома пользователя.
/// </summary>
public static class Log
{
    private const int Capacity = 400;
    private static readonly Queue<string> Lines = new(Capacity);
    private static readonly object Gate = new();

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
