using System.IO;
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
        Check("у предметов ходовой сборки сказано, что они закрывают",
              build.Items.All(advice.BaseWhy.ContainsKey)
              && advice.BaseWhy[Mercs].Answers.Contains(Loc.T("build.vsMagic", "57"))
              && advice.BaseWhy.Values.All(w => !w.Added),
              string.Join(" | ", advice.BaseWhy.Select(kv => $"{Name(kv.Key)}: {string.Join("; ", kv.Value.Answers)}")));
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
