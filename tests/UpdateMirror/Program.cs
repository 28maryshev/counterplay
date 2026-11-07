using System.IO;
using System.Net.Http;
using System.Text;
using Counterplay;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Counterplay.Tests;

/// <summary>
/// Источник обновлений из двух мест (`MirroredUpdateSource`): зеркало на своём
/// домене и GitHub. В Китае GitHub режут, и обновления туда не доходили
/// (журнал 2026-11-08); зеркало не должно при этом сломать обновления
/// остальным, когда само отстало или легло.
///
/// Сеть не нужна: источники здесь поддельные.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        await TieGoesToMirror();
        await StaleMirrorLosesToGithub();
        await BlockedGithubIsSkipped();
        await HangingGithubDoesNotStall();
        await NothingAnswersThrows();
        await DownloadFallsBack();
        await CancelDoesNotFallBack();
        Addresses();

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what);
        if (!ok) _fails++;
    }

    // ── поддельный источник ─────────────────────────────────────────────

    private sealed class Fake(string? version, string name) : IUpdateSource
    {
        public Exception? FeedError;
        public bool Hang;
        public Exception? DownloadError;
        public int Downloads;

        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
            Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
        {
            if (Hang) await Task.Delay(Timeout.Infinite);
            if (FeedError is not null) throw FeedError;
            await Task.Yield();
            return new VelopackAssetFeed
            {
                Assets = version is null ? [] : [Asset(version)]
            };
        }

        public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
            Action<int> progress, CancellationToken cancelToken = default)
        {
            Downloads++;
            cancelToken.ThrowIfCancellationRequested();
            // Обрывок на диске — как у настоящей загрузки, оборванной на середине.
            await File.WriteAllTextAsync(localFile, DownloadError is null ? name : "обрывок", cancelToken);
            if (DownloadError is not null) throw DownloadError;
            progress(100);
        }
    }

    private static VelopackAsset Asset(string v) => new()
    {
        PackageId = "Counterplay",
        Version = SemanticVersion.Parse(v),
        Type = VelopackAssetType.Full,
        FileName = $"Counterplay-{v}-full.nupkg",
    };

    private static MirroredUpdateSource Two(Fake mirror, Fake github, double timeoutSec = 5) =>
        new(TimeSpan.FromSeconds(timeoutSec), ("зеркало", mirror), ("GitHub", github));

    private static Task<VelopackAssetFeed> Feed(MirroredUpdateSource s) =>
        s.GetReleaseFeed(null!, "Counterplay", "win");

    private static string TopOf(VelopackAssetFeed f) =>
        f.Assets.Select(a => a.Version).Max()?.ToString() ?? "пусто";

    // ── выбор фида ───────────────────────────────────────────────────────

    private static async Task TieGoesToMirror()
    {
        Console.WriteLine("одна версия в обоих местах:");
        var s = Two(new Fake("1.3.84", "m"), new Fake("1.3.84", "g"));
        var f = await Feed(s);
        Check(TopOf(f) == "1.3.84" && s.ChosenName == "зеркало", $"берётся зеркало ({s.ChosenName}, {TopOf(f)})");
    }

    // Главная защита: отставшее зеркало отвечает исправно, только новой
    // версии в нём нет. Если бы оно шло первым и единственным, обновления
    // остановились бы у всех.
    private static async Task StaleMirrorLosesToGithub()
    {
        Console.WriteLine("зеркало отстало:");
        var s = Two(new Fake("1.3.83", "m"), new Fake("1.3.84", "g"));
        var f = await Feed(s);
        Check(TopOf(f) == "1.3.84" && s.ChosenName == "GitHub", $"берётся GitHub с новой версией ({s.ChosenName}, {TopOf(f)})");

        var empty = Two(new Fake(null, "m"), new Fake("1.3.84", "g"));
        var f2 = await Feed(empty);
        Check(TopOf(f2) == "1.3.84", $"пустое зеркало не перебивает GitHub ({TopOf(f2)})");
    }

    private static async Task BlockedGithubIsSkipped()
    {
        Console.WriteLine("GitHub не отвечает (как в Китае):");
        var g = new Fake("1.3.85", "g") { FeedError = new HttpRequestException("connection reset") };
        var s = Two(new Fake("1.3.84", "m"), g);
        var f = await Feed(s);
        Check(TopOf(f) == "1.3.84" && s.ChosenName == "зеркало", $"берётся зеркало ({s.ChosenName}, {TopOf(f)})");
    }

    private static async Task HangingGithubDoesNotStall()
    {
        Console.WriteLine("GitHub висит:");
        var s = Two(new Fake("1.3.84", "m"), new Fake("1.3.85", "g") { Hang = true }, timeoutSec: 1);
        var t0 = DateTime.UtcNow;
        var f = await Feed(s);
        var took = (DateTime.UtcNow - t0).TotalSeconds;
        Check(TopOf(f) == "1.3.84", $"фид с зеркала ({TopOf(f)})");
        Check(took < 3, $"ждали не дольше срока ({took:0.0} с при сроке 1 с)");
    }

    private static async Task NothingAnswersThrows()
    {
        Console.WriteLine("не отвечает никто:");
        var s = Two(new Fake("1.3.84", "m") { FeedError = new HttpRequestException("404 зеркала") },
                    new Fake("1.3.84", "g") { FeedError = new HttpRequestException("сброс GitHub") });
        string? msg = null;
        try { await Feed(s); }
        catch (Exception ex) { msg = ex.Message; }
        Check(msg is not null, "бросает — фоновая проверка напишет причину в журнал");
        Check(msg?.Contains("404 зеркала") == true && msg.Contains("сброс GitHub"),
              $"в причине оба источника ({msg})");
    }

    // ── загрузка ─────────────────────────────────────────────────────────

    private static async Task DownloadFallsBack()
    {
        Console.WriteLine("загрузка с выбранного не удалась:");
        var dir = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "UpdateMirror");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "pkg.nupkg");
        if (File.Exists(file)) File.Delete(file);

        var m = new Fake("1.3.84", "m") { DownloadError = new HttpRequestException("обрыв") };
        var g = new Fake("1.3.84", "g");
        var s = Two(m, g);
        await Feed(s);   // ничья — выбрано зеркало
        var pct = 0;
        await s.DownloadReleaseEntry(null!, Asset("1.3.84"), file, p => pct = p);
        Check(m.Downloads == 1 && g.Downloads == 1, $"сначала зеркало, потом GitHub ({m.Downloads}, {g.Downloads})");
        Check(File.ReadAllText(file) == "g", "в файле пакет с GitHub, а не обрывок");
        Check(pct == 100, "ход загрузки доходит до программы");

        // Выбран GitHub — с него и начинаем, зеркало не трогаем.
        var m2 = new Fake("1.3.83", "m");
        var g2 = new Fake("1.3.84", "g");
        var s2 = Two(m2, g2);
        await Feed(s2);
        await s2.DownloadReleaseEntry(null!, Asset("1.3.84"), file, _ => { });
        Check(g2.Downloads == 1 && m2.Downloads == 0, $"фид с GitHub — качаем с GitHub ({m2.Downloads}, {g2.Downloads})");

        var m3 = new Fake("1.3.84", "m") { DownloadError = new HttpRequestException("нет файла") };
        var g3 = new Fake("1.3.84", "g") { DownloadError = new HttpRequestException("сброс") };
        var s3 = Two(m3, g3);
        await Feed(s3);
        Exception? err = null;
        try { await s3.DownloadReleaseEntry(null!, Asset("1.3.84"), file, _ => { }); }
        catch (Exception ex) { err = ex; }
        Check(err is not null && !File.Exists(file), $"не скачалось нигде — ошибка, обрывка нет ({err?.Message})");
    }

    // Отмена — это выход из программы, а не сбой сети: пробовать другой
    // источник незачем.
    private static async Task CancelDoesNotFallBack()
    {
        Console.WriteLine("отмена загрузки:");
        var file = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "UpdateMirror", "pkg2.nupkg");
        var m = new Fake("1.3.84", "m");
        var g = new Fake("1.3.84", "g");
        var s = Two(m, g);
        await Feed(s);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = false;
        try { await s.DownloadReleaseEntry(null!, Asset("1.3.84"), file, _ => { }, cts.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && g.Downloads == 0, $"отмена уходит наверх, GitHub не трогаем ({g.Downloads})");
    }

    private static void Addresses()
    {
        Console.WriteLine("адреса:");
        Check(MirroredUpdateSource.MirrorUrl == "https://data.counterplays.com/app/",
              "зеркало — папка app/ на домене базы (туда заливает pipeline/mirror_app.py)");
        Check(MirroredUpdateSource.GithubUrl.EndsWith("/releases/download/latest/"),
              "GitHub — катящийся тег latest");
    }
}
