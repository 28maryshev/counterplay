using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

// Program — цикл подключения к клиенту League: дождаться, подключиться, переподключиться.
partial class Program
{
    // ── LCU-цикл: Data Dragon один раз, потом перебирает сессии LCU ─────────

    static async Task RunLcuAsync(OverlayWindow overlay, string[] args, CancellationToken ct)
    {
        // Путь к lockfile: первый НЕ-флаговый аргумент, иначе null → автопоиск
        // клиента. Флаги (--autostart и т.п.) пропускаем: иначе автозапуск
        // подсовывал бы «--autostart» вместо пути к lockfile.
        // .cpool сюда попасть не должен: это файл пула, открытый двойным кликом,
        // а не путь к lockfile. Иначе клиент по нему не находился бы и цикл
        // заканчивался сразу после старта.
        var lockfilePath = args.FirstOrDefault(
            a => !a.StartsWith('-') && !a.EndsWith(FileAssoc.Extension, StringComparison.OrdinalIgnoreCase));

        overlay.SetVersion(Log.Version);

        // Синхронизация переписала файлы — перечитать их и перерисовать. В
        // потоке окна: на экране всё живёт в памяти и о подмене файлов само не
        // узнает. Раньше перечитывались только пулы: график и LP с другого
        // компьютера ждали минутного обновления профиля, а настройки — до
        // перезапуска (и до него их затирало сохранение из памяти).
        SyncClient.Pulled = files => overlay.Dispatcher.InvokeAsync(() =>
        {
            if (files.Contains("pools.json"))
            {
                PoolStore.Reload();
                overlay.RefreshPoolMode();
            }
            if (files.Contains("ui.json")) AppSettings.Reload();
            if (files.Contains("session.json"))
            {
                SessionTracker.DropCache();
                if (_refreshSession is { } refresh) _ = Task.Run(refresh);
            }
            // Открытое окно пулов показывает и пулы, и винрейты из журнала игр.
            overlay.ReloadPoolWindow();
        });

        // Обновления НИЧЕГО не задерживают: сайдбар показывается сразу, а новая
        // версия качается фоном и встаёт сама, когда человек не в драфте.
        //
        // Раньше запуск начинался с проверки и, если версия нашлась, вставал на
        // экран загрузки до конца скачивания. Программа при этом была полностью
        // готова работать.
        StartUpdateWatcher(overlay, ct);

        // Имена чемпионов — с диска, мгновенно. Сеть спросим фоном.
        //
        // Раньше запуск ЖДАЛ Data Dragon: два запроса, а справочник меняется
        // раз в патч. В плохой сети это двадцать секунд, и все двадцать
        // программа не показывала ничего.
        if (!DataDragon.LoadFromCache(Loc.DDragonLocale))
        {
            // Первый запуск: заменить нечем, придётся подождать.
            overlay.ShowStatus(Loc.T("status.loadingChamps"));
            await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
        }

        // Портреты уже на диске — читаем их СРАЗУ, это доли секунды. Иначе вид,
        // собранный до разогрева, остаётся с пустыми слотами, и каждый такой
        // вид надо вспоминать и обновлять отдельно. Ровно на этом и попались:
        // лента винрейтов в сайдбаре стояла пустой до конца игры.
        //
        // Из сети — дело другое: там ждать нельзя, и дорисовка оправдана.
        if (IconCache.AllCached())
            await IconCache.PreloadAllAsync(null, ct);
        else
        {
            overlay.ShowStatus(Loc.T("status.loadingIcons"));
            StartIcons(overlay, ct);
        }

        // Ходовые предметы врагов приходят по сети уже после отрисовки карточек:
        // строку «контрят тебя и команду» тогда надо пересобрать, иначе в ней
        // останутся предметы, которые на этих чемпионах не покупают.
        RunesClient.ItemsLoaded = overlay.BuildsArrived;

        StartWarmup(overlay, ct);         // справочники Riot — фоном
        StartPatchWatcher(overlay, ct);   // цены, характеристики и руны — с выходом патча

        // Гарантируем наличие data.db. Качаем базу только СВОЕГО эло (~50 МБ) по
        // сохранённому с прошлого запуска рангу. На ПЕРВОМ запуске ранг ещё
        // неизвестен → берём дефолтный бакет (emerald, ~57 МБ), а НЕ общую базу
        // всех бакетов (~198 МБ). Реальный ранг подхватим из LCU ниже и, если он
        // другой, подкачаем нужный бакет — суммарно всё равно меньше общей базы.
        var storedBucket = Settings.GetString("dataBucket") ?? "emerald";

        // Скачанное фоном в прошлый раз применяем сейчас: база ещё никем не
        // открыта, и это единственный момент, когда подмена заведомо пройдёт.
        DataDb.ApplySwap();

        if (DataDb.HaveUsableDb())
        {
            // Работать есть на чём — обновляемся ФОНОМ. Раньше здесь стоял
            // await: нашлась новая версия, и программа вставала на экран
            // загрузки, пока не докачает. На медленном канале это полчаса, всё
            // это время негодная, хотя рабочая база лежала рядом. Теперь она
            // считает драфт на прежней, а внизу сайдбара идёт полоса.
            StartDbUpdate(overlay, storedBucket, ct);
        }
        else
        {
            // Базы нет вовсе — первый запуск. Тут ждать приходится: считать не на чем.
            await DataDb.EnsureAsync(storedBucket, (msg, frac) => overlay.ShowProgress(msg, frac), ct);
        }
        DataDb.SelfClean();

        // Внешний цикл — переподключение при перезапуске клиента.
        //
        // Первую попытку после обрыва делаем МОЛЧА: клиент почти всегда на месте,
        // и мигать «соединение потеряно» из-за секундной заминки незачем. Если и
        // вторая не удалась — тогда говорим, значит дело серьёзнее.
        var quiet = false;
        while (!ct.IsCancellationRequested)
        {
            // Клиента нет — ни драфта, ни игры: самое спокойное время.
            _restartSafe = true;
            ApplyUpdateIfIdle(overlay);
            if (!quiet)
            {
                overlay.SetLcuReady(false); // не подключены — авто-возврат из трея подавлен
                overlay.ShowStatus(Loc.T("status.waitingClient"));
            }
            var creds = await LockfileReader.WaitForAsync(lockfilePath, ct);

            try
            {
                await RunSessionAsync(overlay, creds, ct);
                quiet = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException
                                          or System.Net.WebSockets.WebSocketException
                                          or IOException)
            {
                // Клиент закрылся (WebSocket оборван без рукопожатия), lockfile устарел
                // или сеть моргнула — НЕ падаем, ждём клиент снова.
                if (quiet)
                {
                    // Вторая неудача подряд — значит не заминка.
                    quiet = false;
                    overlay.ShowStatus(Loc.T("status.connLost"));
                    try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
                }
                else
                {
                    quiet = true;
                    try { await Task.Delay(700, ct); } catch (OperationCanceledException) { return; }
                }
            }
        }
    }
}
