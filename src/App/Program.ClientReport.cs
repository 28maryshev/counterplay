using Counterplay;

// Program — отчёт сайту о подключении к клиенту: подключились ли и каким путём.
partial class Program
{
    /// Слать ли отчёт. Песочница — не игрок, даже когда переходит на
    /// настоящий клиент кнопкой «Боевой режим».
    static bool _clientReportOn;

    // По одному отчёту на исход за запуск: «не вышло», а потом «подключились» —
    // это два разных факта, и второй как раз важен.
    static int _okReported, _failReported;

    /// Подключились ли хоть раз за этот запуск.
    static volatile bool _clientConnected;

    /// Каким путём нашлись последние учётные данные. Для отчёта о неудаче:
    /// данные нашлись, а клиент их не принял — или не нашлись вовсе (null).
    static volatile string? _lastVia;

    /// <summary>
    /// Клиент принял подключение (WebSocket открыт — значит, пароль годный).
    /// Окно клиента появляется позже процесса, поэтому смотрим его через
    /// 20 секунд, а потом шлём отчёт.
    /// </summary>
    static void ReportClientConnected(LcuCredentials creds, CancellationToken ct)
    {
        _clientConnected = true;
        if (!_clientReportOn || Interlocked.Exchange(ref _okReported, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
            catch (OperationCanceledException) { return; }
            var w = ClientWindowProbe.Probe();
            Log.Write($"отчёт о подключении: путь {creds.Via}, окно клиента " +
                      (w.Matched ? "узнаётся" : $"не узнаётся ({w.Other ?? "окон нет"})"));
            await Telemetry.ReportClientAsync(true, creds.Via, w.Matched, w.Other);
        }, ct);
    }

    /// <summary>
    /// Сторож неудачи: процесс клиента живёт три минуты подряд, а подключиться
    /// так и не вышло — сообщаем один раз. Ровно так выглядела поломка у
    /// игрока с WeGame: клиент запущен, программа вечно «ждёт клиент».
    /// Три минуты — с запасом на долгий старт клиента и обновление патча.
    /// </summary>
    static void StartClientFailWatch(CancellationToken ct)
    {
        if (!_clientReportOn) return;
        _ = Task.Run(async () =>
        {
            DateTime? since = null;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                catch (OperationCanceledException) { return; }
                if (_clientConnected) return;
                if (!LcuFinder.IsClientRunning()) { since = null; continue; }
                since ??= DateTime.UtcNow;
                if (DateTime.UtcNow - since.Value < TimeSpan.FromMinutes(3)) continue;
                if (Interlocked.Exchange(ref _failReported, 1) != 0) return;

                var w = ClientWindowProbe.Probe();
                Log.Write($"клиент запущен 3 минуты, подключиться не вышло (путь {_lastVia ?? "не найден"}) — сообщаю");
                await Telemetry.ReportClientAsync(false, _lastVia, w.Matched, w.Other);
                return;
            }
        }, ct);
    }
}
