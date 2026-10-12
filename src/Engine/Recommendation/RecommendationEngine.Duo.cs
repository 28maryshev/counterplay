using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — дуо: уверенность в напарнике, его оценки и лучший напарник для пика.
public sealed partial class RecommendationEngine
{
    /// <summary>
    /// Доверие к связке: сколько мы играем вдвоём за окно свежести, сглаженно
    /// (см. <see cref="MATE_CONF"/>). Одно место на оба применения — вес
    /// напарника в МОЕЙ оценке и надбавка в подсказке ему
    /// (<see cref="BestPartner"/>), — иначе они однажды разъедутся.
    /// </summary>
    private static double MateConfidence()
    {
        var games = SessionTracker.MateStats(PoolStore.ActiveDuo()?.FriendPuuid, FreshDays).Games;
        return games / (games + MATE_CONF);
    }

    /// <summary>
    /// Насколько хорош КАЖДЫЙ чемпион из половины друга в ЭТОМ драфте — тем же
    /// скорингом, которым считается мой пик, только в роли напарника.
    ///
    /// Без этого подсказка напарнику была бы «кто лучше всех сочетается с моим
    /// пиком» и не зависела бы ни от врагов, ни от остальных союзников: саппорт,
    /// которого вражеский бот разбирает, шёл бы наравне с удобным.
    ///
    /// Роль берём ту, под которую чемпион положен в половину, и считаем роль
    /// одной пачкой: позиция у движка приходит из состояния, поэтому на каждую
    /// роль своё подменённое состояние, где локальный игрок сидит в слоте
    /// напарника. <c>DirectOpponent</c> там снимаем намеренно — движок выведет
    /// оппонента ПО ЭТОЙ роли сам (<see cref="InferDirectOpponent"/>), а
    /// готовый указывал бы на моего.
    ///
    /// Личные факторы на это время выключены (<see cref="_forMate"/>):
    /// наигранность и личный винрейт — моя история, не его.
    ///
    /// Мой слот остаётся в команде со своим чемпионом — иначе у его кандидатов
    /// пропала бы структурная связка со мной (адк↔саппорт, лес↔линия). Обычную
    /// синергию со мной его оценка поэтому уже содержит, и
    /// <see cref="BestPartner"/> добавляет только надбавку над ней.
    /// </summary>
    public IReadOnlyDictionary<int, double> PartnerScores(
        DraftState state, IReadOnlyDictionary<string, List<int>> friendHalf,
        string mateRole = "")
    {
        var res = new Dictionary<int, double>();
        if (friendHalf.Count == 0) return res;

        foreach (var (role, champs) in friendHalf)
        {
            // Роль напарника видна в драфте — считаем ТОЛЬКО её. Половина
            // разложена по линиям, а на какой он стоит сейчас, раньше никто не
            // спрашивал: человеку на миде предлагали чемпиона из его
            // джангл-набора. Роль не раскрыта — считаем всю половину, как раньше.
            if (mateRole.Length > 0 && role != mateRole) continue;

            var ids = champs.Where(c => c != 0).Distinct().ToList();
            if (ids.Count == 0) continue;

            var lcu  = DbToLcuRole(role);
            var seat = new DraftPlayer(CellId: -100, ChampionId: 0, PickIntentId: 0,
                                       Position: lcu, IsLocalPlayer: true);
            // Мой слот ОСТАЁТСЯ союзником со своим чемпионом: иначе у его
            // кандидатов пропадает структурная связка со мной (адк↔саппорт,
            // лес↔линия), а она часть той же формулы. Пару со мной BestPartner
            // добавляет не целиком, а НАДБАВКОЙ над обычным союзником — ровно
            // так, как её добавляет движок в моей собственной оценке.
            var team = state.MyTeam
                .Select(p => p.IsLocalPlayer ? p with { IsLocalPlayer = false } : p)
                .Append(seat)
                .ToList();

            var swapped = state with
            {
                MyTeam = team, Me = seat, MyPosition = lcu, DirectOpponent = null,
            };

            _forMate = true;
            try
            {
                foreach (var r in Recommend(swapped, ids.Count, ids))
                    res[r.ChampionId] = r.Score;
            }
            finally { _forMate = false; }
        }
        return res;
    }

    /// <summary>
    /// Кого из половины друга брать НАПАРНИКУ под мой пик.
    ///
    /// Нужно, чтобы дуо-пул предлагал пару СРАЗУ, как кончились баны, а не ждал
    /// чужого хода: напарник чаще всего пикает после меня, и к моему ходу у него
    /// пусто — а подсказка «что брать обоим» нужна как раз заранее.
    ///
    /// Складываем две вещи: его собственную силу в этом драфте
    /// (<see cref="PartnerScores"/> — враги, союзники, структурные связки,
    /// баланс урона; я там уже союзник с моим чемпионом) и НАДБАВКУ за то, что
    /// он не случайный союзник, а напарник: <c>W_SYNERGY · (DUO_MATE_MULT−1) ·
    /// доверие · дельта пары</c>. Ровно тем же слагаемым и с тем же порогом по
    /// совместным играм движок считает напарника в МОЕЙ оценке, так что формула
    /// у подсказки и у подбора одна, и своего веса тут не выдумано.
    ///
    /// Занятых и забаненных не предлагаем — их уже не взять.
    /// </summary>
    public int BestPartner(DraftState state, int mineId, string myRole,
                           IReadOnlyDictionary<string, List<int>> friendHalf,
                           IReadOnlyDictionary<int, double>? partnerScores = null,
                           string mateRole = "")
    {
        if (mineId == 0 || friendHalf.Count == 0) return 0;
        var scores = partnerScores ?? PartnerScores(state, friendHalf, mateRole);
        var conf   = MateConfidence();

        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            // Свой слот не исключаем из занятых только для СЕБЯ; напарнику мой
            // ховер брать нельзя — он занят так же, как чужой.
            if (p.EffectiveChampionId != 0) taken.Add(p.EffectiveChampionId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        var best = 0;
        var bestTotal = double.NegativeInfinity;
        foreach (var (role, champs) in friendHalf)
        {
            // Та же калитка, что в PartnerScores: кандидаты вне его роли там не
            // считались вовсе, и сюда они приходили бы с нулём — а ненулевая
            // надбавка за пару всё равно могла бы их вытащить вперёд.
            if (mateRole.Length > 0 && role != mateRole) continue;

            foreach (var f in champs)
            {
                if (f == 0 || f == mineId || taken.Contains(f)) continue;
                var own   = scores.TryGetValue(f, out var s) ? s : 0.0;
                var pair  = PairStats(mineId, myRole, f, role).Delta;
                // Надбавка, а не вся пара: обычную синергию со мной его оценка
                // уже посчитала — я в его команде союзником. Добавляем ровно то,
                // чем дуо-напарник отличается от случайного, и так же ужимаем по
                // совместным играм.
                var total = own + W_SYNERGY * (DUO_MATE_MULT - 1.0) * conf * pair;
                if (total > bestTotal) { bestTotal = total; best = f; }
            }
        }
        return best;
    }
}
