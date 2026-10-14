using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — советы банов и пикрейты ролей.
public sealed partial class RecommendationEngine
{
    // ---------- Рекомендация банов ----------
    //
    // Один рейтинг на всех кандидатов, без закреплённых мест. Было: моей линии
    // три первых места, защите союзников — не больше двух. Владелец: «убери
    // фиксированное расположение — если персонаж контрит пару из моего пула и
    // пару моих союзников, он должен подниматься на первое место». Теперь
    // кандидат набирает очки за каждого нашего, кого бьёт на его линии, и
    // порядок на экране — просто по очкам.
    //
    // И каждый бан учитывает мой пул: кандидат с любой линии получает очки за
    // то, как мой пул играет против него в той же игре (CrossOf). Владелец:
    // «все баны должны учитывать мой пул — из трёх контр союзникам выбрать ту,
    // что законтрит и мой пул». То же — для союзников: «чем больше показали,
    // тем вероятнее пик против нас всех».

    // Кого защищает бан. Мой пик, мои прошлые наведения и мой пул — «моя
    // сторона»: жёсткие контры им получают надбавку и держатся в пятёрке.
    private enum BanVictimKind { MyPick, MyHover, Pool, Ally, AllyHover }

    private sealed record BanVictim(int Id, string Role, double Weight, BanVictimKind Kind)
    {
        public bool Mine => Kind is BanVictimKind.MyPick or BanVictimKind.MyHover or BanVictimKind.Pool;
    }

    // Чем кандидат бьёт одного нашего: сила контры, очки за неё, жёсткая ли.
    // Cross — не на линии, а в той же игре с другой линии: сила со знаком —
    // кого наши сами бьют, тот теряет очки.
    private readonly record struct BanHit(int Victim, string Role, double Strength, double Value, bool Hard,
                                          bool Cross = false);

    // Кандидат в бан: сила в патче на моей линии и контры нашим по линиям.
    private sealed class BanCandidate(int id)
    {
        public int Id { get; } = id;
        public double Meta;          // очки, когда защищать некого: множитель моей линии
        public bool PickHigh;        // на моей линии берут в полтора раза чаще среднего
        public readonly List<BanHit> Hits = [];
        public readonly Dictionary<string, double> PickOn = new();   // линия → пикрейт там, %
        public readonly Dictionary<string, double> LaneMult = new(); // линия → встречаемость × тир
    }

    /// <summary>
    /// Кого банить. Рейтинг кандидата — сумма по нашим чемпионам (мой пик, мой
    /// пул, пики союзников и то, что они наводили): насколько он бьёт каждого на
    /// его линии × вес этого чемпиона × встречаемость кандидата на линии (как
    /// часто его берут) × его место в тир-листе роли (это и есть сила в патче —
    /// одна на все линии). Жёсткая контра моему чемпиону весит больше и всегда в
    /// пятёрке; бьёт наших на двух линиях и больше — надбавка; второй бан против
    /// того же нашего ценится меньше первого.
    /// hoverHistory: cellId → чемпионы, показанные игроком за драфт.
    /// </summary>
    public IReadOnlyList<BanRec> RecommendBans(
        DraftState state, IReadOnlyDictionary<int, HashSet<int>>? hoverHistory = null, int top = 5)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var stats = RoleStats(myRole);            // champId → (games, wins)
        if (stats.Count == 0) return [];

        var taken   = BanTaken(state, hoverHistory, stats);
        var victims = BanVictims(state, hoverHistory, myRole, stats);
        var cands   = BanCandidates(myRole, stats, victims, taken);
        return [.. PickBans(cands, victims, top)
            .Select(x => new BanRec(x.C.Id, x.Score, BanReasons(x.C, victims, myRole)))];
    }

    /// Кого банить нельзя или незачем: взятых и наведённых, уже забаненных,
    /// всё, что свои наводили за драфт, и тех, на ком я играю (от BAN_OWN_GAMES
    /// игр за месяц на этой роли).
    private HashSet<int> BanTaken(DraftState state, IReadOnlyDictionary<int, HashSet<int>>? hoverHistory,
                                  Dictionary<int, (double Games, double Wins)> stats)
    {
        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
            if (p.EffectiveChampionId != 0) taken.Add(p.EffectiveChampionId);
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);
        // Кого свои уже наводили за драфт — это их запасные пики, и банить их
        // нельзя: отнимешь вариант у союзника. Раньше исключались только
        // текущие наведения, и Вуконг, которого союзник навёл и сменил на Каина,
        // стоял четвёртым баном и тут же — в «контрит ваших: Каин, Вуконг».
        if (hoverHistory != null)
            foreach (var p in state.MyTeam)
                if (hoverHistory.TryGetValue(p.CellId, out var shownBy))
                    taken.UnionWith(shownBy.Where(id => id != 0));
        // Своих не баним: на ком я играю — того бан отнимает у меня. С пулом в
        // счёте Браум с 19 играми за месяц стоял третьим баном у самого
        // владельца — «контрит твой пул (Эш, Сона)». Но «играю» — от трёх игр:
        // с одной игрой за месяц под запретом были Блицкранк, Пайк, Нами (у
        // владельца 35 чемпионов за месяц), и против его пула оставались одни
        // Наутилус, Релл и Рэйкан. Пул целиком не исключаем (решение
        // владельца): в нём бывает двадцать саппортов. Чемпион пула без игр
        // остаётся мейном — против него ищем контры, — но забанить его можно.
        taken.UnionWith(MyHistory().Played.Where(p => p.Games >= BAN_OWN_GAMES && stats.ContainsKey(p.Id)).Select(p => p.Id));
        return taken;
    }

    /// <summary>
    /// Наши чемпионы, которых защищает бан, — с линией и весом.
    ///
    /// Мой пул: журнал за месяц (вес — игры) и активный пул на роль из профиля
    /// (без игр — как POOL_MAIN_GAMES), восемь самых весомых; мастерство — только
    /// если нет ни того, ни другого (оно копится годами: «контрит Зилеана» на
    /// чемпионе, которого не трогали полгода). Весь пул весит W_BAN_POOL и
    /// делится по корню из игр: чемпион с 32 играми важнее того, что лежит в
    /// пуле, но не в десять раз. С вечера 14.10 делится не по играм, а по тому,
    /// как вероятно, что подбор пика предложит этого чемпиона (PoolOdds).
    ///
    /// Кого взял или навёл союзник, взял враг или забанили — того я уже не
    /// возьму, и в пуле его нет: Хвэй союзника на миде считался моим пулом, и
    /// контры ему получали очки.
    ///
    /// Пики союзников и мой — по наведению сейчас; то, что наводили раньше, —
    /// меньшим весом. Союзников больше BAN_ALLY_SHARE — вес каждого ×
    /// √(BAN_ALLY_SHARE / n): вместе они весят больше, чем двое, но растут
    /// медленнее числа. Без поправки четыре пика по 2,6 перевешивали весь мой
    /// пул (4,5) почти втрое, и три первых бана не получали за пул ни очка
    /// (скриншот владельца 14.10); с делением поровну («вместе как двое»)
    /// команда не росла с числом показанных, и пик против всех нас не
    /// поднимался (следующий скриншот). Линия союзника — его позиция, нет
    /// позиции (блайнд, соло без ролей) — основная роль чемпиона.
    /// </summary>
    private List<BanVictim> BanVictims(DraftState state, IReadOnlyDictionary<int, HashSet<int>>? hoverHistory,
                                       string myRole, Dictionary<int, (double Games, double Wins)> stats)
    {
        var list = new List<BanVictim>();

        var mainW = new Dictionary<int, double>();
        foreach (var p in MyHistory().Played.Where(p => p.Games > 0 && stats.ContainsKey(p.Id)))
            mainW[p.Id] = p.Games;
        foreach (var id in PoolStore.ActiveForRole(myRole).Mine.Where(stats.ContainsKey))
            mainW[id] = Math.Max(mainW.GetValueOrDefault(id), POOL_MAIN_GAMES);
        if (mainW.Count == 0)
            foreach (var id in Mastery.Where(kv => stats.ContainsKey(kv.Key))
                                      .OrderByDescending(kv => kv.Value).Take(MainsTake).Select(kv => kv.Key))
                mainW[id] = POOL_MAIN_GAMES;
        var gone = state.MyTeam.Where(p => !p.IsLocalPlayer).Select(p => p.EffectiveChampionId)
            .Concat(state.TheirTeam.Select(p => p.EffectiveChampionId))
            .Concat(state.MyTeamBans).Concat(state.TheirTeamBans)
            .Where(id => id != 0).ToHashSet();
        var mains = mainW.Where(kv => !gone.Contains(kv.Key))
                         .OrderByDescending(kv => kv.Value).Take(MAINS_FOR_BANS).ToList();
        // Вес мейна — насколько вероятно, что подбор пика предложит его на ЭТОМ
        // драфте: очки Recommend (база в патче, наигранность со свежестью,
        // синергия с показанными союзниками, напарник по дуо, стиль) → доли
        // exp(очки / BAN_PICK_TEMP). Владелец 14.10: «баны дают столько контры
        // против Эш, которую я скорее всего и не пикнул бы — посмотри, как
        // считает подбор в драфте». Было: по корню из игр за месяц (Эш с 19
        // играми — третья) и надбавка за синергию; а подбор ставит Эш-саппорта
        // последней (−2,5 против 9,4 у Соны: винрейт в патче −5,7).
        foreach (var (id, weight) in PoolOdds(state, myRole, mains))
            list.Add(new BanVictim(id, myRole, W_BAN_POOL * weight, BanVictimKind.Pool));

        string RoleOf(DraftPlayer p, int champ)
        {
            var r = LcuToDbRole(p.Position);
            return DbRoles.Contains(r) ? r : InferPrimaryRole(champ);
        }
        var shown = new HashSet<int>();
        foreach (var p in state.MyTeam.Where(p => p.EffectiveChampionId != 0))
        {
            var id = p.EffectiveChampionId;
            shown.Add(id);
            list.Add(p.IsLocalPlayer
                ? new BanVictim(id, myRole, W_BAN_MYPICK, BanVictimKind.MyPick)
                : new BanVictim(id, RoleOf(p, id), W_BAN_ALLY, BanVictimKind.Ally));
        }
        // История наведений: игроки в фазе банов перебирают чемпионов — защищаем
        // весь показанный пул (меньшим весом: наведение сейчас важнее прошлого).
        if (hoverHistory != null)
            foreach (var p in state.MyTeam)
                if (hoverHistory.TryGetValue(p.CellId, out var hovered))
                    foreach (var id in hovered.Where(id => id != 0 && shown.Add(id)))
                        list.Add(p.IsLocalPlayer
                            ? new BanVictim(id, myRole, W_BAN_MYPICK * 0.75, BanVictimKind.MyHover)
                            : new BanVictim(id, RoleOf(p, id), W_BAN_HOVER, BanVictimKind.AllyHover));
        var allies = list.Count(v => v.Kind == BanVictimKind.Ally);
        var share = allies > BAN_ALLY_SHARE ? Math.Sqrt(BAN_ALLY_SHARE / allies) : 1.0;
        return [.. list.Where(v => DbRoles.Contains(v.Role))
                       .Select(v => v.Mine ? v : v with { Weight = v.Weight * share })];
    }

    /// <summary>
    /// Доли моего пула: как вероятно, что подбор пика (Recommend) предложит
    /// каждого на этом драфте, — exp(очки / BAN_PICK_TEMP), в сумме 1. Разница
    /// в BAN_PICK_TEMP очков — в e раз. Подбор ничего не вернул (нет данных) —
    /// по корню из игр, как раньше.
    /// </summary>
    private List<(int Id, double Share)> PoolOdds(DraftState state, string myRole, List<KeyValuePair<int, double>> mains)
    {
        if (mains.Count == 0) return [];
        var scores = new Dictionary<int, double>();
        try
        {
            foreach (var r in Recommend(state with { InBanPhase = false }, mains.Count, [.. mains.Select(m => m.Key)]))
                scores[r.ChampionId] = r.Score;
        }
        catch { /* подбор не посчитался — ниже запасной путь */ }

        List<(int Id, double W)> raw;
        if (scores.Count > 0)
        {
            var top = scores.Values.Max();
            raw = [.. mains.Where(m => scores.ContainsKey(m.Key))
                           .Select(m => (m.Key, Math.Exp((scores[m.Key] - top) / BAN_PICK_TEMP)))];
        }
        else
            raw = [.. mains.Select(m => (m.Key, Math.Sqrt(m.Value)))];
        var sum = raw.Sum(x => x.W);
        return sum > 0 ? [.. raw.Select(x => (x.Id, x.W / sum))] : [];
    }

    /// <summary>
    /// Кандидаты и всё, чем каждый опасен. Кандидат на линии — тот, кого там
    /// берут не реже MIN_ROLE_SHARE: Зилеан на топе (0,09% игр) стоял первым
    /// баном за то, что «бьёт Дариуса», хотя встретить его там почти нельзя.
    /// Множитель линии — встречаемость (Meet) × место в тир-листе роли
    /// (TierMult): контра, которую берут в 7% игр и которая стоит в S-тире,
    /// весит больше той же силы у взятого в 0,6% с конца тир-листа.
    /// </summary>
    private List<BanCandidate> BanCandidates(string myRole, Dictionary<int, (double Games, double Wins)> stats,
                                             List<BanVictim> victims, HashSet<int> taken)
    {
        var picks = new Dictionary<string, (Dictionary<int, double> Pick, double Ref)>();
        var cands = new Dictionary<int, BanCandidate>();
        BanCandidate? On(int x, string role)
        {
            if (taken.Contains(x)) return null;
            if (!picks.TryGetValue(role, out var rp)) picks[role] = rp = RolePicks(role);
            var pick = rp.Pick.GetValueOrDefault(x);
            if (pick < MIN_ROLE_SHARE * 100) return null;
            if (!cands.TryGetValue(x, out var c)) cands[x] = c = new BanCandidate(x);
            if (!c.LaneMult.ContainsKey(role))
            {
                c.PickOn[role] = pick;
                c.LaneMult[role] = Meet(pick, rp.Ref) * TierMult(x, role);
            }
            return c;
        }

        // Сила в патче отдельным слагаемым не считается: она уже в множителе
        // линии каждого попадания (место в тир-листе), одинаково на всех линиях.
        // Раньше на моей линии её считали дважды — слагаемым (винрейт) и
        // множителем, а на чужих — один раз (владелец 14.10: «пусть считается
        // для всех, но один раз»). Отдельно она нужна, только когда защищать
        // некого (ни пула, ни мастерства, ни показанных пиков): тогда бан —
        // сильный и частый на моей линии, очки = множитель линии.
        foreach (var x in stats.Keys)
        {
            if (On(x, myRole) is not { } c) continue;
            if (victims.Count == 0) c.Meta = W_BAN_META * c.LaneMult[myRole];
            c.PickHigh = c.PickOn[myRole] >= picks[myRole].Ref * 1.5;
        }

        // Контры нашим: на линии каждого — все, кого там берут. Сила — чистая
        // дельта нашего чемпиона против кандидата к его же среднему винрейту, с
        // темпером по объёму пары (редкие пары — не доказательство).
        for (int i = 0; i < victims.Count; i++)
        {
            var v = victims[i];
            var (bg, bw) = RawBase(v.Id, v.Role);
            if (bg <= 0) continue;
            var baseD = Delta(bg, bw, K);
            foreach (var (x, (mg, mw)) in MatchupsOf(v.Id, v.Role))
            {
                if (x == v.Id || mg <= 0) continue;   // сам себе не контра
                var s = (baseD - Delta(mg, mw, K_PAIR)) * (mg / (mg + MATCHUP_CONF));
                if (s <= 0 || On(x, v.Role) is not { } c) continue;
                // Жёсткая контра моему чемпиону — надбавка (владелец: «у жёстких
                // контр на линии против моих персонажей должен быть буст, и они
                // по-любому должны быть в топах»).
                // Жёсткая — только против того, кого мне правда предложат: Эш с
                // долей 0,6% «жёсткой контрой» Блицкранка не делает.
                var hard = v.Mine && s >= BAN_HARD
                           && (v.Kind != BanVictimKind.Pool || v.Weight >= W_BAN_POOL * BAN_HARD_SHARE);
                c.Hits.Add(new BanHit(i, v.Role, s,
                    v.Weight * s * c.LaneMult[v.Role] * (hard ? W_BAN_HARD : 1.0), hard));
            }
            AddCross(i, v, baseD);
        }
        return [.. cands.Values];

        // Наш чемпион против кандидата на другой линии в той же игре: Вуконг в
        // лесу не встречает Сону на линии, но игры с ним Сона проигрывает. Так
        // мой пул учитывает каждый бан, а не только кандидатов моей линии, а
        // кандидат, который понемногу мешает всем союзникам, складывает очки по
        // каждому — чем больше их показали, тем выше пик против всех нас. Сила
        // — со знаком: кого наши сами бьют (у саппортов-энчантеров — Ренгар,
        // Зед), тот теряет очки. Шум пар тут сильнее, чем на линии, поэтому
        // доверие к паре набирается медленнее (CROSS_CONF).
        void AddCross(int i, BanVictim v, double baseD)
        {
            foreach (var ((x, vr), (g, w)) in CrossOf(v.Id, v.Role))
            {
                if (x == v.Id || g <= 0 || vr == v.Role || !DbRoles.Contains(vr)) continue;
                if (On(x, vr) is not { } c) continue;
                var s = (baseD - Delta(g, w, K_PAIR)) * (g / (g + CROSS_CONF));
                c.Hits.Add(new BanHit(i, vr, s, v.Weight * s * c.LaneMult[vr] * W_BAN_CROSS, false, Cross: true));
            }
        }
    }

    /// Поправка за место в тир-листе роли: от BAN_TIER_LOW у последнего (и у тех,
    /// кого в тир-листе нет) до BAN_TIER_HIGH у первого.
    private double TierMult(int champId, string role) =>
        BAN_TIER_LOW + (BAN_TIER_HIGH - BAN_TIER_LOW) * (1 - TierRank(champId, role));

    /// <summary>
    /// Очки кандидата при том, что выбранные уже баны прикрыли часть наших
    /// пиков: каждый бан, который уже бьёт этот пик, удешевляет следующий
    /// против него в BAN_REPEAT раз (слабое попадание — частично), иначе
    /// пятёрка против одной Ирелии оставила бы без защиты остальных. Мой пул не
    /// дешевеет: в нём несколько чемпионов, и бан против одного не снимает
    /// угрозу другому. Бьёт наших на нескольких линиях — надбавка BAN_FLEX за
    /// каждую сверх первой: один бан снимает угрозу двоим, а враг такого
    /// возьмёт рано и поставит, куда выгоднее.
    /// </summary>
    private static double BanScore(BanCandidate c, double[] covered)
    {
        var sum = c.Meta;
        foreach (var h in c.Hits) sum += h.Value * Math.Pow(BAN_REPEAT, covered[h.Victim]);
        var lanes = c.Hits.Where(h => !h.Cross && h.Strength >= BAN_HIT).Select(h => h.Role).Distinct().Count();
        return lanes > 1 ? sum * (1 + BAN_FLEX * (lanes - 1)) : sum;
    }

    /// <summary>
    /// Выбор пяти: по одному, каждый раз лучший по очкам с учётом уже
    /// выбранных. Порядок на экране — по очкам.
    ///
    /// Два нижних порога, оба без закреплённых мест — встают по своим очкам:
    /// жёсткая контра моему чемпиону, если такая есть, в пятёрке всегда; и
    /// прямых контр моему пулу на линии в ней не меньше BAN_POOL_MIN (владелец:
    /// «часть банов должна быть чётко против моего пула, а остальные — учитывать
    /// и его»). Не набрали очков на место — заменяют самых слабых из прочих.
    /// </summary>
    private static List<(BanCandidate C, double Score)> PickBans(List<BanCandidate> cands, List<BanVictim> victims, int top)
    {
        var covered = new double[victims.Count];
        var rest = new List<BanCandidate>(cands);
        var chosen = new List<(BanCandidate C, double Score)>();
        while (chosen.Count < top && rest.Count > 0)
        {
            var best = rest.MaxBy(c => BanScore(c, covered))!;
            var score = BanScore(best, covered);
            if (score <= 0) break;
            chosen.Add((best, score));
            rest.Remove(best);
            foreach (var h in best.Hits.Where(h => !h.Cross && h.Strength >= BAN_HIT && victims[h.Victim].Kind != BanVictimKind.Pool))
                covered[h.Victim] += Math.Min(1.0, h.Strength / BAN_HARD);
        }
        // Прямая контра моему пулу или пику на линии — «чётко против меня».
        bool AgainstMe(BanCandidate c) =>
            c.Hits.Any(h => !h.Cross && h.Strength >= BAN_HIT && victims[h.Victim].Mine);
        void Ensure(Func<BanCandidate, bool> wanted, int min)
        {
            while (chosen.Count(x => wanted(x.C)) < Math.Min(min, top)
                   && rest.Where(wanted).MaxBy(c => BanScore(c, covered)) is { } add)
            {
                // Уступает самый слабый из тех, кто сам не держит ни один порог.
                var weakest = chosen.Select((x, i) => (x, i))
                                    .Where(t => !wanted(t.x.C) && !(t.x.C.Hits.Any(h => h.Hard)))
                                    .Select(t => t.i).DefaultIfEmpty(-1).Last();
                if (chosen.Count >= top)
                {
                    if (weakest < 0) break;
                    chosen.RemoveAt(weakest);
                }
                chosen.Add((add, BanScore(add, covered)));
                rest.Remove(add);
            }
        }
        if (chosen.Count > 0)
        {
            Ensure(c => c.Hits.Any(h => h.Hard), 1);
            Ensure(AgainstMe, BAN_POOL_MIN);
        }
        return [.. chosen.OrderByDescending(x => x.Score)];
    }

    /// <summary>
    /// Доводы карточки, по важности (окно показывает два первых): жёсткая
    /// контра моему, контра моему пику, моему пулу, пикам союзников, «мешает и
    /// твоему пулу», «мешает команде» (в игре, с другой линии), место в
    /// тир-листе, пикрейт. Контры называют поимённо, кого бьют,
    /// и пикрейт кандидата на той линии, где он их встречает. Кандидат, который
    /// бьёт и мой пул, и союзников, показывает оба довода — видно, почему он первый.
    /// </summary>
    private string[] BanReasons(BanCandidate c, List<BanVictim> victims, string myRole)
    {
        const int R_HARD = 0, R_MINE = 1, R_POOL = 2, R_ALLY = 3, R_CROSS = 4, R_TEAM = 5, R_TIER = 6, R_PICK = 7;
        var rs = new List<(int Prio, string Text)>();
        void Add(int prio, string text) { if (!rs.Any(r => r.Text == text)) rs.Add((prio, text)); }
        string WithPick(IEnumerable<BanHit> hits, string text)
        {
            var pk = hits.Select(h => c.PickOn.GetValueOrDefault(h.Role)).DefaultIfEmpty(0).Max();
            return pk > 0 ? Loc.T("reason.withPick", text, $"{pk:F1}") : text;
        }
        // Одного нашего называем раз — по самому весомому попаданию (Сона в пуле
        // и Сона наведённая — одна Сона).
        List<BanHit> Named(Func<BanVictim, bool> f, bool cross = false) => [.. c.Hits
            .Where(h => h.Cross == cross && h.Strength >= BAN_HIT && f(victims[h.Victim]))
            .OrderByDescending(h => h.Value)
            .DistinctBy(h => victims[h.Victim].Id)];
        int Who(BanHit h) => victims[h.Victim].Id;
        string Name(BanHit h) => DataDragon.Name(Who(h));

        var hard = c.Hits.Where(h => h.Hard).OrderByDescending(h => h.Value).Take(1).ToList();
        if (hard.Count > 0)
            Add(R_HARD, WithPick(hard, Loc.T("reason.hardCounter", Name(hard[0]), $"{hard[0].Strength:F1}")));
        bool SaidHard(List<BanHit> hs) => hard.Count > 0 && hs.All(h => Who(h) == Who(hard[0]));

        var mine = Named(v => v.Kind is BanVictimKind.MyPick or BanVictimKind.MyHover).Take(1).ToList();
        if (mine.Count > 0 && !SaidHard(mine))
            Add(R_MINE, WithPick(mine, Loc.T("reason.countersMyPick", Name(mine[0]))));

        // До двух самых задетых чемпионов пула; если это та же, что в жёсткой
        // контре, — уже сказано.
        var pool = Named(v => v.Kind == BanVictimKind.Pool).Take(2).ToList();
        if (pool.Count > 0 && !SaidHard(pool))
            Add(R_POOL, Loc.T("reason.countersPoolPick", string.Join(", ", pool.Select(Name)),
                              $"{c.PickOn.GetValueOrDefault(myRole):F1}"));

        var allies = Named(v => !v.Mine);
        if (allies.Count == 1)
            Add(R_ALLY, WithPick(allies, Loc.T("reason.countersAlly", Name(allies[0]))));
        else if (allies.Count > 1)
            Add(R_ALLY, WithPick(allies, Loc.T("reason.countersMany", string.Join(", ", allies.Select(Name)))));

        // Мешает моему пулу не на линии, а в игре — до двух самых задетых. Только
        // если в сумме мешает: кого пул сам бьёт, про того не пишем.
        var cross = Named(v => v.Mine, cross: true).Take(2).ToList();
        if (cross.Count > 0 && c.Hits.Where(h => h.Cross && victims[h.Victim].Mine).Sum(h => h.Value) > 0)
            Add(R_CROSS, Loc.T("reason.alsoPool", string.Join(", ", cross.Select(Name))));
        // Мешает союзникам в игре с другой линии — до трёх, если в сумме мешает.
        var team = Named(v => !v.Mine, cross: true).Take(3).ToList();
        if (team.Count > 0 && c.Hits.Where(h => h.Cross && !victims[h.Victim].Mine).Sum(h => h.Value) > 0)
            Add(R_TEAM, Loc.T("reason.alsoTeam", string.Join(", ", team.Select(Name))));

        // Место в тир-листе — на линии, где он опаснее всего (нет контр — на моей).
        var lane = c.Hits.OrderByDescending(h => h.Value).Select(h => h.Role).FirstOrDefault() ?? myRole;
        if (c.PickOn.ContainsKey(lane) && TierGrade(c.Id, lane) is 'S' or 'A')
            Add(R_TIER, Loc.T("reason.tierGrade", TierGrade(c.Id, lane).ToString()));
        if (c.PickHigh) Add(R_PICK, Loc.T("reason.pickRate", $"{c.PickOn[myRole]:F1}"));

        // Порядок — по приоритету, внутри него — как добавлялись.
        var texts = rs.Select((r, i) => (r.Prio, i, r.Text)).OrderBy(r => r.Prio).ThenBy(r => r.i)
                      .Select(r => r.Text).ToList();
        if (texts.Count == 0) texts.Add(Loc.T("reason.notablePick"));
        return [.. texts];
    }

    /// <summary>
    /// Встречаемость кандидата в бан: корень из отношения его пикрейта на роли
    /// к среднему пикрейту регулярного пика этой роли, в пределах 0,3…2.
    ///
    /// Корень, а не прямая пропорция: популярный в семь раз не должен в семь
    /// раз перевешивать силу контры — иначе список банов превращается в
    /// список популярных. Средний пикрейт свой у каждой роли (у стрелков пиков
    /// меньше и каждый берут чаще: 3,3% против 1,8% на топе).
    /// </summary>
    private static double Meet(double pickPct, double refPct) =>
        refPct > 0 ? Math.Clamp(Math.Sqrt(pickPct / refPct), 0.3, 2.0) : 1.0;

    /// Пикрейт чемпионов роли за последние патчи (% игр роли, с весами патчей)
    /// и средний пикрейт регулярного пика — тех, кого берут хотя бы в
    /// MIN_ROLE_SHARE игр.
    private (Dictionary<int, double> Pick, double Ref) RolePicks(string role)
    {
        var stats = RoleStats(role);
        var total = stats.Values.Sum(v => v.Games);
        var pick  = stats.ToDictionary(kv => kv.Key, kv => total > 0 ? 100.0 * kv.Value.Games / total : 0.0);
        var regular = pick.Values.Where(p => p >= MIN_ROLE_SHARE * 100).ToList();
        return (pick, regular.Count > 0 ? regular.Average() : 0.0);
    }

    /// Пикрейт чемпионов роли за последние патчи, % игр роли. Песочнице — кого
    /// союзнику «навести» в фазе банов: популярных чаще, как в жизни.
    public IReadOnlyDictionary<int, double> PickRates(string role) => RolePicks(role).Pick;

    // Суммарные (games, wins) по всем кандидатам роли за окно патчей.
    private Dictionary<int, (double Games, double Wins)> RoleStats(string role)
    {
        var result = new Dictionary<int, (double, double)>();
        var cmd = _db.CreateCommand();
        cmd.CommandText = $@"
            SELECT champion_id, SUM(games*{PW}), SUM(wins*{PW}) FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id HAVING SUM(games*{PW}) >= @min";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            result[rd.GetInt32(0)] = (rd.GetDouble(1), rd.GetDouble(2));
        return result;
    }
}
