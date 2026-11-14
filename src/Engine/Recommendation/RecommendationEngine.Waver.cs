namespace Counterplay;

// Движок подбора — союзник мечется между чемпионами (AllyWaver): подбор по
// среднему из показанных им, а не по нынешнему наведению.
public sealed partial class RecommendationEngine
{
    /// <summary>
    /// Кто из союзников мечется (<see cref="DraftState.Wavering"/>) и из кого
    /// он ещё может выбрать: показанные им, кроме забаненных, залоченных кем
    /// угодно и наведённых другими союзниками. Залочивший уже не мечется — его
    /// считает обычный подбор (<see cref="AllyWaver.Locked"/>: в свой ход
    /// наведённый стоит в ChampionId, и это ещё не лок). Список чемпионов по возрастанию номера: одно и
    /// то же среднее при любом порядке показа, до последнего знака.
    /// </summary>
    private static List<(int Cell, int[] Champs)> Waverers(DraftState s)
    {
        if (s.Wavering is not { Count: > 0 } w) return [];
        var res = new List<(int Cell, int[] Champs)>();
        foreach (var p in s.MyTeam)
        {
            if (p.IsLocalPlayer || AllyWaver.Locked(s, p) || !w.TryGetValue(p.CellId, out var shown)) continue;
            var gone = new HashSet<int>(s.MyTeamBans.Concat(s.TheirTeamBans));
            foreach (var q in s.MyTeam.Concat(s.TheirTeam))
            {
                // Свой слот — не помеха себе: в свой ход его наведённый стоит в ChampionId.
                if (ReferenceEquals(q, p)) continue;
                if (q.ChampionId != 0) gone.Add(q.ChampionId);
                else if (!q.IsLocalPlayer && q.PickIntentId != 0) gone.Add(q.PickIntentId);
            }
            res.Add((p.CellId, [.. shown.Where(c => c != 0 && !gone.Contains(c)).Distinct().Order()]));
        }
        return res;
    }

    /// <summary>
    /// Подбор, когда союзник мечется. Счёт кандидата — среднее его счёта по
    /// всем чемпионам, которых союзник показал: драфт считается по разу на
    /// каждого (союзник «взял» его), и счёты усредняются. Переход к уже
    /// показанному чемпиону поэтому не меняет ничего — центр стоит.
    ///
    /// Остальное в карточке (доводы, теги, полоски, кроме синергии) — от драфта,
    /// где этого союзника ещё нет: доводы про «хорош с X» назвали бы того, кого
    /// он, может, и не возьмёт, и менялись бы на каждом наведении. Полоска и тег
    /// синергии — тоже по среднему, чтобы не спорить со счётом.
    ///
    /// Его показанных не советуем (hold): он может взять любого, так же как его
    /// наведённого не советует обычный подбор (<see cref="TakenBy"/>). Мой
    /// собственный пик остаётся, даже если союзник его показывал.
    ///
    /// Если мечутся двое, вклад каждого считается при пустом слоте другого и
    /// прибавляется к драфту без обоих: пересчётов 1 + сумма показанных, а не
    /// их произведение. Для одного это и есть точное среднее.
    /// </summary>
    private List<Recommendation> ScoreWavering(DraftState state, string myRole, IReadOnlyList<int> candidates,
                                               int mateId, List<(int Cell, int[] Champs)> waver)
    {
        var cells = waver.Select(w => w.Cell).ToHashSet();
        // Его слот пуст: и намерение, и наведённый в свой ход (он в ChampionId).
        var team = state.MyTeam
            .Select(p => cells.Contains(p.CellId) ? p with { ChampionId = 0, PickIntentId = 0 } : p).ToList();
        var neutral = state with { MyTeam = team, Wavering = null };
        var myPickId = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;
        var hold = waver.SelectMany(w => w.Champs).Where(c => c != myPickId).ToHashSet();

        // Напарник по дуо — человек; если мечется он сам, в каждом пересчёте его
        // чемпион — тот, кого он «взял», а без него напарника нет.
        var mateCell = mateId == 0 ? -1
            : state.MyTeam.FirstOrDefault(p => !p.IsLocalPlayer && p.EffectiveChampionId == mateId)?.CellId ?? -1;

        var bas = ScoreAll(neutral, myRole, candidates, cells.Contains(mateCell) ? 0 : mateId, hold);
        var score = bas.ToDictionary(r => r.ChampionId, r => r.Score);
        var syn   = bas.ToDictionary(r => r.ChampionId, r => r.SynergyDelta);

        foreach (var (cell, champs) in waver)
        {
            if (champs.Length == 0) continue;   // всех его показанных уже забрали — его как нет
            var sumScore = new Dictionary<int, double>();
            var sumSyn   = new Dictionary<int, double>();
            foreach (var c in champs)
            {
                var took = neutral with
                {
                    MyTeam = [.. team.Select(p => p.CellId == cell ? p with { PickIntentId = c } : p)],
                };
                var mate = cell == mateCell ? c : cells.Contains(mateCell) ? 0 : mateId;
                foreach (var r in ScoreAll(took, myRole, candidates, mate, hold))
                {
                    sumScore[r.ChampionId] = sumScore.GetValueOrDefault(r.ChampionId) + r.Score;
                    sumSyn[r.ChampionId]   = sumSyn.GetValueOrDefault(r.ChampionId) + r.SynergyDelta;
                }
            }
            foreach (var r in bas)
            {
                if (!sumScore.TryGetValue(r.ChampionId, out var s)) continue;
                score[r.ChampionId] += s / champs.Length - r.Score;
                syn[r.ChampionId]   += sumSyn[r.ChampionId] / champs.Length - r.SynergyDelta;
            }
        }

        return bas
            .Select(r => r with
            {
                Score = score[r.ChampionId],
                SynergyDelta = syn[r.ChampionId],
                Facts = r.Facts is { } f ? f with { Synergy = syn[r.ChampionId] } : null,
            })
            .OrderByDescending(r => r.Score)
            .ToList();
    }
}
