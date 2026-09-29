using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Подпись бана про «угрозу всей команде» называет имена и цифры.
///
/// Было: «Обыгрывает вашу команду — силён против 2 ваших пиков». По такому
/// числу нельзя ни проверить совет, ни выбрать между двумя кандидатами: против
/// КОГО и насколько — не сказано.
///
/// Стало: «силён против Экко (+0,5), Синджед (+3,4)» — те же очки и в том же
/// виде, что на карточках драфта.
///
/// Считаем на живой базе игрока. Нет базы — проверять нечего, выходим молча.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Слово «против» из подписи — по нему отличаем нужные строки от прочих.
    private static string Head => Loc.T("reason.teamThreat", "\u0001").Split('\u0001')[0];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечего"); return 0; }
        Console.WriteLine($"база: {db}");

        using var engine = RecommendationEngine.Create(db, "emerald");

        // Несколько составов: подпись выдаётся не на каждый драфт, и одного
        // набора может не хватить, чтобы она вообще появилась.
        var teams = new[]
        {
            new[] { 157, 64,  103 },   // Ясуо, Ли Син, Ари
            new[] { 122, 51,  238 },   // Дариус, Кейтлин, Зед
            new[] { 86,  11,  99  },   // Гарен, Мастер Йи, Люкс
            new[] { 39,  20,  22  },   // Ирелия, Нунду, Эш
        };

        var seen = 0;
        foreach (var team in teams)
        {
            var bans = engine.RecommendBans(Draft(team), top: 8);
            foreach (var reason in bans.SelectMany(b => b.Reasons)
                                       .Where(r => r.StartsWith(Head, StringComparison.Ordinal)))
            {
                seen++;
                var tail = reason[Head.Length..];

                // Имена с числами в скобках: «Экко (+0,5), Синджед (+3,4)».
                var named = Regex.Matches(tail, @"\(([+-]\d+[.,]\d)\)").Count;
                Check($"подпись называет поимённо: «{Short(reason)}»", named >= 2, $"{named} с цифрами");

                // Под словом «силён против» не должно стоять отрицательных:
                // общая угроза складывается из всех матчапов, и слабые для него
                // тоже попадали в список.
                var weak = Regex.Matches(tail, @"\(-\d").Count;
                Check("   и без тех, против кого он слаб", weak == 0, $"{weak} с минусом");

                // И никакого голого счётчика вместо имён.
                Check("   это не «против N ваших пиков»",
                      !Regex.IsMatch(tail, @"^\s*\d+\s"), tail.TrimStart()[..Math.Min(20, tail.TrimStart().Length)]);
            }
        }

        Check($"подпись вообще встречается (составов: {teams.Length})", seen > 0, $"{seen} шт.");

        // ── Роль союзника участвует в счёте ────────────────────────────────
        //
        // matchup — это противостояние НА ЛИНИИ. Значит у каждой названной пары
        // «кандидат против союзника» ОБЯЗАНЫ быть строки именно в роли этого
        // союзника. Без учёта роли туда попадал, например, лесной Скарнер как
        // угроза Грагасу на топе: строки есть, но лес против леса.
        //
        // Первая версия этой проверки сравнивала подписи «с ролями» и «без
        // ролей» и проходила в обоих случаях: роль влияет и на другие слагаемые,
        // так что разница была всегда. Выброшена.
        var checkedPairs = 0;
        foreach (var team in teams)
        {
            var roleOf = new Dictionary<int, string>();
            for (var i = 0; i < team.Length; i++) roleOf[team[i]] = DbRole(i);

            foreach (var ban in engine.RecommendBans(Draft(team), top: 8))
                foreach (var reason in ban.Reasons.Where(r => r.StartsWith(Head, StringComparison.Ordinal)))
                    foreach (Match m in Regex.Matches(reason, @"id=(\d+) \("))
                    {
                        var victim = int.Parse(m.Groups[1].Value);
                        if (!roleOf.TryGetValue(victim, out var role)) continue;
                        checkedPairs++;
                        Check($"   {ban.ChampionId} против {victim} есть на роли {role}",
                              Games(db, ban.ChampionId, victim, role) > 0, "строки есть");
                    }
        }
        Check("пар проверено на роль", checkedPairs > 0, $"{checkedPairs}");

        Mains(engine);

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: бан объясняет, против кого и насколько"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Драфт: своя роль известна, союзники уже показали пики, врагов нет.
    private static DraftState Draft(IReadOnlyList<int> allies, bool withRole = true)
    {
        // Роли союзникам задаём НАРОЧНО: matchup — это противостояние на линии,
        // и без роли угроза складывалась по всем ролям сразу (лесной матчап
        // приписывался топ-лейнеру). Проверка должна гонять именно этот путь.
        string[] pos = ["top", "jungle", "middle"];   // см. DbRole
        var team = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        for (var i = 0; i < allies.Count; i++)
            team.Add(new DraftPlayer(i + 1, allies[i], 0, withRole ? pos[i % pos.Length] : "", false));
        return new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: team[0], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: true);
    }

    /// <summary>
    /// Защитные баны смотрят на то, чем игрок играет СЕЙЧАС.
    ///
    /// Раньше «мейны» брались только из мастерства, а оно копится годами: в
    /// банах всплывало «контрит Зилеана» на чемпионе, которого не трогали
    /// полгода. Такой совет — потраченный бан.
    ///
    /// Внутри RecommendBans история игр используется ровно в одном месте — в
    /// списке мейнов. Значит разница в подписях при разной истории доказывает,
    /// что она учитывается.
    /// </summary>
    private static void Mains(RecommendationEngine engine)
    {
        Console.WriteLine();
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        var path = System.IO.Path.Combine(dir, "session.json");
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        const string Puuid = "TEST-bans-mains-000000000000000000000000000000000000000000000000000000";

        try
        {
            PoolStore.SetAccount(Puuid, "проверка");
            engine.Mastery = new Dictionary<int, long> { [412] = 900_000L };   // Тамкенч по мастерству

            File.WriteAllText(path, "{\"Accounts\":{}}");
            SessionTracker.DropCache();
            var noHistory = Reasons(engine);

            // Двадцать свежих игр на другом чемпионе этой же роли.
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var games = string.Join(",", Enumerable.Range(0, 20).Select(i =>
                $"{{\"Ts\":{now - i * 3600},\"ChampionId\":89,\"Win\":true}}"));
            File.WriteAllText(path,
                $"{{\"Accounts\":{{\"{Puuid}\":{{\"Queues\":{{\"solo\":{{\"Games\":[{games}]}}}}}}}}}}");
            SessionTracker.DropCache();
            var withHistory = Reasons(engine);

            Check("свежая история меняет защитные баны",
                  withHistory.Count > 0 && !withHistory.SetEquals(noHistory),
                  $"без истории {noHistory.Count}, с историей {withHistory.Count}");
        }
        finally
        {
            if (before is not null) File.WriteAllText(path, before);
            else if (File.Exists(path)) File.Delete(path);
            SessionTracker.DropCache();
            Console.WriteLine("своя история игр возвращена на место");
        }
    }

    /// Подписи «контрит твой пул» — их и питают мейны.
    private static HashSet<string> Reasons(RecommendationEngine engine)
    {
        var head = Loc.T("reason.countersPool", "").Split('')[0];
        return engine.RecommendBans(Draft([157, 64, 103]), top: 10)
                     .SelectMany(b => b.Reasons)
                     .Where(r => r.StartsWith(head, StringComparison.Ordinal))
                     .ToHashSet();
    }

    /// Роль союзника по его месту в составе — в том виде, как её хранит база.
    /// «middle» у клиента и «mid» в базе — разные слова, и путать их нельзя.
    private static string DbRole(int i) => (i % 3) switch { 0 => "top", 1 => "jungle", _ => "mid" };

    /// Сколько игр у пары именно на этой роли.
    private static long Games(string db, int champ, int vs, string role)
    {
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        con.Open();
        var cmd = con.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(SUM(games),0) FROM matchup
                            WHERE champion_id=@c AND vs_champion_id=@v AND role=@r";
        cmd.Parameters.AddWithValue("@c", champ);
        cmd.Parameters.AddWithValue("@v", vs);
        cmd.Parameters.AddWithValue("@r", role);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string Short(string s) => s.Length <= 64 ? s : s[..61] + "…";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
