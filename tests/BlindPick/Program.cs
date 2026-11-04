using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Пик вслепую (BlindMath + часть Blind движка).
///
/// Пока соперник на линии не выбран, «против оппонента» раньше стояло нулём у
/// всех кандидатов: подбор не отличал чемпиона, которого законтрить нечем, от
/// того, у кого контр четверть роли. Теперь там ожидаемый матчап против того,
/// кого возьмут в ответ: пары «выше ожидаемого» по шансам, истинный разброс
/// за вычетом шума, худшее из пула соперника.
///
/// Первая половина — математика на подставных числах: она обязана повторять
/// известные примеры (Ари против Мел, 85% у Ясуо) и честно вычитать шум.
/// Вторая — подбор на живой базе (только чтение): Ари вслепую выше Ясуо,
/// баны его контр это меняют, известный соперник выключает расчёт, у бота
/// невыбранный дуо врага считается так же.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Ahri = 103, Yasuo = 157, Thresh = 412;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "BlindPick");
        Directory.CreateDirectory(AppPaths.RootOverride);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        // База матчей — настоящая, её только читаем.
        DataDb.DirOverride = real;
        // Справочник имён — копией к себе: в выводе нужны имена, а не номера.
        var dd = Path.Combine(AppPaths.RootOverride, "ddragon");
        Directory.CreateDirectory(dd);
        foreach (var f in new[] { "champion-ru_RU.json", "champion-ru_RU.ver" })
        {
            var src = Path.Combine(real, "ddragon", f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dd, f), overwrite: true);
        }
        PoolStore.DirOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-pools", "BlindPick");
        Directory.CreateDirectory(PoolStore.DirOverride);
        var stale = Path.Combine(PoolStore.DirOverride, "pools.json");
        if (File.Exists(stale)) File.Delete(stale);
        PoolStore.Reload();

        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        DataDragon.LoadFromCache("ru_RU");

        Strings();
        MathChecks();
        LiveChecks();
        return Done();
    }

    // ── Строки на всех языках ────────────────────────────────────────────────

    private static readonly string[] Keys =
    [
        "metric.blind", "reason.blindSafe", "reason.blindRisky", "reason.blindRiskyAny",
        "plan.blindSafe", "plan.blindRisky", "plan.tipBlind", "plan.tipCounters",
    ];

    private static void Strings()
    {
        Console.WriteLine("── строки на всех языках ──");
        // Строки встроены в программу ресурсом — читаем их из репозитория.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "assets", "i18n")))
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) { Check("папка строк найдена", false, AppContext.BaseDirectory); return; }
        dir = Path.Combine(dir, "assets", "i18n");
        var docs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        Check("языков 15", docs.Count == 15, $"{docs.Count}");
        static string? Get(JsonElement root, string key)
        {
            var parts = key.Split('.');
            return root.TryGetProperty(parts[0], out var sec) && sec.TryGetProperty(parts[1], out var v)
                ? v.GetString() : null;
        }
        static string Slots(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
        foreach (var key in Keys)
        {
            var ru = Get(docs["ru"], key) ?? "";
            var bad = docs.Where(d => Get(d.Value, key) is not { Length: > 0 } v || Slots(v) != Slots(ru))
                          .Select(d => d.Key).ToList();
            Check($"{key}: есть везде, подстановки как в русском", ru.Length > 0 && bad.Count == 0,
                  bad.Count == 0 ? Slots(ru) : string.Join(", ", bad));
        }
        var left = docs.Where(d => Get(d.Value, "reason.neutral") is not null).Select(d => d.Key).ToList();
        Check("старой строки «нейтральный пик» нет", left.Count == 0, string.Join(", ", left));
        Console.WriteLine();
    }

    // ── Математика ───────────────────────────────────────────────────────────

    private static void MathChecks()
    {
        Console.WriteLine("── математика на подставных числах ──");

        // Ари 50,9%, Мел 45,5%. С шансами, округлёнными до 1,04 и 0,83,
        // вышло бы 55,6%; точно — 55,4%.
        var e = BlindMath.ExpectedWr(0.509, 0.455);
        Check("ожидание пары по шансам: Ари 50,9 против Мел 45,5 → 55,4%",
              Math.Abs(e - 0.554) < 0.001, $"{100 * e:F1}%");

        // Контры Ясуо — 24% пиков мида, пул 7 → 85%; у Ари одна
        // Мел (1,4%) → около 10%.
        var y = BlindMath.CounterChance(0.24, 7);
        var a = BlindMath.CounterChance(0.014, 7);
        Check("1−(1−q)^N: q=24%, N=7 → 85%", Math.Abs(y - 0.85) < 0.01, $"{100 * y:F1}%");
        Check("1−(1−q)^N: q=1,4%, N=7 → 9,4%", Math.Abs(a - 0.094) < 0.002, $"{100 * a:F1}%");

        // Худшее из N на дискретном поле — точно: половина нулей, половина −4.
        var exact = new List<(double, BlindMath.Est)> { (0.5, new(0.0, 0)), (0.5, new(-4.0, 0)) };
        var m1 = BlindMath.ExpectedMin(exact, 1);
        var m2 = BlindMath.ExpectedMin(exact, 2);
        Check("худшее из одного — среднее (−2)", Math.Abs(m1 + 2) < 1e-9, $"{m1:F3}");
        Check("худшее из двух — 0,75·(−4) = −3", Math.Abs(m2 + 3) < 1e-9, $"{m2:F3}");
        // То же поле с крошечной неуверенностью идёт через интеграл — и сходится.
        var fuzzy = new List<(double, BlindMath.Est)> { (0.5, new(0.0, 1e-4)), (0.5, new(-4.0, 1e-4)) };
        var mf = BlindMath.ExpectedMin(fuzzy, 2);
        Check("интеграл сходится с точным расчётом", Math.Abs(mf + 3) < 0.02, $"{mf:F3}");
        // Неуверенно известное поле: N(0, 1), худшее из семи ≈ −1,35.
        var normal = new List<(double, BlindMath.Est)> { (1.0, new(0.0, 1.0)) };
        var mn = BlindMath.ExpectedMin(normal, 7);
        Check("худшее из семи по N(0,1) ≈ −1,35", Math.Abs(mn + 1.352) < 0.01, $"{mn:F3}");

        // Шум: истинный разброс 1,5 пп, от 200 до 4000 игр на пару — шум того
        // же порядка, что сигнал. Один чемпион гуляет (оценка разброса по
        // шестидесяти парам сама шумная), поэтому меряем смещение на трёхстах:
        // наблюдаемый² завышен на шум², за вычетом шума — попадает в правду.
        var rng = new Random(7);
        double obs2 = 0, found2 = 0, real2 = 0;
        const int reps = 300;
        for (int i = 0; i < reps; i++)
        {
            var (cells, truth0) = Synthetic(rng, trueSd: 1.5, n: 60, gamesLo: 200, gamesHi: 4000);
            var real = WeightedSd(cells, truth0);
            var prof = BlindMath.Build(cells, roleTrue2: 0);
            obs2 += prof.Observed * prof.Observed / reps;
            found2 += BlindMath.RawTrue2(cells) / reps;
            real2 += real * real / reps;
        }
        Check("наблюдаемый разброс² завышен шумом", obs2 > 1.3 * real2, $"наблюдаемый² {obs2:F2}, на деле {real2:F2}");
        Check("за вычетом шума² — без смещения (± 8%)", Math.Abs(found2 / real2 - 1) < 0.08,
              $"найдено {found2:F2}, на деле {real2:F2}");

        // Редкий чемпион: 5–15 игр на пару, шум громче сигнала. Своей оценке
        // верить нельзя — без приора роли он вышел бы «идеально слепым».
        var (rare, truth) = Synthetic(rng, trueSd: 1.5, n: 45, gamesLo: 5, gamesHi: 15);
        var noPrior   = BlindMath.Build(rare, roleTrue2: 0);
        var withPrior = BlindMath.Build(rare, roleTrue2: 1.5 * 1.5);
        Check("редкий с приором роли не «идеально слепой»", withPrior.True > 1.0,
              $"с приором {withPrior.True:F2}, своя оценка {noPrior.True:F2}, шум {withPrior.Noise:F2}");

        // Цена контры — худшее из семи минус среднее. По сырым шумным парам она
        // раздута (минимум из шумных оценок всегда ниже правды), по одним
        // усаженным средним — пропадает (всё поле стянуто к центру). По парам
        // с остаточной неуверенностью — близка к правде.
        double Cost(IReadOnlyList<(double, BlindMath.Est)> f) => BlindMath.ExpectedMin(f, 7) - BlindMath.ExpectedAny(f);
        List<(double, BlindMath.Est)> F(Func<BlindMath.Cell, BlindMath.Est> v) => [.. rare.Select(c => (c.Share, v(c)))];
        var trueCost   = Cost(F(c => new(truth[c.Id], 0)));
        var rawCost    = Cost(F(c => new(c.Above, 0)));
        var meanCost   = Cost(F(c => new(withPrior.Of(c.Id).Mean, 0)));
        var postCost   = Cost(F(c => withPrior.Of(c.Id)));
        Check("цена контры по парам с неуверенностью ближе к правде, чем по сырым и по средним",
              Math.Abs(postCost - trueCost) < Math.Abs(rawCost - trueCost)
              && Math.Abs(postCost - trueCost) < Math.Abs(meanCost - trueCost),
              $"правда {trueCost:F2}, сырые {rawCost:F2}, средние {meanCost:F2}, с неуверенностью {postCost:F2}");
    }

    private static double WeightedSd(IReadOnlyList<BlindMath.Cell> cells, Dictionary<int, double> truth)
    {
        var w = cells.Sum(c => c.Share);
        var m = cells.Sum(c => c.Share * truth[c.Id]) / w;
        return Math.Sqrt(cells.Sum(c => c.Share * (truth[c.Id] - m) * (truth[c.Id] - m)) / w);
    }

    /// Подставной чемпион: n соперников с долями, истинные «выше ожидаемого» из
    /// N(0, trueSd), исходы — по биномиальному закону на своём числе игр
    /// (нормальным приближением: чемпионов в замере сотни).
    private static (List<BlindMath.Cell> Cells, Dictionary<int, double> Truth) Synthetic(
        Random rng, double trueSd, int n, int gamesLo, int gamesHi)
    {
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        var shares = Enumerable.Range(0, n).Select(_ => -Math.Log(1 - rng.NextDouble())).ToList();
        var sum = shares.Sum();
        var cells = new List<BlindMath.Cell>();
        var truth = new Dictionary<int, double>();
        for (int i = 0; i < n; i++)
        {
            var t = trueSd * Gauss();
            var g = rng.Next(gamesLo, gamesHi + 1);
            var p = 0.5 + t / 100;
            var w = Math.Clamp(Math.Round(g * p + Math.Sqrt(g * p * (1 - p)) * Gauss()), 0, g);
            truth[i + 1] = t;
            cells.Add(new BlindMath.Cell(i + 1, shares[i] / sum, (w / g - 0.5) * 100,
                                         BlindMath.Noise2(0.5, g), g));
        }
        return (cells, truth);
    }

    // ── Живая база ───────────────────────────────────────────────────────────

    private static void LiveChecks()
    {
        Console.WriteLine();
        Console.WriteLine("── подбор на живой базе ──");
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  базы нет — подбор не проверяем"); return; }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        var none = new HashSet<int>();

        // ── Ари и Ясуо: надёжная вслепую и та, кого легко законтрить ────────
        var pa = engine.BlindProfileOf(Ahri, "mid", "mid");
        var py = engine.BlindProfileOf(Yasuo, "mid", "mid");
        var ra = engine.Blind(Ahri, "mid", "mid", none);
        var ry = engine.Blind(Yasuo, "mid", "mid", none);
        Check("мид: истинный разброс у Ари меньше, чем у Ясуо", pa.True < py.True,
              $"{pa.True:F2} против {py.True:F2} пп");
        Check("мид: шанс нарваться на контру у Ари ниже", ra.Chance < ry.Chance,
              $"{100 * ra.Chance:F0}% против {100 * ry.Chance:F0}%");
        Check("мид: вслепую Ари выше Ясуо", ra.Value > ry.Value, $"{ra.Value:+0.00;-0.00} против {ry.Value:+0.00;-0.00}");

        // ── Центр: типичный чемпион роли вслепую ни в плюсе, ни в минусе ─────
        foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
        {
            var picks = engine.FieldShares(role);
            var mean = picks.Sum(p => p.Share * engine.Blind(p.Id, role, role, none).Value);
            Check($"{role}: вслепую в среднем по пикрейту — ноль", Math.Abs(mean) < 0.01, $"{mean:+0.000;-0.000}");
        }

        // ── «Топ на острове»: топу важна только своя линия ──────────────────
        {
            var picks = engine.FieldShares("top");
            double Avg(string vs) => picks.Sum(p => p.Share * engine.BlindProfileOf(p.Id, "top", vs).True);
            var own = Avg("top");
            var others = new[] { "jungle", "mid", "adc", "support" }.ToDictionary(r => r, Avg);
            Check("топ: разброс против своей линии вдвое больше любой другой",
                  others.Values.All(v => own > 2 * v),
                  $"топ {own:F2}; " + string.Join(", ", others.Select(kv => $"{kv.Key} {kv.Value:F2}")));
        }

        // ── Этап 1: первый пик, врагов не видно ─────────────────────────────
        var first = engine.Recommend(Draft("middle"), 200);
        Check("первый пик: все карточки вслепую", first.Count > 0 && first.All(r => r.Blind),
              $"{first.Count(r => r.Blind)} из {first.Count}");
        var two = engine.Recommend(Draft("middle"), 2, [Ahri, Yasuo]);
        var ah = two.First(r => r.ChampionId == Ahri);
        var ya = two.First(r => r.ChampionId == Yasuo);
        Check("первый пик: «против оппонента» = ожидаемое вслепую",
              Math.Abs(ah.DirectDelta - ra.Value) < 1e-9 && Math.Abs(ya.DirectDelta - ry.Value) < 1e-9,
              $"Ари {ah.DirectDelta:+0.00;-0.00}, Ясуо {ya.DirectDelta:+0.00;-0.00}");
        Check("первый пик: полоска подписана «вслепую»",
              MetricRow.LabelOf("metric.direct", ah) == Loc.T("metric.blind"), MetricRow.LabelOf("metric.direct", ah));
        var safe  = first.Where(r => r.Reasons.Any(x => x.Contains("Надёжен вслепую"))).ToList();
        var risky = first.Where(r => r.Reasons.Any(x => x.Contains("Рискован вслепую"))).ToList();
        Check("первый пик: есть и «надёжен», и «рискован»", safe.Count > 0 && risky.Count > 0,
              $"надёжных {safe.Count}, рискованных {risky.Count} из {first.Count}");
        Check("ни у кого обеих строк сразу", !safe.Intersect(risky).Any(), "");
        // Словами карточки: Ари — надёжна, Ясуо — рискован.
        Check("в карточке Ари «надёжна вслепую»", safe.Any(r => r.ChampionId == Ahri),
              string.Join(" | ", first.First(r => r.ChampionId == Ahri).Reasons.Select(x => x.TrimStart('', '', ''))));
        Check("в карточке Ясуо «рискован вслепую» с названными контрами",
              risky.Any(r => r.ChampionId == Yasuo && r.Reasons.Any(x => x.Contains("Рискован вслепую") && x.Contains("("))),
              string.Join(" | ", first.First(r => r.ChampionId == Yasuo).Reasons));
        Print(engine, "mid", first);
        foreach (var (role, pos) in new[] { ("top", "top"), ("jungle", "jungle"), ("adc", "bottom"), ("support", "utility") })
            Print(engine, role, engine.Recommend(Draft(pos), 200));

        // ── Пул игрока: карточки пула считаются тем же расчётом ─────────────
        // Владелец (04.11): «расчёты должны применяться как для обычного
        // подбора, так и для пулов игрока». Карточки пула — TopFromPool; в пул
        // нарочно положен чемпион, которого нет в поле мида (Зерат с саппорта
        // — 101 играют и там, но возьмём заведомо редкого на миде: Леону, 89).
        {
            const int Leona = 89;
            var pool = new[] { Ahri, Yasuo, Leona };
            var cards = engine.Recommend(Draft("middle"), pool.Length, pool);
            var top   = engine.TopFromPool(Draft("middle"), pool, max: 3);
            Check("пул: все карточки вслепую", cards.Count == pool.Length && cards.All(r => r.Blind),
                  string.Join(", ", cards.Select(r => $"{DataDragon.Name(r.ChampionId)} {r.DirectDelta:+0.00;-0.00}")));
            Check("пул: у Ари и Ясуо то же ожидаемое, что в общем подборе",
                  Math.Abs(cards.First(r => r.ChampionId == Ahri).DirectDelta - ra.Value) < 1e-9
                  && Math.Abs(cards.First(r => r.ChampionId == Yasuo).DirectDelta - ry.Value) < 1e-9, "");
            var leona = cards.First(r => r.ChampionId == Leona);
            Check("пул: чемпион вне поля роли тоже посчитан (не ноль)", Math.Abs(leona.DirectDelta) > 1e-6,
                  $"{DataDragon.Name(Leona)} {leona.DirectDelta:+0.00;-0.00}");
            Check("пул: карточки пула (TopFromPool) — те же, вслепую",
                  top.Count > 0 && top.All(r => r.Blind
                      && Math.Abs(r.DirectDelta - cards.First(c => c.ChampionId == r.ChampionId).DirectDelta) < 1e-9),
                  string.Join(", ", top.Select(r => DataDragon.Name(r.ChampionId))));
        }

        // ── Пул вне подбора: строки «мои чемпионы» в фазе объявления ────────
        // Тот же расчёт через BlindFor (кто недоступен — как в подборе) и тот
        // же вывод, что у строки в карточке: «надёжен» / «рискован».
        {
            var st = Draft("middle", bans: [ry.Counters.FirstOrDefault()]);
            var viaDraft = engine.BlindFor(st, Yasuo, "mid");
            var direct   = engine.Blind(Yasuo, "mid", "mid", new HashSet<int> { ry.Counters.FirstOrDefault() });
            Check("строка пула: BlindFor считает недоступных как подбор",
                  Math.Abs(viaDraft.Value - direct.Value) < 1e-12 && Math.Abs(viaDraft.Chance - direct.Chance) < 1e-12,
                  $"{viaDraft.Value:+0.00;-0.00}, шанс {100 * viaDraft.Chance:F0}%");
            Check("вывод: Ари надёжна (+1), Ясуо рискован (−1)", ra.Verdict == 1 && ry.Verdict == -1,
                  $"Ари {ra.Verdict}, Ясуо {ry.Verdict}");
            Check("вывод совпадает со строкой карточки",
                  first.All(r => (engine.Blind(r.ChampionId, "mid", "mid", none).Verdict switch
                  {
                      1  => r.Reasons.Any(x => x.Contains("Надёжен вслепую")),
                      -1 => r.Reasons.Any(x => x.Contains("Рискован вслепую")),
                      _  => !r.Reasons.Any(x => x.Contains("вслепую")),
                  })), "");
        }

        // ── Этап 2: часть врагов видна, мид ещё нет ─────────────────────────
        // Главная контра Ясуо взята врагом на топ, вторая забанена. Ясуо от
        // этого надёжнее: из пула соперника ушли лучшие ответы.
        if (ry.Counters.Count >= 2)
        {
            var c0 = ry.Counters[0]; var c1 = ry.Counters[1];
            var st = Draft("middle", enemies: [new DraftPlayer(5, c0, 0, "top", false)], bans: [c1]);
            var y2 = engine.Recommend(st, 1, [Yasuo]).First();
            var taken = new HashSet<int> { c0, c1 };
            var ry2 = engine.Blind(Yasuo, "mid", "mid", taken);
            Check("видна часть врагов, мида нет — по-прежнему вслепую", y2.Blind, "");
            Check($"контры Ясуо ({DataDragon.Name(c0)}, {DataDragon.Name(c1)}) заняты — шанс ниже, ожидаемое выше",
                  ry2.Chance < ry.Chance && ry2.Value > ry.Value,
                  $"шанс {100 * ry.Chance:F0}% → {100 * ry2.Chance:F0}%, ожидаемое {ry.Value:+0.00;-0.00} → {ry2.Value:+0.00;-0.00}");
            Check("в карточке — то же ожидаемое", Math.Abs(y2.DirectDelta - ry2.Value) < 1e-9, $"{y2.DirectDelta:+0.00;-0.00}");
        }
        else Check("у Ясуо нашлись две контры", false, string.Join(", ", ry.Counters));

        // ── Последний пик: соперник ответить не успеет — не вслепую ─────────
        // Никто из врагов не пикает после меня — к моему ходу оппонент будет
        // известен. Пугать контрами незачем, и вес пула в банах это не сдвигает.
        {
            var last = Draft("middle") with { ExposedToCounter = false };
            var y4 = engine.Recommend(last, 1, [Yasuo]).First();
            Check("последний пик (враги пикают раньше) — не вслепую",
                  !y4.Blind && y4.DirectDelta == 0 && !y4.Reasons.Any(x => x.Contains("вслепую")),
                  $"против оппонента {y4.DirectDelta:+0.00;-0.00}");
        }

        // ── Соперник известен — вслепую не считается ────────────────────────
        {
            var st = Draft("middle", enemies: [new DraftPlayer(5, Ahri, 0, "middle", false)]);
            var y3 = engine.Recommend(st, 1, [Yasuo]).First();
            Check("мид врага известен — не вслепую", !y3.Blind && !y3.Reasons.Any(x => x.Contains("вслепую")),
                  $"против оппонента {y3.DirectDelta:+0.00;-0.00}");
        }

        // ── Бот: свой саппорт врага виден, их АДК ещё нет ───────────────────
        // «Против оппонента» на боте — вся пара: матчап с их саппортом плюс
        // ожидаемое против ещё не выбранного АДК.
        {
            var st = Draft("utility", enemies: [new DraftPlayer(5, Thresh, 0, "utility", false)]);
            var sup = engine.Recommend(st, 200);
            var taken = new HashSet<int> { Thresh };
            var bad = sup.Where(r => Math.Abs(r.DirectDelta
                          - (engine.VersusDelta(r.ChampionId, "support", Thresh, "support")
                             + engine.Blind(r.ChampionId, "support", "adc", taken).Value)) > 1e-6).ToList();
            Check("саппорт: их АДК не выбран — в «против оппонента» матчап + ожидаемое против АДК",
                  sup.Count > 0 && bad.Count == 0, $"расходится у {bad.Count} из {sup.Count}");
            Check("саппорт: соперник по линии известен — полоска не «вслепую»", sup.All(r => !r.Blind), "");
            var empty = sup.SelectMany(r => r.Reasons).Where(x => x.Contains("()") || x.Contains("( )")).ToList();
            Check("саппорт: нет строк про бот врага с пустым именем", empty.Count == 0, string.Join(" | ", empty.Take(2)));
        }
    }

    // Самые надёжные и самые рискованные вслепую — чтобы глянуть глазами.
    private static void Print(RecommendationEngine engine, string role, IReadOnlyList<Recommendation> recs)
    {
        var none = new HashSet<int>();
        var rows = recs.Select(r => (r, b: engine.Blind(r.ChampionId, role, role, none)))
                       .OrderByDescending(x => x.b.Value).ToList();
        Console.WriteLine($"  {role}: надёжные вслепую сверху, рискованные снизу (ожидаемое, шанс контры, разброс); типичный шанс {100 * rows[0].b.Typical:F0}%");
        foreach (var (r, b) in rows.Take(5).Concat(rows.TakeLast(5)))
            Console.WriteLine($"     {DataDragon.Name(r.ChampionId),-14} {b.Value,6:+0.00;-0.00}  {100 * b.Chance,3:F0}%  {b.Spread:F2}  "
                              + string.Join(", ", b.Counters.Select(DataDragon.Name)));
    }

    private static DraftState Draft(string pos, List<DraftPlayer>? enemies = null, List<int>? bans = null)
    {
        var me = new DraftPlayer(0, 0, 0, pos, true);
        var their = enemies ?? [];
        return new DraftState(
            MyTeam: [me], TheirTeam: their, MyTeamBans: [], TheirTeamBans: bans ?? [],
            Me: me, MyPosition: pos, DirectOpponent: their.FirstOrDefault(p => p.Position == pos),
            ExposedToCounter: true, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: пик вслепую считается и доходит до подбора и пула"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
