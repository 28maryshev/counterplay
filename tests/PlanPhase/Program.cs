using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Фаза объявления пиков (PLANNING, 15 секунд до банов).
///
/// 1. Мои чемпионы — только с ВЫДАННОЙ роли. Выкинуло в лес — саппортов, на
///    которых я играю, в списке нет. Роль каждой игры: записанная в журнал →
///    из связок той же игры по времени → обычная роль чемпиона у меня по
///    связкам → его главная роль в базе. Без связок Эш владельца (саппорт)
///    ушла бы в стрелки.
/// 2. Разбор драфта отличает объявление от банов, и отпечаток драфта тоже —
///    иначе окно не перешло бы от моих чемпионов к советам банов.
/// 3. Окно: в объявлении — мои чемпионы с винрейтом, формой, LP и патчем; клик
///    показывает пик союзникам (наведение пика, не бана); игр на роли нет —
///    пул; нет и пула — строка-пояснение; с таймером банов — советы банов.
/// 4. Синхронизация переносит роль игры между компьютерами.
///
/// Журнал подставной (своя папка), база матчей — настоящая, только чтение.
/// </summary>
internal static class Program
{
    private static int _fails;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PlanPhase");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
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
        WriteJournal(root);
        Lanes();
        DraftRole();
        SyncRole();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — патч и окно проверять нечем"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Console.WriteLine($"\nпатч: {engine.Patch}   бакет: {engine.TierBucket}");
        Patch(engine);

        Assets(playerDir);
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window(engine, Environment.GetEnvironmentVariable("CP_SNAP"));
        app.Shutdown();
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: объявление пиков показывает мою роль как задумано"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Строки на всех языках ─────────────────────────────────────────────

    private static readonly string[] NewKeys =
    [
        "draft.rolePlan", "plan.title", "plan.titlePool", "plan.hint", "plan.colGames", "plan.colPool",
        "plan.colForm", "plan.colPatch", "plan.banned", "plan.fromPool", "plan.empty", "plan.failed",
        "plan.tipRecord", "plan.tipToday", "plan.tipLast", "plan.tipPatch", "plan.tipPrev",
    ];

    private static void Strings()
    {
        Console.WriteLine("строки объявления на всех языках:");
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "i18n");
        if (!Directory.Exists(dir)) dir = Path.Combine(Repo(), "assets", "i18n");
        var docs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        Check("языков 15", docs.Count == 15, $"{docs.Count}");
        foreach (var key in NewKeys)
        {
            var ru = Get(docs["ru"], key);
            var holes = Regex.Matches(ru ?? "", @"\{\d\}").Select(m => m.Value).OrderBy(x => x).ToList();
            var bad = docs.Where(kv =>
            {
                var s = Get(kv.Value, key);
                return s is null || !Regex.Matches(s, @"\{\d\}").Select(m => m.Value).OrderBy(x => x).SequenceEqual(holes);
            }).Select(kv => kv.Key).ToList();
            // Перевод, совпавший с английским, — забытый перевод (кроме коротких).
            var english = docs.Where(kv => kv.Key != "en" && Get(kv.Value, key) is { Length: > 12 } s
                                           && s == Get(docs["en"], key)).Select(kv => kv.Key).ToList();
            Check($"  {key}", bad.Count == 0 && english.Count == 0,
                  bad.Count > 0 ? string.Join(",", bad) : english.Count > 0 ? "по-английски: " + string.Join(",", english)
                  : $"{holes.Count} подстановки");
        }
    }

    // ── 2. Разбор драфта ─────────────────────────────────────────────────────

    private static void Parser()
    {
        Console.WriteLine();
        Console.WriteLine("разбор драфта:");
        DraftState Parse(string phase, bool banNow) => ChampSelectParser.Parse(JsonDocument.Parse($$"""
            {"localPlayerCellId":2,
             "myTeam":[{"cellId":2,"assignedPosition":"jungle","championId":0,"championPickIntent":32}],
             "theirTeam":[],
             "timer":{"phase":"{{phase}}"},
             "actions":[[{"id":1,"actorCellId":2,"type":"ban","completed":false,"isInProgress":{{(banNow ? "true" : "false")}},"championId":0}],
                        [{"id":7,"actorCellId":2,"type":"pick","completed":false,"isInProgress":false,"championId":32}]]}
            """).RootElement);

        var plan = Parse("PLANNING", false);
        Check("PLANNING — объявление, и оно же фаза банов", plan is { InPlanning: true, InBanPhase: true }, "");
        Check("в объявлении номер действия пика известен — показ возможен", plan.MyPickActionId == 7,
              $"{plan.MyPickActionId}");
        Check("…а банить ещё нельзя", !plan.MyBanInProgress, "");
        Check("показанный мной чемпион — моё наведение", plan.Me?.PickIntentId == 32, $"{plan.Me?.PickIntentId}");

        var ban = Parse("BAN_PICK", true);
        Check("таймер банов — уже не объявление", ban is { InPlanning: false, InBanPhase: true, MyBanInProgress: true }, "");

        // Отпечаток драфта: на смене фазы окно обязано перерисоваться, даже если
        // больше ничего не поменялось.
        var hash = typeof(OverlayWindow).Assembly.GetType("Program")!
            .GetMethod("DraftHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        string H(DraftState d) => (string)hash.Invoke(null, [d])!;
        Check("отпечаток объявления отличается от отпечатка банов",
              H(plan) != H(plan with { InPlanning = false }), "");
    }

    // ── 3. Журнал с ролями ───────────────────────────────────────────────────

    private const int Sona = 37, Ashe = 22, Amumu = 32, Hwei = 910, Lux = 99, Xerath = 101, Kassadin = 38;
    // Союзники Зерата в игре без ролей — как у владельца 28.09.
    private const int Aphelios = 523, Malphite = 54, Yone = 777, Fiddlesticks = 9;
    // Трандл владельца: 2.10 — саппорт (связка с ролями), 28.09 — состав без ролей.
    private const int Trundle = 48, Mordekaiser = 82, ChoGath = 31, MissFortune = 21;
    private const int Caitlyn = 51, LeeSin = 64, Jax = 24, Vayne = 67, Sett = 875, XinZhao = 5, Syndra = 134,
                      Samira = 360, Ornn = 516, BelVeth = 200, VelKoz = 161, Shen = 98;
    private static long Day(double d) => Now - (long)(d * 86400);

    // Время, когда программа увидела игру, — через полчаса после её начала
    // (в связке записано начало).
    private const long SeenLag = 1800;

    /// Журнал, похожий на владельца: саппорт, Эш тоже саппортом, но без ролей
    /// в журнале (до 1.3.61), один лесной заход.
    private static void WriteJournal(string root)
    {
        var solo = new JsonArray();
        var flex = new JsonArray();
        void Game(JsonArray q, double day, int champ, bool win, int? lp, string? role)
        {
            var g = new JsonObject { ["Ts"] = Day(day), ["ChampionId"] = champ, ["Win"] = win, ["Wr"] = 50.0 };
            if (lp is not null) g["Lp"] = lp;
            if (role is not null) g["Role"] = role;
            q.Add(g);
        }
        // Сона: шесть игр с записанной ролью, LP +20/−18; и одна старая — вне окна.
        bool[] sona = [true, true, false, true, false, true];
        for (int i = 0; i < sona.Length; i++)
            Game(solo, 6 - i, Sona, sona[i], sona[i] ? 20 : -18, "support");
        Game(solo, 40, Sona, true, 20, "support");
        // Эш: три игры без роли. У двух — связки той же игры (саппорт), у третьей
        // связок нет — роль от обычной роли Эш у меня.
        Game(solo, 10, Ashe, true, 19, null);
        Game(solo, 11, Ashe, false, -17, null);
        Game(solo, 12, Ashe, true, 21, null);
        // Амуму: выкинуло в лес — две игры с ролью.
        Game(solo, 2.5, Amumu, true, 18, "jungle");
        Game(solo, 3.5, Amumu, true, 20, "jungle");
        // Хвей: две игры без роли, связки говорят — одна мид, другая саппорт.
        Game(solo, 7, Hwei, false, -19, null);
        Game(solo, 8, Hwei, true, 22, null);
        // Люкс: игра без роли и без связок — главная роль из базы (саппорт 0,61).
        // То, что я почти всегда саппорт, не учитывается намеренно.
        Game(solo, 9, Lux, false, -16, null);
        // Зерат: игра без роли, но в связках без ролей (до 30.09) есть её
        // состав — Афелий, Мальфит, Ёнэ, Фиддлстикс. Жадная раскладка отдавала
        // мид Зерату (0,51 против 0,47 у Ёнэ, топ забрал Мальфит); вероятнее —
        // Ёнэ в миде, Зерат в саппорте. И игра совсем без сведений — как обычно
        // я его играю.
        Game(solo, 13, Xerath, true, 20, null);
        Game(solo, 14, Xerath, false, -18, null);
        // Кассадин без сведений: главная роль — мид, даже у саппорта-мейна.
        Game(solo, 15, Kassadin, true, 19, null);
        // Трандл: 16-й день — связка с ролями (я саппорт); 17-й — только состав
        // (Мордекайзер, Чо'Гат, Люкс, Мисс Фортуна). По одному составу Трандл —
        // лесник; с его игрой на саппорте — саппорт.
        Game(solo, 16, Trundle, true, 21, null);
        Game(flex, 17, Trundle, false, null, null);
        Game(solo, 18, Shen, true, 18, null);

        var normal = new JsonArray();
        Game(normal, 4, Amumu, false, null, "jungle");
        var aram = new JsonArray();
        Game(aram, 1, Sona, true, null, null);

        long Start(double day) => Day(day) - SeenLag;
        JsonObject Pair(long[] won, long[] lost) => new()
        {
            ["Won"] = new JsonArray(won.Select(x => (JsonNode)x).ToArray()),
            ["Lost"] = new JsonArray(lost.Select(x => (JsonNode)x).ToArray()),
            ["Name"] = "Друг",
        };
        var pairs = new JsonObject
        {
            // Эш — саппорт: у 10-го дня победа, у 11-го поражение. Двое союзников —
            // по отметке у каждого, как в жизни.
            [SessionTracker.PairKey("ally1", "solo", Ashe, 51, "support", "adc")] = Pair([Start(10)], [Start(11)]),
            [SessionTracker.PairKey("ally2", "solo", Ashe, 64, "support", "jungle")] = Pair([Start(10)], [Start(11)]),
            // Хвей: 7-й день — мид (поражение), 8-й — саппорт (победа).
            [SessionTracker.PairKey("ally1", "solo", Hwei, 24, "mid", "top")] = Pair([], [Start(7)]),
            [SessionTracker.PairKey("ally3", "solo", Hwei, 51, "support", "adc")] = Pair([Start(8)], []),
            // Старая связка без ролей у игры, где роль уже есть по другой связке, —
            // мешать она не должна.
            [SessionTracker.PairKey("ally4", "solo", Hwei, 67)] = Pair([Start(7)], []),
            // Трандл, 16-й день: связка с ролями и полным составом (ясно — саппорт);
            // 17-й, флекс: состав без ролей.
            [SessionTracker.PairKey("t1", "solo", Trundle, Sett, "support", "top")] = Pair([Start(16)], []),
            [SessionTracker.PairKey("t6", "solo", Trundle, XinZhao, "support", "jungle")] = Pair([Start(16)], []),
            [SessionTracker.PairKey("t7", "solo", Trundle, Syndra, "support", "mid")] = Pair([Start(16)], []),
            [SessionTracker.PairKey("t8", "solo", Trundle, Samira, "support", "adc")] = Pair([Start(16)], []),
            // Шен, 18-й день: в связке записан мид (жадная раскладка отдала
            // Вел'Козу саппорт), а по составу вероятнее Вел'Коз в миде, Шен —
            // саппорт; так и было у владельца.
            [SessionTracker.PairKey("s1", "solo", Shen, Ornn, "mid", "top")] = Pair([Start(18)], []),
            [SessionTracker.PairKey("s2", "solo", Shen, BelVeth, "mid", "jungle")] = Pair([Start(18)], []),
            [SessionTracker.PairKey("s3", "solo", Shen, VelKoz, "mid", "support")] = Pair([Start(18)], []),
            [SessionTracker.PairKey("s4", "solo", Shen, Caitlyn, "mid", "adc")] = Pair([Start(18)], []),
            [SessionTracker.PairKey("t2", "flex", Trundle, Mordekaiser)] = Pair([], [Start(17)]),
            [SessionTracker.PairKey("t3", "flex", Trundle, ChoGath)] = Pair([], [Start(17)]),
            [SessionTracker.PairKey("t4", "flex", Trundle, Lux)] = Pair([], [Start(17)]),
            [SessionTracker.PairKey("t5", "flex", Trundle, MissFortune)] = Pair([], [Start(17)]),
            // Зерат, 13-й день: состав без ролей.
            [SessionTracker.PairKey("a1", "solo", Xerath, Aphelios)] = Pair([Start(13)], []),
            [SessionTracker.PairKey("a2", "solo", Xerath, Malphite)] = Pair([Start(13)], []),
            [SessionTracker.PairKey("a3", "solo", Xerath, Yone)] = Pair([Start(13)], []),
            [SessionTracker.PairKey("a4", "solo", Xerath, Fiddlesticks)] = Pair([Start(13)], []),
        };

        var store = new JsonObject
        {
            ["LastAccount"] = "test-puuid",
            ["Accounts"] = new JsonObject
            {
                ["test-puuid"] = new JsonObject
                {
                    ["Nick"] = "Тест",
                    ["Queues"] = new JsonObject
                    {
                        ["solo"] = new JsonObject { ["Games"] = solo },
                        ["flex"] = new JsonObject { ["Games"] = flex },
                        ["normal"] = new JsonObject { ["Games"] = normal },
                        ["aram"] = new JsonObject { ["Games"] = aram },
                    },
                    ["Pairs"] = pairs,
                },
            },
        };
        File.WriteAllText(Path.Combine(root, "session.json"), store.ToJsonString());
    }

    // Доли ролей, как в базе (порядок величин живой базы, изумруд 16.19).
    private static double ShareStub(int champ, string role) => (champ, role) switch
    {
        (Sona, "support") => 0.95, (Sona, "mid") => 0.05,
        (Ashe, "adc") => 0.85, (Ashe, "support") => 0.15,
        (Amumu, "jungle") => 0.9, (Amumu, "support") => 0.1,
        (Hwei, "mid") => 0.7, (Hwei, "support") => 0.3,
        (Lux, "mid") => 0.33, (Lux, "support") => 0.61, (Lux, "adc") => 0.05,
        (Trundle, "top") => 0.65, (Trundle, "jungle") => 0.30, (Trundle, "support") => 0.03, (Trundle, "mid") => 0.01,
        (Mordekaiser, "top") => 0.91, (Mordekaiser, "jungle") => 0.05, (Mordekaiser, "mid") => 0.03,
        (Mordekaiser, "support") => 0.01,
        (ChoGath, "top") => 0.32, (ChoGath, "jungle") => 0.60, (ChoGath, "mid") => 0.05,
        (ChoGath, "support") => 0.02, (ChoGath, "adc") => 0.01,
        (MissFortune, "adc") => 0.95, (MissFortune, "support") => 0.04, (MissFortune, "mid") => 0.01,
        (Caitlyn, "adc") => 0.95, (LeeSin, "jungle") => 0.95,
        (Jax, "top") => 0.6, (Jax, "jungle") => 0.35, (Vayne, "adc") => 0.6, (Vayne, "top") => 0.35,
        (Sett, "top") => 0.8, (Sett, "support") => 0.03, (Sett, "jungle") => 0.05,
        (XinZhao, "jungle") => 0.95, (Syndra, "mid") => 0.9, (Samira, "adc") => 0.95,
        (Ornn, "top") => 0.9, (BelVeth, "jungle") => 0.95,
        (VelKoz, "support") => 0.6, (VelKoz, "mid") => 0.4,
        (Shen, "top") => 0.62, (Shen, "support") => 0.3, (Shen, "jungle") => 0.05, (Shen, "mid") => 0.01,
        (Xerath, "mid") => 0.51, (Xerath, "support") => 0.39, (Xerath, "adc") => 0.1,
        (Kassadin, "mid") => 0.94, (Kassadin, "top") => 0.05,
        (Aphelios, "adc") => 0.97,
        (Malphite, "top") => 0.77, (Malphite, "mid") => 0.09, (Malphite, "jungle") => 0.08, (Malphite, "support") => 0.05,
        (Yone, "top") => 0.5, (Yone, "mid") => 0.47,
        (Fiddlesticks, "jungle") => 0.9, (Fiddlesticks, "support") => 0.05,
        _ => 0,
    };

    private static void Lanes()
    {
        Console.WriteLine();
        Console.WriteLine("мои чемпионы по роли:");
        string Show(IEnumerable<SessionTracker.LaneChamp> l) =>
            string.Join(", ", l.Select(c => $"{c.ChampionId}×{c.Games}"));

        var jungle = SessionTracker.LaneChamps("jungle", ShareStub);
        Check("выкинуло в лес — только лесные игры, саппортов нет",
              jungle.Count == 1 && jungle[0] is { ChampionId: Amumu, Games: 3 }, Show(jungle));
        Check("LP — только из ранкеда (нормал без очков)", jungle.FirstOrDefault()?.Lp == 38,
              $"{jungle.FirstOrDefault()?.Lp}");

        var sup = SessionTracker.LaneChamps("support", ShareStub);
        Check("саппорт: Сона, Эш, Зерат, Трандл, Хвей, Люкс, Шен — самые игранные, потом свежие",
              sup.Select(c => c.ChampionId).SequenceEqual([Sona, Ashe, Xerath, Trundle, Hwei, Lux, Shen]), Show(sup));
        Check("Шен — саппорт: записанный в связке мид перебит ясным составом",
              sup.Any(c => c.ChampionId == Shen), "");
        Check("Трандл по составу — саппорт: так я играл его в другой игре",
              sup.FirstOrDefault(c => c.ChampionId == Trundle)?.Games == 2, "");
        // Сам состав, без моих игр на Трандле, говорит «лес»: склонности
        // игрока «вообще» в раскладку не входят.
        var best = typeof(SessionTracker).GetMethod("BestRoles", BindingFlags.NonPublic | BindingFlags.Static)!;
        var plain = (string[]?)best.Invoke(null, [new List<int> { Trundle, Mordekaiser, ChoGath, Lux, MissFortune },
                                                  (Func<int, string, double>)ShareStub, null, null]);
        Check("без моих игр на чемпионе — решает состав (Трандл-лесник)", plain?[0] == "jungle",
              string.Join(" ", plain ?? []));
        Check("Зерат по составу игры — саппорт (жадная раскладка давала мид), и без сведений — тоже",
              sup.FirstOrDefault(c => c.ChampionId == Xerath)?.Games == 2, "");
        var sona = sup.FirstOrDefault(c => c.ChampionId == Sona);
        Check("старая игра и ARAM в счёт не идут", sona?.Games == 6, $"{sona?.Games}");
        Check("форма — от старых к новым", sona is not null && sona.Form.SequenceEqual([true, true, false, true, false, true]),
              string.Join("", sona?.Form.Select(w => w ? "П" : "п") ?? []));
        Check("LP Соны за месяц: 4 × 20 − 2 × 18 = 44", sona?.Lp == 44, $"{sona?.Lp}");
        var ashe = sup.FirstOrDefault(c => c.ChampionId == Ashe);
        Check("Эш без ролей в журнале — саппорт (по связкам, а не стрелок по базе)",
              ashe?.Games == 3, $"{ashe?.Games}");
        Check("Хвей: в саппорты — только игра со связкой саппорта", sup.FirstOrDefault(c => c.ChampionId == Hwei)?.Games == 1, "");

        var mid = SessionTracker.LaneChamps("mid", ShareStub);
        Check("мид: Хвей по связке той же игры и Кассадин по главной роли; ни Люкс, ни Зерата",
              mid.Select(c => c.ChampionId).OrderBy(x => x).SequenceEqual([Kassadin, Hwei]), Show(mid));
        var adc = SessionTracker.LaneChamps("adc", ShareStub);
        Check("стрелок пуст: Эш туда не попала", adc.Count == 0, Show(adc));
        Check("роль неизвестна — пусто", SessionTracker.LaneChamps("", ShareStub).Count == 0, "");
    }

    /// Новая игра получает роль из драфта, если драфт был прямо перед ней и на
    /// том же чемпионе; иначе — из подробностей игры.
    private static void DraftRole()
    {
        Console.WriteLine();
        Console.WriteLine("роль новой игры из драфта:");
        var t = typeof(SessionTracker);
        var entry = t.GetNestedType("HistEntry", BindingFlags.NonPublic)!;
        var of = t.GetMethod("DraftRoleOf", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? Role(int champ, long created) =>
            (string?)of.Invoke(null, [Activator.CreateInstance(entry, 1L, "solo", champ, true, created, 0L)!]);

        SessionTracker.NoteDraftRole(Amumu, "jungle");
        Check("тот же чемпион, игра сразу после драфта — роль драфта", Role(Amumu, Now + 90) == "jungle", "");
        Check("другой чемпион — не эта игра", Role(Sona, Now + 90) is null, "");
        Check("через час — уже другая игра", Role(Amumu, Now + 3600) is null, "");
        Check("игра раньше драфта — не эта", Role(Amumu, Now - 3600) is null, "");
    }

    /// Синхронизация: та же игра без роли получает роль с другого компьютера.
    private static void SyncRole()
    {
        Console.WriteLine();
        Console.WriteLine("синхронизация:");
        string Journal(params (long Ts, string? Role)[] games) => new JsonObject
        {
            ["Accounts"] = new JsonObject
            {
                ["p"] = new JsonObject
                {
                    ["Queues"] = new JsonObject
                    {
                        ["solo"] = new JsonObject
                        {
                            ["Games"] = new JsonArray(games.Select(g =>
                            {
                                var o = new JsonObject { ["Ts"] = g.Ts, ["ChampionId"] = Sona, ["Win"] = true };
                                if (g.Role is not null) o["Role"] = g.Role;
                                return (JsonNode)o;
                            }).ToArray()),
                        },
                    },
                },
            },
        }.ToJsonString();

        var merge = typeof(SyncClient).GetMethod("MergeSession", BindingFlags.NonPublic | BindingFlags.Static)!;
        var merged = JsonNode.Parse((string)merge.Invoke(null, [Journal((100, null), (200, "mid")),
                                                                Journal((100, "support"), (300, "jungle"))])!)!;
        var games = merged["Accounts"]!["p"]!["Queues"]!["solo"]!["Games"]!.AsArray()
            .ToDictionary(g => (long)g!["Ts"]!, g => (string?)g!["Role"]);
        Check("та же игра — роль с другого компьютера", games.GetValueOrDefault(100) == "support",
              games.GetValueOrDefault(100) ?? "нет");
        Check("своя роль остаётся", games.GetValueOrDefault(200) == "mid", "");
        Check("новая игра приходит с ролью", games.GetValueOrDefault(300) == "jungle", "");
    }

    // ── 4. Патч ──────────────────────────────────────────────────────────────

    private static void Patch(RecommendationEngine engine)
    {
        var sona = engine.PatchWr(Sona, "support");
        Check("винрейт Соны-саппорта в патче есть", sona is { Wr: > 35 and < 65 },
              sona is { } s ? $"{s.Wr:F1}%, сдвиг {s.Delta?.ToString("+0.0;-0.0") ?? "нет"}" : "нет");
        Check("Сона в лесу — выборки нет, числа нет", engine.PatchWr(Sona, "jungle") is null, "");
    }

    // ── 5. Окно ──────────────────────────────────────────────────────────────

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
        var hovered = new List<int>();
        var banHovered = new List<int>();
        w.HoverHandler    = id => { hovered.Add(id); return Task.FromResult(200); };
        w.BanHoverHandler = id => { banHovered.Add(id); return Task.FromResult(200); };
        Pump();

        List<PlanRow> Rows() => (((ItemsControl)w.FindName("PlanList")).ItemsSource as IEnumerable<PlanRow>)?.ToList() ?? [];

        // Выкинуло в лес.
        var jungle = Draft("jungle", planning: true);
        w.UpdateBans([], jungle, engine);
        Pump();
        Check("объявление — мои чемпионы вместо советов банов",
              Vis(w, "PlanPanel") && !Vis(w, "BanScroll") && !Vis(w, "BansDonePanel"), "");
        Check("тир-лист под ними", Vis(w, "TierListBar"), "");
        var rows = Rows();
        Check("в лесу — только Амуму, саппортов нет", rows.Select(r => r.ChampionId).SequenceEqual([Amumu]),
              string.Join(", ", rows.Select(r => r.Name)));

        // Своя роль — саппорт.
        var sup = Draft("utility", planning: true);
        w.UpdateBans([], sup, engine);
        Pump();
        rows = Rows();
        // Доли ролей — из живой базы: Зерат по составу игры — саппорт.
        Check("саппорт: Сона, Эш, Зерат, Трандл первыми, Амуму и Кассадина нет",
              rows.Take(4).Select(r => r.ChampionId).SequenceEqual([Sona, Ashe, Xerath, Trundle])
              && !rows.Any(r => r.ChampionId is Amumu or Kassadin),
              string.Join(", ", rows.Select(r => r.Name)));
        var sona = rows.FirstOrDefault(r => r.ChampionId == Sona);
        Check("у Соны: винрейт и игры, форма, LP, патч",
              sona is { WrText: "67%", GamesText: "· 6", LpText: "+44" } && sona.Form.Count == 6
              && sona.PatchText.EndsWith('%'),
              sona is null ? "нет" : $"{sona.WrText} {sona.GamesText} {sona.LpText} {sona.PatchText} {sona.DeltaText}");
        var few = rows.Where(r => r.Games is > 0 and < 5).ToList();
        Check("меньше пяти игр — винрейт без цвета", few.Count > 0 && few.All(r => r.WrBrush == "#8AA0B2"),
              string.Join(", ", few.Select(r => $"{r.Name} {r.Games}: {r.WrBrush}")));
        Check("в подсказке — победы и поражения, последняя игра",
              sona?.Tip?.Contains(Loc.T("plan.tipRecord", 4, 2)) == true && sona.Tip.Contains(Loc.T("plan.tipLast", 1)),
              (sona?.Tip ?? "").Replace("\n", " | "));
        if (snap is { Length: > 0 }) Snap(w, snap, "plan-1-support.png");

        // Клик по строке — показать пик союзникам: наведение пика, не бана.
        var click = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Call(w, "PlanCard_Click", new Border { Tag = Sona }, click);
        Pump();
        Check("клик по Соне — показ пика в клиенте", hovered.LastOrDefault() == Sona && banHovered.Count == 0,
              string.Join(",", hovered));
        Check("её строка выделена", Rows().FirstOrDefault(r => r.ChampionId == Sona)?.IsSelected == true, "");
        Call(w, "TierCell_Click", new Border { Tag = 412 }, click);
        Pump();
        Check("клик по тир-листу в объявлении — тоже показ пика, не бан",
              hovered.LastOrDefault() == 412 && banHovered.Count == 0, string.Join(",", hovered));

        // Игр на роли нет — пул на неё.
        var pools = PoolStore.Current();
        var pool = new ChampPool { Name = "Тест", ByRole = new() { ["top"] = [86, 122] } };
        pools.Pools.Add(pool);
        PoolStore.Persist();
        PoolStore.SetActive(PoolKind.Pool, pool.Id);
        var top = Draft("top", planning: true);
        w.UpdateBans([], top, engine);
        Pump();
        rows = Rows();
        Check("на топе игр нет — чемпионы пула без своих чисел",
              rows.Select(r => r.ChampionId).SequenceEqual([86, 122]) && rows.All(r => r.Games == 0 && r.WrText == "—"),
              string.Join(", ", rows.Select(r => r.Name)));
        Check("заголовок говорит, что это пул", Text(w, "PlanTitle") == Loc.T("plan.titlePool", Loc.T("roles.top")),
              Text(w, "PlanTitle"));

        // Ни игр, ни пула.
        var adc = Draft("bottom", planning: true);
        w.UpdateBans([], adc, engine);
        Pump();
        Check("ни игр, ни пула — строка-пояснение", Rows().Count == 0 && Vis(w, "PlanEmpty"), Text(w, "PlanEmpty"));

        // Таймер банов пошёл — советы банов.
        var bans = sup with { InPlanning = false, MyBanActionId = 0, MyBanInProgress = true };
        w.UpdateBans(engine.RecommendBans(bans), bans, engine);
        Pump();
        Check("баны начались — советы банов, моих чемпионов нет", Vis(w, "BanScroll") && !Vis(w, "PlanPanel"), "");

        w.Close();
        Pump();
    }

    private static DraftState Draft(string pos, bool planning)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, pos, true) };
        return new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: team[0], MyPosition: pos, DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: !planning, MyBanDone: false,
            InPlanning: planning);
    }

    // ── Общее ────────────────────────────────────────────────────────────────

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

    /// Имена и иконки чемпионов. Кэш игрока копируем к себе — его папку не трогаем.
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

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static string Text(Window w, string name) => (w.FindName(name) as TextBlock)?.Text ?? "";

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
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
