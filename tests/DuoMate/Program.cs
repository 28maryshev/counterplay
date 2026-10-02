using System.IO;
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

        // ── 7. Человек и чемпион — два РАЗНЫХ условия ──────────────────────
        //
        // Опознать напарника — ещё не повод подставлять связку под ЕГО чемпиона.
        // `FriendHas` отвечает на один вопрос: лежит ли взятое им в его половине.
        // Пара задумана под конкретных, и подставлять её под случайный пик друга
        // нечестно. Это разъезжалось: человека нашли — и показывали связку с чем
        // угодно, что он взял.
        //
        // Молчание из этого НЕ следует. Состояний три, и они разные: взял из
        // половины — подбираем под него; взял мимо — связки убираем совсем; ещё
        // ничего не показал — предлагаем свою половину, не дожидаясь его. Калитка
        // в оверлее стоит поэтому на промахе, а не на пустом пике (пункт 8).
        Party.Update(Lobby(MeId, FriendId));
        draft = Draft((FriendId, OffPoolChamp));
        Check("друг опознан как ЧЕЛОВЕК даже с чемпионом вне половины",
              Party.MateChampion(draft, duo) == OffPoolChamp, $"{Party.MateChampion(draft, duo)}");
        Check("но связка при этом НЕ срабатывает",
              !duo.FriendHas(OffPoolChamp), "");
        Check("взял из своей половины — срабатывает",
              duo.FriendHas(FriendPoolChamp), "");
        Check("роль в половине не обязана совпадать с его линией",
              duo.FriendHas(FriendPoolChamp), "чемпион лежит под adc, друг на боте");
        Check("пустой пик в половине не числится", !duo.FriendHas(0), "мимо половины и мимо пар");

        // Фиксированные связки: половин у них нет, чемпионы друга живут в парах.
        // Сторона в паре любая — роли разбираются уже при показе.
        var fixedDuo = new DuoPool
        {
            Id = "f", FriendName = "фикс", Manual = true,
            ManualPairs = [new ManualDuoPair { Mine = 201, MineRole = "support",
                                               Friend = FriendPoolChamp, FriendRole = "adc" }],
        };
        Check("фикс-связка: чемпион из пары срабатывает",
              fixedDuo.FriendHas(FriendPoolChamp), "");
        Check("фикс-связка: чемпион вне пар не срабатывает",
              !fixedDuo.FriendHas(OffPoolChamp), "");

        // ── 8. Калитка стоит в самом оверлее ──────────────────────────────
        //
        // Проверки выше гоняют модель, а разъехался в тот раз именно оверлей:
        // в модели было написано «авто-подсказка ждёт чемпиона из половины»,
        // а показывал он связку от любого пика напарника. Поэтому смотрим в
        // исходник — иначе условие снова можно убрать, и всё останется зелёным.
        var root = FindRepoRoot();
        if (root is null) Check("корень репозитория найден", false, "не найден");
        else
        {
            var src = File.ReadAllText(Path.Combine(root, "src", "Ui", "OverlayWindow.xaml.cs"),
                                       Encoding.UTF8);
            var body = Code(src);
            Check("оверлей сверяет пик напарника с его половиной",
                  body.Contains("FriendHas(mateTaken)"), "FriendHas(mateTaken)");
            Check("и при промахе гасит пару, а не показывает связку",
                  body.Contains("if (mateOffPool)") && body.Contains("mateTaken = 0;"),
                  "mateOffPool + mateTaken = 0");
            // «Промахнулся» и «ещё ничего не показал» — разные состояния, и
            // раньше их путало обнуление mateTaken: оба выходили молчанием.
            // Теперь калитка стоит на флаге промаха, а пустой напарник показу не
            // мешает — иначе дуо-пул снова замолчит там, где подсказка нужнее
            // всего: напарник пикает после меня, и к моему ходу у него пусто.
            Check("а показа связок пик напарника НЕ дожидается",
                  body.Contains("if (duo != null && !mateOffPool)")
                  && !body.Contains("if (mateTaken != 0 && duo != null)"),
                  "калитка по mateOffPool, а не по mateTaken");
            // Мало не ждать его пика — надо ещё предложить, что взять ЕМУ, иначе
            // «узнать заранее, что пикать обоим» не работает: карточка покажет
            // только мой пик.
            Check("и сам предлагает, кого взять напарнику",
                  body.Contains("BestPartner("), "BestPartner(...)");
        }

        // ── 9. В списке напарников — не все подряд ────────────────────────
        //
        // Связки копятся на ВСЕХ союзников и во всех очередях, кроме ARAM. В
        // флексе и нормалах их четверо за игру, и список «напарников» разрастался
        // до всех, с кем вообще доводилось играть: у владельца 182 человека.
        // Напарник по дуо-пулу — тот, с кем встаёшь в очередь соло/дуо.
        SessionTracker.Preview =
        [
            // с кем правда стоим в соло/дуо — три игры вместе
            Pair("duo-0001", "Напарник", "solo", 2),
            Pair("duo-0001", "Напарник", "solo", 1),
            // случайный союзник из соло/дуо — одна игра, совпадение
            Pair("rnd-0002", "Случайный", "solo", 1),
            // флекс и нормалы: союзников четверо, напарником никто из них не стал
            Pair("flx-0003", "Флекс", "flex", 9),
            Pair("nrm-0004", "Нормал", "normal", 7),
        ];
        var list = DuoNaming.Mates();
        Check("из флекса и нормалов в список никто не попал",
              list.All(m => m.Puuid is not ("flx-0003" or "nrm-0004")),
              string.Join(", ", list.Select(m => $"{m.Name}·{m.Games}")));
        Check("случайный союзник одной игры — не напарник",
              list.All(m => m.Puuid != "rnd-0002"), "");
        Check("тот, с кем играли не раз, в списке есть",
              list.Any(m => m.Puuid == "duo-0001" && m.Games == 3), "");
        SessionTracker.Preview = null;

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

    /// Подставная связка: с кем, в какой очереди и сколько игр.
    private static SessionTracker.PairStat Pair(string puuid, string name, string queue, int games) =>
        new(puuid, name, queue, 412, 429, games, games);

    /// Корень репозитория: от рабочей папки вверх до Counterplay.csproj.
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Counterplay.csproj")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    /// Код без комментариев: упоминание условия в пояснении — не условие.
    private static string Code(string src)
    {
        var sb = new StringBuilder();
        foreach (var line in src.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//") || t.StartsWith("///")) continue;
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
