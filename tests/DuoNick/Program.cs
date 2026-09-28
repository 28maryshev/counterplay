using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Подпись напарника в разделе связок — по всем состояниям, в каких пул бывает.
///
/// Показывает ровно ту строку, что увидит игрок: ник берётся из файла, которым
/// обменялись, иначе из самих связок, а если напарник не опознан — подписи с
/// именем быть не должно вовсе, потому что показаны связки СО ВСЕМИ.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Him = "FR11111111111111111111111111111111111111111111111111111111111111111111111111";

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        // Связки: одна с опознанным напарником, одна со случайным союзником.
        var pairs = new List<SessionTracker.PairStat>
        {
            new(Him,  "Harribon", "flex", 412, 22, 11, 8),
            new("XX", "Случайный", "solo", 89, 51, 1, 0),
        };

        Console.WriteLine("Как подписан раздел в каждом случае:\n");

        // 1. Пул приехал файлом: ник лежит внутри, рядом с puuid.
        Show("пул получен файлом от друга",
             new DuoPool { FriendName = "supports + top", FriendPuuid = Him, FriendNick = "Harribon" },
             pairs, expect: "Harribon");

        // 2. Файла не было, но вместе уже играли — ник взялся из связок.
        Show("напарник опознан в драфте, ника из файла нет",
             new DuoPool { FriendName = "supports + top", FriendPuuid = Him },
             pairs, expect: "Harribon");

        // 3. Пул собран руками: кто напарник — неизвестно.
        Show("пул собран руками, напарник не опознан",
             new DuoPool { FriendName = "supports + top" },
             pairs, expect: "");

        // 4. Опознан, но совместных игр ещё нет — назвать нечем.
        Show("напарник опознан, но связок ещё нет",
             new DuoPool { FriendName = "mid + jungle", FriendPuuid = Him },
             [], expect: "");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: подпись называет человека или молчит"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Show(string what, DuoPool duo,
                             IReadOnlyList<SessionTracker.PairStat> pairs, string expect)
    {
        var nick = DuoNaming.PartnerNick(duo, pairs);

        // Та же развилка, что и в окне.
        var header = nick.Length > 0
            ? Loc.T("pool.duoWinratesWith", nick)
            : Loc.T("pool.duoWinrates");

        Console.WriteLine($"  {what}");
        Console.WriteLine($"    плитка:    «{duo.FriendName}»");
        Console.WriteLine($"    заголовок: «{header}»");
        var ok = nick == expect;
        Console.WriteLine($"    [{(ok ? "ок" : "ПЛОХО")}] ник = «{nick}», ждали «{expect}»\n");
        if (!ok) _fails++;
    }
}
