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

        Loc.Init(); // язык интерфейса: сохранённый выбор → язык Windows → English

        _ = Telemetry.PingAsync(); // анонимный пинг для статистики активных пользователей

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var app     = new System.Windows.Application();
        var overlay = new OverlayWindow();
        overlay.Show();

        var lcuTask = Task.Run(async () =>
        {
            try   { await RunLcuAsync(overlay, args, cts.Token); }
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

    // ── LCU-цикл: Data Dragon один раз, потом перебирает сессии LCU ─────────

    static async Task RunLcuAsync(OverlayWindow overlay, string[] args, CancellationToken ct)
    {
        // Путь к lockfile: из аргумента, иначе null → автопоиск клиента на любом ПК.
        var lockfilePath = args.Length > 0 ? args[0] : null;

        // Проверка обновлений при каждом запуске (только для установленной версии).
        await CheckForUpdatesAsync(overlay, ct);

        // Data Dragon + иконки грузим один раз при старте
        overlay.ShowStatus(Loc.T("status.loadingChamps"));
        await DataDragon.LoadAsync(Loc.DDragonLocale, ct);

        overlay.ShowStatus(Loc.T("status.loadingIcons"));
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        await RoleIcons.PreloadAsync(ct);

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

        // Ждём пока LCU реально поднимется (lockfile появляется раньше первых ответов)
        overlay.ShowStatus(Loc.T("status.lcuStarting"));
        while (true)
        {
            try
            {
                var (s, _) = await http.GetAsync("/lol-gameflow/v1/availability", ct);
                if (s != 0) break;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(2000, ct);
            }
        }

        var tierBucket = await PlayerInfo.GetTierBucketAsync(http, ct);
        var mastery    = await PlayerInfo.GetMasteryAsync(http, ct); // пул игрока (комфорт)

        RecommendationEngine? engine = null;
        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is not null)
        {
            engine = RecommendationEngine.Create(dbPath, tierBucket);
            engine.Mastery = mastery;
            overlay.ShowReady(Loc.T("status.readyLine", engine.TierBucket, engine.PatchDisplay));
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
            overlay.UpdateRecommendations(engine?.Recommend(draft), draft, engine);
        }

        await using var socket = new LcuEventSocket(creds);
        await socket.ConnectAsync(ct);

        var lastHash = "";
        CancellationTokenSource? hideCts = null; // запланированное скрытие в трей

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
                        overlay.HideToTray();
                        lastHash = "";
                    }
                    else if (phase != "ChampSelect")
                    {
                        // Меню/лобби/конец игры — возвращаем оверлей из трея.
                        overlay.RestoreFromTray();
                        overlay.UpdateRecommendations(null, null);
                        // Меню/лобби/конец игры — программа простаивает: показываем
                        // сноску о программе и карусель советов вместо «Готов».
                        overlay.ShowReady(Loc.T("status.readyPhase", phase));
                        lastHash = "";
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
                    }
                    else
                    {
                        var draft = ChampSelectParser.Parse(ev.Data);

                        // За 5 секунд до конца финализации прячем оверлей в трей.
                        var (timerPhase, timeLeftMs) = ChampSelectTimer(ev.Data);
                        if (timerPhase == "FINALIZATION" && hideCts == null)
                        {
                            hideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var delay = Math.Max(0, timeLeftMs - 5000);
                            var token = hideCts.Token;
                            _ = Task.Run(async () =>
                            {
                                try { await Task.Delay(TimeSpan.FromMilliseconds(delay), token); overlay.HideToTray(); }
                                catch (OperationCanceledException) { }
                            }, token);
                        }

                        var hash = DraftHash(draft);
                        if (hash == lastHash) break;
                        lastHash = hash;
                        if (draft.InBanPhase)
                            overlay.UpdateBans(engine?.RecommendBans(draft), draft);
                        else
                            overlay.UpdateRecommendations(engine?.Recommend(draft), draft, engine);
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

    // Проверка/применение обновлений из GitHub Releases при каждом запуске.
    // Для dev-сборки (не установленной через Velopack) — тихо пропускается.
    static async Task CheckForUpdatesAsync(OverlayWindow overlay, CancellationToken ct)
    {
        try
        {
            var mgr = new UpdateManager(new GithubSource("https://github.com/28maryshev/counterplay", null, false));
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
    static string DraftHash(DraftState s) =>
        string.Join(",", s.MyTeam.Select(p  => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.TheirTeam.Select(p => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.MyTeamBans) + "|" +
        string.Join(",", s.TheirTeamBans) + "|" +
        (s.InBanPhase ? "ban" : "pick");
}
