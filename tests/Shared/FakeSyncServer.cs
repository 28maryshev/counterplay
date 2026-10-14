using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Counterplay.Tests;

/// <summary>
/// Свой крошечный сервер с тем же контрактом, что у <c>/api/sync/{id}</c> сайта:
/// GET отдаёт <c>{rev, blob}</c> (нет строки — <c>rev 0</c> и пустой ком), PUT
/// записывает, только если номер версии совпал, иначе 409.
///
/// Общий для проверок, которые ходят через этот адрес: синхронизации и
/// дуо-обмена. Жил копией внутри одной из них — второй копии не заводим.
/// Подключается в проект проверки строкой
/// <c>&lt;Compile Include="..\Shared\FakeSyncServer.cs" /&gt;</c>.
/// </summary>
internal sealed class FakeSyncServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (int Rev, byte[] Blob)> _rows = [];

    /// Адрес, который ставится в <c>EndpointOverride</c>, со слэшем на конце.
    public string Url { get; }

    public FakeSyncServer()
    {
        var port = FreePort();
        Url = $"http://localhost:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    /// Сколько строк на «сервере».
    public int Count { get { lock (_rows) return _rows.Count; } }

    /// Строки как есть: id → (версия, ком).
    public List<(string Id, int Rev, byte[] Blob)> Rows()
    {
        lock (_rows) return _rows.Select(kv => (kv.Key, kv.Value.Rev, kv.Value.Blob)).ToList();
    }

    /// Сеть «пропала»: дальше на этот адрес не ответит никто.
    public void Stop() { try { _listener.Stop(); } catch { } }

    public void Dispose() { try { _listener.Close(); } catch { } }

    private void Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext c;
            try { c = _listener.GetContext(); } catch { return; }
            try
            {
                var id = c.Request.Url!.AbsolutePath.Trim('/');
                byte[] body;

                if (c.Request.HttpMethod == "GET")
                {
                    (int Rev, byte[] Blob) row;
                    bool has;
                    lock (_rows) has = _rows.TryGetValue(id, out row);
                    // Нет строки — это не ошибка: так выглядит первый заход.
                    body = has
                        ? Encoding.UTF8.GetBytes(
                            $"{{\"rev\":{row.Rev},\"blob\":\"{Convert.ToBase64String(row.Blob)}\"}}")
                        : Encoding.UTF8.GetBytes("{\"rev\":0,\"blob\":null}");
                }
                else if (c.Request.HttpMethod == "PUT")
                {
                    using var sr = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(sr.ReadToEnd());
                    var rev = doc.RootElement.GetProperty("rev").GetInt32();
                    var blob = Convert.FromBase64String(doc.RootElement.GetProperty("blob").GetString()!);

                    lock (_rows)
                    {
                        var have = _rows.TryGetValue(id, out var cur) ? cur.Rev : 0;
                        if (have != rev)
                        {
                            // Как настоящий: версия не та — 409 и текущий номер.
                            c.Response.StatusCode = 409;
                            body = Encoding.UTF8.GetBytes($"{{\"error\":\"stale\",\"rev\":{have}}}");
                        }
                        else
                        {
                            _rows[id] = (have + 1, blob);
                            body = Encoding.UTF8.GetBytes($"{{\"rev\":{have + 1}}}");
                        }
                    }
                }
                else
                {
                    c.Response.StatusCode = 405;
                    body = [];
                }

                c.Response.ContentType = "application/json";
                c.Response.ContentLength64 = body.Length;
                c.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch { /* проверка не про устойчивость сервера */ }
            finally { try { c.Response.Close(); } catch { } }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
