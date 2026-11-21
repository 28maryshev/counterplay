using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Прогон подбора рун по всем файлам выгрузки — отдельный режим проверки
/// (CP_SWEEP=папка с файлами связок, CP_SWEEP_OUT=файл отчёта). Владелец
/// (21.11): «проводил ещё тесты, чтобы посмотреть руны остальных на других
/// линиях?» — до этого подбор целиком смотрели у четырёх чемпионов.
///
/// Каждая связка — против трёх самых частых соперников по линии (на боте ещё и
/// самый частый второй бот-лейнер) и четырёх составов врагов: поук, танки с
/// контролем, ныряльщики, смешанный. Отчёт по линиям: сколько раз кейстоун не
/// тот, что в первом варианте, и на скольких играх; какие руны ставятся и
/// вместо чего; смена вторичного дерева, осколки; что цифры не подтвердили. И
/// странности: недопустимая страница, руна реже 3% на чемпионе, подбора нет,
/// падение.
/// </summary>
internal static class Sweep
{
    private static readonly (string Name, int[] Ids)[] Comps =
    [
        ("поук", [101, 81, 99, 126, 110]),          // Ксерат, Эзреаль, Люкс, Джейс, Варус
        ("танки и контроль", [54, 113, 89, 516, 32]), // Малфит, Седжуани, Леона, Орн, Амуму
        ("ныряльщики", [238, 121, 91, 107, 131]),    // Зед, Ка'Зикс, Талон, Ренгар, Диана
        ("смешанный", [122, 64, 103, 222, 412]),      // Дариус, Ли Син, Ари, Джинкс, Треш
    ];

    public static void Run(RecommendationEngine engine, string dir, string outPath)
    {
        var sb = new StringBuilder();
        var byRole = new SortedDictionary<string, Tally>();
        var odd = new List<string>();
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f).ToList();
        foreach (var f in files)
        {
            ChampStats s;
            try { s = RunesClient.Parse(File.ReadAllText(f)); }
            catch (Exception e) { odd.Add($"{Path.GetFileName(f)}: не читается — {e.Message}"); continue; }
            var t = byRole.TryGetValue(s.Role, out var x) ? x : byRole[s.Role] = new Tally();
            var bot = s.Role is "adc" or "support";
            var opps = s.Vs.OrderByDescending(kv => kv.Value.Games).Take(3).Select(kv => kv.Key).ToList();
            if (opps.Count == 0) opps.Add(0);
            int? duo = bot ? s.VsDuo.OrderByDescending(kv => kv.Value.Games).Select(kv => (int?)kv.Key).FirstOrDefault() : null;
            var variant1 = RunesClient.Choices(s, null)[0].Keystone;
            foreach (var opp in opps)
                foreach (var (comp, ids) in Comps)
                {
                    var lane = new List<int>();
                    if (opp != 0) lane.Add(opp);
                    if (duo is { } d && d != opp) lane.Add(d);
                    var enemies = lane.Concat(ids.Where(i => !lane.Contains(i) && i != s.ChampionId)).Take(5).ToArray();
                    var foes = s.Role == "jungle" ? [] : lane.ToArray();
                    RuneAdvisor.Advice? a;
                    try
                    {
                        a = RuneAdvisor.Adapt(s, s.ChampionId, enemies, opp == 0 ? null : opp, foes,
                                              id => engine.DamageShare(id), RuneIcons.SlotOf);
                    }
                    catch (Exception e)
                    {
                        odd.Add($"{s.ChampionId}-{s.Role} против {opp} ({comp}): падение — {e.GetType().Name}: {e.Message}");
                        continue;
                    }
                    t.Runs++;
                    if (a is null) { t.NoAdvice++; continue; }
                    var page = a.Choice.Page;
                    if (!Legal(page)) odd.Add($"{Name(s)} против {DataDragon.Name(opp)} ({comp}): недопустимая страница {string.Join(",", page.Perks)}|{string.Join(",", page.Secondary)}");
                    if (a.Choice.Keystone != variant1)
                    {
                        t.OtherKeystone++;
                        t.Keystones[RuneIcons.NameOf(a.Choice.Keystone)] = t.Keystones.GetValueOrDefault(RuneIcons.NameOf(a.Choice.Keystone)) + 1;
                    }
                    if (page.Sub != RunesClient.Choices(s, opp == 0 ? null : opp, int.MaxValue, duo)[0].Page.Sub) t.TreeSwitch++;
                    foreach (var w in a.Swaps)
                    {
                        if (w.Shard) { t.Shards[w.To == RuneAdvisor.ShardHealth ? "здоровье" : "стойкость"] = t.Shards.GetValueOrDefault(w.To == RuneAdvisor.ShardHealth ? "здоровье" : "стойкость") + 1; continue; }
                        var key = $"{RuneIcons.NameOf(w.From)} → {RuneIcons.NameOf(w.To)}";
                        t.Swaps[key] = t.Swaps.GetValueOrDefault(key) + 1;
                        // Руна, которую на чемпионе почти не берут, — в основное дерево
                        // по долям кейстоуна, во вторичное — по долям дерева.
                        var share = a.Choice.Share?.GetValueOrDefault(w.To)
                                    ?? 100;
                        var subShare = a.Choice.SubShare?.GetValueOrDefault(page.Sub)?.GetValueOrDefault(w.To);
                        var taken = RuneIcons.SlotOf(w.To)?.Style == page.Primary ? share : subShare ?? 100;
                        if (taken < RuneAdvisor.MinShare)
                            odd.Add($"{Name(s)} против {DataDragon.Name(opp)} ({comp}): поставлена {RuneIcons.NameOf(w.To)} — на чемпионе {taken:0.#}%");
                    }
                    t.Kept += a.Kept.Count;
                    t.Changed += a.Swaps.Count > 0 ? 1 : 0;
                }
        }

        sb.AppendLine($"Файлов {files.Count}; составов {Comps.Length}; соперников на связку до 3 (на боте ещё самый частый второй бот-лейнер).");
        foreach (var (role, t) in byRole)
        {
            sb.AppendLine();
            sb.AppendLine($"── {role}: прогонов {t.Runs}, подбора нет {t.NoAdvice}, страница поменялась {t.Changed} ({Pct(t.Changed, t.Runs)}), "
                          + $"кейстоун не как в первом варианте {t.OtherKeystone} ({Pct(t.OtherKeystone, t.Runs)}), "
                          + $"вторичное дерево сменилось {t.TreeSwitch} ({Pct(t.TreeSwitch, t.Runs)}), цифры не подтвердили правило {t.Kept}");
            if (t.Keystones.Count > 0) sb.AppendLine("   другой кейстоун: " + string.Join(", ", t.Keystones.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key} {k.Value}")));
            sb.AppendLine("   замены: " + string.Join("; ", t.Swaps.OrderByDescending(k => k.Value).Take(12).Select(k => $"{k.Key} {k.Value}")));
            if (t.Shards.Count > 0) sb.AppendLine("   осколок: " + string.Join(", ", t.Shards.Select(k => $"{k.Key} {k.Value}")));
        }
        sb.AppendLine();
        sb.AppendLine($"СТРАННОСТИ: {(odd.Count == 0 ? "нет" : odd.Count.ToString())}");
        foreach (var o in odd.Take(40)) sb.AppendLine("  " + o);
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"  прогон по файлам: {outPath}");
    }

    private sealed class Tally
    {
        public int Runs, NoAdvice, Changed, OtherKeystone, TreeSwitch, Kept;
        public Dictionary<string, int> Swaps = [], Shards = [], Keystones = [];
    }

    private static string Name(ChampStats s) => $"{DataDragon.Name(s.ChampionId)} ({s.Role})";
    private static string Pct(int a, int b) => b == 0 ? "—" : $"{100.0 * a / b:0}%";

    /// Каждая руна страницы — в своём ряду своего дерева, ряды вторичного разные.
    private static bool Legal(RunePage p)
    {
        var prim = p.Perks.Select(RuneIcons.SlotOf).ToList();
        var sec = p.Secondary.Select(RuneIcons.SlotOf).ToList();
        return prim.Count == 4 && prim.Select((s, i) => s is { } v && v.Style == p.Primary && v.Row == i).All(x => x)
            && sec.Count == 2 && sec.All(s => s is { } v && v.Style == p.Sub && v.Row > 0)
            && sec[0]!.Value.Row != sec[1]!.Value.Row;
    }
}
