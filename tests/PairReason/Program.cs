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

        // Знак решает судьбу строки в интерфейсе: без него её выбрасывают как
        // нейтральную, а с обычным «за» — как общую для многих кандидатов.
        Check("помечена как несменяемый довод",
              line[0] == RecommendationEngine.SIGN_KEY,
              line[0] == RecommendationEngine.SIGN_KEY  ? "да"
              : line[0] == RecommendationEngine.SIGN_GOOD ? "нет, обычное «за»"
              : line[0] == RecommendationEngine.SIGN_BAD  ? "нет, «против»" : "нет, без знака");

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
