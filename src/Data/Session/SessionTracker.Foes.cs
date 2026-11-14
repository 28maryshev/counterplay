namespace Counterplay;

// Журнал игр — личные матчапы: кто был у меня на линии и в чужой команде,
// дочитывание врагов старых игр, выборка «мои игры против» для банов и карточек.
public static partial class SessionTracker
{
    /// Одна моя игра против врага на моей линии: на ком я, на какой роли, кто
    /// стоял напротив, чем кончилось, когда.
    public sealed record FoeGame(int MyChamp, string Role, int Foe, bool Win, long Ts);

    /// Подмена для проверок: настоящая выборка копится только сыгранными
    /// играми. В боевом режиме всегда null.
    public static IReadOnlyList<FoeGame>? FoeGamesOverride { get; set; }

    // Очереди, где линия выдана драфтом. ARAM — нет: там линий нет.
    private static readonly string[] FoeQueues = ["solo", "flex", "normal"];

    // Сколько старых игр дочитывать за одно обновление журнала. Запрос к
    // клиенту на игру; сотня игр владельца с номером догоняется за пять игр,
    // а обновление после игры не стоит на месте.
    private const int FoeBackfillPerRefresh = 25;

    /// Положить врагов прочитанной игры в её запись журнала (в любой очереди),
    /// а с ними и мою роль, если её нет (игры, догнанные из истории, пишутся
    /// без роли). null — клиент не ответил: запись не трогаем, спросим в другой раз.
    private static void SetFoes(Account acc, long gameId, Dictionary<string, int>? foes, string myRole)
    {
        if (foes is null) return;
        foreach (var q in acc.Queues.Values)
            foreach (var g in q.Games)
                if (g.Id == gameId && g.Foes is null)
                {
                    g.Foes = foes;
                    if (string.IsNullOrEmpty(g.Role) && myRole.Length > 0) g.Role = myRole;
                }
    }

    /// <summary>
    /// Врагов пишут с 1.3.91. У игр, записанных раньше, но с номером (с 1.3.63),
    /// их можно дочитать из клиента: подробная игра отдаётся по номеру. Берём
    /// свежие первыми и не больше <see cref="FoeBackfillPerRefresh"/> за раз.
    /// Игры без номера не дочитать — их узнать в клиенте не по чему.
    /// </summary>
    private static async Task BackfillFoesAsync(LcuHttpClient http, Account acc, string puuid,
                                                Func<int, string, double>? roleShare, CancellationToken ct)
    {
        var todo = FoeQueues.Where(acc.Queues.ContainsKey)
                            .SelectMany(q => acc.Queues[q].Games)
                            .Where(g => g.Id is > 0 && g.Foes is null)
                            .OrderByDescending(g => g.Ts)
                            .Take(FoeBackfillPerRefresh)
                            .ToList();
        int got = 0;
        foreach (var g in todo)
        {
            ct.ThrowIfCancellationRequested();
            var (_, myRole, foes) = await FetchAlliesAsync(http, g.Id!.Value, puuid, roleShare, ct);
            if (foes is null) continue;
            SetFoes(acc, g.Id!.Value, foes, myRole);
            if (foes.Count > 0) got++;
        }
        if (todo.Count > 0) Log.Write($"журнал игр: враги дочитаны у {got} из {todo.Count} старых игр");
    }

    /// <summary>
    /// Мои игры, где известно, кто стоял на моей линии: текущий аккаунт, соло,
    /// флекс и обычные, все записанные. Роль — из драфта (её пишут с 1.3.61),
    /// иначе угаданная по подробной игре.
    /// </summary>
    public static IReadOnlyList<FoeGame> FoeGames()
    {
        if (FoeGamesOverride is { } fake) return fake;
        // Песочница с подставным профилем: его игры — без врагов, а настоящий
        // журнал в чужой профиль подмешивать нельзя (см. PreviewGames).
        if (PreviewGames is not null) return [];
        // Журнал разбирается с диска целиком, а движок спрашивает на каждом
        // сдвиге драфта — держим 20 секунд, как историю (History).
        if (_foeCache is { } c && (DateTime.UtcNow - c.At).TotalSeconds < 20) return c.Games;
        var res = new List<FoeGame>();
        try
        {
            var s = Load();
            var acc = s.LastAccount is { Length: > 0 } k && s.Accounts.TryGetValue(k, out var a)
                ? a : s.Accounts.Values.FirstOrDefault();
            if (acc is null) return res;
            foreach (var q in FoeQueues)
                if (acc.Queues.TryGetValue(q, out var ql))
                    foreach (var g in ql.Games)
                        if (g.ChampionId > 0 && g.Role is { Length: > 0 } role
                            && g.Foes is { } foes && foes.TryGetValue(role, out var foe) && foe > 0)
                            res.Add(new FoeGame(g.ChampionId, role, foe, g.Win, g.Ts));
        }
        catch { /* журнала нет или он битый — личных матчапов нет */ }
        _foeCache = (DateTime.UtcNow, res);
        return res;
    }

    private static (DateTime At, List<FoeGame> Games)? _foeCache;
}
