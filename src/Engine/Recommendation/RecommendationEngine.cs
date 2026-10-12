using Microsoft.Data.Sqlite;

namespace Counterplay;

// Движок подбора — каркас: открытие базы, выбор бакета и патчей, готовность данных.
// Подбор, баны, тир-лист, веса и чтение таблиц — в соседних RecommendationEngine.*.cs.
public sealed partial class RecommendationEngine : IDisposable
{
    private readonly SqliteConnection _db;

    private readonly string _p1, _p2, _p3; // последние 3 патча (могут совпадать если патчей меньше)

    private readonly bool _botlaneReady;   // достаточно ли бот-данных, чтобы их учитывать

    public string TierBucket  { get; }

    public string PatchDisplay { get; } // "16.12, 16.11, 16.10" для вывода

    private RecommendationEngine(SqliteConnection db, string tierBucket, string[] patches)
    {
        _db          = db;
        TierBucket   = tierBucket;
        PatchDisplay = string.Join(", ", patches);
        // Заполняем до 3 слотов: если патчей меньше — дублируем последний (IN без эффекта)
        _p1 = patches.Length > 0 ? patches[0] : "0.0";
        _p2 = patches.Length > 1 ? patches[1] : _p1;
        _p3 = patches.Length > 2 ? patches[2] : _p2;

        // Бот-матчапы (#4) включаем только когда накоплено достаточно бот-данных.
        // Считаем по объёму записей botlane_matchup, а НЕ по общему числу матчей:
        // старые матчи бот-статистику не содержат, их учитывать нельзя.
        _botlaneReady = BotlaneTotalGames(db) >= BOTLANE_MIN_GAMES;
    }

    // Сумма игр в botlane_matchup (0, если таблицы нет — старая база).
    private static double BotlaneTotalGames(SqliteConnection db)
    {
        try
        {
            var cmd = db.CreateCommand();
            // Сводные строки пропускаем: это та же статистика второй раз.
            cmd.CommandText = "SELECT COALESCE(SUM(games),0) FROM botlane_matchup WHERE patch <> 'all'";
            var r = cmd.ExecuteScalar();
            return r is null or DBNull ? 0 : Convert.ToDouble(r);
        }
        catch { return 0; }
    }

    // Порядок фолбэка: если в базе нет данных для нужного бакета, берём ближайший.
    private static readonly string[] BucketFallback = ["silver", "gold", "emerald", "master"];

    public static RecommendationEngine Create(string dbPath, string tierBucket)
    {
        // Не используем Mode=ReadOnly — WAL-режим требует доступа к -shm файлу даже для чтения.
        var db = new SqliteConnection($"Data Source={dbPath}");
        db.Open();

        // Берём последние PATCH_WINDOW патчей (корректная версионная сортировка).
        var patchCmd = db.CreateCommand();
        patchCmd.CommandText = @"
            SELECT DISTINCT patch FROM base_wr
            ORDER BY CAST(SUBSTR(patch, 1, INSTR(patch, '.') - 1) AS INTEGER) DESC,
                     CAST(SUBSTR(patch, INSTR(patch, '.') + 1)     AS INTEGER) DESC
            LIMIT @n";
        // +1: берём с запасом, чтобы после возможного удержания новейшего осталось PATCH_WINDOW.
        patchCmd.Parameters.AddWithValue("@n", PATCH_WINDOW + 1);
        var patches = new List<string>();
        using (var rd = patchCmd.ExecuteReader())
            while (rd.Read()) patches.Add(rd.GetString(0));

        // Удержание патча: новейший патч не берём, пока он не набрал данных — иначе
        // первые дни нового патча рекомендации шумят. Держим предыдущий (полный)
        // патч, пока новый не покроет >= HOLD_FRACTION пар. Синхронно с
        // pipeline/freshness.py и ботом (bot/lib/freshness.js).
        // Бакет, в котором можно мерить готовность патча. Побакетная база содержит
        // ТОЛЬКО свой бакет, а проверка раньше всегда смотрела в «emerald» — и у
        // всех, кроме изумруда, читала нули. Ноль у ПРОШЛОГО патча означает «делить
        // не на что», то есть «новый готов», и удержание молча выключалось: движок
        // брал только что вышедший патч, где данных ещё нет. У изумруда всё
        // работало, поэтому беда всплыла, когда человек перешёл в золото.
        var holdBucket = BucketWithData(db, tierBucket);
        if (patches.Count >= 2 && !PatchReady(db, patches[0], patches[1], holdBucket))
            patches.RemoveAt(0);
        if (patches.Count > PATCH_WINDOW) patches = patches.Take(PATCH_WINDOW).ToList();
        if (patches.Count == 0) patches.Add("0.0");

        // Проверяем бакет по последнему патчу; если нет данных — ищем ближайший.
        var effectiveBucket = tierBucket;
        var checkCmd = db.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM base_wr WHERE tier_bucket=@t AND patch=@p";
        checkCmd.Parameters.AddWithValue("@t", tierBucket);
        checkCmd.Parameters.AddWithValue("@p", patches[0]);
        var count = (long)(checkCmd.ExecuteScalar() ?? 0L);

        if (count == 0)
        {
            foreach (var b in BucketFallback)
            {
                checkCmd.Parameters["@t"].Value = b;
                var n = (long)(checkCmd.ExecuteScalar() ?? 0L);
                if (n > 0) { effectiveBucket = b; break; }
            }
            Console.WriteLine($"  [предупреждение] Нет данных для бакета '{tierBucket}' — использую '{effectiveBucket}'.");
        }

        var engine = new RecommendationEngine(db, effectiveBucket, [.. patches]);
        Log.Write($"движок: бакет {effectiveBucket}, патчи {engine.PatchDisplay}, " +
                  $"сводка по дивизионам {(engine.HasPool ? "есть" : "нет")}, " +
                  $"готовность мерили по «{holdBucket}»");
        return engine;
    }

    // ── Политика удержания патча (синхронно с pipeline/freshness.py, bot/lib/freshness.js) ──
    private const int HOLD_MIN_GAMES = 150;      // игр на «чемпион+роль», чтобы считать пару набранной

    private const double HOLD_FRACTION = 0.7;    // доля пар прошлого патча, покрытых новым → «готов»

    private const string HOLD_BUCKET = "emerald"; // основной бакет для оценки готовности

    // Сколько «чемпион+роль» в заданном бакете набрали >= HOLD_MIN_GAMES игр на патче.
    private static int PatchCoverage(SqliteConnection db, string patch, string bucket)
    {
        var c = db.CreateCommand();
        c.CommandText = @"SELECT COUNT(*) FROM (
            SELECT champion_id, role FROM base_wr
            WHERE patch=@p AND tier_bucket=@b
            GROUP BY champion_id, role HAVING SUM(games) >= @min)";
        c.Parameters.AddWithValue("@p", patch);
        c.Parameters.AddWithValue("@b", bucket);
        c.Parameters.AddWithValue("@min", HOLD_MIN_GAMES);
        return (int)(long)(c.ExecuteScalar() ?? 0L);
    }

    /// <summary>
    /// Бакет, по которому МОЖНО судить о готовности патча в этом файле. Сперва
    /// свой, затем любой из запасных, затем основной. Общая база содержит все
    /// бакеты, побакетная — один: мерить надо тот, который есть.
    /// </summary>
    private static string BucketWithData(SqliteConnection db, string wanted)
    {
        var c = db.CreateCommand();
        c.CommandText = "SELECT COUNT(*) FROM base_wr WHERE tier_bucket=@b LIMIT 1";
        c.Parameters.AddWithValue("@b", wanted);
        foreach (var b in new[] { wanted }.Concat(BucketFallback).Append(HOLD_BUCKET))
        {
            c.Parameters["@b"].Value = b;
            if ((long)(c.ExecuteScalar() ?? 0L) > 0) return b;
        }
        return HOLD_BUCKET;
    }

    // Готов ли новейший патч: покрывает ли >= HOLD_FRACTION пар предыдущего патча.
    private static bool PatchReady(SqliteConnection db, string newest, string prev, string bucket)
    {
        int cn = PatchCoverage(db, newest, bucket), cp = PatchCoverage(db, prev, bucket);
        return cp <= 0 || (double)cn / cp >= HOLD_FRACTION;
    }

    // Ищет data.db рядом с exe, потом в pipeline/.
    // Пропускает пустые/неинициализированные файлы (без таблицы base_wr) —
    // иначе пустой data.db в рабочей папке перекрывал бы настоящую базу.
    public static string? FindDb()
    {
        // Скачанная база в профиле пользователя (её ведёт DataDb.EnsureAsync) —
        // тем же путём, что у DataDb: у базы свой шов, у данных игрока свой.
        var downloaded = DataDb.LocalPath;

        // По умолчанию — скачанная база (как у всех). Dev-базы рядом с проектом
        // берём в приоритет ТОЛЬКО в dev-режиме (COUNTERPLAY_DEV_DB=1), иначе они
        // идут в хвост — чтобы у разработчика не «побеждала» устаревшая локальная.
        var order = DataDb.DevDbEnabled
            ? DataDb.DevCandidates.Append(downloaded)
            : new[] { downloaded }.Concat(DataDb.DevCandidates);
        return order.FirstOrDefault(p => File.Exists(p) && HasData(p));
    }

    // Валиден ли файл БД: ненулевой размер и есть таблица base_wr с данными.
    public static bool HasData(string path)
    {
        try
        {
            if (new FileInfo(path).Length == 0) return false;
            using var db = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            db.Open();
            var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='base_wr')";
            return (long)(cmd.ExecuteScalar() ?? 0L) == 1;
        }
        catch { return false; }
    }

    /// Патч, по которому считаются тир-листы и бан-рейт (с учётом удержания).
    public string Patch => _p1;

    public void Dispose() => _db.Dispose();
}
