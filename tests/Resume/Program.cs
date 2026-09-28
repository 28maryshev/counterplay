using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Counterplay.Tests;

/// <summary>
/// Докачка с места обрыва.
///
/// Раньше оборванная загрузка начиналась с нуля: при 51 КБ/с кусок в 16 МБ не
/// укладывался в отведённое время, и так по кругу. Теперь недокачанный хвост
/// сохраняется, а следующая попытка берёт файл с того места, где оборвалось.
///
/// Место тихое и опасное: склей не тот хвост — и база молча окажется битой,
/// причём выяснится это далеко от причины. Поэтому проверяем не «не упало», а
/// побайтовое совпадение с целым файлом.
///
/// Работаем на настоящей раздаче (R2) и на небольшом файле — data-version.json
/// слишком мал для Range, поэтому берём серебряную базу и качаем её началом.
/// </summary>
internal static class Program
{
    private static int _fails;
    private const string Url = "https://data.counterplays.com/data-silver.db.gz";
    private const int Part = 3 * 1024 * 1024;    // сколько «успели» до обрыва

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        var dir = Path.Combine(Path.GetTempPath(), "cp-resume-test");
        Directory.CreateDirectory(dir);
        try
        {
            await Run(dir);
        }
        catch (HttpRequestException e)
        {
            Console.WriteLine($"раздача недоступна ({e.Message}) — проверять нечего");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: докачка собирает файл верно"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static async Task Run(string dir)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Сколько весит файл и умеет ли раздача куски.
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));
        head.EnsureSuccessStatusCode();
        var total = head.Content.Headers.ContentLength ?? 0;
        var ranges = head.Headers.AcceptRanges.Contains("bytes");
        Console.WriteLine($"файл {total / 1048576.0:0.0} МБ, куски поддерживаются: {(ranges ? "да" : "нет")}");
        Check("раздача умеет отдавать куски", ranges, ranges ? "да" : "нет");
        if (!ranges || total <= Part) return;

        // Эталон: качаем целиком.
        var whole = await http.GetByteArrayAsync(Url);
        Check("эталон скачан целиком", whole.LongLength == total, $"{whole.LongLength} из {total}");

        // 1) Обрыв: сохраняем только начало, как оборванная загрузка.
        var tmp = Path.Combine(dir, "data.db.tmp");
        await File.WriteAllBytesAsync(tmp, whole[..Part]);
        Console.WriteLine($"оборвали на {Part / 1048576.0:0.0} МБ");

        // 2) Докачка ровно тем же способом, что и в программе: Range с хвоста.
        var req = new HttpRequestMessage(HttpMethod.Get, Url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(Part, total - 1);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Check("раздача отдала запрошенный хвост",
              resp.StatusCode == System.Net.HttpStatusCode.PartialContent,
              resp.StatusCode.ToString());

        await using (var dst = new FileStream(tmp, FileMode.Append))
            await resp.Content.CopyToAsync(dst);

        // 3) Итог обязан совпасть с целым файлом байт в байт.
        var got = await File.ReadAllBytesAsync(tmp);
        Check("длина сошлась", got.LongLength == total, $"{got.LongLength} против {total}");
        Check("содержимое совпало с целым файлом",
              Convert.ToHexString(SHA256.HashData(got)) == Convert.ToHexString(SHA256.HashData(whole)),
              Convert.ToHexString(SHA256.HashData(got))[..16]);

        // 4) И собранный файл должен быть годным архивом, а не мусором.
        try
        {
            await using var src = new MemoryStream(got);
            await using var gz = new System.IO.Compression.GZipStream(
                src, System.IO.Compression.CompressionMode.Decompress);
            await using var outp = new MemoryStream();
            await gz.CopyToAsync(outp);
            var head16 = Encoding.ASCII.GetString(outp.ToArray()[..15]);
            Check("распаковалось в базу SQLite", head16 == "SQLite format 3", head16);
        }
        catch (Exception e)
        {
            Check("распаковалось в базу SQLite", false, e.GetType().Name);
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
