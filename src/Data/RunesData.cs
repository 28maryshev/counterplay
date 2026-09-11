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
/// Ходовой предмет чемпиона на этой роли — из чего подбирается ответ составу.
public sealed record ItemStat(int Id, int Games, double Winrate);

public sealed record BuildData(
    IReadOnlyList<int> Items, IReadOnlyList<int> Core, int Games, double Winrate,
    IReadOnlyList<int> Spells);

/// <summary>Данные по связке чемпион+роль (то, что отдаёт сервер).</summary>
public sealed record ChampStats(
    int ChampionId, string Role, string Patch, int Games,
    IReadOnlyList<RuneChoice> Keystones,
    IReadOnlyDictionary<int, (int Games, Dictionary<int, double> Deltas)> Vs,
    IReadOnlyList<BuildData> Builds,    // до 3 вариантов сборки, у каждого свой экспорт
    IReadOnlyList<ItemStat> Items);     // ходовые предметы чемпиона — материал для подбора под врагов

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
    private static Dictionary<int, string> _mainRole = new();  // champ → основная роль
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
            _mainRole = new();
            if (root.TryGetProperty("mainRole", out var mr))
                foreach (var p in mr.EnumerateObject())
                    _mainRole[int.Parse(p.Name)] = p.Value.GetString()!;
        }
        catch { _available = null; }
    }

    /// Загрузился ли манифест. Отличает «связки нет в данных» от «мы вообще не
    /// знаем, что есть на сервере» — снаружи это выглядит одинаково.
    public static bool ManifestLoaded => UseMock || _available is not null;

    /// Есть ли данные по связке (иначе панель не показываем).
    public static bool Has(int champ, string role) =>
        UseMock || _available?.Contains($"{champ}-{role}") == true;

    /// <summary>
    /// Роль для показа рун. Если клиент раскрыл позицию (ranked/normal draft) —
    /// берём её. Если нет (custom games, блайнд) — основную роль чемпиона из
    /// данных: руны почти не зависят от того, куда встал соперник.
    /// </summary>
    public static string? ResolveRole(int champ, string lcuRole)
    {
        if (UseMock) return string.IsNullOrEmpty(lcuRole) ? "mid" : lcuRole;
        if (!string.IsNullOrEmpty(lcuRole) && Has(champ, lcuRole)) return lcuRole;
        return _mainRole.GetValueOrDefault(champ);   // null → данных по чемпиону нет
    }

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

        // Набор зависит от чемпиона: с одинаковыми рунами на всех песочница
        // бесполезна — на ней не видно ни разницы между чемпионами, ни того, как
        // панель ведёт себя на самом деле.
        var tags = DataDragon.ClassTags(champ);
        var style = DataDragon.IsApChampion(champ) ? "ap"
                  : tags.Contains("Tank") || tags.Contains("Support") ? "tank"
                  : "ad";

        var list = style switch
        {
            // Колдовство: Аери / Комета / Электрокинетик
            "ap" => new List<RuneChoice>
            {
                Make(8214, 8200, 8300, [8214, 8226, 8210, 8237], [8306, 8345], 52.0, 44, 4310),
                Make(8229, 8200, 8100, [8229, 8226, 8210, 8237], [8139, 8135], 51.3, 33, 3120),
                Make(8112, 8100, 8200, [8112, 8143, 8136, 8135], [8226, 8210], 50.4, 16, 1580),
            },
            // Стойкость: Хватка / Последствия / Хранитель
            "tank" => new List<RuneChoice>
            {
                Make(8437, 8400, 8300, [8437, 8446, 8429, 8451], [8306, 8345], 52.6, 48, 5010),
                Make(8439, 8400, 8200, [8439, 8446, 8429, 8451], [8226, 8210], 51.8, 29, 2870),
                Make(8465, 8400, 8100, [8465, 8446, 8473, 8242], [8139, 8135], 50.7, 15, 1490),
            },
            // Точность: Завоеватель / Первый удар / Град клинков
            _ => new List<RuneChoice>
            {
                Make(8010, 8000, 8100, [8010, 9111, 9104, 8014], [8139, 8135], 52.4, 46, 4820),
                Make(8005, 8000, 8200, [8005, 9111, 9104, 8014], [8226, 8210], 51.1, 31, 3240),
                Make(9923, 8100, 8000, [9923, 8143, 8136, 8135], [9111, 8014], 50.2, 14, 1470),
            },
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
        // Предметы — тоже по типу чемпиона: подбор под состав врагов выбирает
        // только из того, что чемпион реально собирает, и на общем списке его
        // было бы не проверить.
        var itemPool = style switch
        {
            "ap"   => new[] { 3020, 6653, 3157, 3089, 3135, 4645, 3165, 3111, 3102, 3116, 3152, 3137 },
            "tank" => new[] { 3047, 3111, 3068, 3075, 3110, 3143, 3193, 3065, 3001, 3050, 3084, 8020 },
            _      => new[] { 3006, 3031, 6673, 3072, 3036, 3026, 3047, 6672, 3153,
                              3033, 3095, 3156, 3161, 6676, 3143, 3139 },
        };
        var core3 = new Func<int, int[]>(shift => itemPool.Skip(shift).Take(6).ToArray());
        var builds = new List<BuildData>
        {
            new(core3(0), core3(0).Take(3).ToArray(), 1830, W(53.7), [4, 12]),
            new(core3(3), core3(3).Take(3).ToArray(), 940,  W(52.1), [4, 12]),
            new(core3(6), core3(6).Take(3).ToArray(), 610,  W(51.4), [4, 12]),
        };
        // Ходовые предметы для песочницы: те же, что в сборках выше, — чтобы
        // подбор под состав было на чём проверить без сети.
        var mockItems = itemPool
            .Select((id, i) => new ItemStat(id, 900 - i * 40, W(52.0 - i * 0.1)))
            .ToList();
        return new ChampStats(champ, role, "16.13", 9530, list, vs, builds, mockItems);
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

        var items = r.TryGetProperty("items", out var itemsEl)
            ? itemsEl.EnumerateArray()
                .Select(i => new ItemStat(i.GetProperty("id").GetInt32(),
                                          i.GetProperty("games").GetInt32(),
                                          i.GetProperty("wr").GetDouble()))
                .ToList()
            : [];

        return new ChampStats(
            r.GetProperty("champion").GetInt32(),
            r.GetProperty("role").GetString()!,
            r.GetProperty("patches")[0].GetString()!,
            r.GetProperty("games").GetInt32(),
            keystones, vs, builds, items);
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

        var scored = s.Keystones
            .Select(k => k with
            {
                VsDelta = deltas.GetValueOrDefault(k.Keystone),
                VsGames = vsGames,
            })
            .OrderByDescending(k => k.Winrate + k.VsDelta)
            .ToList();

        if (scored.Count <= 3) return scored;

        // Не только винрейт: самый ХОДОВОЙ кейстоун обязан быть в тройке. Иначе
        // можно показать три экзотики с высоким винрейтом на малой выборке и не
        // показать то, что реально играют 60% людей.
        var picked = new List<RuneChoice> { scored[0] };
        var meta = scored.OrderByDescending(k => k.PickRate).First();
        if (meta.Keystone != picked[0].Keystone) picked.Add(meta);

        foreach (var k in scored)
        {
            if (picked.Count >= 3) break;
            if (picked.Any(p => p.Keystone == k.Keystone)) continue;
            picked.Add(k);
        }

        return picked.OrderByDescending(k => k.Winrate + k.VsDelta).ToList();
    }
}
