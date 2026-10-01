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

        /// Файл пулов ПРОВЕРКИ, не игрока. Здесь стоял настоящий `%APPDATA%`, и
    /// проверки его удаляли «на время» — так у владельца и пропали пулы.
    private static string Path_ => System.IO.Path.Combine(PoolStore.DirOverride!, "pools.json");

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        // Пулы — в свою папку, НЕ в файл игрока. Проверки однажды уже оставили
        // в его pools.json тестовые аккаунты, а свои пулы — пустыми.
        PoolStore.DirOverride = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "counterplay-test-pools", "PoolOwner");
        System.IO.Directory.CreateDirectory(PoolStore.DirOverride);
        // И начинаем с ЧИСТОГО листа: состояние, оставшееся от прошлого
        // запуска, делает проверку неповторяемой. SetFavourite, например,
        // переключает — со звездой от прошлого раза он её гасит, и проверка
        // падает через раз.
        var stale = System.IO.Path.Combine(PoolStore.DirOverride, "pools.json");
        if (System.IO.File.Exists(stale)) System.IO.File.Delete(stale);
        PoolStore.Reload();


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
        var (pool, _, owner, ownerNick) = PoolFile.ParseWithOwner(file);
        Check("пул прочитался", pool is not null, pool?.Name ?? "—");
        Check("хозяин прочитан целиком", owner == Friend, $"{owner.Length} знаков");
        Check("ник хозяина приехал", ownerNick == "Harribon", ownerNick);

        // ── Дуо-файл: хозяин + его напарник ─────────────────────────────────
        PoolStore.SetAccount(Friend, "Harribon");
        var duoOfHis = new DuoPool { Id = "d1", FriendName = "я", FriendPuuid = Me };
        duoOfHis.Mine["support"] = [412];
        duoOfHis.Friend["adc"] = [22];
        var duoFile = PoolFile.Export(duoOfHis);

        // Его напарник — это Я. Значит для меня второй половиной становится ОН.
        PoolStore.SetAccount(Me, "я");
        var (_, mineNow, _, _) = PoolFile.ParseWithOwner(duoFile);
        Check("зеркальный случай: хозяином стал отправитель",
              mineNow?.FriendPuuid == Friend, Tail(mineNow?.FriendPuuid));

        // Тот же файл у постороннего: связка чужая, второй половиной остаётся
        // тот, кто в ней записан.
        PoolStore.SetAccount(Third, "третий");
        var (_, forThird, _, _) = PoolFile.ParseWithOwner(duoFile);
        Check("у постороннего половина не подменяется",
              forThird?.FriendPuuid == Me, Tail(forThird?.FriendPuuid));

        // ── Мусор вместо puuid не должен просочиться ────────────────────────
        var junk = file.Replace(Friend, "не-пуид-а-ерунда\nс переносом");
        var (_, _, bad, _) = PoolFile.ParseWithOwner(junk);
        Check("подделанный puuid отброшен", bad.Length == 0, bad.Length == 0 ? "пусто" : bad);

        // ── Старый файл без хозяина ─────────────────────────────────────────
        var old = """
        {"Format":"counterplay-pool","Version":1,"Kind":"pool","Name":"OLD",
         "ByRole":{"mid":[1,2,3]}}
        """;
        var (oldPool, _, noOwner, _) = PoolFile.ParseWithOwner(old);
        Check("старый файл грузится", oldPool is not null, oldPool?.Name ?? "—");
        Check("хозяина в нём нет, и это не ошибка", noOwner.Length == 0, "пусто");

        FiveStack();
        SandboxPair();
        NickTravels();
    }

    /// <summary>
    /// Имя напарника доезжает получателю — иначе плитка у него молчит, хотя
    /// человек известен отправителю.
    ///
    /// Проверяем три пути: личный пул в половину «Дуо», готовый дуо-файл и тот
    /// же дуо-файл кодом. Имя обязано встать под ТУ ЖЕ половину, что и puuid,
    /// иначе на плитке будет один человек, а считаться будет другой.
    /// </summary>
    private static void NickTravels()
    {
        // Друг выгружает свой личный пул.
        PoolStore.SetAccount(Friend, "Harribon");
        var his = new ChampPool { Id = "h2", Name = "поддержка" };
        his.ByRole["support"] = [412];
        var poolFile = PoolFile.Export(his);

        PoolStore.SetAccount(Me, "я");
        var (_, _, _, nick1) = PoolFile.ParseWithOwner(poolFile);
        Check("личный пул везёт имя хозяина", nick1 == "Harribon", nick1);

        // Друг выгружает готовый дуо-пул, где вторая половина — Я.
        PoolStore.SetAccount(Friend, "Harribon");
        var duo = new DuoPool { Id = "d9", FriendName = "я + он", FriendPuuid = Me, FriendNick = "я" };
        duo.Mine["support"] = [412];
        duo.Friend["adc"] = [22];
        var duoFile = PoolFile.Export(duo);

        // У МЕНЯ стороны зеркальны: второй половиной становится отправитель.
        PoolStore.SetAccount(Me, "я");
        var (_, mine, _, _) = PoolFile.ParseWithOwner(duoFile);
        Check("дуо-файл: имя встало под отправителя", mine?.FriendNick == "Harribon",
              mine?.FriendNick ?? "(пусто)");
        Check("и puuid под него же", mine?.FriendPuuid == Friend, Tail(mine?.FriendPuuid));

        // Тот же файл кодом — формат один, значит и имя то же.
        var code = PoolFile.ToCode(duoFile);
        var back = PoolFile.FromCode(code);
        var (_, byCode, _, _) = PoolFile.ParseWithOwner(back!);
        Check("кодом имя доезжает так же", byCode?.FriendNick == "Harribon",
              byCode?.FriendNick ?? "(пусто)");

        // Посторонний: связка чужая, имя остаётся тем, что записано.
        PoolStore.SetAccount(Third, "третий");
        var (_, forThird, _, _) = PoolFile.ParseWithOwner(duoFile);
        Check("у постороннего имя не подменяется", forThird?.FriendNick == "я",
              forThird?.FriendNick ?? "(пусто)");
    }

    /// <summary>
    /// Песочница. Пати там нет и быть не может — лобби никто не открывал,
    /// puuid у выдуманных союзников пустые. Связка всё равно должна строиться:
    /// напарник тот, кто взял чемпиона из половины друга.
    ///
    /// В бою это правило убрано намеренно (посторонний, случайно взявший
    /// чемпиона из пула, становился «напарником»), поэтому проверяем и что вне
    /// песочницы оно молчит.
    /// </summary>
    private static void SandboxPair()
    {
        PoolStore.SetAccount(Me, "я");
        var duo = new DuoPool { Id = "ds", FriendName = "sandbox" };
        duo.Friend["support"] = [412, 89];      // половина друга
        duo.Mine["adc"] = [22];

        const int HisChamp = 412, Other = 64;
        var team = new List<DraftPlayer>
        {
            new(0, 22, 0, "adc", true),                 // я, без puuid — как в песочнице
            new(1, Other, 0, "top", false),
            new(2, HisChamp, 0, "support", false),      // взял из половины друга
            new(3, Other, 0, "jungle", false),
        };
        var state = Draft(team);

        Party.Sandbox = false;
        Check("вне песочницы пара по чемпиону НЕ строится",
              Party.MateChampion(state, duo) == 0, Party.MateChampion(state, duo).ToString());

        Party.Sandbox = true;
        try
        {
            var champ = Party.MateChampion(state, duo);
            Check("в песочнице напарник найден по половине друга", champ == HisChamp,
                  $"{champ} (ждали {HisChamp})");

            // Никто не взял из половины друга — пары нет, а не случайный союзник.
            var none = Draft(team.Where(p => p.ChampionId != HisChamp).ToList());
            Check("чужих в напарники не берём", Party.MateChampion(none, duo) == 0,
                  Party.MateChampion(none, duo).ToString());
        }
        finally { Party.Sandbox = false; }
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
