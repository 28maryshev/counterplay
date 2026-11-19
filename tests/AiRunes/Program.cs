using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Подбор рун под состав — Counterplay AI на третьем месте панели рун
/// (владелец, 19.11: «на нижнее место, где Искусное лавирование, добавим
/// Counterplay AI runes, которые будут работать как наши предметы, — подбирать
/// руны после финальных пиков»). Повод — Хватка нежити первым вариантом у Эш
/// стрелком: на роли её почти никто не берёт.
///
/// Подбор (RuneAdvisor):
///  1. танки и контроль — Резня, Неудержимость, осколок стойкости; страница
///     остаётся допустимой: каждая руна в своём ряду своего дерева;
///  2. руну, которую на чемпионе не берут (доля ниже порога), не ставим; в
///     старом файле долей нет — правило работает;
///  3. дерева на странице нет — руну оттуда не ставим;
///  4. ряд защиты Решимости решает оппонент по линии: с наскока — Костяная
///     броня, издалека — Второе дыхание;
///  5. мягкий состав — ничего из этого.
/// Окно:
///  6. вариантов два, третье место — подбор: пока пики идут, пустая карточка
///     со знаком и шкалой «сколько игроков выбрали»;
///  7. пятеро врагов взяли, а союзник ещё нет — подбор ждёт;
///  8. все выбрали — на карточке страница: заменённые руны в кольце, подбор
///     выбран сам, «Применить» отдаёт его страницу;
///  9. игрок сам выбрал вариант до конца пиков — подбор выбор не перехватывает;
/// 10. новый чемпион — снова пустое место, шкала с нуля.
///
/// Справочники Riot — из кэша игрока (копией в свою папку) или из сети; база
/// матчей — настоящая, только чтение; настройки и пулы — в своей папке; окно
/// невидимое. CP_SNAP=папка — снимки окна до и после конца пиков.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Ashe = 22, Jinx = 222;
    // Пятеро танков с жёстким контролем: Мальфит, Седжуани, Леона, Наутилус, Амуму.
    private static readonly int[] Tanky = [54, 113, 89, 111, 32];
    // Мягкий состав: Эзреаль, Люкс, Джинкс, Ари, Зиггс.
    private static readonly int[] Soft = [81, 99, 222, 103, 115];
    private static readonly int[] Allies = [266, 64, 103, 412];   // Аатрокс, Ли Син, Ари, Треш

    private const int LT = 8008, PTA = 8005, Coup = 8014, Overgrowth = 8451;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AiRunes");
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

        try
        {
            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            if (!RuneIcons.Loaded) { Check("справочник рун загружен", false, "нет ни кэша, ни сети"); return Done(); }

            Advisor(engine);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window(engine, Environment.GetEnvironmentVariable("CP_SNAP"));
            app.Shutdown();
        }
        finally
        {
            AppPaths.RootOverride = null;
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: подбор рун меняет только ходовые руны в своём ряду и приходит после всех пиков"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── Данные ───────────────────────────────────────────────────────────────

    private const int Zed = 238, Senna = 235;

    /// Связка Эш стрелком: Темп (Точность + вторичное sub) и, по желанию,
    /// Натиск; доли рун и деревьев — как в выгрузке с 19.11 (withShares=false —
    /// старый файл без долей).
    private static ChampStats Ashe_(int[] secondary, int sub = 8400, int row3 = Coup,
                                    Dictionary<int, double>? share = null,
                                    Dictionary<int, double>? subPick = null,
                                    bool withShares = true, bool withPta = false,
                                    Dictionary<int, double>? vsZed = null)
    {
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>> subShare =
            new Dictionary<int, IReadOnlyDictionary<int, double>>
            {
                [8400] = new Dictionary<int, double>
                         { [8444] = 50, [8473] = 40, [8429] = 20, [Overgrowth] = 30, [RuneAdvisor.Unflinching] = 10 },
                [8300] = new Dictionary<int, double> { [8345] = 90, [8347] = 80, [8304] = 20 },
            };
        RuneChoice Ks(int ks, double wr, double pick, int primary, int[] perks, int s, int[] sec) =>
            new(ks, 100000, wr, pick, new RunePage(primary, s, perks, sec, [5005, 5008, 5011], 50000, wr), 0, 0)
            {
                Share = withShares ? share ?? new() { [Coup] = 60, [RuneAdvisor.CutDown] = 33, [9111] = 95, [9104] = 90 } : null,
                SubShare = withShares ? subShare : null,
                SubPick = withShares ? subPick ?? new() { [8400] = 55, [8300] = 45 } : null,
            };
        var list = new List<RuneChoice> { Ks(LT, 50.1, 89.7, 8000, [LT, 9111, 9104, row3], sub, secondary) };
        if (withPta) list.Add(Ks(PTA, 49.5, 8.4, 8000, [PTA, 9111, 9104, Coup], 8300, [8345, 8347]));
        var vs = new Dictionary<int, (int, Dictionary<int, double>)>();
        if (vsZed is not null) vs[Zed] = (900, vsZed);
        return new ChampStats(Ashe, "adc", "16.22", 268180, list, vs, [], []);
    }

    private static RuneAdvisor.Advice? Adapt(RecommendationEngine engine, ChampStats s, int[] enemies, int? lane = null) =>
        RuneAdvisor.Adapt(s, Ashe, enemies, lane, id => engine.DamageShare(id), RuneIcons.SlotOf);

    /// Каждая руна страницы — в своём ряду своего дерева, ряды не повторяются.
    private static bool Legal(RunePage p)
    {
        var prim = p.Perks.Select(RuneIcons.SlotOf).ToList();
        var sec = p.Secondary.Select(RuneIcons.SlotOf).ToList();
        return prim.Count == 4 && prim.Select((s, i) => s is { } v && v.Style == p.Primary && v.Row == i).All(x => x)
            && sec.Count == 2 && sec.All(s => s is { } v && v.Style == p.Sub && v.Row > 0)
            && sec[0]!.Value.Row != sec[1]!.Value.Row;
    }

    private static string Show(RuneAdvisor.Advice? a) =>
        a is null ? "нет подбора"
        : (a.Swaps.Count == 0 ? "замен нет"
           : string.Join("; ", a.Swaps.Select(s => $"{RuneIcons.NameOf(s.From)}→{(s.Shard ? "осколок " + s.To : RuneIcons.NameOf(s.To))}"
                                                  + (s.Why.Length > 0 ? $" ({s.Why})" : ""))))
          + $" | {RuneIcons.StyleName(a.Choice.Page.Sub)}: {string.Join(",", a.Choice.Page.Secondary.Select(RuneIcons.NameOf))}";

    private static void Advisor(RecommendationEngine engine)
    {
        Console.WriteLine("подбор:");
        Console.WriteLine($"  Зед бьёт с наскока: {ChampionTraits.BurstThreat(Zed)}, Сенна издалека: "
                          + $"{ChampionTraits.LongRange(Senna) && !ChampionTraits.BurstThreat(Senna)}");
        var baseStats = Ashe_([8444, Overgrowth]);

        // ── 1. Танки и контроль ─────────────────────────────────────────────
        var a = Adapt(engine, baseStats, Tanky);
        var to = a?.Swaps.Select(s => s.To).ToHashSet() ?? [];
        Check("танки и контроль: Резня, Неудержимость, осколок стойкости",
              to.IsSupersetOf([RuneAdvisor.CutDown, RuneAdvisor.Unflinching, RuneAdvisor.ShardTenacity]), Show(a));
        Check("страница допустима: руны в своих рядах, кейстоун тот же",
              a is not null && Legal(a.Choice.Page) && a.Choice.Keystone == LT
              && a.Choice.Page.Perks.Contains(RuneAdvisor.CutDown) && !a.Choice.Page.Perks.Contains(Coup)
              && a.Choice.Page.Shards[2] == RuneAdvisor.ShardTenacity,
              a is null ? "" : $"{string.Join(",", a.Choice.Page.Perks)} | {string.Join(",", a.Choice.Page.Secondary)} | {string.Join(",", a.Choice.Page.Shards)}");
        Check("ходовая страница не тронута",
              baseStats.Keystones[0].Page.Perks[3] == Coup && baseStats.Keystones[0].Page.Shards[2] == 5011, "");

        // ── 2. Руну не берут ────────────────────────────────────────────────
        var rare = Adapt(engine, Ashe_([8444, Overgrowth], share: new() { [Coup] = 99, [RuneAdvisor.CutDown] = 1 }), Tanky);
        Check("Резню на чемпионе почти не берут (1%) — не ставим",
              rare is not null && rare.Swaps.All(s => s.To != RuneAdvisor.CutDown), Show(rare));
        var old = Adapt(engine, Ashe_([8444, Overgrowth], withShares: false), Tanky);
        Check("в старом файле долей нет — правило работает",
              old?.Swaps.Any(s => s.To == RuneAdvisor.CutDown) == true, Show(old));

        // ── 3. Вторичное дерево меняется под оппонента ──────────────────────
        var tree = Adapt(engine, Ashe_([8345, 8347], sub: 8300), Soft, lane: Zed);
        Check("Вдохновение, а на линии Зед — вторичным становится Решимость с Костяной бронёй",
              tree is not null && tree.Choice.Page.Sub == 8400 && tree.Choice.Page.Secondary.Contains(RuneAdvisor.BonePlating)
              && Legal(tree.Choice.Page)
              && tree.Swaps.Any(s => s.To == RuneAdvisor.BonePlating && s.Why.Contains(DataDragon.Name(Zed))),
              Show(tree));
        var rareTree = Adapt(engine, Ashe_([8345, 8347], sub: 8300, subPick: new() { [8300] = 95, [8400] = 5 }), Soft, lane: Zed);
        Check("Решимость с этим кейстоуном берут в 5% игр — дерево не меняем",
              rareTree is not null && rareTree.Choice.Page.Sub == 8300 && Legal(rareTree.Choice.Page), Show(rareTree));
        var oldTree = Adapt(engine, Ashe_([8345, 8347], sub: 8300, withShares: false), Soft, lane: Zed);
        Check("в старом файле долей деревьев нет — дерево не меняем",
              oldTree is not null && oldTree.Choice.Page.Sub == 8300, Show(oldTree));
        var pokeInsp = Adapt(engine, Ashe_([8345, 8347], sub: 8300), Soft, lane: Senna);
        Check("Вдохновение с Печеньем против поука — уже отвечает, дерево не меняем",
              pokeInsp is not null && pokeInsp.Choice.Page.Sub == 8300
              && pokeInsp.Covered.Any(s => s.To == RuneAdvisor.Biscuits), Show(pokeInsp));

        // ── 4. Руны вторичного дерева — по оппоненту ────────────────────────
        var allin = Adapt(engine, Ashe_([8444, Overgrowth]), Soft, lane: Zed);
        Check("оппонент с наскока — Костяная броня вместо Второго дыхания, причина называет его",
              allin?.Swaps.Any(s => s.From == 8444 && s.To == RuneAdvisor.BonePlating
                                    && s.Why.Contains(DataDragon.Name(Zed))) == true && Legal(allin!.Choice.Page),
              Show(allin));
        var poke = Adapt(engine, Ashe_([RuneAdvisor.BonePlating, Overgrowth]), Soft, lane: Senna);
        Check("оппонент издалека — Второе дыхание вместо Костяной брони",
              poke?.Swaps.Any(s => s.From == RuneAdvisor.BonePlating && s.To == RuneAdvisor.SecondWind) == true,
              Show(poke));
        var already = Adapt(engine, Ashe_([RuneAdvisor.BonePlating, Overgrowth]), Soft, lane: Zed);
        Check("Костяная броня уже стоит — замены нет, но в «уже отвечает» она есть",
              already is not null && already.Swaps.All(s => s.To != RuneAdvisor.BonePlating)
              && already.Covered.Any(s => s.To == RuneAdvisor.BonePlating),
              Show(already));

        // ── 5. Кейстоун — по данным против оппонента ────────────────────────
        var ks = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [PTA] = 1.5, [LT] = -0.5 }), Soft, lane: Zed);
        Check("против Зеда Натиск выигрывает больше Темпа — кейстоун Натиск, с причиной",
              ks is not null && ks.Choice.Keystone == PTA && ks.KeystoneWhy?.Contains(DataDragon.Name(Zed)) == true,
              $"{(ks is null ? "—" : RuneIcons.NameOf(ks.Choice.Keystone))}: {ks?.KeystoneWhy}");
        var noLane = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [PTA] = 1.5, [LT] = -0.5 }), Soft);
        Check("без оппонента — Темп (лучший винрейт)", noLane?.Choice.Keystone == LT && noLane.KeystoneWhy is null,
              noLane is null ? "" : RuneIcons.NameOf(noLane.Choice.Keystone));

        // ── 6. Мягкий состав ────────────────────────────────────────────────
        var soft = Adapt(engine, Ashe_([8444, Overgrowth], row3: RuneAdvisor.CutDown), Soft);
        Check("толстых нет — Удар милосердия вместо Резни; ни Неудержимости, ни осколка",
              soft is not null && soft.Swaps.Any(s => s.From == RuneAdvisor.CutDown && s.To == Coup)
              && soft.Swaps.All(s => s.To is not (RuneAdvisor.Unflinching or RuneAdvisor.ShardTenacity)),
              Show(soft));
        Check("разбор для песочницы есть: оппонент, состав, что нужно",
              tree?.Trace.Any(t => t.Contains(DataDragon.Name(Zed))) == true && tree.Trace.Any(t => t.StartsWith("состав"))
              && tree.Trace.Any(t => t.StartsWith("вторичное")),
              string.Join(" / ", tree?.Trace ?? []));
    }

    // ── Окно ─────────────────────────────────────────────────────────────────

    private static ChampStats Stats(int champ) => RunesClient.Parse(
        $"{{\"champion\":{champ},\"role\":\"adc\",\"patches\":[\"16.22\"],\"games\":268180,\"wr\":50.0," +
        "\"keystones\":[" +
        "{\"id\":8008,\"games\":240429,\"wr\":50.1,\"pick\":89.7,\"page\":{\"primary\":8000,\"sub\":8400," +
        "\"perks\":[8008,9111,9104,8014],\"secondary\":[8444,8451],\"shards\":[5005,5008,5011],\"games\":90000,\"wr\":50.3}," +
        "\"share\":{\"8014\":60.0,\"8017\":33.0,\"9111\":95.0,\"9104\":90.0}," +
        "\"subShare\":{\"8400\":{\"8444\":50.0,\"8473\":40.0,\"8451\":30.0,\"8242\":10.0}},\"subPick\":{\"8400\":60.0,\"8300\":40.0}}," +
        "{\"id\":8005,\"games\":22533,\"wr\":47.4,\"pick\":8.4,\"page\":{\"primary\":8000,\"sub\":8300," +
        "\"perks\":[8005,9111,9104,8014],\"secondary\":[8345,8347],\"shards\":[5005,5008,5011],\"games\":9000,\"wr\":47.6}}" +
        "],\"vs\":{},\"items\":[],\"spells\":[],\"builds\":[]}");

    private static void Window(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine("окно:");
        var s = AppSettings.Current;
        s.DraftRunes = true;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        var trace = "";
        w.RuneAiTrace = t => trace = t;
        Pump();

        var stats = Stats(Ashe);
        Check("в файле доли рун прочитаны", stats.Keystones[0].Share?.GetValueOrDefault(RuneAdvisor.CutDown) == 33.0
              && stats.Keystones[0].SubShare?[8400].GetValueOrDefault(RuneAdvisor.Unflinching) == 10.0
              && stats.Keystones[1].Share is null, "");

        void Draft(int me, int[] enemies, int alliesLocked)
        {
            var d = State(me, enemies, alliesLocked);
            w.UpdateRecommendations(engine.Recommend(d, 6), d, engine);
            Pump();
        }

        Draft(Ashe, [], 0);
        w.ShowRunes(stats, Ashe, DataDragon.Name(Ashe), "adc", null);
        Pump();
        Check("руны на экране", Vis(w, "RunesBar"), "");

        // ── 6. Пока пики идут ───────────────────────────────────────────────
        var cards = Cards(w);
        var ai = cards.FirstOrDefault(c => c.Vm.IsAi);
        Check("вариантов два, подбор — третьим, внизу слева, пока пустой",
              cards.Count(c => !c.Vm.IsAi) == 2 && ai.Vm is { Index: -1, FillVis: Visibility.Visible }
              && ai.Row == 1 && ai.Col == 0,
              string.Join(", ", cards.Select(c => $"{(c.Vm.IsAi ? "AI" : c.Vm.Label)}@{c.Row},{c.Col}")));
        Check("знак горит, рамки блёклые", ai.Vm is { AiVis: Visibility.Visible, BodyOpacity: < 0.5 }, $"{ai.Vm?.BodyOpacity}");

        for (var n = 1; n <= 4; n++) Draft(Ashe, Tanky.Take(n).ToArray(), Math.Min(n, 4));
        Settle();
        var bar = Bar(w);
        var want = 72.0 * (1 + 4 + 4) / 10;
        Check("шкала — сколько игроков выбрали: 9 из 10",
              bar is not null && Math.Abs(bar.Width - want) < 0.5, $"{bar?.Width:0.#} из 72 (ждали {want:0.#})");

        // ── 7. Враги все, союзник ещё нет ───────────────────────────────────
        Draft(Ashe, Tanky, 3);
        Settle();
        ai = Cards(w).FirstOrDefault(c => c.Vm.IsAi);
        Check("пятеро врагов взяли, союзник ещё выбирает — подбор ждёт",
              ai.Vm is { Index: -1, FillVis: Visibility.Visible }, $"карточка {ai.Vm?.Index}");
        if (snap is { Length: > 0 }) Snap(w, snap, "runes-ai-1-waiting.png");

        // ── 8. Все выбрали ──────────────────────────────────────────────────
        Draft(Ashe, Tanky, 4);
        Settle();
        ai = Cards(w).FirstOrDefault(c => c.Vm.IsAi);
        // Роли врагов скрыты (как в соло/дуо) — оппонента подбору даёт окно:
        // того, кого само поставило на мою роль.
        var lane = (Get(w, "_lastDraft") as DraftState)?.DirectOpponent?.EffectiveChampionId ?? 0;
        Check("клиент оппонента не назвал — подбор берёт того, кого окно поставило на мою линию",
              lane != 0 && trace.Contains("оппонент по линии: " + DataDragon.Name(lane)),
              $"окно: {(lane == 0 ? "никого" : DataDragon.Name(lane))}; разбор: {trace.Split('\n')[0]}");
        var sel = (int)Get(w, "_runeSelected")!;
        var rings = ai.Vm?.PrimaryMini.Concat(ai.Vm.SecondaryMini)
            .Count(r => r.Ring is SolidColorBrush b && b.Color == System.Windows.Media.Color.FromRgb(0x36, 0xD6, 0xE7)) ?? 0;
        Check("все выбрали — на карточке страница, заменённые руны в кольце",
              ai.Vm is { Index: 2, FillVis: Visibility.Collapsed, BodyOpacity: 1.0 } && ai.Vm.KeystoneIcon is not null
              && rings >= 2,
              $"карточка {ai.Vm?.Index}, в кольце {rings}");
        Check("подсказка: заголовок и причины", ai.Vm?.Tip.StartsWith(Loc.T("runes.aiTipHead")) == true
              && ai.Vm.Tip.Contains(RuneIcons.NameOf(RuneAdvisor.CutDown)), ai.Vm?.Tip.Replace("\n", " / ") ?? "");
        var page = Call(w, "PageAt", sel) as RunePage;
        Check("подбор выбран сам, «Применить» отдаёт его страницу",
              sel == 2 && page is not null && page.Perks.Contains(RuneAdvisor.CutDown) && page.Shards[2] == RuneAdvisor.ShardTenacity,
              $"выбрано {sel}, страница {(page is null ? "—" : string.Join(",", page.Perks) + " | " + string.Join(",", page.Shards))}");
        if (snap is { Length: > 0 }) Snap(w, snap, "runes-ai-2-ready.png");

        // ── 9. Выбрал сам до конца пиков ────────────────────────────────────
        Draft(Jinx, Tanky, 3);
        w.ShowRunes(Stats(Jinx), Jinx, DataDragon.Name(Jinx), "adc", null);
        Pump();
        Click(w, 1);
        Draft(Jinx, Tanky, 4);
        Settle();
        sel = (int)Get(w, "_runeSelected")!;
        ai = Cards(w).FirstOrDefault(c => c.Vm.IsAi);
        Check("игрок выбрал второй вариант сам — подбор пришёл, но выбор его",
              sel == 1 && ai.Vm is { Index: 2 }, $"выбрано {sel}, подбор {ai.Vm?.Index}");

        // ── 10. Новый чемпион ───────────────────────────────────────────────
        Draft(Ashe, Tanky.Take(1).ToArray(), 0);
        w.ShowRunes(stats, Ashe, DataDragon.Name(Ashe), "adc", null);
        Pump();
        ai = Cards(w).FirstOrDefault(c => c.Vm.IsAi);
        var from = ai.Vm?.FillWidth;
        Settle();
        var end = Bar(w);
        Check("новый чемпион — пустое место, шкала растёт с нуля",
              ai.Vm is { Index: -1 } && from == 0 && end is not null && Math.Abs(end.Width - 72.0 * 2 / 10) < 0.5
              && (int)Get(w, "_runeSelected")! == 0,
              $"карточка {ai.Vm?.Index}, шкала с {from:0.#} до {end?.Width:0.#} (ждали с 0 до {72.0 * 2 / 10:0.#})");
        w.Close();
        Pump();
    }

    /// Роль раздана, я стрелок и уже выбрал; союзники выбирают по порядку
    /// (alliesLocked из четырёх взяли, остальные пока наводят), враги взяли первых.
    private static DraftState State(int me, int[] enemies, int alliesLocked)
    {
        var self = new DraftPlayer(0, me, 0, "bottom", true);
        var team = new List<DraftPlayer> { self };
        for (var i = 0; i < Allies.Length; i++)
            team.Add(i < alliesLocked
                ? new DraftPlayer(1 + i, Allies[i], 0, "", false)
                : new DraftPlayer(1 + i, 0, Allies[i], "", false));
        var their = enemies.Select((id, i) => new DraftPlayer(5 + i, id, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: team, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: self, MyPosition: "bottom", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static List<(OverlayWindow.RuneOptionVm Vm, int Row, int Col, ContentPresenter Cp)> Cards(Window w) =>
        ((Grid)w.FindName("RuneGrid")).Children.OfType<ContentPresenter>()
            .Where(c => c.Content is OverlayWindow.RuneOptionVm)
            .Select(c => ((OverlayWindow.RuneOptionVm)c.Content, Grid.GetRow(c), Grid.GetColumn(c), c))
            .ToList();

    /// Шкала в карточке подбора — из её шаблона.
    private static FrameworkElement? Bar(Window w)
    {
        var cp = Cards(w).FirstOrDefault(c => c.Vm.IsAi).Cp;
        if (cp is null) return null;
        cp.ApplyTemplate();
        return cp.ContentTemplate?.FindName("AiFillBar", cp) as FrameworkElement;
    }

    private static void Click(OverlayWindow w, int index)
    {
        var fe = new FrameworkElement { Tag = index };
        var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Call(w, "RuneOption_Click", fe, e);
        Pump();
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    /// Имена, иконки и руны. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons", "items" })
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
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static object? Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static object? Get(object o, string field) =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o);

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Дождаться конца анимаций (шкала растёт 0,45 с).
    private static void Settle()
    {
        for (var i = 0; i < 12; i++) Pump();
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
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
