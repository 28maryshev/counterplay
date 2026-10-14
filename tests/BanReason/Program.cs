using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Подписи банов про союзников называют поимённо тех, кого бан защищает, и не
/// врут.
///
/// История: сначала было «Обыгрывает вашу команду — силён против 2 ваших
/// пиков» — число без имён, по нему нельзя ни проверить совет, ни выбрать между
/// двумя кандидатами. Потом «силён против Экко (+0,5), Синджед (+3,4)», и среди
/// имён попадались те, против кого он слаб. С 14.10 отдельной «угрозы команде»
/// нет: рейтинг банов складывает контры каждому нашему, и подпись одна —
/// «Контрит ваших: Ирелия, Мастер Йи · пик 3,6%».
///
/// Держим: (1) подпись про нескольких встречается и называет их по именам;
/// (2) каждый названный правда проигрывает кандидату — его винрейт в паре ниже
/// его же среднего; (3) пара есть именно на роли союзника: matchup — это
/// противостояние на линии, и без роли лесной Скарнер числился угрозой Грагасу
/// на топе.
///
/// Считаем на живой базе игрока. Нет базы — проверять нечего, выходим молча.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Начала подписей про союзников — по ним отличаем нужные строки от прочих.
    private static string Many => Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];

    private static string One => Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к любому
        // хранилищу. Раньше проверка писала в настоящие файлы и «возвращала
        // как было»: правку, сделанную рядом работающей программой, это откатывало.
        AppPaths.RootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "counterplay-test-root", "BanReason");
        System.IO.Directory.CreateDirectory(AppPaths.RootOverride);
        // А база матчей — скачанная, настоящая: её проверка только читает.
        DataDb.DirOverride = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        // Пулы — в свою папку, НЕ в файл игрока. Проверки однажды уже оставили
        // в его pools.json тестовые аккаунты, а свои пулы — пустыми.
        PoolStore.DirOverride = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "counterplay-test-pools", "BanReason");
        System.IO.Directory.CreateDirectory(PoolStore.DirOverride);
        // И начинаем с ЧИСТОГО листа: состояние, оставшееся от прошлого
        // запуска, делает проверку неповторяемой. SetFavourite, например,
        // переключает — со звездой от прошлого раза он её гасит, и проверка
        // падает через раз.
        var stale = System.IO.Path.Combine(PoolStore.DirOverride, "pools.json");
        if (System.IO.File.Exists(stale)) System.IO.File.Delete(stale);
        PoolStore.Reload();

        Loc.SetLanguage("ru");

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечего"); return 0; }
        Console.WriteLine($"база: {db}");

        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        // Несколько составов: подпись выдаётся не на каждый драфт, и одного
        // набора может не хватить, чтобы она вообще появилась. Имена чемпионов
        // не загружены — в подписи они «id=N», по ним и разбираем.
        var teams = new[]
        {
            new[] { 157, 64,  103 },   // Ясуо, Ли Син, Ари
            new[] { 122, 51,  238 },   // Дариус, Кейтлин, Зед
            new[] { 86,  11,  99  },   // Гарен, Мастер Йи, Люкс
            new[] { 39,  20,  22  },   // Ирелия, Нунду, Эш
        };

        int many = 0, pairs = 0;
        foreach (var team in teams)
        {
            var roleOf = new Dictionary<int, string>();
            for (var i = 0; i < team.Length; i++) roleOf[team[i]] = DbRole(i);

            foreach (var ban in engine.RecommendBans(Draft(team), top: 8))
                foreach (var reason in ban.Reasons.Where(r => r.StartsWith(Many, StringComparison.Ordinal)
                                                           || r.StartsWith(One, StringComparison.Ordinal)))
                {
                    var named = Regex.Matches(reason, @"id=(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                    if (reason.StartsWith(Many, StringComparison.Ordinal))
                    {
                        many++;
                        Check($"подпись называет поимённо: «{Short(reason)}»", named.Count >= 2, $"{named.Count} имён");
                        Check("   и это не «N ваших пиков»", !Regex.IsMatch(reason[Many.Length..], @"^\s*\d+\s"), "");
                    }
                    foreach (var victim in named)
                    {
                        if (!roleOf.TryGetValue(victim, out var role)) continue;
                        pairs++;
                        var (g, w) = Rows(db, engine, "matchup", victim, role, ban.ChampionId);
                        var (bg, bw) = Rows(db, engine, "base_wr", victim, role);
                        Check($"   {ban.ChampionId} против {victim} есть на роли {role}", g > 0, $"{g:F0} игр");
                        Check($"   и {victim} ему проигрывает", g > 0 && w / g < bw / bg,
                              g > 0 ? $"{100 * w / g:F1}% в паре против {100 * bw / bg:F1}% в среднем" : "пары нет");
                    }
                }
        }
        Check($"подпись про нескольких союзников встречается (составов: {teams.Length})", many > 0, $"{many} шт.");
        Check("пар проверено на роль", pairs > 0, $"{pairs}");

        Mains(engine);

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: бан объясняет, кого защищает, и не врёт"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Пара (союзник против кандидата) и средний винрейт союзника на роли — в
    /// бакете и окне патчей движка, с теми же весами патчей (1; 0,6; 0,3). По
    /// всем патчам базы сравнивать нельзя: Люкс в 16.18 выигрывала у Талона
    /// 56%, а в окне 16.20 + 16.19 проигрывает, и движок прав.
    /// </summary>
    private static (double G, double W) Rows(string db, RecommendationEngine engine, string table,
                                             int champ, string role, int? vs = null)
    {
        var patches = engine.PatchDisplay.Split(", ");
        using var con = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}");
        con.Open();
        var cmd = con.CreateCommand();
        var weight = "CASE patch " + string.Join(" ", patches.Select((p, i) => $"WHEN @p{i} THEN {new[] { "1.0", "0.6", "0.3" }[Math.Min(i, 2)]}")) + " END";
        cmd.CommandText = $@"SELECT COALESCE(SUM(games*{weight}),0), COALESCE(SUM(wins*{weight}),0) FROM {table}
                             WHERE champion_id=@c AND role=@r AND tier_bucket=@t
                               {(vs is null ? "" : "AND vs_champion_id=@v")}
                               AND patch IN ({string.Join(",", patches.Select((_, i) => $"@p{i}"))})";
        for (int i = 0; i < patches.Length; i++) cmd.Parameters.AddWithValue($"@p{i}", patches[i]);
        cmd.Parameters.AddWithValue("@c", champ);
        cmd.Parameters.AddWithValue("@r", role);
        cmd.Parameters.AddWithValue("@t", engine.TierBucket);
        if (vs is not null) cmd.Parameters.AddWithValue("@v", vs.Value);
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return (Convert.ToDouble(rd.GetValue(0)), Convert.ToDouble(rd.GetValue(1)));
    }

    /// Драфт: своя роль известна, союзники уже показали пики, врагов нет.
    private static DraftState Draft(IReadOnlyList<int> allies, bool withRole = true)
    {
        // Роли союзникам задаём НАРОЧНО: matchup — это противостояние на линии,
        // и без роли контра складывалась по всем ролям сразу (лесной матчап
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
        var dir = AppPaths.Root;
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
        // С 10.10 подпись называет и пикрейт контры: «Контрит твой пул (Сона) · пик 3,5%».
        var head = Loc.T("reason.countersPoolPick", "", "").Split('')[0];
        return engine.RecommendBans(Draft([157, 64, 103]), top: 10)
                     .SelectMany(b => b.Reasons)
                     .Where(r => r.StartsWith(head, StringComparison.Ordinal))
                     .ToHashSet();
    }

    /// Роль союзника по его месту в составе — в том виде, как её хранит база.
    /// «middle» у клиента и «mid» в базе — разные слова, и путать их нельзя.
    private static string DbRole(int i) => (i % 3) switch { 0 => "top", 1 => "jungle", _ => "mid" };

    private static string Short(string s) => s.Length <= 64 ? s : s[..61] + "…";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
