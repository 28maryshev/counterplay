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

    private static double? Num(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
