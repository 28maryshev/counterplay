using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Блок «стартовые» в наборе предметов, который программа кладёт в магазин игры.
///
/// Владелец 07.11, со снимком набора Эзреаля (Клинок Дорана и зелье): «все
/// сборки должны содержать все стартовые предметы — Лук, Щит Дорана, бутылки
/// хп, фласки — в верхней части». Блок собирал пару «Доран по роли + зелье»,
/// а Лука и Шлема Дорана, появившихся позже, программа не знала вовсе.
///
/// Меряет:
/// - в каждой роли в блоке все стартовые предметы, без повторов; ходовые для
///   роли первыми, за ними оба зелья;
/// - список сверен с нынешним Data Dragon: каждый предмет продаётся на Ущелье,
///   и нет стартового предмета, которого в списке нет — Riot добавит нового
///   Дорана, проверка его назовёт;
/// - отсев по патчу (<c>ItemIcons.InPatch</c>) настоящих предметов не трогает.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static;

    // Длинный меч и Тотем контроля носят метку Lane, но в блок стартовых не
    // идут: меч — компонент сборки, тотем — покупка с базы.
    private static readonly HashSet<int> NotStarters = [1036, 2055];

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        // Data Dragon кладёт справочник на диск — только в свою папку.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-starter-items");
        AppPaths.RootOverride = root;

        var t = typeof(RunesImporter);
        var all = (int[])t.GetField("AllStarters", Hidden)!.GetValue(null)!;
        var potions = (int[])t.GetField("Potions", Hidden)!.GetValue(null)!;
        int[] Start(string role) => (int[])t.GetMethod("StartItems", Hidden)!.Invoke(null, [role])!;
        int[] Own(string role)   => (int[])t.GetMethod("RoleStarters", Hidden)!.Invoke(null, [role])!;

        // ── Каждая роль: всё стартовое, своё первым ────────────────────────
        Console.WriteLine("без сети (названия предметов не загружены):");
        CheckRoles(all, potions, Start, Own);

        // ── Список против нынешнего Data Dragon ────────────────────────────
        Console.WriteLine();
        Dictionary<int, JsonElement> items;
        string version;
        try { (version, items) = await LoadItemsAsync(); }
        catch (Exception ex)
        {
            Check("Data Dragon ответил", false, ex.Message);
            return Finish();
        }
        Console.WriteLine($"Data Dragon {version}, предметов {items.Count}:");

        foreach (var id in all)
        {
            var ok = items.TryGetValue(id, out var it) && Purchasable(it);
            Check($"{id} {(ok ? Name(it) : "")} продаётся на Ущелье", ok,
                  ok ? $"{Gold(it)} золота" : "нет в патче или не продаётся");
        }

        var ourNames = all.Where(items.ContainsKey).Select(id => Name(items[id])).ToHashSet();
        var missed = items
            .Where(kv => !all.Contains(kv.Key) && !NotStarters.Contains(kv.Key)
                         && LooksLikeStarter(kv.Value)
                         && !ourNames.Contains(Name(kv.Value)))   // 1105–1107 — копии питомцев
            .Select(kv => $"{kv.Key} {Name(kv.Value)}")
            .ToList();
        Check("в Data Dragon нет стартовых предметов мимо списка", missed.Count == 0,
              missed.Count == 0 ? "все в списке" : "добавить в RunesImporter.AllStarters: " + string.Join(", ", missed));

        // ── Отсев по патчу с загруженными названиями ───────────────────────
        Console.WriteLine();
        Console.WriteLine("названия предметов загружены:");
        await DataDragon.LoadAsync("en_US", CancellationToken.None);
        await ItemIcons.LoadNamesAsync("en_US", CancellationToken.None);
        Check("названия загрузились", ItemIcons.NamesLoaded, DataDragon.Version);
        Check("несуществующий предмет отсеивается", !ItemIcons.InPatch(999_999), "999999");
        CheckRoles(all, potions, Start, Own);

        try { Directory.Delete(root, recursive: true); } catch { }
        return Finish();
    }

    private static void CheckRoles(int[] all, int[] potions,
                                   Func<string, int[]> start, Func<string, int[]> own)
    {
        foreach (var (role, first) in new (string, int)[]
                 { ("adc", 1055), ("mid", 1056), ("top", 1054), ("support", 3865), ("jungle", 1101), ("", 2003) })
        {
            var list = start(role);
            var mine = own(role);
            var name = role == "" ? "роль неизвестна" : role;
            var lacking = all.Except(list).ToList();
            Check($"{name}: все стартовые ({all.Length})", lacking.Count == 0,
                  lacking.Count == 0 ? $"{list.Length} в блоке" : "нет: " + string.Join(", ", lacking));
            Check($"{name}: без повторов", list.Distinct().Count() == list.Length, string.Join(" ", list));
            Check($"{name}: первым {first}", list.FirstOrDefault() == first, $"первым {list.FirstOrDefault()}");
            var head = mine.Concat(potions).ToArray();
            Check($"{name}: ходовые для роли, за ними зелья", list.Take(head.Length).SequenceEqual(head),
                  string.Join(" ", list.Take(head.Length)));
        }
    }

    /// Признаки стартового предмета в Data Dragon: метка Lane или Jungle (по ним
    /// магазин собирает раздел START), продаётся на Ущелье, ни из чего не
    /// собирается, не тотем, не для одного чемпиона, до 500 золота.
    private static bool LooksLikeStarter(JsonElement it)
    {
        var tags = it.TryGetProperty("tags", out var tg)
            ? tg.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
        return (tags.Contains("Lane") || tags.Contains("Jungle"))
            && !tags.Contains("Trinket")
            && Purchasable(it)
            && !it.TryGetProperty("from", out _)
            && !it.TryGetProperty("requiredChampion", out _)
            && Gold(it) is > 0 and <= 500;
    }

    private static bool Purchasable(JsonElement it) =>
        it.GetProperty("gold").GetProperty("purchasable").GetBoolean()
        && it.TryGetProperty("maps", out var m) && m.TryGetProperty("11", out var sr) && sr.GetBoolean();

    private static int Gold(JsonElement it) => it.GetProperty("gold").GetProperty("total").GetInt32();
    private static string Name(JsonElement it) => it.GetProperty("name").GetString() ?? "";

    private static async Task<(string, Dictionary<int, JsonElement>)> LoadItemsAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var v = JsonDocument.Parse(await http.GetStringAsync("https://ddragon.leagueoflegends.com/api/versions.json"));
        var version = v.RootElement[0].GetString()!;
        var doc = JsonDocument.Parse(await http.GetStringAsync(
            $"https://ddragon.leagueoflegends.com/cdn/{version}/data/en_US/item.json"));
        var items = new Dictionary<int, JsonElement>();
        foreach (var p in doc.RootElement.GetProperty("data").EnumerateObject())
            if (int.TryParse(p.Name, out var id)) items[id] = p.Value;
        return (version, items);
    }

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: в каждой роли все стартовые предметы патча, своё первым"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
