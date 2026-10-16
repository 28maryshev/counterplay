using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

// Program — фоновые задачи: обновление базы, прогрев, иконки, обмен с напарником, сторож патча.
partial class Program
{
    /// <summary>
    /// Фоновое обновление базы — качаем, пока программа работает на прежней.
    ///
    /// Скачанное ложится рядом и ждёт подмены: рабочую базу держит открытой
    /// движок, и подменить её можно только в перерыве между драфтами. Этим
    /// занимается ApplyNewDbIfReady в сессии клиента.
    /// </summary>
    private static void StartDbUpdate(OverlayWindow overlay, string? bucket, CancellationToken ct) =>
        _ = Task.Run(() => UpdateDbAsync(overlay, bucket, ct), ct);

    /// <summary>
    /// Скачать базу фоном и применить, если её никто не держит.
    ///
    /// Попытка подмены прямо здесь — ради случая «клиент LoL закрыт»: движка
    /// нет, файл свободен, и ждать событий от клиента не надо. Если клиент
    /// запущен, движок держит базу и подмена не пройдёт — её подхватит
    /// ApplyNewDbIfReady в перерыве между драфтами.
    /// </summary>
    private static async Task UpdateDbAsync(OverlayWindow overlay, string? bucket,
                                            CancellationToken ct, bool onlyOnNewPatch = false)
    {
        try
        {
            if (await DataDb.UpdateInBackgroundAsync(
                    bucket, (m, f) => overlay.ShowSideProgress(m, f, "db"), ct, onlyOnNewPatch))
                DataDb.ApplySwap();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Write($"фоновое обновление базы: {ex.Message}"); }
        finally { overlay.HideSideProgress("db"); }
    }

    /// <summary>
    /// Разогрев: иконки и справочники Riot — в фоне.
    ///
    /// Ничего из этого не нужно, чтобы программа начала работать. Иконки
    /// подтягиваются по мере готовности (`IconCache.Get` отдаёт null, пока
    /// картинки нет, и карточка просто рисуется без портрета — а события
    /// драфта идут часто, так что следующая перерисовка её подхватит). Руны и
    /// характеристики предметов нужны, только когда чемпион уже выбран.
    ///
    /// Раньше всё это грузилось ПЕРЕД первым показом окна, со строчками
    /// «Загружаю иконки…». На тёплом кэше это пара секунд, на холодном или в
    /// плохой сети — куда больше, и всё это время программа была витриной
    /// собственной загрузки.
    ///
    /// Порядок внутри важен: сперва сверяем справочник с сетью (вдруг вышел
    /// патч и на диске старый), и только потом тянем иконки — иначе скачали бы
    /// набор прошлого патча, а следом ещё раз новый.
    ///
    /// Не загрузилось — повторяем фоном, пока не выйдет. Раньше всё это
    /// грузилось один раз: запустил программу без сети — и до перезапуска
    /// после пика не было ни рун, ни сборки, хотя сеть давно вернулась.
    /// </summary>
    private static void StartWarmup(OverlayWindow overlay, CancellationToken ct)
    {
        // Список связок рун — первым и отдельно. Он крошечный и с нашего сайта,
        // а без него панели рун нет вовсе. Раньше он стоял в конце цепочки, за
        // разбором предметов (в плохой сети — минута с лишним), и пик в первые
        // секунды после запуска его опережал.
        _ = Task.Run(() => KeepTryingAsync("список рун и сборок", RunesClient.LoadManifestAsync, ct), ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
                await ItemIcons.PreloadAsync(ct);   // иконки контр-предметов
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Write($"разогрев не доделан: {ex.Message}"); }

            // Руны: справочник (имена/иконки) + названия предметов + что предмет
            // даёт (броня, магзащита, срез лечения, пробивание — на этом строится
            // подбор сборки под состав врагов). Без справочника рун панель
            // не знает ни одной руны и показывать ей нечего.
            var tries = await KeepTryingAsync("справочники рун и предметов", async t =>
            {
                await RuneIcons.LoadAsync(Loc.DDragonLocale, t);
                await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, t);
                await ItemFacts.LoadAsync(t);
                return RuneIcons.Loaded && ItemIcons.NamesLoaded && ItemFacts.Loaded;
            }, ct);

            // Доехали не с первого раза — панель рун и карточки могли уже стоять
            // на экране без названий рун и контр-предметов: перерисовать.
            if (tries > 1)
            {
                overlay.RefsArrived();
                overlay.BuildsArrived();
            }
        }, ct);
    }

    /// <summary>
    /// Повторять шаг, пока он не скажет «готово»: паузы от 10 секунд, растут до
    /// минуты. Пока сети нет, попытка стоит одного неудачного запроса.
    /// Возвращает номер удачной попытки; 0 — программа закрывается.
    /// </summary>
    private static async Task<int> KeepTryingAsync(string what, Func<CancellationToken, Task<bool>> step,
                                                   CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(10);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (await step(ct))
                {
                    if (attempt > 1) Log.Write($"{what}: загрузились с попытки №{attempt}");
                    return attempt;
                }
            }
            catch (OperationCanceledException) { return 0; }
            catch (Exception ex) { Log.Write($"{what}: {ex.Message}"); }

            if (attempt == 1) Log.Write($"{what}: не загрузились, повторяю фоном");
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return 0; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    /// <summary>
    /// Портреты чемпионов, которых на диске ещё нет, — фоном.
    ///
    /// Первый запуск или свежий патч: качать полторы сотни картинок на глазах у
    /// человека незачем, программа к работе готова и без них. Пустые слоты
    /// дорисуются, когда портреты доедут.
    /// </summary>
    private static void StartIcons(OverlayWindow overlay, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
                overlay.IconsArrived();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write($"портреты не докачались: {ex.Message}"); }
        }, ct);

    /// <summary>
    /// Обмен наигранностью с напарником по дуо-пулу.
    ///
    /// Зачем: подсказку «что брать обоим» программа считает и для половины
    /// друга, но его наигранность ей неизвестна — своя к его чемпионам не
    /// относится. Каждый выкладывает свои числа по чемпионам СВОЕЙ половины,
    /// второй их забирает, и подбор у обоих считается на одних данных.
    ///
    /// Первый проход НЕ на запуске: забранное нужно следующему драфту, а
    /// нынешний и так считается на прошлом снимке из кэша. Дальше раз в три
    /// часа — наигранность меняется медленно, окно у неё месяц.
    /// </summary>
    private static void StartDuoShareWatcher(Func<IReadOnlyDictionary<int, long>?> mastery,
                                             CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            var delay = TimeSpan.FromMinutes(2);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromHours(3);

                try { await DuoShareOnceAsync(mastery(), ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception e) { Log.Write($"дуо-обмен: проход не удался — {e.Message}"); }
            }
        }, ct);
    }

    /// Один проход по всем дуо-пулам с секретом: выложить своё, забрать чужое.
    private static async Task DuoShareOnceAsync(IReadOnlyDictionary<int, long>? mastery,
                                                CancellationToken ct)
    {
        var me = PoolStore.AccountPuuid;
        if (string.IsNullOrEmpty(me)) return;

        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0";
        foreach (var duo in PoolStore.Current().DuoPools.ToList())
        {
            if (string.IsNullOrEmpty(duo.ShareSecret) || string.IsNullOrEmpty(duo.FriendPuuid))
                continue;   // секрета нет — обмена нет, половина считается как прежде

            // Выкладываем только чемпионов СВОЕЙ половины: напарнику нужны
            // ровно они, чтобы посчитать подсказку для меня.
            var snap = DuoShare.Snapshot(duo.Mine.Values.SelectMany(l => l), mastery,
                                         RecommendationEngine.FreshDays,
                                         RecommendationEngine.RegularGames);
            if (snap.Count > 0)
                await DuoShare.PushAsync(duo.ShareSecret, me, version,
                                         Settings.GetString("dataBucket") ?? "", snap, ct);

            await DuoShare.PullAsync(duo.ShareSecret, duo.FriendPuuid, ct);
        }
    }

    /// Следит за выходом патча и перечитывает справочники Riot.
    ///
    /// Цены, характеристики предметов и описания рун Riot правит вместе с
    /// балансом. Раньше они брались один раз при старте: программа живёт в трее
    /// сутками, и после выхода патча она продолжала показывать прошлые числа —
    /// пока человек её не перезапустит.
    ///
    /// Проверка дешёвая (один маленький файл с номерами версий) и редкая: патчи
    /// выходят раз в две недели, спешить некуда.
    private static void StartPatchWatcher(OverlayWindow overlay, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(3), ct); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var was = DataDragon.Version;
                    // LoadAsync сам перечитает номер версии из versions.json.
                    await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
                    if (DataDragon.Version != was)
                    {
                        Log.Write($"патч сменился: {was} → {DataDragon.Version}, обновляю справочники");
                        // Каждый из них сверяется с номером патча сам и перечитает
                        // только то, что устарело.
                        await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
                        await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, ct);
                        await ItemIcons.PreloadAsync(ct);
                        await ItemFacts.LoadAsync(ct);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // Сети нет или Data Dragon прилёг — попробуем через три часа.
                    Log.Write($"проверка патча не удалась: {ex.Message}");
                }

                // Список связок рун — тоже каждый круг: руны нового патча сайт
                // выкладывает, когда наберутся матчи, а не в день патча. Не
                // ответил — в силе остаётся прежний.
                await RunesClient.LoadManifestAsync(ct);

                // База — отдельным заходом, и НЕ только когда сменился Data
                // Dragon: пайплайн выкладывает её не в ту же минуту, что Riot
                // патч, а через день-другой, когда наберутся матчи. Поэтому
                // спрашиваем каждый круг; свой манифест сам скажет, сменился ли
                // патч, и внутри патча ничего не качается.
                //
                // Раньше сверка была только на запуске, и у того, кто держит
                // программу в трее неделями, база оставалась от прошлого патча.
                await UpdateDbAsync(overlay, Settings.GetString("dataBucket"), ct,
                                    onlyOnNewPatch: true);
            }
        }, ct);
    }
}
