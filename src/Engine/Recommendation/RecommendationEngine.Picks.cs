using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — подбор пиков: Recommend и всё, из чего складывается оценка кандидата
// (матчапы, стиль, синергия пары, баланс урона, структура состава).
public sealed partial class RecommendationEngine
{
    // ── Доли урона чемпиона (физ/маг/чистый) ────────────────────────────────
    // Замеры из матчей (таблица champion_damage). Пока её нет в скачанной базе —
    // возвращаем null, и оверлей падает на грубую оценку Data Dragon.
    private bool? _dmgTable;

    private readonly Dictionary<int, (double P, double M, double T)?> _dmgCache = new();

    public (double Phys, double Magic, double True)? DamageShare(int champId)
    {
        if (_dmgTable == false) return null;
        if (_dmgCache.TryGetValue(champId, out var hit)) return hit;

        (double, double, double)? res = null;
        try
        {
            if (_dmgTable is null)
            {
                var chk = _db.CreateCommand();
                chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='champion_damage'";
                _dmgTable = chk.ExecuteScalar() is not null;
                if (_dmgTable == false) return null;
            }

            var cmd = _db.CreateCommand();
            cmd.CommandText = @"
                SELECT COALESCE(SUM(phys),0), COALESCE(SUM(magic),0), COALESCE(SUM(trued),0)
                FROM champion_damage
                WHERE champion_id=@c AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
            cmd.Parameters.AddWithValue("@c",  champId);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            if (rd.Read())
            {
                double p = rd.GetDouble(0), m = rd.GetDouble(1), t = rd.GetDouble(2);
                var sum = p + m + t;
                if (sum > 0) res = (p / sum, m / sum, t / sum);
            }
        }
        catch { _dmgTable = false; }

        _dmgCache[champId] = res;
        return res;
    }

    public IReadOnlyList<Recommendation> Recommend(DraftState state, int topN = 6,
                                                   IReadOnlyCollection<int>? only = null)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];
        var wDirect = DirectWeight(myRole); // вес прямой контры зависит от роли

        // Чемпионы моего активного пула на эту роль — им «комфорт» пула (см.
        // ComfortDelta). Normal/пул не активен → пусто, флора нет.
        _comfortPool = [.. PoolStore.ActiveForRole(myRole).Mine];

        // only — переопределение кандидатов (пул игрока): считаем ЛУЧШЕГО из пула
        // той же логикой, даже если он не проходит порог общего списка.
        var candidates = only?.ToList() ?? GetCandidates(myRole);
        Console.WriteLine($"  [диаг] role={myRole}  tier={TierBucket}  патчи={PatchDisplay}  кандидатов={candidates.Count}");

        // Исключаем из кандидатов: залоченные пики обеих команд, баны, а также
        // чемпионов, наведённых (ховер) ЧУЖИМИ слотами — их уже не запикать.
        // СВОЙ пик/ховер НЕ исключаем: хочу видеть его в списке (насколько «угадал»).
        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
        {
            if (p.IsLocalPlayer) continue;            // мой слот — оставляем в подборе
            if (p.ChampionId != 0) taken.Add(p.ChampionId);
            else if (p.PickIntentId != 0) taken.Add(p.PickIntentId);
        }
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        // Мой уже выбранный (или наведённый) чемпион — чтобы пометить и гарантированно показать.
        var myPickId = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;

        // Учитываем ховеры (EffectiveChampionId): рекомендации обновляются ещё
        // на этапе наведения чемпиона союзником/врагом, не дожидаясь лока.
        var directOppId = state.DirectOpponent?.EffectiveChampionId ?? 0;
        // Роли врагов скрыты (Blind/Solo-Duo) → прямого оппонента по позиции нет.
        // Определяем его эвристикой: враг, у которого частая роль = моей.
        if (directOppId == 0)
            directOppId = InferDirectOpponent(state, myRole);

        var allyData = state.MyTeam
            .Where(p => p.EffectiveChampionId != 0 && !p.IsLocalPlayer)
            .Select(p => (Id: p.EffectiveChampionId, Role: LcuToDbRole(p.Position))).ToList();

        // Напарник по активному дуо-пулу — ЧЕЛОВЕК из моей пати (см. Party).
        // По чемпиону его больше не ищем: у друга с широким пулом случайный
        // союзник, взявший оттуда чемпиона, сходил за напарника.
        var mateId = Party.MateChampion(state, PoolStore.ActiveDuo());

        // Сколько мы играем вдвоём СЕЙЧАС: от этого зависит, насколько громко
        // звучит дуо-вес (MATE_CONF). Считается ОДИН раз на драфт, а не на
        // чемпиона: журнал один и тот же, а кандидатов четыре сотни.
        var mateConf = mateId == 0 ? 0.0 : MateConfidence();

        // Бот — это 2v2: при адк/саппорте контрим и вражеского дуо-партнёра.
        // Пример: вражеский Эзреаль (адк) контрит Блицкранга (саппорт) — он сблинкуется
        // с хука, поэтому Блиц получит штраф против такого бота.
        var duoRole = myRole == "adc" ? "support" : myRole == "support" ? "adc" : null;
        var enemyDuoId = duoRole == null ? 0 : state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0 && LcuToDbRole(p.Position) == duoRole)
            .Select(p => p.EffectiveChampionId).FirstOrDefault();
        // Если виден ВЕСЬ вражеский бот (и прямой оппонент, и дуо) — даём боту
        // чуть больший вес: пик можно подогнать под известный 2v2.
        var wBotlane = (duoRole != null && enemyDuoId != 0 && directOppId != 0)
            ? W_BOTLANE_BOTH : W_BOTLANE;

        // ПРОЧИЕ враги (не лейн-оппонент и не дуо-партнёр — те покрыты своими
        // факторами): каждому определяем роль (позиция, иначе частая роль по
        // базе). В скоре на каждого сперва пробуем кросс-ролевой матчап (реальная
        // пара «моя роль против его роли», сигнал ~1.8 пп), а если такой пары в
        // базе ещё нет — фолбэк на same-role таблицу с меньшим весом.
        var otherEnemies = new List<(int Id, string Role)>();
        foreach (var p in state.TheirTeam)
        {
            var id = p.EffectiveChampionId;
            if (id == 0 || id == directOppId) continue;
            if (duoRole != null && id == enemyDuoId) continue;   // покрыт botlane-фактором
            var r = LcuToDbRole(p.Position);
            if (string.IsNullOrEmpty(r)) r = InferPrimaryRole(id);
            otherEnemies.Add((id, r));
        }

        // Динамический вес синергии: без информации о врагах синергия с союзниками
        // важнее, но потолок умеренный (1.5): по замерам сигнал синергии (~1.5 пп)
        // слабее вражеских матчапов, и раздутый вес делал подбор «глухим» к пикам
        // врагов в начале драфта (плюс чемпион на десятке совместных игр всплывал
        // в топ при пустой вражеской команде).
        const double W_SYN_MAX = 1.5;
        var knownEnemies = state.TheirTeam.Count(p => p.EffectiveChampionId != 0);
        var wSynergy = W_SYNERGY + (W_SYN_MAX - W_SYNERGY) * Math.Max(0.0, 1.0 - knownEnemies / 5.0);

        // ── Драфт-фичи (архетип/нейтральность) ──────────────────────────────
        var allEnemyIds = state.TheirTeam
            .Where(p => p.EffectiveChampionId != 0).Select(p => p.EffectiveChampionId).ToList();
        // Неопределённость драфта: 1.0 если врагов не видно, 0 если все 5 + есть оппонент.
        var uncertainty = Math.Clamp(
            1.0 - knownEnemies / 5.0 + (directOppId == 0 ? 0.2 : 0.0), 0.0, 1.0);
        // Доминирующий архетип врага определяем при 2+ известных пиках.
        ChampionTraits.Arch? enemyDom = allEnemyIds.Count >= 2
            ? ChampionTraits.Dominant(allEnemyIds) : null;

        // Item value (п.1): союзники (без меня) и враги для профиля уязвимостей.
        var vulnAllyIds = allyData.Select(a => a.Id).ToList();

        // Структурная синергия (п.4): id союзных джанглера и АДК.
        var jungleAllyId = allyData.FirstOrDefault(a => a.Role == "jungle").Id;
        var adcAllyId    = allyData.FirstOrDefault(a => a.Role == "adc").Id;

        var ordered = candidates
            .Where(id => !taken.Contains(id))
            .Select(champId =>
            {
                // Базовый WR + темпер по выборке: мало игр → WR ближе к 50%
                // (иначе «68% на 100 играх» раздувает оценку и обоснование).
                var (bg, bw)  = RawBase(champId, myRole);
                var rawBase   = Delta(bg, bw, K);   // сила чемпиона без темпера — база для чистых дельт
                var baseDelta = rawBase * (bg / (bg + BASE_CONF));

                // ЧИСТАЯ контра: матчап-WR минус собственный базовый WR — иначе сила
                // чемпиона считалась бы дважды (в базе ×1.0 и внутри контры ×W_DIRECT),
                // и мета-чемпионы возглавляли бы список «как контрпики». Плюс темпер
                // по объёму пары: 4 игры 4 победы больше не дают фантомных +8пп.
                double PureVs(double g, double w) =>
                    g > 0 ? (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF)) : 0.0;

                // Прямой оппонент — отдельный матчап
                var (dg, dw)    = directOppId != 0 ? RawMatchup(champId, myRole, directOppId) : (0, 0);
                var directDelta = PureVs(dg, dw);

                // Прочие враги: по каждому сперва кросс-ролевой матчап (реальный
                // контекст пары, сигнал ~1.8 пп), если его в базе нет — same-role
                // фолбэк. Каждый пул складывает игры/победы и сглаживается один
                // раз — редкие пары не обнуляются делением на количество врагов.
                double oxg = 0, oxw = 0;                      // пул кросс-матчапов
                double omg = 0, omw = 0;                      // пул same-role фолбэка
                var otherByEnemy = new List<(int Id, double Delta)>();
                foreach (var (eid, erole) in otherEnemies)
                {
                    var (cg, cw) = erole.Length > 0
                        ? RawBotlane(champId, myRole, eid, erole) : (0.0, 0.0);
                    if (cg > 0)
                    {
                        oxg += cg; oxw += cw;
                        otherByEnemy.Add((eid, PureVs(cg, cw)));
                    }
                    else
                    {
                        var (mg, mw) = RawMatchup(champId, myRole, eid);
                        omg += mg; omw += mw;
                        otherByEnemy.Add((eid, PureVs(mg, mw)));
                    }
                }
                var otherDelta = PureVs(omg, omw);
                var crossDelta = PureVs(oxg, oxw);

                // Синергия с союзниками — общий расчёт (AllySynergy): им же советы
                // банов взвешивают мой пул.
                var (synDelta, synByAlly) = AllySynergy(champId, myRole, rawBase, allyData, mateId, mateConf);

                var comfortDelta = ComfortDelta(champId); // наигранность игрока

                // Драфт-фичи: нейтральность, трифекта, анти-стиль.
                var (draftBonus, styleScore, draftReasons) =
                    DraftFit(champId, enemyDom, uncertainty);

                // Item value (п.1) + баланс типа урона (AD/AP): влияют на СКОР (штраф за
                // стак уязвимости/типа урона, бонус за наказание врага), но в тексте
                // причин НЕ выводятся — уязвимости показываем строкой итем-иконок внизу.
                var (vulnPen, _, _)   = ItemValue.VulnPenalty(champId, vulnAllyIds);
                var (exploit, _)      = ItemValue.ExploitBonus(champId, allEnemyIds);
                var (dmgDelta, _, _)  = ItemValue.DamageBalance(champId, vulnAllyIds, allEnemyIds);

                // Структурная синергия джангл↔саппорт (п.4).
                var (structBonus, structReasons) = StructuralBonus(champId, myRole, jungleAllyId, adcAllyId, vulnAllyIds);
                draftReasons.AddRange(structReasons);

                // Бот 2v2: матчап против вражеского дуо-партнёра (кросс-роль).
                var (btg, btw)   = _botlaneReady && enemyDuoId != 0 && duoRole != null
                    ? RawBotlane(champId, myRole, enemyDuoId, duoRole) : (0.0, 0.0);
                var botlaneDelta = PureVs(btg, btw);

                if (botlaneDelta >= 0.8)
                    draftReasons.Add(Good(Loc.T("reason.botlaneGood", DataDragon.Name(enemyDuoId))));
                else if (botlaneDelta <= -1.2)
                    draftReasons.Add(Bad(Loc.T("reason.botlaneBad", DataDragon.Name(enemyDuoId))));

                var personalDelta = PersonalDelta(champId);   // «у меня на нём идёт»

                // Против того, кто тебя контрит, личная прибавка ужимается.
                // Берём ту же величину, что показана в карточке строкой «против
                // оппонента» (матчап + бот 2v2): объяснение и счёт должны
                // говорить об одном и том же. Обе величины правим ЗДЕСЬ, до
                // причин и до записи в карточку, — иначе бар комфорта обещал бы
                // то, чего в оценке уже нет.
                var guard = CounterGuard(directDelta + botlaneDelta);
                comfortDelta  *= guard;
                personalDelta *= guard;

                var score   = W_BASE * baseDelta + wDirect * directDelta + W_OTHER * otherDelta
                            + wSynergy * synDelta + W_POOL * comfortDelta + draftBonus
                            - W_VULN * vulnPen + W_EXPLOIT * exploit + W_STRUCT * structBonus
                            + wBotlane * botlaneDelta + W_CROSS * crossDelta + W_DMGBAL * dmgDelta
                            + W_PERSONAL * personalDelta;
                var reasonList = BuildReasons(champId, directDelta, directOppId, synDelta, synByAlly,
                                              otherDelta, otherByEnemy, baseDelta, comfortDelta)
                                   .Concat(draftReasons).ToList();
                // Личный винрейт показываем только когда он заметен — иначе строка
                // «твой винрейт» висела бы у каждого второго кандидата.
                if (personalDelta >= 1.5)
                {
                    var (pg, pw) = MyHistory().Recent(champId);
                    if (pg > 0)
                        reasonList.Add(Good(Loc.T("reason.personalWr", $"{100.0 * pw / pg:F0}", pg)));
                }
                var reasons = reasonList.ToArray();

                // «Против оппонента» на нижней линии — это вся вражеская пара, а не
                // один визави. Саппорту важнее всего, как он играется против ЧУЖОГО
                // АДК (и наоборот), но раньше полоска показывала только матчап со
                // своей же ролью: пока вражеский саппорт не выбран, там стоял ноль,
                // хотя счёт уже учитывал контру против их адк.
                var shownDirect = directDelta + botlaneDelta;
                return new Recommendation(champId, score, baseDelta, shownDirect, otherDelta, synDelta, comfortDelta, styleScore, reasons);
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        // Кого игрок попросил не советовать (👎 на карточке): скрытых нет,
        // возвращающиеся — ниже своего места. Только в общем подборе: с only
        // считают названных поимённо (пул, мой пик, наведённого), и пул
        // отсеивает их сам (TopFromPool). Мой пик не трогаем.
        if (only is null)
            ordered = Dislikes.Apply(ordered, r => r.ChampionId, Dislikes.Kind.Pick, myRole, keep: myPickId);

        // Топ-6: мой уже выбранный чемпион остаётся в подборе наравне со всеми и
        // помечается, но НЕ пиннится — если из-за новых пиков он вышел из топа,
        // он выпадает из списка так же, как любой другой кандидат.
        return ordered.Take(topN)
            .Select((r, i) => r with { Rank = i + 1, IsMyPick = r.ChampionId == myPickId })
            .ToList();
    }

    /// До max ВЫГОДНЫХ пиков из пула (Score > 0), отсортированы по силе. Если ни
    /// один не выгоден против врагов — возвращаем ОДИН лучший (пул всегда что-то
    /// предлагает). Общий подбор идёт ниже этих карточек.
    public IReadOnlyList<Recommendation> TopFromPool(DraftState state, IReadOnlyCollection<int> poolChamps, int max = 3)
    {
        if (poolChamps.Count == 0) return [];
        // Не советовать — и из пула: игрок сам убрал чемпиона с карточки пула.
        var myPick = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;
        var scored = Dislikes.Apply(Recommend(state, poolChamps.Count, poolChamps), r => r.ChampionId,
                                    Dislikes.Kind.Pick, LcuToDbRole(state.MyPosition), keep: myPick);
        if (scored.Count == 0) return [];
        var good = scored.Where(r => r.Score > 0).Take(max).ToList();
        return good.Count > 0 ? good : [scored[0]];
    }

    /// Чистая контра кандидата ПРОТИВ КОНКРЕТНОГО врага (%пп) — та же формула, что
    /// внутри скоринга: матчап минус собственная база, темпер по объёму пары.
    /// Для подсветки врагов при наведении на карточку.
    public double VersusDelta(int champId, string myRole, int enemyId, string enemyRole)
    {
        if (champId == 0 || enemyId == 0) return 0.0;
        var (bg, bw) = RawBase(champId, myRole);
        var rawBase  = Delta(bg, bw, K);

        var (g, w) = enemyRole.Length > 0 ? RawBotlane(champId, myRole, enemyId, enemyRole) : (0.0, 0.0);
        if (g <= 0) (g, w) = RawMatchup(champId, myRole, enemyId);
        return g > 0 ? (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF)) : 0.0;
    }

    /// Вклад «против стиля» в расчёте на ОДНОГО врага: та же трифекта и тот же
    /// анти-стиль, что в DraftFit, но по архетипу конкретного чемпиона.
    public static double StyleVsArch(int champId, ChampionTraits.Arch enemyArch)
    {
        var (f2b, dive, pick) = ChampionTraits.Archetype(champId);
        var want = enemyArch switch
        {
            ChampionTraits.Arch.Dive     => f2b,
            ChampionTraits.Arch.PickPoke => dive,
            _                            => pick,
        };
        double style = enemyArch switch
        {
            ChampionTraits.Arch.PickPoke =>
                ChampionTraits.Gapclose(champId) + ChampionTraits.Engage(champId),
            ChampionTraits.Arch.Dive =>
                ChampionTraits.Peel(champId) + ChampionTraits.Disengage(champId),
            _ =>
                (ChampionTraits.LongRange(champId) ? 2 : 0) + ChampionTraits.Burst(champId),
        };
        return W_TRIFECTA * want + W_STYLE * style;
    }

    /// <summary>
    /// Синергия чемпиона моей роли с показанными союзниками — полоска «синергия»
    /// в карточке пика. Совместные игры со всеми союзниками складываются, и от их
    /// винрейта отнимается собственная база чемпиона (rawBase): «пара играет
    /// лучше, чем этот чемпион в среднем», а не «сильный чемпион хорош с кем
    /// угодно». Доверие — по объёму (CONF_GAMES): мало совместных игр — меньше
    /// доверия, чтобы «чемпионы на 8 играх» не доминировали при высоком весе без
    /// врагов. По каждому союзнику — его чистая дельта (для доводов).
    ///
    /// Надбавка за напарника: его связка со мной весит больше, чем связка со
    /// случайным союзником, и ложится в ТУ ЖЕ полосу, а не в скор отдельным
    /// слагаемым — иначе оценка росла бы, а объяснение в карточке осталось
    /// прежним. Дельта напарника уже сглажена по объёму, второй раз не
    /// темперим; умножаем на доверие к связке (mateConf): пока вы вдвоём не
    /// наиграли, надбавка почти не слышна — ответ на «случайный сопартиец гнёт
    /// совет не хуже настоящего напарника».
    ///
    /// Зовёт подбор пика; советы банов берут вес моего пула из самого подбора
    /// (PoolOdds), так что синергия там та же.
    /// </summary>
    private (double Delta, List<(int Id, string Role, double Delta)> ByAlly) AllySynergy(
        int champId, string myRole, double rawBase,
        IReadOnlyList<(int Id, string Role)> allies, int mateId, double mateConf)
    {
        var raw = allies
            .Select(a => { var (g, w) = RawSynergy(champId, myRole, a.Id, a.Role); return (a.Id, a.Role, G: g, W: w); })
            .ToList();
        var byAlly = raw.Select(x => (x.Id, x.Role,
            Delta: x.G > 0 ? (Delta(x.G, x.W, K_PAIR) - rawBase) * (x.G / (x.G + MATCHUP_CONF)) : 0.0)).ToList();
        var games = raw.Sum(x => x.G);
        var delta = raw.Count > 0 && games > 0
            ? (Delta(games, raw.Sum(x => x.W), K_PAIR) - rawBase) * (games / (games + CONF_GAMES)) : 0.0;
        if (mateId != 0)
        {
            var mate = byAlly.FirstOrDefault(x => x.Id == mateId);
            if (mate.Id != 0) delta += (DUO_MATE_MULT - 1.0) * mate.Delta * mateConf;
        }
        return (delta, byAlly);
    }

    /// <summary>
    /// Синергия моего чемпиона с показанными союзниками, когда линий не выдают
    /// (вслепую, своя игра): тот же расчёт, что у подбора (<see cref="AllySynergy"/>),
    /// — одна сглаженная дельта на всех союзников и вклад каждого. Мою роль
    /// передаёт вызывающий (та, на которой я играю чемпиона); роль союзника —
    /// позиция, если клиент её дал, иначе его главная роль в базе.
    /// </summary>
    public (double Delta, List<(int Id, double Delta)> ByAlly) BlindSynergy(
        int champId, string myRole, IReadOnlyList<(int Id, string Role)> allies)
    {
        var (bg, bw) = RawBase(champId, myRole);
        var roled = allies.Select(a => (a.Id, Role: a.Role.Length > 0 ? a.Role : InferPrimaryRole(a.Id))).ToList();
        var (delta, byAlly) = AllySynergy(champId, myRole, Delta(bg, bw, K), roled, 0, 0);
        return (delta, [.. byAlly.Select(x => (x.Id, x.Delta))]);
    }

    /// Чистая синергия ПАРЫ (m ↔ f), как дельта: WR пары минус база m, темпер по
    /// объёму. Роли маржинализуем (пара может быть любых линий).
    public double PairSynergy(int m, string mRole, int f, string? fRole = null)
    {
        // ПО РОЛИ, а не по всем сразу. Связка живёт на конкретной линии:
        // Мальфит с Ясуо на топе — 8034 игры и чистые +1,26, а если досыпать
        // его же лес и саппорт, где пара проигрывает, выходит −0,22. Кружок у
        // союзника зажигается от +0,3 — и настоящая связка до него не
        // доезжала, хотя в карточке та же пара стояла с плюсом.
        // Линия союзника учитывается там же (RawSynergy): «Ясуо на миде» и
        // «Ясуо в боте» — разные пары.
        var (g, w) = RawSynergy(m, mRole, f, fRole);
        // Нет данных по роли (редкий чемпион, свежий патч) — берём что есть:
        // приблизительно, но лучше, чем промолчать.
        if (g <= 0) (g, w) = RawSynergyAny(m, f);
        if (g <= 0) return 0.0;

        var (bg, bw) = RawBase(m, mRole);
        if (bg <= 0) return 0.0;
        var rawBase = Delta(bg, bw, K);
        // Темпер тот же, что у строки в карточке (PureVs): кружок и подпись
        // обязаны показывать одно и то же число, иначе они спорят друг с другом.
        return (Delta(g, w, K_PAIR) - rawBase) * (g / (g + MATCHUP_CONF));
    }

    /// Статистика фиксированной связки для показа в настройках:
    ///   Games — сколько совместных игр в данных (сила выборки),
    ///   Wr    — сырой винрейт связки (wins/games),
    ///   Delta — насколько связка сильнее, чем эти двое по отдельности
    ///           (темпер. WR пары минус средний базовый WR обоих чемпионов).
    /// Роли заданы → берём роль-специфичные данные; при нуле игр откат на маржинал.
    public (int Games, double Wr, double Delta) PairStats(int m, string? mRole, int f, string? fRole)
    {
        (double g, double w) syn = (0, 0);
        if (!string.IsNullOrEmpty(mRole) && !string.IsNullOrEmpty(fRole))
            syn = RawSynergyRoles(m, mRole!, f, fRole!);
        if (syn.g <= 0) syn = RawSynergyAny(m, f);   // откат: пары по ролям редки
        if (syn.g <= 0) return (0, 0, 0);

        var (mg, mw) = !string.IsNullOrEmpty(mRole) ? RawBase(m, mRole!) : RawBaseAny(m);
        var (fg, fw) = !string.IsNullOrEmpty(fRole) ? RawBase(f, fRole!) : RawBaseAny(f);
        var baseM = mg > 0 ? Delta(mg, mw, K) : 0.0;
        var baseF = fg > 0 ? Delta(fg, fw, K) : 0.0;
        var synDelta = Delta(syn.g, syn.w, K_PAIR) - (baseM + baseF) / 2.0;
        return ((int)Math.Round(syn.g), 100.0 * syn.w / syn.g, synDelta);
    }

    // Структурные правила синергии джангл↔саппорт (п.4): не из статистики, а из
    // логики команды («агро-джанглеру нужен сетап», «оллин-АДК нужен инициатор»).
    private static (double Bonus, List<string> Reasons) StructuralBonus(
        int champId, string myRole, int jungleAllyId, int adcAllyId, IReadOnlyList<int> allyIds)
    {
        double b = 0;
        var reasons = new List<string>();

        // Тимфайт-вомбо: у кандидата свой мощный AoE/канал-ульт, а у команды уже есть
        // ядро таких же ультов И кто-то умеет собрать/зафиксировать пачку → его ульт
        // добивает всю сгруппированную команду. Классика «стакай ульты на пачку».
        if (TeamSynergies.HasAoeUlt(champId))
        {
            int aoeAllies = allyIds.Count(TeamSynergies.HasAoeUlt);
            if (aoeAllies >= 2 && allyIds.Any(TeamSynergies.IsGrouper))
            {
                b += Math.Min(aoeAllies, 3) * 0.7; // +1.4 … +2.1 (×W_STRUCT)
                reasons.Add(Good(Loc.T("reason.womboAoe")));
            }
        }

        // Союзный джанглер агрессивен, но без своего жёсткого контроля → ему нужен
        // лейнер с CC-сетапом и приоритетом; слабый ранний рушит его план.
        if (jungleAllyId != 0 &&
            ChampionTraits.EarlyPower(jungleAllyId) >= 2 && ChampionTraits.HardCc(jungleAllyId) == 0)
        {
            if (ChampionTraits.HardCc(champId) >= 1)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.jgSetup", DataDragon.Name(jungleAllyId))));
            }
            if (ChampionTraits.EarlyPower(champId) >= 2) b += 0.5;
            if (ChampionTraits.EarlyPower(champId) == 0) b -= 1;
        }

        // Саппорт под стиль АДК.
        if (myRole == "support" && adcAllyId != 0)
        {
            if (ChampionTraits.EngageDependentAdc(adcAllyId) && ChampionTraits.Engage(champId) >= 2)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.adcEngage", DataDragon.Name(adcAllyId))));
            }
            if (ChampionTraits.ScaleAdcCarry(adcAllyId) && ChampionTraits.Peel(champId) >= 2)
            {
                b += 1;
                reasons.Add(Good(Loc.T("reason.adcScale", DataDragon.Name(adcAllyId))));
            }
        }

        return (b, reasons);
    }

    // Прямой оппонент, когда роли врагов скрыты: враг с НАИБОЛЬШЕЙ долей игр на
    // моей роли (порог 15% — роль явно играбельна для него). Раньше требовалось
    // строгое совпадение с самой частой ролью — у флекс-пиков (Вел'Коз мид/сапп,
    // Пантеон топ/сапп) она другая, и «Vs opponent» молчал нулём.
    private int InferDirectOpponent(DraftState state, string myRole)
    {
        int best = 0;
        double bestShare = 0.15;
        foreach (var p in state.TheirTeam)
        {
            var id = p.EffectiveChampionId;
            if (id == 0) continue;
            // Роль слота известна (позиция из LCU или ручная метка игрока) и это
            // не моя роль — такой враг точно не мой оппонент по линии.
            var known = LcuToDbRole(p.Position);
            if (!string.IsNullOrEmpty(known) && known != myRole) continue;
            var share = RoleShare(id, myRole);
            if (share > bestShare) { bestShare = share; best = id; }
        }
        return best;
    }
}
