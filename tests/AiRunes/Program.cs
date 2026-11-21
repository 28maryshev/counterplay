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
            RealFiles(engine);
            // Отдельный режим: подбор по всем файлам выгрузки (Sweep.cs).
            if (Environment.GetEnvironmentVariable("CP_SWEEP") is { Length: > 0 } sweepDir)
            {
                Sweep.Run(engine, sweepDir,
                          Environment.GetEnvironmentVariable("CP_SWEEP_OUT") ?? Path.Combine(sweepDir, "sweep.txt"));
                return Done();
            }
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

    // Новые чемпионы, чьи наборы ещё не разобраны по гайдам (ChampionTraits):
    // идут по меткам. Новый чемпион в Data Dragon — повод внести его в список.
    private static readonly string[] NewChamps = ["Locke", "Yunara", "Zaahen"];

    /// <summary>
    /// 7. Настоящие файлы выгрузки. Владелец (20.11): «хочу, чтобы в релизе руны
    /// отрабатывали как надо». Подбор до сих пор проверялся на собранных руками
    /// данных, выгрузка — на своей временной базе; файлы в tests/AiRunes/files
    /// выгрузка сделала 20.11 из живой базы коллектора (с vsDuo и perkVs после
    /// девяти часов сбора): Кай'Са стрелком, Лулу в поддержке, Атрокс на топе.
    /// Программа их читает, подбор по ним работает: кейстоун — и против
    /// второго бот-лейнера, правило по линии — сверкой с настоящими цифрами.
    /// И файл без новых полей (таким он на сайте до первой выкладки) —
    /// читается, подбор — по правилам.
    /// </summary>
    private static void RealFiles(RecommendationEngine engine)
    {
        var dir = Path.Combine(RepoRoot(), "tests", "AiRunes", "files");
        ChampStats Load(string name) => RunesClient.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")));
        RuneAdvisor.Advice? Run(ChampStats s, int lane, int[] foes, int[] enemies) =>
            RuneAdvisor.Adapt(s, s.ChampionId, enemies, lane, foes, id => engine.DamageShare(id), RuneIcons.SlotOf);
        string Line(RuneAdvisor.Advice? a, string part) => a?.Trace.FirstOrDefault(t => t.Contains(part)) ?? "—";

        const int Kaisa = 145, Lulu = 117, Aatrox = 266, Jhin = 202, Thresh = 412, Jayce = 126;
        int[] team = [Jhin, Thresh, 238, 64, 122];          // + Зед, Ли Син, Дариус

        // Стрелок: против Джина (той же роли) и Треша (второй бот-лейнер).
        var kaisa = Load("145-adc");
        Check("настоящий файл стрелка читается: поправки против второго бот-лейнера и руны против соперников есть",
              kaisa.ChampionId == Kaisa && kaisa.VsDuo.ContainsKey(Thresh) && kaisa.PerkVs.Count > 20,
              $"vs {kaisa.Vs.Count}, vsDuo {kaisa.VsDuo.Count}, perkVs {kaisa.PerkVs.Count}");
        var with = RunesClient.Choices(kaisa, Jhin, int.MaxValue, Thresh).ToDictionary(k => k.Keystone, k => k.VsDelta);
        var without = RunesClient.Choices(kaisa, Jhin, int.MaxValue).ToDictionary(k => k.Keystone, k => k.VsDelta);
        var kAdv = Run(kaisa, Jhin, [Jhin, Thresh], team);
        Check("стрелок: кейстоун считается и против Треша (поправки меняются), подбор есть, страница допустима",
              with.Any(kv => Math.Abs(kv.Value - without.GetValueOrDefault(kv.Key)) > 0.001)
              && kAdv is not null && Legal(kAdv.Choice.Page)
              && Line(kAdv, "второй бот-лейнер:").Contains(DataDragon.Name(Thresh)) && !Line(kAdv, "второй бот-лейнер:").Contains("нет)"),
              $"{Line(kAdv, "оппонент по линии")} | {Line(kAdv, "кейстоуны:")}");
        // Против Треша у Темпа −4,8 на 358 играх (это в основном сама линия), у
        // Натиска +0,5 на 13. Выгрузка ужимает поправку к нулю, и Натиск (7%
        // игр, 47% побед) выходил «лучше на пять пунктов» только потому, что
        // про него почти ничего не известно. Поправку с малой выборкой надо
        // ужимать к линии, а не к нулю: тогда — Темп.
        Check("горстка игр против Треша не делает Натиск лучшим: подбор — Смертельный темп",
              kAdv?.Choice.Keystone == 8008,
              $"{(kAdv is null ? "—" : RuneIcons.NameOf(kAdv.Choice.Keystone))} | {Line(kAdv, "кейстоуны:")}");

        // Саппорт: против Треша (той же роли) и Кай'Сы (второй бот-лейнер).
        var lulu = Load("117-support");
        var lAdv = Run(lulu, Thresh, [Thresh, Kaisa], [Kaisa, Thresh, 238, 64, 122]);
        Check("саппорт: настоящий файл читается, кейстоун и против Кай'Сы, страница допустима",
              lulu.VsDuo.ContainsKey(Kaisa) && lAdv is not null && Legal(lAdv.Choice.Page)
              && !Line(lAdv, "второй бот-лейнер:").Contains("нет)"),
              $"{Line(lAdv, "оппонент по линии")} | {Line(lAdv, "кейстоуны:")}");

        // Топ: на странице Атрокса — Костяная пластина; против Джейса (поук)
        // правило просит Второе дыхание в тот же ряд — и сверяет его с
        // Костяной пластиной по настоящим играм Атрокса против поука.
        var aatrox = Load("266-top");
        var cmp = RuneAdvisor.Compare(aatrox, RuneAdvisor.SecondWind, RuneAdvisor.BonePlating, RuneAdvisor.LaneKind.Poke);
        var aAdv = Run(aatrox, Jayce, [Jayce], [Jayce, Thresh, 238, 64, Jhin]);
        Check("топ: правило «против поука» сверено с настоящими играми Атрокса (Второе дыхание против Костяной пластины)",
              cmp is { Total: > 0, Against: RuneAdvisor.BonePlating } && aAdv is not null && Legal(aAdv.Choice.Page)
              && Line(aAdv, "против поука:").Contains("вес"),
              $"{(cmp is null ? "сравнить не по чему" : $"разница {cmp.Raw:+0.0;-0.0}, игр {cmp.Total}, итог {cmp.Mixed:+0.0;-0.0}")} | {Line(aAdv, "против поука:")}");

        // Владелец (21.11): «вчера на Соне я видел Комету в кейстоунах». Файл
        // Соны с сайта на 21.11: Пушинка — 89% игр и 51,9% побед, Комета — 7% и
        // 48,6%. Против Амуму у Пушинки −5,3 на 298 играх (линия тяжёлая), у
        // Кометы +2,3 на 28: разброс на 28 играх — ±9 пунктов, настоящая разница
        // кейстоунов — пункт-два. Ужатие отличия к линии весом 50 игр (как
        // ужимает выгрузка) ставило Комету первой; весом ~600 — Пушинку.
        const int Sona = 37, Amumu = 32, Sivir = 15;
        var sona = Load("37-support");
        var first = RunesClient.Choices(sona, Amumu, 2, Sivir)[0];
        var sAdv = Run(sona, Amumu, [Amumu, Sivir], [Amumu, Sivir, 238, 64, 122]);
        Check("Сона против Амуму и Сивир: 28 игр Кометы не перевешивают — первой Пушинка, подбор тоже",
              first.Keystone == 8214 && sAdv?.Choice.Keystone == 8214,
              $"первая {RuneIcons.NameOf(first.Keystone)}, подбор {(sAdv is null ? "—" : RuneIcons.NameOf(sAdv.Choice.Keystone))} | {Line(sAdv, "кейстоуны:")}");

        // Файл без новых полей — таким он на сайте до первой выкладки.
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "145-adc.json")))!.AsObject();
        node.Remove("vsDuo");
        node.Remove("perkVs");
        var old = RunesClient.Parse(node.ToJsonString());
        var oAdv = Run(old, Jhin, [Jhin, Thresh], team);
        Check("файл без новых полей читается, подбор по правилам",
              old.VsDuo.Count == 0 && old.PerkVs.Count == 0 && oAdv is not null && Legal(oAdv.Choice.Page)
              && oAdv.Trace.Any(t => t.Contains("в файле нет")),
              Line(oAdv, "в файле нет"));
    }

    /// Корень репозитория — где лежит Counterplay.csproj (проверка запускается из bin).
    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("не нашёл Counterplay.csproj выше " + AppContext.BaseDirectory);
    }

    /// Связка Эш стрелком: Темп (Точность + вторичное sub) и, по желанию,
    /// Натиск; доли рун и деревьев — как в выгрузке с 19.11 (withShares=false —
    /// старый файл без долей).
    private static ChampStats Ashe_(int[] secondary, int sub = 8400, int row3 = Coup,
                                    Dictionary<int, double>? share = null,
                                    Dictionary<int, double>? subPick = null,
                                    bool withShares = true, bool withPta = false,
                                    Dictionary<int, double>? vsZed = null, int shard3 = 5011,
                                    double ptaWr = 49.5, Dictionary<int, double>? duoVsLux = null,
                                    Dictionary<int, PerkVsFoe>? perkVs = null)
    {
        IReadOnlyDictionary<int, IReadOnlyDictionary<int, double>> subShare =
            new Dictionary<int, IReadOnlyDictionary<int, double>>
            {
                [8400] = new Dictionary<int, double>
                         { [8444] = 50, [8473] = 40, [8429] = 20, [Overgrowth] = 30, [RuneAdvisor.Unflinching] = 10 },
                [8300] = new Dictionary<int, double> { [8345] = 90, [8347] = 80, [8304] = 20 },
            };
        RuneChoice Ks(int ks, double wr, double pick, int primary, int[] perks, int s, int[] sec) =>
            new(ks, 100000, wr, pick, new RunePage(primary, s, perks, sec, [5005, 5008, shard3], 50000, wr), 0, 0)
            {
                Share = withShares ? share ?? new() { [Coup] = 60, [RuneAdvisor.CutDown] = 33, [9111] = 95, [9104] = 90 } : null,
                SubShare = withShares ? subShare : null,
                SubPick = withShares ? subPick ?? new() { [8400] = 55, [8300] = 45 } : null,
            };
        var list = new List<RuneChoice> { Ks(LT, 50.1, 89.7, 8000, [LT, 9111, 9104, row3], sub, secondary) };
        if (withPta) list.Add(Ks(PTA, ptaWr, 8.4, 8000, [PTA, 9111, 9104, Coup], 8300, [8345, 8347]));
        var vs = new Dictionary<int, (int, Dictionary<int, double>)>();
        if (vsZed is not null) vs[Zed] = (900, vsZed);
        var vsDuo = new Dictionary<int, (int, Dictionary<int, double>)>();
        if (duoVsLux is not null) vsDuo[99] = (700, duoVsLux);
        return new ChampStats(Ashe, "adc", "16.22", 268180, list, vs, [], [])
        {
            VsDuo = vsDuo,
            PerkVs = perkVs ?? new Dictionary<int, PerkVsFoe>(),
        };
    }

    private static RuneAdvisor.Advice? Adapt(RecommendationEngine engine, ChampStats s, int[] enemies, int? lane = null,
                                             int[]? foes = null) =>
        RuneAdvisor.Adapt(s, Ashe, enemies, lane, foes ?? (lane is { } l ? [l] : []),
                          id => engine.DamageShare(id), RuneIcons.SlotOf);

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

        // ── 4б. Кто как бьёт на линии — ручные списки важнее меток ──────────
        const int Xerath = 101, Darius = 122, Kennen = 85;
        var xer = Adapt(engine, Ashe_([RuneAdvisor.BonePlating, Overgrowth]), Soft, lane: Xerath);
        Check("Ксерат («взрывной» по меткам, на деле поук) — Второе дыхание, а не Костяная броня",
              xer?.Swaps.Any(s => s.To == RuneAdvisor.SecondWind) == true
              && xer.Choice.Page.Secondary.Contains(RuneAdvisor.SecondWind), Show(xer));
        var dar = Adapt(engine, Ashe_([8444, Overgrowth]), Soft, lane: Darius);
        Check("Дариус (без метки «взрывной») — Костяная броня",
              dar?.Swaps.Any(s => s.To == RuneAdvisor.BonePlating) == true, Show(dar));
        var ken = Adapt(engine, Ashe_([RuneAdvisor.BonePlating, Overgrowth]), Soft, lane: Kennen);
        Check("Кеннен (без метки поука) — Второе дыхание",
              ken?.Swaps.Any(s => s.To == RuneAdvisor.SecondWind && s.Why.Contains(DataDragon.Name(Kennen))) == true,
              Show(ken));
        var hp = Adapt(engine, Ashe_([8444, Overgrowth], shard3: RuneAdvisor.ShardHealthScaling), Soft, lane: Darius);
        Check("против наскока без контроля — осколок плоского здоровья вместо растущего",
              hp?.Choice.Page.Shards[2] == RuneAdvisor.ShardHealth, Show(hp));
        var hpPoke = Adapt(engine, Ashe_([8444, Overgrowth], shard3: RuneAdvisor.ShardHealthScaling), Soft, lane: Xerath);
        Check("против поука растущее здоровье остаётся", hpPoke?.Choice.Page.Shards[2] == RuneAdvisor.ShardHealthScaling,
              Show(hpPoke));

        // ── 4в. Бот — линия два на два; лес — линии нет ─────────────────────
        const int Caitlyn = 51, Leona = 89, Lux = 99;
        var duo = Adapt(engine, Ashe_([8444, Overgrowth]), Soft, lane: Caitlyn, foes: [Caitlyn, Leona]);
        Check("бот: Кейтлин издалека, но рядом Леона с наскока — Костяная броня, причина называет Леону",
              duo?.Swaps.Any(s => s.To == RuneAdvisor.BonePlating && s.Why.Contains(DataDragon.Name(Leona))) == true,
              Show(duo));
        var duoPoke = Adapt(engine, Ashe_([RuneAdvisor.BonePlating, Overgrowth]), Soft, lane: Caitlyn, foes: [Caitlyn, Lux]);
        // Владелец (20.11): «руны на боте у АДК и саппорта должны учитывать
        // пики двух врагов» — причина называет обоих, если так бьют оба.
        Check("бот: Кейтлин и Люкс — обе издалека — Второе дыхание, причина называет обеих",
              duoPoke?.Swaps.Any(s => s.To == RuneAdvisor.SecondWind
                                      && s.Why.Contains(DataDragon.Name(Caitlyn)) && s.Why.Contains(DataDragon.Name(Lux))) == true,
              Show(duoPoke));
        const int Draven = 119;
        var duoAllIn = Adapt(engine, Ashe_([8444, Overgrowth]), Soft, lane: Draven, foes: [Draven, Leona]);
        Check("бот: Дрейвен и Леона — оба с наскока — Костяная броня, причина называет обоих",
              duoAllIn?.Swaps.Any(s => s.To == RuneAdvisor.BonePlating
                                       && s.Why.Contains(DataDragon.Name(Draven)) && s.Why.Contains(DataDragon.Name(Leona))) == true,
              Show(duoAllIn));
        // Кейстоун — и против второго бот-лейнера: против Кейтлин поправок нет,
        // против Люкс у Натиска +2,0, у Темпа −0,5. Варианты (против
        // оппонента) — Темп; подбор — Натиск, причина называет обеих.
        var duoKs = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, duoVsLux: new() { [PTA] = 2.0, [LT] = -0.5 }),
                          Soft, lane: Caitlyn, foes: [Caitlyn, Lux]);
        Check("бот: кейстоун и против второго бот-лейнера — против Люкс Натиск лучше, причина называет обеих",
              duoKs?.Choice.Keystone == PTA && duoKs.KeystoneWhy?.Contains(DataDragon.Name(Lux)) == true
              && duoKs.KeystoneWhy.Contains(DataDragon.Name(Caitlyn)),
              $"{(duoKs is null ? "—" : RuneIcons.NameOf(duoKs.Choice.Keystone))}: {duoKs?.KeystoneWhy}");
        var jg = Adapt(engine, Ashe_([8444, Overgrowth]), Soft, lane: Zed, foes: []);
        Check("лес: соперников на линии нет — Костяной брони по оппоненту нет, разбор так и говорит",
              jg is not null && jg.Swaps.All(s => !s.Why.Contains(DataDragon.Name(Zed)))
              && jg.Trace.Any(t => t.Contains("линии нет") || t.Contains("не знаем")), Show(jg));
        var unknown = DataDragon.GetAllIconUrls().Keys
            .Where(id => !ChampionTraits.LaneKnown(id) && !NewChamps.Contains(DataDragon.DdId(id)))
            .Select(DataDragon.Name).ToList();
        Check("все чемпионы разобраны «как бьёт на линии» (кроме ещё не проверенных новых)",
              unknown.Count == 0, unknown.Count == 0 ? "" : "не в списках: " + string.Join(", ", unknown));

        // ── 5. Кейстоун — по данным против оппонента ────────────────────────
        var ks = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [PTA] = 1.5, [LT] = -0.5 }), Soft, lane: Zed);
        // Владелец (20.11, скриншот «+1,1 п.п.»): «что означают эти п.п.? на
        // человеческом языке, чтоб всем было понятно» — проценты и словами.
        Check("против Зеда Натиск выигрывает больше Темпа — кейстоун Натиск, с причиной словами: «на 2,0% чаще», без «п.п.»",
              ks is not null && ks.Choice.Keystone == PTA && ks.KeystoneWhy?.Contains(DataDragon.Name(Zed)) == true
              && ks.KeystoneWhy.Contains("2,0%") && !ks.KeystoneWhy.Contains("п.п.") && !ks.KeystoneWhy.Contains('+'),
              $"{(ks is null ? "—" : RuneIcons.NameOf(ks.Choice.Keystone))}: {ks?.KeystoneWhy}");
        var noLane = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [PTA] = 1.5, [LT] = -0.5 }), Soft);
        Check("без оппонента — Темп (лучший винрейт)", noLane?.Choice.Keystone == LT && noLane.KeystoneWhy is null,
              noLane is null ? "" : RuneIcons.NameOf(noLane.Choice.Keystone));

        // Владелец (20.11, скриншот): Шая против Зиггса — подбор взял
        // Завоевателя с причиной «против Зиггса: −2,3 п.п.». Против Зиггса в
        // минусе все кейстоуны (Темп −2,4, Завоеватель −2,3, Натиск −2,2): это
        // тяжёлая линия, а не плохая руна, а взят он за винрейт (52,5 против
        // 51,7). Минус причиной не бывает; причина — то, чем он правда лучше.
        var hard = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [PTA] = -2.2, [LT] = -2.4 }), Soft, lane: Zed);
        Check("тяжёлая линия, все кейстоуны в минусе — Темп без причины (он и так первый)",
              hard?.Choice.Keystone == LT && hard.KeystoneWhy is null,
              $"{(hard is null ? "—" : RuneIcons.NameOf(hard.Choice.Keystone))}: {hard?.KeystoneWhy}");
        var xayah = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, ptaWr: 50.9,
                                        vsZed: new() { [PTA] = -2.3, [LT] = -2.4 }), Soft, lane: Zed);
        Check("тяжёлая линия, Натиск выше по винрейту — причина «побеждает чаще Темпа», без минуса",
              xayah?.Choice.Keystone == PTA && xayah.KeystoneWhy is { } xw
              && xw.Contains(RuneIcons.NameOf(LT)) && !xw.Contains('−') && !xw.Contains("-2"),
              $"{(xayah is null ? "—" : RuneIcons.NameOf(xayah.Choice.Keystone))}: {xayah?.KeystoneWhy}");
        // Поправки против Зеда только у Темпа (−3): у Натиска игр с ним мало.
        // Натиску — средняя по прочим, а не ноль: иначе на тяжёлой линии он
        // выходил бы на три пункта лучше только из-за малой выборки.
        var thin = Adapt(engine, Ashe_([8444, Overgrowth], withPta: true, vsZed: new() { [LT] = -3.0 }), Soft, lane: Zed);
        Check("у Натиска нет поправки против Зеда — он не выигрывает за счёт пустоты: Темп",
              thin?.Choice.Keystone == LT,
              $"{(thin is null ? "—" : RuneIcons.NameOf(thin.Choice.Keystone))}: {thin?.KeystoneWhy}");

        // ── 5б. Правило по линии сверяется с цифрами ────────────────────────
        // Владелец (20.11): «а что если те руны сильнее были бы в конкретном
        // случае, даже учитывая, что там кто-то бьёт с наскока, — сопоставить
        // и определить, стоит ли поменять». Страница: Решимость со Вторым
        // дыханием; против Дрейвена (наскок) правило ставит Костяную броню в
        // тот же ряд. Цифры — игры Эш против соперников того же склада.
        const int DravenId = 119, XerathId = 101;
        static PerkVsFoe Foe(int g, int w, params (int Perk, int G, int W)[] perks) =>
            new(g, w, perks.ToDictionary(x => x.Perk, x => (x.G, x.W)));
        var bp = RuneAdvisor.BonePlating;
        var against = Adapt(engine, Ashe_([8444, Overgrowth], perkVs: new()
        {
            [DravenId] = Foe(10000, 5000, (8444, 6000, 3300), (bp, 4000, 2000)),
        }), Soft, lane: DravenId);
        Check("цифры против: со Вторым дыханием против наскока выигрывают на 5 п.п. чаще (10 000 игр) — оставили, причина видна",
              against is not null && against.Swaps.All(s => s.To != bp)
              && against.Kept.Any(k => k.From == bp && k.To == 8444 && k.Data.Contains(RuneIcons.NameOf(bp))),
              $"{Show(against)} | оставили: {string.Join("; ", against?.Kept.Select(k => k.Why + " · " + k.Data) ?? [])}");
        var forIt = Adapt(engine, Ashe_([8444, Overgrowth], perkVs: new()
        {
            [DravenId] = Foe(10000, 5200, (8444, 6000, 3000), (bp, 4000, 2200)),
        }), Soft, lane: DravenId);
        Check("цифры за: с Костяной броней на 5 п.п. чаще — ставим, в причине цифры и с чем сравнивали",
              forIt?.Swaps.Any(s => s.To == bp && s.Data.Contains("5,0%") && !s.Data.Contains("п.п.")
                                    && s.Data.Contains(RuneIcons.NameOf(8444))) == true
              && forIt.Kept.Count == 0, Show(forIt));
        var fewGames = Adapt(engine, Ashe_([8444, Overgrowth], perkVs: new()
        {
            [DravenId] = Foe(200, 100, (8444, 100, 55), (bp, 100, 45)),
        }), Soft, lane: DravenId);
        Check("игр мало (100 и 100, −10 п.п.) — это шум: ставим по правилу, цифр в причине нет",
              fewGames?.Swaps.Any(s => s.To == bp && s.Data.Length == 0) == true, Show(fewGames));
        var otherKind = Adapt(engine, Ashe_([8444, Overgrowth], perkVs: new()
        {
            [XerathId] = Foe(10000, 5000, (8444, 6000, 3300), (bp, 4000, 2000)),
        }), Soft, lane: DravenId);
        Check("цифры только против поука (Ксерат) за наскок (Дрейвен) не решают — по правилу",
              otherKind?.Swaps.Any(s => s.To == bp) == true, Show(otherKind));
        // Состав без поука (у Soft он есть, и Второе дыхание по составу
        // само тянет Решимость — это правило по составу, сверять его не с чем).
        int[] Brawl = [DravenId, 238, 55, 122, 91];   // Дрейвен, Зед, Катарина, Дариус, Талон
        var treeSwitch = Adapt(engine, Ashe_([8345, 8347], sub: 8300, perkVs: new()
        {
            [DravenId] = Foe(20000, 10400, (bp, 3000, 1350), (8345, 12000, 6300), (8347, 15000, 7800)),
        }), Brawl, lane: DravenId);
        Check("дерево сменилось бы ради Костяной брони, а с ней против наскока выигрывают реже, чем без неё, — дерево остаётся",
              treeSwitch is not null && treeSwitch.Choice.Page.Sub == 8300 && !treeSwitch.Choice.Page.Secondary.Contains(bp)
              && treeSwitch.Kept.Any(k => k.From == bp && k.To == bp),
              $"{Show(treeSwitch)} | оставили: {string.Join("; ", treeSwitch?.Kept.Select(k => k.Why + " · " + k.Data) ?? [])}");

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
        // Владелец (20.11, скриншоты подсказок рун и сборки): «оформи так же
        // красиво описания, почему были взяты те или иные руны» — как у
        // предметов: руна иконкой и названием, под ней почему и вместо чего.
        var cut = ai.Vm?.WhyItems.FirstOrDefault(x => x.Name == RuneIcons.NameOf(RuneAdvisor.CutDown));
        var instead = Loc.T("build.whyInstead", "").Trim().TrimEnd(':');
        Check("подсказка как у сборки: заголовок, Резня с иконкой, причиной и «вместо чего», без простого текста",
              ai.Vm?.WhyHead == Loc.T("runes.aiTipHead") && ai.Vm.Tip.Length == 0
              && cut is { Icon: not null } && cut.Text.Contains(instead)
              && cut.NameBrush is SolidColorBrush cb && cb.Color == System.Windows.Media.Color.FromRgb(0x36, 0xD6, 0xE7),
              $"{ai.Vm?.WhyHead} | " + string.Join(" / ", ai.Vm?.WhyItems.Select(x => $"{x.Name}: {x.Text.Replace("\n", " · ")}") ?? []));
        var page = Call(w, "PageAt", sel) as RunePage;
        Check("подбор выбран сам, «Применить» отдаёт его страницу",
              sel == 2 && page is not null && page.Perks.Contains(RuneAdvisor.CutDown) && page.Shards[2] == RuneAdvisor.ShardTenacity,
              $"выбрано {sel}, страница {(page is null ? "—" : string.Join(",", page.Perks) + " | " + string.Join(",", page.Shards))}");
        if (snap is { Length: > 0 })
        {
            Snap(w, snap, "runes-ai-2-ready.png");
            SnapRuneTip(w, snap, "runes-ai-3-tip.png");
        }

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

    /// Подсказка карточки подбора — снимком. Саму всплывашку не открываем (это
    /// окно поверх экрана): рисуем её ToolTip отдельно, с данными карточки
    /// (как SnapTip в tests/AiBuildRow).
    private static void SnapRuneTip(Window w, string dir, string file)
    {
        Border? card = null;
        void Walk(DependencyObject d)
        {
            if (d is Border { ToolTip: System.Windows.Controls.ToolTip, DataContext: OverlayWindow.RuneOptionVm { IsAi: true } } b) card ??= b;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk((DependencyObject)w.FindName("RuneGrid"));
        if (card is null) return;
        var tip = (System.Windows.Controls.ToolTip)card.ToolTip;
        tip.DataContext = card.DataContext;
        if (System.Windows.Application.Current.TryFindResource(typeof(System.Windows.Controls.ToolTip)) is Style st) tip.Style = st;
        tip.ApplyTemplate();
        tip.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        tip.Arrange(new Rect(tip.DesiredSize));
        tip.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tip);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        Directory.CreateDirectory(dir);
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок подсказки: {Path.Combine(dir, file)}");
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
