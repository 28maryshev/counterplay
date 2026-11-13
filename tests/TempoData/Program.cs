using System.IO;
using System.Text;
using System.Windows.Media;
using Counterplay;
using Color = System.Windows.Media.Color;
using Microsoft.Data.Sqlite;

namespace Counterplay.Tests;

/// <summary>
/// Сила по времени игры из данных (duration_wr), а не из меток.
///
/// Повод (09.11.2026): владелец — «делай всё» по предложениям, среди них «сила
/// по времени игры из данных». Ранняя сила держалась на метках aggressive/scale:
/// на миде не было ни одного раннего чемпиона, а замер дыр не смог измерить
/// «раннюю игру» — 5 драфтов из 807 тысяч.
///
/// Меряет:
/// - расчёт (BuildTempo): сильный рано, поздний и ровный чемпион на
///   синтетических строках получают силу по отрезкам с нужным знаком, а с малым
///   числом игр — не получают ничего; шкала ранней силы (EarlyFromTempo) — 3, 0, 2;
/// - подмена: с данными ChampionTraits.EarlyPower берёт их, без — метки;
/// - движок на настоящей базе без таблицы — силы по времени нет, подбор цел;
/// - движок на копии базы с подложенной duration_wr — Tempo и TeamTempo есть,
///   плашки «винрейт по длине игры» в полосе состава собираются (отрезок,
///   знак, «%», цвет по знаку), ранняя сила подменена.
/// Копия базы — во временной папке; данные игрока не трогает.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "TempoData");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var player = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = player;
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Math_();
        Override();
        Engine(player, root);

        ChampionTraits.EarlyPowerData = null;
        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: сила по времени считается из данных" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // Чемпион 1 — рано 60%, поздно 40%; 2 — наоборот; 3 — ровно 50%; 4 — мало игр.
    private static readonly (int, int, double, double)[] Rows =
    [
        (1, 0, 600, 360), (1, 1, 500, 250), (1, 2, 300, 120),
        (2, 0, 600, 240), (2, 1, 500, 250), (2, 2, 300, 180),
        (3, 0, 600, 300), (3, 1, 500, 250), (3, 2, 300, 150),
        (4, 0, 200, 140), (4, 1, 200, 100), (4, 2, 100, 30),
    ];

    private static void Math_()
    {
        Console.WriteLine("расчёт:");
        var t = RecommendationEngine.BuildTempo(Rows);
        Check("сильный рано: рано плюс, поздно минус", t.TryGetValue(1, out var a) && a.Early > 1 && a.Late < -1,
              t.ContainsKey(1) ? $"{a.Early:F2} / {a.Mid:F2} / {a.Late:F2}" : "нет");
        Check("поздний: рано минус, поздно плюс", t.TryGetValue(2, out var b) && b.Early < -1 && b.Late > 1,
              t.ContainsKey(2) ? $"{b.Early:F2} / {b.Mid:F2} / {b.Late:F2}" : "нет");
        Check("ровный: около нуля", t.TryGetValue(3, out var c) && Math.Abs(c.Early) < 0.5 && Math.Abs(c.Late) < 0.5,
              t.ContainsKey(3) ? $"{c.Early:F2} / {c.Mid:F2} / {c.Late:F2}" : "нет");
        Check("мало игр — силы нет", !t.ContainsKey(4), t.ContainsKey(4) ? "есть" : "нет");
        var e = RecommendationEngine.EarlyFromTempo(t);
        Check("шкала ранней силы: 3, 0, 2", e.GetValueOrDefault(1) == 3 && e.GetValueOrDefault(2) == 0 && e.GetValueOrDefault(3) == 2,
              string.Join(", ", e.OrderBy(x => x.Key).Select(x => $"{x.Key}→{x.Value}")));
    }

    private static void Override()
    {
        Console.WriteLine("подмена:");
        const int Jinx = 222;   // по меткам — гиперкерри, 0
        ChampionTraits.EarlyPowerData = null;
        var byTags = ChampionTraits.EarlyPower(Jinx);
        ChampionTraits.EarlyPowerData = new Dictionary<int, int> { [Jinx] = 3 };
        var byData = ChampionTraits.EarlyPower(Jinx);
        ChampionTraits.EarlyPowerData = null;
        Check("с данными — данные, без — метки", byTags == 0 && byData == 3, $"метки {byTags}, данные {byData}");
    }

    private static void Engine(string player, string root)
    {
        Console.WriteLine("движок:");
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  базы нет — дальше нечем"); return; }

        // Копия базы без таблицы. С 13.11 настоящая база приходит уже с
        // duration_wr, поэтому «без таблицы» — только на копии с убранной.
        var bare = Path.Combine(root, "bare.db");
        File.Copy(db, bare);
        Exec(bare, "DROP TABLE IF EXISTS duration_wr");
        ChampionTraits.EarlyPowerData = null;
        using (var plain = RecommendationEngine.Create(bare, "emerald"))
        {
            Check("база без duration_wr — силы по времени нет", plain.Tempo(222) is null && ChampionTraits.EarlyPowerData is null,
                  plain.Tempo(222) is null ? "нет" : "есть");
        }
        SqliteConnection.ClearAllPools();

        // Копия базы с подложенной таблицей: Джинкс поздняя, Дрейвен ранний.
        var copy = Path.Combine(root, "data.db");
        File.Copy(bare, copy);
        Exec(copy, @"
            CREATE TABLE duration_wr (champion_id INTEGER, role TEXT, tier_bucket TEXT, patch TEXT, bucket INTEGER,
                                      games INTEGER, wins INTEGER, PRIMARY KEY (champion_id, role, tier_bucket, patch, bucket));
            INSERT INTO duration_wr SELECT c, 'adc', 'emerald', p.patch, b, 2000,
                CASE WHEN c = 222 THEN (CASE b WHEN 0 THEN 800 WHEN 1 THEN 1000 ELSE 1200 END)
                     ELSE              (CASE b WHEN 0 THEN 1200 WHEN 1 THEN 1000 ELSE 800 END) END
            FROM (SELECT 222 AS c UNION SELECT 119) JOIN (SELECT DISTINCT patch FROM base_wr) p
            JOIN (SELECT 0 AS b UNION SELECT 1 UNION SELECT 2);");
        SqliteConnection.ClearAllPools();
        using var engine = RecommendationEngine.Create(copy, "emerald");
        var jinx = engine.Tempo(222);
        var draven = engine.Tempo(119);
        Check("с таблицей: Джинкс сильнее поздно, Дрейвен — рано",
              jinx is { } j && j.Late > j.Early && draven is { } d && d.Early > d.Late,
              $"Джинкс {Fmt(jinx)}, Дрейвен {Fmt(draven)}");
        Check("ранняя сила подменена данными: Дрейвен 3, Джинкс 0",
              ChampionTraits.EarlyPower(119) == 3 && ChampionTraits.EarlyPower(222) == 0,
              $"Дрейвен {ChampionTraits.EarlyPower(119)}, Джинкс {ChampionTraits.EarlyPower(222)}");
        // Данные только у Джинкс: плашки команды — её сила, поздно с плюсом.
        // Владелец, 13.11: цифры подписаны отрезком и «%», знак виден цветом —
        // зелёная с плюсом, красная с минусом.
        var chips = OverlayWindow.TempoChips(engine, [222, 64, 89]);
        string Show(TempoChipVm c) => $"{c.Label} {c.Value}";
        static Color Of(TempoChipVm c) => ((SolidColorBrush)c.Fg).Color;
        Check("плашки «винрейт по длине игры»: до 25 мин −9,0% красная, после 35 мин +9,0% зелёная",
              chips.Count == 3
              && Show(chips[0]) == "до 25 мин −9,0%" && Show(chips[2]) == "после 35 мин +9,0%"
              && Of(chips[0]).R > Of(chips[0]).G && Of(chips[2]).G > Of(chips[2]).R,
              string.Join(" | ", chips.Select(c => $"{Show(c)} {Of(c)}")));
        Check("подпись над плашками называет винрейт", Loc.T("comp.tempoHeader").Contains("Винрейт"), Loc.T("comp.tempoHeader"));
        Check("двое — плашек нет", OverlayWindow.TempoChips(engine, [222, 119]).Count == 0, "пусто");
        SqliteConnection.ClearAllPools();
    }

    private static void Exec(string db, string sql)
    {
        using var con = new SqliteConnection($"Data Source={db}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Fmt((double Early, double Mid, double Late)? t) =>
        t is { } v ? $"{v.Early:F1}/{v.Mid:F1}/{v.Late:F1}" : "нет";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
