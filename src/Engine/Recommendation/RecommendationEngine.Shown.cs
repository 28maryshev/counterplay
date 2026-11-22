namespace Counterplay;

// Движок подбора — союзник показал чемпиона, но ещё не взял. Всё, что оценка
// кандидата берёт от союзников (синергия, связки, дыры состава, уязвимости,
// баланс урона), считается с его наведённым и без него и смешивается:
// напарник по паре линий весит HOVER_TRUST_PAIR, прочие — HOVER_TRUST.
public sealed partial class RecommendationEngine
{
    /// Союзники для слагаемых оценки: чемпионы с ролями, лесник и стрелок
    /// (связки), дыры состава.
    private sealed record AllySet(
        IReadOnlyList<(int Id, string Role)> Data, IReadOnlyList<int> Ids,
        int JungleId, int AdcId, IReadOnlyList<TeamGap> Gaps);

    /// <summary>
    /// Союзники на этом драфте: все показанные (<c>Shown</c>) и смесь наборов,
    /// из которой считаются числа оценки (<c>Parts</c>, веса в сумме 1).
    /// <c>ShownPartners</c> — наведённые, но не взятые напарники по паре линий:
    /// по ним тег «Хорош с показанным».
    /// </summary>
    private sealed record AllyMix(
        AllySet Shown, IReadOnlyList<(AllySet Set, double Weight)> Parts, IReadOnlyList<int> ShownPartners);

    /// <summary>
    /// Что оценка кандидата берёт от союзников. Числа (<c>Synergy</c>,
    /// <c>VulnPen</c>, <c>DmgDelta</c>, <c>Struct</c>, <c>Need</c>) — уже
    /// смешанные: они идут в счёт и в полоски карточки, чтобы объяснение и счёт
    /// говорили одно. Пары по союзникам, уязвимость, баланс урона и строки
    /// связок — по всем показанным: строки и иконки говорят о тех, кто стоит в
    /// слотах.
    /// </summary>
    private sealed record AllyTerms(
        double Synergy, List<(int Id, string Role, double Delta)> ByAlly,
        (double Penalty, ItemValue.Cat? Cat, int Count) Vuln, double VulnPen,
        (double Delta, bool Stack, bool Ad) Dmg, double DmgDelta,
        double Struct, List<(string Kind, string Text)> StructReasons, double Need);

    /// Чей слот считать выбором, даже если он не залочен: мой слот в подсказке
    /// напарнику (<see cref="PartnerScores"/>) — свой пик я знаю. -1 — ничей.
    /// Поле, как и <see cref="_forMate"/>: ставится на время вызова и
    /// снимается в finally.
    private int _sureCell = -1;

    /// <summary>
    /// Пары линий, где союзник решает больше прочих (владелец, 22.11.2026):
    /// стрелок и саппорт, лес и мид, лес и топ — в любую сторону.
    /// </summary>
    internal static bool IsLanePair(string myRole, string allyRole) =>
        (myRole, allyRole) is ("adc", "support") or ("support", "adc")
                           or ("jungle", "mid") or ("mid", "jungle")
                           or ("jungle", "top") or ("top", "jungle");

    /// <summary>
    /// Наборы союзников для оценки. Надёжный — тот, кто взял
    /// (<see cref="AllyWaver.Locked"/>), напарник по пати (договариваетесь
    /// голосом) и мой слот в подсказке напарнику. Наведённых нет — один набор
    /// с весом 1, оценка та же, что до 22.11.
    ///
    /// Иначе — вложенная смесь: все показанные с весом <c>HOVER_TRUST</c>,
    /// надёжные плюс наведённые напарники по паре — <c>HOVER_TRUST_PAIR −
    /// HOVER_TRUST</c>, одни надёжные — остальное. Так каждый наведённый
    /// присутствует в оценке ровно со своим весом: напарник по паре —
    /// <c>HOVER_TRUST_PAIR</c>, прочие — <c>HOVER_TRUST</c>, и это при любом
    /// числе наведённых. Совпадающие наборы сливаются, лишнего расчёта нет.
    /// </summary>
    private AllyMix AllySets(DraftState state, string myRole, IReadOnlyList<int> enemyIds, int mateId)
    {
        var shown = state.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0).ToList();
        var sure = shown.Where(p => AllyWaver.Locked(state, p) || p.CellId == _sureCell
                                    || (mateId != 0 && p.EffectiveChampionId == mateId)).ToList();
        var partners = shown.Except(sure).Where(p => IsLanePair(myRole, LcuToDbRole(p.Position))).ToList();
        AllySet Of(List<DraftPlayer> ps)
        {
            var data = ps.Select(p => (Id: p.EffectiveChampionId, Role: LcuToDbRole(p.Position))).ToList();
            var ids = data.Select(a => a.Id).ToList();
            return new AllySet(data, ids,
                               data.FirstOrDefault(a => a.Role == "jungle").Id,
                               data.FirstOrDefault(a => a.Role == "adc").Id,
                               TeamGaps(ids, enemyIds, myRole));
        }
        var all = Of(shown);
        if (sure.Count == shown.Count) return new AllyMix(all, [(all, 1.0)], []);

        var withPartners = sure.Concat(partners).ToList();
        var parts = new List<(AllySet, double)>();
        // Наведённые только напарники — «надёжные + напарники» и есть все.
        if (withPartners.Count == shown.Count) parts.Add((all, HOVER_TRUST_PAIR));
        else
        {
            parts.Add((all, HOVER_TRUST));
            if (partners.Count > 0) parts.Add((Of(withPartners), HOVER_TRUST_PAIR - HOVER_TRUST));
        }
        var rest = 1.0 - parts.Sum(p => p.Item2);
        parts.Add((Of(sure), rest));
        return new AllyMix(all, parts, [.. partners.Select(p => p.EffectiveChampionId)]);
    }

    /// <summary>
    /// Слагаемые от союзников для кандидата: каждое число — по каждому набору
    /// смеси (<see cref="AllySets"/>) с его весом. Без наведённого его слот
    /// пуст, то есть подбор знает о нём столько же, сколько о враге, которого
    /// ещё не видно, — поэтому на раннем пике заметную часть работы делает
    /// расчёт вслепую. Синергия по каждому набору складывается из одних и тех
    /// же строк базы, второй раз в базу не ходит.
    /// </summary>
    private AllyTerms AllyTermsFor(int champId, string myRole, double rawBase, AllyMix mix,
                                   IReadOnlyList<int> enemyIds, int mateId, double mateConf)
    {
        var shown = mix.Shown;
        var raw = SynergyRaw(champId, myRole, shown.Data);
        var byAlly = raw.Select(x => (x.Id, x.Role, Delta: PairSynergyDelta(x.G, x.W, rawBase))).ToList();
        var vuln = ItemValue.VulnPenalty(champId, shown.Ids);
        var dmg = ItemValue.DamageBalance(champId, shown.Ids, enemyIds);
        var (_, stReasons) = StructuralBonus(champId, myRole, shown.JungleId, shown.AdcId, shown.Ids);

        double syn = 0, vulnPen = 0, dmgDelta = 0, st = 0, need = 0;
        foreach (var (set, w) in mix.Parts)
        {
            var ids = set.Ids.ToHashSet();
            syn      += w * SynergyPool([.. raw.Where(x => ids.Contains(x.Id))], rawBase, mateId, mateConf);
            vulnPen  += w * ItemValue.VulnPenalty(champId, set.Ids).Penalty;
            dmgDelta += w * ItemValue.DamageBalance(champId, set.Ids, enemyIds).Delta;
            st       += w * StructuralBonus(champId, myRole, set.JungleId, set.AdcId, set.Ids).Bonus;
            need     += w * NeedBonus(champId, set.Gaps, set.Ids.Count);
        }
        return new AllyTerms(syn, byAlly, vuln, vulnPen, dmg, dmgDelta, st, stReasons, need);
    }
}
