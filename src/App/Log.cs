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

    /// Последние n строк одним текстом — для буфера обмена.
    public static string Tail(int n = 100)
    {
        lock (Gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Counterplay · журнал ({Math.Min(n, Lines.Count)} строк)");
            foreach (var l in Lines.Skip(Math.Max(0, Lines.Count - n))) sb.AppendLine(l);
            return sb.ToString();
        }
    }

    public static int Count { get { lock (Gate) return Lines.Count; } }
}
