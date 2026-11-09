using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Counterplay;

/// <summary>
/// Признаки «против стиля врага» по каждому драфту — для замера по исходам
/// (pipeline/calibrate.py). Сам замер — в Python, а метки архетипов и
/// инструментов живут только здесь (ChampionTags, ChampionTraits), поэтому
/// признаки считает программа той же формулой, что подбор
/// (RecommendationEngine.StyleParts), и кладёт в ту же выжимку таблицей
/// draft_style.
///
///   Counterplay.exe --style-features calib-slice.db
///
/// По драфту: у каждой команды — доминанта ВРАЖЕСКОЙ пятёрки (как в подборе при
/// всех известных врагах), у каждого своего чемпиона — Want (трифекта) и Style
/// (инструменты), сумма по пятёрке. В таблицу — разность команды 100 и 200.
///
/// С 09.11 — ещё таблица draft_gaps: дыры состава полной пятёрки той же
/// функцией, что у подбора (RecommendationEngine.TeamGaps без роли), по
/// каждому виду 1 — открыта, 0 — нет; в таблицу — разность команд. Тип урона
/// (needAp и needAd) — одним признаком: перекос в любую сторону.
/// Пишет только в переданный файл; папку игрока не трогает (журнал выключен,
/// справочник чемпионов — только чтение кэша).
/// </summary>
public static class StyleFeatures
{
    /// Виды дыр в порядке столбцов draft_gaps (calibrate.py читает их по именам).
    public static readonly string[] GapColumns =
        ["front", "disengage", "engage", "peel", "cc", "dmg", "antitank", "wave", "early"];

    private static string GapColumn(string kind) => kind switch
    {
        "needFront" => "front", "needDisengage" => "disengage", "needEngage" => "engage",
        "needPeel" => "peel", "needCc" => "cc", "needAp" or "needAd" => "dmg",
        "needAntiTank" => "antitank", "needWaveclear" => "wave", "needEarly" => "early",
        _ => throw new ArgumentException(kind),
    };

    private static double[] Gaps(List<int> mine, List<int> enemy)
    {
        var v = new double[GapColumns.Length];
        foreach (var g in RecommendationEngine.TeamGaps(mine, enemy, null))
            v[Array.IndexOf(GapColumns, GapColumn(g.Kind))] = 1;
        return v;
    }

    public static int Run(string dbPath)
    {
        Log.FileDisabled = true;
        // Классы чемпионов (часть архетипа) — из кэша справочника, язык не важен.
        if (!DataDragon.LoadFromCache("en_US") && !DataDragon.LoadFromCache("ru_RU"))
        { Console.Error.WriteLine("нет кэша справочника чемпионов — запустите программу один раз"); return 2; }
        if (!File.Exists(dbPath)) { Console.Error.WriteLine($"нет файла {dbPath}"); return 2; }

        using var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();
        Exec(db, "DROP TABLE IF EXISTS draft_style");
        Exec(db, "CREATE TABLE draft_style (match_id TEXT PRIMARY KEY, want REAL, style REAL)");
        Exec(db, "DROP TABLE IF EXISTS draft_gaps");
        Exec(db, $"CREATE TABLE draft_gaps (match_id TEXT PRIMARY KEY, {string.Join(", ", GapColumns.Select(c => c + " REAL"))})");

        var rows = new List<(string Id, double Want, double Style)>();
        var gapRows = new List<(string Id, double[] Diff)>();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT match_id, slots FROM drafts";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var teams = new Dictionary<int, List<int>> { [100] = [], [200] = [] };
                foreach (var s in JsonDocument.Parse(rd.GetString(1)).RootElement.EnumerateArray())
                    if (teams.TryGetValue(s.GetProperty("t").GetInt32(), out var list))
                        list.Add(s.GetProperty("c").GetInt32());
                if (teams[100].Count != 5 || teams[200].Count != 5) continue;
                var (wa, sa) = Side(teams[100], teams[200]);
                var (wb, sb) = Side(teams[200], teams[100]);
                rows.Add((rd.GetString(0), wa - wb, sa - sb));
                var ga = Gaps(teams[100], teams[200]);
                var gb = Gaps(teams[200], teams[100]);
                gapRows.Add((rd.GetString(0), [.. ga.Zip(gb, (x, y) => x - y)]));
            }
        }

        using var tx = db.BeginTransaction();
        using (var ins = db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO draft_style VALUES (@m, @w, @s)";
            var pm = ins.Parameters.Add("@m", SqliteType.Text);
            var pw = ins.Parameters.Add("@w", SqliteType.Real);
            var ps = ins.Parameters.Add("@s", SqliteType.Real);
            foreach (var (id, w, s) in rows)
            {
                pm.Value = id; pw.Value = w; ps.Value = s;
                ins.ExecuteNonQuery();
            }
        }
        using (var ins = db.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = $"INSERT INTO draft_gaps VALUES (@m, {string.Join(", ", GapColumns.Select(c => "@" + c))})";
            var pm = ins.Parameters.Add("@m", SqliteType.Text);
            var ps = GapColumns.Select(c => ins.Parameters.Add("@" + c, SqliteType.Real)).ToArray();
            foreach (var (id, diff) in gapRows)
            {
                pm.Value = id;
                for (var i = 0; i < ps.Length; i++) ps[i].Value = diff[i];
                ins.ExecuteNonQuery();
            }
        }
        tx.Commit();
        Console.WriteLine($"draft_style: {rows.Count} драфтов");
        // Сколько команд с каждой дырой — чтобы видеть, хватит ли выборки на замер.
        Console.WriteLine("draft_gaps: " + string.Join(", ", GapColumns.Select((c, i) =>
            $"{c} {gapRows.Count(r => r.Diff[i] != 0)}")) + " драфтов с разницей");
        return 0;
    }

    // Сумма по своей пятёрке против доминанты врага.
    private static (double Want, double Style) Side(List<int> mine, List<int> enemy)
    {
        var dom = ChampionTraits.Dominant(enemy);
        double w = 0, s = 0;
        foreach (var c in mine)
        {
            var (want, style) = RecommendationEngine.StyleParts(c, dom);
            w += want; s += style;
        }
        return (w, s);
    }

    private static void Exec(SqliteConnection db, string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }
}
