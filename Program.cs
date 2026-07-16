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
        using var single = new Mutex(initiallyOwned: true, "Counterplay.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowSignal);
                show.Set(); // существующий экземпляр развернётся из трея
            }
            catch { /* сигнал не дошёл — просто выходим */ }
            return;
        }

        Loc.Init(); // язык интерфейса: сохранённый выбор → язык Windows → English

        // Автозапуск с Windows: включаем при первом старте новой версии и один раз
        // сообщаем об этом в панели (молча прописываться в автозагрузку — дурной тон).
        var autostartNotice = Autostart.ApplyOnStartup();

        _ = Telemetry.PingAsync(); // анонимный пинг для статистики активных пользователей

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var app     = new System.Windows.Application();
        var overlay = new OverlayWindow();

        // Автозапуск с Windows: стартуем свёрнутыми в трей. Показывать окно
        // «LCU is starting…» при входе в систему — навязчиво; оверлей появится
        // сам, когда запустится клиент.
        if (args.Contains("--autostart")) overlay.StartInTray();
        else                              overlay.Show();

        if (autostartNotice) overlay.ShowAutostartNotice();

        // Повторный запуск (клик по ярлыку, пока мы в трее) — разворачиваем окно.
        StartShowSignalListener(overlay, cts.Token);

        // Тестовый режим (dotnet run test): песочница-драфт без клиента LoL.
        var testMode = args.Contains("test") || args.Contains("--test");

        var lcuTask = Task.Run(async () =>
        {
            try
            {
                if (testMode) await TestMode.RunAsync(overlay, cts.Token);
                else          await RunLcuAsync(overlay, args, cts.Token);
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
        var champ = draft?.Me?.EffectiveChampionId ?? 0;
        if (draft is null || champ == 0 || draft.IsAram)
        {
            _runesShownFor = "";
            overlay.HideRunes();
            return;
        }

        // Роль: из драфта, иначе основная роль чемпиона (custom games/блайнд не
        // раскрывают позицию — но руны показать всё равно нужно).
        var role = RunesClient.ResolveRole(champ, RecommendationEngine.LcuToDbRole(draft.MyPosition));
        if (role is null || !RunesClient.Has(champ, role))
        {
            overlay.HideRunes();
            return;
        }

        var opponent = draft.DirectOpponent?.EffectiveChampionId;
        var key = $"{champ}:{role}:{opponent ?? 0}";
        if (key == _runesShownFor) return;   // уже показано для этой связки
        _runesShownFor = key;

        var stats = await RunesClient.GetAsync(champ, role, ct);
        overlay.ShowRunes(stats, champ, DataDragon.Name(champ), role, opponent);
    }

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
                if (handle.WaitOne(500)) overlay.RestoreFromTray(force: true);
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
        var lockfilePath = args.FirstOrDefault(a => !a.StartsWith('-'));

        // Проверка обновлений при каждом запуске (только для установленной версии).
        await CheckForUpdatesAsync(overlay, ct);
        StartUpdateWatcher(ct); // и дальше — раз в 4 часа (программа живёт в трее сутками)

        // Data Dragon + иконки грузим один раз при старте
        overlay.ShowStatus(Loc.T("status.loadingChamps"));
        await DataDragon.LoadAsync(Loc.DDragonLocale, ct);

        overlay.ShowStatus(Loc.T("status.loadingIcons"));
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        await RoleIcons.PreloadAsync(ct);
        await ItemIcons.PreloadAsync(ct); // иконки контр-предметов

        // Руны: справочник (имена/иконки) + названия предметов + манифест сервера.
        // Если данных на сервере ещё нет — панель просто не появится, фича
        // включится сама, когда наберётся выборка.
        await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
        await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, ct);
        await RunesClient.LoadManifestAsync(ct);

        // Гарантируем наличие data.db (скачиваем/обновляем из дата-релиза с прогрессом).
        await DataDb.EnsureAsync((msg, frac) => overlay.ShowProgress(msg, frac), ct);

        // Внешний цикл — переподключение при перезапуске клиента
        while (!ct.IsCancellationRequested)
        {
            overlay.ShowStatus(Loc.T("status.waitingClient"));
            var creds = await LockfileReader.WaitForAsync(lockfilePath, ct);

            try
            {
                await RunSessionAsync(overlay, creds, ct);
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
                overlay.ShowStatus(Loc.T("status.connLost"));
                try { await Task.Delay(3000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    // ── Одна сессия клиента ────────────────────────────────────────────────────

    static async Task RunSessionAsync(OverlayWindow overlay, LcuCredentials creds, CancellationToken ct)
    {
        using var http = new LcuHttpClient(creds);

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

        var tierBucket = await PlayerInfo.GetTierBucketAsync(http, ct);
        var mastery    = await PlayerInfo.GetMasteryAsync(http, ct); // пул игрока (комфорт)

        // Импорт рун и билда прямо в клиент — по кнопкам в панели.
        overlay.ApplyRunesHandler  = (page, name) => RunesImporter.ApplyRunesAsync(http, page, name, ct);
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
        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is not null)
        {
            engine = RecommendationEngine.Create(dbPath, tierBucket);
            engine.Mastery = mastery;
            overlay.ShowReady();
        }
        else
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
        }

        // Уже в чемп-выборе? — сразу покажем рекомендации
        var (initCode, initBody) = await http.GetAsync("/lol-champ-select/v1/session", ct);
        if (initCode == 200)
        {
            using var doc = JsonDocument.Parse(initBody);
            var draft = ChampSelectParser.Parse(doc.RootElement);
            myPickActionId = draft.MyPickActionId;
            myBanActionId  = draft.MyBanActionId;
            overlay.UpdateRecommendations(
                draft.IsAram ? engine?.RecommendAram(draft) : engine?.Recommend(draft), draft, engine);
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

        await using var socket = new LcuEventSocket(creds);
        await socket.ConnectAsync(ct);

        var lastHash = "";
        CancellationTokenSource? hideCts = null; // запланированное скрытие в трей
        // История ховеров своей команды за текущий драфт (cellId → чемпионы):
        // союзники в фазе банов перебирают несколько чемпионов — баны считаются
        // по всему показанному пулу, а не только по текущему наведению.
        var hoverHistory = new Dictionary<int, HashSet<int>>();

        await foreach (var ev in socket.ReadEventsAsync(ct))
        {
            switch (ev.Uri)
            {
                case "/lol-gameflow/v1/session":
                    var phase = PhaseOf(ev.Data);
                    // Фаза геймфлоу — единственный источник правды для трея.
                    if (phase is "GameStart" or "InProgress" or "Reconnect")
                    {
                        // Игра идёт — оверлей скрыт в трее (не разворачиваем ни при каких
                        // событиях, иначе прозрачное topmost-окно блокирует вход в игру).
                        overlay.SetGameActive(true);
                        overlay.HideToTray();
                        lastHash = "";
                    }
                    else if (phase != "ChampSelect")
                    {
                        // Меню/лобби/конец игры — возвращаем оверлей из трея.
                        overlay.SetGameActive(false);
                        overlay.RestoreFromTray();
                        overlay.UpdateRecommendations(null, null);
                        // Меню/лобби/конец игры — программа простаивает: показываем
                        // сноску о программе и карусель советов вместо «Готов».
                        overlay.ShowReadyPhase(phase);
                        // После игры (EndOfGame) ранг/LP обновились — перечитываем трекер.
                        await RefreshSessionAsync();
                        lastHash = "";
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
                        overlay.ShowStatus(Loc.T("status.waitNextDraft")); // подавится, если в трее
                        lastHash = "";
                        hoverHistory.Clear();
                    }
                    else
                    {
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
                                    overlay.HideToTray();
                                }
                                catch (OperationCanceledException) { }
                            }, token);
                        }
                        else if (timerPhase != "FINALIZATION")
                        {
                            // Активный драфт (не финализация) — авто-показ оверлея разрешён,
                            // чтобы окно надёжно всплыло на новом чемп-селекте после игры.
                            overlay.SetGameActive(false);
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
                            overlay.UpdateRecommendations(engine?.RecommendAram(draft), draft, engine);
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
                            var recs = engine?.Recommend(draft);
                            overlay.UpdateRecommendations(recs, draft, engine);

                            // Руны: панель для МОЕГО чемпиона (залоченного или наведённого).
                            // Плюс предзагрузка топ-кандидатов — чтобы к моменту пика
                            // данные уже лежали в памяти и панель появилась мгновенно.
                            if (recs is { Count: > 0 })
                                RunesClient.Prefetch(recs.Select(r => r.ChampionId),
                                                     RecommendationEngine.LcuToDbRole(draft.MyPosition), ct);
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

    // Фоновая проверка обновлений раз в 4 часа. С автозапуском программа висит в
    // трее сутками — без этого она узнала бы о новой версии только при следующем
    // запуске Windows. Обновление НЕ перезапускает приложение на ходу (человек
    // может быть в драфте): скачиваем и применяем при выходе.
    static void StartUpdateWatcher(CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromHours(4), ct); }
                catch (OperationCanceledException) { return; }
                try
                {
                    var mgr = new UpdateManager(UpdateSource);
                    if (!mgr.IsInstalled) return;
                    var info = await mgr.CheckForUpdatesAsync();
                    if (info == null) continue;
                    await mgr.DownloadUpdatesAsync(info);
                    // Применить при выходе, без перезапуска на ходу.
                    mgr.WaitExitThenApplyUpdates(info, silent: true, restart: false);
                }
                catch { /* офлайн / лимит GitHub — попробуем через 4 часа */ }
            }
        }, ct);
    }

    // Проверка/применение обновлений из GitHub Releases при каждом запуске.
    // Для dev-сборки (не установленной через Velopack) — тихо пропускается.
    static async Task CheckForUpdatesAsync(OverlayWindow overlay, CancellationToken ct)
    {
        try
        {
            var mgr = new UpdateManager(UpdateSource);
            if (!mgr.IsInstalled) return; // запущено из dev-сборки — не обновляемся

            overlay.ShowStatus(Loc.T("status.checkingUpdates"));
            var info = await mgr.CheckForUpdatesAsync();
            if (info == null) return; // актуальная версия

            // Загрузка обновления со строкой состояния и скоростью.
            var total = info.TargetFullRelease?.Size ?? 0L;
            var sw = Stopwatch.StartNew();
            long lastBytes = 0; var lastT = TimeSpan.Zero;
            await mgr.DownloadUpdatesAsync(info, pct =>
            {
                var frac = pct / 100.0;
                var now  = sw.Elapsed;
                double bps = 0;
                if (total > 0 && (now - lastT).TotalSeconds >= 0.2)
                {
                    var bytes = (long)(frac * total);
                    bps = (bytes - lastBytes) / (now - lastT).TotalSeconds;
                    lastBytes = bytes; lastT = now;
                }
                var speed = bps > 0 ? $" · {DataDb.FormatSpeed(bps)}" : "";
                overlay.ShowProgress(Loc.T("status.downloadingUpdate", pct, speed), frac);
            });
            // Скачивание завершено — Velopack дальше распаковывает/проверяет молча
            // (тот самый «застрявший» хвост). Показываем неопределённую стадию.
            overlay.ShowProgressBusy(Loc.T("status.applyingUpdate"));
            // Применяем и перезапускаемся в новую версию.
            mgr.ApplyUpdatesAndRestart(info);
        }
        catch { /* офлайн / нет релизов — работаем на текущей версии */ }
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
