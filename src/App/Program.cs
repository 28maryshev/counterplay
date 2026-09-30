using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

class Program
{
    // Подключаем WPF-процесс к консоли родителя — иначе Ctrl+C не работает при dotnet run
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        // Velopack: обязательно ПЕРВОЙ строкой — обрабатывает хуки установки/обновления.
        VelopackApp.Build().Run();

        AttachConsole(-1); // -1 = ATTACH_PARENT_PROCESS

        // Диагностический прогон движка рекомендаций (настройка весов), затем выход.
        if (args.Contains("--drafttest")) { DraftTest.Run().GetAwaiter().GetResult(); return; }

        // Диагностика автозапуска (поддержка, отладка): показать состояние и выйти.
        if (args.Contains("--autostart-info"))
        {
            Console.WriteLine($"supported: {Autostart.Supported}");
            Console.WriteLine($"stub:      {Autostart.StubPath ?? "(none)"}");
            Console.WriteLine($"enabled:   {Autostart.IsEnabled}");
            Console.WriteLine($"setting:   {Settings.GetBool("autostart")?.ToString() ?? "(unset)"}");
            return;
        }

        // Один экземпляр на пользователя. С автозапуском программа уже висит в
        // трее — второй запуск (клик по ярлыку) плодил бы второй оверлей и мешал
        // обновлению (Velopack держит блокировку папки). Вместо этого будим тот,
        // что уже работает, и выходим.
        // У песочницы свой замок: иначе она молча выходила, когда открыта боевая
        // программа, и вместо неё разворачивалось уже запущенное окно. Смотреть
        // на изменения рядом с настоящим окном — обычное дело при разработке.
        var sandbox = args.Contains("test") || args.Contains("--test");
        using var single = new Mutex(
            initiallyOwned: true,
            sandbox ? "Counterplay.SingleInstance.Test" : "Counterplay.SingleInstance",
            out var isFirst);
        // Двойной клик по файлу пула: путь приходит аргументом.
        var poolFile = args.FirstOrDefault(
            a => a.EndsWith(FileAssoc.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        if (!isFirst)
        {
            try
            {
                // Сигнал — голое событие, данных в нём нет. Путь передаём файлом:
                // кладём ДО сигнала, иначе первый экземпляр проснётся и ничего
                // не найдёт.
                if (poolFile is not null) OpenRequest.Put(poolFile);
                using var show = EventWaitHandle.OpenExisting(ShowSignal);
                show.Set(); // существующий экземпляр развернётся из трея
            }
            catch { /* сигнал не дошёл — просто выходим */ }
            return;
        }

        Loc.Init(); // язык интерфейса: сохранённый выбор → язык Windows → English

        // Шапку файла журнала пишет первая же запись, а первая запись случается
        // уже при создании окна — значит версию и режим надо знать до него.
        // Иначе в присланном журнале не видно, о какой сборке речь.
        Log.Mode = sandbox ? "тестовый" : "боевой";
        // Песочница живёт на той же базе, что и боевой экземпляр, и своей не
        // качает: подменить файл она всё равно не может, пока основной запущен.
        DataDb.ReuseLocal = sandbox;
        Log.Version = CurrentVersion();

        // Автозапуск с Windows: включаем при первом старте новой версии и один раз
        // сообщаем об этом в панели (молча прописываться в автозагрузку — дурной тон).
        var autostartNotice = Autostart.ApplyOnStartup();

        // Связка .cpool с программой: двойной клик по присланному файлу должен
        // открывать импорт, а не блокнот. Проверяем на каждом старте — путь
        // меняется при переустановке.
        FileAssoc.EnsureRegistered();

        _ = Telemetry.PingAsync(); // анонимный пинг для статистики активных пользователей

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var app     = new System.Windows.Application();
        // Общий вид для всех окон: подсказки в фирменной плашке, а не системной.
        // Окна пулов, настроек и подтверждения собраны кодом, своей разметки у
        // них нет, и стиль до них доходит только отсюда.
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            // Путь с именем сборки, а не просто "/src/...": короткий вид
            // разрешается относительно точки входа, и вне приложения (в
            // проверке) словарь по нему не находится.
            Source = new Uri("/Counterplay;component/src/Ui/Theme.xaml", UriKind.Relative),
        });
        var overlay = new OverlayWindow();

        // Автозапуск с Windows: стартуем свёрнутыми в трей. Показывать окно
        // «LCU is starting…» при входе в систему — навязчиво; оверлей появится
        // сам, когда запустится клиент.
        if (args.Contains("--autostart")) overlay.StartInTray();
        else                              overlay.Show();

        if (autostartNotice) overlay.ShowAutostartNotice();

        // Повторный запуск (клик по ярлыку, пока мы в трее) — разворачиваем окно.
        StartShowSignalListener(overlay, cts.Token);

        // Программу запустили двойным кликом по файлу пула — предложим загрузить
        // его, как только окно готово принимать диалоги.
        if (poolFile is not null)
            overlay.Dispatcher.BeginInvoke(new Action(() => overlay.OpenPoolFile(poolFile)),
                                           System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var lcuTask = Task.Run(async () =>
        {
            try
            {
                // «test empty» — сразу скелетон-вид (профиль без единой игры).
                if (sandbox)   // песочница-драфт без клиента LoL
                {
                    await TestMode.RunAsync(overlay, args.Contains("empty"), args.Contains("firstgame"),
                                            args.Contains("fivegames"), cts.Token);
                    // Песочница вернула управление — значит, в ней нажали
                    // «Боевой режим»: дальше работаем на настоящем клиенте.
                    //
                    // Аргументы песочницы обязательно вычищаем: путь к lockfile
                    // берётся как ПЕРВЫЙ не-флаговый аргумент, и слово «test»
                    // уходило туда как путь. Клиент по нему не находился, цикл
                    // сразу заканчивался — и программа закрывалась целиком.
                    if (TestMode.SwitchToLive)
                    {
                        var liveArgs = args
                            .Where(a => a is not ("test" or "--test" or "empty"
                                                  or "firstgame" or "fivegames" or "golive"))
                            .ToArray();
                        Console.WriteLine("[live] стартую боевой цикл, аргументы: "
                                          + (liveArgs.Length == 0 ? "(нет)" : string.Join(" ", liveArgs)));
                        await RunLcuAsync(overlay, liveArgs, cts.Token);
                    }
                }
                else await RunLcuAsync(overlay, args, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                overlay.Dispatcher.Invoke(() =>
                    System.Windows.MessageBox.Show(ex.Message, "Counterplay", MessageBoxButton.OK, MessageBoxImage.Error));
            }
            finally
            {
                overlay.Dispatcher.Invoke(() => app.Shutdown());
            }
        });

        overlay.Closed += (_, _) => cts.Cancel();
        app.Run();

        cts.Cancel();
        lcuTask.GetAwaiter().GetResult();
    }

    // ── Руны и билд ────────────────────────────────────────────────────────
    // Панель показываем, когда мой чемпион уже определён (залочен или наведён)
    // и по связке чемпион+роль есть данные на сервере. Запрос идёт один раз за
    // драфт, а не на каждый ховер, поэтому сеть не мешает.
    private static string _runesShownFor = "";   // связка, для которой уже показали руны

    public static async Task UpdateRunesAsync(OverlayWindow overlay, DraftState? draft, CancellationToken ct)
    {
        // Только залоченный чемпион, не наведённый. Раньше руны и сборка
        // появлялись уже по ховеру, и это сбивало: подсказки на экране, а пик
        // ещё не сделан — человек считал, что чемпион уже взят, и терял ход.
        // Кнопка подтверждения при этом работает по ховеру, как и раньше:
        // навёл — подтвердил — увидел сборку.
        var champ = draft?.Me?.ChampionId ?? 0;
        if (draft is null || champ == 0 || draft.IsAram)
        {
            _runesShownFor = "";
            overlay.HideRunes();
            return;
        }

        // Роль: из драфта, иначе основная роль чемпиона (custom games/блайнд не
        // раскрывают позицию — но руны показать всё равно нужно).
        var wanted = RecommendationEngine.LcuToDbRole(draft.MyPosition);
        var role = RunesClient.ResolveRole(champ, wanted);
        if (role is null || !RunesClient.Has(champ, role))
        {
            // «Не показывает руны» — жалоба, которую нельзя разобрать без этой
            // строки: причин три (не загрузился манифест, роль не раскрыта,
            // связки нет в данных), а внешне они выглядят одинаково — пустотой.
            if (_runesMissLogged != champ)
            {
                _runesMissLogged = champ;
                Log.Write($"руны не показаны: {DataDragon.Name(champ)}, роль «{wanted}» → " +
                          $"{(role is null ? "связка не найдена" : $"«{role}» нет в данных")}, " +
                          $"манифест {(RunesClient.ManifestLoaded ? "загружен" : "НЕ ЗАГРУЖЕН")}");
            }
            overlay.HideRunes();
            return;
        }

        var opponent = draft.DirectOpponent?.EffectiveChampionId;
        var key = $"{champ}:{role}:{opponent ?? 0}";
        if (key == _runesShownFor) return;   // уже показано для этой связки
        _runesShownFor = key;

        var stats = await RunesClient.GetAsync(champ, role, ct);
        _runesMissLogged = 0;
        Log.Write($"руны: {DataDragon.Name(champ)} на роли «{role}»" +
                  (stats is null ? " — данные не пришли" : $", игр {stats.Games:N0}"));
        overlay.ShowRunes(stats, champ, DataDragon.Name(champ), role, opponent);
    }

    // Чемпион, по которому уже написали «рун нет» — чтобы не повторяться каждый
    // цикл чемп-селекта.
    static int _runesMissLogged;

    // Имя события «покажи окно» — им второй экземпляр будит первый.
    const string ShowSignal = "Counterplay.ShowWindow";

    // Слушаем сигнал от повторных запусков и разворачиваем оверлей из трея.
    static void StartShowSignalListener(OverlayWindow overlay, CancellationToken ct)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!handle.WaitOne(500)) continue;
                overlay.RestoreFromTray(force: true);
                // Второй запуск мог принести файл пула — забираем и открываем.
                if (OpenRequest.Take() is { } file)
                    overlay.Dispatcher.BeginInvoke(new Action(() => overlay.OpenPoolFile(file)));
            }
        })
        { IsBackground = true };
        thread.Start();
    }

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

        // Пришло чужое — перечитать пулы и перерисовать. В потоке окна: на
        // экране они живут в памяти и о подмене файлов сами не узнают.
        SyncClient.Pulled = () => overlay.Dispatcher.InvokeAsync(() =>
        {
            PoolStore.Reload();
            overlay.RefreshPoolMode();
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
    /// </summary>
    private static void StartWarmup(OverlayWindow overlay, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
                await RoleIcons.PreloadAsync(ct);
                await ItemIcons.PreloadAsync(ct);   // иконки контр-предметов

                // Руны: справочник (имена/иконки) + названия предметов + манифест
                // сервера. Если данных на сервере ещё нет — панель просто не
                // появится, фича включится сама, когда наберётся выборка.
                await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
                await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, ct);
                // Что предмет даёт (броня, магзащита, срез лечения, пробивание) —
                // на этом строится подбор сборки под состав врагов.
                await ItemFacts.LoadAsync(ct);
                await RunesClient.LoadManifestAsync(ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Log.Write($"разогрев не доделан: {ex.Message}"); }
        }, ct);

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

    /// Скачанное обновление, которое ждёт тихой минуты. Пусто — ждать нечего.
    private static Velopack.VelopackAsset? _updateStaged;

    /// <summary>
    /// Можно ли сейчас перезапуститься.
    ///
    /// Нельзя в драфте и в игре: перезапуск отнимает секунду, но именно эта
    /// секунда — то, ради чего программу открывают. В меню, в лобби и при
    /// закрытом клиенте — можно.
    /// </summary>
    private static volatile bool _restartSafe = true;

    /// <summary>
    /// Поставить скачанное обновление и перезапуститься, если сейчас можно.
    ///
    /// Не время — ничего не делаем: позовут снова в конце драфта или на смене
    /// фазы. Совсем не позовут (человек закрыл программу) — обновление никуда
    /// не денется, оно уже на диске, и встанет при следующем запуске.
    ///
    /// Пробуем ОДИН раз за сеанс: если установка не удалась, повторять её по
    /// кругу — верный способ получить программу, которая только и делает, что
    /// перезапускается.
    /// </summary>
    static void ApplyUpdateIfIdle(OverlayWindow overlay)
    {
        var staged = _updateStaged;
        if (staged is null) return;
        if (!_restartSafe || overlay.GameActive) return;
        if (AlreadyFailed(staged.Version?.ToString())) { _updateStaged = null; return; }

        _updateStaged = null;
        try
        {
            Log.Write($"ставлю версию {staged.Version} и перезапускаюсь");
            overlay.ShowProgressBusy(Loc.T("status.applyingUpdate"));
            ApplySilently(overlay, new UpdateManager(UpdateSource), staged);
        }
        catch (Exception ex)
        {
            // Не встало — работаем на прежней версии, попробуем при запуске.
            Log.Write($"обновление не поставилось: {ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>
    /// С чем перезапускаться после обновления.
    ///
    /// Переносим свои ключи (в том числе --autostart: с ним программа
    /// поднимается сразу в трей) и путь к lockfile. Открытый двойным кликом
    /// файл пула НЕ переносим — это разовое действие, повторять его незачем.
    /// Был значок в трее — уходим в трей, даже если запускались иначе: человек
    /// убрал окно сам, и возвращать его без спроса невежливо.
    /// </summary>
    static string[] RestartArgs(OverlayWindow overlay)
    {
        var args = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.EndsWith(FileAssoc.Extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overlay.InTray && !args.Contains("--autostart")) args.Add("--autostart");
        return [.. args];
    }

    // Бакеты, за которыми уже ходили в этом запуске: одна попытка на бакет.
    private static readonly HashSet<string> _bucketFetched = [];

    // ── Одна сессия клиента ────────────────────────────────────────────────────

    static async Task RunSessionAsync(OverlayWindow overlay, LcuCredentials creds, CancellationToken ct)
    {
        using var http = new LcuHttpClient(creds);

        // Язык клиента LoL — лучшая подсказка, чем язык Windows: человек с
        // английской системой, играющий в русский клиент, ждёт русский интерфейс
        // (у него и имена чемпионов русские). Ручной выбор это не трогает.
        _ = Task.Run(async () =>
        {
            try
            {
                var (code, body) = await http.GetAsync("/riotclient/region-locale", ct);
                if (code is < 200 or >= 300) return;
                using var doc = JsonDocument.Parse(body);
                var locale = doc.RootElement.TryGetProperty("locale", out var l) ? l.GetString() : null;
                if (Loc.FromClientLocale(locale) is { } lang)
                    overlay.Dispatcher.Invoke(() => Loc.SetLanguageAuto(lang));
            }
            catch { /* старый клиент или нет такого эндпоинта — остаёмся на языке Windows */ }
        }, ct);

        // Ждём пока LCU реально поднимется (lockfile появляется раньше первых ответов).
        // Ждём НЕ вечно: если клиент закрылся или lockfile протух (порт/пароль от
        // прошлой сессии), выходим — внешний цикл перечитает свежие креды.
        overlay.ShowStatus(Loc.T("status.lcuStarting"));
        var waitStart = DateTime.UtcNow;
        while (true)
        {
            try
            {
                var (s, _) = await http.GetAsync("/lol-gameflow/v1/availability", ct);
                if (s != 0) break;
            }
            catch (HttpRequestException)
            {
                if (!LcuFinder.IsClientRunning() || DateTime.UtcNow - waitStart > TimeSpan.FromSeconds(30))
                    return; // клиента нет или креды мертвы — начинаем заново
                await Task.Delay(2000, ct);
            }
        }

        // LCU ответил — соединение живо. Теперь слежению за окном разрешён
        // авто-возврат оверлея из трея (до этого он подавлен, чтобы на старте не
        // показывать «ожидание клиента»).
        overlay.SetLcuReady(true);

        // Ранг мог ещё не прочитаться — тогда берём запомненный с прошлого раза и
        // базу не трогаем. Раньше на его месте молча оказывался «изумруд», и
        // программа качала чужую базу, а следом свою.
        var tierBucket = await PlayerInfo.GetTierBucketAsync(http, ct)
                         ?? Settings.GetString("dataBucket");
        // Запоминаем ранг для скачивания нужной базы на следующем запуске. Если
        // ранг сменился (поднялся), а на диске база ДРУГОГО одиночного бакета —
        // она не содержит его данных, поэтому подкачаем нужную сразу. Общая
        // база ("all") содержит все бакеты — до-качка не нужна.
        if (tierBucket is not null) Settings.Set("dataBucket", tierBucket);
        var loaded = DataDb.CurrentBucket;
        if (tierBucket is not null && loaded is not null && loaded != "all" && loaded != tierBucket
            && _bucketFetched.Add(tierBucket))
            // Add() — не только проверка ранга, но и защита от петли: если
            // закачка не довела до конца (оборвалась сеть, не записался тег),
            // переподключение к клиенту не должно тянуть базу снова и снова.
            //
            // ФОНОМ, как и на запуске. Раньше тут стоял EnsureAsync с экраном
            // загрузки — и это был последний путь, по которому программа могла
            // пропасть посреди сеанса: ранг читается уже после подключения к
            // клиенту, то есть человек к тому моменту сидит в лобби.
            //
            // Заодно снимается двойная закачка. Видно в журнале живого запуска:
            // запуск начал тянуть базу золота, следом ранг прочитался — и та же
            // база поехала ВТОРОЙ раз, 64 МБ вместо 32. Замок внутри
            // UpdateInBackgroundAsync пропустит только одну; до эло игрока
            // движок поработает на соседнем бакете, что несравнимо лучше
            // экрана загрузки.
            StartDbUpdate(overlay, tierBucket, ct);

        var mastery = await PlayerInfo.GetMasteryAsync(http, ct); // пул игрока (комфорт)
        // Чемпионы аккаунта: которых нет — помечаем в рекомендациях «нет чемпиона».
        overlay.SetOwnedChampions(await PlayerInfo.GetOwnedChampionsAsync(http, ct));
        // Аккаунт (puuid) — ключ пулов чемпионов: подгружаем набор этого игрока.
        var (poolPuuid, poolName) = await PlayerInfo.GetAccountAsync(http, ct);
        PoolStore.SetAccount(poolPuuid, poolName);

        // Клиент назвал аккаунт — раньше этого синхронизироваться не с чем:
        // адрес строки на сервере считается из пароля И профиля.
        _ = SyncClient.AutoAsync(poolPuuid, "запуск", ct);

        // Импорт рун и билда прямо в клиент — по кнопкам в панели.
        overlay.ApplyRunesHandler  = (page, id, name) => RunesImporter.ApplyRunesAsync(http, page, id, name, ct);
        overlay.ApplySpellsHandler = spells => RunesImporter.ApplySpellsAsync(http, spells, ct);
        overlay.ExportBuildHandler = (core, full, alt, role, id, name) =>
            RunesImporter.ExportItemSetAsync(http, core, full, alt, role, id, name, ct);

        // Id моих текущих действий пика/бана — обновляются на каждом снимке сессии.
        // Кнопки в оверлее (навести/залочить/забанить) используют именно их.
        // -1 = действия нет (id 0 — валидный: в кастомках нумерация с нуля).
        int myPickActionId = -1;
        int myBanActionId  = -1;
        overlay.HoverHandler    = champId => RunesImporter.HoverChampionAsync(http, myPickActionId, champId, ct);
        overlay.LockHandler     = champId => RunesImporter.LockChampionAsync(http, myPickActionId, champId, ct);
        overlay.BanHoverHandler = champId => RunesImporter.HoverChampionAsync(http, myBanActionId, champId, ct);
        overlay.BanLockHandler  = champId => RunesImporter.LockChampionAsync(http, myBanActionId, champId, ct);

        RecommendationEngine? engine = null;
        // Скачанное фоном могло дождаться этого момента — сейчас движка ещё нет,
        // базу никто не держит.
        DataDb.ApplySwap();
        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is not null)
        {
            // Ранг ещё неизвестен — собираем движок на середине шкалы: без него
            // не будет ни тир-листа, ни подбора, а прочитается ранг событием
            // ниже, и движок пересоберётся уже под него.
            engine = RecommendationEngine.Create(dbPath, tierBucket ?? "emerald");
            engine.Mastery = mastery;
            overlay.SetEngine(engine);   // окну настроек пула — считать WR/дельту связок
            overlay.ShowReady();
        }
        else
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
        }

        /// <summary>
        /// Применить скачанную фоном базу и пересобрать движок.
        ///
        /// Зовётся только между драфтами. Подмена требует закрыть соединение с
        /// базой, а посреди драфта это отняло бы рекомендации на те полсекунды,
        /// ради которых программу и открывают.
        /// </summary>
        void ApplyNewDbIfReady()
        {
            if (!DataDb.SwapReady) return;
            try
            {
                var old = engine;
                engine = null;          // наружу битый движок не отдаём
                old?.Dispose();         // отпускаем файл базы
                if (!DataDb.ApplySwap()) return;   // занята — подождём ещё

                dbPath = RecommendationEngine.FindDb();
                if (dbPath is null) return;
                engine = RecommendationEngine.Create(dbPath, tierBucket ?? "emerald");
                engine.Mastery = mastery;
                overlay.SetEngine(engine);
                Log.Write("движок пересобран на свежей базе");
            }
            catch (Exception ex)
            {
                Log.Write($"пересборка движка на свежей базе не удалась: {ex.Message}");
            }
        }

        // Синк фазы геймфлоу при (ПЕРЕ)подключении. Подписка на события отдаёт
        // только БУДУЩИЕ события, а не текущее состояние: если сокет умер во время
        // игры и ожил после (клиент перезапустил UX / сеть моргнула), событие
        // «конец игры» прошло мимо — без этого синка _gameActive застрял бы в true
        // и оверлей навсегда остался бы в трее (ровно симптом «висит в процессах,
        // но окно не возвращается»). Читаем фазу явно и приводим видимость в норму.
        try
        {
            var (gfCode, gfBody) = await http.GetAsync("/lol-gameflow/v1/session", ct);
            if (gfCode == 200)
            {
                using var gdoc = JsonDocument.Parse(gfBody);
                var phase = PhaseOf(gdoc.RootElement);
                _restartSafe = phase != "ChampSelect";
                if (phase is "GameStart" or "InProgress" or "Reconnect")
                {
                    _restartSafe = false;
                    overlay.SetGameActive(true);
                    // По умолчанию прячемся: прозрачное topmost-окно мешает входу
                    // в игру. Настройкой это можно отключить.
                    if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                }
                else if (phase != "ChampSelect")
                {
                    // Только снимаем игровой флаг + задаём текст. Сам возврат из
                    // трея делает слежение за окном (уважая ручное скрытие и
                    // видимость клиента) — не форсим, иначе окно всплывало бы не к
                    // месту (в т.ч. на старте с автозапуском).
                    overlay.SetGameActive(false);
                    overlay.ShowReadyPhase(phase);
                }
            }
        }
        catch { /* геймфлоу временно недоступен — не критично */ }

        // Уже в чемп-выборе? — сразу покажем рекомендации
        var (initCode, initBody) = await http.GetAsync("/lol-champ-select/v1/session", ct);
        if (initCode == 200)
        {
            using var doc = JsonDocument.Parse(initBody);
            var draft = ChampSelectParser.Parse(doc.RootElement);
            myPickActionId = draft.MyPickActionId;
            myBanActionId  = draft.MyBanActionId;
            overlay.UpdateRecommendations(
                draft.IsAram ? engine?.RecommendAram(draft, AppSettings.Current.DraftCount)
                             : engine?.Recommend(draft, AppSettings.Current.DraftCount),
                draft, engine);
        }

        // Сменили аккаунт в клиенте — перечитываем ВСЁ, что к нему привязано.
        // Без этого панель ждала бы следующего тика фонового обновления (до
        // минуты) и показывала бы ранг с пулом прошлого игрока.
        async Task ReloadAccountAsync()
        {
            try
            {
                var (puuid, name) = await PlayerInfo.GetAccountAsync(http, ct);
                // Пусто — клиент между аккаунтами (вышли, ещё не вошли): ничего
                // не трогаем, дождёмся события о новом игроке.
                if (string.IsNullOrEmpty(puuid) || puuid == poolPuuid) return;
                poolPuuid = puuid;
                PoolStore.SetAccount(puuid, name);

                // Эло у нового аккаунта своё: и пул чемпионов, и владение
                // чемпионами, и рекомендации считаются по нему.
                // Ранг ещё не прочитался — оставляем прежний бакет. Иначе на
                // каждом старте качали бы базу «по умолчанию», а следом свою.
                var bucket = await PlayerInfo.GetTierBucketAsync(http, ct)
                             ?? Settings.GetString("dataBucket");
                if (bucket is null)
                {
                    Log.Write("ранг ещё не читается, база подождёт до следующего события");
                    return;
                }
                Settings.Set("dataBucket", bucket);
                mastery = await PlayerInfo.GetMasteryAsync(http, ct);
                overlay.SetOwnedChampions(await PlayerInfo.GetOwnedChampionsAsync(http, ct));

                if (engine is not null)
                {
                    // Бакет базы зашит в движок при создании — под новое эло его
                    // пересобираем (данные уже на диске, это дёшево).
                    if (bucket != tierBucket && dbPath is not null)
                    {
                        tierBucket = bucket;
                        // Прежний закрываем: он держал базу открытой, а SQLite в
                        // режиме WAL не отпускает файл даже на чтении. Брошенное
                        // соединение означало бы, что скачанную фоном базу уже
                        // никогда не подменить.
                        var old = engine;
                        engine = RecommendationEngine.Create(dbPath, bucket);
                        overlay.SetEngine(engine);
                        old.Dispose();
                    }
                    engine.Mastery = mastery;
                }

                overlay.RefreshPoolMode();        // пул нового игрока
                await RefreshSessionAsync();      // ник, ранг, LP, последние игры
            }
            catch { /* клиент в переходном состоянии — попробуем на следующем событии */ }
        }

        // Трекер сессии для экрана ожидания (ранг/LP/последние игры/винрейт).
        async Task RefreshSessionAsync()
        {
            try { overlay.ShowSession(await SessionTracker.RefreshAsync(http, ct)); }
            catch { /* LCU временно недоступен — пропускаем обновление */ }
        }
        await RefreshSessionAsync();

        // Периодическое обновление трекера: история матчей LCU появляется с
        // задержкой после конца игры — событие EndOfGame может прийти раньше её.
        // Раз в минуту тихо догоняем (2 лёгких GET к локальному LCU).
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
                catch (OperationCanceledException) { return; }
                await RefreshSessionAsync();
            }
        }, ct);

        // Состав пати на момент запуска: событие о лобби придёт только когда оно
        // изменится, а программу вполне могли включить с уже собранным лобби.
        async Task RefreshPartyAsync()
        {
            try
            {
                var (code, body) = await http.GetAsync("/lol-lobby/v2/lobby", ct);
                if (code == 200)
                    Party.Update(System.Text.Json.JsonDocument.Parse(body).RootElement);
            }
            catch { /* лобби нет или клиент занят — узнаем из события */ }
        }
        await RefreshPartyAsync();

        await using var socket = new LcuEventSocket(creds);
        await socket.ConnectAsync(ct);

        var lastHash = "";
        bool draftUnhidden = false; // сбрасывали ли ручное скрытие на этом драфте
        CancellationTokenSource? hideCts = null; // запланированное скрытие в трей
        // История ховеров своей команды за текущий драфт (cellId → чемпионы):
        // союзники в фазе банов перебирают несколько чемпионов — баны считаются
        // по всему показанному пулу, а не только по текущему наведению.
        var hoverHistory = new Dictionary<int, HashSet<int>>();

        await foreach (var ev in socket.ReadEventsAsync(ct))
        {
            switch (ev.Uri)
            {
                case "/lol-summoner/v1/current-summoner":
                    await ReloadAccountAsync();
                    break;

                // Состав пати: кто со мной в лобби. Нужен, чтобы в драфте узнать
                // напарника по человеку. Delete (лобби закрылось) пропускаем —
                // оно исчезает при переходе в чемп-селект, то есть ровно тогда,
                // когда напарник и нужен.
                case "/lol-lobby/v2/lobby":
                    if (ev.EventType != "Delete") Party.Update(ev.Data);
                    break;

                case "/lol-gameflow/v1/session":
                    var phase = PhaseOf(ev.Data);
                    // Программу могли запустить, когда лобби уже собрано: события
                    // о нём тогда не будет, а начальный опрос пришёлся на момент
                    // без лобби. Пока состав неизвестен — переспрашиваем на каждой
                    // смене фазы, это один лёгкий запрос к локальному клиенту.
                    if (!Party.Known && phase is "Lobby" or "Matchmaking" or "ReadyCheck" or "ChampSelect")
                        await RefreshPartyAsync();
                    // Очередь лобби сменилась → подставляем режим пула, запомненный
                    // для НЕЁ (соло-дуо не переезжает во флекс, и наоборот).
                    if (QueueKeyOf(ev.Data) is { } qk && PoolStore.SetQueue(qk))
                        overlay.RefreshPoolMode();
                    // Фаза геймфлоу — единственный источник правды для трея.
                    if (phase is "GameStart" or "InProgress" or "Reconnect")
                    {
                        _restartSafe = false;      // не перезапускаемся посреди игры
                        // Игра идёт — оверлей скрыт в трее (не разворачиваем ни при каких
                        // событиях, иначе прозрачное topmost-окно блокирует вход в игру).
                        overlay.SetGameActive(true);
                        if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                        lastHash = "";
                    }
                    else if (phase != "ChampSelect")
                    {
                        // Драфта нет — самое время подменить скачанную фоном базу
                        // и поставить скачанное обновление.
                        _restartSafe = true;
                        ApplyNewDbIfReady();
                        ApplyUpdateIfIdle(overlay);
                        // Меню/лобби/конец игры — возвращаем оверлей из трея.
                        overlay.SetGameActive(false);
                        overlay.RestoreFromTray();
                        overlay.UpdateRecommendations(null, null);
                        // Меню/лобби/конец игры — программа простаивает: показываем
                        // сноску о программе и карусель советов вместо «Готов».
                        overlay.ShowReadyPhase(phase);
                        // После игры (EndOfGame) ранг/LP обновились — перечитываем трекер.
                        await RefreshSessionAsync();
                        // И отдаём свежую историю на сервер. Главный момент из
                        // всех: именно здесь появляются новые игры и связки.
                        _ = SyncClient.AutoAsync(PoolStore.AccountPuuid, "после игры", ct);
                        lastHash = "";
                        draftUnhidden = false; // новый драфт снова снимет ручное скрытие
                        hoverHistory.Clear();
                    }
                    // ChampSelect: НЕ восстанавливаем здесь — показ управляется
                    // обработчиком champ-select и флагом _inTray. Иначе разворачивали бы
                    // окно обратно во время FINALIZATION (за 5с до игры).
                    break;

                case "/lol-champ-select/v1/session":
                    if (ev.EventType == "Delete")
                    {
                        // Конец драфта: в игру или дродж. НЕ восстанавливаем из трея —
                        // видимостью управляет фаза геймфлоу (Lobby/EndOfGame вернёт окно,
                        // GameStart оставит скрытым). Иначе при входе в игру окно всплывёт.
                        hideCts?.Cancel(); hideCts = null;
                        overlay.UpdateRecommendations(null, null);
                        // Не просто статус: окно должно вернуться на экран готовности
                        // той фазы, в которой клиент уже находится (см. DraftEnded).
                        overlay.DraftEnded(); // подавится, если в трее
                        _restartSafe = true;  // драфт кончился — можно и то, и другое
                        ApplyNewDbIfReady();
                        ApplyUpdateIfIdle(overlay);
                        lastHash = "";
                        draftUnhidden = false; // новый драфт снова снимет ручное скрытие
                        hoverHistory.Clear();
                    }
                    else
                    {
                        _restartSafe = false;   // идёт драфт — не до перезапуска
                        var draft = ChampSelectParser.Parse(ev.Data);
                        myPickActionId = draft.MyPickActionId;
                        myBanActionId  = draft.MyBanActionId;

                        // За 5 секунд до конца финализации прячем оверлей в трей.
                        var (timerPhase, timeLeftMs) = ChampSelectTimer(ev.Data);
                        if (timerPhase == "FINALIZATION" && hideCts == null)
                        {
                            hideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var delay = Math.Max(0, timeLeftMs - 5000);
                            var token = hideCts.Token;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                                    overlay.SetGameActive(true); // вход в игру — подавляем авто-показ
                                    if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                                }
                                catch (OperationCanceledException) { }
                            }, token);
                        }
                        else if (timerPhase != "FINALIZATION")
                        {
                            // Активный драфт (не финализация) — авто-показ оверлея разрешён,
                            // чтобы окно надёжно всплыло на новом чемп-селекте после игры.
                            overlay.SetGameActive(false);
                            // Один раз за драфт снимаем ручное скрытие: закрыл окно
                            // раньше — на новом чемп-селекте оно всё равно вернётся
                            // (но закрыть его В ТЕЧЕНИЕ драфта по-прежнему можно).
                            if (!draftUnhidden) { overlay.AllowAutoShow(); draftUnhidden = true; }
                        }

                        var hash = DraftHash(draft);
                        if (hash == lastHash) break;
                        lastHash = hash;

                        // Копим показанных командой чемпионов (пул на этот драфт).
                        foreach (var p in draft.MyTeam)
                        {
                            var idc = p.EffectiveChampionId;
                            if (idc == 0) continue;
                            if (!hoverHistory.TryGetValue(p.CellId, out var setc))
                                hoverHistory[p.CellId] = setc = [];
                            setc.Add(idc);
                        }

                        if (draft.IsAram)
                        {
                            overlay.UpdateRecommendations(
                                engine?.RecommendAram(draft, AppSettings.Current.DraftCount), draft, engine);
                            overlay.HideRunes();               // руны не в этом режиме
                        }
                        else if (draft.InBanPhase)
                        {
                            overlay.UpdateBans(engine?.RecommendBans(draft, hoverHistory), draft, engine);
                            overlay.HideRunes();               // руны — на этапе пика, не банов
                            _runesShownFor = "";               // сбросить, чтобы после банов показать заново
                        }
                        else
                        {
                            var recs = engine?.Recommend(draft, AppSettings.Current.DraftCount);
                            overlay.UpdateRecommendations(recs, draft, engine);

                            // Руны: панель для МОЕГО чемпиона (залоченного или наведённого).
                            // Плюс предзагрузка топ-кандидатов — чтобы к моменту пика
                            // данные уже лежали в памяти и панель появилась мгновенно.
                            if (recs is { Count: > 0 })
                                RunesClient.Prefetch(recs.Select(r => r.ChampionId),
                                                     RecommendationEngine.LcuToDbRole(draft.MyPosition), ct);

                            // И сборки ВРАГОВ: по ним видно, кто из них правда
                            // купит предмет, которым нас «накажут». Без этого
                            // строка контр-предметов судит по одному классу и
                            // предлагает Посох серафима тем, кто его не берёт.
                            RunesClient.PrefetchRoles(
                                draft.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                     .Select(p => (p.EffectiveChampionId,
                                                   RecommendationEngine.LcuToDbRole(p.Position))), ct);
                            _ = UpdateRunesAsync(overlay, draft, ct);
                        }
                    }
                    break;
            }
        }

        hideCts?.Cancel();
        engine?.Dispose();
    }

    // Достаёт фазу таймера чемп-селекта и остаток времени (мс).
    static (string phase, double leftMs) ChampSelectTimer(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("timer", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            var ph = t.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? "" : "";
            double left = 0;
            if (t.TryGetProperty("adjustedTimeLeftInPhase", out var l) && l.ValueKind == JsonValueKind.Number)
                left = l.GetDouble();
            else if (t.TryGetProperty("timeLeftInPhase", out var l2) && l2.ValueKind == JsonValueKind.Number)
                left = l2.GetDouble();
            return (ph, left);
        }
        return ("", 0);
    }

    /// <summary>
    /// Источник обновлений — СТАТИЧЕСКИЙ фид, а не GitHub API.
    ///
    /// Почему не GithubSource: он ходит в api.github.com, а тот без токена даёт
    /// 60 запросов в час НА IP-АДРЕС. За общим IP (общежитие, интернет-кафе,
    /// мобильный оператор с NAT) эти 60 делят сотни чужих людей — и обновления
    /// у пользователя просто перестают приходить, молча. Мы дважды напоролись на
    /// это сами.
    ///
    /// Прямые ссылки на файлы релизов (CDN GitHub) лимитом НЕ ограничены.
    /// Тег `latest` — катящийся: release.ps1 перезаливает в него фид и пакеты,
    /// поэтому адрес всегда один и тот же.
    /// </summary>
    static Velopack.Sources.IUpdateSource UpdateSource =>
        new Velopack.Sources.SimpleWebSource(
            "https://github.com/28maryshev/counterplay/releases/download/latest/");

    // Фоновая проверка обновлений: первый заход через 10 минут, дальше раз в час.
    // С автозапуском программа висит в
    // трее сутками — без этого она узнала бы о новой версии только при следующем
    // запуске Windows. Обновление НЕ перезапускает приложение на ходу (человек
    // может быть в драфте): скачиваем и применяем при выходе.
    // Версия сборки: у установленной берём ту, что знает Velopack, у dev-сборки —
    // из атрибутов самой сборки.
    static string CurrentVersion()
    {
        try
        {
            var mgr = new UpdateManager(UpdateSource);
            if (mgr.IsInstalled && mgr.CurrentVersion is { } v) return v.ToString();
        }
        catch { /* не установлено через Velopack */ }
        var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return asm is null ? "dev" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
    }

    static void StartUpdateWatcher(OverlayWindow overlay, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            // Первый заход — почти сразу: проверка ушла со старта сюда, и тянуть
            // с ней десять минут значило бы, что человек эти десять минут сидит
            // на старой версии. Пять секунд — чтобы не спорить за сеть с
            // разогревом и не мешать окну появиться.
            var delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromHours(1);

                try
                {
                    var mgr = new UpdateManager(UpdateSource);
                    if (!mgr.IsInstalled) return;

                    // Признак «скачано и ждёт перезапуска» спрашиваем У VELOPACK, а не
                    // помним по факту своей же загрузки. Раньше уведомление показывалось
                    // ровно один раз — сразу после скачивания; если тот заход падал
                    // (сеть, лимит GitHub) или обновление скачал прошлый запуск, человек
                    // не узнавал о новой версии никогда.
                    if (mgr.UpdatePendingRestart is { } staged)
                    {
                        var ready = staged.Version?.ToString() ?? "";

                        // Уже на ней: пакет мог остаться лежать после УДАЧНОЙ
                        // установки. Без этой ветки отметка о попытке приняла бы
                        // удачу за неудачу и навсегда запретила бы эту версию.
                        if (ready == mgr.CurrentVersion?.ToString()) { ForgetUpdateAttempt(); continue; }

                        overlay.ShowUpdateReady(ready);
                        if (AlreadyFailed(ready))
                        {
                            Log.Write($"версия {ready} однажды не поставилась — сама больше не пробую");
                            continue;
                        }
                        // Качать нечего — но и ждать следующего запуска незачем:
                        // ставим в первую тихую минуту, как и всё остальное.
                        _updateStaged = staged;
                        ApplyUpdateIfIdle(overlay);
                        continue;
                    }

                    // Ставить нечего — значит, прошлая попытка удалась. Отметку
                    // снимаем, иначе она запретила бы и честное обновление до
                    // той же версии.
                    ForgetUpdateAttempt();

                    var info = await mgr.CheckForUpdatesAsync();
                    if (info == null) { Log.Write("обновлений нет (фоновая проверка)"); continue; }
                    var v = info.TargetFullRelease?.Version.ToString() ?? "?";
                    Log.Write($"фоновая проверка: есть версия {v}, качаю");
                    // Полосой внизу сайдбара, у версии — там же, где идёт
                    // загрузка базы. Раньше обновление качалось совсем молча:
                    // программа что-то тянула, а по ней этого было не видно.
                    await mgr.DownloadUpdatesAsync(info, pct =>
                        overlay.ShowSideProgress(
                            Loc.T("status.downloadingUpdate", pct, ""), pct / 100.0, "app"));
                    overlay.HideSideProgress("app");
                    Log.Write($"версия {v} скачана");
                    overlay.ShowUpdateReady(v);

                    // Ставим при первой возможности. Раньше скачанное ждало
                    // СЛЕДУЮЩЕГО ЗАПУСКА, а программа с автозапуском живёт
                    // неделями — человек всё это время сидел на старой версии,
                    // хотя новая лежала у него на диске.
                    _updateStaged = info.TargetFullRelease;
                    ApplyUpdateIfIdle(overlay);
                }
                catch (Exception ex)
                {
                    // Офлайн, лимит GitHub, битый фид — на следующем круге
                    // попробуем снова. Но молчать нельзя: раньше сюда уходила
                    // ЛЮБАЯ причина, по которой человек не получал обновление,
                    // и по жалобе «не качается» смотреть было не на что.
                    Log.Write($"фоновая проверка обновлений не удалась: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>
    /// Применить обновление и перезапуститься — без чужого окна поверх клиента.
    ///
    /// ApplyUpdatesAndRestart поднимает собственное окно Velopack: «Установка
    /// обновления, пожалуйста, подождите». Программа с автозапуском стартует
    /// вместе с клиентом LoL, и это окно вылезало поверх игры — притом что своё
    /// состояние мы и так показываем в сайдбаре.
    ///
    /// WaitExitThenApplyUpdates делает то же самое молча, но ЖДЁТ нашего
    /// выхода — и ждёт всего 60 секунд. Поэтому сразу за ним гасим программу:
    /// закрываем окно (на этом снимается значок из трея) и выходим. Мгновенный
    /// выход тут не грубость: прежний метод делал ровно то же, только со своим
    /// окном впридачу.
    /// </summary>
    static void ApplySilently(OverlayWindow overlay, UpdateManager mgr, VelopackAsset? asset)
    {
        // Помечаем попытку. Если установка не удастся, программа поднимется на
        // ПРЕЖНЕЙ версии, снова увидит готовое обновление и пойдёт ставить его
        // опять — бесконечный круг «ставлю — перезапуск — не встало», без
        // единого сообщения человеку. По этой отметке следующий запуск поймёт,
        // что эту версию уже пробовал.
        Settings.Set("updateTried", asset?.Version?.ToString() ?? "");
        mgr.WaitExitThenApplyUpdates(asset, silent: true, restart: true,
                                     restartArgs: RestartArgs(overlay));
        try { overlay.Dispatcher.Invoke(overlay.Close); }
        catch { /* окно уже закрыто — не повод не выходить */ }
        Environment.Exit(0);
    }

    /// <summary>
    /// Пробовали ли уже поставить эту версию — и остались на прежней.
    ///
    /// Значит, установка не прошла: сама программа второй раз за неё не
    /// берётся, иначе получится круг из перезапусков. Человеку версия
    /// покажется готовой — поставит вручную или переустановит.
    /// </summary>
    static bool AlreadyFailed(string? version) =>
        !string.IsNullOrEmpty(version) && Settings.GetString("updateTried") == version;

    /// Снять отметку о неудачной попытке. Пишем, только если есть что снимать:
    /// иначе файл настроек трогался бы на каждом запуске без всякой нужды.
    static void ForgetUpdateAttempt()
    {
        if (!string.IsNullOrEmpty(Settings.GetString("updateTried")))
            Settings.Set("updateTried", "");
    }

    // Очередь текущего лобби из события геймфлоу: gameData.queue.id → наш ключ
    // (solo/flex/normal/aram). Нужна, чтобы режим пула помнился отдельно по очередям.
    static string? QueueKeyOf(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("gameData", out var gd) || gd.ValueKind != JsonValueKind.Object) return null;
        if (!gd.TryGetProperty("queue", out var q) || q.ValueKind != JsonValueKind.Object) return null;
        if (!q.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) return null;
        return SessionTracker.QueueOf(id.GetInt32());
    }

    static string PhaseOf(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";

    // В хэш входит EffectiveChampionId (залок ИЛИ ховер) — иначе наведение
    // чемпиона не меняло бы хэш и рекомендации не пересчитывались бы до лока.
    // MyPickActionId/MyPickInProgress тоже обязаны быть в хэше: свап позицией/
    // очередью и наступление моего хода не меняют составы, но без перерисовки
    // оверлей держит устаревший драфт — кнопка лока не появляется или лочит
    // по старому action id.
    static string DraftHash(DraftState s) =>
        string.Join(",", s.MyTeam.Select(p  => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.TheirTeam.Select(p => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.MyTeamBans) + "|" +
        string.Join(",", s.TheirTeamBans) + "|" +
        string.Join(",", s.Bench) + "|" +
        (s.InBanPhase ? "ban" : "pick") + "|" +
        $"{s.MyPickActionId}:{s.MyPickInProgress}|" +
        string.Join(",", s.ActiveCells) + $":{s.FirstPickCell}";
}
