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

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: бан объясняет, против кого и насколько"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Драфт: своя роль известна, союзники уже показали пики, врагов нет.
    private static DraftState Draft(IReadOnlyList<int> allies)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, "utility", true) };
        for (var i = 0; i < allies.Count; i++)
            team.Add(new DraftPlayer(i + 1, allies[i], 0, "", false));
        return new DraftState(
            MyTeam: team, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: team[0], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: 0, MyBanInProgress: true);
    }

    private static string Short(string s) => s.Length <= 64 ? s : s[..61] + "…";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
