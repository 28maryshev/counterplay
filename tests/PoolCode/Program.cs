using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Код передачи пула — тот же пул одной строкой, которую можно кинуть в
/// мессенджер. Проверяем круговой обход, длину (её человеку копировать) и то,
/// что по дороге строку можно потрепать: мессенджеры ломают длинные строки
/// переносами, а копируют её обычно «с запасом».
/// </summary>
internal static class Program
{
    private static int _fails;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ── обычный пул ────────────────────────────────────────────────────
        var pool = new ChampPool
        {
            Name = "Саппорты",
            ByRole = new() { ["support"] = [412, 89, 16, 26, 201], ["adc"] = [22, 51] },
        };
        var json = PoolFile.Export(pool);
        var code = PoolFile.ToCode(json);
        Console.WriteLine($"пул из 7 чемпионов: JSON {json.Length} знаков → код {code.Length}");

        var back = PoolFile.FromCode(code);
        var (p2, _) = PoolFile.Parse(back ?? "");
        Check("пул вернулся из кода",
              p2 is not null && p2.Name == pool.Name && p2.ByRole["support"].Count == 5,
              p2 is null ? "не разобрался" : p2.Name);
        Check("код короче, чем сам JSON", code.Length < json.Length, $"{code.Length} против {json.Length}");
        Check("код влезает в одно сообщение (до 900 знаков)", code.Length < 900, $"{code.Length}");

        // ── дуо ────────────────────────────────────────────────────────────
        var duo = new DuoPool
        {
            FriendName = "Ваня", Manual = true,
            Mine = new() { ["support"] = [412] },
            Friend = new() { ["adc"] = [22] },
            ManualPairs = [new ManualDuoPair { Mine = 412, MineRole = "support", Friend = 22, FriendRole = "adc" }],
        };
        var (_, d2) = PoolFile.Parse(PoolFile.FromCode(PoolFile.ToCode(PoolFile.Export(duo))) ?? "");
        Check("дуо вернулось из кода",
              d2 is not null && d2.FriendName == "Ваня" && d2.Manual && d2.ManualPairs.Count == 1,
              d2?.FriendName ?? "не разобралось");

        // ── что делает с кодом жизнь ───────────────────────────────────────
        Check("перенос строки посередине не мешает",
              Same(code, Insert(code, "\r\n", code.Length / 2)), "");
        Check("пробелы по краям не мешают", Same(code, $"  {code}  \n"), "");
        Check("приписка перед кодом не мешает",
              Same(code, $"лови мой пул: {code}"), "");

        Check("чужой текст отвергается", PoolFile.FromCode("привет, как дела") is null, "");
        Check("пустая строка отвергается", PoolFile.FromCode("") is null, "");
        Check("обрезанный код отвергается", PoolFile.FromCode(code[..(code.Length / 2)]) is null, "");
        Check("код с испорченной серединой отвергается",
              PoolFile.FromCode(Insert(code.Remove(code.Length / 2, 8), "ZZZZZZZZ", code.Length / 2)) is null
              || PoolFile.Parse(PoolFile.FromCode(
                     Insert(code.Remove(code.Length / 2, 8), "ZZZZZZZZ", code.Length / 2)) ?? "") is (null, null),
              "");

        Check("свой код узнаётся", PoolFile.LooksLikeCode(code), "");
        Check("чужой текст не узнаётся", !PoolFile.LooksLikeCode("просто текст"), "");

        // ── большой пул: 5 ролей по 20 чемпионов ───────────────────────────
        var big = new ChampPool { Name = "Всё подряд", ByRole = new() };
        var id = 1;
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
            big.ByRole[role] = Enumerable.Range(id, 20).Select(x => id++ + x % 3).ToList();
        var bigCode = PoolFile.ToCode(PoolFile.Export(big));
        Console.WriteLine($"пул из 100 чемпионов: код {bigCode.Length} знаков");
        Check("даже большой пул влезает в сообщение", bigCode.Length < 1500, $"{bigCode.Length}");
        Check("большой пул возвращается целиком",
              PoolFile.Parse(PoolFile.FromCode(bigCode) ?? "") is ({ } bp, _)
              && bp.ByRole.Values.Sum(l => l.Count) == big.ByRole.Values.Sum(l => l.Count), "");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: код передачи работает" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static string Insert(string s, string what, int at) => s[..at] + what + s[at..];

    private static bool Same(string original, string mangled) =>
        PoolFile.FromCode(mangled) is { } a && PoolFile.FromCode(original) is { } b && a == b;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
