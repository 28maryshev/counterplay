using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Counterplay.Tests;

/// <summary>
/// Подставной клиент League для журнала игр: кто в клиенте и последние игры
/// из истории. Остальные адреса отвечают 404 — так же, как настоящий клиент,
/// когда ему нечего сказать, и журнал на это уже рассчитан.
///
/// Клиент программы ходит строго на <c>https://127.0.0.1:{порт}</c>
/// (<c>LcuCredentials.HttpBase</c>), поэтому сервер — на TLS с самоподписанным
/// сертификатом, который клиент принимает, как и настоящий.
/// Подключается в проект проверки строкой
/// <c>&lt;Compile Include="..\Shared\FakeLcu.cs" /&gt;</c>.
/// </summary>
internal sealed class FakeLcu : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert = SelfSigned();
    private readonly string _puuid;
    private readonly string _nick;
    private readonly List<(int Queue, int Champion, bool Win, DateTimeOffset At)> _games;

    /// Куда стучаться: <c>new LcuHttpClient(fake.Credentials)</c>.
    public LcuCredentials Credentials { get; }

    /// Игры — новейшие первыми, как отдаёт клиент.
    public FakeLcu(string puuid, string nick,
                   IEnumerable<(int Queue, int Champion, bool Win, DateTimeOffset At)> games)
    {
        _puuid = puuid;
        _nick = nick;
        _games = games.OrderByDescending(g => g.At).ToList();
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Credentials = new LcuCredentials("LeagueClient", 0, port, "test", "https");
        _ = Task.Run(Serve);
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        _cert.Dispose();
    }

    private async Task Serve()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Answer(c));
        }
    }

    private async Task Answer(TcpClient c)
    {
        using var _ = c;
        try
        {
            using var ssl = new SslStream(c.GetStream());
            await ssl.AuthenticateAsServerAsync(_cert);
            var path = await ReadPath(ssl);
            var (status, body) = Route(path);
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                     + "Content-Type: application/json\r\n"
                     + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
            await ssl.WriteAsync(bytes);
            await ssl.FlushAsync();
        }
        catch { /* клиент закрыл соединение — его дело */ }
    }

    /// Строка запроса до пустой строки заголовков; тела у GET нет.
    private static async Task<string> ReadPath(Stream s)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (!sb.ToString().Contains("\r\n\r\n"))
        {
            var n = await s.ReadAsync(buf);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        var line = sb.ToString().Split("\r\n")[0].Split(' ');
        return line.Length > 1 ? line[1] : "";
    }

    private (int, string) Route(string path)
    {
        if (path == "/lol-summoner/v1/current-summoner")
            return (200, new JsonObject { ["puuid"] = _puuid, ["gameName"] = _nick }.ToJsonString());

        if (path.StartsWith("/lol-match-history/v1/products/lol/current-summoner/matches"))
        {
            var games = new JsonArray();
            var id = 7_000_000_000L;
            foreach (var g in _games)
                games.Add(new JsonObject
                {
                    ["gameId"] = id--,
                    ["queueId"] = g.Queue,
                    ["gameCreation"] = g.At.ToUnixTimeMilliseconds(),
                    ["gameDuration"] = 1800,
                    ["participants"] = new JsonArray(new JsonObject
                    {
                        ["championId"] = g.Champion,
                        ["stats"] = new JsonObject { ["win"] = g.Win },
                    }),
                    ["participantIdentities"] = new JsonArray(new JsonObject
                    {
                        ["player"] = new JsonObject { ["puuid"] = _puuid },
                    }),
                });
            return (200, new JsonObject { ["games"] = new JsonObject { ["games"] = games } }.ToJsonString());
        }
        return (404, "{}");
    }

    /// Ключ эфемерный, а SChannel на Windows такой сертификат серверу не даёт:
    /// прогон через PFX делает ключ «настоящим».
    private static X509Certificate2 SelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
    }
}
