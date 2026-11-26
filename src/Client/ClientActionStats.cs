namespace Counterplay;

/// <summary>
/// Что программа сделала в клиенте League за драфт и что из этого вышло:
/// навести, залочить, забанить, фраза в чат, руны, спеллы, набор предметов.
/// По каждому виду — сколько удалось, сколько нет и код последней неудачи.
///
/// Зачем: на клиенте Tencent подключение работает, а работают ли сами
/// действия, видно не было — клиента у нас нет (журнал 2026-11-26). Отчёт
/// уходит сайту, когда драфт кончился (`Program.FlushClientActions`).
///
/// Учитывает тот, кто знает исход: руны, спеллы, предметы
/// (`RunesImporter`) и чат (`DraftChat`) — у себя, а пик, бан и наведение —
/// обёртка `Program.Tracked`: только она знает, пик это или бан.
/// </summary>
public static class ClientActionStats
{
    // Виды действий. Эти же имена принимает сайт
    // (app/api/telemetry/actions/route.ts) — менять вместе.
    public const string Hover = "hover", Pick = "pick", BanHover = "banHover", Ban = "ban",
                        Chat = "chat", Runes = "runes", Spells = "spells", Items = "items";

    public static readonly IReadOnlyList<string> Kinds = [Hover, Pick, BanHover, Ban, Chat, Runes, Spells, Items];

    /// Клиент не ответил, запрос сорвался или ответ не разобрался.
    public const int NoAnswer = 0;

    /// Чат: среди бесед клиента нет беседы драфта.
    public const int NoConversation = -2;

    /// Итог по одному виду. <see cref="Code"/> и <see cref="Detail"/> —
    /// последней неудачи; неудач не было — пусто.
    public sealed record Entry(string Kind, int Ok, int Fail, int? Code, string? Detail);

    private sealed class Counter
    {
        public int Ok, Fail;
        public int? Code;
        public string? Detail;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Counter> Counters = [];

    public static bool Succeeded(int status) => status is >= 200 and < 300;

    /// <summary>
    /// Учесть исход действия. Возвращает <paramref name="status"/> как есть,
    /// чтобы им можно было обернуть вызов. Неизвестный вид не учитывается.
    /// </summary>
    /// <param name="status">HTTP-код ответа клиента, <see cref="NoAnswer"/> или <see cref="NoConversation"/>.</param>
    /// <param name="detail">Пояснение к неудаче — строки клиента, не игрока (какие беседы были).</param>
    public static int Record(string kind, int status, string? detail = null)
    {
        if (!Kinds.Contains(kind)) return status;
        lock (Gate)
        {
            if (!Counters.TryGetValue(kind, out var c)) Counters[kind] = c = new Counter();
            if (Succeeded(status)) c.Ok++;
            else
            {
                c.Fail++;
                c.Code = status;
                c.Detail = Clean(detail);
            }
        }
        return status;
    }

    /// Забрать накопленное и обнулить. Пусто — действий не было.
    public static IReadOnlyList<Entry> Take()
    {
        lock (Gate)
        {
            var list = new List<Entry>();
            foreach (var k in Kinds)
                if (Counters.TryGetValue(k, out var c))
                    list.Add(new Entry(k, c.Ok, c.Fail, c.Code, c.Detail));
            Counters.Clear();
            return list;
        }
    }

    /// Пояснение уходит, только если похоже на служебные слова клиента:
    /// латиница, цифры, `_` и запятые, до 80 знаков. Иначе — ничего.
    public static string? Clean(string? s) =>
        s is { Length: > 0 and <= 80 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or ',') ? s : null;
}
