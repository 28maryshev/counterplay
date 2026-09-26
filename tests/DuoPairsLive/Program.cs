using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Винрейт связок на живом клиенте.
///
/// Остальные проверки работают на придуманных данных и отвечают на вопрос
/// «правильно ли считает». Эта отвечает на другой: доходит ли дело до счёта
/// вообще — история читается, подробные игры отдаются, союзники опознаются.
///
/// Клиент не запущен — проверка не падает, а говорит, что проверять нечего:
/// в сборочной машине League не стоит.
///
/// Настоящий session.json сохраняется и возвращается на место, в том числе при
/// падении: у игрока там журнал за сезон.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "session.json");

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        LcuCredentials creds;
        try
        {
            using var cts0 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            creds = await LockfileReader.WaitForAsync(null, cts0.Token);
        }
        catch
        {
            Console.WriteLine("клиент League не запущен — проверять нечего");
            return 0;
        }

        var before = File.Exists(Path_) ? File.ReadAllText(Path_) : null;
        try
        {
            using var http = new LcuHttpClient(creds);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await Run(http, cts.Token);
        }
        finally
        {
            if (before is not null) File.WriteAllText(Path_, before);
            else if (File.Exists(Path_)) File.Delete(Path_);
            Console.WriteLine("\nпрежний session.json возвращён на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: связки считаются на живой истории"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static async Task Run(LcuHttpClient http, CancellationToken ct)
    {
        // Чистый лист: иначе непонятно, набрались связки сейчас или лежали с
        // прошлого запуска программы.
        if (File.Exists(Path_)) File.Delete(Path_);

        var session = await SessionTracker.RefreshAsync(http, ct);
        Check("клиент ответил, кто играет", session is not null, session is null ? "нет" : "да");

        var pairs = SessionTracker.TopPairs(take: 200);
        Check("связки набрались с пустого места", pairs.Count > 0, $"{pairs.Count}");

        if (pairs.Count == 0) return;

        var mates = pairs.Select(p => p.AllyPuuid).Distinct().Count();
        var named = pairs.Count(p => p.AllyName.Length > 0);
        Console.WriteLine($"  связок {pairs.Count}, разных союзников {mates}, "
                          + $"с именем {named}");

        Check("имена союзников читаются", named > 0, $"{named} из {pairs.Count}");
        Check("чемпионы проставлены",
              pairs.All(p => p.MyChampionId != 0 && p.AllyChampionId != 0), "все");
        Check("счёт не превышает числа игр",
              pairs.All(p => p.Wins <= p.Games && p.Games > 0), "да");

        // Ключ собирается и разбирается обратно: puuid содержит '-' и '_', и
        // разбор по разделителю не должен на них спотыкаться.
        var one = pairs[0];
        var (g, w) = SessionTracker.PairStats(one.AllyPuuid, one.MyChampionId, one.AllyChampionId);
        Check("поиск по связке находит её же", g == one.Games && w == one.Wins,
              $"{w}-{g - w} против {one.Wins}-{one.Games - one.Wins}");

        var top = pairs.GroupBy(p => p.AllyPuuid)
                       .OrderByDescending(x => x.Sum(p => p.Games)).First();
        var (mg, mw) = SessionTracker.MateStats(top.Key);
        Check("итог по союзнику сходится с суммой его связок",
              mg == top.Sum(p => p.Games) && mw == top.Sum(p => p.Wins),
              $"{mw}-{mg - mw}");
        Console.WriteLine($"  чаще всего играли с «{top.First().AllyName}»: {mg} игр вместе");
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
