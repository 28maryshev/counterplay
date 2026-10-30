using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Клиент принял соединение и не отвечает — запрос истекает по таймауту.
///
/// HttpClient сообщает о таймауте исключением TaskCanceledException, а это
/// разновидность отмены. Цикл подключения (<c>RunLcuAsync</c>) отмену своего
/// токена считает выходом из программы, а прочие ошибки связи ловит как
/// HttpRequestException / WebSocketException / IOException. Таймаут не
/// подходил ни под то, ни под другое и уходил наверх, где
/// <c>catch (OperationCanceledException)</c> глотал его молча, а
/// <c>finally</c> закрывал программу. Игрок видел: программа пропала.
///
/// Проверяется на настоящем сокете: сервер на localhost принимает соединение
/// и ничего не шлёт (даже рукопожатия TLS) — ровно то, что видит программа от
/// зависшего клиента. Таймаут проверки — две секунды вместо боевых двадцати.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var held = new List<TcpClient>();
        _ = Task.Run(async () =>
        {
            // Принимаем и держим: соединение есть, ответа нет.
            while (true)
            {
                try { held.Add(await listener.AcceptTcpClientAsync()); }
                catch { return; }
            }
        });

        var creds = new LcuCredentials("LeagueClient", Environment.ProcessId, port, "x", "https");
        using var http = new LcuHttpClient(creds, TimeSpan.FromSeconds(2));

        // ── Молчащий клиент — неудача связи, а не отмена ───────────────────
        foreach (var (what, call) in new (string, Func<CancellationToken, Task>)[]
                 {
                     ("GET",    c => http.GetAsync("/lol-gameflow/v1/availability", c)),
                     ("POST",   c => http.PostAsync("/x", "{}", c)),
                     ("PUT",    c => http.PutAsync("/x", "{}", c)),
                     ("DELETE", c => http.DeleteAsync("/x", c)),
                     ("PATCH",  c => http.PatchAsync("/x", "{}", c)),
                 })
        {
            var ex = await Catch(() => call(CancellationToken.None));
            Check($"{what}: молчащий клиент — ошибка связи (HttpRequestException)",
                  ex is HttpRequestException, Describe(ex));
        }

        // ── Отмена — по-прежнему отмена ────────────────────────────────────
        // Ею программа и выходит: закрыли окно — запросы прерываются, цикл
        // подключения узнаёт свою отмену и заканчивается, а не переподключается.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            var ex = await Catch(() => http.GetAsync("/lol-gameflow/v1/availability", cts.Token));
            Check("отмена своим токеном — по-прежнему отмена (OperationCanceledException)",
                  ex is OperationCanceledException, Describe(ex));
        }

        // ── Цикл подключения ловит то, что бросает клиент ───────────────────
        // Тот же фильтр, что в RunLcuAsync: ошибка связи — ждём клиент снова.
        {
            var ex = await Catch(() => http.GetAsync("/lol-gameflow/v1/session", CancellationToken.None));
            var caught = ex is HttpRequestException or System.Net.WebSockets.WebSocketException or IOException;
            var quits  = ex is OperationCanceledException;
            Check("цикл подключения переждёт, а не закроет программу",
                  caught && !quits, Describe(ex));
        }

        listener.Stop();
        foreach (var c in held) c.Dispose();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: молчащий клиент — ошибка связи, программа ждёт его снова"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static async Task<Exception?> Catch(Func<Task> call)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await call(); return null; }
        catch (Exception ex) { return ex; }
        finally { _last = sw.Elapsed; }
    }

    private static TimeSpan _last;

    private static string Describe(Exception? ex) =>
        ex is null ? "ответ пришёл (не должен был)"
                   : $"{ex.GetType().Name} за {_last.TotalSeconds:0.0} с: {ex.Message}";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
