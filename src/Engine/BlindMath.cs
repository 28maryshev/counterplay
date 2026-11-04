namespace Counterplay;

/// <summary>
/// Математика «слепоты» чемпиона: насколько его исход зависит от того, кого
/// возьмут против него. Чистые функции без базы и драфта — их зовёт
/// <see cref="RecommendationEngine"/> (часть Blind) и проверяет tests/BlindPick.
///
/// Шаги:
///
///   1. «Выше ожидаемого». Сырой винрейт пары смешивает контру с силой обоих
///      в патче. Ожидание пары считается через шансы: odds = p/(1−p),
///      odds(A против B) = odds(A)/odds(B). Ари 50,9% против Мел 45,5% —
///      ожидание 55,4%, а факт 51,5%: настоящая контра около −4 пп.
///   2. Разброс — стандартное отклонение «выше ожидаемого» по соперникам,
///      каждый с весом своей доли в пиках роли.
///   3. Наблюдаемый разброс завышен шумом малых выборок: разброс² = истинный² +
///      шум², а шум пары биномиальный и известен по числу игр, p(1−p)/n.
///   4. Контра — пара хуже ожидаемого больше чем на 2 пп. Шанс нарваться на неё
///      вслепую — 1 − (1 − q)^N, где q — доля контр в пиках роли, N — пул
///      соперника (7: столько чемпионов в среднем покрывают 80% мастерства
///      мидера).
///
/// Шаг 3 доведён до каждой пары. Пара — не число, а
/// распределение: среднее, усаженное к центру чемпиона, и остаточная
/// неуверенность. Худшее из N и доля контр считаются по этим распределениям.
/// Иначе выходило две беды сразу: по сырым парам «главной контрой» каждого
/// оказывалась пара на двадцати играх (минимум шумных оценок всегда ниже
/// правды), а по одним усаженным средним редкий чемпион выглядел самым
/// надёжным — всё его поле стягивалось к центру. Соперник-то настоящий
/// матчап знает; мы не знаем лишь, какой из них плохой.
/// </summary>
public static class BlindMath
{
    /// Порог контры, пп «выше ожидаемого»: хуже ожидания на 2 пункта и больше.
    public const double COUNTER_PP = 2.0;

    /// Ожидаемый винрейт A против B по их собственным винрейтам (доли 0…1).
    public static double ExpectedWr(double wrA, double wrB)
    {
        var odds = Odds(wrA) / Odds(wrB);
        return odds / (1.0 + odds);
    }

    private static double Odds(double p)
    {
        p = Math.Clamp(p, 1e-6, 1 - 1e-6);
        return p / (1 - p);
    }

    /// Шум пары в пп²: биномиальная дисперсия доли побед на g играх.
    public static double Noise2(double expected, double games) =>
        games > 0 ? expected * (1 - expected) / games * 1e4 : double.PositiveInfinity;

    /// <summary>Пара «мой чемпион против соперника».</summary>
    /// <param name="Id">соперник</param>
    /// <param name="Share">его доля в пиках роли соперника</param>
    /// <param name="Above">факт минус ожидание по шансам, пп</param>
    /// <param name="Noise2">шум этой оценки, пп²</param>
    /// <param name="Games">мои игры против него — по ним видно, кого мне на деле достаётся</param>
    public readonly record struct Cell(int Id, double Share, double Above, double Noise2, double Games);

    /// Оценка пары после усадки: среднее и остаточная неуверенность, пп и пп².
    public readonly record struct Est(double Mean, double Var);

    /// <summary>
    /// Профиль чемпиона против одной роли соперников.
    /// Center — средняя «выше ожидаемого» по пикрейту; Observed/True/Noise —
    /// разбросы, пп; Drawn — чего ему достаётся на деле (среднее по своим
    /// играм), это уже сидит в его базовом винрейте; Pairs — пары после
    /// усадки. Пара без игр — Unknown: центр и весь истинный разброс.
    /// </summary>
    public sealed record Profile(double Center, double Observed, double True, double Noise,
                                 double Drawn, IReadOnlyDictionary<int, Est> Pairs)
    {
        public Est Unknown => new(Center, True * True);

        public Est Of(int id) => Pairs.TryGetValue(id, out var e) ? e : Unknown;
    }

    /// Сырой истинный разброс², без приора роли: наблюдаемый² минус шум².
    /// Нужен, чтобы посчитать приор роли (среднее по её чемпионам).
    public static double RawTrue2(IReadOnlyList<Cell> cells)
    {
        var (_, obs2, noise2) = Moments(cells);
        return Math.Max(obs2 - noise2, 0.0);
    }

    /// <summary>
    /// Профиль по парам. roleTrue2 — типичный истинный разброс² роли: когда
    /// шум у чемпиона громче сигнала, своей оценке разброса верить нельзя, и
    /// она тянется к типичной. Иначе редкий чемпион, у которого шум съел
    /// весь разброс, выходил бы «идеально слепым» — ровно наоборот правде:
    /// про него просто ничего не известно.
    /// </summary>
    public static Profile Build(IReadOnlyList<Cell> cells, double roleTrue2)
    {
        roleTrue2 = Math.Max(roleTrue2, 0);
        if (cells.Count == 0 || cells.Sum(c => c.Share) <= 0)
            return new Profile(0, 0, Math.Sqrt(roleTrue2), 0, 0, new Dictionary<int, Est>());

        var (m, obs2, noise2) = Moments(cells);
        var raw  = Math.Max(obs2 - noise2, 0.0);
        // Доля сигнала в типичной паре: шум мал — верим своей оценке, велик —
        // типичной для роли.
        var r    = roleTrue2 > 0 ? roleTrue2 / (roleTrue2 + noise2) : 1.0;
        var tau2 = r * raw + (1 - r) * roleTrue2;

        // Усадка пары к центру (нормальное к нормальному): чем шумнее пара по
        // сравнению с истинным разбросом, тем меньше верится её отклонению и
        // тем больше остаётся неуверенности.
        var pairs = new Dictionary<int, Est>(cells.Count);
        foreach (var c in cells)
            pairs[c.Id] = tau2 <= 0 ? new Est(m, 0)
                : double.IsInfinity(c.Noise2) ? new Est(m, tau2)
                : new Est(m + (c.Above - m) * tau2 / (tau2 + c.Noise2), tau2 * c.Noise2 / (tau2 + c.Noise2));

        var games = cells.Sum(c => c.Games);
        var drawn = games > 0 ? cells.Sum(c => c.Games * pairs[c.Id].Mean) / games : m;

        return new Profile(m, Math.Sqrt(obs2), Math.Sqrt(tau2), Math.Sqrt(noise2), drawn, pairs);
    }

    private static (double Mean, double Obs2, double Noise2) Moments(IReadOnlyList<Cell> cells)
    {
        var w = cells.Sum(c => c.Share);
        if (w <= 0) return (0, 0, 0);
        var m     = cells.Sum(c => c.Share * c.Above) / w;
        var obs2  = cells.Sum(c => c.Share * (c.Above - m) * (c.Above - m)) / w;
        var fin   = cells.Where(c => !double.IsInfinity(c.Noise2)).ToList();
        var wf    = fin.Sum(c => c.Share);
        var noise = wf > 0 ? fin.Sum(c => c.Share * c.Noise2) / wf : 0;
        return (m, obs2, noise);
    }

    // ── Поле соперников: (доля, оценка пары) ────────────────────────────────

    /// <summary>
    /// Ожидаемое худшее из N соперников, вытянутых по долям (с возвращением):
    /// столько стоит матчап, если соперник берёт из своего пула лучшую
    /// контру. Если все пары известны точно — считается дискретно (у Ари вся
    /// опасность — одна Мел); иначе по смеси распределений пар:
    /// P(худшее > t) = (1 − G(t))^N, G — доля поля, у которой пара ниже t.
    /// </summary>
    public static double ExpectedMin(IReadOnlyList<(double Share, Est Pair)> field, int n)
    {
        var w = field.Sum(f => f.Share);
        if (w <= 0 || n <= 0) return 0;

        if (field.All(f => f.Pair.Var <= 1e-12))
        {
            double f0 = 0, e = 0;
            foreach (var (share, pair) in field.OrderBy(f => f.Pair.Mean))
            {
                var s = share / w;
                // P(минимум = эта пара) = (1−F)^N − (1−F−s)^N.
                e  += pair.Mean * (Math.Pow(Math.Max(1 - f0, 0), n) - Math.Pow(Math.Max(1 - f0 - s, 0), n));
                f0 += s;
            }
            return e;
        }

        // E[X] = lo + ∫ P(X > t) dt на отрезке, за которым вероятности уже нет.
        var lo = field.Min(f => f.Pair.Mean - 6 * Math.Sqrt(f.Pair.Var)) - 0.01;
        var hi = field.Max(f => f.Pair.Mean + 6 * Math.Sqrt(f.Pair.Var)) + 0.01;
        const int steps = 300;
        var dt = (hi - lo) / steps;
        var sum = 0.0;
        for (int k = 0; k < steps; k++)
        {
            var t = lo + (k + 0.5) * dt;
            sum += Math.Pow(Math.Max(1 - Below(field, t, w), 0), n);
        }
        return lo + sum * dt;
    }

    /// Средняя по долям — соперник берёт что угодно, не глядя на меня.
    public static double ExpectedAny(IReadOnlyList<(double Share, Est Pair)> field)
    {
        var w = field.Sum(f => f.Share);
        return w > 0 ? field.Sum(f => f.Share * f.Pair.Mean) / w : 0;
    }

    /// Доля контр в пиках: сколько поля хуже ожидаемого больше чем на
    /// COUNTER_PP — с учётом того, насколько уверенно известна каждая пара.
    public static double CounterShare(IReadOnlyList<(double Share, Est Pair)> field)
    {
        var w = field.Sum(f => f.Share);
        return w > 0 ? Below(field, -COUNTER_PP, w) : 0;
    }

    /// Шанс, что в пуле из N чемпионов у соперника найдётся контра: 1 − (1 − q)^N.
    public static double CounterChance(double q, int n) => 1 - Math.Pow(1 - Math.Clamp(q, 0, 1), n);

    // Доля поля (по пикам), у которой пара не выше t.
    private static double Below(IReadOnlyList<(double Share, Est Pair)> field, double t, double w)
    {
        var g = 0.0;
        foreach (var (share, pair) in field)
            g += share * (pair.Var <= 1e-12
                ? (pair.Mean <= t ? 1.0 : 0.0)
                : Phi((t - pair.Mean) / Math.Sqrt(pair.Var)));
        return g / w;
    }

    // Функция нормального распределения (Абрамовиц–Стиган 7.1.26, ошибка ~1e-7).
    private static double Phi(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }
}
