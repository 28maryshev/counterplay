using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — ARAM: подбор со скамейки.
public sealed partial class RecommendationEngine
{
    // ---------- ARAM: подбор по скамейке ----------

    /// ARAM: врагов не видно, но есть скамейка. Из [мой текущий + скамейка] выбираем
    /// пик, лучше всего дополняющий команду: синергия, смешанный урон, закрытие «дыр»
    /// композиции (фронт/саст/поук), сила чемпиона, комфорт. ARAM-данных нет —
    /// синергию/WR берём из SR как слабый сигнал, остальное эвристикой по трейтам.
    public IReadOnlyList<Recommendation> RecommendAram(DraftState state, int topN = 6)
    {
        _comfortPool = [];   // в ARAM пулов нет — только наигранность
        var me = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer);
        var myChamp = me?.EffectiveChampionId ?? 0;

        var cands = new List<int>();
        if (myChamp != 0) cands.Add(myChamp);
        cands.AddRange(state.Bench);
        cands = cands.Where(c => c != 0).Distinct().ToList();
        if (cands.Count == 0) return [];

        var allyIds = state.MyTeam
            .Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
            .Select(p => p.EffectiveChampionId).ToList();

        // «Дыры» композиции команды (без меня). Фронт = танк/бойец/заход (не просто cc).
        bool hasFront = allyIds.Any(a => ChampionTraits.IsTanky(a) || ChampionTags.Has(a, "engage"));
        bool hasHeal  = allyIds.Any(ChampionTraits.HealReliant);

        var scored = cands.Select(champId =>
        {
            var (bg, bw)  = RawBaseAny(champId);
            var rawBase   = Delta(bg, bw, K);
            var baseDelta = rawBase * (bg / (bg + BASE_CONF));

            // Чистая синергия (минус собственная база) — как в основном подборе.
            var syn = allyIds.Select(a => RawSynergyAny(champId, a)).ToList();
            var synGames = syn.Sum(x => x.g);
            var synDelta = syn.Count > 0 && synGames > 0
                ? (Delta(synGames, syn.Sum(x => x.w), K_PAIR) - rawBase) * (synGames / (synGames + CONF_GAMES)) : 0.0;

            // Баланс типа урона (в ARAM смешанный урон особенно важен). Врагов нет.
            var (dmgDelta, _, _) = ItemValue.DamageBalance(champId, allyIds, []); // влияет на скор, без текста

            double gap = 0;
            var reasons = new List<string>();
            if (!hasFront && (ChampionTraits.IsTanky(champId) || ChampionTags.Has(champId, "engage")))
            { gap += 2.5; reasons.Add(Good(Loc.T("reason.aramFront"))); }
            if (!hasHeal && ChampionTraits.HealReliant(champId))
            { gap += 1.5; reasons.Add(Good(Loc.T("reason.aramHeal"))); }
            if (ChampionTraits.LongRange(champId)) gap += 0.8;

            var comfort = ComfortDelta(champId);
            if      (comfort >= 3.8) reasons.Add(Good(Loc.T("reason.comfortHigh")));
            else if (comfort >= 1.9) reasons.Add(Good(Loc.T("reason.comfortMid")));

            if (synDelta > 0.5) reasons.Add(Good(Loc.T("reason.fitsTeam")));
            if      (baseDelta >= 2.5) reasons.Add(Good(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}")));
            else if (baseDelta >= 1.0) reasons.Add(Good(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}")));
            if (reasons.Count == 0) reasons.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));

            var score = W_ARAM_BASE * baseDelta + W_ARAM_SYN * synDelta
                      + W_ARAM_DMG * dmgDelta + W_ARAM_GAP * gap + W_POOL * comfort;
            return new Recommendation(champId, score, baseDelta, 0, 0, synDelta, comfort, 0, [.. reasons]);
        })
        .OrderByDescending(r => r.Score)
        .ToList();

        return scored.Take(topN)
            .Select((r, i) => r with { Rank = i + 1, IsMyPick = r.ChampionId == myChamp })
            .ToList();
    }

    // ARAM: базовый WR чемпиона без учёта роли (по всем ролям).
    private (double g, double w) RawBaseAny(int champId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM base_wr
            WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // ARAM: синергия пары без учёта роли (сумма по всем ролям, обе стороны пары).
    private (double g, double w) RawSynergyAny(int champId, int allyId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM (
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@c AND ally_id=@a AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                UNION ALL
                SELECT games*{PW} AS games, wins*{PW} AS wins FROM synergy
                  WHERE champion_id=@a AND ally_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            )";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }

    // Матчап без учёта роли (сумма по всем ролям) — для банов, когда роль своя или
    // союзника ещё не назначена (ranked solo/duo, блайнд): защиту всё равно считаем.
    private (double g, double w) RawMatchupAny(int champId, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT COALESCE(SUM(games*{PW}),0), COALESCE(SUM(wins*{PW}),0) FROM matchup
            WHERE champion_id=@c AND vs_champion_id=@v AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return RawAgg(cmd);
    }
}
