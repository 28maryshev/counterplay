using System.Diagnostics;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Запуск не ждёт чужой сервер.
///
/// Проверка обновлений на старте ходит на GitHub, а тот иногда отвечает
/// минутами. Программа из-за этого не открывалась, хотя работать ей ничто не
/// мешало. Теперь у проверки есть срок: не уложилась — идём работать, её
/// доделает часовой сторож.
///
/// Здесь проверяется сам срок. Дёрнуть настоящий Velopack нельзя — он работает
/// только в установленном экземпляре, — но ломаться будет не он, а то, как мы
/// его ждём: перепутанный тип ошибки превратил бы «не дождались» либо в
/// зависание, либо в «обновлений не бывает никогда».
/// </summary>
internal static class Program
{
    private static int _fails;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        var limit = TimeSpan.FromMilliseconds(300);

        // ── Ответили вовремя — отдаём ответ ────────────────────────────────
        var fast = Deadline.OrNull(Task.FromResult<string?>("1.3.37"), limit)
                           .GetAwaiter().GetResult();
        Check("вовремя — ответ доходит", fast == "1.3.37", fast ?? "null");

        // ── Ответили «обновлений нет» — это тоже ответ ─────────────────────
        var none = Deadline.OrNull(Task.FromResult<string?>(null), limit)
                           .GetAwaiter().GetResult();
        Check("«обновлений нет» проходит как null", none is null, none ?? "null");

        // ── Сервер молчит — не ждём ────────────────────────────────────────
        // Задача не кончится никогда: ровно тот случай, ради которого срок и
        // заведён.
        var sw = Stopwatch.StartNew();
        var hung = Deadline.OrNull(new TaskCompletionSource<string?>().Task, limit)
                           .GetAwaiter().GetResult();
        sw.Stop();
        Check("молчащий сервер не держит запуск", hung is null, hung ?? "null");
        Check("и отпускает примерно в срок",
              sw.ElapsedMilliseconds >= 250 && sw.ElapsedMilliseconds < 2000,
              $"{sw.ElapsedMilliseconds} мс при сроке {limit.TotalMilliseconds:0}");

        // ── Ошибка сервера доходит до зовущего ─────────────────────────────
        // Её пишут в журнал: «не обновляется» — самая частая жалоба, и глотать
        // причину нельзя.
        var boom = Task.FromException<string?>(new InvalidOperationException("фид битый"));
        var caught = "";
        try { Deadline.OrNull(boom, limit).GetAwaiter().GetResult(); }
        catch (Exception e) { caught = e.Message; }
        Check("ошибка не глотается", caught == "фид битый", caught.Length > 0 ? caught : "проглочена");

        // ── Программу закрывают — ждать нечего ─────────────────────────────
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try
        {
            Deadline.OrNull(new TaskCompletionSource<string?>().Task,
                            TimeSpan.FromMinutes(5), cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check("при закрытии программы ожидание снимается", cancelled, "да");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: запуск не ждёт молчащий сервер"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
