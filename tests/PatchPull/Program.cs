using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Вышел патч — база подтягивается сама, без перезапуска программы.
///
/// Проверка ставит СВОЙ сервер и гоняет через него весь путь: манифест →
/// решение качать или нет → закачка → готовность к подмене. Ни сети, ни боевой
/// базы игрока: папка временная, источник подменён.
///
/// Главное, что тут утверждается: **внутри патча не качается ничего**.
/// Пайплайн перевыкладывает базу почти каждый день, добирая матчи, и версия
/// файла меняется вместе с ней. Если гнаться за версией, человек получит по
/// сотне с лишним мегабайт в сутки на ровном месте. Тянуть надо, когда Riot
/// выпустил новый патч — по нему и сверяемся.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Что сервер отдаёт сейчас и сколько раз у него просили саму базу.
    private static string _patch = "16.19";
    private static string _version = "AAA111";
    private static int _dbHits;
    // Задержка на выдаче базы: без неё две сверки не накладываются, и замок
    // между ними нечем проверить — файлы в проверке крошечные.
    private static volatile int _slowMs;

    private static string Db => DataDb.LocalPath;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var dir = Path.Combine(Path.GetTempPath(), "cp-patchpull");
        Try(() => Directory.Delete(dir, true));
        Directory.CreateDirectory(dir);

        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));

        DataDb.DirOverride    = dir;
        DataDb.SourceOverride = $"http://localhost:{port}";

        try { Run(); }
        finally
        {
            DataDb.DirOverride = null;
            DataDb.SourceOverride = null;
            try { listener.Stop(); } catch { }
            Try(() => Directory.Delete(dir, true));
            Console.WriteLine("\nсвой сервер погашен, боевая база не тронута");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: база тянется на новый патч и только на него"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var ct = CancellationToken.None;

        // ── 1. Первая сверка: базы нет, качаем ─────────────────────────────
        var got = DataDb.UpdateInBackgroundAsync("gold", null, ct).GetAwaiter().GetResult();
        Check("1. первая сверка скачала базу", got && DataDb.SwapReady, got ? "да" : "нет");
        Check("   патч запомнен", DataDb.DbPatch == "16.19", DataDb.DbPatch ?? "—");
        Check("   за базой сходили один раз", _dbHits == 1, _dbHits.ToString());
        Check("   рабочая база не тронута", !File.Exists(Db), File.Exists(Db) ? "подменена" : "цела");

        Check("   подмена проходит", DataDb.ApplySwap(), "да");
        Check("   и база на месте", File.Exists(Db) && File.ReadAllText(Db).StartsWith("база"),
              File.Exists(Db) ? "да" : "нет");

        // ── 2. Пайплайн перевыложил базу, патч ТОТ ЖЕ ──────────────────────
        // Так бывает почти каждый день: версия новая, данных чуть больше.
        // Повторная сверка обязана пройти мимо.
        _version = "BBB222";
        var before = _dbHits;
        var again = DataDb.UpdateInBackgroundAsync("gold", null, ct, onlyOnNewPatch: true)
                          .GetAwaiter().GetResult();
        Check("2. внутри патча база не качается", !again && _dbHits == before,
              _dbHits == before ? "не полезли" : $"сходили ещё {_dbHits - before} раз");

        // ── 3. Вышел патч ──────────────────────────────────────────────────
        _patch = "16.20";
        _version = "CCC333";
        var onPatch = DataDb.UpdateInBackgroundAsync("gold", null, ct, onlyOnNewPatch: true)
                            .GetAwaiter().GetResult();
        Check("3. на новый патч база скачалась", onPatch && DataDb.SwapReady, onPatch ? "да" : "нет");
        Check("   патч обновился", DataDb.DbPatch == "16.20", DataDb.DbPatch ?? "—");
        DataDb.ApplySwap();

        // ── 4. Второй заход по тому же патчу — снова мимо ──────────────────
        before = _dbHits;
        DataDb.UpdateInBackgroundAsync("gold", null, ct, onlyOnNewPatch: true)
              .GetAwaiter().GetResult();
        Check("4. по тому же патчу второй раз не ходим", _dbHits == before, _dbHits.ToString());

        // ── 5. Две сверки разом — качает одна ──────────────────────────────
        // Сторож патчей и запуск могут совпасть; обе писали бы в один .tmp.
        _patch = "16.21";
        _version = "DDD444";
        before = _dbHits;
        _slowMs = 700;          // растягиваем выдачу, чтобы сверки наложились
        var a = Task.Run(() => DataDb.UpdateInBackgroundAsync("gold", null, ct, onlyOnNewPatch: true));
        var b = Task.Run(() => DataDb.UpdateInBackgroundAsync("gold", null, ct, onlyOnNewPatch: true));
        Task.WaitAll(a, b);
        _slowMs = 0;
        Check("5. одновременные сверки качают один раз", _dbHits - before == 1,
              $"{_dbHits - before}");
        Check("   и хотя бы одна довела дело до конца", a.Result || b.Result, "да");
    }

    // ── Крошечный сервер вместо хранилища ───────────────────────────────────
    private static void Serve(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = l.GetContext(); } catch { return; }
            try
            {
                var path = c.Request.Url!.AbsolutePath;
                byte[] body;

                // Считаем ЛЮБОЕ обращение за базой, включая запасной путь без
                // сжатия. Без этого промах второй сверки (она спотыкается о
                // занятый .tmp и уходит качать несжатую) оставался невидимым.
                if (c.Request.HttpMethod == "GET" && path.Contains(".db"))
                {
                    Interlocked.Increment(ref _dbHits);
                    if (_slowMs > 0) Thread.Sleep(_slowMs);
                }

                if (path.EndsWith("data-version.json"))
                {
                    body = Encoding.UTF8.GetBytes(
                        $"{{\"version\":\"{_version}\",\"patch\":\"{_patch}\"," +
                        $"\"buckets\":{{\"gold\":{{\"version\":\"{_version}\",\"size_mb\":1}}}}}}");
                    c.Response.ContentType = "application/json";
                }
                else if (path.EndsWith(".db.gz"))
                {
                    using var ms = new MemoryStream();
                    using (var gz = new GZipStream(ms, CompressionMode.Compress, true))
                    {
                        var raw = Encoding.UTF8.GetBytes($"база патча {_patch} версии {_version}");
                        gz.Write(raw, 0, raw.Length);
                    }
                    body = ms.ToArray();
                }
                else { c.Response.StatusCode = 404; c.Response.Close(); continue; }

                c.Response.ContentLength64 = body.Length;
                // HEAD спрашивают ради размера — тело в ответ на него не шлём.
                if (c.Request.HttpMethod != "HEAD")
                    c.Response.OutputStream.Write(body, 0, body.Length);
                c.Response.Close();
            }
            catch { try { c.Response.Abort(); } catch { } }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void Try(Action a) { try { a(); } catch { /* нечего чистить */ } }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
