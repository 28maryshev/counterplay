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
    private readonly string? _puuid;
    private readonly string _nick;
    private readonly List<(int Queue, int Champion, bool Win, DateTimeOffset At)> _games;
    private readonly Dictionary<string, Rank> _ranks;

    /// Ранг одной очереди: ключ клиента (<c>RANKED_SOLO_5x5</c>, <c>RANKED_FLEX_SR</c>);
    /// пустая лига — отборочные не сыграны.
    public sealed record Rank(string Tier, string Division, int Lp, int Wins, int Losses);

    /// Куда стучаться: <c>new LcuHttpClient(fake.Credentials)</c>.
    public LcuCredentials Credentials { get; }

    /// <summary>
    /// Ответы, заданные проверкой: путь → (код, тело). Проверяются первыми и
    /// при любом методе, в обход журнала игр. Нужны тем, кому мало журнала:
    /// чат драфта, спеллы, наборы предметов (ClientActions).
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, (int Status, string Body)> Fixed { get; } = [];

    /// Что спрашивали — «МЕТОД путь», по порядку прихода.
    public System.Collections.Concurrent.ConcurrentQueue<string> Requests { get; } = [];

    /// Подробные игры по номеру (<c>/lol-match-history/v1/games/{id}</c>) —
    /// тело как у клиента; номера нет — 404.
    public Dictionary<long, string> Details { get; } = [];

    /// <summary>
    /// Подробная игра как у клиента: <paramref name="puuid"/> — саппорт на
    /// <paramref name="myChamp"/> в синей команде, враг на его линии —
    /// <paramref name="foe"/>; бот-линии помечены, как их метит клиент
    /// (остальным ролям верить нельзя, их раскладывает доля роли). Для
    /// <see cref="Details"/>. Зовут PersonalFoes и PersonalOff.
    /// </summary>
    public static string LaneGame(string puuid, int myChamp, int foe, bool win, DateTimeOffset at)
    {
        var parts = new JsonArray();
        var ids = new JsonArray();
        (int Champ, string Lane, string Role)[] blue = [(myChamp, "BOTTOM", "SUPPORT"), (222, "BOTTOM", "CARRY"), (86, "TOP", "SOLO"), (64, "JUNGLE", "NONE"), (103, "MIDDLE", "SOLO")];
        (int Champ, string Lane, string Role)[] red = [(foe, "BOTTOM", "SUPPORT"), (51, "BOTTOM", "CARRY"), (122, "TOP", "SOLO"), (254, "JUNGLE", "NONE"), (134, "MIDDLE", "SOLO")];
        for (int i = 0; i < 10; i++)
        {
            var (champ, lane, role) = i < 5 ? blue[i] : red[i - 5];
            parts.Add(new JsonObject
            {
                ["participantId"] = i + 1, ["championId"] = champ, ["teamId"] = i < 5 ? 100 : 200,
                ["stats"] = new JsonObject { ["win"] = (i < 5) == win },
                ["timeline"] = new JsonObject { ["lane"] = lane, ["role"] = role },
            });
            ids.Add(new JsonObject
            {
                ["participantId"] = i + 1,
                ["player"] = new JsonObject { ["puuid"] = i == 0 ? puuid : $"TEST-lane-other-{i}" },
            });
        }
        return new JsonObject
        {
            ["queueId"] = 420, ["gameCreation"] = at.ToUnixTimeMilliseconds(), ["gameDuration"] = 1800,
            ["participants"] = parts, ["participantIdentities"] = ids,
        }.ToJsonString();
    }

    /// Сколько раз спрашивали подробную игру.
    public int DetailAsks => _detailAsks;
    private int _detailAsks;

    /// Игры — новейшие первыми, как отдаёт клиент. puuid null — клиент открыт,
    /// но в аккаунт не вошли: «кто в клиенте» отвечает 404. Без рангов —
    /// ранги тоже 404.
    public FakeLcu(string? puuid, string nick,
                   IEnumerable<(int Queue, int Champion, bool Win, DateTimeOffset At)> games,
                   IReadOnlyDictionary<string, Rank>? ranks = null)
    {
        _puuid = puuid;
        _nick = nick;
        _games = games.OrderByDescending(g => g.At).ToList();
        _ranks = ranks?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
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
            var (method, path) = await ReadRequest(ssl);
            Requests.Enqueue($"{method} {path}");
            var (status, body) = Fixed.TryGetValue(path, out var f) ? f : Route(path);
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = $"HTTP/1.1 {status} {(status is >= 200 and < 300 ? "OK" : "Not Found")}\r\n"
                     + "Content-Type: application/json\r\n"
                     + $"Content-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(head));
            await ssl.WriteAsync(bytes);
            await ssl.FlushAsync();
        }
        catch { /* клиент закрыл соединение — его дело */ }
    }

    /// <summary>
    /// Метод и путь из строки запроса. Тело (у PATCH, POST, PUT) дочитывается
    /// по Content-Length: закрыть соединение с непрочитанным телом — значит
    /// оборвать его, и клиент может не получить ответ. У GET тела нет.
    /// </summary>
    private static async Task<(string Method, string Path)> ReadRequest(Stream s)
    {
        var buf = new byte[8192];
        var data = new List<byte>();
        int headEnd;
        while ((headEnd = HeadEnd(data)) < 0)
        {
            var n = await s.ReadAsync(buf);
            if (n == 0) break;
            data.AddRange(buf.AsSpan(0, n).ToArray());
        }
        var head = Encoding.ASCII.GetString(data.ToArray(), 0, headEnd < 0 ? data.Count : headEnd);
        var lines = head.Split("\r\n");
        var length = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.TryParse(p[1].Trim(), out var v) ? v : 0)
            .FirstOrDefault();
        var have = headEnd < 0 ? 0 : data.Count - headEnd - 4;
        while (have < length)
        {
            var n = await s.ReadAsync(buf);
            if (n == 0) break;
            have += n;
        }
        var line = lines[0].Split(' ');
        return (line[0], line.Length > 1 ? line[1] : "");
    }

    private static int HeadEnd(List<byte> d)
    {
        for (int i = 0; i + 3 < d.Count; i++)
            if (d[i] == '\r' && d[i + 1] == '\n' && d[i + 2] == '\r' && d[i + 3] == '\n') return i;
        return -1;
    }

    private (int, string) Route(string path)
    {
        if (_puuid is null) return (404, "{}");

        if (path == "/lol-summoner/v1/current-summoner")
            return (200, new JsonObject { ["puuid"] = _puuid, ["gameName"] = _nick }.ToJsonString());

        if (path.StartsWith("/lol-ranked/v1/ranked-stats/") && _ranks.Count > 0)
        {
            var map = new JsonObject();
            foreach (var (key, r) in _ranks)
                map[key] = new JsonObject
                {
                    ["tier"] = r.Tier, ["division"] = r.Division, ["leaguePoints"] = r.Lp,
                    ["wins"] = r.Wins, ["losses"] = r.Losses,
                };
            return (200, new JsonObject { ["queueMap"] = map }.ToJsonString());
        }

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

        if (path.StartsWith("/lol-match-history/v1/games/"))
        {
            Interlocked.Increment(ref _detailAsks);
            return long.TryParse(path["/lol-match-history/v1/games/".Length..], out var gid)
                   && Details.TryGetValue(gid, out var detail) ? (200, detail) : (404, "{}");
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
