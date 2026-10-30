using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Counterplay;

/// <summary>Автопоиск установленного клиента LoL (lockfile) на любом ПК.</summary>
public static class LcuFinder
{
    private static readonly string[] ProcNames = ["LeagueClientUx", "LeagueClient"];

    /// Запущен ли клиент LoL прямо сейчас.
    public static bool IsClientRunning()
    {
        foreach (var name in ProcNames)
        {
            var procs = Process.GetProcessesByName(name);
            try { if (procs.Length > 0) return true; }
            finally { foreach (var p in procs) p.Dispose(); }
        }
        return false;
    }

    /// Имя процесса — один из процессов клиента LoL.
    public static bool IsClientProcessName(string name) =>
        ProcNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// Находит путь к lockfile: сначала по запущенному процессу клиента
    /// (его папка = каталог установки), затем по типичным путям. null — не найден.
    ///
    /// ВАЖНО: без запущенного клиента lockfile не годится — Riot не всегда удаляет
    /// его при выходе, и старый файл содержит протухшие порт/пароль. Раньше при
    /// автозапуске (клиента ещё нет) читался именно такой файл, и программа
    /// вечно стучалась на мёртвый порт, показывая «LCU is starting…».
    public static string? FindLockfilePath()
    {
        if (!IsClientRunning()) return null;

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
    /// (по процессу/типичным путям) на каждой итерации, т.е. работает на любом ПК,
    /// а без годного lockfile — командная строка клиента.
    public static async Task<LcuCredentials> WaitForAsync(string? explicitPath, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var path = explicitPath ?? LcuFinder.FindLockfilePath();
            if (path != null && File.Exists(path))
            {
                try
                {
                    var creds = Read(path);
                    if (IsLive(creds)) return creds;
                    // Файл от прошлого запуска клиента. Компьютер выключился
                    // посреди игры, файл остался, а только что запущенный клиент
                    // ещё не успел записать новый. Раньше такой файл принимался:
                    // программа полминуты стучалась на мёртвый порт, и окно не
                    // выходило из трея. Ждём новый, а пока — командная строка.
                }
                catch (IOException) { /* файл ещё дописывается — подождём */ }
                // Пустой или недописанный файл. Раньше исключение выходило из
                // цикла, и программа больше не подключалась: у китайского
                // клиента (WeGame) lockfile пустой всегда.
                catch (FormatException) { }
            }
            if (explicitPath == null && LcuCommandLine.TryRead() is { } fromCmd) return fromCmd;
            await Task.Delay(1000, ct);
        }
        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    /// Жив ли процесс, записавший lockfile: номер из файла принадлежит
    /// процессу с тем же именем (или одному из процессов клиента). После
    /// перезагрузки номер старого клиента может достаться чужой программе —
    /// поэтому сверяется и имя, а не только то, что процесс есть.
    public static bool IsLive(LcuCredentials c)
    {
        try
        {
            using var p = Process.GetProcessById(c.Pid);
            var name = p.ProcessName;
            return string.Equals(name, c.Name, StringComparison.OrdinalIgnoreCase)
                || LcuFinder.IsClientProcessName(name);
        }
        catch { return false; }   // процесса нет или он только что вышел
    }
}

/// <summary>HTTP-клиент к LCU: Basic-авторизация + доверие самоподписанному серту.</summary>
public sealed class LcuHttpClient : IDisposable
{
    private readonly HttpClient _http;

    /// <param name="timeout">Срок ответа; не задан — 20 с. Короче — только проверкам.</param>
    public LcuHttpClient(LcuCredentials creds, TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            // LCU слушает на localhost с самоподписанным сертификатом — принимаем его.
            // Более строгий вариант — пиннинг корневого riotgames.pem (Riot его публикует).
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        // Таймаут: по умолчанию у HttpClient сто секунд, а здесь разговор идёт с
        // соседним процессом на localhost — столько ждать не от чего. Цена
        // длинного ожидания видна на кнопке выбора: пока запрос висит, нажатие
        // считается «уже идёт» и кнопка не отвечает, то есть зависший вызов
        // глушил бы её почти на весь драфт.
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(creds.HttpBase),
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };

        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{creds.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }

    /// GET без бросков на 4xx/5xx: возвращает (код, тело).
    public Task<(int Status, string Body)> GetAsync(string path, CancellationToken ct) =>
        SendAsync(c => _http.GetAsync(path, c), ct);

    // Запись в клиент (импорт рун, наборы предметов). Так же без бросков на 4xx/5xx.

    public async Task<(int Status, string Body)> PostAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(c => _http.PostAsync(path, content, c), ct);
    }

    public async Task<(int Status, string Body)> PutAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await SendAsync(c => _http.PutAsync(path, content, c), ct);
    }

    public Task<(int Status, string Body)> DeleteAsync(string path, CancellationToken ct) =>
        SendAsync(c => _http.DeleteAsync(path, c), ct);

    public async Task<(int Status, string Body)> PatchAsync(string path, string json, CancellationToken ct)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Patch, path) { Content = content };
        return await SendAsync(c => _http.SendAsync(req, c), ct);
    }

    /// <summary>
    /// Один запрос: ответ — (код, тело); клиент не ответил в срок —
    /// HttpRequestException, как при оборванном соединении.
    ///
    /// Сам HttpClient о таймауте сообщает TaskCanceledException — тем же, чем
    /// об отмене. А отмену цикл подключения понимает как выход из программы:
    /// ошибки связи он пережидает, отмену пропускает наверх, и там она
    /// закрывала программу молча. Клиент, который принял соединение и молчит, —
    /// неудача связи, и выглядеть она должна так же. Отмена своим токеном
    /// остаётся отменой: ею программа и закрывается.
    /// </summary>
    private async Task<(int Status, string Body)> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        try
        {
            using var resp = await send(ct);
            return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"клиент LoL не ответил за {_http.Timeout.TotalSeconds:0} с", ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
