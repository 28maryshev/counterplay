using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;

class Program
{
    // Подключаем WPF-процесс к консоли родителя — иначе Ctrl+C не работает при dotnet run
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        AttachConsole(-1); // -1 = ATTACH_PARENT_PROCESS

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var app     = new Application();
        var overlay = new OverlayWindow();
        overlay.Show();

        var lcuTask = Task.Run(async () =>
        {
            try   { await RunLcuAsync(overlay, args, cts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                overlay.Dispatcher.Invoke(() =>
                    MessageBox.Show(ex.Message, "Counterplay", MessageBoxButton.OK, MessageBoxImage.Error));
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
        var lockfilePath = args.Length > 0 ? args[0] : LockfileReader.DefaultPath;

        // Data Dragon + иконки грузим один раз при старте
        overlay.ShowStatus("Загружаю данные чемпионов…");
        await DataDragon.LoadAsync(ct);

        overlay.ShowStatus("Загружаю иконки чемпионов…");
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        await RoleIcons.PreloadAsync(ct);

        // Внешний цикл — переподключение при перезапуске клиента
        while (!ct.IsCancellationRequested)
        {
            overlay.ShowStatus("Жду запуска League of Legends…");
            var creds = await LockfileReader.WaitForAsync(lockfilePath, ct);

            try
            {
                await RunSessionAsync(overlay, creds, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (HttpRequestException)
            {
                // Клиент закрылся или lockfile устарел — ждём нового
                overlay.ShowStatus("Соединение потеряно, жду перезапуска…");
                await Task.Delay(3000, ct);
            }
        }
    }

    // ── Одна сессия клиента ────────────────────────────────────────────────────

    static async Task RunSessionAsync(OverlayWindow overlay, LcuCredentials creds, CancellationToken ct)
    {
        using var http = new LcuHttpClient(creds);

        // Ждём пока LCU реально поднимется (lockfile появляется раньше первых ответов)
        overlay.ShowStatus("LCU запускается…");
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

        RecommendationEngine? engine = null;
        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is not null)
        {
            engine = RecommendationEngine.Create(dbPath, tierBucket);
            overlay.ShowStatus($"Готов · {engine.TierBucket} · {engine.PatchDisplay}");
        }
        else
        {
            overlay.ShowStatus("data.db не найден — запусти pipeline/collect.py");
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

        await foreach (var ev in socket.ReadEventsAsync(ct))
        {
            switch (ev.Uri)
            {
                case "/lol-gameflow/v1/session":
                    var phase = PhaseOf(ev.Data);
                    if (phase != "ChampSelect")
                    {
                        overlay.UpdateRecommendations(null, null);
                        overlay.ShowStatus($"Фаза: {phase}");
                        lastHash = "";
                    }
                    break;

                case "/lol-champ-select/v1/session":
                    if (ev.EventType == "Delete")
                    {
                        overlay.UpdateRecommendations(null, null);
                        overlay.ShowStatus("Жду следующего чемп-выбора…");
                        lastHash = "";
                    }
                    else
                    {
                        var draft = ChampSelectParser.Parse(ev.Data);
                        var hash  = DraftHash(draft);
                        if (hash == lastHash) break;
                        lastHash = hash;
                        overlay.UpdateRecommendations(engine?.Recommend(draft), draft, engine);
                    }
                    break;
            }
        }

        engine?.Dispose();
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
        string.Join(",", s.TheirTeamBans);
}
