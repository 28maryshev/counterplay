using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — советы банов и пикрейты ролей.
public sealed partial class RecommendationEngine
{
    // ---------- Рекомендация банов ----------

    /// Кого банить: сильные/популярные в патче чемпионы твоей роли, которые ещё и
    /// плохи лично для тебя (контрят твой пул). Возвращает топ-N.
    /// hoverHistory: cellId → чемпионы, показанные игроком за драфт (пул команды).
    /// Бан, контрящий сразу нескольких из показанных, получает бонус за широту.
    public IReadOnlyList<BanRec> RecommendBans(
        DraftState state, IReadOnlyDictionary<int, HashSet<int>>? hoverHistory = null, int top = 5)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var stats = RoleStats(myRole);            // champId → (games, wins)
        if (stats.Count == 0) return [];
        var maxGames = stats.Values.Max(v => v.Games);
        var myPicks  = RolePicks(myRole);         // пикрейт моей роли за последние патчи

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

        // Мои мейны на этой роли — чтобы банить их контр-пики.
        //
        // Сперва те, кем игрок ДЕЙСТВИТЕЛЬНО играет в последнее время. Раньше
        // брали только мастерство, а оно копится годами и не забывается: в
        // банах всплывало «контрит Зилеана» на чемпионе, которого человек не
        // трогал полгода. Совет про него — потраченный бан.
        //
        // Вторым — мой активный пул на эту роль из профиля: «этих я готов
        // взять». Раньше баны пул не читали вовсе, и у игрока с собранным пулом
        // саппортов, но короткой историей, контрить было почти нечего.
        //
        // Вес мейна — сколько я на нём играю: контра чемпиону с 32 играми за
        // месяц важнее контры тому, кто просто лежит в пуле.
        //
        // Мастерством добираем, только если нет ни истории, ни пула: у свежей
        // установки без него список был бы пустым.
        var mainW = new Dictionary<int, double>();
        foreach (var p in MyHistory().Played.Where(p => p.Games > 0 && stats.ContainsKey(p.Id)))
            mainW[p.Id] = p.Games;
        foreach (var id in PoolStore.ActiveForRole(myRole).Mine.Where(stats.ContainsKey))
            mainW[id] = Math.Max(mainW.GetValueOrDefault(id), POOL_MAIN_GAMES);
        if (mainW.Count == 0)
            foreach (var id in Mastery.Where(kv => stats.ContainsKey(kv.Key))
                                      .OrderByDescending(kv => kv.Value).Take(MainsTake).Select(kv => kv.Key))
                mainW[id] = POOL_MAIN_GAMES;
        var myMains = mainW.OrderByDescending(kv => kv.Value).Take(MAINS_FOR_BANS).ToList();
        // Своих не баним: на ком я играл за месяц на этой роли — того бан
        // отнимает у меня. С пулом в счёте Браум с 19 играми за месяц стоял
        // третьим баном у самого владельца — «контрит твой пул (Эш, Сона)».
        // Пул целиком не исключаем (решение владельца): в нём бывает двадцать
        // саппортов, и советы лишились бы Треша, Лулу, Нами. Чемпион пула без
        // игр остаётся мейном — против него ищем контры, — но забанить его можно.
        taken.UnionWith(MyHistory().Played.Where(p => p.Games > 0 && stats.ContainsKey(p.Id)).Select(p => p.Id));

        var scores  = new Dictionary<int, double>();
        // Доводы с приоритетом: карточка показывает первые два, и первыми
        // должны идти те, ради которых бан и выбран, — защита моего пика, пиков
        // союзников, угроза команде, — а «сильный в патче» уже потом.
        // Моя линия — впереди защиты союзников: бан прежде всего мой.
        const int R_MINE = 0, R_POOL = 1, R_ALLY = 2, R_TEAM = 3, R_META = 4, R_PICK = 5;
        var reasons = new Dictionary<int, List<(int Prio, string Text)>>();
        void AddReason(int id, string r, int prio)
        {
            if (!reasons.TryGetValue(id, out var l)) reasons[id] = l = [];
            if (!l.Any(x => x.Text == r)) l.Add((prio, r));
        }
        // Пикрейт кандидата на роли, где он встречает того, кого контрит, — в
        // подпись: видно, что бан не на того, кого почти не берут.
        var pickOn = new Dictionary<(int Champ, string Role), double>();
        // Сколько очков кандидат набрал за МОЮ линию (мета роли, контра пулу,
        // защита моего пика) — по ним делятся слоты, см. выбор ниже.
        var laneScore = new Dictionary<int, double>();

        // 1. Сила в патче + популярность + контр-пики моего пула (кандидаты моей роли).
        foreach (var x in stats.Keys)
        {
            if (taken.Contains(x)) continue;
            // Офф-мета на этой роли — не кандидат в бан вовсе: Зилеан на топе
            // (0,09% игр) стоял первым баном за то, что «бьёт Дариуса», хотя
            // встретить его там почти невозможно.
            var pick = myPicks.Pick.GetValueOrDefault(x);
            if (pick < MIN_ROLE_SHARE * 100) continue;
            var (g, w) = stats[x];
            var metaWr = ((w + K / 2.0) / (g + K) - PRIOR) * 100 * (g / (g + BASE_CONF));
            var pop    = maxGames > 0 ? g / maxGames : 0;
            var meet   = Meet(pick, myPicks.Ref);

            // Насколько x бьёт мой пул: средняя контра по мейнам с весом по
            // играм. Раньше брали самый контрящий мейн — но с пулом из профиля
            // мейнов стало много, и «бьёт хоть кого-то» сказать можно почти про
            // любого. Средняя по тому, что я реально беру, — это и есть «как
            // часто он испортит мне игру».
            double counterMe = 0, wSum = 0;
            var beaten = new List<(int Id, double Hit)>();
            foreach (var (m, wm) in myMains)
            {
                if (m == x) continue;   // сам себе не контра (чемпион пула без игр — тоже кандидат)
                var (mg, mw) = RawMatchup(m, myRole, x);
                if (mg <= 0) continue;
                // Чистая контра моего мейна: его WR в паре ниже его же среднего,
                // с темпером по объёму пары (редкие пары — не доказательство).
                var (mbg, mbw) = RawBase(m, myRole);
                var s = (Delta(mbg, mbw, K) - Delta(mg, mw, K_PAIR)) * (mg / (mg + MATCHUP_CONF));
                wSum += wm;
                if (s <= 0) continue;
                counterMe += wm * s;
                if (s >= 1.5) beaten.Add((m, wm * s));
            }
            counterMe = wSum > 0 ? counterMe / wSum : 0;

            // Контра весит столько, насколько вероятно её встретить: та же сила
            // против моего мейна у чемпиона, которого берут в 7% игр, стоит
            // бана, а у взятого в 0,6% — почти нет.
            var score = W_BAN_META * metaWr + W_BAN_POP * (pop * 10) + W_BAN_COUNTER * counterMe * meet;
            if (score <= 0) continue;
            scores[x] = score;
            laneScore[x] = score;

            pickOn[(x, myRole)] = pick;
            // Называем тех, кого он бьёт сильнее всего с учётом игр, — до двух.
            if (beaten.Count > 0 && counterMe >= 0.5)
                AddReason(x, Loc.T("reason.countersPoolPick",
                    string.Join(", ", beaten.OrderByDescending(b => b.Hit).Take(2).Select(b => DataDragon.Name(b.Id))),
                    $"{pick:F1}"), R_POOL);
            if (metaWr >= 1.5) AddReason(x, Good(Loc.T("reason.strongPatch", $"{50 + metaWr:F1}")), R_META);
            if (pick >= myPicks.Ref * 1.5) AddReason(x, Loc.T("reason.pickRate", $"{pick:F1}"), R_PICK);
        }

        // 2. Защита заявленных пиков: банить тех, кто контрит уже наведённых/взятых
        //    чемпионов. Приоритет — мой пик, затем союзники. Роль часто не назначена
        //    (ranked solo/duo, блайнд), поэтому матчап при пустой роли берём по всем ролям.
        var protectees = new List<(int Id, string? Role, double Weight, bool Mine)>();
        var me = state.MyTeam.FirstOrDefault(p => p.IsLocalPlayer);
        if (me is { EffectiveChampionId: not 0 })
            protectees.Add((me.EffectiveChampionId, string.IsNullOrEmpty(myRole) ? null : myRole, W_BAN_MYPICK, true));
        foreach (var ally in state.MyTeam)
        {
            if (ally.IsLocalPlayer || ally.EffectiveChampionId == 0) continue;
            var ar = LcuToDbRole(ally.Position);
            protectees.Add((ally.EffectiveChampionId, string.IsNullOrEmpty(ar) ? null : ar, W_BAN_ALLY, false));
        }

        // История ховеров: игроки в фазе банов перебирают чемпионов — защищаем
        // весь показанный пул (меньшим весом: наведение сейчас важнее прошлого).
        if (hoverHistory != null)
        {
            var seen = protectees.Select(p => p.Id).ToHashSet();
            foreach (var player in state.MyTeam)
                if (hoverHistory.TryGetValue(player.CellId, out var shown))
                {
                    var hr = LcuToDbRole(player.Position);
                    foreach (var hid in shown)
                        if (hid != 0 && seen.Add(hid))
                            protectees.Add((hid, string.IsNullOrEmpty(hr) ? null : hr,
                                            player.IsLocalPlayer ? W_BAN_MYPICK * 0.75 : W_BAN_HOVER,
                                            player.IsLocalPlayer));
                }
        }

        // Самый жёсткий контрпик каждого защищаемого — ему гарантируется место
        // в топе банов (см. ниже): игрок навёл чемпиона в фазе банов — значит
        // хочет увидеть, от кого его защищать.
        var bestByProtectee = new Dictionary<int, (int Champ, double Strength)>();
        var victimsOf = new Dictionary<int, HashSet<int>>(); // бан-кандидат → кого он контрит ЗАМЕТНО (для бонуса ширины)
        // Все, кого кандидат контрит хоть сколько-то: из этого строим подпись
        // «контрит Сону, Люкс и Морgану» — игрок должен видеть ВЕСЬ список.
        var hitsOf = new Dictionary<int, List<(int Id, bool Mine, string? Role)>>();

        // Пикрейт на роли защищаемого: контру на линии берут именно туда. Роль
        // неизвестна — встречаемость не учитываем (множитель 1).
        var picksByRole = new Dictionary<string, (Dictionary<int, double> Pick, double Ref)>
            { [myRole] = myPicks };

        foreach (var (pid, prole, weight, mine) in protectees)
            foreach (var c in TopCounters(pid, prole, 8, minGames: 20, minEdge: 0.48))
            {
                if (taken.Contains(c)) continue;
                var meet = 1.0;
                if (prole != null && DbRoles.Contains(prole))
                {
                    if (!picksByRole.TryGetValue(prole, out var rp)) picksByRole[prole] = rp = RolePicks(prole);
                    var pick = rp.Pick.GetValueOrDefault(c);
                    if (pick < MIN_ROLE_SHARE * 100) continue;   // на этой линии его почти не берут
                    meet = Meet(pick, rp.Ref);
                    pickOn[(c, prole)] = pick;
                }
                var (mg, mw) = prole != null ? RawMatchup(pid, prole, c) : RawMatchupAny(pid, c);
                if (mg <= 0) continue;
                // Насколько c бьёт нашего чемпиона: чистая дельта против его же
                // среднего WR, с темпером по объёму пары. Порог мягкий: чистые
                // темперированные дельты малы, а защиту показать важно.
                var (pbg, pbw) = prole != null ? RawBase(pid, prole) : RawBaseAny(pid);
                var strength = (Delta(pbg, pbw, K) - Delta(mg, mw, K_PAIR)) * (mg / (mg + MATCHUP_CONF));
                if (strength < 0.15) continue;
                // В счёт и в «самую жёсткую контру» — с поправкой на то, как
                // часто её берут; пороги «контрит ли вообще» — по чистой силе.
                var likely = strength * meet;
                scores[c] = scores.GetValueOrDefault(c) + weight * likely;
                if (mine) laneScore[c] = laneScore.GetValueOrDefault(c) + weight * likely;
                if (!hitsOf.TryGetValue(c, out var hits)) hitsOf[c] = hits = [];
                if (!hits.Any(h => h.Id == pid)) hits.Add((pid, mine, prole));
                if (!bestByProtectee.TryGetValue(pid, out var cur) || likely > cur.Strength)
                    bestByProtectee[pid] = (c, likely);
                if (strength >= 0.3)
                {
                    if (!victimsOf.TryGetValue(c, out var vs)) victimsOf[c] = vs = [];
                    vs.Add(pid);
                }
            }

        // Угроза для всей команды: чемпион, который обыгрывает НАШИХ вообще, даже
        // не будучи чьей-то персональной топ-контрой. Считаем по всем показанным
        // пикам сразу — один бан снимает проблему всей команде.
        // С РОЛЯМИ: matchup хранит только противостояние на линии, и без роли
        // угроза складывалась по всем ролям сразу — лесной матчап приписывался
        // топ-лейнеру.
        var teamPicks = protectees
            .Select(p => (p.Id, p.Role))
            .Where(p => p.Id > 0)
            .Distinct()
            .ToList();

        // В счёте участвует весь показанный пул, включая прошлые наведения, — так
        // бан выбирается точнее. А в подписи называем только то, что игрок видит
        // на экране прямо сейчас: иначе «бьёт 5 твоих пиков» при двух пиках в
        // окне выглядит выдумкой.
        var onScreen = state.MyTeam
            .Where(p => p.EffectiveChampionId != 0)
            .Select(p => p.EffectiveChampionId)
            .ToHashSet();

        foreach (var (c, t) in TeamThreats(teamPicks))
        {
            if (taken.Contains(c)) continue;
            scores[c] = scores.GetValueOrDefault(c) + W_BAN_TEAM * t.Threat;

            // Называем поимённо и с цифрами: «силён против Экко (+0,5),
            // Синджед (+3,4)». Прежнее «силён против 2 ваших пиков» не давало
            // ни проверить, ни выбрать между двумя кандидатами.
            //
            // Сильные — первыми: если пострадавших много, обрежется хвост, а не
            // то, ради чего бан и нужен.
            var named = t.Victims
                .Where(onScreen.Contains)
                // Только те, против кого он ДЕЙСТВИТЕЛЬНО силён. Общая угроза
                // складывается из всех матчапов, и среди них попадаются
                // отрицательные — назвать их под словом «силён против» значило
                // бы соврать. Проверка это и поймала: в списке стояло «(-…)».
                .Where(v => t.Edge.GetValueOrDefault(v) > 0)
                .OrderByDescending(v => t.Edge.GetValueOrDefault(v))
                .Select(v => $"{DataDragon.Name(v)} ({Points(t.Edge.GetValueOrDefault(v))})")
                .Take(3)
                .ToList();
            if (t.Threat >= 0.5 && named.Count >= 2)
                AddReason(c, Loc.T("reason.teamThreat", string.Join(", ", named)), R_TEAM);
        }

        // Бонус за ширину: кандидат, контрящий 2+ РАЗНЫХ наших чемпионов,
        // отсекает сразу несколько вариантов пула — именно его стоит банить.
        foreach (var (c, vs) in victimsOf)
            if (vs.Count >= 2)
                // Нелинейно (count^1.4): двое — заметно, трое и больше — сильно.
                scores[c] = scores.GetValueOrDefault(c) + W_BAN_BREADTH * Math.Pow(vs.Count, 1.4);

        // Подписи «кого контрит этот бан». Одного называем как раньше, нескольких
        // перечисляем поимённо — видно, что одним баном закрывается пол-команды.
        // С пикрейтом, как у контры моему пулу; на нескольких линиях — больший.
        string WithPick(int c, IEnumerable<string?> roles, string text)
        {
            var pk = roles.Where(r => r != null).Select(r => pickOn.GetValueOrDefault((c, r!))).DefaultIfEmpty(0).Max();
            return pk > 0 ? Loc.T("reason.withPick", text, $"{pk:F1}") : text;
        }
        foreach (var (c, hits) in hitsOf)
        {
            var roles = hits.Select(h => h.Role);
            if (hits.Count == 1)
            {
                var (id, mine, _) = hits[0];
                AddReason(c, WithPick(c, roles,
                    Loc.T(mine ? "reason.countersMyPick" : "reason.countersAlly", DataDragon.Name(id))),
                    mine ? R_MINE : R_ALLY);
                continue;
            }
            // Свой пик — первым в списке: он важнее чужих.
            var names = hits.OrderByDescending(h => h.Mine).Select(h => DataDragon.Name(h.Id));
            AddReason(c, WithPick(c, roles, Loc.T("reason.countersMany", string.Join(", ", names))),
                      hits.Any(h => h.Mine) ? R_MINE : R_ALLY);
        }

        // Выбор пяти. Бан прежде всего МОЙ: моей линии (контра моему пику и
        // пулу, сила роли) — LANE_SLOTS мест, и стоят они первыми. Защите
        // союзников — не больше TEAM_SLOTS. Раньше «широкие» баны и по контре
        // на каждого наведённого занимали до четырёх мест из пяти: у
        // владельца-саппорта четыре бана были про Ирелию и Мастера Йи и один —
        // про его линию.
        var lane = new List<int>();
        var team = new List<int>();
        bool Chosen(int k) => lane.Contains(k) || team.Contains(k);

        // 1. Моя линия: самая жёсткая контра моему пику (если навёл), затем по очкам линии.
        var laneCap = Math.Min(LANE_SLOTS, top);
        if (me is { EffectiveChampionId: not 0 }
            && bestByProtectee.TryGetValue(me.EffectiveChampionId, out var myBest))
            lane.Add(myBest.Champ);
        foreach (var k in laneScore.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Select(kv => kv.Key))
        {
            if (lane.Count >= laneCap) break;
            if (!lane.Contains(k)) lane.Add(k);
        }

        // 2. Команда: сначала «широкие» баны — бьют сразу нескольких наших, один
        //    такой закрывает пол-команды; затем самая жёсткая контра каждому
        //    союзнику по порядку; затем по очкам защиты.
        double TeamPart(int k) => scores.GetValueOrDefault(k) - laneScore.GetValueOrDefault(k);
        var teamOrder = victimsOf
            .Where(v => v.Value.Count >= 2)
            .OrderByDescending(v => v.Value.Count)
            .ThenByDescending(v => scores.GetValueOrDefault(v.Key))
            .Select(v => v.Key)
            .Concat(protectees.Where(p => !p.Mine)
                              .Select(p => bestByProtectee.TryGetValue(p.Id, out var b) ? b.Champ : 0)
                              .Where(c => c != 0))
            .Concat(scores.Keys.Where(k => TeamPart(k) > 0).OrderByDescending(TeamPart));
        var teamCap = Math.Min(TEAM_SLOTS, top - lane.Count);
        foreach (var k in teamOrder)
        {
            if (team.Count >= teamCap) break;
            if (!Chosen(k)) team.Add(k);
        }

        // 3. Добор по общему счёту, если какой-то части не хватило кандидатов, —
        //    но не больше двух банов на одного нашего чемпиона.
        const int maxPerProtectee = 2;
        var perProtectee = new Dictionary<int, int>();
        foreach (var k in lane.Concat(team))
            if (hitsOf.TryGetValue(k, out var hs0))
                foreach (var h in hs0) perProtectee[h.Id] = perProtectee.GetValueOrDefault(h.Id) + 1;
        foreach (var k in scores.Keys.Where(k => !Chosen(k)).OrderByDescending(k => scores[k]))
        {
            if (lane.Count + team.Count >= top) break;
            var hits = hitsOf.GetValueOrDefault(k);
            if (hits is { Count: > 0 } && hits.All(h => perProtectee.GetValueOrDefault(h.Id) >= maxPerProtectee))
                continue;
            (laneScore.GetValueOrDefault(k) >= TeamPart(k) ? lane : team).Add(k);
            if (hits != null)
                foreach (var h in hits) perProtectee[h.Id] = perProtectee.GetValueOrDefault(h.Id) + 1;
        }

        // На экране — сначала моя линия, потом команда; внутри — по очкам.
        return lane.OrderByDescending(k => scores.GetValueOrDefault(k))
            .Concat(team.OrderByDescending(k => scores.GetValueOrDefault(k)))
            .Select(k =>
            {
                // Порядок — по приоритету, внутри него — как добавлялись.
                var rs = (reasons.GetValueOrDefault(k) ?? [])
                    .Select((r, i) => (r.Prio, i, r.Text))
                    .OrderBy(r => r.Prio).ThenBy(r => r.i)
                    .Select(r => r.Text).ToList();
                if (rs.Count == 0) rs.Add(Loc.T("reason.notablePick"));
                return new BanRec(k, scores.GetValueOrDefault(k), [.. rs]);
            })
            .ToList();
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
