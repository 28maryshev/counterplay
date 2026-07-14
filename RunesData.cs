using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Counterplay;

/// <summary>Страница рун: деревья, перки, осколки — ровно то, что принимает LCU.</summary>
public sealed record RunePage(
    int Primary, int Sub,
    IReadOnlyList<int> Perks,      // 4 основных (первый — кейстоун)
    IReadOnlyList<int> Secondary,  // 2 вторичных
    IReadOnlyList<int> Shards,     // 3 осколка
    int Games, double Winrate);

/// <summary>Вариант рун для кнопки в панели.</summary>
public sealed record RuneChoice(
    int Keystone, int Games, double Winrate, double PickRate,
    RunePage Page, double VsDelta, int VsGames);

/// <summary>
/// Сборка: 6 слотов. Core — предметы, которые реально играли ВМЕСТЕ (у них и
/// винрейт); остальные — ходовые докупки, которыми набор добит до шести.
/// </summary>
public sealed record BuildData(
    IReadOnlyList<int> Items, IReadOnlyList<int> Core, int Games, double Winrate,
    IReadOnlyList<int> Spells);

/// <summary>Данные по связке чемпион+роль (то, что отдаёт сервер).</summary>
public sealed record ChampStats(
    int ChampionId, string Role, string Patch, int Games,
    IReadOnlyList<RuneChoice> Keystones,
    IReadOnlyDictionary<int, (int Games, Dictionary<int, double> Deltas)> Vs,
    IReadOnlyList<BuildData> Builds);   // до 3 вариантов сборки, у каждого свой экспорт

/// <summary>
/// Клиент статистики рун/билдов. Данные лежат на сервере готовыми JSON
/// (counterplays.com/api/stats), кэшируются Cloudflare и в памяти процесса.
///
/// Запрос идёт ОДИН раз за драфт (когда чемпион выбран), а не на каждый ховер,
/// поэтому сеть тут не мешает. Плюс предзагрузка: как только движок посчитал
/// рекомендации, тянем данные для топ-кандидатов заранее — к моменту пика они
/// уже в памяти.
///
/// Чего нет в манифесте — того нет и в интерфейсе: фичи включаются сами, когда
/// на сервере накопится достаточная выборка.
/// </summary>
public static class RunesClient
{
    private const string BaseUrl = "https://counterplays.com/api/stats/v1";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly Dictionary<string, ChampStats?> Cache = new();
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static HashSet<string>? _available;   // "157-mid" — что есть на сервере
    private static string? _patch;

    /// Тестовый режим (dotnet run test): реальных рун в базе ещё нет, поэтому
    /// панель наполняем правдоподобными моками — чтобы обкатать вид и импорт.
    public static bool UseMock { get; set; }

    /// Подтянуть манифест (что доступно). Тихо: нет сети — фича просто выключена.
    public static async Task LoadManifestAsync(CancellationToken ct)
    {
        if (UseMock) { _patch = "16.13"; _available = null; return; }

        try
        {
            var json = await Http.GetStringAsync($"{BaseUrl}/manifest.json", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _patch = root.GetProperty("patch").GetString();
            _available = root.GetProperty("available").EnumerateArray()
                             .Select(x => x.GetString()!).ToHashSet();
        }
        catch { _available = null; }
    }

    /// Есть ли данные по связке (иначе панель не показываем).
    public static bool Has(int champ, string role) =>
        UseMock || _available?.Contains($"{champ}-{role}") == true;

    /// Данные по связке. null — нет данных/сети.
    public static async Task<ChampStats?> GetAsync(int champ, string role, CancellationToken ct)
    {
        if (UseMock) return Mock(champ, role);

        var key = $"{champ}-{role}";
        await Gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        finally { Gate.Release(); }

        ChampStats? stats = null;
        try
        {
            if (Has(champ, role) && _patch != null)
            {
                var json = await Http.GetStringAsync($"{BaseUrl}/{_patch}/{key}.json", ct);
                stats = Parse(json);
            }
        }
        catch { /* сеть моргнула — панель просто не появится */ }

        await Gate.WaitAsync(ct);
        try { Cache[key] = stats; }
        finally { Gate.Release(); }
        return stats;
    }

    /// Предзагрузка для кандидатов (чтобы к моменту пика данные уже были).
    public static void Prefetch(IEnumerable<int> champs, string role, CancellationToken ct)
    {
        foreach (var c in champs.Take(3))
        {
            var champ = c;
            _ = Task.Run(() => GetAsync(champ, role, ct), ct);
        }
    }

    // ── Моки для тестового режима ────────────────────────────────────────
    // Правдоподобные страницы рун реальных деревьев: Точность, Доминирование,
    // Колдовство. Цифры выдуманы — это стенд для UI, а не рекомендация.
    private static ChampStats Mock(int champ, string role)
    {
        var rnd = new Random(champ * 31 + role.GetHashCode());
        double W(double b) => Math.Round(b + rnd.NextDouble() * 3 - 1.5, 1);

        RuneChoice Make(int keystone, int primary, int sub, int[] perks, int[] second, double wr, double pick, int games)
            => new(keystone, games, W(wr), pick,
                   new RunePage(primary, sub, perks, second, [5008, 5008, 5001], games / 2, W(wr)),
                   0, 0);

        var list = new List<RuneChoice>
        {
            // Завоеватель (Точность) + Доминирование
            Make(8010, 8000, 8100, [8010, 9111, 9104, 8014], [8139, 8135], 52.4, 46, 4820),
            // Электрокинетик (Доминирование) + Точность
            Make(8112, 8100, 8000, [8112, 8143, 8136, 8135], [9111, 8014], 51.1, 31, 3240),
            // Первый удар (Вдохновение) + Колдовство
            Make(8369, 8300, 8200, [8369, 8306, 8321, 8347], [8226, 8210], 50.2, 14, 1470),
        };

        // Поправки против «оппонента» — тоже мок, но в правдоподобных пределах.
        var vs = new Dictionary<int, (int, Dictionary<int, double>)>();
        foreach (var opp in new[] { 122, 238, 157, 86, 92 })
            vs[opp] = (rnd.Next(60, 260), new Dictionary<int, double>
            {
                [8010] = Math.Round(rnd.NextDouble() * 4 - 2, 1),
                [8112] = Math.Round(rnd.NextDouble() * 4 - 2, 1),
                [8369] = Math.Round(rnd.NextDouble() * 4 - 2, 1),
            });

        // Три варианта сборки по 6 слотов: core (реально сыгранный набор) + докупки.
        var builds = new List<BuildData>
        {
            new([3006, 3031, 6673, 3072, 3036, 3026], [3006, 3031, 6673], 1830, W(53.7), [4, 12]),
            new([3047, 6672, 3153, 3031, 3026, 6333], [3047, 6672, 3153], 940,  W(52.1), [4, 12]),
            new([3006, 6675, 3036, 3095, 3072, 6676], [3006, 6675, 3036], 610,  W(51.4), [4, 12]),
        };
        return new ChampStats(champ, role, "16.13", 9530, list, vs, builds);
    }

    /// Разбор JSON сервера (формат — pipeline/export_runes.py).
    public static ChampStats Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        var keystones = new List<RuneChoice>();
        foreach (var k in r.GetProperty("keystones").EnumerateArray())
        {
            var p = k.GetProperty("page");
            var page = new RunePage(
                p.GetProperty("primary").GetInt32(),
                p.GetProperty("sub").GetInt32(),
                p.GetProperty("perks").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                p.GetProperty("secondary").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                p.GetProperty("shards").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                p.GetProperty("games").GetInt32(),
                p.GetProperty("wr").GetDouble());

            keystones.Add(new RuneChoice(
                k.GetProperty("id").GetInt32(),
                k.GetProperty("games").GetInt32(),
                k.GetProperty("wr").GetDouble(),
                k.GetProperty("pick").GetDouble(),
                page, 0, 0));
        }

        var vs = new Dictionary<int, (int, Dictionary<int, double>)>();
        if (r.TryGetProperty("vs", out var vsEl))
            foreach (var pair in vsEl.EnumerateObject())
            {
                var deltas = new Dictionary<int, double>();
                foreach (var d in pair.Value.GetProperty("keystones").EnumerateArray())
                    deltas[d.GetProperty("id").GetInt32()] = d.GetProperty("delta").GetDouble();
                vs[int.Parse(pair.Name)] = (pair.Value.GetProperty("games").GetInt32(), deltas);
            }

        // До 3 вариантов сборки — у каждого своя кнопка экспорта в панели.
        var spellsEl = r.GetProperty("spells");
        var spells = spellsEl.GetArrayLength() > 0
            ? spellsEl[0].GetProperty("spells").EnumerateArray().Select(x => x.GetInt32()).ToList()
            : new List<int>();

        var builds = r.GetProperty("builds").EnumerateArray().Take(3)
            .Select(b => new BuildData(
                b.GetProperty("items").EnumerateArray().Select(x => x.GetInt32()).ToList(),
                b.TryGetProperty("core", out var c)
                    ? c.EnumerateArray().Select(x => x.GetInt32()).ToList()
                    : [],
                b.GetProperty("games").GetInt32(),
                b.GetProperty("wr").GetDouble(),
                spells))
            .ToList();

        return new ChampStats(
            r.GetProperty("champion").GetInt32(),
            r.GetProperty("role").GetString()!,
            r.GetProperty("patches")[0].GetString()!,
            r.GetProperty("games").GetInt32(),
            keystones, vs, builds);
    }

    /// <summary>
    /// Итоговые варианты для панели: база + поправка на оппонента.
    ///
    /// Поправка ДОБАВЛЯЕТСЯ к базовому винрейту, а не заменяет его: пары
    /// матчапов тонкие (~60 игр на патч), чистый винрейт по ним был бы шумом.
    /// На сервере дельта уже темперирована объёмом выборки.
    /// </summary>
    public static IReadOnlyList<RuneChoice> Choices(ChampStats s, int? opponentId)
    {
        var deltas = new Dictionary<int, double>();
        int vsGames = 0;
        if (opponentId is { } opp && s.Vs.TryGetValue(opp, out var v))
        {
            vsGames = v.Games;
            deltas = v.Deltas;
        }

        return s.Keystones
            .Select(k => k with
            {
                VsDelta = deltas.GetValueOrDefault(k.Keystone),
                VsGames = vsGames,
            })
            .OrderByDescending(k => k.Winrate + k.VsDelta)
            .Take(3)
            .ToList();
    }
}
