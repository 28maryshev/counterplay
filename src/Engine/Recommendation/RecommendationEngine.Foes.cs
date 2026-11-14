namespace Counterplay;

// Движок подбора — личные матчапы: как игрок сам выступает против чемпиона на
// своей линии по журналу игр (SessionTracker.FoeGames), сверх того, что ждёт
// статистика для его чемпионов. Нужно банам: не тратить бан на того, кого
// игрок обыгрывает, и поднять того, кому он проигрывает (владелец 14.11, по
// разбору: «мнение игрока о матчапе решает, банить или нет»). И карточке пика —
// строка «твои игры на X против Y».
public sealed partial class RecommendationEngine
{
    /// Мои игры против чемпиона на моей линии: сколько, сколько побед и
    /// перевес — на сколько пунктов винрейта я против него лучше ожидаемого
    /// (минус — хуже), с усадкой к нулю на PERSONAL_K воображаемых игр.
    public sealed record PersonalVs(int Games, int Wins, double Edge);

    // Пересчёт — когда сменилась роль или сама выборка (журнал перечитан, через
    // 20 секунд или после записи; в проверке — подменён).
    private (IReadOnlyList<SessionTracker.FoeGame> From, string Role, Dictionary<int, PersonalVs> Map)? _personal;

    /// <summary>
    /// Личные матчапы на роли: враг на моей линии → мои игры против него.
    ///
    /// Ожидание в каждой игре — мой винрейт на роли по тем же играм плюс то,
    /// что статистика даёт моему чемпиону против этого врага (дельта пары к
    /// силе чемпиона, с доверием по объёму пары). Так 2–7 против Блицкранка на
    /// Соне, которую он и так контрит, — меньше перевеса, чем 2–7 на Брауме,
    /// которого он не контрит: часть поражений статистика уже объясняет, и
    /// баны её уже учли. Перевес = Σ(победа − ожидание) / (игр + PERSONAL_K).
    /// Своих игр против одного врага — горстка, и шум огромен: 2–7 при
    /// ожидании 4,3 — это −5,8 пункта, 0–10 — −10.
    /// </summary>
    public IReadOnlyDictionary<int, PersonalVs> PersonalVsFoes(string role)
    {
        var all = SessionTracker.FoeGames();
        if (_personal is { } c && c.Role == role && ReferenceEquals(c.From, all)) return c.Map;
        var games = all.Where(g => g.Role == role).ToList();
        var map = new Dictionary<int, PersonalVs>();
        if (games.Count > 0)
        {
            // Мой винрейт на роли. Мало игр — середина: по десятку игр он скорее шум.
            var mine = games.Count >= PERSONAL_BASE_MIN ? (double)games.Count(g => g.Win) / games.Count : 0.5;
            var expect = new Dictionary<(int, int), double>();
            double Expect(int me, int foe)
            {
                if (expect.TryGetValue((me, foe), out var e)) return e;
                var (bg, bw) = RawBase(me, role);
                var d = 0.0;
                if (bg > 0 && MatchupsOf(me, role).TryGetValue(foe, out var m) && m.g > 0)
                    d = (Delta(m.g, m.w, K_PAIR) - Delta(bg, bw, K)) * (m.g / (m.g + MATCHUP_CONF)) / 100;
                return expect[(me, foe)] = Math.Clamp(mine + d, 0.05, 0.95);
            }
            foreach (var byFoe in games.GroupBy(g => g.Foe))
            {
                var n = byFoe.Count();
                var w = byFoe.Count(g => g.Win);
                var edge = byFoe.Sum(g => (g.Win ? 1 : 0) - Expect(g.MyChamp, g.Foe)) / (n + PERSONAL_K) * 100;
                map[byFoe.Key] = new PersonalVs(n, w, edge);
            }
        }
        _personal = (all, role, map);
        return map;
    }

    /// Мои игры против <paramref name="foe"/> на роли по моим чемпионам:
    /// чемпион → (игр, побед). Для строки карточки пика — один раз на драфт.
    public Dictionary<int, (int Games, int Wins)> PersonalByChamp(int foe, string role)
    {
        var res = new Dictionary<int, (int Games, int Wins)>();
        foreach (var g in SessionTracker.FoeGames())
            if (g.Role == role && g.Foe == foe)
            {
                var cur = res.GetValueOrDefault(g.MyChamp);
                res[g.MyChamp] = (cur.Games + 1, cur.Wins + (g.Win ? 1 : 0));
            }
        return res;
    }
}
