using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Союзник мечется между чемпионами (14.11.2026). Владелец: «если союзник
/// быстро переключает пики — остановить перевыбор центральной части, взять
/// средний показатель из его показанных пиков; 3–4 раза быстро переключил —
/// пусть сработает».
///
/// 1. AllyWaver: три смены наведения за 10 секунд — мечется, в среднее идут все
///    показанные; возврат к показанному ничего не меняет, новый добавляется;
///    20 секунд на одном — успокоился; лок — всё; медленный перебор, свой слот,
///    «снял и навёл того же» — не метания.
/// 2. Движок на живой базе: без усреднения центр скачет за наведением; с ним
///    — тот же список до последнего знака на любом из показанных и на пустом
///    наведении; счёт — среднее точного счёта по показанным; показанных он не
///    советует, мой пик остаётся; забаненный выпадает из среднего; залочивший
///    считается собой.
/// 3. Отпечаток драфта (Program.DraftHash) видит начало и конец метаний: иначе
///    «успокоился» без перемен в драфте до подбора не доехал бы.
///
/// База — живая, только чтение; всё остальное — в своей папке.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Джанглеры, между которыми мечется союзник: Ли Син, Вай, Амуму, Ка'Зикс.
    private static readonly int[] Shown = [64, 254, 32, 121];
    private const int WaverCell = 1;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AllyWaver");
        try { Directory.Delete(root, recursive: true); } catch { /* не было */ }
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
        Names(playerDir);

        Tracker();
        Hash();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — движок проверять нечем"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Engine(engine);
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: центр не скачет за мечущимся союзником, счёт — среднее показанных"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Кто мечется ──────────────────────────────────────────────────────

    private static void Tracker()
    {
        Console.WriteLine("кто мечется (AllyWaver):");
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var w = new AllyWaver();
        IReadOnlyDictionary<int, IReadOnlyList<int>>? Hover(int champ, double sec, int cell = WaverCell,
                                                              bool locked = false) =>
            w.Observe(Slots(cell, champ, locked), t0.AddSeconds(sec));
        string Set(IReadOnlyDictionary<int, IReadOnlyList<int>>? r) =>
            r is null ? "никто" : string.Join("; ", r.Select(kv => $"{kv.Key}: {NameList(kv.Value)}"));

        Hover(Shown[0], 0);
        Hover(Shown[1], 2);
        var two = Hover(Shown[2], 4);
        Check("две смены — ещё не метания", two is null, Set(two));
        var three = Hover(Shown[3], 6);
        Check("третья смена за 10 секунд — мечется, в среднем все четверо с первым",
              three?.GetValueOrDefault(WaverCell) is { } s3 && s3.SequenceEqual(Shown), Set(three));
        var back = Hover(Shown[1], 7);
        Check("вернулся к показанному — те же четверо", back?[WaverCell].SequenceEqual(Shown) == true, Set(back));
        var none = Hover(0, 8);
        Check("снял наведение — всё ещё мечется", none?[WaverCell].SequenceEqual(Shown) == true, Set(none));
        var more = Hover(59, 9);
        Check("новый чемпион добавился к среднему", more?[WaverCell].SequenceEqual([.. Shown, 59]) == true, Set(more));
        var still = Hover(59, 9 + AllyWaver.Calm.TotalSeconds - 1);
        Check("держит одного чуть меньше 20 секунд — мечется", still is not null, Set(still));
        var calm = Hover(59, 9 + AllyWaver.Calm.TotalSeconds);
        Check("20 секунд на одном — успокоился", calm is null, Set(calm));
        Hover(Shown[0], 40);
        Hover(Shown[1], 41);
        var again = Hover(Shown[2], 42);
        Check("после покоя снова три быстрых смены — снова мечется, с того, на ком стоял",
              again?[WaverCell].SequenceEqual([59, .. Shown[..3]]) == true, Set(again));
        var locked = Hover(Shown[2], 43, locked: true);
        Check("залочил — не мечется", locked is null, Set(locked));
        var after = Hover(Shown[2], 44, locked: true);
        Check("и после лока не начинает", after is null, Set(after));

        w.Reset();
        for (var i = 0; i < 6; i++) Hover(Shown[i % 4], i * 6.0);
        Check("смена раз в 6 секунд — не метания", Hover(Shown[2], 36) is null, "");

        w.Reset();
        for (var i = 0; i < 6; i++) w.Observe(Slots(0, Shown[i % 4], false, mine: true), t0.AddSeconds(i));
        Check("свой слот не в счёт", w.Observe(Slots(0, Shown[1], false, mine: true), t0.AddSeconds(7)) is null, "");

        w.Reset();
        for (var i = 0; i < 8; i++) Hover(i % 2 == 0 ? Shown[0] : 0, i);
        Check("навёл, снял, навёл того же — не метания", Hover(Shown[0], 9) is null, "");

        w.Reset();
        Hover(Shown[0], 0); Hover(Shown[1], 1); Hover(Shown[0], 2);
        var flip = Hover(Shown[1], 3);
        Check("туда-сюда между двумя — мечется между двумя",
              flip?[WaverCell].SequenceEqual(Shown[..2]) == true, Set(flip));

        w.Reset();
        int[] many = [64, 254, 32, 121, 59, 60, 11, 76, 113, 104];
        for (var i = 0; i < many.Length; i++) Hover(many[i], i);
        var capped = Hover(many[^1], many.Length);
        Check($"в среднем не больше {AllyWaver.MaxShown}, старые уходят",
              capped?[WaverCell].SequenceEqual(many[^AllyWaver.MaxShown..]) == true, Set(capped));

        // Свой ход: наведённый стоит в championId ещё до лока — это не лок.
        w.Reset();
        IReadOnlyDictionary<int, IReadOnlyList<int>>? Turn(int champ, double sec, bool done = false) =>
            w.Observe(Slots(WaverCell, champ, locked: true, inTurn: !done), t0.AddSeconds(sec));
        Turn(Shown[0], 0); Turn(Shown[1], 1); Turn(Shown[2], 2);
        var turn = Turn(Shown[3], 3);
        Check("перебирает в свой ход (чемпион в championId) — тоже мечется",
              turn?[WaverCell].SequenceEqual(Shown) == true, Set(turn));
        var done = Turn(Shown[3], 4, done: true);
        Check("ход кончился с чемпионом — залочил", done is null, Set(done));

        w.Reset();
        Check("новый драфт — с чистого листа", Hover(Shown[0], 100) is null, "");
    }

    /// Мой слот 0 (мид, пусто), союзник в ячейке cell наводит или залочил champ.
    /// inTurn — сейчас его ход пика: чемпион в championId ещё не лок.
    private static DraftState Slots(int cell, int champ, bool locked, bool mine = false, bool inTurn = false)
    {
        var team = Enumerable.Range(0, 5).Select(i => new DraftPlayer(i, 0, 0, "", i == 0)).ToList();
        team[cell] = new DraftPlayer(cell, locked ? champ : 0, locked ? 0 : champ, "", mine);
        return Draft(team, []) with { ActiveCells = inTurn ? [cell] : [] };
    }

    // ── 2. Отпечаток драфта ─────────────────────────────────────────────────

    private static void Hash()
    {
        Console.WriteLine();
        Console.WriteLine("отпечаток драфта:");
        var m = typeof(OverlayWindow).Assembly.GetType("Program")!
            .GetMethod("DraftHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        string H(DraftState s) => (string)m.Invoke(null, [s])!;
        var d = Slots(WaverCell, Shown[0], false);
        var w = d with { Wavering = new Dictionary<int, IReadOnlyList<int>> { [WaverCell] = Shown } };
        var w2 = d with { Wavering = new Dictionary<int, IReadOnlyList<int>> { [WaverCell] = [.. Shown.Reverse()] } };
        Check("начал или кончил метаться — отпечаток другой", H(d) != H(w), "");
        Check("порядок показа на отпечаток не влияет", H(w) == H(w2), "");
    }

    // ── 3. Движок ───────────────────────────────────────────────────────────

    private static void Engine(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("подбор (мид; союзники: Гарен топ, Джинкс бот залочены, лес мечется; враги Зед, Ясуо):");
        var wave = new Dictionary<int, IReadOnlyList<int>> { [WaverCell] = Shown };

        // Без усреднения: центр идёт за каждым наведением.
        var plain = Shown.Select(c => engine.Recommend(Mid(c), 6)).ToList();
        for (var i = 0; i < Shown.Length; i++)
            Console.WriteLine($"  лес навёл {Name(Shown[i]),-10} без среднего: {NameList(plain[i].Select(r => r.ChampionId))}");
        var jumps = plain.Skip(1).Count(p => !Ids(p).SequenceEqual(Ids(plain[0])));
        Check("без усреднения центр меняется вслед за наведением (иначе проверять нечего)", jumps > 0,
              $"другой список у {jumps} из {Shown.Length - 1}");

        // С усреднением: на любом из показанных и на пустом — одно и то же.
        var sw = Stopwatch.StartNew();
        var avg = engine.Recommend(Mid(Shown[0]) with { Wavering = wave }, 6);
        var avgMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"  мечется, среднее:               {NameList(Ids(avg))}");
        var same = Shown.Append(0).All(c => Same(engine.Recommend(Mid(c) with { Wavering = wave }, 6), avg));
        Check("на любом из показанных и на пустом наведении — тот же список, те же числа", same, "");

        // Счёт — среднее точного счёта по каждому показанному.
        var wide = engine.Recommend(Mid(Shown[2]) with { Wavering = wave }, 20);
        double Mean(int id, Func<Recommendation, double> f) =>
            Shown.Average(c => f(engine.Recommend(Mid(c), 1, [id])[0]));
        var worst = wide.Max(r => Math.Abs(r.Score - Mean(r.ChampionId, x => x.Score)));
        Check("счёт каждого — среднее его счёта при каждом показанном", worst < 1e-6, $"расхождение {worst:E1}");
        var worstSyn = wide.Max(r => Math.Abs(r.SynergyDelta - Mean(r.ChampionId, x => x.SynergyDelta)));
        Check("полоска синергии — тоже среднее", worstSyn < 1e-6, $"расхождение {worstSyn:E1}");
        Check("тег синергии говорит то же, что полоска",
              wide.All(r => r.Facts is null || Math.Abs(r.Facts.Synergy - r.SynergyDelta) < 1e-9), "");
        Check("показанных им не советуем", !wide.Any(r => Shown.Contains(r.ChampionId)),
              NameList(wide.Where(r => Shown.Contains(r.ChampionId)).Select(r => r.ChampionId)));
        Check("доводы не называют мечущегося союзника",
              !wide.SelectMany(r => r.Reasons).Any(t => Shown.Any(c => t.Contains(Name(c)))), "");

        // Мой пик — даже если союзник его показывал.
        var mineShown = Mid(Shown[0], me: Shown[3]) with { Wavering = wave };
        var mine = engine.Recommend(mineShown, 1, [Shown[3]]);
        Check("мой пик в счёте, даже если союзник его показывал", mine.Count == 1 && mine[0].IsMyPick, "");

        // Забаненный выпадает из среднего.
        var banned = Mid(Shown[0], ban: Shown[2]) with { Wavering = wave };
        var rest = Shown.Where(c => c != Shown[2]).ToArray();
        var bAvg = engine.Recommend(banned, 10);
        var bWorst = bAvg.Max(r => Math.Abs(r.Score -
            rest.Average(c => engine.Recommend(Mid(c, ban: Shown[2]), 1, [r.ChampionId])[0].Score)));
        Check("забаненный из показанных в среднее не идёт", bWorst < 1e-6, $"расхождение {bWorst:E1}");

        // Его ход: наведённый в ChampionId — то же среднее, что и по намерению.
        var inTurn = Mid(Shown[1], locked: true) with { ActiveCells = [WaverCell], Wavering = wave };
        Check("в свой ход (чемпион в championId) — то же среднее",
              Same(engine.Recommend(inTurn, 6), avg), "");

        // Залочил — считается сам, метания забыты.
        var lockedState = Mid(Shown[1], locked: true);
        Check("залочивший союзник — по своему чемпиону",
              Same(engine.Recommend(lockedState with { Wavering = wave }, 6), engine.Recommend(lockedState, 6)), "");

        // Двое мечутся: по-прежнему стоит, когда любой из них переходит к показанному.
        int[] tops = [86, 122, 24];   // Гарен, Дариус, Джакс — топ передумал лочить
        var two = new Dictionary<int, IReadOnlyList<int>> { [WaverCell] = Shown, [2] = tops };
        var twoA = engine.Recommend(Mid(Shown[0], top: tops[0]) with { Wavering = two }, 6);
        var twoB = engine.Recommend(Mid(Shown[3], top: tops[2]) with { Wavering = two }, 6);
        Console.WriteLine($"  мечутся двое, среднее:          {NameList(Ids(twoA))}");
        Check("мечутся двое — список стоит, кто бы из них куда ни навёл", Same(twoA, twoB), "");

        // Время: каждый показанный — ещё один пересчёт.
        sw.Restart();
        engine.Recommend(Mid(Shown[1]), 6);
        var plainMs = sw.ElapsedMilliseconds;
        sw.Restart();
        engine.Recommend(Mid(Shown[1]) with { Wavering = two }, 6);
        var twoMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"  время: обычный {plainMs} мс, мечется один {avgMs} мс (первый, холодный), двое {twoMs} мс");
        Check("двое мечутся — подбор укладывается в секунду", twoMs < 1000, $"{twoMs} мс");
    }

    /// Мид; Гарен (топ) и Джинкс (бот) залочены, лес навёл forest; враги Зед и Ясуо.
    private static DraftState Mid(int forest, bool locked = false, int me = 0, int ban = 0, int top = 0)
    {
        var team = new List<DraftPlayer>
        {
            new(0, 0, me, "middle", true),
            new(WaverCell, locked ? forest : 0, locked ? 0 : forest, "jungle", false),
            new(2, top == 0 ? 86 : 0, top, "top", false),
            new(3, 222, 0, "bottom", false),
            new(4, 0, 0, "utility", false),
        };
        var their = new List<DraftPlayer> { new(5, 238, 0, "", false), new(6, 157, 0, "", false) };
        return Draft(team, their, ban == 0 ? [] : [ban]);
    }

    private static DraftState Draft(List<DraftPlayer> team, List<DraftPlayer> their, IReadOnlyList<int>? bans = null) =>
        new(MyTeam: team, TheirTeam: their, MyTeamBans: bans ?? [], TheirTeamBans: [],
            Me: team[0], MyPosition: team[0].Position, DirectOpponent: null, ExposedToCounter: true,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);

    private static bool Same(IReadOnlyList<Recommendation> a, IReadOnlyList<Recommendation> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.ChampionId == x.Second.ChampionId
                                                && x.First.Score == x.Second.Score
                                                && x.First.SynergyDelta == x.Second.SynergyDelta
                                                && x.First.Reasons.SequenceEqual(x.Second.Reasons));

    private static List<int> Ids(IEnumerable<Recommendation> r) => [.. r.Select(x => x.ChampionId)];

    /// Имена чемпионов в выводе: кэш Data Dragon игрока копируем к себе.
    private static void Names(string playerDir)
    {
        var from = Path.Combine(playerDir, "ddragon");
        if (Directory.Exists(from))
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Name(int id) => DataDragon.Name(id);
    private static string NameList(IEnumerable<int> ids) => string.Join(", ", ids.Select(Name));

    private static void Check(string what, bool ok, string detail)
    {
        if (!ok) _fails++;
        Console.WriteLine($"  {(ok ? "ок  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    }
}
