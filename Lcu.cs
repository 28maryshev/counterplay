using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Counterplay;

/// <summary>Автопоиск установленного клиента LoL (lockfile) на любом ПК.</summary>
public static class LcuFinder
{
    private static readonly string[] ProcNames = ["LeagueClientUx", "LeagueClient"];

    /// Находит путь к lockfile: сначала по запущенному процессу клиента
    /// (его папка = каталог установки), затем по типичным путям. null — не найден.
    public static string? FindLockfilePath()
    {
        // 1. По процессу клиента — работает при любой папке установки.
        foreach (var name in ProcNames)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = proc.MainModule?.FileName;
                    var dir = string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
                    if (dir != null)
                    {
                        var lf = Path.Combine(dir, "lockfile");
                        if (File.Exists(lf)) return lf;
                    }
                }
                catch { /* доступ к модулю может быть запрещён — идём дальше */ }
                finally { proc.Dispose(); }
            }
        }

        // 2. Типичные места установки на всех дисках.
        foreach (var p in CommonPaths())
            if (File.Exists(p)) return p;

        return null;
    }

    private static IEnumerable<string> CommonPaths()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            if (!d.IsReady) continue;
            var r = d.RootDirectory.FullName;
            yield return Path.Combine(r, "Riot Games", "League of Legends", "lockfile");
            yield return Path.Combine(r, "Games", "Riot Games", "League of Legends", "lockfile");
            yield return Path.Combine(r, "Program Files", "Riot Games", "League of Legends", "lockfile");
            yield return Path.Combine(r, "Program Files (x86)", "Riot Games", "League of Legends", "lockfile");
        }
    }
}

/// <summary>Учётные данные LCU из lockfile (name:pid:port:password:protocol).</summary>
public sealed record LcuCredentials(string Name, int Pid, int Port, string Password, string Protocol)
{
    private string Token => Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{Password}"));

    /// Заголовок HTTP Basic: base64("riot:&lt;password&gt;").
    public string AuthHeader => "Basic " + Token;

    public string HttpBase => $"https://127.0.0.1:{Port}";
    public Uri    WsUri    => new($"wss://127.0.0.1:{Port}/");
}

/// <summary>Поиск и парсинг lockfile клиента LoL.</summary>
public static class LockfileReader
{
    /// Путь по умолчанию (твоя установка). Можно переопределить аргументом командной строки.
    public const string DefaultPath = @"D:\Games\Riot Games\League of Legends\lockfile";

    public static LcuCredentials Read(string path)
    {
        // Клиент держит lockfile открытым → читаем с FileShare.ReadWrite.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var raw = sr.ReadToEnd().Trim();

        var p = raw.Split(':');
        if (p.Length != 5)
            throw new FormatException($"Неожиданный формат lockfile: '{raw}'");

        return new LcuCredentials(p[0], int.Parse(p[1]), int.Parse(p[2]), p[3], p[4]);
    }

    /// Ждёт появления lockfile. Если explicitPath не задан — автопоиск клиента
    /// (по процессу/типичным путям) на каждой итерации, т.е. работает на любом ПК.
    public static async Task<LcuCredentials> WaitForAsync(string? explicitPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var path = explicitPath ?? LcuFinder.FindLockfilePath();
            if (path != null && File.Exists(path))
            {
                try { return Read(path); }
                catch (IOException) { /* файл ещё дописывается — подождём */ }
            }
            await Task.Delay(1000, ct);
        }
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }
}

/// <summary>HTTP-клиент к LCU: Basic-авторизация + доверие самоподписанному серту.</summary>
public sealed class LcuHttpClient : IDisposable
{
    private readonly HttpClient _http;

    public LcuHttpClient(LcuCredentials creds)
    {
        var handler = new HttpClientHandler
        {
            // LCU слушает на localhost с самоподписанным сертификатом — принимаем его.
            // Более строгий вариант — пиннинг корневого riotgames.pem (Riot его публикует).
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri(creds.HttpBase) };

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{creds.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// GET без бросков на 4xx/5xx: возвращает (код, тело).
    public async Task<(int Status, string Body)> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(path, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ((int)resp.StatusCode, body);
    }

    public void Dispose() => _http.Dispose();
}
