namespace Counterplay;

// Движок подбора — пик вслепую: чем грозит чемпион, пока соперник ещё не выбран.
// Математика — BlindMath; здесь — откуда берутся пары и как это ложится на драфт.
public sealed partial class RecommendationEngine
{
    // Кто вообще играет роль: тот же отбор, что у кандидатов (GetCandidates), —
    // доля в пиках роли и винрейт за окно двух патчей. По доле соперник
    // «тянет» чемпиона из пула, по винрейту считается ожидание пары.
    private readonly Dictionary<string, List<(int Id, double Share, double Wr)>> _roleField = new();

    private List<(int Id, double Share, double Wr)> RoleField(string role)
    {
        if (_roleField.TryGetValue(role, out var hit)) return hit;
        var rows = new List<(int Id, double G, double W)>();
        try
        {
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}), SUM(wins*{PW}) FROM base_wr
                WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY champion_id";
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) rows.Add((rd.GetInt32(0), rd.GetDouble(1), rd.GetDouble(2)));
        }
        catch { /* нет таблицы — поле пустое, вслепую молчим */ }

        var total = rows.Sum(r => r.G);
        var kept  = rows.Where(r => r.G >= MIN_GAMES && r.G >= MIN_ROLE_SHARE * total).ToList();
        var sum   = kept.Sum(r => r.G);
        return _roleField[role] = [.. kept.Select(r => (r.Id, r.G / sum, (r.W + K / 2) / (r.G + K)))];
    }

    // Пары чемпиона против соперников одной роли: «выше ожидаемого» по шансам
    // и шум. Своя линия — таблица matchup, чужая — кросс-ролевая (CrossOf).
    // Обе уже со сводкой по дивизионам (WithPool), как во всём подборе.
    private List<BlindMath.Cell> BlindCells(int champId, string myRole, string vsRole)
    {
        var mine = RoleField(myRole).FirstOrDefault(x => x.Id == champId);
        double wr;
        if (mine.Id != 0) wr = mine.Wr;
        else { var (bg, bw) = RawBase(champId, myRole); wr = (bw + K / 2) / (bg + K); }

        var pairs = vsRole == myRole
            ? MatchupsOf(champId, myRole)
            : CrossOf(champId, myRole).Where(kv => kv.Key.VsRole == vsRole)
                                      .ToDictionary(kv => kv.Key.Vs, kv => kv.Value);
        var cells = new List<BlindMath.Cell>();
        foreach (var x in RoleField(vsRole))
        {
            if (x.Id == champId || !pairs.TryGetValue(x.Id, out var p) || p.g <= 0) continue;
            var e = BlindMath.ExpectedWr(wr, x.Wr);
            cells.Add(new BlindMath.Cell(x.Id, x.Share, (p.w / p.g - e) * 100, BlindMath.Noise2(e, p.g), p.g));
        }
        return cells;
    }

    // Профили всех чемпионов роли против одной роли соперников. Считаются разом:
    // приор разброса (типичный по роли) нужен до того, как строить любой из них.
    // Данные за жизнь движка не меняются — кэш на неё же.
    private readonly Dictionary<(string My, string Vs), (double RoleTrue2, Dictionary<int, BlindMath.Profile> ById)>
        _blindProfiles = new();

    private BlindMath.Profile BlindProfile(int champId, string myRole, string vsRole)
    {
        if (!_blindProfiles.TryGetValue((myRole, vsRole), out var set))
        {
            var mine  = RoleField(myRole);
            var cells = mine.ToDictionary(c => c.Id, c => BlindCells(c.Id, myRole, vsRole));
            var w     = mine.Sum(c => c.Share);
            var true2 = w > 0 ? mine.Sum(c => c.Share * BlindMath.RawTrue2(cells[c.Id])) / w : 0;
            set = (true2, cells.ToDictionary(kv => kv.Key, kv => BlindMath.Build(kv.Value, true2)));
            _blindProfiles[(myRole, vsRole)] = set;
        }
        // Чемпион вне поля роли (пул игрока на редкой линии) — тем же приором.
        if (!set.ById.TryGetValue(champId, out var prof))
            set.ById[champId] = prof = BlindMath.Build(BlindCells(champId, myRole, vsRole), set.RoleTrue2);
        return prof;
    }

    // ── Поле соперника по уже взятым врагам ─────────────────────────────────
    // Кого враг возьмёт на роль, зависит от его уже взятых тиммейтов: саппорта
    // берут под своего АДК, АДК — под саппорта. Замер 05.11.2026 по 636 тыс.
    // драфтов (кто с кем в одной команде, без самого драфта): саппорт по их АДК
    // угадывается на 15,7% лучше, АДК по саппорту — на 14,5%, лес по
    // остальным — на 3,8%, а саппорт по их топу и миду — на 0,6% (почти ничего).
    // Наивный Байес по парам из таблицы синергии: доля × Π по взятым
    // (совместные игры + FIELD_PRIOR·доля) / (игры тиммейта + FIELD_PRIOR) / доля.
    private readonly Dictionary<(int K, string KRole, string Role), (Dictionary<int, double> Co, double Total)> _coPicks = new();

    private (Dictionary<int, double> Co, double Total) CoPicks(int k, string kRole, string role)
    {
        if (_coPicks.TryGetValue((k, kRole, role), out var hit)) return hit;
        var co = new Dictionary<int, double>();
        try
        {
            // Пара записана одной строкой в любую сторону — берём обе.
            var cmd = _db.CreateCommand();
            cmd.CommandText = $@"
                SELECT champion_id, SUM(games*{PW}) FROM synergy
                WHERE ally_id=@k AND ally_role=@kr AND role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY champion_id
                UNION ALL
                SELECT ally_id, SUM(games*{PW}) FROM synergy
                WHERE champion_id=@k AND role=@kr AND ally_role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
                GROUP BY ally_id";
            cmd.Parameters.AddWithValue("@k",  k);
            cmd.Parameters.AddWithValue("@kr", kRole);
            cmd.Parameters.AddWithValue("@r",  role);
            cmd.Parameters.AddWithValue("@t",  TierBucket);
            cmd.Parameters.AddWithValue("@p1", _p1);
            cmd.Parameters.AddWithValue("@p2", _p2);
            cmd.Parameters.AddWithValue("@p3", _p3);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                if (!rd.IsDBNull(1)) co[rd.GetInt32(0)] = co.GetValueOrDefault(rd.GetInt32(0)) + rd.GetDouble(1);
        }
        catch { /* нет таблицы — поле без поправки */ }
        return _coPicks[(k, kRole, role)] = (co, co.Values.Sum());
    }

    // Кто из врагов уже известен и на какой роли: позиция из драфта (окно
    // раскладывает скрытые роли само), иначе — самая частая роль чемпиона.
    private List<(int Id, string Role)> KnownEnemies(DraftState state) =>
        [.. state.TheirTeam.Where(p => p.EffectiveChampionId != 0)
            .Select(p => (p.EffectiveChampionId,
                          LcuToDbRole(p.Position) is { Length: > 0 } r ? r : InferPrimaryRole(p.EffectiveChampionId)))];

    // Ключ поправки: только тиммейты на ДРУГИХ ролях (на своей — это он сам).
    private static string KnownKey(IReadOnlyList<(int Id, string Role)> known, string role) =>
        string.Join(",", known.Where(k => k.Id != 0 && k.Role.Length > 0 && k.Role != role)
                              .Select(k => $"{k.Id}:{k.Role}").Order());

    private readonly Dictionary<string, Dictionary<int, double>> _fieldWeights = new();

    // Доли поля роли с поправкой на уже взятых врагов (без нормировки — её
    // делает BlindMath по сумме долей).
    private Dictionary<int, double> FieldWeights(string role, IReadOnlyList<(int Id, string Role)> known, string key)
    {
        var ck = role + "|" + key;
        if (_fieldWeights.TryGetValue(ck, out var hit)) return hit;
        if (_fieldWeights.Count > 256) _fieldWeights.Clear();
        var field = RoleField(role);
        var w = field.ToDictionary(x => x.Id, x => x.Share);
        foreach (var (k, kr) in known)
        {
            if (k == 0 || kr.Length == 0 || kr == role) continue;
            var (co, total) = CoPicks(k, kr, role);
            if (total <= 0) continue;
            foreach (var x in field)
                w[x.Id] *= (co.GetValueOrDefault(x.Id) + FIELD_PRIOR * x.Share) / (total + FIELD_PRIOR) / x.Share;
        }
        return _fieldWeights[ck] = w;
    }

    // Соперники, которых ещё можно взять против меня: поле роли без меня самого
    // и без тех, кто уже взят, наведён чужими или забанен, с долями по уже
    // взятым врагам. Пара без игр — центр чемпиона со всем истинным разбросом:
    // про неё ничего не известно.
    private List<(int Id, double Share, BlindMath.Est Pair)> BlindField(
        BlindMath.Profile prof, int champId, string vsRole, IReadOnlySet<int> taken, Dictionary<int, double> weights) =>
        [.. RoleField(vsRole)
            .Where(x => x.Id != champId && !taken.Contains(x.Id))
            .Select(x => (x.Id, weights.GetValueOrDefault(x.Id, x.Share), prof.Of(x.Id)))];

    // Результат на драфт: подбор в одном событии зовётся несколько раз (общий
    // список, пул, наведение, вес пула в банах, напарник), а интеграл худшего
    // из пула не бесплатный. Ключ — чемпион, роли и кто уже недоступен.
    private readonly Dictionary<string, (double Raw, double Chance, List<(int Id, double Share, BlindMath.Est Pair)> Field)>
        _blindRaw = new();

    private static string TakenKey(IReadOnlySet<int> taken) => string.Join(",", taken.Order());

    /// <summary>
    /// Ожидаемый матчап вслепую до центровки, пп. Соперник с долей
    /// BLIND_COUNTER_RATE берёт лучшую контру из пула в BLIND_POOL чемпионов,
    /// иначе — что угодно по пикрейту. Из этого вычитается то, что чемпиону
    /// достаётся на деле (Drawn): это уже сидит в его базовом винрейте, и
    /// Ясуо, которого и так постоянно контрят, второй раз за это не платит.
    /// </summary>
    private (double Raw, double Chance, List<(int Id, double Share, BlindMath.Est Pair)> Field) BlindRaw(
        int champId, string myRole, string vsRole, IReadOnlySet<int> taken, string takenKey,
        Dictionary<int, double> weights, string knownKey)
    {
        var key = $"{champId}|{myRole}|{vsRole}|{takenKey}|{knownKey}";
        if (_blindRaw.TryGetValue(key, out var hit)) return hit;
        if (_blindRaw.Count > 4000) _blindRaw.Clear();   // драфтов за жизнь движка много

        var prof  = BlindProfile(champId, myRole, vsRole);
        var field = BlindField(prof, champId, vsRole, taken, weights);
        var pairs = field.Select(f => (f.Share, f.Pair)).ToList();
        var raw   = BLIND_COUNTER_RATE * BlindMath.ExpectedMin(pairs, BLIND_POOL)
                  + (1 - BLIND_COUNTER_RATE) * BlindMath.ExpectedAny(pairs)
                  - prof.Drawn;
        return _blindRaw[key] = (raw, BlindMath.CounterChance(BlindMath.CounterShare(pairs), BLIND_POOL), field);
    }

    // Центр — типичный чемпион роли в том же драфте (по пикрейту). Без центровки
    // вслепую минус стоял бы у всех (лучшая контра всегда хуже среднего), и
    // карточки пула («выгодные — с оценкой выше нуля») пустели бы на первом
    // пике.
    // Заодно — типичный шанс нарваться на контру: строка в карточке говорит
    // «надёжен» и «рискован» относительно него.
    private readonly Dictionary<string, (double Raw, double Chance)> _blindCenter = new();

    private (double Raw, double Chance) BlindCenter(string myRole, string vsRole, IReadOnlySet<int> taken, string takenKey,
                                                    Dictionary<int, double> weights, string knownKey)
    {
        var key = $"{myRole}|{vsRole}|{takenKey}|{knownKey}";
        if (_blindCenter.TryGetValue(key, out var c)) return c;
        if (_blindCenter.Count > 64) _blindCenter.Clear();
        var mine = RoleField(myRole);
        var w    = mine.Sum(x => x.Share);
        if (w <= 0) return _blindCenter[key] = (0, 0);
        double raw = 0, chance = 0;
        foreach (var x in mine)
        {
            var r = BlindRaw(x.Id, myRole, vsRole, taken, takenKey, weights, knownKey);
            raw    += x.Share * r.Raw / w;
            chance += x.Share * r.Chance / w;
        }
        return _blindCenter[key] = (raw, chance);
    }

    /// <summary>
    /// Чем грозит взять чемпиона вслепую против ещё не выбранного соперника
    /// роли vsRole (своей линии или, у бота, вражеского дуо). taken — кого
    /// соперник уже не возьмёт: взятые, наведённые чужими, баны. Один расчёт
    /// на общий подбор и на пул игрока: карточки пула, подсказка напарнику и
    /// вес пула в банах идут через тот же Recommend. known — уже взятые враги
    /// с ролями: по ним поле соперника сдвигается (их саппорт — под их АДК).
    /// </summary>
    public BlindRisk Blind(int champId, string myRole, string vsRole, IReadOnlySet<int> taken,
                           IReadOnlyList<(int Id, string Role)>? known = null)
    {
        if (champId == 0 || RoleField(vsRole).Count == 0) return BlindRisk.None;
        known ??= [];
        var tk = TakenKey(taken);
        var kk = KnownKey(known, vsRole);
        var weights = FieldWeights(vsRole, known, kk);
        var (raw, chance, field) = BlindRaw(champId, myRole, vsRole, taken, tk, weights, kk);
        // Назвать можно только уверенные контры: пара в среднем хуже порога.
        var counters = field.Where(f => f.Pair.Mean <= -BlindMath.COUNTER_PP)
                            .OrderByDescending(f => f.Share).Select(f => f.Id).Take(2).ToList();
        var center = BlindCenter(myRole, vsRole, taken, tk, weights, kk);
        var value  = raw - center.Raw;
        return new BlindRisk(value, chance, center.Chance, BlindProfile(champId, myRole, vsRole).True,
                             counters, Verdict(value, chance, center.Chance));
    }

    /// Вслепую против своей линии в этом драфте — для строк пула вне подбора
    /// (фаза объявления): кто уже недоступен, считается как в подборе.
    public BlindRisk BlindFor(DraftState state, int champId, string role) =>
        Blind(champId, role, role, TakenBy(state), KnownEnemies(state));

    /// Кто играет роль и какую долю её пиков забирает — поле, из которого
    /// соперник «тянет» пул. С known — доли с поправкой на уже взятых врагов
    /// (нормированы к единице).
    public IReadOnlyList<(int Id, double Share)> FieldShares(string role,
                                                             IReadOnlyList<(int Id, string Role)>? known = null)
    {
        if (known is null || known.Count == 0) return [.. RoleField(role).Select(x => (x.Id, x.Share))];
        var w = FieldWeights(role, known, KnownKey(known, role));
        var sum = w.Values.Sum();
        return [.. RoleField(role).Select(x => (x.Id, sum > 0 ? w[x.Id] / sum : 0.0))];
    }

    /// Профиль чемпиона против роли — для проверки и разборов: центр, разбросы,
    /// что достаётся на деле, усаженные пары.
    public BlindMath.Profile BlindProfileOf(int champId, string myRole, string vsRole) =>
        BlindProfile(champId, myRole, vsRole);

    /// <summary>
    /// Вывод для игрока: шанс нарваться на контру заметно ниже или выше
    /// типичного для роли. Очки идут по ожидаемому (Value), а вывод — по
    /// шансу: у Ясуо ожидаемое почти нулевое (его и так всё время контрят, это
    /// уже в его винрейте), но контра найдётся у двух соперников из трёх, и
    /// сказать об этом надо. Со знаком очков вывод не спорит: «надёжен» —
    /// только при плюсе, «рискован» — только при нуле и ниже.
    /// </summary>
    private static int Verdict(double value, double chance, double typical) =>
        value >= BLIND_REASON_PP && chance <= typical - BLIND_CHANCE_GAP ? 1
        : value <= 0 && chance >= typical + BLIND_CHANCE_GAP ? -1
        : 0;

    /// Строка в карточке подбора — с шансом и, если они есть, самыми частыми контрами.
    private static string? BlindReason(BlindRisk r)
    {
        var pct = $"{100 * r.Chance:F0}";
        if (r.Verdict > 0)
            return Good(Loc.T("reason.blindSafe", pct));
        if (r.Verdict < 0)
            return Bad(r.Counters.Count > 0
                ? Loc.T("reason.blindRisky", pct, string.Join(", ", r.Counters.Select(DataDragon.Name)))
                : Loc.T("reason.blindRiskyAny", pct));
        return null;
    }
}
