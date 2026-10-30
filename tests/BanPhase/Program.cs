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
/// 1д. Пул весит так, как его расставляет подбор пика на этом драфте.
/// 1е. Чем больше союзников показали, тем больше весит команда; «в игре» — для всех.
/// 1ж. Сила в патче — только место в тир-листе, один раз на все линии.
/// 1з. Доводы на карточке — по вкладу в очки; строка про мой пул не теряется.
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
        TeamGrows(engine);
        StrengthOnce(engine);
        ReasonOrder(engine);
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

        // Подпись о контре пулу называет пикрейт — видно, почему этот бан, а не другой.
        // Формулировок две: «Контрит твой пул (…) · пик N%» и, если среди задетых
        // есть жёсткая контра, «Жёсткая контра: … · пик N%». Какая выпадет, решают
        // данные: на базе 22.10.2026 все восемь банов для Соны, Браума и Вел'Коза —
        // жёсткие контры, и проверка, ждавшая только первую, падала и на выпущенном
        // коде. Пикрейт ищется по хвосту «· пик N%»: в строке жёсткой контры свой
        // процент (−3,0% WR), и общий поиск числа с % пропустил бы строку без пика.
        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        static string Head(string key) => Loc.T(key, "\u0001", "\u0002").Split('\u0001')[0];
        var heads = new[] { Head("reason.countersPoolPick"), Head("reason.hardCounter") };
        var pickTail = new Regex(Regex.Escape(Loc.T("reason.withPick", "\u0001", "\u0002").Split('\u0001')[1])
                                      .Replace("\u0002", @"\d+[.,]\d"));
        var reasons = engine.RecommendBans(Draft("utility"), top: 8).SelectMany(b => b.Reasons)
                            .Where(r => heads.Any(h => r.StartsWith(h, StringComparison.Ordinal))).ToList();
        Check("подпись о контре пулу («контрит …» или «жёсткая контра …») встречается", reasons.Count > 0,
              reasons.FirstOrDefault() ?? "нет");
        Check("в ней пикрейт «· пик N%»", reasons.All(r => pickTail.IsMatch(r)),
              reasons.FirstOrDefault(r => !pickTail.IsMatch(r)) ?? reasons.FirstOrDefault() ?? "");
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

        // Доводы по важности: защита пиков раньше места в тир-листе («S-тир в
        // патче»). Довода «сильный в патче» с 14.10 нет: сила в патче — только
        // место в тир-листе, один раз на все линии.
        var tier = Loc.T("reason.tierGrade", "\u0001").Split('\u0001')[0];
        var tierTail = Loc.T("reason.tierGrade", "\u0001").Split('\u0001')[1];
        int Pos(BanRec b, Func<string, bool> f) => Array.FindIndex(b.Reasons, r => f(r));
        var wideBans = engine.RecommendBans(state, history, top: 15);
        var order = wideBans.Select(b => (Guard: Pos(b, r => r.StartsWith(ally, StringComparison.Ordinal) || r.StartsWith(many, StringComparison.Ordinal)),
                                          Tier: Pos(b, r => r.StartsWith(tier, StringComparison.Ordinal) && r.EndsWith(tierTail, StringComparison.Ordinal))))
                            .Where(x => x.Guard >= 0 && x.Tier >= 0).ToList();
        Check("защита пиков — раньше места в тир-листе", order.Count > 0 && order.All(x => x.Guard < x.Tier), $"{order.Count} карточек с обоими");
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
        // И в пятёрку она проходит СВОИМИ очками, с надбавкой, а не страховкой
        // «не набрала — встаёт пятой». Страховка работает только на пятёрке,
        // поэтому смотрим длинный список: там место — чистый порядок по очкам.
        //
        // Раньше здесь стояло «в первой тройке» — ожидание под данные, а не
        // правило. На базе по всем рангам (30.10) выше неё встали Мальфит и
        // Чо'Гат, которые бьют обоих показанных союзников (Ирелию на линии и
        // Мастера Йи в игре), — ровно то, о чём просил владелец: бьёт
        // нескольких наших — поднимается наверх.
        var firstHard = wide.ToList().FindIndex(b => Has(b, hard));
        Check("и проходит в пятёрку своими очками", firstHard is >= 0 and < 5,
              $"место {firstHard + 1} в порядке по очкам");

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

    // ── 1д. Пул весит так, как его расставляет подбор пика ───────────────────

    /// <summary>
    /// Скриншот владельца 14.10 (саппорт; Доктор Мундо — до него наводили
    /// Насуса, — Каин, Мальзахар, Джинкс): Блицкранк с «Жёсткая контра: Эш» и
    /// львиной долей очков за Эш. «Эш я скорее всего и не пикнул бы — посмотри,
    /// как считает подбор в драфте». Баны весили пул по играм за месяц (Эш с 19
    /// играми — третья) и надбавке за синергию; подбор пика ставит Эш-саппорта
    /// последней (−2,5 против 9,4 у Соны: винрейт в патче −5,7).
    ///
    /// Держим: доля каждого чемпиона пула в весе пула = exp(очки Recommend / 3),
    /// нормированные, — на том же драфте; у Эш доля меньше 2%, и «жёсткой
    /// контры» против неё в пятёрке нет. Ранние заходы того же дня (иконки
    /// синергии, затем синергия × 0,15) сняты: синергия уже внутри очков подбора.
    /// </summary>
    private static void Partners(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("пул весит так, как его расставляет подбор пика:");
        int[] mains = [37, 201, 22, 161, 910, 101, 78, 117];
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>
            { [37] = 32, [201] = 22, [22] = 19, [161] = 15, [910] = 12, [101] = 8, [78] = 8, [117] = 6 });
        var team = new List<DraftPlayer>
        {
            new(0, 0, 36, "top", false),       // Доктор Мундо (до него — Насус)
            new(1, 0, 141, "jungle", false),   // Каин
            new(2, 0, 90, "middle", false),    // Мальзахар
            new(3, 0, 222, "bottom", false),   // Джинкс
            new(4, 0, 0, "utility", true),
        };
        var state = Snapshot(team, team[4], "utility");
        var score = engine.Recommend(state with { InBanPhase = false }, mains.Length, mains)
                          .ToDictionary(r => r.ChampionId, r => r.Score);
        var weights = PoolWeights(engine, state);
        var total = weights.Values.Sum();
        var top = weights.Keys.Max(id => score[id]);
        var norm = weights.Keys.Sum(id => Math.Exp((score[id] - top) / 3.0));
        foreach (var id in weights.Keys.OrderByDescending(id => score[id]))
        {
            var want = Math.Exp((score[id] - top) / 3.0) / norm;
            Check($"   {DataDragon.Name(id)}: подбор {score[id]:F2} → доля {want:P1}",
                  Math.Abs(weights[id] / total - want) < 1e-9, $"{weights[id] / total:P1}");
        }
        Check("Эш (у подбора последняя) — меньше 2% пула", weights.GetValueOrDefault(22) / total < 0.02,
              $"{weights.GetValueOrDefault(22) / total:P1}");
        var hist = new Dictionary<int, HashSet<int>> { [0] = [75, 36], [1] = [141], [2] = [90], [3] = [222] };
        var bans = engine.RecommendBans(state, hist, top: 5);
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {string.Join(" | ", b.Reasons.Take(2))}");
        var hardAshe = Loc.T("reason.hardCounter", DataDragon.Name(22), "\u0001").Split('\u0001')[0];
        Check("«жёсткой контры» против Эш в пятёрке нет",
              !bans.Any(b => b.Reasons.Any(r => r.StartsWith(hardAshe, StringComparison.Ordinal))),
              string.Join(", ", bans.Where(b => b.Reasons.Any(r => r.StartsWith(hardAshe, StringComparison.Ordinal)))
                                    .Select(b => DataDragon.Name(b.ChampionId))));
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

    // ── 1е. Чем больше союзников показали, тем больше весит команда ──────────

    /// <summary>
    /// Скриншот владельца 14.10 (саппорт; Фиора, Виего, Хвэй — до него наводили
    /// Зерата, — Джин; Мальфита забанил союзник): Блицкранк, Милио, Физз,
    /// Наутилус, Леона — четыре жёсткие контры пулу и один бан про команду.
    /// «Четверо показали — по идее тем вероятнее пик против нас всех».
    ///
    /// Было две причины: союзники вместе весили как двое (показали четверо —
    /// каждый вполовину, команда с числом не росла), а как кандидат мешает
    /// союзнику в игре с другой линии, не считалось вовсе — только для моего
    /// пула. Теперь вес союзников × √(2 / n) (вместе растут, но медленнее числа),
    /// и «в игре» считается для всех наших. Заодно: Хвэй союзника больше не
    /// числится в моём пуле.
    /// </summary>
    private static void TeamGrows(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("чем больше союзников показали, тем больше весит команда:");
        var games = new Dictionary<int, int>
        {
            [37] = 32, [201] = 22, [22] = 19, [161] = 15, [910] = 12, [101] = 8, [78] = 8, [117] = 6,
            [98] = 5, [412] = 3, [53] = 1, [555] = 1, [267] = 1,
        };
        SessionTracker.HistoryOverride = Hist(games);
        int[] picks = [114, 234, 910, 202];                     // Фиора, Виего, Хвэй, Джин
        string[] pos = ["top", "jungle", "middle", "bottom"];
        DraftState Shown(int n)
        {
            var team = new List<DraftPlayer>();
            for (int i = 0; i < 4; i++) team.Add(new(i, 0, i < n ? picks[i] : 0, pos[i], false));
            team.Add(new(4, 0, 0, "utility", true));
            return Snapshot(team, team[4], "utility") with { MyTeamBans = [54] };   // Мальфит — союзником
        }
        var sums = Enumerable.Range(1, 4).Select(n => AllyWeights(engine, Shown(n)).Sum()).ToList();
        Check("вес команды растёт с каждым показанным союзником",
              sums.Zip(sums.Skip(1)).All(p => p.Second > p.First), string.Join(" → ", sums.Select(x => x.ToString("F2"))));

        var state = Shown(4);
        var hist = new Dictionary<int, HashSet<int>> { [0] = [114], [1] = [234], [2] = [101, 910], [3] = [202] };
        var bans = engine.RecommendBans(state, hist, top: 5);
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {string.Join(" | ", b.Reasons.Take(2))}");
        var ally = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var many = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        var team = Loc.T("reason.alsoTeam", "\u0001").Split('\u0001')[0];
        var also = Loc.T("reason.alsoPool", "\u0001").Split('\u0001')[0];
        bool Has(BanRec b, params string[] heads) => b.Reasons.Any(r => heads.Any(h => r.StartsWith(h, StringComparison.Ordinal)));
        // Про что бан — по двум доводам, которые видны на карточке.
        bool Shows(BanRec b, params string[] heads) => b.Reasons.Take(2).Any(r => heads.Any(h => r.StartsWith(h, StringComparison.Ordinal)));
        var forTeam = bans.Where(b => Shows(b, ally, many, team)).ToList();
        Check("четверо показали — против команды в пятёрке не меньше двух", forTeam.Count >= 2,
              string.Join(", ", forTeam.Select(b => DataDragon.Name(b.ChampionId))));
        Check("и они мешают ещё и моему пулу", forTeam.Count(b => Has(b, also)) >= 2,
              string.Join(", ", forTeam.Where(b => Has(b, also)).Select(b => DataDragon.Name(b.ChampionId))));
        Check("прямых контр моему пулу по-прежнему не меньше двух", bans.Count(b => Has(b, PoolHead, HardHead)) >= 2, "");
        Check("Хвэй союзника — не мой пул", !PoolWeights(engine, state).ContainsKey(910), "");
        Check("довод «мешает команде» встречается",
              engine.RecommendBans(state, hist, top: 15).Any(b => Has(b, team)), "");
        SessionTracker.HistoryOverride = null;
    }

    /// Веса союзников (пики и прошлые наведения) в банах на этом драфте.
    private static List<double> AllyWeights(RecommendationEngine engine, DraftState state)
    {
        var t = typeof(RecommendationEngine);
        var stats = t.GetMethod("RoleStats", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, ["support"])!;
        var list = (System.Collections.IEnumerable)t.GetMethod("BanVictims", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(engine, [state, null, "support", stats])!;
        object P(object o, string n) => o.GetType().GetProperty(n)!.GetValue(o)!;
        return [.. list.Cast<object>().Where(v => P(v, "Kind").ToString() == "Ally").Select(v => (double)P(v, "Weight"))];
    }

    // ── 1ж. Сила в патче — один раз, на всех линиях ───────────────────────────

    /// <summary>
    /// Владелец 14.10: «сила в патче считается только для моей линии, но и в
    /// общей формуле (как часто берут + место в тир-листе) тоже — на моей линии
    /// дважды? Пусть считается для всех, но один раз». Было: слагаемое «винрейт
    /// в патче» у кандидатов моей линии плюс множитель «место в тир-листе» у
    /// каждого попадания. Теперь сила в патче — только множитель, одинаковый на
    /// всех линиях; слагаемое остаётся, лишь когда защищать некого (ни пула, ни
    /// мастерства, ни показанных пиков), — иначе советов не было бы вовсе.
    /// </summary>
    private static void StrengthOnce(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("сила в патче — один раз:");
        var t = typeof(RecommendationEngine);
        object P(object o, string n) => o.GetType().GetProperty(n)?.GetValue(o) ?? o.GetType().GetField(n)!.GetValue(o)!;
        List<double> Metas(DraftState state)
        {
            var stats = t.GetMethod("RoleStats", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, ["support"])!;
            var taken = t.GetMethod("BanTaken", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, [state, null, stats])!;
            var victims = t.GetMethod("BanVictims", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(engine, [state, null, "support", stats])!;
            var cands = (System.Collections.IEnumerable)t.GetMethod("BanCandidates", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(engine, ["support", stats, victims, taken])!;
            return [.. cands.Cast<object>().Select(c => (double)P(c, "Meta"))];
        }
        var me = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        var withPool = Metas(Snapshot(me, me[0], "utility"));
        Check("есть кого защищать — слагаемого «сила в патче» нет ни у кого", withPool.All(m => m == 0),
              $"{withPool.Count(m => m != 0)} из {withPool.Count}");
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>());
        var empty = Metas(Snapshot(me, me[0], "utility"));
        Check("защищать некого — сильные и частые на моей линии всё равно набирают очки", empty.Any(m => m > 0),
              $"{empty.Count(m => m > 0)}");
        Check("и советы есть", engine.RecommendBans(Snapshot(me, me[0], "utility"), null, top: 5).Count == 5, "");
        SessionTracker.HistoryOverride = null;
    }

    // ── 1з. Доводы на карточке — по вкладу, строка про меня не теряется ──────

    /// <summary>
    /// Скриншот владельца 14.10 (саппорт; Доктор Мундо — до него наводили
    /// Гарена, — Лиллия, Мальзахар, Кай'Са): у Вуконга первой строкой «Контрит
    /// союзника Гарен · пик 0,5%» — 2,4 очка из 25,5, а «Мешает команде» за 14,6
    /// не видно. Доводы шли по фиксированной важности. Владелец выбрал вариант
    /// «по вкладу, но пул не теряется»: первая строка — самая весомая часть
    /// очков, вторая — про мой пул или пик, если первая не про меня.
    ///
    /// Держим на той же сцене: у каждого бана пятёрки, где есть строка про
    /// меня, она в первых двух; у Вуконга первой стоит «Мешает команде», а не
    /// контра Гарену; жёсткая контра и «Контрит твой пул» — одна строка (это
    /// подписи одной и той же части очков).
    /// </summary>
    private static void ReasonOrder(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("доводы по вкладу:");
        SessionTracker.HistoryOverride = Hist(new Dictionary<int, int>
        {
            [37] = 32, [201] = 22, [22] = 19, [161] = 15, [910] = 12, [101] = 8, [78] = 8, [117] = 6,
            [98] = 5, [412] = 3, [53] = 1, [555] = 1, [267] = 1,
        });
        var team = new List<DraftPlayer>
        {
            new(0, 0, 36, "top", false),       // Доктор Мундо (до него — Гарен)
            new(1, 0, 876, "jungle", false),   // Лиллия
            new(2, 0, 90, "middle", false),    // Мальзахар
            new(3, 0, 145, "bottom", false),   // Кай'Са
            new(4, 0, 0, "utility", true),
        };
        var state = Snapshot(team, team[4], "utility");
        var hist = new Dictionary<int, HashSet<int>> { [0] = [86, 36], [1] = [876], [2] = [90], [3] = [145] };
        var bans = engine.RecommendBans(state, hist, top: 5);
        foreach (var b in bans)
            Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} {b.Score,5:F1}  {string.Join(" | ", b.Reasons.Take(2))}");

        string Head(string key, int args) => Loc.T(key, [.. Enumerable.Repeat<object>("\u0001", args)]).Split('\u0001')[0];
        string[] mineHeads = [Head("reason.hardCounter", 2), Head("reason.countersPoolPick", 2),
                              Head("reason.countersMyPick", 1), Head("reason.alsoPool", 1)];
        bool AboutMe(string r) => mineHeads.Any(h => r.StartsWith(h, StringComparison.Ordinal));
        var lost = bans.Where(b => b.Reasons.Any(AboutMe) && !b.Reasons.Take(2).Any(AboutMe)).ToList();
        Check("строка про мой пул в первых двух — у каждого, у кого она есть", lost.Count == 0,
              string.Join("; ", lost.Select(b => $"{DataDragon.Name(b.ChampionId)}: {string.Join(" | ", b.Reasons)}")));
        var hard = Head("reason.hardCounter", 2);
        var pool = Head("reason.countersPoolPick", 2);
        var both = bans.Where(b => b.Reasons.Any(r => r.StartsWith(hard, StringComparison.Ordinal))
                                && b.Reasons.Any(r => r.StartsWith(pool, StringComparison.Ordinal))).ToList();
        Check("жёсткая контра и «контрит твой пул» — одна строка", both.Count == 0,
              string.Join(", ", both.Select(b => DataDragon.Name(b.ChampionId))));
        var wukong = engine.RecommendBans(state, hist, top: 15).FirstOrDefault(b => b.ChampionId == 62);
        var team3 = Head("reason.alsoTeam", 1);
        Check("у Вуконга первой — «мешает команде», а не контра Гарену",
              wukong is not null && wukong.Reasons[0].StartsWith(team3, StringComparison.Ordinal),
              wukong is null ? "Вуконга нет в пятнадцати" : string.Join(" | ", wukong.Reasons.Take(2)));
        SessionTracker.HistoryOverride = null;
    }

    private static int _crossFirst;

    /// <summary>
    /// Пример владельца на живой базе: кандидат моей линии, которого берут и на
    /// другой линии, бьёт там двоих — союзник с этой линии показывает обоих
    /// (одного сейчас, второго наводил). Очки таких кандидатов обязаны расти, а
    /// места — подниматься. Кандидатов и жертв выбираем по базе: патч меняется,
    /// а правило — нет.
    ///
    /// Очки — у большинства, а не у каждого (20.10, база 16.20): у отдельного
    /// они могут и упасть, и это не ошибка. Союзник показал Атрокса — подбор
    /// пика переставил мой пул поддержки на Хвэя (доля 0,7 → 3,2 из 4,5), а Хвэй
    /// сам бьёт лесного Вуконга: очки Вуконга за пул 8,2 → 1,6. За Атрокса и
    /// Камиллу он набрал +16, но стоит 21-м, и двадцать банов до него уже бьют
    /// тех же двоих — повтор удешевляет (BAN_REPEAT): итог 8,2 → 6,3. То же с
    /// местом: тех же жертв может бить кто-то сильнее.
    /// </summary>
    private static void CrossLane(RecommendationEngine engine, Dictionary<int, int> games, string pos, string role)
    {
        string[] lanes = ["top", "jungle", "mid", "adc", "support"];
        string[] lcu   = ["top", "jungle", "middle", "bottom", "utility"];
        SessionTracker.HistoryOverride = Hist(games);
        var me = new List<DraftPlayer> { new(0, 0, 0, pos, true) };
        var alone = engine.RecommendBans(Snapshot(me, me[0], pos), null, top: 40);
        int found = 0, rose = 0, gained = 0;
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
                Console.WriteLine($"  {role}: {DataDragon.Name(x.ChampionId)} бьёт пул и {lane} {DataDragon.Name(victims[0])}+{DataDragon.Name(victims[1])}: "
                                  + $"место {rankBefore} → {rankAfter}, очки {x.Score:F1} → {after?.Score:F1}");
                if (after is not null && after.Score > x.Score) gained++;
                if (rankAfter < rankBefore) rose++;
                if (rankAfter == 1) _crossFirst++;
            }
        Check($"{role}: перекрёстные кандидаты нашлись", found > 0, $"{found}");
        Check($"{role}: очки выросли у большинства", gained * 2 > found, $"{gained} из {found}");
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
        "ban.ours", "ban.theirs", "ban.doneTitle", "ban.rateTip", "ban.allyBanned",
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
        // Роль целиком, все грейды: при 15 на роль список кончался на S (владелец
        // 20.10: «добавь ещё чемпионов, почему кончается только на S»).
        var tierCols = (((ItemsControl)w.FindName("TierList")).ItemsSource as IEnumerable<TierRoleCol>)?.ToList() ?? [];
        var fullRoles = engine.TierList(perRole: 500).GroupBy(t => t.Role).ToDictionary(g => g.Key, g => g.Count());
        Check("тир-лист — роль целиком, ниже S тоже",
              tierCols.Count == 5 && tierCols.All(c => c.TierCells.Any(x => x.Grade is "A" or "B" or "C"))
              && tierCols.Sum(c => c.TierCells.Count) == fullRoles.Values.Sum(),
              string.Join(" ", tierCols.Select(c => c.TierCells.Count)));

        // Полоса «Чемпионы на роль» — строго порядком тир-листа, и с включённым
        // пулом тоже: его чемпионы на своих местах, бирюзовым кольцом. С 28.09
        // пул стоял впереди, и владелец 20.10 увидел полосу «не по убыванию».
        // Пул — двое из середины и конца тир-листа, чтобы «впереди» было видно.
        var supTier = engine.TierList(perRole: 200).Where(t => t.Role == "support").Select(t => t.ChampionId).ToList();
        var stripPool = new ChampPool { Name = "проверка полосы",
                                        ByRole = new() { ["support"] = [supTier[supTier.Count / 2], supTier[^1]] } };
        PoolStore.Current().Pools.Add(stripPool);
        PoolStore.SetActive(PoolKind.Pool, stripPool.Id);
        Call(w, "RenderRolePool", advice);
        Pump();
        var strip = (((ItemsControl)w.FindName("RolePoolList")).ItemsSource as IEnumerable<TierCell>)?.ToList() ?? [];
        Check("полоса роли — порядком тир-листа, пул на своих местах",
              strip.Select(c => c.ChampionId).SequenceEqual(supTier) && strip.Count(c => c.InPool) == 2,
              $"первые: {string.Join(", ", strip.Take(4).Select(c => $"{DataDragon.Name(c.ChampionId)}{(c.InPool ? "*" : "")}"))}");
        PoolStore.SetActive(PoolKind.Pool, null);
        PoolStore.Current().Pools.Remove(stripPool);

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
        // Ряды банов под заголовками колонок команд: наших два и «…», их — «?».
        var chips = Slots(w, "MyTeamBansStrip");
        Check("под «Моей командой» — ряд банов, два сделаны",
              Vis(w, "MyTeamBansStrip") && chips.Count == 5 && chips.Count(x => !x.IsEmpty) == 2
              && chips.Where(x => x.IsEmpty).All(x => x.Placeholder == "…"),
              $"{Vis(w, "MyTeamBansStrip")} {chips.Count}/{chips.Count(x => !x.IsEmpty)}");
        Check("под «Врагами» — пять скрытых «?»", Vis(w, "EnemyTeamBansStrip")
              && Slots(w, "EnemyTeamBansStrip") is { Count: 5 } en && en.All(x => x is { IsEmpty: true, Placeholder: "?" }), "");
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
        // Сразу, пока идут 4 секунды показа. Стояло после ожидания доезда и
        // второго события — и доходило до проверки за 2,5–3,8 с: под нагрузкой
        // показ успевал кончиться, и пункт падал через раз (16.10, на коде и до,
        // и после правок того дня — время отрисовки то же). Отсюда — меньше
        // секунды. На пиках программа обновляет панель рун: рун ещё нет —
        // «вернуть пул роли». Пока держится панель банов, строка — за
        // тир-листом: полоса чемпионов роли наезжала на него (скриншот владельца).
        w.HideRunes();
        w.ShowRunes(null, 0, "", "support", null);
        Pump();
        Check("пока видны баны, полосы чемпионов роли нет", !Vis(w, "RolePoolBar") && Vis(w, "TierListBar"),
              $"пул роли {Vis(w, "RolePoolBar")}, тир-лист {Vis(w, "TierListBar")}");
        Check("пики начались, а панель банов держится", Vis(w, "BansDonePanel") && !Vis(w, "RecScroll"), "");
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
        // Панель банов ушла, а кого забанили — видно под заголовками колонок.
        var chipsOurs = Slots(w, "MyTeamBansStrip");
        var chipsTheirs = Slots(w, "EnemyTeamBansStrip");
        Check("на пиках баны — под заголовками колонок, пять и пять",
              Vis(w, "MyTeamBansStrip") && Vis(w, "EnemyTeamBansStrip")
              && chipsOurs.Count(x => !x.IsEmpty) == 5 && chipsTheirs.Count(x => !x.IsEmpty) == 5,
              $"{chipsOurs.Count(x => !x.IsEmpty)} / {chipsTheirs.Count(x => !x.IsEmpty)}");
        Check("в ряду забаненный мейн помечен", chipsTheirs.Count(x => x.IsMyMain) == 1, "");
        if (snap is { Length: > 0 }) Snap(w, snap, "5-picks-team-bans.png");

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
        // Союзник пропустил бан. Пока идут баны, пустой кружок в ряду — «…»;
        // баны кончились при тех же банах — просто пустой: ряд пересобран,
        // хотя сами баны не менялись.
        var four = oursFull with { MyTeamBans = [157, 64, 555, 238] };
        w2.UpdateBans([], four, engine);
        Pump();
        Check("пропущенный бан, пока банят, — «…»",
              Slots(w2, "MyTeamBansStrip").Count(x => x is { IsEmpty: true, Placeholder: "…" }) == 1, "");
        var fourPicks = four with { InBanPhase = false, MyBanActionId = -1, MyBanInProgress = false };
        w2.UpdateRecommendations(engine.Recommend(fourPicks, 6), fourPicks, engine);
        Pump();
        Check("баны кончились — пропущенный пустой, без «…»",
              Slots(w2, "MyTeamBansStrip") is var left && left.Count(x => !x.IsEmpty) == 4
              && left.Count(x => x is { IsEmpty: true, Placeholder: "" }) == 1,
              string.Join(" ", Slots(w2, "MyTeamBansStrip").Select(x => x.IsEmpty ? $"[{x.Placeholder}]" : x.Name)));
        // Режим без банов (вслепую) — ряда нет вовсе.
        var blind = fourPicks with { MyTeamBans = [], TheirTeamBans = [], MyBanDone = false };
        w2.UpdateRecommendations(engine.Recommend(blind, 6), blind, engine);
        Pump();
        Check("вслепую банов нет — и ряда нет", !Vis(w2, "MyTeamBansStrip") && !Vis(w2, "EnemyTeamBansStrip"), "");
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

        // «Поиск игры»: экран ожидания в фазе поиска, потом та же цепочка, что
        // у «Банов». Владелец 15 октября: подсказку режима пула было не
        // рассмотреть — «Баны» уводят окно в драфт сразу. Поиск укорочен до 2 с.
        panelType.GetField("SearchSeconds", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 2.0);
        var search = Get<System.Windows.Controls.Button>(panel, "_searchBtn")!;
        search.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Wait(0.5);
        var status = ((System.Windows.Controls.TextBlock)overlay.FindName("ReadyStatusText")).Text;
        Check("«Поиск игры» — экран ожидания в фазе поиска, драфта ещё нет",
              Vis(overlay, "ReadyScroll") && status == Loc.T("phaseState.matchmaking"), status);
        // Второе нажатие — отсчёт заново, а не отмена: первая версия гасила
        // поиск, и владелец пять раз подряд отменял его через секунду.
        search.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Wait(0.5);
        status = ((System.Windows.Controls.TextBlock)overlay.FindName("ReadyStatusText")).Text;
        Check("ещё нажатие — поиск идёт дальше, а не гаснет", status == Loc.T("phaseState.matchmaking"), status);
        // Поиск кончается по таймеру окна, а не по часам проверки: ждём
        // объявления (до 4 с), а не ровно 2,5 с. Под нагрузкой таймер отставал,
        // и пункт падал через раз — 16.10, и на коде до правок того дня.
        var planned = WaitFor(() => Last() is { InPlanning: true } && Vis(overlay, "PlanPanel"), 4);
        Check("поиск кончился — объявление пиков, как по кнопке «Баны»", planned,
              Last() is not { } ld ? "драфта нет" : ld.InPlanning ? "объявление, панели нет"
              : ld.InBanPhase ? "уже баны" : "пики");

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
        // Ждём объявления, а не ровно секунду: таймер песочницы под нагрузкой
        // отставал, и пункт падал через раз (16.10, роль при этом была верной).
        var jungle = WaitFor(() => Last() is { InPlanning: true, MyPosition: "jungle" } && Vis(overlay, "PlanPanel"), 3);
        d = Last();
        Check("выбрал лес руками — «Баны» оставляют лес", jungle,
              $"{d?.MyPosition ?? "драфта нет"}, {(d is { InPlanning: true } ? "объявление" : "не объявление")}, "
              + $"панель {(Vis(overlay, "PlanPanel") ? "видна" : "скрыта")}");
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
