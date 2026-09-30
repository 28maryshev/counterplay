using System.Text;
using Counterplay;
using Microsoft.Data.Sqlite;

namespace Counterplay.Tests;

/// <summary>
/// Иконки под портретами: у врага — контры, у союзника — синергия.
///
/// Владелец: «их всегда должно быть только 3, и работать они должны от твоей
/// роли у союзников, а общие контры у врагов — относительно их роли, которую
/// они взяли, а не от всех чемпионов».
///
/// Под Векс на миде стояла ОДНА иконка. Причина была не в нехватке данных:
/// парные таблицы не знают, кто роль правда играет, и в кандидаты лезли
/// Фиддлстикс-мид на восемнадцати играх и Сивир-мид на пятнадцати. Строгий
/// порог такие отсекал — и вместе с ними всё остальное.
///
/// Теперь кандидаты берутся те же, что и в самом подборе (порог игр И доля
/// роли), и до трёх список добирается: сперва настоящие победные матчапы,
/// потом ровные. Проигрышный третьей иконкой не ставится — это была бы не
/// контра.
///
/// Считаем на живой базе игрока. Нет базы — проверять нечего, выходим молча.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Своя планка доли роли — заведомо мягче кодовой (0,5%). Проверка должна
    /// ловить «чемпион роль не играет», а не повторять число из движка.
    private const double SANE_SHARE = 0.004;
    private const int    SANE_GAMES = 120;

    private static readonly string[] Roles = ["top", "jungle", "mid", "adc", "support"];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        DataDragon.LoadFromCache("ru_RU");

        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is null) { Console.WriteLine("базы нет — проверять нечего"); return 0; }

        using var engine = RecommendationEngine.Create(dbPath, "emerald");
        var patches = engine.PatchDisplay.Split(',').Select(s => s.Trim()).ToArray();
        Console.WriteLine($"база: {dbPath}");
        Console.WriteLine($"патчи движка: {engine.PatchDisplay}");

        // Кто роль правда играет — считаем САМИ, прямо из base_wr.
        var plays = RolePlayers(dbPath, patches);
        if (plays.Values.All(v => v.Count == 0))
        {
            Console.WriteLine("в базе нет base_wr по этим патчам — проверять нечего");
            return 0;
        }
        Console.WriteLine("играют роль: " + string.Join(", ", Roles.Select(r => $"{r} {plays[r].Count}")));
        Console.WriteLine();

        // ── Контры: три штуки и все — из этой роли ─────────────────────────
        var short3 = new List<string>();
        var aliens = new List<string>();
        var checkedPairs = 0;
        foreach (var role in Roles)
            foreach (var enemy in plays[role])
            {
                checkedPairs++;
                var ids = engine.TopCounters(enemy, role, 3, fill: true);
                if (ids.Count != 3)
                    short3.Add($"{DataDragon.Name(enemy)} ({role}): {ids.Count}");
                foreach (var id in ids)
                    if (!plays[role].Contains(id))
                        aliens.Add($"{DataDragon.Name(id)} против {DataDragon.Name(enemy)} ({role})");
            }

        Check($"контр ровно три у каждого из {checkedPairs}", short3.Count == 0, Few(short3));
        Check("и все они эту роль играют", aliens.Count == 0, Few(aliens));

        // ── Роль врага правда участвует ────────────────────────────────────
        // Без неё пришлось бы мешать линии, которые не встречаются. Берём
        // чемпиона, который ходит и на топ, и на мид: ответы должны различаться.
        var flexes = plays["top"].Intersect(plays["mid"]).Take(5).ToList();
        if (flexes.Count == 0) Console.WriteLine("  (флекс-пиков топ/мид в базе нет — сравнить не на ком)");
        else
        {
            var same = flexes.Where(f =>
                engine.TopCounters(f, "top", 3, fill: true)
                      .SequenceEqual(engine.TopCounters(f, "mid", 3, fill: true))).ToList();
            Check("на топе и на миде контры разные", same.Count == 0,
                  same.Count == 0 ? $"проверено {flexes.Count}" : Few(same.Select(DataDragon.Name)));
        }

        // ── Без роли — старое поведение, а не пустота ──────────────────────
        var anyRole = engine.TopCounters(plays["mid"].First(), null, 3, fill: true);
        Check("без роли подсказка всё равно есть", anyRole.Count > 0, $"{anyRole.Count} шт.");

        // ── Синергия: кандидаты МОЕЙ роли, союзник — в своей ───────────────
        var synAliens = new List<string>();
        var synShort  = new List<string>();
        var synPairs  = 0;
        foreach (var allyRole in Roles)
            foreach (var ally in plays[allyRole])
                foreach (var myRole in Roles)
                {
                    if (myRole == allyRole) continue;   // двоих на линии не бывает
                    synPairs++;
                    var ids = engine.TopSynergies(ally, myRole, 3, allyRole);
                    if (ids.Count != 3)
                        synShort.Add($"{DataDragon.Name(ally)} ({allyRole}) / моя роль {myRole}: {ids.Count}");
                    foreach (var id in ids)
                        if (!plays[myRole].Contains(id))
                            synAliens.Add($"{DataDragon.Name(id)} в {myRole} к {DataDragon.Name(ally)}");
                }

        Check("напарники берутся из МОЕЙ роли", synAliens.Count == 0, Few(synAliens));
        Check($"и их тоже три (из {synPairs} пар)", synShort.Count == 0, Few(synShort));

        // Роль союзника участвует: у флекс-пика на разных линиях напарники разные.
        var flexSyn = plays["top"].Intersect(plays["mid"]).Take(5).ToList();
        if (flexSyn.Count > 0)
        {
            var same = flexSyn.Where(f =>
                engine.TopSynergies(f, "support", 3, "top")
                      .SequenceEqual(engine.TopSynergies(f, "support", 3, "mid"))).ToList();
            Check("   и зависят от линии союзника", same.Count == 0,
                  same.Count == 0 ? $"проверено {flexSyn.Count}" : Few(same.Select(DataDragon.Name)));
        }

        // ── Цифры на портретах: линия союзника и линия врага ───────────────
        // Под союзником — прибавка пары, под врагом — матчап. Оба числа идут в
        // один скор с карточкой, поэтому считать их надо на тех же ролях, что
        // и подбор. Флекс-пик показывает, что роль правда участвует: одна и та
        // же пара на разных линиях — это разные пары.
        var flexAllies = plays["top"].Intersect(plays["mid"]).Take(12).ToList();
        var myCand = plays["support"].First();

        var synDiff = flexAllies
            .Select(f => (Id: f,
                          Top: engine.PairSynergy(myCand, "support", f, "top"),
                          Mid: engine.PairSynergy(myCand, "support", f, "mid")))
            .Where(x => x.Top != 0 || x.Mid != 0).ToList();
        Check("связка считается на линии союзника",
              synDiff.Count > 0 && synDiff.Any(x => Math.Abs(x.Top - x.Mid) >= 0.05),
              synDiff.Count == 0 ? "флекс-пиков не нашлось"
                  : $"{synDiff.Count} пар, наибольший разброс "
                    + $"{synDiff.Max(x => Math.Abs(x.Top - x.Mid)):0.00} пп");

        // И эта разница правда доезжает до кружка: порог +0,3, а сдвиг больше.
        var ringFlips = synDiff.Count(x => (x.Top >= 0.3) != (x.Mid >= 0.3));
        Console.WriteLine($"  (кружок загорается по-разному у {ringFlips} из {synDiff.Count})");

        // Выборка не просела: число под портретом должно стоять не на десятке игр.
        var thin = flexAllies
            .Select(f => (Id: f, engine.PairStats(myCand, "support", f, "mid").Games))
            .Where(x => x.Games < 60).ToList();
        Check("и стоит на приличной выборке", thin.Count == 0,
              thin.Count == 0
                  ? $"минимум {flexAllies.Min(f => engine.PairStats(myCand, "support", f, "mid").Games)} игр"
                  : Few(thin.Select(x => $"{DataDragon.Name(x.Id)}: {x.Games}")));

        var flexEnemies = plays["top"].Intersect(plays["mid"]).Take(12).ToList();
        var vsDiff = flexEnemies
            .Select(f => (Id: f,
                          Top: engine.VersusDelta(myCand, "support", f, "top"),
                          Mid: engine.VersusDelta(myCand, "support", f, "mid")))
            .Where(x => x.Top != 0 || x.Mid != 0).ToList();
        Check("матчап считается на линии врага",
              vsDiff.Count > 0 && vsDiff.Any(x => Math.Abs(x.Top - x.Mid) >= 0.05),
              vsDiff.Count == 0 ? "флекс-пиков не нашлось"
                  : $"{vsDiff.Count} пар, наибольший разброс "
                    + $"{vsDiff.Max(x => Math.Abs(x.Top - x.Mid)):0.00} пп");

        // ── Пример владельца ───────────────────────────────────────────────
        foreach (var (champ, role) in new[] { (711, "mid"), (222, "adc"), (24, "top") })
            if (plays[role].Contains(champ))
            {
                var ids = engine.TopCounters(champ, role, 3, fill: true);
                Console.WriteLine($"  ({DataDragon.Name(champ)} {role}: "
                                  + string.Join(", ", ids.Select(DataDragon.Name)) + ")");
            }

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "ИТОГ: под портретами по три иконки, и все — из нужной роли"
            : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Кто роль правда играет — по своей планке, а не по кодовой.
    private static Dictionary<string, HashSet<int>> RolePlayers(string dbPath, string[] patches)
    {
        var res = Roles.ToDictionary(r => r, _ => new HashSet<int>());
        var inList = string.Join(",", patches.Select((_, i) => $"@p{i}"));
        using var con = new SqliteConnection($"Data Source={dbPath}");
        con.Open();
        foreach (var role in Roles)
        {
            var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games) g FROM base_wr
                WHERE role=@r AND tier_bucket='emerald' AND patch IN ({inList})
                GROUP BY champion_id";
            cmd.Parameters.AddWithValue("@r", role);
            for (int i = 0; i < patches.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", patches[i]);

            var rows = new List<(int Id, double G)>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) rows.Add((rd.GetInt32(0), Convert.ToDouble(rd.GetValue(1))));

            var total = rows.Sum(x => x.G);
            if (total <= 0) continue;
            foreach (var (id, g) in rows)
                if (g >= SANE_GAMES && g >= SANE_SHARE * total) res[role].Add(id);
        }
        return res;
    }

    private static string Few(IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return "нет";
        return $"{list.Count}: " + string.Join("; ", list.Take(4));
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
