using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Таймер драфта из события клиента (поле <c>timer</c> сессии чемп-селекта):
/// фаза, сколько в ней осталось, сколько было всего и есть ли срок вообще.
/// В фазе пиков клиент заводит таймер заново на каждый ход, и остаток — это
/// время того, чей ход сейчас.
///
/// Не часть <see cref="DraftState"/>: остаток меняется в каждом событии, а по
/// снимку драфта цикл клиента решает, пересчитывать ли подбор (отпечаток
/// драфта), — таймер пересчитывал бы его на каждое событие.
/// </summary>
public readonly record struct DraftTimer(string Phase, double LeftMs, double TotalMs, bool Infinite)
{
    public static DraftTimer Parse(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object ||
            !session.TryGetProperty("timer", out var t) || t.ValueKind != JsonValueKind.Object)
            return new("", 0, 0, false);
        var phase = t.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";
        // adjustedTimeLeftInPhase — то, что показывает сам клиент (с поправкой
        // на задержку); timeLeftInPhase — запасной.
        var left = Num(t, "adjustedTimeLeftInPhase") ?? Num(t, "timeLeftInPhase") ?? 0;
        var total = Num(t, "totalTimeInPhase") ?? 0;
        var infinite = t.TryGetProperty("isInfinite", out var inf) && inf.ValueKind == JsonValueKind.True;
        return new(phase, left, total, infinite);
    }

    /// <summary>
    /// Когда кончится нынешний ход пика (UTC) — чей бы он ни был: отсчёт
    /// стоит у каждого, чей ход сейчас (<c>ActiveCells</c>), таймер у них
    /// общий. Мой ли ход — <c>MyPickInProgress</c>. null — отсчёта нет: пиков
    /// сейчас никто не делает (баны, объявление, финализация), ARAM
    /// (чемпионов раздают, ходов нет), драфт без срока (тренировка).
    /// </summary>
    public DateTime? PickTurnDeadline(DraftState draft, DateTime nowUtc) =>
        !draft.IsAram && (draft.MyPickInProgress || draft.ActiveCells.Count > 0) && !Infinite && LeftMs > 0
            ? nowUtc.AddMilliseconds(LeftMs) : null;

    private static (string Phase, double Left) _last;

    /// <summary>
    /// Начало каждого хода — строкой в журнал программы: фаза, длина хода,
    /// сколько осталось. Ход начался — фаза сменилась или остаток подскочил
    /// (в фазе пиков клиент заводит таймер заново на каждый ход). Так видно
    /// настоящие сроки драфта: у Riot их таблицы нет, вики — до 26.1, а в 26.1
    /// таймеры срезали «примерно на 30 секунд». По этим строкам сверены сроки
    /// песочницы (TestPanel.PickTurnSeconds и соседи). Строк — около десяти
    /// на драфт.
    /// </summary>
    public static void LogTurn(DraftTimer t)
    {
        var turn = t.Phase != _last.Phase || t.LeftMs > _last.Left + 1000;
        _last = (t.Phase, t.LeftMs);
        if (!turn || t.Phase.Length == 0) return;
        Log.Write($"таймер драфта: {t.Phase}, ход {t.TotalMs / 1000:0.0} с, осталось {t.LeftMs / 1000:0.0} с" +
                  (t.Infinite ? ", без срока" : ""));
    }

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
