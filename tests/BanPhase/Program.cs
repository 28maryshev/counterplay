using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Фаза банов.
///
/// 1. Контры к моим мейнам весят столько, насколько вероятно их встретить.
///    Было: первым баном стоял Зилеан на топе (0,09% игр роли) за то, что «бьёт
///    Дариуса», Тарик в саппортах (0,6%) — за Брауна. Бан на того, кого почти
///    не берут, потрачен. Теперь офф-мета не кандидат вовсе, а сила контры
///    умножается на встречаемость (корень из пикрейта к среднему по роли).
/// 2. Тир-лист банов: на роли — кого банят чаще, роль чемпиону даёт четверть
///    его игр, бан-рейт полный. Без порога Ясуо вставал бы в стрелки.
/// 3. После своего бана окно показывает баны обеих команд с бан-рейтом.
///
/// Считаем на живой базе игрока (только чтение). Нет базы — проверять нечего.
/// Снимок окна для глаз: переменная CP_SNAP — папка, куда положить PNG.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "BanPhase");
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Strings();
        Parser();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — движок и окно проверять нечем"); return Done(); }
        Console.WriteLine($"база: {db}");
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Console.WriteLine($"патч: {engine.Patch}   бакет: {engine.TierBucket}");

        Meet(engine);
        Rates(engine);
        BanTier(engine);
        SessionTracker.HistoryOverride = null;

        // Имена и иконки: без них песочнице не из кого выбирать, а снимку нечего рисовать.
        Assets(playerDir);
        Shown(engine);
        Lane(engine);
        var snap = Environment.GetEnvironmentVariable("CP_SNAP");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window(engine, snap);
        Sandbox(engine, snap);
        app.Shutdown();
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: фаза банов считает и показывает как задумано"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Контры к мейнам — с поправкой на пикрейт ─────────────────────────

    /// Мейны по ролям — те, на ком старая формула давала офф-мета первым баном.
    private static readonly (string Lcu, string Db, int[] Mains)[] Cases =
    [
        ("top",     "top",     [86, 122]),            // Гарен, Дариус → был Зилеан 0,09%
        ("middle",  "mid",     [103, 134, 61]),       // Ари, Синдра, Орианна → Триндамир 0,18%
        ("utility", "support", [37, 201, 161]),       // Сона, Браум, Вел'Коз → Тарик 0,6%
        ("jungle",  "jungle",  [64, 254]),            // Ли Син, Вай
        ("bottom",  "adc",     [222, 51]),            // Джинкс, Кейтлин
    ];

    private static void Meet(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("контры к мейнам взвешены пикрейтом:");
        foreach (var (lcu, role, mains) in Cases)
        {
            SessionTracker.HistoryOverride = Hist(mains);
            var picks = engine.PickRates(role);
            var bans  = engine.RecommendBans(Draft(lcu), top: 5);
            var line  = string.Join(", ", bans.Select(b => $"{b.ChampionId}:{picks.GetValueOrDefault(b.ChampionId):F1}%"));
            Console.WriteLine($"  {role,-8} {line}");

            Check($"{role}: банов пять", bans.Count == 5, $"{bans.Count}");
            // Без союзников в счёте только моя роль: кандидат обязан на ней играться.
            var rare = bans.Where(b => picks.GetValueOrDefault(b.ChampionId) < 0.5).ToList();
            Check($"{role}: нет офф-меты (< 0,5% игр роли)", rare.Count == 0,
                  rare.Count == 0 ? "все играются" : string.Join(", ", rare.Select(b => b.ChampionId)));
            // Встречаемость сдвигает список к тем, кого берут: средний пикрейт
            // пятёрки выше среднего пикрейта регулярного пика роли.
            var regular = picks.Values.Where(p => p >= 0.5).Average();
            var avg = bans.Average(b => picks.GetValueOrDefault(b.ChampionId));
            Check($"{role}: пятёрка банов берётся чаще среднего пика роли", avg > regular,
                  $"{avg:F2}% против {regular:F2}%");
        }

        // Подпись о контре называет пикрейт — видно, почему этот бан, а не другой.
        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        var head = Loc.T("reason.countersPoolPick", "\u0001", "\u0002").Split('\u0001')[0];
        var reasons = engine.RecommendBans(Draft("utility"), top: 8).SelectMany(b => b.Reasons)
                            .Where(r => r.StartsWith(head, StringComparison.Ordinal)).ToList();
        Check("подпись «контрит … · пик N%» встречается", reasons.Count > 0,
              reasons.FirstOrDefault() ?? "нет");
        Check("в ней число пикрейта", reasons.All(r => Regex.IsMatch(r, @"\d+[.,]\d%")),
              reasons.FirstOrDefault() ?? "");
    }

    // ── 1б. Наведённое своими не банится, доводы — по важности и с пикрейтом ──

    /// <summary>
    /// Сцена со скриншота владельца: союзники навели Ясуо и Каина, а до Каина
    /// тот же союзник наводил Вуконга. Вуконг стоял четвёртым баном («обыгрывает
    /// вашу команду») и тут же в подписи Шиваны «контрит ваших: Каин, Вуконг»:
    /// программа защищала запасной пик союзника и сама же советовала его
    /// забанить.
    /// </summary>
    private static void Shown(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("наведённое своими:");
        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        var team = new List<DraftPlayer>
        {
            new(0, 0, 157, "top", false),      // Ясуо
            new(1, 0, 141, "jungle", false),   // Каин (до него — Вуконг)
            new(2, 0, 0, "middle", false),
            new(3, 0, 0, "bottom", false),
            new(4, 0, 0, "utility", true),
        };
        var state = new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: team[4], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: true);
        var history = new Dictionary<int, HashSet<int>> { [0] = [157], [1] = [62, 141] };
        var bans = engine.RecommendBans(state, history, top: 5);
        Console.WriteLine($"  баны: {string.Join(", ", bans.Select(b => DataDragon.Name(b.ChampionId)))}");

        var shown = history.Values.SelectMany(x => x).ToHashSet();
        var bad = bans.Where(b => shown.Contains(b.ChampionId)).ToList();
        Check("наведённых своими (и сменённых) в банах нет", bad.Count == 0,
              string.Join(", ", bad.Select(b => DataDragon.Name(b.ChampionId))));
        var all = bans.SelectMany(b => b.Reasons).ToList();
        Check("сменённый пик союзника по-прежнему защищают", all.Any(r => r.Contains(DataDragon.Name(62))),
              all.FirstOrDefault(r => r.Contains(DataDragon.Name(62))) ?? "о Вуконге ни слова");

        // Контры пикам союзников называют пикрейт, как контры моему пулу.
        var ally = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var many = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        var guard = all.Where(r => r.StartsWith(ally, StringComparison.Ordinal) || r.StartsWith(many, StringComparison.Ordinal)).ToList();
        Check("контры пикам союзников есть", guard.Count > 0, $"{guard.Count}");
        Check("и в них пикрейт", guard.All(r => Regex.IsMatch(r, @"\d+[.,]\d%$")), guard.FirstOrDefault() ?? "");

        // Доводы по важности: защита пиков раньше «сильного в патче».
        var strong = Loc.T("reason.strongPatch", "\u0001").Split('\u0001')[0];
        int Pos(BanRec b, Func<string, bool> f) => Array.FindIndex(b.Reasons, r => f(r));
        var order = bans.Select(b => (Guard: Pos(b, r => r.StartsWith(ally, StringComparison.Ordinal) || r.StartsWith(many, StringComparison.Ordinal)),
                                      Meta: Pos(b, r => r.TrimStart(RecommendationEngine.SIGN_GOOD).StartsWith(strong, StringComparison.Ordinal))))
                        .Where(x => x.Guard >= 0 && x.Meta >= 0).ToList();
        Check("защита пиков — раньше силы в патче", order.All(x => x.Guard < x.Meta), $"{order.Count} карточек с обоими");
        SessionTracker.HistoryOverride = null;
    }

    // ── 1в. Бан прежде всего мой: линия первой, пул из профиля ───────────────

    /// <summary>
    /// Скриншот владельца-саппорта: союзники навели Ирелию и Мастера Йи, и
    /// четыре бана из пяти были про них, один — про его линию. Теперь моей
    /// линии — не меньше трёх мест, и они первые; защите союзников — не больше
    /// двух. Веса журнала — как у владельца за месяц.
    /// </summary>
    private static void Lane(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("моя линия первой:");
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int> { [37] = 32, [201] = 19, [22] = 18, [161] = 17, [910] = 10, [101] = 8 });
        var team = new List<DraftPlayer>
        {
            new(0, 0, 39, "top", false),       // Ирелия
            new(1, 0, 11, "jungle", false),    // Мастер Йи
            new(2, 0, 0, "middle", false),
            new(3, 0, 0, "bottom", false),
            new(4, 0, 0, "utility", true),
        };
        var state = Snapshot(team, team[4], "utility");
        var bans = engine.RecommendBans(state, new Dictionary<int, HashSet<int>> { [0] = [39], [1] = [11] }, top: 5);
        var sup = engine.PickRates("support");
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {b.Reasons.FirstOrDefault()}");
        bool OnLane(BanRec b) => sup.GetValueOrDefault(b.ChampionId) >= 0.5;
        Check("не меньше трёх банов — на мою линию", bans.Count(OnLane) >= 3, $"{bans.Count(OnLane)} из {bans.Count}");
        Check("и они первые", bans.Take(3).All(OnLane), string.Join(", ", bans.Take(3).Select(b => DataDragon.Name(b.ChampionId))));
        int[] mineIds = [37, 201, 22, 161, 910, 101];
        Check("своих мейнов не банит", !bans.Any(b => mineIds.Contains(b.ChampionId)),
              string.Join(", ", bans.Where(b => mineIds.Contains(b.ChampionId)).Select(b => DataDragon.Name(b.ChampionId))));
        var ally = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var many = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        var guard = bans.Count(b => !OnLane(b));
        Check("защите союзников — не больше двух", guard <= 2, $"{guard}");
        Check("защита союзников всё же есть", bans.Any(b => b.Reasons.Any(r =>
                  r.StartsWith(ally, StringComparison.Ordinal) || r.StartsWith(many, StringComparison.Ordinal))), "");

        // Пул из профиля: истории нет, в активном пуле саппорта — Сона и Браум.
        // Раньше баны пул не читали, и контрить было бы некого.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>());
        var pool = new ChampPool { Name = "проверка", ByRole = new() { ["support"] = [37, 201] } };
        PoolStore.Current().Pools.Add(pool);
        PoolStore.SetActive(PoolKind.Pool, pool.Id);
        var me = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        var head = Loc.T("reason.countersPoolPick", "\u0001", "").Split('\u0001')[0];
        var poolReasons = engine.RecommendBans(Snapshot(me, me[0], "utility"), null, top: 5)
                                .SelectMany(b => b.Reasons).Where(r => r.StartsWith(head, StringComparison.Ordinal)).ToList();
        Check("пул из профиля: контры ему есть без истории", poolReasons.Count > 0, poolReasons.FirstOrDefault() ?? "нет");
        Check("называют чемпионов пула", poolReasons.All(r => r.Contains(DataDragon.Name(37)) || r.Contains(DataDragon.Name(201))), "");
        // Пул целиком банов не закрывает (решение владельца: только наигранные
        // за месяц) — чемпион пула без игр в банах допустим, а с играми — нет.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int> { [201] = 12 });
        var withGames = engine.RecommendBans(Snapshot(me, me[0], "utility"), null, top: 10);
        Check("сыгранный за месяц из пула не банится", withGames.All(b => b.ChampionId != 201), "");
        PoolStore.SetActive(PoolKind.Pool, null);
        PoolStore.Current().Pools.Remove(pool);
        SessionTracker.HistoryOverride = null;
    }

    private static DraftState Snapshot(List<DraftPlayer> team, DraftPlayer me, string pos) => new(
        MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
        Me: me, MyPosition: pos, DirectOpponent: null, ExposedToCounter: false,
        InBanPhase: true, Bench: [], IsAram: false,
        MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
        FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: true);

    // ── 2. Бан-рейт и тир-лист банов ─────────────────────────────────────────

    private static void Rates(RecommendationEngine engine)
    {
        Console.WriteLine();
        var rates = engine.BanRates();
        // В матче десять банов, часть пропускают: сумма бан-рейтов — это банов
        // на матч, умноженное на сто.
        var perMatch = rates.Values.Sum() / 100.0;
        Check("банов на матч — от 8 до 10", perMatch is > 8 and <= 10, $"{perMatch:F2}");
        Check("бан-рейт не выше 100%", rates.Values.All(r => r is > 0 and <= 100),
              $"макс {rates.Values.DefaultIfEmpty().Max():F1}%");
        var top = rates.OrderByDescending(kv => kv.Value).First();
        Check("BanRate отдаёт то же, что BanRates", Math.Abs(engine.BanRate(top.Key) - top.Value) < 1e-9,
              $"{top.Key}: {top.Value:F1}%");
        Check("неизвестный чемпион — 0", engine.BanRate(999_999) == 0, "0");
    }

    private static void BanTier(RecommendationEngine engine)
    {
        Console.WriteLine();
        var list = engine.BanTierList(15);
        var games = RoleGames(engine);
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            var col = list.Where(t => t.Role == role).ToList();
            Console.WriteLine($"  {role,-8} {string.Join(", ", col.Take(6).Select(t => $"{t.ChampionId}:{t.BanRate:F1}%"))}");
            Check($"{role}: 15 строк", col.Count == 15, $"{col.Count}");
            Check($"{role}: по убыванию бан-рейта",
                  col.Zip(col.Skip(1)).All(p => p.First.BanRate >= p.Second.BanRate), "");
            Check($"{role}: бан-рейт полный, как у чемпиона",
                  col.All(t => Math.Abs(t.BanRate - engine.BanRate(t.ChampionId)) < 1e-9), "");
            // Банят чемпиона, а не роль: в колонку роли он попадает, только если
            // играется на ней хотя бы в четверти своих игр.
            var stray = col.Where(t =>
            {
                var g = games.GetValueOrDefault(t.ChampionId);
                return g is null || g.GetValueOrDefault(role) < 0.25 * g.Values.Sum();
            }).ToList();
            Check($"{role}: нет чужих для роли", stray.Count == 0,
                  string.Join(", ", stray.Select(t => t.ChampionId)));
        }
    }

    /// Игры чемпиона по ролям в текущем патче — прямо из базы.
    private static Dictionary<int, Dictionary<string, double>> RoleGames(RecommendationEngine engine)
    {
        var res = new Dictionary<int, Dictionary<string, double>>();
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={RecommendationEngine.FindDb()}");
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT champion_id, role, SUM(games) FROM base_wr WHERE tier_bucket=@t AND patch=@p GROUP BY 1, 2";
        cmd.Parameters.AddWithValue("@t", engine.TierBucket);
        cmd.Parameters.AddWithValue("@p", engine.Patch);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            if (!res.TryGetValue(rd.GetInt32(0), out var m)) res[rd.GetInt32(0)] = m = [];
            m[rd.GetString(1)] = rd.GetDouble(2);
        }
        return res;
    }

    // ── 3. Разбор клиента: мой бан сделан ────────────────────────────────────

    private static void Parser()
    {
        Console.WriteLine();
        bool Done(string actions) => ChampSelectParser.Parse(JsonDocument.Parse(
            $"{{\"localPlayerCellId\":2,\"myTeam\":[],\"theirTeam\":[],\"actions\":{actions}}}").RootElement).MyBanDone;

        Check("бан в процессе — не сделан",
              !Done("[[{\"id\":3,\"actorCellId\":2,\"type\":\"ban\",\"completed\":false,\"isInProgress\":true,\"championId\":157}]]"), "");
        Check("бан завершён — сделан",
              Done("[[{\"id\":3,\"actorCellId\":2,\"type\":\"ban\",\"completed\":true,\"isInProgress\":false,\"championId\":157}]]"), "");
        Check("пустой бан по таймеру — тоже сделан",
              Done("[[{\"id\":3,\"actorCellId\":2,\"type\":\"ban\",\"completed\":true,\"championId\":0}]]"), "");
        // Планирование: действий ещё нет. «Нет незавершённого» здесь не значит «забанил».
        Check("действий ещё нет — не сделан", !Done("[]"), "");
        Check("чужой бан не мой",
              !Done("[[{\"id\":4,\"actorCellId\":1,\"type\":\"ban\",\"completed\":true,\"championId\":64}]]"), "");
    }

    // ── 4. Строки на всех языках ─────────────────────────────────────────────

    private static readonly string[] NewKeys =
    [
        "reason.countersPoolPick", "reason.pickRate",
        "tier.modePicks", "tier.modeBans", "tier.titleBans", "tier.byBanRate",
        "ban.ours", "ban.theirs", "ban.doneStatus", "ban.doneTitle", "ban.rateTip",
    ];

    private static void Strings()
    {
        Console.WriteLine("строки фазы банов на всех языках:");
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "i18n");
        if (!Directory.Exists(dir)) dir = Path.Combine(Repo(), "assets", "i18n");
        var docs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        Check("языков 15", docs.Count == 15, $"{docs.Count}");
        foreach (var key in NewKeys)
        {
            var en = Get(docs["en"], key);
            var holes = Regex.Matches(en ?? "", @"\{\d\}").Select(m => m.Value).OrderBy(x => x).ToList();
            var bad = docs.Where(kv =>
            {
                var s = Get(kv.Value, key);
                return s is null || !Regex.Matches(s, @"\{\d\}").Select(m => m.Value).OrderBy(x => x).SequenceEqual(holes);
            }).Select(kv => kv.Key).ToList();
            Check($"  {key}", bad.Count == 0, bad.Count == 0 ? $"{holes.Count} подстановки" : string.Join(",", bad));
        }
    }

    private static string? Get(JsonElement root, string key)
    {
        var e = root;
        foreach (var part in key.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    // ── 5. Окно: советы → баны команд → пики ─────────────────────────────────

    private static void Window(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine();
        Console.WriteLine("окно:");
        var s = AppSettings.Current;
        s.BansTierMode = "picks";
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();

        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        // Советы по банам: мой ход.
        var advice = Draft("utility", ours: [], theirs: [], done: false);
        w.UpdateBans(engine.RecommendBans(advice), advice, engine);
        Pump();
        Check("до бана — советы", Vis(w, "BanScroll") && !Vis(w, "BansDonePanel"), "");
        var cards = (((ItemsControl)w.FindName("BanFullList")).ItemsSource as IEnumerable<RecCard>)?.ToList() ?? [];
        Check("на карточке до двух доводов, без служебных знаков",
              cards.Count > 0 && cards.All(c => c.Reason.Split('\n').Length <= 2
                  && !c.Reason.Any(ch => ch is RecommendationEngine.SIGN_GOOD or RecommendationEngine.SIGN_BAD or RecommendationEngine.SIGN_KEY)),
              cards.FirstOrDefault()?.Reason.Replace("\n", " | ") ?? "");
        Check("у кого доводов два — оба видны",
              cards.Where(c => engine.RecommendBans(advice).First(b => b.ChampionId == c.ChampionId).Reasons.Length >= 2)
                   .All(c => c.Reason.Contains('\n')), "");
        Check("тир-лист под ними — пики", Vis(w, "TierList") && !Vis(w, "BanTierList"), "");

        // Кнопка «Баны» над тир-листом: вид меняется на этот драфт, настройка —
        // нет (она задаёт, с чего начинается каждый драфт).
        var click = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Call(w, "TierMode_Click", new Border { Tag = "bans" }, click);
        Pump();
        Check("кнопка «Баны» в драфте — тир-лист банов", Vis(w, "BanTierList") && !Vis(w, "TierList"), "");
        Check("а настройка по умолчанию не тронута", s.BansTierMode == "picks", s.BansTierMode);
        w.DraftEnded();
        Pump();
        w.UpdateBans(engine.RecommendBans(advice), advice, engine);
        Pump();
        Check("новый драфт — снова вид из настроек (пики)", Vis(w, "TierList") && !Vis(w, "BanTierList"), "");

        // Умолчание «Баны» в настройках: драфт сразу с тир-листа банов.
        s.BansTierMode = "bans";
        w.UpdateBans(engine.RecommendBans(advice), advice, engine);
        Pump();
        var banCols = ((ItemsControl)w.FindName("BanTierList")).ItemsSource as IEnumerable<TierRoleCol>;
        Check("режим «Баны» — тир-лист банов", Vis(w, "BanTierList") && !Vis(w, "TierList"), "");
        Check("в нём пять ролей", banCols?.Count() == 5, $"{banCols?.Count()}");
        var first = banCols?.First().TierCells.First();
        Check("под иконкой бан-рейт", first is not null && first.WrText.EndsWith('%'), first?.WrText ?? "");
        if (snap is { Length: > 0 }) Snap(w, snap, "1-advice-bans-tier.png");

        // Свой бан сделан, союзники добанивают, вражеские скрыты.
        var done = Draft("utility", ours: [157, 64], theirs: [], done: true);
        w.UpdateBans([], done, engine);
        Pump();
        Check("после бана — баны команд вместо советов", Vis(w, "BansDonePanel") && !Vis(w, "BanScroll"), "");
        Check("тир-лист остался", Vis(w, "TierListBar"), "");
        var ours = Slots(w, "BansOurList");
        Check("наших слотов пять, два заняты", ours.Count == 5 && ours.Count(x => !x.IsEmpty) == 2,
              $"{ours.Count}/{ours.Count(x => !x.IsEmpty)}");
        Check("у бана — бан-рейт патча", ours[0].Rate == $"{engine.BanRate(157):F1}%",
              $"{ours[0].Rate} против {engine.BanRate(157):F1}%");
        // До раскрытия их половины нет, наша — по центру места под обе.
        Check("их половины не видно", Opacity(w, "BansTheirHalf") == 0, $"{Opacity(w, "BansTheirHalf")}");
        Check("наша сдвинута к центру", ShiftY(w, "BansOurHalf") > 20, $"{ShiftY(w, "BansOurHalf"):0}");
        Check("наших не пять — волны нет", !Vis(w, "BansOurWave"), "");
        Wait(0.6);
        Check("новые баны допрыгнули", SlotOpacity(w, "BansOurList", 0) == 1 && SlotOpacity(w, "BansOurList", 1) == 1,
              $"{SlotOpacity(w, "BansOurList", 0)} / {SlotOpacity(w, "BansOurList", 1)}");
        Check("вспышка по рамке — синяя", FlashColor(w, "BansOurList", 1) == "#FF36D6E7", FlashColor(w, "BansOurList", 1));
        if (snap is { Length: > 0 }) Snap(w, snap, "2-done-ours.png");

        // Свои добанили — синяя волна по нашей половине, их всё ещё нет.
        var oursFull = Draft("utility", ours: [157, 64, 555, 238, 523], theirs: [], done: true);
        w.UpdateBans([], oursFull, engine);
        Pump();
        Check("наших пять — синяя волна", Vis(w, "BansOurWave"), "");
        Check("их половины по-прежнему нет", Opacity(w, "BansTheirHalf") == 0 && !Vis(w, "BansTheirWave"), "");
        Wait(1.4);
        if (snap is { Length: > 0 }) Snap(w, snap, "3-ours-wave.png");

        // Таймер банов кончился: клиент раскрывает вражеские уже первым снимком
        // пиков. Окно держит панель — их половина выезжает снизу.
        var reveal = oursFull with
        {
            TheirTeamBans = [24, 893, 145, 119, 37], InBanPhase = false,
            MyBanActionId = -1, MyBanInProgress = false,
        };
        w.UpdateRecommendations(engine.Recommend(reveal, 6), reveal, engine);
        // По очереди и с их стороны — справа налево: у каждого слота левее
        // задержка больше. Меряем задержки, а не прозрачность в момент: окно в
        // проверке невидимое, и под нагрузкой часы анимаций убегали — к
        // первому замеру проявлялись все сразу.
        Pump();
        var delays = Slots(w, "BansTheirList").Select(x => x.Delay).ToList();
        Check("их баны появляются по очереди справа налево",
              delays.Count == 5 && delays.Zip(delays.Skip(1)).All(p => p.First > p.Second),
              string.Join(" ", delays.Select(x => x.ToString("0.00"))));
        Check("вспышка по рамке — красная", FlashColor(w, "BansTheirList", 4) == "#FFFF5A4D",
              FlashColor(w, "BansTheirList", 4));
        // Сразу, пока идут 4 секунды показа: со снимками прогон медленнее, и
        // позже показ успевал кончиться — а после него полоса роли законна.
        // На пиках программа обновляет панель рун: рун ещё нет — «вернуть пул
        // роли». Пока держится панель банов, строка — за тир-листом: полоса
        // чемпионов роли наезжала на него (скриншот владельца).
        w.HideRunes();
        w.ShowRunes(null, 0, "", "support", null);
        Pump();
        Check("пока видны баны, полосы чемпионов роли нет", !Vis(w, "RolePoolBar") && Vis(w, "TierListBar"),
              $"пул роли {Vis(w, "RolePoolBar")}, тир-лист {Vis(w, "TierListBar")}");
        Poll(() => SlotOpacity(w, "BansTheirList", 2) > 0.5, 2);   // середина появления — для снимка
        if (snap is { Length: > 0 }) Snap(w, snap, "4a-reveal-mid.png");
        Check("у наших, показанных раньше, вспышки нет", FlashColor(w, "BansOurList", 0) == "",
              FlashColor(w, "BansOurList", 0));
        Pump();
        Check("пики начались, а панель банов держится", Vis(w, "BansDonePanel") && !Vis(w, "RecScroll"), "");
        var theirs = Slots(w, "BansTheirList");
        Check("вражеские — пять с бан-рейтом", theirs.Count(x => !x.IsEmpty && x.Rate.EndsWith('%')) == 5, "");
        // Забанили моего мейна — помечен он один: золотая рамка, золотое имя,
        // в подсказке — сколько игр.
        var sona = theirs.FindIndex(x => x.Name == DataDragon.Name(37));
        Check("забаненный мейн помечен, и только он",
              sona >= 0 && theirs[sona] is { IsMyMain: true, MainGames: 20 } && theirs.Count(x => x.IsMyMain) == 1,
              string.Join(", ", theirs.Where(x => x.IsMyMain).Select(x => $"{x.Name} {x.MainGames}")));
        Check("в подсказке — «твой мейн»", sona >= 0 && (theirs[sona].Tip ?? "").Contains(Loc.T("ban.yourMain", 20)),
              (theirs[sona].Tip ?? "").Replace("\n", " | "));
        Wait(1.2);
        Check("их половина выехала", Opacity(w, "BansTheirHalf") == 1 && ShiftY(w, "BansTheirHalf") == 0,
              $"{Opacity(w, "BansTheirHalf")} / {ShiftY(w, "BansTheirHalf"):0}");
        Check("наша поднялась на место", ShiftY(w, "BansOurHalf") == 0, $"{ShiftY(w, "BansOurHalf"):0}");
        Check("их пять — красная волна", Vis(w, "BansTheirWave"), "");
        Check("у мейна — золотая рамка со свечением", MainGlow(w, "BansTheirList", sona) == "#FFF5D77A",
              MainGlow(w, "BansTheirList", sona));
        Check("у остальных рамки нет", MainGlow(w, "BansTheirList", sona == 0 ? 1 : 0) == "", "");
        if (snap is { Length: > 0 }) Snap(w, snap, "4-revealed.png");

        // Подержали — пики.
        Wait(3.3);
        Check("через 4 секунды — пики, панели банов нет", !Vis(w, "BansDonePanel") && Vis(w, "RecScroll"), "");
        Check("а пул роли — на своём месте", Vis(w, "RolePoolBar") && !Vis(w, "TierListBar"),
              $"пул роли {Vis(w, "RolePoolBar")}, тир-лист {Vis(w, "TierListBar")}");

        // Раскрытие внутри фазы банов (если клиент когда-то так сделает) —
        // показано сразу, и на пиках держать уже нечего.
        var w2 = new OverlayWindow();
        ShowHidden(w2);
        w2.SetEngine(engine);
        var early = oursFull with { TheirTeamBans = [24, 893, 145, 119, 37] };
        w2.UpdateBans([], early, engine);
        Pump();
        Wait(1.0);
        Check("раскрыты ещё в банах — видны сразу", Opacity(w2, "BansTheirHalf") == 1, "");
        var picks2 = early with { InBanPhase = false, MyBanActionId = -1, MyBanInProgress = false };
        w2.UpdateRecommendations(engine.Recommend(picks2, 6), picks2, engine);
        Pump();
        Check("…и пики без задержки", Vis(w2, "RecScroll") && !Vis(w2, "BansDonePanel"), "");
        w2.Close();

        w.Close();
        Pump();
        s.BansTierMode = "picks";
        Settings(snap);
    }

    /// Пункт в окне настроек: «Тир-лист по умолчанию» с выбором «Пики / Баны»,
    /// по умолчанию — пики.
    private static void Settings(string? snap)
    {
        Check("умолчание у новой установки — пики", AppSettings.Defaults().BansTierMode == "picks",
              AppSettings.Defaults().BansTierMode);
        var type = typeof(OverlayWindow).Assembly.GetType("Counterplay.SettingsWindow")!;
        var win = (Window)Activator.CreateInstance(type, nonPublic: true)!;
        ShowHidden(win);
        Pump();
        var texts = new List<string>();
        var stack = new Stack<DependencyObject>([win]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is TextBlock tb) texts.Add(tb.Text);
            foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(c);
        }
        var title = Loc.T("settings.bansTierMode");
        Check("в настройках есть «" + title + "»", texts.Contains(title), "");
        Check("с выбором «" + Loc.T("tier.modePicks") + " / " + Loc.T("tier.modeBans") + "»",
              texts.Contains(Loc.T("tier.modePicks")) && texts.Contains(Loc.T("tier.modeBans")), "");
        if (snap is { Length: > 0 })
        {
            // Раздел банов внизу — прокручиваем к пункту, чтобы он попал в снимок.
            var stack2 = new Stack<DependencyObject>([win]);
            while (stack2.Count > 0)
            {
                var d = stack2.Pop();
                if (d is TextBlock tb && tb.Text == title) { tb.BringIntoView(new Rect(0, 0, 10, 220)); break; }
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack2.Push(c);
            }
            Pump();
            Snap(win, snap, "0-settings.png");
        }
        win.Close();
        Pump();
    }

    // ── 6. Песочница: кнопка «Баны» — живая фаза банов и пики после неё ──────

    /// Панель песочницы — та же, что открывает «dotnet run -- test»; кнопки
    /// нажимаем событием, как мышью. Время настоящее: боты банят до 14-й
    /// секунды, баны обеих команд держатся 4 секунды.
    private static void Sandbox(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine();
        Console.WriteLine("песочница, кнопка «Баны»:");
        var asm = typeof(OverlayWindow).Assembly;
        Counterplay.Sandbox.Active = true;   // ничего не пишет в данные игрока (их тут и нет)
        asm.GetType("Counterplay.TestMode")!
           .GetMethod("EnterSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!
           .Invoke(null, [engine]);
        // Журнал как у владельца-саппорта: Сона и Браум — мейны. Один враг в
        // песочнице банит мейна, и это должно быть видно на панели.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int> { [37] = 32, [201] = 19, [22] = 6 });

        var overlay = new OverlayWindow();
        ShowHidden(overlay);
        overlay.SetEngine(engine);
        var allIds = DataDragon.GetAllIconUrls().Keys.ToList();
        var panelType = asm.GetType("Counterplay.TestPanel")!;
        var panel = (Window)Activator.CreateInstance(panelType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [overlay, engine, allIds, false, false, false], null)!;
        overlay.BanLockHandler = id => { Call(panel, "LockMyBan", id); return Task.FromResult(200); };
        Pump();

        DraftState? Last() => Get<DraftState>(overlay, "_lastDraft");
        IReadOnlyList<BanRec>? Advice() => Get<IReadOnlyList<BanRec>>(overlay, "_lastBans");

        Get<System.Windows.Controls.Button>(panel, "_stageBans")!
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Wait(4.5);

        var d = Last();
        Check("фаза банов, мой ход", d is { InBanPhase: true, MyBanInProgress: true, MyBanDone: false }, "");
        var hovered = d?.MyTeam.Where(p => !p.IsLocalPlayer && p.PickIntentId != 0).ToList() ?? [];
        Check("двое союзников навели пики", hovered.Count == 2,
              string.Join(", ", hovered.Select(p => DataDragon.Name(p.PickIntentId))));
        var reasons = Advice()?.SelectMany(b => b.Reasons).ToList() ?? [];
        var names = hovered.Select(p => DataDragon.Name(p.PickIntentId)).ToList();
        Check("советы защищают их пики", reasons.Any(r => names.Any(r.Contains)),
              reasons.FirstOrDefault(r => names.Any(r.Contains)) ?? "ни слова о них");
        if (snap is { Length: > 0 }) Snap(overlay, snap, "5-sandbox-advice.png");

        // Банлю первого из советов — как кнопкой в окне.
        var mine = Advice()?.FirstOrDefault()?.ChampionId ?? 0;
        Call(panel, "LockMyBan", mine);
        Pump();
        d = Last();
        Check("после моего бана — баны команд", d is { InBanPhase: true, MyBanDone: true } && Vis(overlay, "BansDonePanel"),
              DataDragon.Name(mine));
        Check("мой бан среди наших", d?.MyTeamBans.Contains(mine) == true, "");
        Check("вражеские пока скрыты", d?.TheirTeamBans.Count == 0, $"{d?.TheirTeamBans.Count}");

        // Боты добанивают к 14-й секунде; через секунду таймер банов кончается,
        // и вражеские раскрываются уже с пиками — как у клиента. Пока ждём,
        // запоминаем последние наведения союзников: на пиках они уйдут в план.
        var lastIntent = new Dictionary<int, int>();
        var ended = WaitFor(() =>
        {
            var x = Last();
            if (x is { InBanPhase: true })
                foreach (var p in x.MyTeam.Where(p => !p.IsLocalPlayer && p.PickIntentId != 0))
                    lastIntent[p.CellId] = p.PickIntentId;
            return x is { InBanPhase: false };
        }, 20);
        d = Last();
        Check("баны кончились — вражеские раскрыты с первым снимком пиков",
              ended && d!.TheirTeamBans.Count == 5 && d.MyTeamBans.Count == 5,
              $"{d?.MyTeamBans.Count} + {d?.TheirTeamBans.Count}");
        Check("десять разных банов", d is not null && d.MyTeamBans.Concat(d.TheirTeamBans).Distinct().Count() == 10, "");
        Check("окно держит панель банов", Vis(overlay, "BansDonePanel"), "");
        var myMains = RecommendationEngine.MyMains();   // Сона, Браум и Эш (6 из 57 — тоже десятая часть)
        var mainBanned = d?.TheirTeamBans.Where(myMains.ContainsKey).ToList() ?? [];
        Check("враг в песочнице забанил моего мейна", mainBanned.Count == 1,
              string.Join(", ", mainBanned.Select(DataDragon.Name)));
        Check("на панели он помечен", Slots(overlay, "BansTheirList").Count(x => x.IsMyMain) == 1, "");
        Wait(1.2);
        Check("на панели все десять с бан-рейтом, их половина выехала",
              Slots(overlay, "BansOurList").Concat(Slots(overlay, "BansTheirList")).Count(x => !x.IsEmpty) == 10
              && Opacity(overlay, "BansTheirHalf") == 1, "");
        if (snap is { Length: > 0 }) Snap(overlay, snap, "6-sandbox-revealed.png");
        var allBans = d?.MyTeamBans.Concat(d.TheirTeamBans).ToHashSet() ?? [];

        Wait(3.3);
        d = Last();
        Check("через 4 секунды — подбор, авто-драфт идёт", d is { InBanPhase: false } && Vis(overlay, "RecScroll"), "");
        SessionTracker.HistoryOverride = null;
        Check("баны ушли в драфт", d is not null && d.MyTeamBans.Count == 5 && d.TheirTeamBans.Count == 5, "");

        // Добираем всех разом: боты не берут забаненных, союзники — то, что наводили.
        Call(panel, "InstantDraft");
        Pump();
        d = Last();
        var picked = d?.MyTeam.Concat(d.TheirTeam).Select(p => p.EffectiveChampionId).Where(x => x != 0).ToList() ?? [];
        Check("в драфте ни одного забаненного", picked.Count >= 9 && !picked.Any(allBans.Contains),
              $"{picked.Count} пиков");
        var kept = lastIntent.Where(kv => !allBans.Contains(kv.Value))
                             .Count(kv => d?.MyTeam.First(p => p.CellId == kv.Key).EffectiveChampionId == kv.Value);
        Check("союзники взяли то, что наводили", kept == lastIntent.Count(kv => !allBans.Contains(kv.Value)),
              $"{kept} из {lastIntent.Count}");

        panel.Hide();
        overlay.Close();
        Pump();
        asm.GetType("Counterplay.TestMode")!
           .GetMethod("LeaveSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!
           .Invoke(null, null);
    }

    private static T? Get<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    /// Ждать условия (не дольше seconds), проверяя каждые 0,25 с.
    private static bool WaitFor(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            Wait(0.25);
        }
        return done();
    }

    /// Подождать, не останавливая очередь окна: таймеры песочницы тикают.
    private static void Wait(double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Pump();
    }

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static double Opacity(Window w, string name) => ((UIElement)w.FindName(name)).Opacity;

    private static double ShiftY(Window w, string name) =>
        ((UIElement)w.FindName(name)).RenderTransform is TranslateTransform t ? t.Y : 0;

    /// Прозрачность слота на экране — дорос ли «прыжок» нового бана.
    private static double SlotOpacity(Window w, string list, int i)
    {
        var ic = (ItemsControl)w.FindName(list);
        return ic.ItemContainerGenerator.ContainerFromIndex(i) is DependencyObject c
               && VisualTreeHelper.GetChildrenCount(c) > 0 && VisualTreeHelper.GetChild(c, 0) is UIElement e
            ? e.Opacity : -1;
    }

    /// Свечение рамки мейна в слоте ("" — рамки нет: это не мой мейн).
    private static string MainGlow(Window w, string list, int i)
    {
        var ic = (ItemsControl)w.FindName(list);
        if (ic.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) return "нет слота";
        var stack = new Stack<DependencyObject>([c]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is System.Windows.Shapes.Rectangle { Visibility: Visibility.Visible,
                                                       Effect: System.Windows.Media.Effects.DropShadowEffect fx })
                return fx.Color.ToString();
            for (var k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++) stack.Push(VisualTreeHelper.GetChild(d, k));
        }
        return "";
    }

    /// Цвет вспышки по рамке слота ("" — вспышки нет: бан показан раньше).
    private static string FlashColor(Window w, string list, int i)
    {
        var ic = (ItemsControl)w.FindName(list);
        if (ic.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) return "нет слота";
        var stack = new Stack<DependencyObject>([c]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is System.Windows.Shapes.Ellipse { Effect: System.Windows.Media.Effects.DropShadowEffect fx })
                return fx.Color.ToString();
            for (var k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++) stack.Push(VisualTreeHelper.GetChild(d, k));
        }
        return "";
    }

    /// Ждать условия мелким шагом (30 мс) — для проверок, где важен момент.
    private static void Poll(Func<bool> done, double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var until = DateTime.UtcNow.AddSeconds(seconds);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        t.Tick += (_, _) => { if (done() || DateTime.UtcNow > until) { t.Stop(); frame.Continue = false; } };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static List<BanSlotVm> Slots(Window w, string name) =>
        (((ItemsControl)w.FindName(name)).ItemsSource as IEnumerable<BanSlotVm>)?.ToList() ?? [];

    /// Снимок содержимого окна — посмотреть глазами. Окно невидимо, но дерево
    /// рисуется как обычно.
    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    /// Для снимка: имена и иконки чемпионов. Кэш игрока копируем к себе — его
    /// папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    /// Журнал: на каждом мейне по двадцать свежих игр.
    private static SessionTracker.PlayHistory Hist(int[] mains) =>
        Hist(mains.ToDictionary(m => m, _ => 20));

    /// Журнал с заданным числом игр на чемпионе.
    private static SessionTracker.PlayHistory Hist(Dictionary<int, int> games)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var times = Enumerable.Range(0, SessionTracker.PlayHistory.KeepTimes)
                              .Select(i => now - i * 86400L).ToArray();
        return new SessionTracker.PlayHistory(
            games.ToDictionary(kv => kv.Key, kv => (kv.Value, kv.Value / 2)),
            games.ToDictionary(kv => kv.Key, _ => times),
            60, now);
    }

    private static DraftState Draft(string pos, List<int>? ours = null, List<int>? theirs = null, bool done = false)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, pos, true) };
        return new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: ours ?? [], TheirTeamBans: theirs ?? [],
            Me: team[0], MyPosition: pos, DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: done ? -1 : 0, MyBanInProgress: !done, MyBanDone: done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
