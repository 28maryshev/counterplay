using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Связка с союзником доходит до карточки как довод «за».
///
/// Владелец навёл Мальфита при Ясуо в команде и не увидел ни слова про подброс
/// с ультом. Причина нашлась в двух местах сразу:
///
///   1. Движок добавлял подпись о связке БЕЗ знака, а отбор доводов в
///      интерфейсе первым делом выбрасывает нейтральные — «не говорят ни за,
///      ни против». Связка отсеивалась даже не как общая.
///   2. Со знаком её съел бы следующий отсев: текст дословно одинаков у всех
///      подкидывающих кандидатов (Мальфит, Грагас, Чо'Гат), а общими считаются
///      доводы, встречающиеся у 60% списка.
///
/// Проверяется ПЕРВОЕ — то, что делает движок: подпись есть и помечена как
/// несменяемый довод «за». Второе (исключение для таких доводов при отсеве
/// общих) проверкой не покрыто: рисование карточек оказалось не по зубам —
/// строки доводов не достаются из дерева окна. Это записано и в журнале.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Malphite = 54, Yasuo = 157;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        // Справочник Riot нужен для дальности автоатаки: без него все чемпионы
        // выглядят ближними, и проверка ниже прошла бы впустую.
        if (!DataDragon.LoadFromCache("ru_RU"))
            Console.WriteLine("  (справочника чемпионов нет — дальность не проверяем)");
        else

        // ── «Бьёт с дистанции» говорим только про дальних ──────────────────
        //
        // Владелец: «Люкс и Насус. Насус не бьёт с дистанции». И правда:
        // правило «инициатор + дальний урон» принимало тег `scale`, а он про
        // позднюю игру, а не про дальность. У Насуса 125 единиц — палка в упор.
        //
        // Тег `poke` при этом дальностью НЕ проверяется: он авторский и значит
        // размен умениями (Гангпланк, Гнар, Джейс бьют в упор, а достают
        // издалека), так что проверка это тоже стережёт.
        {
        foreach (var (a, b, want) in new[]
                 {
                     (99, 75,  false),  // Люкс + Насус — Насус в упор
                     (54, 11,  false),  // Мальфит + Мастер Йи
                     (54, 222, true),   // Мальфит + Джинкс — вот это дальний
                     (89, 96,  true),   // Леона + Ког'Мао
                 })
        {
            var link = TeamSynergies.ExplainPair(a, b, "");
            Check($"«с дистанции» {(want ? "есть" : "нет")}: {DataDragon.Name(a)} + {DataDragon.Name(b)}",
                  (link?.Kind == "frontline") == want, link?.Text ?? "связки нет");
        }

        // Ни один ближний не должен попадать в эту связку через `scale`.
        var wrong = DataDragon.GetAllIconUrls().Keys
            .Where(id => !DataDragon.IsRanged(id) && !ChampionTags.Has(id, "poke")
                         && TeamSynergies.ExplainPair(54, id, "")?.Kind == "frontline")
            .Select(DataDragon.Name).ToList();
        Check("ближних в «бьёт с дистанции» нет", wrong.Count == 0,
              wrong.Count == 0 ? "да" : string.Join(", ", wrong.Take(6)));
        }

        // ── Сама связка распознаётся ───────────────────────────────────────
        var pair = TeamSynergies.ExplainPair(Malphite, Yasuo, "mid");
        Check("связка Мальфит+Ясуо распознана", pair is not null, pair?.Kind ?? "нет");
        if (pair is null) return Done();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — дальше проверять нечего"); return Done(); }

        using var engine = RecommendationEngine.Create(db, "emerald");
        var recs = engine.Recommend(Draft(), 20);

        var malph = recs.FirstOrDefault(r => r.ChampionId == Malphite);
        Check("Мальфит попал в подбор", malph is not null,
              malph is null ? "нет" : $"оценка {malph.Score:F1}");
        if (malph is null) return Done();

        var line = malph.Reasons.FirstOrDefault(r => r.Contains("ультует") || r.Contains("подброс"));
        Check("в доводах есть связка с Ясуо", line is not null,
              line is null ? string.Join(" | ", malph.Reasons) : line[1..]);
        if (line is null) return Done();

        // Цифра: остальные доводы в карточке её приводят, и связка без неё
        // выглядела голословной — «выгодно», а насколько, непонятно.
        var num = System.Text.RegularExpressions.Regex.Match(line, @"\(([+-]\d+[.,]\d)%\)");
        Check("рядом стоит измеренная прибавка", num.Success, num.Success ? num.Groups[1].Value : "цифры нет");
        if (num.Success)
            Check("   и она положительная", !num.Groups[1].Value.StartsWith('-'), num.Groups[1].Value);

        // Знак решает судьбу строки в интерфейсе: без него её выбрасывают как
        // нейтральную, а с обычным «за» — как общую для многих кандидатов.
        Check("помечена как несменяемый довод",
              line[0] == RecommendationEngine.SIGN_KEY,
              line[0] == RecommendationEngine.SIGN_KEY  ? "да"
              : line[0] == RecommendationEngine.SIGN_GOOD ? "нет, обычное «за»"
              : line[0] == RecommendationEngine.SIGN_BAD  ? "нет, «против»" : "нет, без знака");

        // ── Кружок у союзника считает то же, что и подпись ────────────────
        //
        // Кружок и цифра под портретом союзника берутся из PairSynergy, а она
        // смотрела синергию ПО ВСЕМ РОЛЯМ сразу. У Мальфита пара с Ясуо на топе
        // даёт +1,26 на 8034 играх, а вместе с его лесом и саппортом, где пара
        // проигрывает, выходит −0,22 — ниже порога в 0,3, и кружок не зажигался.
        // В карточке при этом стоял плюс: она считает по роли.
        //
        // Роли ОБЕ: своя и союзника. Ясуо в этом драфте на миде, и пара
        // «Мальфит-топ + Ясуо-мид» — не то же, что сумма всех его линий.
        // Сайдбар передаёт сюда ровно эту роль, поэтому и проверка передаёт.
        var ring = engine.PairSynergy(Malphite, "top", Yasuo, "mid");
        Check("связка видна и кружку союзника", ring >= 0.3, $"{ring:+0.00;-0.00}");

        // И это должно быть ТО ЖЕ число, что в подписи, иначе они спорят.
        var said = double.Parse(num.Groups[1].Value.Replace(',', '.'),
                                System.Globalization.CultureInfo.InvariantCulture);
        Check("   и совпадает с числом в подписи", Math.Abs(ring - said) < 0.1,
              $"кружок {ring:0.0}, подпись {said:0.0}");

        // ── Кружки у ВРАГОВ тоже считают по ролям ─────────────────────────
        //
        // Число у вражеского портрета берётся из VersusDelta. Там роль врага
        // участвует: сперва кросс-ролевая таблица (моя роль против ЕГО роли),
        // и лишь если по паре нет данных — откат на противостояние по линии.
        // Проверяем по последствию: тот же враг, названный мидером и топером,
        // обязан дать разные числа.
        //
        // Пара выбрана НАРОЧНО: Йоне(топ) против Зеда — кросс top-mid 4992 игры,
        // а same-role top всего 416. Числа обязаны разойтись. У Мальфита с Ясуо
        // они случайно совпали (Ясуо и на топе встречается), и проверка на такой
        // паре ничего бы не значила.
        const int Yone = 777, Zed = 238;
        var asMid = engine.VersusDelta(Yone, "top", Zed, "mid");
        var asTop = engine.VersusDelta(Yone, "top", Zed, "top");
        Check("роль врага меняет его число", Math.Abs(asMid - asTop) > 0.05,
              $"против мидера {asMid:+0.00;-0.00}, против топера {asTop:+0.00;-0.00}");

        // Роль неизвестна (блайнд, соло-дуо) — молчать нельзя, берём по линии.
        var unknown = engine.VersusDelta(Yone, "top", Zed, "");
        Check("   без роли откат на противостояние по линии",
              Math.Abs(unknown - asTop) < 0.001, $"{unknown:+0.00;-0.00}");

        return Done();
    }

    /// Драфт: я на топе, Ясуо в миду, вражеский топ известен.
    private static DraftState Draft()
    {
        var me    = new DraftPlayer(0, 0, 0, "top", true);
        var yasuo = new DraftPlayer(1, Yasuo, 0, "middle", false);
        var enemy = new DraftPlayer(5, 23, 0, "top", false);   // Триндамир
        return new DraftState(
            MyTeam: [me, yasuo], TheirTeam: [enemy], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "top", DirectOpponent: enemy, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: связка доходит до карточки как довод «за»"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
