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
/// 1в. Один рейтинг вместо закреплённых мест (14.10): кто бьёт и мой пул, и
///    пики союзников, поднимается; жёсткая контра моему — в пятёрке всегда.
/// 1г. Каждый бан учитывает мой пул (14.10): союзные баны набирают очки и за
///    него, прямых контр пулу в пятёрке не меньше двух.
/// 1д. Вес мейна в банах растёт с его синергией с союзниками (та же, что на пиках).
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
        AllyPool(engine);
        Partners(engine);
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

    private static readonly string[] Lanes = ["top", "jungle", "mid", "adc", "support"];

    private static string PoolHead => Loc.T("reason.countersPoolPick", "\u0001", "").Split('\u0001')[0];

    private static string HardHead => Loc.T("reason.hardCounter", "\u0001", "").Split('\u0001')[0];

    private static void Meet(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("контры к мейнам взвешены пикрейтом:");
        var rolePicks = Lanes.ToDictionary(r => r, r => engine.PickRates(r));
        var roleRef = Lanes.ToDictionary(r => r, r => rolePicks[r].Values.Where(p => p >= 0.5).Average());
        foreach (var (lcu, role, mains) in Cases)
        {
            SessionTracker.HistoryOverride = Hist(mains);
            var picks = engine.PickRates(role);
            var bans  = engine.RecommendBans(Draft(lcu), top: 5);
            var line  = string.Join(", ", bans.Select(b => $"{b.ChampionId}:{picks.GetValueOrDefault(b.ChampionId):F1}%"));
            Console.WriteLine($"  {role,-8} {line}");

            Check($"{role}: банов пять", bans.Count == 5, $"{bans.Count}");
            // С 14.10 мой пул учитывает и кандидатов с других линий (как мой
            // чемпион играет против них в той же игре): у стрелка в банах бывают
            // саппорты и Мальфит. Поэтому кандидат обязан играться не на моей
            // роли, а на своей — той, где его встречаемость наибольшая.
            var best = bans.Select(b => (b.ChampionId, Lane: Lanes.Select(r => (Role: r,
                                Pick: rolePicks[r].GetValueOrDefault(b.ChampionId), Ref: roleRef[r]))
                            .MaxBy(x => x.Pick / x.Ref))).ToList();
            var rare = best.Where(b => b.Lane.Pick < 0.5).ToList();
            Check($"{role}: нет офф-меты (< 0,5% игр своей роли)", rare.Count == 0,
                  rare.Count == 0 ? "все играются" : string.Join(", ", rare.Select(b => $"{b.ChampionId} {b.Lane.Role} {b.Lane.Pick:F1}%")));
            // Встречаемость сдвигает список к тем, кого берут: в среднем пятёрку
            // берут чаще среднего регулярного пика их ролей.
            var ratio = best.Average(b => b.Lane.Pick / b.Lane.Ref);
            Check($"{role}: пятёрка банов берётся чаще среднего пика своих ролей", ratio > 1,
                  $"в {ratio:F2} раза");
            Check($"{role}: прямых контр мейнам на моей линии — не меньше двух",
                  bans.Count(b => picks.GetValueOrDefault(b.ChampionId) >= 0.5 && b.Reasons.Any(r =>
                      r.StartsWith(PoolHead, StringComparison.Ordinal) || r.StartsWith(HardHead, StringComparison.Ordinal))) >= 2,
                  string.Join(", ", bans.Select(b => b.ChampionId)));
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
        // Защищать — значит учитывать в рейтинге, а не держать за ним место: с
        // 14.10 мест у защиты союзников нет, и в пятёрку контра Вуконгу
        // проходит, только если набрала больше контр моему пулу.
        var wide = engine.RecommendBans(state, history, top: 30).SelectMany(b => b.Reasons).ToList();
        Check("сменённый пик союзника по-прежнему защищают", wide.Any(r => r.Contains(DataDragon.Name(62))),
              wide.FirstOrDefault(r => r.Contains(DataDragon.Name(62))) ?? "о Вуконге ни слова");

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

    // ── 1в. Один рейтинг: пул и союзники вместе, жёсткие контры — в пятёрке ──

    /// <summary>
    /// Было: моей линии три первых места, защите союзников — не больше двух
    /// (после скриншота владельца-саппорта, где четыре бана из пяти были про
    /// Ирелию и Мастера Йи). Владелец 14.10: «убери фиксированное расположение
    /// — если персонаж контрит пару из моего пула и пару союзников, он должен
    /// подниматься на первое место», и следом: «у жёстких контр на линии против
    /// моих персонажей должен быть буст, и они по-любому должны быть в топах».
    ///
    /// Теперь порядок — по очкам; кто бьёт и мой пул, и показанные пики
    /// союзников, складывает очки и поднимается; жёсткая контра моему чемпиону
    /// в пятёрке всегда. Веса журнала — как у владельца за месяц.
    /// </summary>
    private static void Lane(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("один рейтинг:");
        var owner = new Dictionary<int, int> { [37] = 32, [201] = 19, [22] = 18, [161] = 17, [910] = 10, [101] = 8 };
        SessionTracker.HistoryOverride = Hist(owner);
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
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {b.Reasons.FirstOrDefault()}");

        Check("порядок — по очкам, без закреплённых мест",
              bans.Zip(bans.Skip(1)).All(p => p.First.Score >= p.Second.Score),
              string.Join(" ≥ ", bans.Select(b => $"{b.Score:F1}")));
        int[] mineIds = [.. owner.Keys];
        Check("своих мейнов не банит", !bans.Any(b => mineIds.Contains(b.ChampionId)),
              string.Join(", ", bans.Where(b => mineIds.Contains(b.ChampionId)).Select(b => DataDragon.Name(b.ChampionId))));
        var pool = Loc.T("reason.countersPoolPick", "\u0001", "").Split('\u0001')[0];
        var hard = Loc.T("reason.hardCounter", "\u0001", "").Split('\u0001')[0];
        var ally = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var many = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        bool Has(BanRec b, params string[] heads) => b.Reasons.Any(r => heads.Any(h => r.StartsWith(h, StringComparison.Ordinal)));
        Check("в пятёрке есть контры моему пулу", bans.Any(b => Has(b, pool, hard)), "");
        Check("и контры пикам союзников", bans.Any(b => Has(b, ally, many)), "");

        // Жёсткие контры моему пулу: на этой сцене они есть (Блицкранк бьёт Эш
        // на 6 с лишним очков) — значит, хоть одна обязана стоять в пятёрке,
        // даже когда союзники показали чемпионов с сильными контрами.
        var wide = engine.RecommendBans(state, new Dictionary<int, HashSet<int>> { [0] = [39], [1] = [11] }, top: 40);
        Check("жёсткие контры моим вообще находятся", wide.Any(b => Has(b, hard)), $"{wide.Count(b => Has(b, hard))} из {wide.Count}");
        Check("жёсткая контра моему — в пятёрке", bans.Any(b => Has(b, hard)),
              bans.FirstOrDefault(b => Has(b, hard)) is { } h ? $"{DataDragon.Name(h.ChampionId)}: {h.Reasons[0]}" : "нет");
        // И стоят выше, чем стояли бы без надбавки: первая жёсткая — в первой тройке.
        var firstHard = bans.ToList().FindIndex(b => Has(b, hard));
        Check("и стоит в первой тройке", firstHard is >= 0 and < 3, $"место {firstHard + 1}");

        CrossLane(engine, owner, "utility", "support");
        CrossLane(engine, new Dictionary<int, int> { [86] = 25, [122] = 12 }, "top", "top");
        CrossLane(engine, new Dictionary<int, int> { [103] = 20, [134] = 15, [61] = 8 }, "middle", "mid");
        Check("перекрёстный кандидат поднимается на первое место (хоть на одной из трёх ролей)",
              _crossFirst > 0, $"{_crossFirst} раз");

        // Пул из профиля: истории нет, в активном пуле саппорта — Сона и Браум.
        // Раньше баны пул не читали, и контрить было бы некого.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>());
        var poolDef = new ChampPool { Name = "проверка", ByRole = new() { ["support"] = [37, 201] } };
        PoolStore.Current().Pools.Add(poolDef);
        PoolStore.SetActive(PoolKind.Pool, poolDef.Id);
        var me = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        var poolReasons = engine.RecommendBans(Snapshot(me, me[0], "utility"), null, top: 5)
                                .SelectMany(b => b.Reasons)
                                .Where(r => r.StartsWith(pool, StringComparison.Ordinal) || r.StartsWith(hard, StringComparison.Ordinal)).ToList();
        Check("пул из профиля: контры ему есть без истории", poolReasons.Count > 0, poolReasons.FirstOrDefault() ?? "нет");
        Check("называют чемпионов пула", poolReasons.All(r => r.Contains(DataDragon.Name(37)) || r.Contains(DataDragon.Name(201))), "");
        // Пул целиком банов не закрывает (решение владельца: только наигранные
        // за месяц) — чемпион пула без игр в банах допустим, а с играми — нет.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int> { [201] = 12 });
        var withGames = engine.RecommendBans(Snapshot(me, me[0], "utility"), null, top: 10);
        Check("сыгранный за месяц из пула не банится", withGames.All(b => b.ChampionId != 201), "");
        PoolStore.SetActive(PoolKind.Pool, null);
        PoolStore.Current().Pools.Remove(poolDef);
        SessionTracker.HistoryOverride = null;
    }

    // ── 1г. Каждый бан учитывает мой пул ─────────────────────────────────────

    /// <summary>
    /// Скриншот владельца 14.10 (саппорт; Фиора, Мастер Йи — до него наводили
    /// Грейвза, — Хвэй, Тристана): Вуконг, Мальфит, Катарина, Наутилус, Раммус,
    /// и у первых трёх за мой пул ни очка. Владелец: «все баны должны учитывать
    /// мой пул — из трёх контр союзникам выбрать ту, что законтрит и мой пул;
    /// часть банов — чётко против пула».
    ///
    /// Причин было три: (1) против пула на линии почти никого не осталось —
    /// «сыгранные за месяц не банятся» срабатывало с одной игры (Блицкранк,
    /// Пайк, Нами); (2) четверо союзников по 2,6 перевешивали весь пул (4,5);
    /// (3) кандидат с чужой линии про мой пул ничего не знал. Теперь: свои — от
    /// трёх игр, союзники вместе весят как двое, а как мой пул играет против
    /// кандидата в той же игре с другой линии, считается по кросс-ролевой
    /// таблице.
    /// </summary>
    private static void AllyPool(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("каждый бан учитывает мой пул:");
        // Журнал владельца за месяц: мейны и горстка чемпионов по одной-три игры.
        var games = new Dictionary<int, int>
        {
            [37] = 32, [201] = 22, [22] = 19, [161] = 15, [910] = 12, [101] = 8, [78] = 8, [117] = 6,
            [412] = 3, [53] = 1, [555] = 1, [267] = 1,   // Треш 3, Блицкранк, Пайк, Нами — по одной
        };
        var team = new List<DraftPlayer>
        {
            new(0, 0, 114, "top", false),      // Фиора
            new(1, 0, 11, "jungle", false),    // Мастер Йи (до него — Грейвз)
            new(2, 0, 910, "middle", false),   // Хвэй
            new(3, 0, 18, "bottom", false),    // Тристана
            new(4, 0, 0, "utility", true),
        };
        var state = Snapshot(team, team[4], "utility");
        var hist = new Dictionary<int, HashSet<int>> { [0] = [114], [1] = [104, 11], [2] = [910], [3] = [18] };

        SessionTracker.HistoryOverride = Hist(games);
        var bans = engine.RecommendBans(state, hist, top: 5);
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {string.Join(" | ", b.Reasons.Take(2))}");
        var wide = engine.RecommendBans(state, hist, top: 40);

        Check("чемпион с одной игрой за месяц банится (Блицкранк)", wide.Any(b => b.ChampionId == 53),
              wide.Any(b => b.ChampionId == 53) ? $"место {wide.ToList().FindIndex(b => b.ChampionId == 53) + 1}" : "нет в сорока");
        Check("с тремя играми — нет (Треш)", wide.All(b => b.ChampionId != 412), "");

        var also  = Loc.T("reason.alsoPool", "\u0001").Split('\u0001')[0];
        var ally  = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var many  = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        bool Has(BanRec b, params string[] heads) => b.Reasons.Any(r => heads.Any(h => r.StartsWith(h, StringComparison.Ordinal)));
        Check("прямых контр моему пулу в пятёрке не меньше двух", bans.Count(b => Has(b, PoolHead, HardHead)) >= 2,
              $"{bans.Count(b => Has(b, PoolHead, HardHead))}");
        var allyBans = bans.Where(b => Has(b, ally, many)).ToList();
        Check("баны против союзников говорят, что мешают и моему пулу",
              allyBans.Count > 0 && allyBans.Any(b => Has(b, also)),
              string.Join("; ", allyBans.Select(b => $"{DataDragon.Name(b.ChampionId)}: {string.Join(" | ", b.Reasons.Take(2))}")));

        // Тот же драфт без пула: у союзных банов, которые мешают пулу, очков
        // меньше — значит, пул в их счёте правда участвует.
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>());
        var bare = engine.RecommendBans(state, hist, top: 40).ToDictionary(b => b.ChampionId, b => b.Score);
        foreach (var b in allyBans.Where(b => Has(b, also)))
            Check($"   {DataDragon.Name(b.ChampionId)}: с моим пулом очков больше, чем без него",
                  bare.TryGetValue(b.ChampionId, out var without) && b.Score > without,
                  $"{(bare.TryGetValue(b.ChampionId, out var w0) ? w0.ToString("F1") : "—")} → {b.Score:F1}");
        SessionTracker.HistoryOverride = null;
    }

    // ── 1д. Синергия мейна с союзниками — вес в банах ────────────────────────

    /// <summary>
    /// Скриншот владельца 14.10: союзники выбрали Ирелию, Катарину и Тристану, и
    /// у них уже видна синергия с моим пулом — «на пиках мне предложат того, кто
    /// с ними сочетается, контрам ему — повышенный рейтинг». Первым заходом вес
    /// поднимали по иконкам под портретами, и надбавка доставалась одному-двум
    /// (Браум ×1,45, Сона ×1,15); владелец: «не по иконкам, а по синергии в
    /// целом — тем же алгоритмом, что на пиках, в пропорциях».
    ///
    /// Держим: вес мейна в банах × (1 + 0,15 × синергия), где синергия — ровно
    /// полоска «синергия» из подбора пика (Recommend → SynergyDelta) на том же
    /// драфте; надбавку получают несколько чемпионов пула, больше — у кого
    /// синергия выше. Веса — из внутреннего списка «наших» через отражение.
    /// </summary>
    private static void Partners(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("синергия мейна с союзниками:");
        int[] mains = [37, 201, 22, 161, 910, 101, 78, 117];
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>
            { [37] = 32, [201] = 22, [22] = 19, [161] = 15, [910] = 12, [101] = 8, [78] = 8, [117] = 6 });
        var team = new List<DraftPlayer>
        {
            new(0, 0, 39, "top", false),       // Ирелия
            new(1, 0, 0, "jungle", false),
            new(2, 0, 55, "middle", false),    // Катарина
            new(3, 0, 18, "bottom", false),    // Тристана
            new(4, 0, 0, "utility", true),
        };
        var state = Snapshot(team, team[4], "utility");
        // Синергия из подбора пика на том же драфте.
        var syn = engine.Recommend(state with { InBanPhase = false }, mains.Length, mains)
                        .ToDictionary(r => r.ChampionId, r => r.SynergyDelta);

        var withAllies = PoolWeights(engine, state);
        var me = new List<DraftPlayer> { new(4, 0, 0, "utility", true) };
        var alone = PoolWeights(engine, Snapshot(me, me[0], "utility"));
        var got = mains.Where(alone.ContainsKey).ToDictionary(m => m, m => withAllies.GetValueOrDefault(m) / alone[m]);
        foreach (var m in got.Keys.OrderByDescending(m => syn.GetValueOrDefault(m)))
        {
            var want = 1 + 0.15 * Math.Max(0, syn.GetValueOrDefault(m));
            Check($"   {DataDragon.Name(m)}: синергия {syn.GetValueOrDefault(m):+0.00;-0.00} → вес × {want:F2}",
                  Math.Abs(got[m] - want) < 1e-9, $"× {got[m]:F2}");
        }
        Check("надбавку получает не один чемпион пула", got.Values.Count(x => x > 1.0) >= 3,
              $"{got.Values.Count(x => x > 1.0)} из {got.Count}");
        SessionTracker.HistoryOverride = null;
    }

    /// Вес каждого чемпиона моего пула в банах на этом драфте (BanVictims).
    private static Dictionary<int, double> PoolWeights(RecommendationEngine engine, DraftState state)
    {
        var t = typeof(RecommendationEngine);
        var stats = t.GetMethod("RoleStats", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, ["support"])!;
        var list = (System.Collections.IEnumerable)t.GetMethod("BanVictims", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, [state, null, "support", stats])!;
        object P(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
        return list.Cast<object>().Where(v => P(v, "Kind").ToString() == "Pool")
                   .ToDictionary(v => (int)P(v, "Id"), v => (double)P(v, "Weight"));
    }

    private static int _crossFirst;

    /// <summary>
    /// Пример владельца на живой базе: кандидат моей линии, которого берут и на
    /// другой линии, бьёт там двоих — союзник с этой линии показывает обоих
    /// (одного сейчас, второго наводил). Его очки обязаны вырасти, а места —
    /// подниматься (у отдельного кандидата место может и не вырасти: тех же
    /// жертв бьёт кто-то сильнее). Кандидатов и жертв выбираем по базе: патч
    /// меняется, а правило — нет.
    /// </summary>
    private static void CrossLane(RecommendationEngine engine, Dictionary<int, int> games, string pos, string role)
    {
        string[] lanes = ["top", "jungle", "mid", "adc", "support"];
        string[] lcu   = ["top", "jungle", "middle", "bottom", "utility"];
        SessionTracker.HistoryOverride = Hist(games);
        var me = new List<DraftPlayer> { new(0, 0, 0, pos, true) };
        var alone = engine.RecommendBans(Snapshot(me, me[0], pos), null, top: 40);
        int found = 0, rose = 0;
        foreach (var x in alone.Take(30))
            for (int li = 0; li < lanes.Length && found < 6; li++)
            {
                var lane = lanes[li];
                if (lane == role || engine.PickRates(lane).GetValueOrDefault(x.ChampionId) < 0.5) continue;
                // Кого он бьёт на той линии: он в топе контрпиков у них.
                var victims = engine.PickRates(lane)
                    .Where(kv => kv.Value >= 1.0 && kv.Key != x.ChampionId && !games.ContainsKey(kv.Key))
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => kv.Key)
                    .Where(v => engine.TopCounters(v, lane, 5).Contains(x.ChampionId))
                    .Take(2).ToList();
                if (victims.Count < 2) continue;
                found++;
                var team = new List<DraftPlayer> { me[0], new(1, victims[0], 0, lcu[li], false) };
                var hist = new Dictionary<int, HashSet<int>> { [1] = [victims[0], victims[1]] };
                var with = engine.RecommendBans(Snapshot(team, team[0], pos), hist, top: 40);
                var after = with.FirstOrDefault(b => b.ChampionId == x.ChampionId);
                var rankBefore = alone.ToList().IndexOf(x) + 1;
                var rankAfter  = after is null ? 99 : with.ToList().IndexOf(after) + 1;
                Console.WriteLine($"  {role}: {DataDragon.Name(x.ChampionId)} бьёт пул и {lane} {DataDragon.Name(victims[0])}+{DataDragon.Name(victims[1])}: место {rankBefore} → {rankAfter}");
                Check($"   очки выросли ({DataDragon.Name(x.ChampionId)})", after is not null && after.Score > x.Score,
                      $"{x.Score:F1} → {after?.Score:F1}");
                if (rankAfter < rankBefore) rose++;
                if (rankAfter == 1) _crossFirst++;
            }
        Check($"{role}: перекрёстные кандидаты нашлись", found > 0, $"{found}");
        Check($"{role}: и поднялись", rose > 0, $"{rose} из {found}");
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
        "ban.ours", "ban.theirs", "ban.doneStatus", "ban.doneTitle", "ban.rateTip", "ban.allyBanned",
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

        // Союзник забанил второй совет. Раньше карточку молча подменял
        // следующий кандидат — «через пять секунд поменялся один чемпион».
        // Теперь она на месте, перечёркнута, а новый кандидат — внизу.
        List<RecCard> Cards() => (((ItemsControl)w.FindName("BanFullList")).ItemsSource as IEnumerable<RecCard>)?.ToList() ?? [];
        var shown = Cards();
        var allyBan = shown.Count > 1 ? shown[1].ChampionId : 0;
        var afterAlly = Draft("utility", ours: [allyBan], theirs: [], done: false);
        w.UpdateBans(engine.RecommendBans(afterAlly), afterAlly, engine);
        Pump();
        var now = Cards();
        Check("забаненный союзником совет — на своём месте, перечёркнут",
              now.Count > 1 && now[1] is { Gone: true } g && g.ChampionId == allyBan && g.Reason == Loc.T("ban.allyBanned"),
              string.Join(", ", now.Select(c => (c.Gone ? "✕" : "") + c.Name)));
        Check("остальные советы не сдвинулись, новый — внизу",
              now.Count == shown.Count + 1 && now.Where(c => !c.Gone).Count() == shown.Count
              && shown.Where(c => c.ChampionId != allyBan).Select(c => c.ChampionId)
                      .SequenceEqual(now.Where(c => !c.Gone).Take(shown.Count - 1).Select(c => c.ChampionId)),
              $"было: {string.Join(", ", shown.Select(c => c.Name))}");
        var banClicks = 0;
        w.BanHoverHandler = _ => { banClicks++; return Task.FromResult(200); };
        Call(w, "BanCard_Click", new Border { Tag = allyBan }, click);
        Pump();
        Check("клик по перечёркнутому ничего не наводит", banClicks == 0, $"{banClicks}");
        w.BanHoverHandler = null;
        if (snap is { Length: > 0 }) Snap(w, snap, "1b-ally-banned.png");

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
        // Без анимации: баны видны сразу, в кольце цвета команды (владелец:
        // «убери волну и поочерёдное появление, пусть появляются сразу все»).
        Check("баны видны сразу", SlotOpacity(w, "BansOurList", 0) == 1 && SlotOpacity(w, "BansOurList", 1) == 1,
              $"{SlotOpacity(w, "BansOurList", 0)} / {SlotOpacity(w, "BansOurList", 1)}");
        Check("наши — в синем кольце", RingColor(w, "BansOurList", 1) == "#FF36D6E7", RingColor(w, "BansOurList", 1));
        Check("волн нет вовсе", w.FindName("BansOurWave") is null && w.FindName("BansTheirWave") is null, "");
        if (snap is { Length: > 0 }) Snap(w, snap, "2-done-ours.png");

        // Свои добанили, их всё ещё нет.
        var oursFull = Draft("utility", ours: [157, 64, 555, 238, 523], theirs: [], done: true);
        w.UpdateBans([], oursFull, engine);
        Pump();
        Check("наших пять — все видны сразу", Enumerable.Range(0, 5).All(k => SlotOpacity(w, "BansOurList", k) == 1), "");
        Check("их половины по-прежнему нет", Opacity(w, "BansTheirHalf") == 0, "");
        if (snap is { Length: > 0 }) Snap(w, snap, "3-ours-full.png");

        // Таймер банов кончился: клиент раскрывает вражеские уже первым снимком
        // пиков. Окно держит панель, обе половины сразу на местах.
        var reveal = oursFull with
        {
            TheirTeamBans = [24, 893, 145, 119, 37], InBanPhase = false,
            MyBanActionId = -1, MyBanInProgress = false,
        };
        w.UpdateRecommendations(engine.Recommend(reveal, 6), reveal, engine);
        Pump();
        // Единственная анимация панели — сдвиг: их половина поднимается снизу,
        // все пять разом (слоты сами по себе видны сразу).
        Check("раскрыли — их половина едет на место", Moving(w, "BansTheirHalf") && Moving(w, "BansOurHalf"), "");
        Check("их баны видны сразу, все пять", Enumerable.Range(0, 5).All(k => SlotOpacity(w, "BansTheirList", k) == 1),
              string.Join(" ", Enumerable.Range(0, 5).Select(k => SlotOpacity(w, "BansTheirList", k).ToString("0.0"))));
        Wait(0.8);
        Check("доехали: их половина на месте, наша поднялась",
              Opacity(w, "BansTheirHalf") == 1 && ShiftY(w, "BansTheirHalf") == 0 && ShiftY(w, "BansOurHalf") == 0,
              $"{Opacity(w, "BansTheirHalf")} / {ShiftY(w, "BansTheirHalf"):0} / {ShiftY(w, "BansOurHalf"):0}");
        Check("их — в красном кольце", RingColor(w, "BansTheirList", 4) == "#FFFF5A4D", RingColor(w, "BansTheirList", 4));
        // Следующее событие драфта (в бою их несколько в секунду) слоты не
        // пересобирает: состав тот же.
        var theirSrc = ((ItemsControl)w.FindName("BansTheirList")).ItemsSource;
        w.UpdateRecommendations(engine.Recommend(reveal, 6), reveal, engine);
        Pump();
        Check("второе событие слоты не пересобирает и сдвиг не перезапускает",
              ReferenceEquals(((ItemsControl)w.FindName("BansTheirList")).ItemsSource, theirSrc)
              && ShiftY(w, "BansTheirHalf") == 0 && Opacity(w, "BansTheirHalf") == 1, "");
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
        Check("у мейна — золотая рамка со свечением", MainGlow(w, "BansTheirList", sona) == "#FFF5D77A",
              MainGlow(w, "BansTheirList", sona));
        Check("у остальных рамки нет", MainGlow(w, "BansTheirList", sona == 0 ? 1 : 0) == "", "");
        if (snap is { Length: > 0 }) Snap(w, snap, "4-revealed.png");

        // Подержали — пики.
        Wait(4.3);
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
        Poll(() => Opacity(w2, "BansTheirHalf") == 1 && ShiftY(w2, "BansTheirHalf") == 0, 2);
        Check("раскрыты ещё в банах — на месте без удержания", Opacity(w2, "BansTheirHalf") == 1, "");
        var picks2 = early with { InBanPhase = false, MyBanActionId = -1, MyBanInProgress = false };
        w2.UpdateRecommendations(engine.Recommend(picks2, 6), picks2, engine);
        Pump();
        Check("…и пики без задержки", Vis(w2, "RecScroll") && !Vis(w2, "BansDonePanel"), "");
        // Драфт кончился, пока панель была на экране (дродж, «Сброс» в
        // песочнице), — следующий начинает панель с чистого листа: вражеских
        // ещё не видно, наша половина по центру.
        w2.UpdateBans([], early, engine);
        Pump();
        w2.UpdateRecommendations(null, null);
        w2.DraftEnded();
        Pump();
        w2.UpdateBans([], oursFull, engine);
        Pump();
        Check("новый драфт — панель с чистого листа", Opacity(w2, "BansTheirHalf") == 0
              && Slots(w2, "BansTheirList").All(x => x.IsEmpty) && ShiftY(w2, "BansOurHalf") > 20, "");
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

        // Объявление пиков укорочено до 3 секунд (в песочнице — 15, как в клиенте).
        var planField = panelType.GetField("PlanSeconds", BindingFlags.NonPublic | BindingFlags.Static)!;
        planField.SetValue(null, 3.0);

        Get<System.Windows.Controls.Button>(panel, "_stageBans")!
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Wait(1);
        var d = Last();
        Check("сначала объявление пиков: мои чемпионы, банить рано",
              d is { InPlanning: true, InBanPhase: true, MyBanInProgress: false }
              && Vis(overlay, "PlanPanel") && !Vis(overlay, "BanScroll"), "");
        Wait(4.5);

        d = Last();
        Check("объявление кончилось — советы банов", d is { InPlanning: false } && !Vis(overlay, "PlanPanel")
              && Vis(overlay, "BanScroll"), "");
        Check("фаза банов, мой ход", d is { InBanPhase: true, MyBanInProgress: true, MyBanDone: false }, "");
        var hovered = d?.MyTeam.Where(p => !p.IsLocalPlayer && p.PickIntentId != 0).ToList() ?? [];
        // Сколько союзников показывают пик — случайно, от двух до четырёх.
        var plan = panelType.GetField("_hoverPlan", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel) as System.Collections.IEnumerable;
        var planCells = plan?.Cast<object>().Select(x => (int)x.GetType().GetField("Item1")!.GetValue(x)!)
                             .Distinct().Count() ?? 0;
        Check("союзники навели пики — от двух до четырёх, все, кто собирался",
              hovered.Count is >= 2 and <= 4 && hovered.Count == planCells,
              $"{hovered.Count} из {planCells}: " + string.Join(", ", hovered.Select(p => DataDragon.Name(p.PickIntentId))));
        // Пики союзников учтены в рейтинге: их контры набирают очки. В пятёрку
        // такая контра проходит, только если бьёт сильнее, чем контры моему
        // пулу, — пики в песочнице случайные, поэтому смотрим рейтинг шире.
        var names = hovered.Select(p => DataDragon.Name(p.PickIntentId)).ToList();
        var reasons = d is null ? [] : engine.RecommendBans(d, null, 30).SelectMany(b => b.Reasons).ToList();
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

        // «Сброс»: драфт в окне закрыт, слоты пусты; пока настраиваешь слоты,
        // драфт не открывается; «Драфт» открывает его снова.
        Call(panel, "ResetDraft");
        Pump();
        Check("сброс — окно ушло с драфта", !Vis(overlay, "FullView") && Vis(overlay, "IdlePanel"), "");
        var allyCombos = panelType.GetField("_ally", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(panel) as System.Windows.Controls.ComboBox[];
        Check("слоты пусты", allyCombos?.All(c => c.SelectedIndex <= 0) == true, "");
        allyCombos![0].SelectedItem = DataDragon.Name(86);   // Гарен на топ — настройка перед запуском
        Pump();
        Check("правка слота драфт не открывает", !Vis(overlay, "FullView"), "");
        Get<System.Windows.Controls.Button>(panel, "_stageDraft")!
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();
        d = Last();
        Check("«Драфт» — драфт снова в окне, с настроенным слотом",
              Vis(overlay, "FullView") && d?.MyTeam.Any(p => p.EffectiveChampionId == 86) == true, "");

        // Встал руками в лес — «Баны» не переставляют на основную роль (саппорт):
        // так смотрят окно на линии, где игр нет вовсе.
        Call(panel, "ResetDraft");
        var rows = (string[])panelType.GetField("_rowRoles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var radios = (System.Windows.Controls.RadioButton[])panelType
            .GetField("_meRadio", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var jungleRow = Array.IndexOf(rows, "jungle");
        radios[jungleRow].IsChecked = true;
        radios[jungleRow].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Get<System.Windows.Controls.Button>(panel, "_stageBans")!
            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Wait(1);
        d = Last();
        Check("выбрал лес руками — «Баны» оставляют лес", d is { InPlanning: true, MyPosition: "jungle" }
              && Vis(overlay, "PlanPanel"), d?.MyPosition ?? "");
        Call(panel, "StopBanSim");

        panel.Hide();
        overlay.Close();
        Pump();
        planField.SetValue(null, 15.0);
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

    /// Половина панели сейчас под анимацией сдвига.
    private static bool Moving(Window w, string name) =>
        ((UIElement)w.FindName(name)).RenderTransform is TranslateTransform { HasAnimatedProperties: true };

    /// Цвет кольца команды у слота: обводка без заливки поверх портрета.
    private static string RingColor(Window w, string list, int i)
    {
        var ic = (ItemsControl)w.FindName(list);
        if (ic.ItemContainerGenerator.ContainerFromIndex(i) is not DependencyObject c) return "нет слота";
        var stack = new Stack<DependencyObject>([c]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is System.Windows.Shapes.Ellipse { Fill: null, Stroke: SolidColorBrush b, StrokeDashArray.Count: 0 })
                return b.Color.ToString();
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
