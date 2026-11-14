namespace Counterplay;

// Журнал игр — вечер: как игрок выступает в начале вечера и ближе к концу, и
// после двух поражений подряд. Честный ответ на «меня закинули в losers queue»:
// по его же играм видно, падает ли винрейт к пятой игре и после серии
// поражений (владелец 14.11, по разбору подкаста: «на выходных за десять игр
// подряд теряют то, что набрали за неделю»).
public static partial class SessionTracker
{
    /// <summary>
    /// Вечер игрока по журналу.
    /// <para><see cref="Game"/> — номер следующей игры в нынешнем вечере (1 —
    /// вечер ещё не начат), <see cref="Wins"/>–<see cref="Losses"/> — его счёт,
    /// <see cref="LossStreak"/> — поражений подряд в его конце.</para>
    /// <para>По всей истории: <see cref="Early"/> — первые
    /// <see cref="EveningEarly"/> игры каждого вечера, <see cref="Late"/> —
    /// остальные; <see cref="AfterTwo"/> — игры сразу после двух и больше
    /// поражений подряд в том же вечере, <see cref="Rest"/> — все прочие.</para>
    /// </summary>
    public sealed record Evening(
        int Game, int Wins, int Losses, int LossStreak,
        (int Games, int Wins) Early, (int Games, int Wins) Late,
        (int Games, int Wins) AfterTwo, (int Games, int Wins) Rest);

    /// Сколько первых игр вечера считаются «началом».
    public const int EveningEarly = 3;

    /// Перерыв, после которого начинается новый вечер, — от конца одной игры до
    /// начала следующей. Очередь и драфт — минуты, ужин и сон — больше часа.
    private const long EveningGapSec = 60 * 60;

    // Начало игры, когда его нет (до 1.3.63 писали только конец): типичная
    // игра — полчаса.
    private const long TypicalGameSec = 30 * 60;

    // Очереди вечера. ARAM не считаем: там чемпиона не выбирают, и винрейт —
    // про случай, а не про то, устал ли игрок.
    private static readonly string[] EveningQueues = ["solo", "flex", "normal"];

    /// <summary>
    /// Вечер по играм аккаунта (соло, флекс, обычные — вместе: устаёт игрок, а
    /// не очередь). Игры — с концом <c>Ts</c> и, если есть, началом
    /// <c>Start</c>; новый вечер — после перерыва дольше часа.
    /// </summary>
    private static Evening BuildEvening(Account acc, long now)
    {
        var games = EveningQueues.Where(acc.Queues.ContainsKey)
                                 .SelectMany(q => acc.Queues[q].Games)
                                 .Where(g => g.Ts > 0)
                                 .OrderBy(g => g.Ts)
                                 .Select(g => (Start: g.Start is > 0 and var s && s < g.Ts ? s : g.Ts - TypicalGameSec, End: g.Ts, g.Win))
                                 .ToList();
        return EveningOf(games, now);
    }

    /// Сам счёт — отдельно от журнала, чтобы проверка гоняла его на готовых играх.
    public static Evening EveningOf(IReadOnlyList<(long Start, long End, bool Win)> games, long now)
    {
        (int, int) early = (0, 0), late = (0, 0), afterTwo = (0, 0), rest = (0, 0);
        static (int, int) Add((int G, int W) a, bool win) => (a.G + 1, a.W + (win ? 1 : 0));
        int n = 0, streak = 0, w = 0, l = 0;
        long prevEnd = 0;
        foreach (var (start, end, win) in games)
        {
            if (prevEnd == 0 || start - prevEnd > EveningGapSec) { n = 0; streak = 0; w = 0; l = 0; }
            n++;
            if (n <= EveningEarly) early = Add(early, win); else late = Add(late, win);
            if (streak >= 2) afterTwo = Add(afterTwo, win); else rest = Add(rest, win);
            streak = win ? 0 : streak + 1;
            if (win) w++; else l++;
            prevEnd = end;
        }
        // Нынешний вечер идёт, если с конца последней игры не прошло часа.
        var going = prevEnd > 0 && now - prevEnd <= EveningGapSec;
        return going
            ? new Evening(n + 1, w, l, streak, early, late, afterTwo, rest)
            : new Evening(1, 0, 0, 0, early, late, afterTwo, rest);
    }
}
