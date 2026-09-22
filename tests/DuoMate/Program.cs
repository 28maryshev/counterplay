using System.Text;
using System.Text.Json;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Опознание напарника по дуо-пулу. Раньше напарником считался любой союзник,
/// взявший чемпиона из половины друга — у друга с широким пулом так опознавался
/// случайный человек. Теперь смотрим на состав пати.
///
/// Клиент LoL для проверки не нужен: подкладываем такие же JSON, какие присылает
/// клиент, — лобби и сессию чемп-селекта.
/// </summary>
internal static class Program
{
    private const long MeId     = 111;
    private const long FriendId = 222;
    private const long RandomId = 333;

    // Чемпионы: 412 Тхреш (в половине друга), 89 Лcamp; 64 — вне пула.
    private const int FriendPoolChamp = 412;
    private const int OffPoolChamp    = 64;

    private static int _fails;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // ── 1. Играем вдвоём: друг в пати ───────────────────────────────────
        Party.Update(Lobby(MeId, FriendId));
        Check("пати прочиталась", Party.Known && Party.Names.Count == 1, string.Join(",", Party.Names));

        var duo = Duo(FriendPoolChamp);

        // Друг взял чемпиона из своей половины — очевидный случай.
        var draft = Draft((FriendId, FriendPoolChamp), (RandomId, 99));
        Check("друг из пати опознан", Party.MateChampion(draft, duo) == FriendPoolChamp,
              $"{Party.MateChampion(draft, duo)}");

        // Друг взял что-то мимо своего пула — он всё равно напарник.
        draft = Draft((FriendId, OffPoolChamp), (RandomId, 99));
        Check("друг опознан и вне пула", Party.MateChampion(draft, duo) == OffPoolChamp,
              $"{Party.MateChampion(draft, duo)}");

        // ГЛАВНОЕ: чемпиона из половины друга взял ПОСТОРОННИЙ, друга в команде нет.
        draft = Draft((RandomId, FriendPoolChamp));
        Check("чужой с чемпионом из пула друга НЕ считается напарником",
              Party.MateChampion(draft, duo) == 0, $"{Party.MateChampion(draft, duo)}");

        // ── 2. Играем в одиночку: пати пустая ───────────────────────────────
        Party.Update(Lobby(MeId));
        Check("соло-лобби: пати пуста", Party.Known && Party.Names.Count == 0, "");
        draft = Draft((RandomId, FriendPoolChamp));
        Check("в соло напарника нет, даже если кто-то взял чемпиона из пула",
              Party.MateChampion(draft, duo) == 0, $"{Party.MateChampion(draft, duo)}");

        // ── 3. Про пати ничего не знаем (запустились посреди драфта) ────────
        // Раньше тут работал откат по чемпиону — он-то и выдавал чужого человека
        // за напарника. Теперь молчим: неверная пара хуже отсутствующей.
        Forget();
        draft = Draft((RandomId, FriendPoolChamp));
        Check("без сведений о пати пару НЕ предлагаем",
              Party.MateChampion(draft, duo) == 0, $"{Party.MateChampion(draft, duo)}");

        // ── 4. Разбор чемп-селекта доносит, кто есть кто ────────────────────
        var parsed = ChampSelectParser.Parse(Session());
        var friend = parsed.MyTeam.FirstOrDefault(p => p.SummonerId == FriendId);
        Check("summonerId и puuid дочитываются из сессии",
              friend is not null && friend.Puuid == "puuid-222",
              friend is null ? "друга нет в составе" : friend.Puuid);
        Check("свой слот помечен", parsed.MyTeam.Count(p => p.IsLocalPlayer) == 1, "");

        // ── 5. Узнаём по puuid, если summonerId клиент не дал ───────────────
        Party.Update(Lobby(MeId, FriendId));
        var noId = new DraftState(
            [new DraftPlayer(0, 0, 0, "utility", true),
             new DraftPlayer(1, FriendPoolChamp, 0, "bottom", false, 0, "puuid-222")],
            [], [], [], null, "utility", null, false, false, [], false, -1, false, [], -1, -1, false);
        Check("напарник узнаётся и по одному puuid",
              Party.MateChampion(noId, duo) == FriendPoolChamp, $"{Party.MateChampion(noId, duo)}");

        // ── 6. Ник клиент дал не всегда, а пати всё равно непустая ─────────
        Forget();
        Party.Update(Json($$"""
            {"localMember":{"summonerId":{{MeId}}},
             "members":[{"summonerId":{{MeId}}},{"summonerId":{{FriendId}}}]}
            """));
        draft = Draft((FriendId, OffPoolChamp));
        Check("пати без ников всё равно опознаётся",
              Party.Known && Party.MateChampion(draft, duo) == OffPoolChamp,
              $"{Party.MateChampion(draft, duo)}");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: напарник определяется верно" : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── подставные ответы клиента ─────────────────────────────────────────

    /// Лобби: первый — я, остальные — сопартийцы.
    private static JsonElement Lobby(long me, params long[] mates)
    {
        var members = string.Join(",", new[] { me }.Concat(mates).Select(
            id => $$"""{"summonerId":{{id}},"puuid":"puuid-{{id}}","gameName":"Игрок{{id}}","tagLine":"RU"}"""));
        return Json($$"""
            {"localMember":{"summonerId":{{me}},"puuid":"puuid-{{me}}"},"members":[{{members}}]}
            """);
    }

    /// Драфт: я на саппорте плюс перечисленные союзники.
    private static DraftState Draft(params (long Id, int Champ)[] allies)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, "utility", true, MeId, $"puuid-{MeId}") };
        var cell = 1;
        foreach (var (id, champ) in allies)
            team.Add(new DraftPlayer(cell++, champ, 0, "", false, id, $"puuid-{id}"));
        return new DraftState(team, [], [], [], team[0], "utility", null,
                              false, false, [], false, -1, false, [], -1, -1, false);
    }

    private static DuoPool Duo(int friendChamp) => new()
    {
        Id = "t", FriendName = "друг",
        Mine   = new() { ["support"] = [201] },
        Friend = new() { ["adc"] = [friendChamp] },
    };

    /// Сессия чемп-селекта — проверяем, что разбор достаёт summonerId/puuid.
    private static JsonElement Session() => Json($$"""
        {"localPlayerCellId":0,
         "myTeam":[
           {"cellId":0,"championId":0,"championPickIntent":0,"assignedPosition":"utility",
            "summonerId":{{MeId}},"puuid":"puuid-{{MeId}}"},
           {"cellId":1,"championId":{{FriendPoolChamp}},"championPickIntent":0,"assignedPosition":"bottom",
            "summonerId":{{FriendId}},"puuid":"puuid-{{FriendId}}"}],
         "theirTeam":[],"actions":[]}
        """);

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    /// Вернуть состояние «про пати ничего не известно». Отдельного метода в Party
    /// нет намеренно — забывать состав в бою нельзя, поэтому здесь лезем отражением.
    private static void Forget()
    {
        var t = typeof(Party);
        foreach (var f in new[] { "Ids", "Puuids" })
            (t.GetField(f, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                 .GetValue(null) as System.Collections.IList)?.Clear();
        t.GetProperty("Known")!.SetValue(null, false);
        t.GetField("_lastLogged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, -1);
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
