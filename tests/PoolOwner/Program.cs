using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Пул помнит хозяев.
///
/// Файл возит с собой puuid того, кто его отдал. Загрузив личный пул друга в
/// половину «Дуо», мы сразу знаем, ЧЕЙ это набор, и дальше ищем в команде
/// именно его — а не первого попавшегося сопартийца. Это и есть разница между
/// «играем вдвоём» и «играем впятером, и среди них он».
///
/// Работаем с НАСТОЯЩИМ pools.json: PoolStore другого пути не даёт. Прежнее
/// содержимое сохраняем и возвращаем, даже если проверка упала.
/// </summary>
internal static class Program
{
    private static int _fails;

    // 78 знаков, как у настоящих: важно, что длину никто не режет по дороге.
    private const string Me     = "ME00000000000000000000000000000000000000000000000000000000000000000000000000";
    private const string Friend = "FR11111111111111111111111111111111111111111111111111111111111111111111111111";
    private const string Third  = "TH22222222222222222222222222222222222222222222222222222222222222222222222222";

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "pools.json");

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        var existed = File.Exists(Path_);
        var before = existed ? File.ReadAllText(Path_) : null;
        try
        {
            if (existed) File.Delete(Path_);
            Run();
        }
        finally
        {
            if (before is not null) File.WriteAllText(Path_, before);
            else if (File.Exists(Path_)) File.Delete(Path_);
            Console.WriteLine(existed ? "\nпрежний pools.json возвращён на место"
                                      : "\nсвоего pools.json не было — временный убран");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: пул помнит хозяев"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        // ── Друг выгружает свой ЛИЧНЫЙ пул ──────────────────────────────────
        PoolStore.SetAccount(Friend, "Harribon");
        var his = new ChampPool { Id = "h1", Name = "Harribon" };
        his.ByRole["support"] = [412, 89];
        var file = PoolFile.Export(his);
        Check("в файле есть хозяин", file.Contains(Friend), Head(file));

        // ── Я загружаю его себе ─────────────────────────────────────────────
        PoolStore.SetAccount(Me, "я");
        var (pool, _, owner) = PoolFile.ParseWithOwner(file);
        Check("пул прочитался", pool is not null, pool?.Name ?? "—");
        Check("хозяин прочитан целиком", owner == Friend, $"{owner.Length} знаков");

        // ── Дуо-файл: хозяин + его напарник ─────────────────────────────────
        PoolStore.SetAccount(Friend, "Harribon");
        var duoOfHis = new DuoPool { Id = "d1", FriendName = "я", FriendPuuid = Me };
        duoOfHis.Mine["support"] = [412];
        duoOfHis.Friend["adc"] = [22];
        var duoFile = PoolFile.Export(duoOfHis);

        // Его напарник — это Я. Значит для меня второй половиной становится ОН.
        PoolStore.SetAccount(Me, "я");
        var (_, mineNow, _) = PoolFile.ParseWithOwner(duoFile);
        Check("зеркальный случай: хозяином стал отправитель",
              mineNow?.FriendPuuid == Friend, Tail(mineNow?.FriendPuuid));

        // Тот же файл у постороннего: связка чужая, второй половиной остаётся
        // тот, кто в ней записан.
        PoolStore.SetAccount(Third, "третий");
        var (_, forThird, _) = PoolFile.ParseWithOwner(duoFile);
        Check("у постороннего половина не подменяется",
              forThird?.FriendPuuid == Me, Tail(forThird?.FriendPuuid));

        // ── Мусор вместо puuid не должен просочиться ────────────────────────
        var junk = file.Replace(Friend, "не-пуид-а-ерунда\nс переносом");
        var (_, _, bad) = PoolFile.ParseWithOwner(junk);
        Check("подделанный puuid отброшен", bad.Length == 0, bad.Length == 0 ? "пусто" : bad);

        // ── Старый файл без хозяина ─────────────────────────────────────────
        var old = """
        {"Format":"counterplay-pool","Version":1,"Kind":"pool","Name":"OLD",
         "ByRole":{"mid":[1,2,3]}}
        """;
        var (oldPool, _, noOwner) = PoolFile.ParseWithOwner(old);
        Check("старый файл грузится", oldPool is not null, oldPool?.Name ?? "—");
        Check("хозяина в нём нет, и это не ошибка", noOwner.Length == 0, "пусто");

        FiveStack();
    }

    /// <summary>
    /// Игра впятером. В команде четыре союзника, и только один из них — хозяин
    /// второй половины пула. Пара обязана строиться вокруг него, а не вокруг
    /// первого попавшегося: ради этого puuid в файле и возится.
    /// </summary>
    private static void FiveStack()
    {
        PoolStore.SetAccount(Me, "я");
        var duo = new DuoPool { Id = "dx", FriendName = "Harribon", FriendPuuid = Friend };

        const int HisChamp = 412, Other = 64;
        var team = new List<DraftPlayer>
        {
            new(0, 99, 0, "mid", true,  Puuid: Me),
            new(1, Other, 0, "top", false, Puuid: "AAA" + new string('0', 73)),
            new(2, HisChamp, 0, "support", false, Puuid: Friend),   // он
            new(3, Other, 0, "jungle", false, Puuid: "BBB" + new string('0', 73)),
            new(4, Other, 0, "adc", false, Puuid: "CCC" + new string('0', 73)),
        };
        var state = Draft(team);

        var champ = Party.MateChampion(state, duo);
        Check("впятером пара строится вокруг хозяина пула", champ == HisChamp,
              $"{champ} (ждали {HisChamp})");

        // Его в игре нет — подставлять вместо него случайного союзника нельзя.
        var without = Draft(team.Where(p => p.Puuid != Friend).ToList());
        var none = Party.MateChampion(without, duo);
        Check("без хозяина пара не предлагается", none == 0, none.ToString());
    }

    private static DraftState Draft(IReadOnlyList<DraftPlayer> myTeam) => new(
        MyTeam: myTeam, TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
        Me: myTeam.FirstOrDefault(p => p.IsLocalPlayer), MyPosition: "mid",
        DirectOpponent: null, ExposedToCounter: false, InBanPhase: false,
        Bench: [], IsAram: false, MyPickActionId: -1, MyPickInProgress: false,
        ActiveCells: [], FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);

    private static string Head(string s) => s.Length > 40 ? s[..40] + "…" : s;
    private static string Tail(string? s) =>
        string.IsNullOrEmpty(s) ? "—" : s[..2] + "…(" + s.Length + ")";

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
