using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — подсказки и доводы: угрозы врагов, контры и синергии под портретами,
// строки доводов на карточках.
public sealed partial class RecommendationEngine
{
    // (champion_id, games, wins) → ранг по нижней границе Уилсона. minEdge отсекает
    // «неуверенные» пары (для контр); для синергии minEdge<0 — просто топ-N по LB.
    /// «Кто бьёт нашу команду в целом»: для каждого чемпиона — насколько он играет
    /// против НАШИХ лучше, чем против всех подряд, и скольких наших это покрывает.
    /// Отличается от списка контрпиков конкретного игрока: чемпион может не быть
    /// топ-контрой ни для кого по отдельности, но стабильно обыгрывать всю
    /// команду — такой бан помогает всем сразу.
    // Возвращает по каждому кандидату: насколько он опасен нашей команде и КОГО
    // именно из неё бьёт. Список нужен подписи: в счёт идут не только пики на
    // экране, но и чемпионы, которых союзники наводили раньше, — писать про них
    // «твои пики» нельзя, игрок их уже не видит.
    /// <summary>
    /// Чем кандидат опасен для нашей команды: общая угроза, список пострадавших
    /// и насколько он бьёт КАЖДОГО из них.
    ///
    /// Разбивка по именам нужна подписи в банах: «силён против 2 ваших пиков» —
    /// число без имён, по нему нельзя ни проверить, ни решить. Сила указана в
    /// тех же очках, что на карточках драфта.
    /// </summary>
    private sealed record TeamThreat(double Threat, List<int> Victims, Dictionary<int, double> Edge);

    private Dictionary<int, TeamThreat> TeamThreats(
        IReadOnlyCollection<(int Id, string? Role)> allies)
    {
        var res = new Dictionary<int, TeamThreat>();
        var allyIds = allies.Select(a => a.Id).Where(x => x > 0).Distinct().ToList();
        if (allyIds.Count < 2) return res;   // «командная» угроза начинается с двоих
        try
        {
            // Роль союзника сужает выборку до его линии. Роли может не быть
            // (ranked solo/duo, блайнд) — тогда берём по всем, как раньше:
            // приблизительно, но лучше, чем молчать.
            var parts = new List<string>();
            foreach (var (id, role) in allies)
            {
                if (id <= 0) continue;
                if (!string.IsNullOrEmpty(role) && DbRoles.Contains(role))
                    parts.Add($"(vs_champion_id = {id} AND role = '{role}')");
                else
                    parts.Add($"vs_champion_id = {id}");
            }
            if (parts.Count == 0) return res;
            var ids = string.Join(" OR ", parts);

            // Строка на КАЖДУЮ пару, а не одна на кандидата: сумму обратно по
            // именам не разложить, а подпись именно этого и требует. Складываем
            // сами, тем же способом, чтобы порядок банов не изменился.
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, vs_champion_id,
                       SUM(games*{PW}) AS g,
                       SUM(wins*{PW})  AS w
                FROM   matchup
                WHERE  ({ids}) AND patch IN (@p1, @p2, @p3)
                GROUP  BY champion_id, vs_champion_id";
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);

            var rows = new Dictionary<int, List<(int Vs, double G, double W)>>();
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var id = rd.GetInt32(0);
                    var vs = rd.GetInt32(1);
                    if (vs <= 0) continue;
                    if (!rows.TryGetValue(id, out var list)) rows[id] = list = [];
                    list.Add((vs, Convert.ToDouble(rd.GetValue(2)), Convert.ToDouble(rd.GetValue(3))));
                }

            foreach (var (id, list) in rows)
            {
                var victims = list.Select(r => r.Vs).Distinct().ToList();
                var n = victims.Count;
                var g = list.Sum(r => r.G);
                var w = list.Sum(r => r.W);
                if (n < 2 || g < 150) continue;      // прежние HAVING, теперь у нас

                // Чистая угроза: WR против наших минус собственный средний WR —
                // иначе наверх лезли бы просто сильные чемпионы патча.
                var (bg, bw) = RawBaseAny(id);
                if (bg <= 0) continue;
                var mine = Delta(bg, bw, K);
                var threat = (Delta(g, w, K_PAIR) - mine) * (g / (g + MATCHUP_CONF));
                if (threat <= 0) continue;

                // По каждому пострадавшему — тот же перевес, только по его паре.
                var edge = new Dictionary<int, double>();
                foreach (var r in list)
                    if (r.G > 0) edge[r.Vs] = Delta(r.G, r.W, K_PAIR) - mine;

                // Масштабируем по охвату: бьёт троих — весомее, чем двоих.
                res[id] = new TeamThreat(threat * n / allyIds.Count, victims, edge);
            }
        }
        catch { /* нет данных — фактор молчит */ }
        return res;
    }

    // Одна строка подсказки: кандидат, объём пары и её винрейт.
    private readonly record struct HintRow(int Id, double Games, double Wins)
    {
        public double Wr => Games > 0 ? Wins / Games : 0;
    }

    private static List<HintRow> ReadHintRows(SqliteCommand cmd)
    {
        var rows = new List<HintRow>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new HintRow(rd.GetInt32(0),
                                 Convert.ToDouble(rd.GetValue(1)),
                                 Convert.ToDouble(rd.GetValue(2))));
        return rows;
    }

    private static List<int> RankByWilson(SqliteCommand cmd, int top, double minEdge = HINT_MIN_EDGE) =>
        ReadHintRows(cmd)
            .Select(r => (r.Id, Lb: WilsonLower(r.Wins, r.Games)))
            .Where(x => x.Lb > minEdge)
            .OrderByDescending(x => x.Lb)
            .Take(top).Select(x => x.Id).ToList();

    /// <summary>
    /// Топ N чемпионов той же роли, лучше всего контрящих данного врага.
    /// Если роль неизвестна — запрос по всем ролям. minGames/minEdge можно
    /// ослабить (защитные баны: данные по паре разрежены, а показать надо).
    /// </summary>
    /// <param name="fill">
    /// Добрать до N, если по строгим порогам набралось меньше. Нужно боковым
    /// подсказкам: там место под три иконки, и «одна вместо трёх» читается как
    /// поломка. Добор берёт следующих по Уилсону, но только с настоящим
    /// перевесом (WR выше половины) — иначе это был бы уже не контрпик.
    /// </param>
    public IReadOnlyList<int> TopCounters(int enemyId, string? enemyRole = null, int top = 3,
                                          int? minGames = null, double? minEdge = null,
                                          bool fill = false)
    {
        if (enemyId == 0) return [];
        try
        {
            var byRole = !string.IsNullOrEmpty(enemyRole);
            var cmd = _db.CreateCommand();
            // По всем дивизионам сразу: данные по парам разрежены, фильтр по бакету
            // добил бы выборку. Ранжируем/фильтруем по Уилсону уже в C#.
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}) AS g, SUM(wins*{PW}) AS w
                FROM   matchup
                WHERE  vs_champion_id = @v AND patch IN (@p1, @p2, @p3)
                       {(byRole ? "AND role = @r AND champion_id IN (" + RolePlayersSql + ")" : "")}
                GROUP  BY champion_id";
            cmd.Parameters.AddWithValue("@v",   enemyId);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            if (byRole) { cmd.Parameters.AddWithValue("@r", enemyRole); AddRolePlayerParams(cmd); }

            var rows = ReadHintRows(cmd);
            double gMin = minGames ?? HINT_MIN_GAMES, eMin = minEdge ?? HINT_MIN_EDGE;
            var strict = rows.Where(r => r.Games >= gMin && WilsonLower(r.Wins, r.Games) > eMin)
                             .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                             .Select(r => r.Id);
            if (!fill) return strict.Take(top).ToList();

            // Сначала те, у кого перевес настоящий…
            var winning = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr > 0.50)
                              .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                              .Select(r => r.Id);
            // …и только потом ровные пары. Против чемпиона вроде Джинкс роль
            // может не знать НИ ОДНОГО победного матчапа: лучший ответ там —
            // тот, кто не проигрывает. Ниже этой границы уже проигрыш, и
            // называть его контрой нельзя даже ради третьей иконки.
            var even = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr >= HINT_EVEN_WR)
                           .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
                           .Select(r => r.Id);
            return strict.Concat(winning).Concat(even).Distinct().Take(top).ToList();
        }
        catch { return []; }
    }

    /// <summary>
    /// Топ N чемпионов МОЕЙ роли с наилучшей синергией с данным союзником.
    /// </summary>
    /// <param name="allyRole">
    /// Роль союзника, если известна. Без неё считается сумма по всем его ролям,
    /// а это разные пары: у Владимира на топе в лучших напарниках-саппортах
    /// Бард и Тарик, у него же на миде — Блицкранк и Рената.
    /// </param>
    public IReadOnlyList<int> TopSynergies(int allyId, string myRole, int top = 3,
                                           string? allyRole = null)
    {
        if (allyId == 0 || string.IsNullOrEmpty(myRole)) return [];
        // Своя роль союзнику не подходит: двоих на одной линии не бывает, и
        // сумма по такой паре собрана из случайных флексов.
        if (allyRole == myRole) allyRole = null;

        var byRole = ByAllyRole(allyId, myRole, top, allyRole);
        if (allyRole is null || byRole.Count >= top) return byRole;
        // Роль союзника известна, но пары по ней разрежены — добираем общими.
        return byRole.Concat(ByAllyRole(allyId, myRole, top, null))
                     .Distinct().Take(top).ToList();
    }

    private List<int> ByAllyRole(int allyId, string myRole, int top, string? allyRole)
    {
        try
        {
            var cmd = _db.CreateCommand();
            // Синергия по всем дивизионам (данные разрежены), порог игр ниже и без
            // фильтра edge — чтобы стабильно заполнять 3 лучших партнёра по LB.
            // Кандидаты — только те, кто мою роль правда играет: без этого в
            // напарники к Фиддлстиксу на топ выходил Дрэйвен на двенадцати играх.
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}) AS g, SUM(wins*{PW}) AS w
                FROM   synergy
                WHERE  ally_id = @a AND role = @r AND patch IN (@p1, @p2, @p3)
                       {(allyRole is null ? "" : "AND ally_role = @ar")}
                       AND champion_id IN ({RolePlayersSql})
                GROUP  BY champion_id
                HAVING SUM(games*{PW}) >= @min";
            cmd.Parameters.AddWithValue("@a",   allyId);
            cmd.Parameters.AddWithValue("@min", HINT_MIN_SYN);
            cmd.Parameters.AddWithValue("@r",   myRole);
            cmd.Parameters.AddWithValue("@p1",  _p1);
            cmd.Parameters.AddWithValue("@p2",  _p2);
            cmd.Parameters.AddWithValue("@p3",  _p3);
            if (allyRole is not null) cmd.Parameters.AddWithValue("@ar", allyRole);
            AddRolePlayerParams(cmd);
            return RankByWilson(cmd, top, minEdge: -1.0);
        }
        catch { return []; }
    }

    // ---------- Обоснование ----------

    private static string[] BuildReasons(
        int    champId,
        double directDelta, int    directOppId,
        double synDelta,    IReadOnlyList<(int Id, string Role, double Delta)> synByAlly,
        double otherDelta,  IReadOnlyList<(int Id, double Delta)> otherByEnemy,
        double baseDelta,   double comfortDelta)
    {
        var lines = new List<string>();

        // 0. Комфорт: часто наигранный чемпион игрока — упоминаем первым.
        if      (comfortDelta >= 3.8) lines.Add(Good(Loc.T("reason.comfortHigh")));
        else if (comfortDelta >= 1.9) lines.Add(Good(Loc.T("reason.comfortMid")));

        // 1. Развёрнутые объяснения синергии с конкретными союзниками.
        // Сначала пары с наибольшей статистической синергией. Дедупим по ТИПУ
        // связки — чтобы не повторять одинаковые по смыслу фразы. До 2 объяснений.
        var seenKinds = new HashSet<string>();
        foreach (var a in synByAlly.OrderByDescending(x => x.Delta))
        {
            var ex = TeamSynergies.ExplainPair(champId, a.Id, a.Role);
            if (ex is null) continue;
            if (!seenKinds.Add(ex.Value.Kind)) continue; // тот же тип связки уже показан
            // Помечаем как довод «за» и как несменяемый: без знака интерфейс
            // считал связку нейтральной и выбрасывал её первой же строкой
            // отбора. Владелец на это и наткнулся: Мальфит с Ясуо в команде,
            // а про подброс с ультом в карточке ни слова.
            // Со ЦИФРОЙ: сколько связка даёт на самом деле. Без неё строка
            // обещает выгоду, а насколько — не говорит; остальные доводы в
            // карточке числа приводят, и связка выглядела голословной.
            //
            // Цифру показываем, только когда она заметна. У пары может не
            // оказаться игр (свежий чемпион) или перевес может быть нулевым:
            // комбо при этом настоящее, просто данные его не выделяют, и
            // приписывать «+0,0%» значило бы делать вид, что померили точнее,
            // чем померили. Отрицательную не пишем тем более — довод «за» с
            // минусом читается как ошибка.
            lines.Add(Key(a.Delta >= 0.3
                ? $"{ex.Value.Text} ({Points(a.Delta)}%)"
                : ex.Value.Text));
            if (seenKinds.Count >= 2) break;
        }
        var explained = seenKinds.Count;

        // 2. Матчап с прямым оппонентом. Пороги под ЧИСТУЮ дельту (матчап минус
        // собственная база): типичная сильная контра теперь +2..+5пп, не +5..+10.
        if (directOppId != 0)
        {
            var oppName = DataDragon.Name(directOppId);
            if      (directDelta >=  2.0) lines.Add(Good(Loc.T("reason.lineWinStrong", oppName, $"{directDelta:F1}")));
            else if (directDelta >=  1.0) lines.Add(Good(Loc.T("reason.lineWin", oppName, $"{directDelta:F1}")));
            else if (directDelta >=  0.3) lines.Add(Good(Loc.T("reason.lineEdge", oppName)));
            else if (directDelta <= -1.5) lines.Add(Bad(Loc.T("reason.lineHard", oppName, $"{directDelta:F1}")));
            else                          lines.Add(Loc.T("reason.lineNeutral", oppName));
        }

        // 3. Выгодные матчапы против конкретных врагов (чистые дельты — порог ниже)
        if (otherByEnemy.Count > 0)
        {
            var good = otherByEnemy.Where(x => x.Delta >= 1.0)
                                   .OrderByDescending(x => x.Delta).Take(2).ToList();
            if (good.Count > 0)
            {
                var names = string.Join(", ", good.Select(x => DataDragon.Name(x.Id)));
                lines.Add(Good(Loc.T("reason.favMatchups", names)));
            }
        }

        // 4. Если понятных объяснений не нашлось, но статистика хорошая —
        // мягкая общая фраза вместо сухих процентов.
        if (explained == 0 && synByAlly.Count > 0 && synDelta > 0.5)
            lines.Add(Good(Loc.T("reason.fitsTeam")));

        // 5. Базовый WR
        if      (baseDelta >=  2.5) lines.Add(Good(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}")));
        else if (baseDelta >=  1.0) lines.Add(Good(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}")));
        else if (baseDelta <= -1.5) lines.Add(Bad(Loc.T("reason.baseLow", $"{50 + baseDelta:F1}")));

        if (lines.Count == 0) lines.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));
        return [.. lines];
    }
}
