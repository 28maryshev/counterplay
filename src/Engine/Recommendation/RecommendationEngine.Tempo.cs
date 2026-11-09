namespace Counterplay;

// Движок подбора — сила по времени игры: насколько чемпион выигрывает чаще в
// коротких матчах, чем в длинных (таблица duration_wr, сборщик с 09.11.2026).
//
// Владелец, 09.11 («делай всё» по предложениям): ранняя и поздняя сила держались
// на ручных метках aggressive/scale — на миде не было ни одного раннего
// чемпиона, и замер дыр не смог измерить «раннюю игру» (5 драфтов из 807
// тысяч). Данные заменяют метки, как только таблица есть в базе; до того —
// всё по-старому.
public sealed partial class RecommendationEngine
{
    // Чемпиону нужно столько игр (взвешенных по патчам) по всем отрезкам, чтобы
    // судить о его темпе; доверие к отрезку — g/(g+TEMPO_CONF). На глаз: отрезок
    // «после 35 минут» — около пятой части игр, и на 1000 играх в нём ~200.
    private const double TEMPO_MIN_GAMES = 1000;
    private const double TEMPO_CONF = 300;
    // Ранний минус поздний, пп: от +2,5 — сильный рано (3), до −2,5 — гиперкерри
    // (0), до −1 — набирает к поздней (1), между — ровный (2). Шкала та же, что
    // у меток EarlyPower, чтобы её читатели (сетап под ганки, темп, дыры) не менялись.
    private const double TEMPO_EARLY = 2.5, TEMPO_LATE = -1.0, TEMPO_HYPER = -2.5;

    private Dictionary<int, (double Early, double Mid, double Late)>? _tempo;
    private bool _tempoTried;

    /// Сила чемпиона по отрезкам (до 25, 25–35, после 35 минут), пп к его
    /// среднему; null — таблицы нет или игр мало.
    public (double Early, double Mid, double Late)? Tempo(int champId)
    {
        EnsureTempo();
        return _tempo is not null && _tempo.TryGetValue(champId, out var t) ? t : null;
    }

    /// Сумма силы по отрезкам у команды (только чемпионы с данными); null — данных
    /// нет ни у кого.
    public (double Early, double Mid, double Late)? TeamTempo(IEnumerable<int> team)
    {
        double e = 0, m = 0, l = 0; var any = false;
        foreach (var id in team)
            if (Tempo(id) is { } t) { e += t.Early; m += t.Mid; l += t.Late; any = true; }
        return any ? (e, m, l) : null;
    }

    /// Читает duration_wr один раз на движок и подменяет метки ранней силы
    /// (<see cref="ChampionTraits.EarlyPowerData"/>). Таблицы нет — молчит.
    private void EnsureTempo()
    {
        if (_tempoTried) return;
        _tempoTried = true;
        // Подмена — по ЭТОЙ базе: после подмены базы без таблицы старые данные не висят.
        ChampionTraits.EarlyPowerData = null;
        try
        {
            using var chk = _db.CreateCommand();
            chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='duration_wr'";
            if (chk.ExecuteScalar() is null) return;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, bucket, SUM(games*{PW}), SUM(wins*{PW}) FROM duration_wr
                WHERE tier_bucket=@t AND patch IN (@p1,@p2,@p3) GROUP BY champion_id, bucket";
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            var rows = new List<(int, int, double, double)>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read()) rows.Add((rd.GetInt32(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetDouble(3)));
            _tempo = BuildTempo(rows);
            ChampionTraits.EarlyPowerData = EarlyFromTempo(_tempo);
            Log.Write($"сила по времени: {_tempo.Count} чемпионов из duration_wr");
        }
        catch (Exception ex) { Log.Write($"сила по времени не прочиталась: {ex.Message}"); }
    }

    /// <summary>
    /// Строки (чемпион, отрезок, игры, победы) → сила по отрезкам, пп к своему
    /// среднему: (победы + K/2)/(игры + K) отрезка минус то же по всем отрезкам,
    /// с доверием g/(g+TEMPO_CONF). Меньше TEMPO_MIN_GAMES игр — чемпиона нет.
    /// </summary>
    public static Dictionary<int, (double Early, double Mid, double Late)> BuildTempo(
        IEnumerable<(int Champ, int Bucket, double Games, double Wins)> rows)
    {
        var res = new Dictionary<int, (double, double, double)>();
        foreach (var g in rows.GroupBy(r => r.Champ))
        {
            double G = g.Sum(r => r.Games), W = g.Sum(r => r.Wins);
            if (G < TEMPO_MIN_GAMES) continue;
            var all = (W + K / 2) / (G + K);
            double Part(int b)
            {
                var (gb, wb) = (g.Where(r => r.Bucket == b).Sum(r => r.Games), g.Where(r => r.Bucket == b).Sum(r => r.Wins));
                return gb <= 0 ? 0 : ((wb + K / 2) / (gb + K) - all) * 100 * gb / (gb + TEMPO_CONF);
            }
            res[g.Key] = (Part(0), Part(1), Part(2));
        }
        return res;
    }

    /// Сила по отрезкам → шкала ранней силы 0..3 (как у меток).
    public static Dictionary<int, int> EarlyFromTempo(IReadOnlyDictionary<int, (double Early, double Mid, double Late)> tempo) =>
        tempo.ToDictionary(kv => kv.Key, kv =>
        {
            var d = kv.Value.Early - kv.Value.Late;
            return d >= TEMPO_EARLY ? 3 : d <= TEMPO_HYPER ? 0 : d <= TEMPO_LATE ? 1 : 2;
        });
}
