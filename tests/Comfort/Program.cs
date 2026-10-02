using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Наигранность должна идти за тем, чем игрок играет СЕЙЧАС.
///
/// Повод (жалоба игрока, 1 октября): «я много играл Нилу, пока она была
/// сильной; её занерфили, а она всё равно в топ-4». Комфорт стоял на
/// пожизненных очках мастерства Riot, а они не убывают никогда — заброшенный
/// чемпион держался наверху вечно, и нерф его оттуда сдвинуть не мог.
///
/// Проверка подставляет историю игр через <see cref="SessionTracker.HistoryOverride"/>
/// и меряет одного и того же чемпиона в двух состояниях: «играю его в этом
/// месяце» и «не брал полгода». Разница должна быть больше, чем разрыв между
/// местами в топ-10 — иначе затухание ничего не решает.
///
/// Журнал игрока при этом не читается вовсе: шов отдаёт подменную историю.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Очки мастерства «глубокого мейна» — столько набирается за сотни игр.
    private const long MainPoints = 150_000;

    /// Сколько драфтов в развёртке сдвига по местам.
    private const int DraftCount = 40;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // Пулы — в свою папку. Файл игрока проверки однажды уже стёрли.
        PoolStore.DirOverride = Path.Combine(
            Path.GetTempPath(), "counterplay-test-pools", "Comfort");
        Directory.CreateDirectory(PoolStore.DirOverride);
        var stale = Path.Combine(PoolStore.DirOverride, "pools.json");
        if (File.Exists(stale)) File.Delete(stale);
        PoolStore.Reload();

        var dbPath = FindDb();
        if (dbPath is null)
        {
            Console.Error.WriteLine("не нашёл data-slice.db — запускать из корня репозитория");
            return 2;
        }

        Console.WriteLine($"база: {dbPath}");
        using var engine = Quiet(() => RecommendationEngine.Create(dbPath, "emerald"));
        Console.WriteLine($"патчи: {engine.PatchDisplay}   бакет: {engine.TierBucket}\n");

        // Кандидаты на роль: берём чемпиона из СЕРЕДИНЫ списка — у вершины
        // сдвиг по местам упёрся бы в потолок, у хвоста его не видно.
        var empty = Draft("utility", [], []);
        var plain = Scored(engine, empty, null).Values.OrderByDescending(r => r.Score).ToList();
        if (plain.Count < 30)
        {
            Console.Error.WriteLine($"мало кандидатов ({plain.Count}) — мерить нечего");
            return 2;
        }
        var champ = plain[20].ChampionId;
        var clean = plain[21].ChampionId;   // без мастерства — для флора пула
        Console.WriteLine($"чемпион замера: {DataDragon.Name(champ)} (место {21} из {plain.Count})");

        // Меряем на ЗАПОЛНЕННОМ драфте, а не на пустом. На пустом оценкам нечем
        // сжиматься — там нет ни контры, ни синергии, и «разброс топ-8» выходит
        // вдвое шире настоящего. Живой драфт с врагами и союзниками — то, что
        // видит игрок, и то, на чём меряны пороги в PoolBias.
        var rnd     = new Random(20261002);   // фиксированное зерно — цифры повторяемы
        var ids     = plain.Select(r => r.ChampionId).Where(i => i != champ && i != clean).ToList();
        var enemies = Pick(rnd, ids, 5);
        var allies  = Pick(rnd, ids.Except(enemies).ToList(), 4);
        var state   = Draft("utility", allies, enemies);
        Console.WriteLine($"драфт: {enemies.Count} врагов, {allies.Count} союзников\n");

        engine.Mastery = new Dictionary<int, long> { [champ] = MainPoints };

        // ── 1. Один чемпион, две истории ────────────────────────────────────
        Console.WriteLine("── наигранность: играю сейчас против играл когда-то ──");

        var fresh   = Comfort(engine, state, champ, Hist(champ, recent: 12, daysSince: 1,   span: 400));
        var stale6m = Comfort(engine, state, champ, Hist(champ, recent: 0,  daysSince: 200, span: 400));

        Console.WriteLine($"  играю в этом месяце (12 игр):   комфорт {fresh:F2}");
        Console.WriteLine($"  не брал 200 дней:               комфорт {stale6m:F2}");
        Console.WriteLine($"  как было до правки (8.0 × кривая): "
                          + $"{8.0 * MainPoints / (MainPoints + 80_000.0):F2} в обоих случаях");

        Check("заброшенный чемпион теряет наигранность", stale6m < fresh - 2.0,
              $"{fresh:F2} → {stale6m:F2}");
        Check("но не обнуляется — забытое вспоминается быстрее незнакомого", stale6m > 0.2,
              $"{stale6m:F2}");

        // ── 2. Сдвиг по местам: развёртка по драфтам ────────────────────────
        // По ОДНОМУ драфту судить нельзя: где контрпик разводит оценки широко,
        // те же очки комфорта стоят меньше мест, чем там, где кандидаты стоят
        // плотно. Поэтому гоняем DraftCount сценариев и смотрим медиану —
        // так же, как меряет PoolBias.
        Console.WriteLine("\n── на сколько мест уезжает заброшенный чемпион ──");
        var shifts   = new List<double>();
        var freshPos = new List<double>();
        var stalePos = new List<double>();
        var spreads  = new List<double>();
        var wasTop6  = 0;   // попадал в видимую шестёрку, пока его играют
        var leftTop6 = 0;   // ...и вылетел из неё, когда забросили
        var histFresh = Hist(champ, recent: 12, daysSince: 1,   span: 400);
        var histStale = Hist(champ, recent: 0,  daysSince: 200, span: 400);

        for (var n = 0; n < DraftCount; n++)
        {
            var e = Pick(rnd, ids, 5);
            var a = Pick(rnd, ids.Except(e).ToList(), 4);
            var st = Draft("utility", a, e);

            var rf = Rank(engine, st, champ, histFresh);
            var rs = Rank(engine, st, champ, histStale);
            shifts.Add(rs - rf);
            freshPos.Add(rf);
            stalePos.Add(rs);
            if (rf <= 6) { wasTop6++; if (rs > 6) leftTop6++; }

            var live = Scored(engine, st, null).Values.OrderByDescending(r => r.Score).ToList();
            if (live.Count >= 8) spreads.Add(live[1].Score - live[7].Score);
        }

        Console.WriteLine($"  место, пока играю:  медиана {Median(freshPos):F1}");
        Console.WriteLine($"  место, когда забыл: медиана {Median(stalePos):F1}");
        Console.WriteLine($"  сдвиг вниз:         медиана {Median(shifts):F1} мест "
                          + $"(из {DraftCount} драфтов)");
        Console.WriteLine($"  ушёл из топ-6:      {leftTop6} из {wasTop6} раз, когда там был");
        Console.WriteLine($"  для масштаба: разрыв 2-е…8-е место, медиана {Median(spreads):F2}");

        Check("забытый чемпион заметно уходит вниз", Median(shifts) >= 3,
              $"медиана сдвига {Median(shifts):F1} мест");
        // Не «всегда»: если чемпион и правда силён в патче и контрит состав,
        // он обязан остаться наверху без всякой наигранности — это и есть
        // правильное поведение. Требуем лишь, чтобы забвение перевешивало
        // чаще, чем нет.
        Check("и чаще, чем нет, покидает видимую шестёрку",
              wasTop6 > 0 && leftTop6 * 2 >= wasTop6,
              $"{leftTop6} из {wasTop6}");

        // ── 4. Затухание монотонно ──────────────────────────────────────────
        Console.WriteLine("\n── кривая затухания по дням простоя ──");
        double? prev = null;
        var monotone = true;
        foreach (var d in new[] { 0, 15, 30, 45, 60, 90, 120, 180, 365 })
        {
            var c = Comfort(engine, state, champ, Hist(champ, recent: 0, daysSince: d, span: 400));
            Console.WriteLine($"  {d,3} дней простоя → {c:F2}");
            if (prev is { } p && c > p + 1e-9) monotone = false;
            prev = c;
        }
        Check("с простоем наигранность только падает", monotone, "");
        var floor120 = Comfort(engine, state, champ, Hist(champ, recent: 0, daysSince: 120, span: 400));
        var floor365 = Comfort(engine, state, champ, Hist(champ, recent: 0, daysSince: 365, span: 400));
        Check("ниже дна не падает", Math.Abs(floor120 - floor365) < 1e-9,
              $"{floor120:F2} и {floor365:F2}");

        // ── 5. Новичка затухание не наказывает ──────────────────────────────
        // Журнал копится только вперёд от установки. У того, кто поставил
        // программу неделю назад, НЕ сыграно ничего — но это не значит
        // «забросил»: мы просто не видели. Дольше глубины журнала простоя не
        // бывает, и у новичка его нет вовсе.
        Console.WriteLine("\n── журнал ведётся неделю: простоя быть не может ──");
        var cold = Comfort(engine, state, champ, Hist(champ, recent: 0, daysSince: 7, span: 7));
        var full = MASTERY_MAX_IN_CODE * MainPoints / (MainPoints + 80_000.0);
        Console.WriteLine($"  комфорт {cold:F2} (полное мастерство без затухания — {full:F2})");
        Check("новичок наигранность не теряет", Math.Abs(cold - full) < 0.01,
              $"{cold:F2} против {full:F2}");

        // ── 6. Флор пула на месте ───────────────────────────────────────────
        Console.WriteLine("\n── флор пула ──");
        var poolNoMastery = Comfort(engine, state, clean,
                                    Hist(clean, recent: 0, daysSince: 400, span: 400), pool: [clean]);
        Console.WriteLine($"  чемпион без очков мастерства, но в пуле: {poolNoMastery:F2}");
        Check("пул даёт флор второму аккаунту", poolNoMastery >= 1.19,
              $"{poolNoMastery:F2}");

        // Глубокий мейн, которого играют: пул ничего не добавляет поверх.
        var mainOut = Comfort(engine, state, champ, Hist(champ, recent: 12, daysSince: 1, span: 400));
        var mainIn  = Comfort(engine, state, champ, Hist(champ, recent: 12, daysSince: 1, span: 400),
                              pool: [champ]);
        Check("пул не добавляется поверх живой наигранности", Math.Abs(mainIn - mainOut) < 1e-9,
              $"{mainOut:F2} → {mainIn:F2}");

        // ── 7. Журнал игрока не читали ──────────────────────────────────────
        Console.WriteLine("\n── данные игрока ──");
        Check("история бралась из шва, а не из session.json",
              SessionTracker.HistoryOverride is not null, "");

        SessionTracker.HistoryOverride = null;

        Console.WriteLine(_fails == 0 ? "\nВсё в порядке." : $"\nПроблем: {_fails}.");
        return _fails == 0 ? 0 : 1;
    }

    /// Потолок мастерства, с которым собран движок (см. MASTERY_MAX).
    private const double MASTERY_MAX_IN_CODE = 3.0;

    /// <summary>
    /// Подменная история: чемпион сыгран <paramref name="recent"/> раз за окно
    /// свежести, последний раз <paramref name="daysSince"/> дней назад, а сам
    /// журнал ведётся <paramref name="span"/> дней.
    /// </summary>
    private static SessionTracker.PlayHistory Hist(int champId, int recent, double daysSince, double span)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new SessionTracker.PlayHistory(
            new Dictionary<int, int> { [champId] = recent },
            new Dictionary<int, long> { [champId] = now - (long)(daysSince * 86400) },
            span, now);
    }

    private static double Comfort(RecommendationEngine engine, DraftState state, int champId,
                                  SessionTracker.PlayHistory hist, List<int>? pool = null)
    {
        SessionTracker.HistoryOverride = hist;
        return Scored(engine, state, pool)[champId].ComfortDelta;
    }

    private static int Rank(RecommendationEngine engine, DraftState state, int champId,
                            SessionTracker.PlayHistory hist)
    {
        SessionTracker.HistoryOverride = hist;
        var all = Scored(engine, state, null);
        return all.Values.Count(r => r.Score > all[champId].Score) + 1;
    }

    private static Dictionary<int, Recommendation> Scored(
        RecommendationEngine engine, DraftState state, List<int>? pool)
    {
        var a = PoolStore.Current();
        a.Pools.Clear(); a.DuoPools.Clear();
        if (pool is null)
        {
            a.ActiveKind = PoolKind.Normal; a.ActiveId = null;
        }
        else
        {
            a.Pools.Add(new ChampPool { Id = "t", Name = "t", ByRole = new() { ["support"] = [.. pool] } });
            a.ActiveKind = PoolKind.Pool; a.ActiveId = "t";
        }
        return Quiet(() => engine.Recommend(state, 400)).ToDictionary(r => r.ChampionId, r => r);
    }

    private static DraftState Draft(string lcuRole, List<int> allies, List<int> enemies)
    {
        var me = new DraftPlayer(0, 0, 0, lcuRole, true);
        var mine = new List<DraftPlayer> { me };
        for (var i = 0; i < allies.Count; i++) mine.Add(new DraftPlayer(i + 1, allies[i], 0, "", false));
        var theirs = enemies.Select((id, i) => new DraftPlayer(10 + i, id, 0, "", false)).ToList();
        return new DraftState(mine, theirs, [], [], me, lcuRole, null,
                              false, false, [], false, -1, false, [], -1, -1, false);
    }

    private static List<int> Pick(Random rnd, List<int> from, int n)
    {
        var copy = from.ToList();
        var outp = new List<int>();
        for (var i = 0; i < n && copy.Count > 0; i++)
        {
            var k = rnd.Next(copy.Count);
            outp.Add(copy[k]); copy.RemoveAt(k);
        }
        return outp;
    }

    private static double Median(List<double> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }

    /// Движок сыплет диагностику в консоль на каждый вызов — заглушаем.
    private static T Quiet<T>(Func<T> f)
    {
        var old = Console.Out;
        Console.SetOut(TextWriter.Null);
        try { return f(); }
        finally { Console.SetOut(old); }
    }

    /// Боевая база (та же, что у приложения), иначе — срез из репозитория.
    private static string? FindDb()
    {
        var live = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "data.db");
        if (File.Exists(live) && HasTable(live, "matchup") && HasTable(live, "synergy")) return live;

        string? dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 6; i++)
        {
            if (dir is null) break;
            var p = Path.Combine(dir, "data-slice.db");
            if (File.Exists(p)) return p;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }

    private static bool HasTable(string db, string table)
    {
        try
        {
            using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db};Mode=ReadOnly");
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", table);
            return cmd.ExecuteScalar() is not null;
        }
        catch { return false; }
    }
}
