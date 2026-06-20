using System.Text;
using System.Text.Json;
using Counterplay;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var lockfilePath = args.Length > 0 ? args[0] : LockfileReader.DefaultPath;

try
{
    await RunAsync(lockfilePath, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("\nЗавершение.");
}

static async Task RunAsync(string lockfilePath, CancellationToken ct)
{
    var creds = await LockfileReader.WaitForAsync(lockfilePath, ct);
    Console.WriteLine($"Клиент найден: порт {creds.Port}.\n");

    await DataDragon.LoadAsync(ct);

    using var http = new LcuHttpClient(creds);

    // Определяем ранг игрока и подбираем бакет данных
    var tierBucket = await PlayerInfo.GetTierBucketAsync(http, ct);
    Console.WriteLine($"Ранг игрока → бакет: {tierBucket}");

    // Ищем data.db
    var dbPath = RecommendationEngine.FindDb();
    RecommendationEngine? engine = null;
    if (dbPath is not null)
    {
        engine = RecommendationEngine.Create(dbPath, tierBucket);
        Console.WriteLine($"БД: {dbPath}  патчи={engine.PatchDisplay}  бакет={engine.TierBucket}");
    }
    else
    {
        Console.WriteLine("data.db не найден — запусти pipeline/collect.py для сбора данных.");
        Console.WriteLine("Режим: только отображение состава (без рекомендаций).\n");
    }

    await PrintPhaseAsync(http, ct);
    await PrintChampSelectIfActiveAsync(http, engine, ct);

    await using var socket = new LcuEventSocket(creds);
    await socket.ConnectAsync(ct);
    Console.WriteLine("Слушаю события LCU. Зайди в лобби и запусти поиск игры…\n");

    var lastHash = "";

    await foreach (var ev in socket.ReadEventsAsync(ct))
    {
        switch (ev.Uri)
        {
            case "/lol-gameflow/v1/session":
                Console.WriteLine($"[Фаза] {PhaseOf(ev.Data, ev.EventType)}");
                break;

            case "/lol-champ-select/v1/session":
                if (ev.EventType == "Delete")
                {
                    Console.WriteLine("[Драфт] champ select завершён.\n");
                    lastHash = "";
                }
                else
                {
                    var draft = ChampSelectParser.Parse(ev.Data);
                    var hash  = DraftHash(draft);
                    if (hash == lastHash) break; // состав не изменился — не спамим
                    lastHash = hash;
                    var recs = engine?.Recommend(draft);
                    DraftPrinter.Print(draft, recs);
                }
                break;
        }
    }

    engine?.Dispose();
}

static async Task PrintPhaseAsync(LcuHttpClient http, CancellationToken ct)
{
    var (status, body) = await http.GetAsync("/lol-gameflow/v1/session", ct);
    if (status != 200) return;
    using var doc = JsonDocument.Parse(body);
    Console.WriteLine($"[Фаза] {PhaseOf(doc.RootElement, "")}");
}

static async Task PrintChampSelectIfActiveAsync(LcuHttpClient http, RecommendationEngine? engine, CancellationToken ct)
{
    var (status, body) = await http.GetAsync("/lol-champ-select/v1/session", ct);
    if (status != 200) return;
    using var doc = JsonDocument.Parse(body);
    var draft = ChampSelectParser.Parse(doc.RootElement);
    var recs  = engine?.Recommend(draft);
    DraftPrinter.Print(draft, recs);
}

static string PhaseOf(JsonElement data, string fallback) =>
    data.ValueKind == JsonValueKind.Object &&
    data.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
        ? p.GetString() ?? fallback
        : fallback;

static string DraftHash(DraftState s) =>
    string.Join(",", s.MyTeam.Select(p => $"{p.ChampionId}:{p.Position}")) + "|" +
    string.Join(",", s.TheirTeam.Select(p => $"{p.ChampionId}:{p.Position}")) + "|" +
    string.Join(",", s.MyTeamBans) + "|" +
    string.Join(",", s.TheirTeamBans);
