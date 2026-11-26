using Counterplay;

// Program — отчёт сайту о действиях в клиенте за драфт: что программа
// сделала (навести, залочить, забанить, чат, руны, спеллы, предметы) и
// удалось ли. Счётчики — ClientActionStats.
partial class Program
{
    /// <summary>
    /// Действие с чемпионом (навести, залочить, забанить) с учётом исхода.
    /// Номер действия читается в момент вызова: он меняется на каждом снимке
    /// драфта. Своего действия нет (-1) или нет чемпиона — в клиент ничего
    /// не ушло, такой вызов не считается.
    /// </summary>
    static Func<int, Task<int>> Tracked(string kind, Func<int> actionId, Func<int, int, Task<int>> act) =>
        async champId =>
        {
            var id = actionId();
            var status = await act(id, champId);
            if (id >= 0 && champId > 0) ClientActionStats.Record(kind, status);
            return status;
        };

    /// <summary>
    /// Драфт кончился (фаза ушла из ChampSelect: игра началась, драфт
    /// развалился) — отправить, что накопилось. Фаза меняется несколько раз
    /// подряд, а уходит один отчёт: Take обнуляет счётчики. В песочнице
    /// счётчики обнуляются, но не отправляются.
    /// </summary>
    static void FlushClientActions()
    {
        var entries = ClientActionStats.Take();
        if (!_clientReportOn || entries.Count == 0) return;
        Log.Write("отчёт о действиях: " + string.Join(", ", entries.Select(e =>
            $"{e.Kind} {e.Ok}/{e.Fail}" + (e.Code is { } c ? $" ({c}{(e.Detail is { } d ? " " + d : "")})" : ""))));
        _ = Telemetry.ReportActionsAsync(entries);
    }
}
