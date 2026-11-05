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
/// Пишет только в переданный файл; папку игрока не трогает (журнал выключен,
/// справочник чемпионов — только чтение кэша).
/// </summary>
public static class StyleFeatures
{
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

        var rows = new List<(string Id, double Want, double Style)>();
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
        tx.Commit();
        Console.WriteLine($"draft_style: {rows.Count} драфтов");
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
