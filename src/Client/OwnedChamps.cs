namespace Counterplay;

/// <summary>
/// Чемпионы аккаунта за сеанс клиента: кого помечать «нет на аккаунте».
///
/// Клиент отдаёт список не сразу. Первые ~10 секунд после запуска он на запрос
/// отвечает 404 «Champion data has not yet been received» и присылает список
/// событием, когда загрузит. Программа подключается как раз в это окно
/// (автозапуск с Windows, перезапуск клиента) и читала список один раз:
/// пустой ответ значил «владение неизвестно», и до конца сеанса отметок не
/// было ни в одном драфте.
///
/// Теперь список принимается из любого источника: ответ на запрос (подключение,
/// смена аккаунта, начало драфта, если списка всё ещё нет) и событие клиента
/// (загрузка; покупка чемпиона). Пустой ответ — «данных ещё нет»: прежний
/// список он не затирает.
/// </summary>
public sealed class OwnedChamps(Func<CancellationToken, Task<HashSet<int>>> fetch,
                                Action<HashSet<int>> publish)
{
    /// И адрес запроса, и тема события со списком. Тема должна проходить
    /// фильтр сокета (<see cref="LcuEventSocket.IsRelevant"/>) — иначе событие
    /// до программы не дойдёт.
    public const string EventUri = "/lol-champions/v1/owned-champions-minimal";

    /// Не чаще этого спрашивать в драфте, пока списка нет: события драфта идут
    /// по нескольку в секунду.
    public TimeSpan RetryGap { get; init; } = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private HashSet<int> _known = [];
    private DateTime _lastTry = DateTime.MinValue;

    /// Список уже есть — отметки «нет на аккаунте» работают.
    public bool Known { get { lock (_gate) return _known.Count > 0; } }

    /// Спросить клиент. why — в журнал программы.
    public async Task LoadAsync(string why, CancellationToken ct)
    {
        lock (_gate) _lastTry = DateTime.UtcNow;
        HashSet<int> ids;
        try { ids = await fetch(ct); }
        catch { ids = []; }
        Accept(ids, why);
    }

    /// Начало драфта: списка всё ещё нет (событие пришло до подписки или
    /// потерялось) — спросить снова, но не чаще <see cref="RetryGap"/>.
    public Task EnsureAsync(CancellationToken ct)
    {
        lock (_gate)
            if (_known.Count > 0 || DateTime.UtcNow - _lastTry < RetryGap) return Task.CompletedTask;
        return LoadAsync("драфт", ct);
    }

    /// Сменился аккаунт: прежний список чужой. Лучше без отметок, чем красная
    /// рамка на чемпионе, который у нового аккаунта есть.
    public void Forget()
    {
        lock (_gate)
        {
            if (_known.Count == 0) return;
            _known = [];
        }
        publish([]);
    }

    /// Событие сокета. Своё — разбирает и возвращает true.
    public bool TryHandle(LcuEvent ev)
    {
        if (ev.Uri != EventUri) return false;
        if (ev.EventType != "Delete") Accept(PlayerInfo.ParseOwnedChampions(ev.Data), "клиент прислал");
        return true;
    }

    private void Accept(HashSet<int> ids, string why)
    {
        lock (_gate)
        {
            if (ids.Count == 0)
            {
                if (_known.Count == 0) Log.Write($"чемпионы аккаунта ({why}): клиент ещё не отдал список");
                return;
            }
            if (ids.SetEquals(_known)) return;
            _known = ids;
        }
        Log.Write($"чемпионы аккаунта ({why}): {ids.Count}");
        publish(ids);
    }
}
