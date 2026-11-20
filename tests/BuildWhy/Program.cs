using System.IO;
using System.Reflection;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Почему предмет в сборке под состав (владелец, 20.11: «в итемах при
/// наведении должен объяснять, почему выбрал именно эти итемы»).
///
/// 1. Подпись замены не путает тип урона. Состав бьёт магией на 57% (физ.
///    43%): броня из ходовой сборки против него бесполезна, её меняют — и до
///    20.11 причиной писали «против физ. урона (43%)»: порог замены 55%, а
///    подпись выбиралась по порогу 60%.
/// 2. У каждого предмета подбора своя причина: поставленный — на что отвечает
///    и вместо чего встал (у сменившего броню — «броня, а они бьют магией»);
///    оставленный из ходовой — что из состава он уже закрывает.
/// 3. Для случая «менять нечего» — те же причины у предметов ходовой сборки.
/// 4. Скриншот владельца 20.11: у саппорта Мандат императора под «против маг.
///    урона» — он встал на место брони как лучший из остальных, магзащиту
///    чемпион не собирает. Теперь так и сказано (NoFit), а на место брони
///    сначала ищется магзащита.
/// 5. Две задачи, отвечаемые обувью (контроль и броня): вторая не перебивает
///    первую. И при любом составе: каждую причину строки закрывает предмет,
///    никто не стоит «вместо себя», поставленное не пропадает.
///
/// Гарен с выдуманной ходовой сборкой и выдуманными винрейтами предметов;
/// состав врагов — пять магов, доли урона подставлены. Справочники Data
/// Dragon — копия кэша игрока во временной папке.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Garen = 86;
    private static readonly int[] Mages = [1, 63, 115, 101, 161];   // Энни, Брэнд, Зиггс, Ксерат, Вел'Коз

    // Предметы (id Data Dragon).
    private const int Stridebreaker = 6631, Mercs = 3111, Cleaver = 3071, Sterak = 3053,
                      Thornmail = 3075, DeadMans = 3742, Visage = 3065, ForceOfNature = 4401,
                      Maw = 3156, EdgeOfNight = 3814, Randuin = 3143, Steelcaps = 3047;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "BuildWhy");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        try
        {
            Assets(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay"));
            if (!ItemFacts.Loaded) { Console.WriteLine("справочника предметов нет — проверять нечем"); return Done(); }
            MagicComp();
            NoResistSupport();
            CcComp();
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
        Console.WriteLine(_fails == 0 ? "ИТОГ: у каждого предмета подбора своя причина, тип урона не путается"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Пять магов, магия 57%: броня в ходовой сборке бесполезна.
    private static void MagicComp()
    {
        Console.WriteLine("пять магов, магический урон 57%:");
        var thorn = ItemFacts.Of(Thornmail);
        var mercs = ItemFacts.Of(Mercs);
        Check("в справочнике есть Шипованная броня (броня без магзащиты) и Ртутные (ботинки с магзащитой)",
              thorn is { Armor: >= 30, MagicResist: < 20 } && mercs is { Boots: true, MagicResist: >= 15 },
              $"{thorn?.Name}: броня {thorn?.Armor}, магзащита {thorn?.MagicResist}; "
              + $"{mercs?.Name}: магзащита {mercs?.MagicResist}");

        var build = new BuildData(
            Items: [Stridebreaker, Mercs, Cleaver, Sterak, Thornmail, DeadMans],
            Core: [Stridebreaker, Mercs, Cleaver], Games: 6300, Winrate: 52.4, Spells: [4, 14]);
        var items = new List<ItemStat>
        {
            new(Stridebreaker, 6000, 52.0), new(Mercs, 4000, 51.5), new(Cleaver, 3500, 52.5),
            new(Sterak, 3000, 53.0), new(Thornmail, 1500, 51.0), new(DeadMans, 1400, 50.8),
            new(Visage, 1200, 53.4), new(ForceOfNature, 900, 52.8), new(Maw, 700, 52.1),
            new(EdgeOfNight, 500, 51.2), new(Randuin, 600, 50.5), new(Steelcaps, 2500, 51.0),
        };
        var stats = new ChampStats(Garen, "top", "16.22", 9000, [],
                                   new Dictionary<int, (int, Dictionary<int, double>)>(), [build], items);

        (double, double, double)? Share(int id) => id == Garen ? (1.0, 0.0, 0.0) : (0.43, 0.57, 0.0);
        var advice = BuildAdvisor.Adapt(stats, build, Mages, [Garen], Share);
        var a = advice.Builds.FirstOrDefault();
        Check("подбор есть", a is not null, $"вариантов {advice.Builds.Count}");
        if (a is null) return;
        Console.WriteLine($"  сборка: {string.Join(", ", a.Items.Select(Name))}");
        Console.WriteLine($"  заменено: {string.Join(", ", a.Changed.Select(Name))}");
        Console.WriteLine($"  причины: {string.Join(" | ", a.Reasons)}");

        // ── 1. Тип урона в подписи ─────────────────────────────────────────
        var wrong = Loc.T("build.vsPhys", "43");
        Check("броня заменена — она бесполезна против магов",
              !a.Items.Contains(Thornmail) && !a.Items.Contains(DeadMans), "");
        Check("в причинах нет «против физ. урона (43%)» — физического урона меньше половины",
              !a.Reasons.Contains(wrong), string.Join(" | ", a.Reasons));
        Check("а есть «против маг. урона (57%)»",
              a.Reasons.Contains(Loc.T("build.vsMagic", "57")), string.Join(" | ", a.Reasons));

        // ── 2. Причина у каждого предмета ──────────────────────────────────
        foreach (var id in a.Items)
        {
            var w = a.Why.GetValueOrDefault(id);
            Console.WriteLine($"    {Name(id)}: " + (w is null ? "—" :
                $"{(w.Added ? "поставлен" : "из ходовой")}; отвечает: [{string.Join("; ", w.Answers)}]"
                + (w.Replaced != 0 ? $"; вместо {Name(w.Replaced)} ({w.ReplacedWhy})" : "")
                + (w.RunnerUp != 0 ? $"; запасной {Name(w.RunnerUp)}" : "")));
        }
        Check("у каждого из шести предметов есть причина",
              a.Items.All(a.Why.ContainsKey), $"{a.Items.Count(a.Why.ContainsKey)} из {a.Items.Count}");
        Check("заменённые — «поставлены», и сказано, вместо чего; остальные — «из ходовой»",
              a.Changed.All(id => a.Why.GetValueOrDefault(id) is { Added: true, Replaced: not 0 })
              && a.Items.Except(a.Changed).All(id => a.Why.GetValueOrDefault(id) is { Added: false }), "");
        var armor = Loc.T("build.wastedArmor", "57");
        var instead = a.Changed.Select(id => a.Why[id])
            .Where(w => w.Replaced is Thornmail or DeadMans).ToList();
        Check("вставшие на место брони говорят: «броня, а они бьют магией (57%)»",
              instead.Count == 2 && instead.All(w => w.ReplacedWhy == armor),
              string.Join(" | ", instead.Select(w => $"{Name(w.Replaced)}: {w.ReplacedWhy}")));
        Check("Ртутные остались и отвечают на магический урон",
              a.Why.GetValueOrDefault(Mercs) is { Added: false } m
              && m.Answers.Contains(Loc.T("build.vsMagic", "57")), "");

        // ── 3. Менять нечего — у ходовой те же причины ─────────────────────
        // Строка подбора тогда — сама ходовая сборка, и её предметам подсказку
        // берут из BaseWhy: что из состава каждый уже закрывает.
        Honest(a);
        Check("у предметов ходовой сборки сказано, что они закрывают",
              build.Items.All(advice.BaseWhy.ContainsKey)
              && advice.BaseWhy[Mercs].Answers.Contains(Loc.T("build.vsMagic", "57"))
              && advice.BaseWhy.Values.All(w => !w.Added),
              string.Join(" | ", advice.BaseWhy.Select(kv => $"{Name(kv.Key)}: {string.Join("; ", kv.Value.Answers)}")));
    }

    /// Скриншот владельца 20.11: у саппорта подбор поставил Мандат императора, а
    /// подсказка строки говорила «против маг. урона (73%)». Мандат магзащиты не
    /// даёт: он встал на место брони, бесполезной против магов, как лучший из
    /// остальных предметов, — магзащиту этот чемпион не собирает. Подпись
    /// приписывала ему чужую задачу.
    private static void NoResistSupport()
    {
        const int Sona = 37, Ionian = 3158, Moonstone = 6617, Mandate = 4005, Ardent = 3504,
                  Flowing = 6616, Redemption = 3107;
        Console.WriteLine();
        Console.WriteLine("Сона против пяти магов (73%), магзащиту не собирает:");
        var pool = new[] { Ionian, Moonstone, Mandate, Ardent, Flowing, Redemption };
        Check("в справочнике есть Мандат, и ни один её предмет кроме брони не даёт магзащиты",
              ItemFacts.Of(Mandate) is not null
              && pool.All(id => ItemFacts.Of(id) is { MagicResist: < 30 } f && !(f.Boots && f.MagicResist >= 15)),
              string.Join(", ", pool.Select(id => $"{Name(id)}: {ItemFacts.Of(id)?.MagicResist}")));

        var build = new BuildData(
            Items: [Ionian, Moonstone, Ardent, Flowing, Redemption, Thornmail],
            Core: [Moonstone, Ardent], Games: 2600, Winrate: 51.9, Spells: [4, 3]);
        var items = new List<ItemStat>
        {
            new(Ionian, 2500, 51.5), new(Moonstone, 2400, 51.8), new(Ardent, 1800, 52.0),
            new(Flowing, 1500, 52.1), new(Redemption, 1200, 51.0), new(Thornmail, 300, 50.2),
            new(Mandate, 1400, 53.2),
        };
        var stats = new ChampStats(Sona, "support", "16.22", 3000, [],
                                   new Dictionary<int, (int, Dictionary<int, double>)>(), [build], items);
        (double, double, double)? Share(int id) => id == Sona ? (0.1, 0.9, 0.0) : (0.27, 0.73, 0.0);
        var a = BuildAdvisor.Adapt(stats, build, Mages, [Sona], Share).Builds.FirstOrDefault();
        Check("подбор есть, и Мандат встал на место брони",
              a is not null && a.Items.Contains(Mandate) && !a.Items.Contains(Thornmail),
              a is null ? "" : string.Join(", ", a.Items.Select(Name)));
        if (a is null) return;
        Console.WriteLine($"  причины строки: {string.Join(" | ", a.Reasons)}");
        Honest(a);

        Check("строка не приписывает Мандату ответ на магический урон",
              !a.Reasons.Contains(Loc.T("build.vsMagic", "73")), string.Join(" | ", a.Reasons));
        var w = a.Why.GetValueOrDefault(Mandate);
        Check("у Мандата сказано: вместо брони, магзащиту не собирают — взят лучший из остальных",
              w is { Added: true, Replaced: Thornmail, Answers.Count: 0 }
              && w.NoFit == Loc.T("build.noFitMr")
              && w.ReplacedWhy == Loc.T("build.wastedArmor", "73"),
              w is null ? "нет причины" : $"заменил {Name(w.Replaced)} ({w.ReplacedWhy}), нечем ответить: «{w.NoFit}», отвечает: [{string.Join("; ", w.Answers)}]");

        // Что увидит игрок, наведя на Мандат: тот же метод, что рисует подсказку.
        var text = (string)typeof(OverlayWindow)
            .GetMethod("AiItemWhy", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [Mandate, w, false, stats])!;
        Console.WriteLine("  подсказка Мандата:\n    " + text.Replace("\n", "\n    "));
        Check("в подсказке Мандата: вместо брони, магзащиту не собирают, и ни слова «против маг. урона»",
              text.Contains(Capitalize(Loc.T("build.noFitMr"))) && text.Contains(ItemIcons.NameOf(Thornmail))
              && !text.Contains(Loc.T("build.vsMagic", "73")), "");
    }

    private static string Capitalize(string s) => s.Length > 0 ? char.ToUpper(s[0]) + s[1..] : s;

    /// Гарен против трёх танков с долгим контролем и физическим уроном: задач
    /// две, и обе отвечаются обувью — Ртутные (контроль) и Бронированные сапоги
    /// (броня). До 20.11 вторая задача ставила сапоги поверх Ртутных: ответ на
    /// контроль пропадал, а «много контроля» оставалось в причинах.
    private static void CcComp()
    {
        const int Berserker = 3006, Mercs = 3111, Shojin = 3161;
        Console.WriteLine();
        Console.WriteLine("Гарен против контроля и физического урона (70%):");
        int[] foes = [89, 111, 54, 122, 119];   // Леона, Наутилус, Мальфит, Дариус, Дрейвен
        var build = new BuildData(
            Items: [Stridebreaker, Berserker, Cleaver, Shojin, Visage, EdgeOfNight],
            Core: [Stridebreaker, Berserker, Cleaver], Games: 6300, Winrate: 52.4, Spells: [4, 14]);
        var items = new List<ItemStat>
        {
            new(Stridebreaker, 6000, 52.0), new(Berserker, 5000, 51.0), new(Cleaver, 3500, 52.5),
            new(Shojin, 3000, 53.0), new(Visage, 1200, 51.4), new(EdgeOfNight, 500, 51.2),
            new(Mercs, 3000, 51.8), new(Steelcaps, 2500, 53.5),
            new(Randuin, 600, 51.0), new(Thornmail, 900, 51.5),
        };
        var stats = new ChampStats(Garen, "top", "16.22", 9000, [],
                                   new Dictionary<int, (int, Dictionary<int, double>)>(), [build], items);
        (double, double, double)? Share(int id) => id == Garen ? (1.0, 0.0, 0.0) : (0.7, 0.3, 0.0);
        // Стерак в 16.23 сам даёт стойкость — в ходовой его нет, иначе задача
        // контроля закрыта заранее и обуви делить нечего.
        Check("стойкость в ходовой не куплена, а у Ртутных она есть",
              build.Items.All(id => ItemFacts.Of(id) is not { Tenacity: true })
              && ItemFacts.Of(Mercs) is { Tenacity: true, Boots: true },
              string.Join(", ", build.Items.Where(id => ItemFacts.Of(id) is { Tenacity: true }).Select(Name)));
        var a = BuildAdvisor.Adapt(stats, build, foes, [Garen], Share).Builds.FirstOrDefault();
        Check("подбор есть", a is not null, "");
        if (a is null) return;
        Console.WriteLine($"  сборка: {string.Join(", ", a.Items.Select(Name))}");
        Console.WriteLine($"  причины строки: {string.Join(" | ", a.Reasons)}");
        foreach (var id in a.Items)
            Console.WriteLine($"    {Name(id)}: отвечает [{string.Join("; ", a.Why.GetValueOrDefault(id)?.Answers ?? [])}]");
        Honest(a);
        var boots = a.Items.Where(id => ItemFacts.Of(id) is { Boots: true }).ToList();
        Check("обувь одна, и это Ртутные — ответ на контроль не перебит сапогами под броню",
              boots.Count == 1 && boots[0] == Mercs, string.Join(", ", boots.Select(Name)));
    }

    /// Законы, которые держат подсказку честной при любом составе.
    private static void Honest(BuildAdvisor.Adapted a)
    {
        var answered = a.Items.SelectMany(id => a.Why.GetValueOrDefault(id)?.Answers ?? []).ToHashSet();
        var orphan = a.Reasons.Where(r => !answered.Contains(r)).ToList();
        Check("каждую причину строки закрывает предмет сборки — нет задачи «на словах»",
              orphan.Count == 0, orphan.Count == 0 ? "" : "без предмета: " + string.Join(" | ", orphan));
        var self = a.Items.Where(id => a.Why.GetValueOrDefault(id)?.Replaced == id).ToList();
        Check("ни один предмет не стоит «вместо себя»",
              self.Count == 0, string.Join(", ", self.Select(Name)));
        var lost = a.Changed.Where(id => !a.Items.Contains(id)).ToList();
        Check("всё, что подбор поставил, в сборке и осталось",
              lost.Count == 0, lost.Count == 0 ? "" : "пропали: " + string.Join(", ", lost.Select(Name)));
    }

    private static string Name(int id) => ItemFacts.Of(id)?.Name ?? id.ToString();

    /// Имена, классы чемпионов и предметы. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "items" })
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
        ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemFacts.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
