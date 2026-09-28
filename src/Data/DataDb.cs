using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Counterplay;

/// <summary>
/// Централизованная база данных. Источник — отдельный «дата-релиз» на GitHub
/// (его обновляют раз в патч). При каждом запуске программа сверяет версию и,
/// если на сервере новее, тихо подкачивает свежую base в папку пользователя.
/// Так данные мета держатся актуальными без действий пользователя.
/// </summary>
public static class DataDb
{
    /// <summary>
    /// Подменить папку базы. Нужно ПРОВЕРКАМ: они гоняют подмену файла на
    /// боевых именах, и делать это в профиле пользователя нельзя — там лежит
    /// его настоящая база, да ещё и открытая запущенной программой.
    /// В работе всегда null.
    /// </summary>
    public static string? DirOverride { get; set; }

    // Постоянное место БД — вне каталога установки, переживает обновления приложения.
    private static string Dir => DirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
    public static string LocalPath        => Path.Combine(Dir, "data.db");
    private static string LocalVersionPath => Path.Combine(Dir, "data-version.txt");

    // Скачанная фоном база ждёт здесь, пока рабочую не станет можно подменить.
    // Рядом — её версия: писать её в боевой data-version.txt раньше подмены
    // нельзя, иначе после перезапуска программа сочтёт старый файл свежим.
    private static string PendingPath        => LocalPath + ".new";
    private static string PendingVersionPath => LocalPath + ".new.ver";

    // Откуда качаем базу. Первым — своё хранилище на Cloudflare (R2), вторым —
    // дата-релиз на GitHub.
    //
    // Порядок такой не из вкусовщины, а по замерам с обычного домашнего канала:
    // GitHub отдавал 0.11–0.12 МБ/с, сеть Cloudflare — 5–6 МБ/с, Data Dragon —
    // 9 МБ/с. Разница в полсотни раз, и канал тут ни при чём. При 51 КБ/с кусок
    // в 16 МБ не успевал за отведённые пять минут, и загрузка начиналась заново.
    //
    // GitHub оставляем запасным путём: по нему качают версии программы,
    // выпущенные до переезда, и он же выручит, если R2 будет недоступен.
    private const string R2Url      = "https://data.counterplays.com";
    private const string GhUrl      = "https://github.com/28maryshev/counterplay/releases/download/data";

    /// Источники по порядку предпочтения. Пустой R2Url — значит ещё не завели.
    private static IEnumerable<string> Sources()
    {
        if (R2Url.Length > 0) yield return R2Url;
        yield return GhUrl;
    }

    private static string VersionUrlOn(string baseUrl) => baseUrl + "/data-version.json";
    // Побакетная база (data-<bucket>.db, ~50 МБ) — качаем только свой ранг. Если
    // бакет неизвестен — общая тонкая data.db (все бакеты, ~177 МБ).
    private static string DataUrlOn(string baseUrl, string? bucket) =>
        string.IsNullOrEmpty(bucket) ? baseUrl + "/data.db" : $"{baseUrl}/data-{bucket}.db";

    // Локальные dev-базы (рядом с проектом). Используются как есть, БЕЗ скачивания,
    // ТОЛЬКО в dev-режиме (см. DevDbEnabled). По умолчанию их игнорируем — база
    // качается из облака, как у всех пользователей.
    public static readonly string[] DevCandidates =
    [
        "data.db",
        Path.Combine("pipeline", "data.db"),
        Path.Combine(AppContext.BaseDirectory, "data.db"),
        Path.Combine(AppContext.BaseDirectory, "pipeline", "data.db"),
        @"C:\Counterplay\pipeline\data.db",
    ];

    /// Использовать локальную dev-базу вместо облачной? По умолчанию НЕТ —
    /// приложение качает базу из релиза, как у обычных пользователей. Включить
    /// dev-режим (для отладки пайплайна на своей базе): переменная окружения
    /// COUNTERPLAY_DEV_DB=1.
    public static bool DevDbEnabled =>
        Environment.GetEnvironmentVariable("COUNTERPLAY_DEV_DB") is "1" or "true";

    // Какой бакет сейчас на диске: "all" (общая), "silver".. или null (базы нет).
    // Хранится префиксом в data-version.txt ("bucket:version").
    public static string? CurrentBucket
    {
        get
        {
            try
            {
                if (!File.Exists(LocalVersionPath)) return null;
                var tag = File.ReadAllText(LocalVersionPath).Trim();
                var i = tag.IndexOf(':');
                return i > 0 ? tag[..i] : "all"; // старый формат без бакета = общая
            }
            catch { return null; }
        }
    }

    // Таблицы движка + число последних патчей, которые держим локально.
    private static readonly string[] EngineTables =
        ["base_wr", "matchup", "synergy", "botlane_matchup", "champion_bans"];
    private const int SelfCleanKeep = 5;

    /// Самоочистка локальной базы: держим только последние SelfCleanKeep патчей
    /// (движку нужно 3). Обычно no-op — скачанная база уже тонкая, — но страхует
    /// от старой раздутой базы, оставшейся от прежней версии программы.
    public static void SelfClean()
    {
        try
        {
            if (!File.Exists(LocalPath) || !RecommendationEngine.HasData(LocalPath)) return;
            using var db = new SqliteConnection($"Data Source={LocalPath}");
            db.Open();

            var patches = new List<string>();
            using (var c = db.CreateCommand())
            {
                c.CommandText = @"SELECT DISTINCT patch FROM base_wr
                    ORDER BY CAST(SUBSTR(patch,1,INSTR(patch,'.')-1) AS INTEGER) DESC,
                             CAST(SUBSTR(patch,INSTR(patch,'.')+1)   AS INTEGER) DESC
                    LIMIT @k";
                c.Parameters.AddWithValue("@k", SelfCleanKeep);
                using var rd = c.ExecuteReader();
                while (rd.Read()) patches.Add(rd.GetString(0));
            }
            if (patches.Count < SelfCleanKeep) return; // старых патчей нет — чистить нечего

            var placeholders = string.Join(",", patches.Select((_, i) => $"@p{i}"));
            long removed = 0;
            foreach (var t in EngineTables)
            {
                using var del = db.CreateCommand();
                del.CommandText = $"DELETE FROM {t} WHERE patch NOT IN ({placeholders})";
                for (int i = 0; i < patches.Count; i++)
                    del.Parameters.AddWithValue($"@p{i}", patches[i]);
                try { removed += del.ExecuteNonQuery(); } catch { /* таблицы может не быть */ }
            }
            if (removed > 0)
                using (var vac = db.CreateCommand()) { vac.CommandText = "VACUUM"; vac.ExecuteNonQuery(); }
        }
        catch { /* очистка необязательна — не мешаем запуску */ }
    }

    public static string FormatSpeed(double bytesPerSec) =>
        bytesPerSec >= 1_000_000 ? $"{bytesPerSec / 1_048_576.0:0.0} МБ/с"
        : bytesPerSec > 0        ? $"{bytesPerSec / 1024.0:0} КБ/с"
        : "";

    /// <summary>
    /// Брать локальную базу как есть и ничего не качать.
    ///
    /// Ставится для песочницы. Она работает на той же папке, что и боевой
    /// экземпляр, и когда оба запущены, подменить файл нельзя — базу держит
    /// открытой основной. Песочница честно качала, распаковывала и падала на
    /// подмене, а следом уходила качать общую базу на 198 МБ. И так каждый
    /// запуск. Данные ей нужны любые — свежесть тут ни на что не влияет.
    /// </summary>
    public static bool ReuseLocal { get; set; }

    /// Есть ли годная к работе база на диске. По этому признаку решается,
    /// обновляться фоном (есть на чём работать) или на экране загрузки.
    public static bool HaveUsableDb() =>
        File.Exists(LocalPath) && new FileInfo(LocalPath).Length > 0
        && RecommendationEngine.HasData(LocalPath);

    /// Скачана ли фоном база, которая ждёт подмены.
    public static bool SwapReady
    {
        get
        {
            try { return File.Exists(PendingPath) && new FileInfo(PendingPath).Length > 0; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Подменить рабочую базу скачанной фоном.
    ///
    /// Вызывать в момент, когда базу никто не держит открытой: SQLite в режиме
    /// WAL держит файл и на чтение, и подмена под открытым соединением на
    /// Windows просто не выйдет. Не вышла — файл остаётся ждать: следующий
    /// тихий момент или следующий запуск.
    /// </summary>
    public static bool ApplySwap()
    {
        if (!SwapReady) return false;
        try
        {
            // Одного Dispose движку мало: Microsoft.Data.Sqlite держит закрытые
            // соединения в пуле, и файл остаётся открытым. Замерено — подмена
            // после Dispose падает с «отказано в доступе», а после очистки пула
            // проходит. Без этой строки скачанная фоном база не применилась бы
            // никогда: каждый запуск качал бы её заново.
            SqliteConnection.ClearAllPools();
            File.Move(PendingPath, LocalPath, overwrite: true);
            if (File.Exists(PendingVersionPath))
                File.Move(PendingVersionPath, LocalVersionPath, overwrite: true);
            Log.Write("новая база применена");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"подмена базы не вышла ({ex.Message}) — оставляю ждать");
            return false;
        }
    }

    /// <summary>
    /// Обновить базу, не мешая работать.
    ///
    /// От <see cref="EnsureAsync"/> отличается одним: рабочий data.db не
    /// трогается. Скачанное ложится рядом, а подменяется потом —
    /// <see cref="ApplySwap"/>. Пока идёт закачка, программа считает драфт на
    /// прежней базе, и внизу сайдбара видно, сколько осталось.
    ///
    /// Вернёт true, если скачанное готово к подмене.
    /// </summary>
    public static async Task<bool> UpdateInBackgroundAsync(
        string? bucket, Action<string, double>? progress, CancellationToken ct)
    {
        if (DevDbEnabled && DevCandidates.Any(p => File.Exists(p) && RecommendationEngine.HasData(p)))
            return false;
        if (ReuseLocal) return false;          // песочница живёт на общей базе

        try
        {
            var (source, remoteVer) = await FetchVersionAsync(bucket, ct);
            if (remoteVer is null) return false;          // сервер молчит — не время
            var wantedTag = $"{bucket ?? "all"}:{remoteVer}";
            var localTag  = File.Exists(LocalVersionPath) ? File.ReadAllText(LocalVersionPath).Trim() : null;
            if (wantedTag == localTag) return false;      // уже свежая

            // Уже скачали ровно эту версию и ждём тихой минуты — второй раз не качаем.
            if (SwapReady && File.Exists(PendingVersionPath)
                && File.ReadAllText(PendingVersionPath).Trim() == wantedTag)
                return true;

            Log.Write($"база обновляется фоном: {localTag ?? "—"} → {wantedTag}");
            var label = Loc.T("status.updatingDb");
            progress?.Invoke(label, 0);

            var url = DataUrlOn(source, bucket);
            try
            {
                await DownloadAsync(url + ".gz", label, progress, ct, gzipped: true, dest: PendingPath);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                          && ex is not OperationCanceledException)
            {
                Log.Write($"сжатая база не далась ({ex.Message}) — качаю обычную");
                await DownloadAsync(url, label, progress, ct, dest: PendingPath);
            }

            await File.WriteAllTextAsync(PendingVersionPath, wantedTag, ct);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            // Сети нет, места нет, сервер прилёг — работаем на прежней базе.
            Log.Write($"фоновое обновление базы не удалось: {ex.Message}");
            return false;
        }
    }

    /// Гарантирует наличие актуальной базы. Для dev — берёт локальную как есть.
    /// Для установленной версии — сверяет версию с сервером и подкачивает свежую.
    /// progress: (текст со статусом+скоростью, доля 0..1).
    /// bucket — эло игрока (silver/gold/emerald/master) → качаем маленькую базу
    /// только его ранга; null — общая тонкая база (все бакеты). Версию сверяем по
    /// «bucket:version», поэтому смена ранга сама триггерит подкачку нужной базы.
    public static async Task EnsureAsync(string? bucket, Action<string, double>? progress, CancellationToken ct)
    {
        // 1. Dev-режим (по opt-in): локальная база рядом с проектом — не качаем.
        if (DevDbEnabled && DevCandidates.Any(p => File.Exists(p) && RecommendationEngine.HasData(p)))
            return;

        // 1a. Песочница: база уже лежит — работаем на ней. См. ReuseLocal.
        if (ReuseLocal && HaveUsableDb())
        {
            Log.Write("песочница: беру готовую базу, не качаю");
            return;
        }

        // 2. Установленная версия — версионная подкачка.
        try
        {
            Directory.CreateDirectory(Dir);

            // Скачанная фоном база ждёт подмены с прошлого запуска — сейчас её
            // никто не держит, самое время. Дальше сверка версий увидит уже
            // новый тег и никуда не пойдёт.
            ApplySwap();

            // Версию спрашиваем у того же источника, с которого потом качаем:
            // выложиться они могут в разное время, и брать номер у одного, а
            // файл у другого — верный способ записать чужую версию себе.
            var (source, remoteVer) = await FetchVersionAsync(bucket, ct);
            var wantedTag = remoteVer is null ? null : $"{bucket ?? "all"}:{remoteVer}";
            var localTag  = File.Exists(LocalVersionPath) ? File.ReadAllText(LocalVersionPath).Trim() : null;
            var haveDb    = File.Exists(LocalPath) && new FileInfo(LocalPath).Length > 0;

            // База есть и совпадают бакет+версия (или сервер недоступен) — не качаем.
            if (haveDb && (wantedTag is null || wantedTag == localTag)) return;

            var label = haveDb ? Loc.T("status.updatingDb") : Loc.T("status.downloadingDb");
            progress?.Invoke(label, 0);

            // Сперва сжатая база: она втрое с лишним меньше (112 МБ против 32 у
            // изумруда). Распаковка занимает секунды и на фоне скачивания
            // незаметна.
            var url = DataUrlOn(source, bucket);
            try
            {
                await DownloadAsync(url + ".gz", label, progress, ct, gzipped: true);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                          && ex is not OperationCanceledException
                                          && ex is not UnauthorizedAccessException)
            {
                // Сжатой нет (старый дата-релиз) или она не докачалась — берём
                // обычную. Так обновление программы не зависит от того, успел ли
                // пайплайн выложить новый файл.
                //
                // Ошибку ДОСТУПА сюда не пускаем. Она означает, что скачалось всё
                // хорошо, а подменить файл не вышло: базу держит открытой другой
                // экземпляр программы. Качать вместо неё обычную — это те же
                // грабли, только на 198 МБ вместо 38, и с тем же концом.
                Log.Write($"сжатая база не далась ({ex.Message}) — качаю обычную");
                await DownloadAsync(url, label, progress, ct);
            }
            catch (UnauthorizedAccessException)
            {
                // Файл занят другим экземпляром. Остаёмся на той базе, что есть.
                Log.Write("базу держит другой экземпляр — остаюсь на прежней");
                return;
            }

            if (wantedTag is not null)
                await File.WriteAllTextAsync(LocalVersionPath, wantedTag, ct);
        }
        catch
        {
            // Офлайн / нет дата-релиза — работаем на том, что уже скачано (если есть).
        }
    }

    // Версия целевой базы: для бакета — из manifest.buckets[bucket].version,
    // иначе общая version. Если побакетной секции нет (старый релиз) — вернём
    // общую и качнём общую data.db (обратная совместимость).
    private static async Task<(string Source, string? Version)> FetchVersionAsync(
        string? bucket, CancellationToken ct)
    {
        string? firstFailure = null;
        foreach (var src in Sources())
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var json = (await http.GetStringAsync(VersionUrlOn(src), ct)).TrimStart('﻿');
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? ver = null;
                if (!string.IsNullOrEmpty(bucket)
                    && root.TryGetProperty("buckets", out var bs)
                    && bs.TryGetProperty(bucket, out var b)
                    && b.TryGetProperty("version", out var bv))
                    ver = bv.GetString();
                else if (root.TryGetProperty("version", out var v))
                    ver = v.GetString();

                if (firstFailure is not null)
                    Log.Write($"данные беру с запасного источника ({firstFailure})");
                return (src, ver);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                firstFailure ??= ex.Message;
            }
        }
        // Никто не ответил — качать не с чего, останемся на том, что есть.
        return (GhUrl, null);
    }

    // База качается КУСКАМИ по 16 МБ через Range-запросы, а не одним потоком.
    //
    // Одно длинное соединение к релизным ассетам GitHub режется примерно после
    // первых 15–20 МБ: замер на 120 МБ дал 1,5 МБ/с, те же 120 МБ кусками —
    // 19 МБ/с, разница в тринадцать раз. Причина снаружи и не в нашей власти
    // (короткий запрос с любого смещения летит на полной скорости), поэтому
    // просто не держим один поток открытым дольше нужного.
    //
    // Побочная выгода: обрыв связи больше не отправляет закачку в начало —
    // готовые куски остаются на диске.
    private const int ChunkSize = 16 * 1024 * 1024;

    /// <param name="dest">Куда положить готовый файл. По умолчанию рабочая
    /// база; фоновое обновление кладёт рядом, чтобы не трогать работающую.</param>
    private static async Task DownloadAsync(string url, string label, Action<string, double>? progress,
                                            CancellationToken ct, bool gzipped = false,
                                            string? dest = null)
    {
        dest ??= LocalPath;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Размер и поддержку Range узнаём одним лёгким запросом.
        long total = 0;
        bool ranges = false;
        try
        {
            using var head = await http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, url), ct);
            head.EnsureSuccessStatusCode();
            total  = head.Content.Headers.ContentLength ?? 0;
            ranges = head.Headers.AcceptRanges.Contains("bytes");
        }
        catch { /* не ответил на HEAD — качаем одним потоком, как раньше */ }

        var tmp = dest + ".tmp";
        var mark = tmp + ".part";     // что именно лежит в .tmp и сколько ждали всего

        // Докачка. Оборванная загрузка раньше начиналась с нуля: при 51 КБ/с
        // кусок в 16 МБ не укладывался в отведённое время, и так по кругу.
        //
        // Хвост берём только если он от ТОГО ЖЕ файла и той же длины — иначе
        // недокачанная другая база молча склеилась бы с новой. Отметка рядом и
        // хранит эти две вещи.
        long resume = 0;
        if (ranges && total > 0 && File.Exists(tmp) && File.Exists(mark))
        {
            try
            {
                var saved = (await File.ReadAllTextAsync(mark, ct)).Split('\n');
                var have  = new FileInfo(tmp).Length;
                if (saved.Length == 2 && saved[0] == url
                    && long.TryParse(saved[1], out var savedTotal) && savedTotal == total
                    && have > 0 && have < total)
                {
                    resume = have;
                    Log.Write($"докачиваю с {resume / 1048576.0:0} МБ из {total / 1048576.0:0}");
                }
            }
            catch { /* отметка битая — качаем заново */ }
        }
        if (resume == 0)
        {
            try { File.Delete(tmp); } catch { /* нет файла — и хорошо */ }
            if (ranges && total > 0)
                try { await File.WriteAllTextAsync(mark, $"{url}\n{total}", ct); } catch { }
        }

        var sw  = Stopwatch.StartNew();
        long done = resume;
        long lastBytes = 0;
        var  lastT = TimeSpan.Zero;

        void Report(long read, bool force = false)
        {
            var now = sw.Elapsed;
            if (!force && (now - lastT).TotalSeconds < 0.2) return;
            var dt   = (now - lastT).TotalSeconds;
            var bps  = dt > 0 ? (read - lastBytes) / dt : 0;
            var frac = total > 0 ? (double)read / total : 0;
            lastBytes = read; lastT = now;
            var pctTxt = total > 0 ? $" {frac * 100:0}%" : "";
            progress?.Invoke($"{label}{pctTxt} · {FormatSpeed(bps)}", frac);
        }

        await using (var dst = resume > 0 ? new FileStream(tmp, FileMode.Append) : File.Create(tmp))
        {
            var buf = new byte[81920];

            if (!ranges || total <= ChunkSize)
            {
                // Сервер не умеет куски или файл и так маленький — один поток.
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                if (total == 0) total = resp.Content.Headers.ContentLength ?? 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    Report(done);
                }
            }
            else
            {
                for (long off = resume; off < total; off += ChunkSize)
                {
                    var last = Math.Min(off + ChunkSize, total) - 1;
                    var req  = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(off, last);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        Report(done);
                    }
                }
            }
        }

        Report(done, force: true);
        try { File.Delete(mark); } catch { /* уже нет — и хорошо */ }
        Log.Write($"база скачана: {done / 1048576.0:0} МБ за {sw.Elapsed.TotalSeconds:0.0} с " +
                  $"({FormatSpeed(done / Math.Max(0.001, sw.Elapsed.TotalSeconds))}, " +
                  $"{(ranges && total > ChunkSize ? "кусками" : "одним потоком")}" +
                  $"{(gzipped ? ", сжатая" : "")})");

        if (gzipped)
        {
            // Распаковываем в СОСЕДНИЙ файл и только потом подменяем боевой: если
            // архив побился по дороге, рабочая база останется прежней.
            var raw = dest + ".raw";
            var unz = Stopwatch.StartNew();
            await using (var src = File.OpenRead(tmp))
            await using (var gz  = new GZipStream(src, CompressionMode.Decompress))
            await using (var dst = File.Create(raw))
                await gz.CopyToAsync(dst, ct);
            File.Delete(tmp);
            Log.Write($"база распакована: {new FileInfo(raw).Length / 1048576.0:0} МБ "
                      + $"за {unz.Elapsed.TotalSeconds:0.0} с");
            tmp = raw;
        }

        File.Move(tmp, dest, overwrite: true);
    }
}
