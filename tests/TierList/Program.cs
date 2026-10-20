using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Тир-лист — по силе чемпиона, а не по тому, как часто его берут и банят.
///
/// История (журнал 2026-10-20): владелец сравнил наш тир-лист с тир-листом,
/// которому доверяет, — расходились почти во всём. Оценка была LB + 1,2·пик +
/// 0,4·бан: присутствие давало до 20 очков против ±3 у силы. Наверху стояли
/// самые популярные (Кейтлин 49% — первая на боте, Ли Син 48,6% — первый в
/// лесу), бан-рейт чемпиона засчитывался каждой его роли целиком (Ясуо с 1% игр
/// на боте получал все 25% банов из мида), а грейд шёл пирамидой по месту.
///
/// Держим на своей маленькой базе (бот — 100 000 игр роли, мид и топ — меньше):
/// (1) первый в тире — сильнейший, а не самый популярный и банимый;
/// (2) чужой бан-рейт не поднимает: порядок — OP первыми, дальше строго по
///     силе (OP — полоса над S, как на сайте и в эталоне);
/// (3) грейд — по силе: популярный с 49% — B, с 47% — C, средний — A;
/// (4) OP — сильный (от 51,5%) и массовый: (сила − 50) × пикрейт / (1 − бан)
///     от 5. Число не фиксировано — на боте четверо проходят и потолок оставляет
///     троих, в миде OP один, на топе ни одного; сильнейший нишевой с 0,8% игр —
///     S (владелец 20.10: «OP должен быть не всегда, у других чаще всего 1»);
/// (5) порог доли — 0,5%: 0,6% в тир-листе, 0,3% нет;
/// (6) малая выборка не взлетает: 58% на 800 играх ниже 53% на 10 000 — в
///     столбце винрейта, наоборот, первая;
/// (7) пикрейт — от всех игр роли.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Patch = "16.20", Bucket = "emerald";

    // Чемпионы бота: id, игры, победы, баны. Роль — 100 000 игр.
    private const int Popular = 1, Strong = 2, Second = 3, Third = 4, Fourth = 5,
                      Niche = 6, Fringe = 7, Noise = 8, MidBanned = 9, Weak = 10, Filler = 11;
    // Мид: один OP. Топ: OP нет.
    private const int MidOne = 21, MidTwo = 22, TopFlat = 31, TopSecond = 32;

    private static readonly (int Id, int Games, int Wins, int Bans)[] Adc =
    [
        (Popular,   12_000,  5_880, 3_900),   // 49%, 12% игр, 30% банов
        (Strong,    10_000,  5_300,     0),   // 53% → сила 52,5
        (Second,     8_000,  4_200,     0),   // 52,5% → 52,0
        (Third,      6_000,  3_144,     0),   // 52,4% → 51,8
        (Fourth,     5_000,  2_620,     0),   // 52,4% → 51,71: проходит, но четвёртый — потолок
        (Niche,        800,    464,     0),   // 58% на 0,8% игр → 52,29: сильнее Second, влияние 1,8
        (Fringe,       600,    300,     0),   // 0,6% — проходит порог 0,5%
        (Noise,        300,    180,     0),   // 0,3% — не проходит, хоть игр и ≥ 250
        (MidBanned,  1_500,    750, 3_900),   // 50% на боте, банят за мид
        (Weak,      40_000, 18_800,     0),   // 47% → 47,14
        (Filler,    15_800,  7_900,     0),   // 50% — добивка до 100 000
    ];

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока
        // Своя папка вместо папки игрока — на случай, если движок к ней потянется.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "TierList");
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;

        var db = Path.Combine(root, "tier.db");
        BuildDb(db);

        using (var engine = RecommendationEngine.Create(db, Bucket))
        {
            var tier = engine.TierList(perRole: 200).Where(t => t.Role == "adc").ToList();
            var byWr = engine.TierList(perRole: 200, byWinrate: true).Where(t => t.Role == "adc").ToList();
            Console.WriteLine("бот, тир: " + string.Join(", ", tier.Select(t => $"{t.ChampionId}:{t.Grade}")));

            var at = tier.ToDictionary(t => t.ChampionId);
            int Place(int id) => tier.FindIndex(t => t.ChampionId == id) + 1;
            string Grade(int id) => at.TryGetValue(id, out var t) ? t.Grade : "нет";

            // (1)
            Check("первый в тире — сильнейший, а не популярный с банами",
                  tier[0].ChampionId == Strong, $"первый {tier[0].ChampionId}, популярный на {Place(Popular)}-м");
            Check("популярный с 49% и 30% банов — не в пятёрке", Place(Popular) > 5, $"{Place(Popular)}-й");

            // (2) порядок — OP первыми, дальше по силе; баны (свои и чужие) не участвуют
            static double Power(RecommendationEngine.TierEntry t) =>
                (t.Winrate / 100 * t.Games + 1000) / (t.Games + 2000);
            static (int, double) Key(RecommendationEngine.TierEntry t) => (t.Grade == "OP" ? 0 : 1, -Power(t));
            var sorted = tier.Zip(tier.Skip(1)).All(p => Key(p.First).CompareTo(Key(p.Second)) <= 0);
            Check("порядок тира — OP, дальше по силе", sorted,
                  string.Join(" ", tier.Select(t => $"{t.ChampionId}:{t.Grade}:{100 * Power(t):F2}")));
            Check("OP выше нишевого S, хоть тот и сильнее", Place(Niche) > Place(Third), $"{Place(Niche)}-й");
            Check("бан-рейт из мида не поднимает на боте",
                  Place(MidBanned) > Place(Fourth) && Grade(MidBanned) == "A",
                  $"{Place(MidBanned)}-й, {Grade(MidBanned)}");

            // (3)
            Check("популярный с 49% — B", Grade(Popular) == "B", Grade(Popular));
            Check("47% — C", Grade(Weak) == "C", Grade(Weak));
            Check("50% — A", Grade(Filler) == "A", Grade(Filler));

            // (4)
            var op = tier.Where(t => t.Grade == "OP").Select(t => t.ChampionId).OrderBy(x => x).ToList();
            Check("бот: OP — три самых влиятельных из четырёх прошедших", op.SequenceEqual([Strong, Second, Third]), string.Join(",", op));
            Check("нишевой сильнейший (0,8% игр) — S, не OP", Grade(Niche) == "S", Grade(Niche));

            // Число OP не фиксировано: в миде один, на топе ни одного.
            var all = engine.TierList(perRole: 200);
            List<int> OpOf(string role) => [.. all.Where(t => t.Role == role && t.Grade == "OP").Select(t => t.ChampionId)];
            Check("мид: OP один — сильный, но нишевой (1,6% игр) не OP", OpOf("mid").SequenceEqual([MidOne]),
                  string.Join(",", OpOf("mid")));
            Check("топ: OP нет, хоть сильнейший и S", OpOf("top").Count == 0
                  && all.Any(t => t.ChampionId == TopSecond && t.Role == "top" && t.Grade == "S"),
                  string.Join(",", OpOf("top")));
            Check("четвёртый по силе ходовой — S", Grade(Fourth) == "S", Grade(Fourth));

            // (5)
            Check("0,6% игр роли — в тир-листе", at.ContainsKey(Fringe), Grade(Fringe));
            Check("0,3% игр роли — нет", !at.ContainsKey(Noise), Grade(Noise));

            // (6)
            Check("58% на 800 играх ниже 53% на 10 000", Place(Niche) > Place(Strong), $"{Place(Niche)}-й");
            Check("в столбце винрейта 58% — первая", byWr[0].ChampionId == Niche, $"{byWr[0].ChampionId}");
            Check("грейд один в обоих столбцах",
                  byWr.All(w => at[w.ChampionId].Grade == w.Grade), "");

            // (7)
            Check("пикрейт — от всех игр роли", Math.Abs(at[Niche].PickRate - 0.8) < 1e-9,
                  $"{at[Niche].PickRate:F3}%");
        }

        SqliteConnection.ClearAllPools();
        try { File.Delete(db); } catch { /* не страшно */ }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: тир-лист по силе" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// База одного патча и бакета: бот целиком, в миде — только «банят за мид».
    private static void BuildDb(string path)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(path)) File.Delete(path);
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE base_wr (champion_id INTEGER, role TEXT, tier_bucket TEXT, patch TEXT,
                                  games INTEGER, wins INTEGER,
                                  PRIMARY KEY (champion_id, role, tier_bucket, patch));
            CREATE TABLE champion_bans (champion_id INTEGER, tier_bucket TEXT, patch TEXT, bans INTEGER,
                                        PRIMARY KEY (champion_id, tier_bucket, patch));";
        cmd.ExecuteNonQuery();

        void Row(int id, string role, int g, int w)
        {
            var c = con.CreateCommand();
            c.CommandText = "INSERT INTO base_wr VALUES (@id, @r, @t, @p, @g, @w)";
            c.Parameters.AddWithValue("@id", id); c.Parameters.AddWithValue("@r", role);
            c.Parameters.AddWithValue("@t", Bucket); c.Parameters.AddWithValue("@p", Patch);
            c.Parameters.AddWithValue("@g", g); c.Parameters.AddWithValue("@w", w);
            c.ExecuteNonQuery();
        }
        foreach (var (id, g, w, bans) in Adc)
        {
            Row(id, "adc", g, w);
            if (bans <= 0) continue;
            var c = con.CreateCommand();
            c.CommandText = "INSERT INTO champion_bans VALUES (@id, @t, @p, @b)";
            c.Parameters.AddWithValue("@id", id); c.Parameters.AddWithValue("@t", Bucket);
            c.Parameters.AddWithValue("@p", Patch); c.Parameters.AddWithValue("@b", bans);
            c.ExecuteNonQuery();
        }
        // Мид: «банят за мид» играется там в 20 раз чаще, чем на боте.
        Row(MidBanned, "mid", 30_000, 15_000);
        Row(MidOne,    "mid",  6_000,  3_210);   // 53,5% на 16% игр → сила 52,6, влияние 43: OP
        Row(MidTwo,    "mid",    600,    348);   // 58% на 1,6% игр → 51,85, влияние 3,0: S
        // Топ: никто не дотягивает до 51,5 — OP нет.
        Row(TopFlat,   "top", 20_000, 10_100);   // 50,5% → 50,45
        Row(TopSecond, "top",  5_000,  2_575);   // 51,5% → 51,07: S
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
