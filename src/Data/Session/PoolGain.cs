namespace Counterplay;

/// <summary>
/// Насколько чаще игрок выигрывает на своих чемпионах — цифра для зелёного
/// тега на кнопке «Пул», пока пулов нет (владелец, 13.11: «повысим интерес
/// добавить пул … +3% win rate, если есть возможность посчитать; если расчёт
/// сложный — не надо»).
///
/// Общей цифры «пул добавляет N%» у нас нет: в матчах не видно, кто играл с
/// пулом, а прибавка пула в подборе (POOL_COMFORT) — очки ранжирования, не
/// проценты. Поэтому цифра личная, из журнала игрока: винрейт на TopChamps
/// самых игранных чемпионах за 30 дней против всех остальных, ранкед и
/// нормалы. Показываем, только если с каждой стороны не меньше MinGames игр, а
/// разница в плюс, не меньше пункта и больше своей стандартной ошибки — иначе
/// это шум выборки (на 20 играх с каждой стороны ошибка разницы ~16 пп), и
/// тег стоит без числа.
/// </summary>
public static class PoolGain
{
    /// Сколько самых игранных считаем «своими» — размер обычного пула.
    public const int TopChamps = 5;

    /// Игр с каждой стороны, меньше — цифры нет.
    public const int MinGames = 20;

    /// Разница меньше пункта — не повод для тега.
    public const int MinGain = 1;

    public sealed record Result(int Gain, int Top, double TopWr, int TopGames, double RestWr, int RestGames);

    /// <summary>
    /// order — мои чемпионы, самые игранные первыми; stats — игры и победы по
    /// чемпиону за то же окно. null — цифры нет (мало игр, разница не в плюс
    /// или тонет в шуме).
    /// </summary>
    public static Result? Compute(IReadOnlyList<int> order, IReadOnlyDictionary<int, (int Games, int Wins)> stats)
    {
        var top = order.Where(id => stats.TryGetValue(id, out var s) && s.Games > 0)
                       .Take(TopChamps).ToHashSet();
        int tg = 0, tw = 0, rg = 0, rw = 0;
        foreach (var (id, (g, w)) in stats)
            if (top.Contains(id)) { tg += g; tw += w; } else { rg += g; rw += w; }
        if (tg < MinGames || rg < MinGames) return null;

        double pt = (double)tw / tg, pr = (double)rw / rg;
        var diff = 100 * (pt - pr);
        var se = 100 * Math.Sqrt(pt * (1 - pt) / tg + pr * (1 - pr) / rg);
        var gain = (int)Math.Round(diff);
        if (gain < MinGain || diff <= se) return null;
        return new Result(gain, top.Count, 100 * pt, tg, 100 * pr, rg);
    }

    /// Цифра по журналу игрока: окно и очереди — те же, что у полосы «мои
    /// чемпионы» на экране готовности.
    public static Result? ForMe()
    {
        string[] queues = [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal];
        return Compute(SessionTracker.PlayedChamps(SessionTracker.RecentDays),
                       SessionTracker.ChampStatsMap(SessionTracker.RecentDays, queues));
    }
}
