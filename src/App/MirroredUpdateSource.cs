using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace Counterplay;

/// <summary>
/// Источник обновлений в двух местах: зеркало на своём домене (R2 за
/// Cloudflare) и катящийся тег `latest` на GitHub.
///
/// Зачем зеркало: в материковом Китае GitHub режут. Игроки оттуда ставили
/// программу и неделями сидели на версии установки. Один пинговал ещё
/// 1.0.7.0 через четыре дня после выхода 1.3.71, хотя остальные получили её
/// за пять минут (журнал 2026-11-08). Своё зеркало и в остальном мире быстрее:
/// GitHub с домашнего канала отдаёт 0,1 МБ/с, R2 — десятки.
///
/// Фид спрашиваем у ОБОИХ и берём тот, где версия выше. Если бы спрашивали
/// только зеркало, отставшее зеркало (заливка не прошла) молча остановило
/// бы обновления у всех: фид отвечает, просто новой версии в нём нет, и
/// перейти на GitHub повода не было бы. При равных версиях берём зеркало.
/// Качаем сначала с того, чей фид выбран, а не вышло — с другого.
/// </summary>
public sealed class MirroredUpdateSource : IUpdateSource
{
    public const string MirrorUrl = "https://data.counterplays.com/app/";
    public const string GithubUrl = "https://github.com/28maryshev/counterplay/releases/download/latest/";

    private readonly (string Name, IUpdateSource Source)[] _sources;
    private readonly TimeSpan _feedTimeout;

    /// Чей фид выбран последним: с него и начинаем загрузку.
    private int _chosen;

    /// Последний выбор — для журнала и проверки.
    public string? ChosenName => _sources.Length > 0 ? _sources[_chosen].Name : null;

    public MirroredUpdateSource() : this(TimeSpan.FromSeconds(20),
        ("зеркало", new SimpleWebSource(MirrorUrl)),
        ("GitHub",  new SimpleWebSource(GithubUrl))) { }

    /// Порядок источников — порядок предпочтения при равных версиях.
    public MirroredUpdateSource(TimeSpan feedTimeout, params (string Name, IUpdateSource Source)[] sources)
    {
        if (sources.Length == 0) throw new ArgumentException("нужен хотя бы один источник", nameof(sources));
        _sources = sources;
        _feedTimeout = feedTimeout;
    }

    public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel,
        Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        // Спрашиваем всех сразу: заблокированный GitHub висит до таймаута, и
        // ждать его ПЕРЕД зеркалом значило бы прибавлять эти секунды к каждой
        // проверке.
        var tasks = _sources
            .Select(s => WithTimeout(s.Source.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease)))
            .ToArray();
        await Task.WhenAll(tasks);

        int best = -1;
        SemanticVersion? bestVer = null;
        var notes = new List<string>();
        for (int i = 0; i < tasks.Length; i++)
        {
            var (feed, error) = tasks[i].Result;
            if (feed is null) { notes.Add($"{_sources[i].Name}: {error}"); continue; }
            var top = feed.Assets?.Select(a => a.Version).Where(v => v is not null).Max();
            notes.Add($"{_sources[i].Name}: {top?.ToString() ?? "пусто"}");
            // Строго больше: при равных версиях остаётся тот, кто раньше в списке.
            if (best < 0 || (top is not null && (bestVer is null || top.CompareTo(bestVer) > 0)))
            {
                best = i;
                bestVer = top;
            }
        }

        if (best < 0)
            throw new InvalidOperationException("фид обновлений не ответил нигде — " + string.Join("; ", notes));

        _chosen = best;
        // Пишем, только когда источники разошлись или кто-то не ответил:
        // проверка идёт раз в час, и одинаковая строка каждый час ничего не даёт.
        if (notes.Select(n => n[(n.IndexOf(':') + 1)..]).Distinct().Count() > 1)
            Log.Write($"фид обновлений: беру «{_sources[best].Name}» ({string.Join("; ", notes)})");
        return tasks[best].Result.Feed!;
    }

    public async Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile,
        Action<int> progress, CancellationToken cancelToken = default)
    {
        // Сначала тот, чей фид выбран, потом остальные по порядку.
        var order = Enumerable.Range(0, _sources.Length)
            .OrderBy(i => i == _chosen ? 0 : 1).ThenBy(i => i).ToArray();
        Exception? last = null;
        foreach (var i in order)
        {
            try
            {
                await _sources[i].Source.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
                if (last is not null)
                    Log.Write($"обновление {releaseEntry.FileName} скачано с «{_sources[i].Name}»");
                return;
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                Log.Write($"обновление {releaseEntry.FileName} с «{_sources[i].Name}» не скачалось: {ex.Message}");
                // Обрывок не оставляем: следующий источник пишет в тот же файл.
                try { if (File.Exists(localFile)) File.Delete(localFile); } catch { }
            }
        }
        throw last ?? new InvalidOperationException("обновление не скачалось ни с одного источника");
    }

    /// Фид или причина, по которой его нет. Не бросает: один упавший источник
    /// не должен ронять выбор между остальными.
    private async Task<(VelopackAssetFeed? Feed, string? Error)> WithTimeout(Task<VelopackAssetFeed> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(_feedTimeout));
        if (done != task)
        {
            // Задача доживёт сама; её ошибку забираем, чтобы она не всплыла
            // необработанной в финализаторе.
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return (null, $"нет ответа за {_feedTimeout.TotalSeconds:0} с");
        }
        try { return (await task, null); }
        catch (Exception ex) { return (null, ex.Message); }
    }
}
