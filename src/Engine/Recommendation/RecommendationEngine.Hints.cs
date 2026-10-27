using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — подсказки и доводы: контры и синергии под портретами,
// строки доводов на карточках.
public sealed partial class RecommendationEngine
{
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

    // (champion_id, games, wins) → ранг по нижней границе Уилсона. minEdge отсекает
    // «неуверенные» пары (для контр); для синергии minEdge<0 — просто топ-N по LB.
    private static List<HintRow> RankByWilson(SqliteCommand cmd, int top, double minEdge = HINT_MIN_EDGE) =>
        ReadHintRows(cmd)
            .Where(r => WilsonLower(r.Wins, r.Games) > minEdge)
            .OrderByDescending(r => WilsonLower(r.Wins, r.Games))
            .Take(top).ToList();

    /// <summary>Чемпион из подсказки с винрейтом пары (доля, 0..1) и его ролью.</summary>
    public readonly record struct HintPick(int Id, string Role, double Wr);

    /// <summary>
    /// Топ N чемпионов той же роли, лучше всего контрящих данного врага.
    /// Если роль неизвестна — запрос по всем ролям. minGames/minEdge можно
    /// ослабить, когда данные по паре разрежены, а показать надо.
    /// </summary>
    /// <param name="fill">
    /// Добрать до N, если по строгим порогам набралось меньше. Нужно боковым
    /// подсказкам: там место под три иконки, и «одна вместо трёх» читается как
    /// поломка. Добор берёт следующих по Уилсону, но только с настоящим
    /// перевесом (WR выше половины) — иначе это был бы уже не контрпик.
    /// </param>
    public IReadOnlyList<int> TopCounters(int enemyId, string? enemyRole = null, int top = 3,
                                          int? minGames = null, double? minEdge = null,
                                          bool fill = false) =>
        CounterRows(enemyId, enemyRole, top, minGames, minEdge, fill).Select(r => r.Id).ToList();

    /// <summary>
    /// Те же контры, что под портретом врага (с добором), но с винрейтом
    /// контры против него — для карточки чемпиона при наведении на слот.
    /// </summary>
    public IReadOnlyList<HintPick> CountersWithWr(int champId, string role, int top = 5) =>
        CounterRows(champId, role, top, null, null, fill: true)
            .Select(r => new HintPick(r.Id, role, r.Wr)).ToList();

    private List<HintRow> CounterRows(int enemyId, string? enemyRole, int top,
                                      int? minGames, double? minEdge, bool fill)
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
                             .OrderByDescending(r => WilsonLower(r.Wins, r.Games));
            if (!fill) return strict.Take(top).ToList();

            // Сначала те, у кого перевес настоящий…
            var winning = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr > 0.50)
                              .OrderByDescending(r => WilsonLower(r.Wins, r.Games));
            // …и только потом ровные пары. Против чемпиона вроде Джинкс роль
            // может не знать НИ ОДНОГО победного матчапа: лучший ответ там —
            // тот, кто не проигрывает. Ниже этой границы уже проигрыш, и
            // называть его контрой нельзя даже ради третьей иконки.
            var even = rows.Where(r => r.Games >= HINT_FILL_GAMES && r.Wr >= HINT_EVEN_WR)
                           .OrderByDescending(r => WilsonLower(r.Wins, r.Games));
            return strict.Concat(winning).Concat(even).DistinctBy(r => r.Id).Take(top).ToList();
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

        var byRole = ByAllyRole(allyId, myRole, top, allyRole).Select(r => r.Id).ToList();
        if (allyRole is null || byRole.Count >= top) return byRole;
        // Роль союзника известна, но пары по ней разрежены — добираем общими.
        return byRole.Concat(ByAllyRole(allyId, myRole, top, null).Select(r => r.Id))
                     .Distinct().Take(top).ToList();
    }

    /// <summary>
    /// Лучшие напарники чемпиона на всех прочих ролях, с винрейтом пары — для
    /// карточки чемпиона при наведении на слот. Отбор как у иконок под
    /// портретом союзника (кандидаты — кто роль правда играет, ранг по Уилсону),
    /// только ролей четыре, и общий топ сводится из лучших каждой.
    /// </summary>
    public IReadOnlyList<HintPick> PartnersWithWr(int champId, string role, int top = 5)
    {
        if (champId == 0 || string.IsNullOrEmpty(role)) return [];
        return [.. new[] { "top", "jungle", "mid", "adc", "support" }
            .Where(r => r != role)
            .SelectMany(r => ByAllyRole(champId, r, top, role).Select(x => (Row: x, Role: r)))
            .OrderByDescending(x => WilsonLower(x.Row.Wins, x.Row.Games))
            .Take(top)
            .Select(x => new HintPick(x.Row.Id, x.Role, x.Row.Wr))];
    }

    private List<HintRow> ByAllyRole(int allyId, string myRole, int top, string? allyRole)
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
        double baseDelta,   string grade,       double comfortDelta)
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

        // 5. Сила в патче — по грейду тир-листа, чтобы карточка и тир-лист
        // говорили одно: OP — «один из сильнейших», S — хороший, C — низкий;
        // A и B — без строки. До 20.10 пороги были свои (+2,5 / +1 / −1,5 к
        // винрейту за два патча) и с тир-листом не сходились.
        if      (grade == "OP") lines.Add(Good(Loc.T("reason.baseTop", $"{50 + baseDelta:F1}")));
        else if (grade == "S")  lines.Add(Good(Loc.T("reason.baseGood", $"{50 + baseDelta:F1}")));
        else if (grade == "C")  lines.Add(Bad(Loc.T("reason.baseLow", $"{50 + baseDelta:F1}")));

        if (lines.Count == 0) lines.Add(Loc.T("reason.baseNeutral", $"{50 + baseDelta:F1}"));
        return [.. lines];
    }
}
