using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Роли в записи связки.
///
/// Дуо-пул собирают под конкретную пару линий, а история связок ролей не знала:
/// «мы вдвоём на этой паре 7-3» складывалось из всех совместных игр — и с бота,
/// и из леса с мидом. Владелец просил считать строго по ролям.
///
/// Разведка на живом клиенте показала, что прямого источника нет. Клиент отдаёт
/// `timeline.lane`/`role`, но полная раскладка сходится лишь в **16%** команд
/// (замер: 19 игр, 38 команд) — топ-лейнеров он сплошь метит джунглерами.
/// Надёжна одна бот-линия: **66%**. Лучшего в LCU нет: `teamPosition`
/// отсутствует, `/timeline` отдаёт 404, в `stats` роли тоже нет.
///
/// Поэтому: бот-линию берём у клиента, остальные три роли достраиваем по доле
/// роли чемпиона. Проверяем именно это — на тех же формах ответа, что пришли с
/// живого клиента.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Yone = 777, Elise = 60, Anivia = 34, Karthus = 30, Thresh = 412;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        DataDragon.LoadFromCache("ru_RU");

        // ── Роли из ответа клиента ─────────────────────────────────────────
        //
        // Форма настоящая, снята с живой истории: клиент назвал ДВУХ джунглеров
        // и не назвал ни одного топа. Бот-линию он при этом знает точно.
        var broken = new (int Pid, int Champ, string Lane, string Role)[]
        {
            (1, Yone,    "JUNGLE", "NONE"),
            (2, Elise,   "JUNGLE", "NONE"),
            (3, Anivia,  "MIDDLE", "SOLO"),
            (4, Karthus, "BOTTOM", "CARRY"),
            (5, Thresh,  "BOTTOM", "SUPPORT"),
        };

        // Без определителя долей известна только бот-линия — и это честно.
        var bare = SessionTracker.RolesOfTeam(broken, null);
        Check("без долей роли — только бот-линия",
              bare.Count == 2 && bare[4] == "adc" && bare[5] == "support", Show(bare));

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  (базы нет — доли роли не проверить)"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");

        var full = SessionTracker.RolesOfTeam(broken, engine.RoleShare);
        Check("бот-линия осталась как сказал клиент",
              full.GetValueOrDefault(4) == "adc" && full.GetValueOrDefault(5) == "support", Show(full));
        Check("остальные роли достроены", full.Count == 5, Show(full));
        Check("и ни одна не повторилась",
              full.Values.Distinct().Count() == full.Count, Show(full));
        Check("двух джунглеров больше нет",
              full.Values.Count(r => r == "jungle") <= 1, Show(full));

        // Пустая команда не должна ничего выдумывать.
        Check("пустой состав — пустые роли",
              SessionTracker.RolesOfTeam([], engine.RoleShare).Count == 0, "да");

        // ── Роли переживают хранение ───────────────────────────────────────
        //
        // Ключ связки — то, чем она живёт годами: ошибка в формате тихо теряет
        // накопленный счёт.
        var key = SessionTracker.PairKey("puuid-1", "solo", 412, 429, "support", "adc");
        var got = SessionTracker.ParsePairKey(key);
        Check("ключ с ролями разбирается обратно",
              got is { Puuid: "puuid-1", Queue: "solo", Mine: 412, His: 429,
                       MyRole: "support", HisRole: "adc" }, key);

        // Старые записи без ролей обязаны читаться по-прежнему: переразметить их
        // нечем, а выбросить — значит потерять всё, что накоплено.
        var old = SessionTracker.PairKey("puuid-1", "solo", 412, 429);
        var oldGot = SessionTracker.ParsePairKey(old);
        Check("старый ключ без ролей всё ещё читается",
              oldGot is { Mine: 412, His: 429, MyRole: "", HisRole: "" }, old);

        Check("мусор отбрасывается",
              SessionTracker.ParsePairKey("ерунда") is null
              && SessionTracker.ParsePairKey("a|b|неЧисло|4") is null, "да");

        return Done();
    }

    private static string Show(Dictionary<int, string> r) =>
        r.Count == 0 ? "пусто" : string.Join(", ", r.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: роли у связок берутся честно и переживают хранение"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
