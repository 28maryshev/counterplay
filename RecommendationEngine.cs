using Microsoft.Data.Sqlite;

namespace Counterplay;

public sealed record Recommendation(
    int    ChampionId,
    double Score,
    double BaseDelta,     // %пп vs 50%
    double DirectDelta,   // %пп матчап vs прямой оппонент
    double OtherDelta,    // %пп средний vs прочие враги
    double SynergyDelta,  // %пп средняя синергия с союзниками
    string[] Reasons);

public sealed class RecommendationEngine : IDisposable
{
    // Лаплас-сглаживание: при малом числе игр тянем к 50%.
    private const double K     = 50.0;
    private const double PRIOR = 0.5;

    private const double W_BASE    = 1.0;
    private const double W_DIRECT  = 2.5;
    private const double W_OTHER   = 0.8;
    private const double W_SYNERGY = 1.2;

    // Минимум игр на роли суммарно по всем агрегируемым патчам.
    private const int MIN_GAMES = 30;

    // Количество патчей для агрегации (берём последние N).
    private const int PATCH_WINDOW = 3;

    private readonly SqliteConnection _db;
    private readonly string _p1, _p2, _p3; // последние 3 патча (могут совпадать если патчей меньше)

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
        patchCmd.Parameters.AddWithValue("@n", PATCH_WINDOW);
        var patches = new List<string>();
        using (var rd = patchCmd.ExecuteReader())
            while (rd.Read()) patches.Add(rd.GetString(0));

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

        return new RecommendationEngine(db, effectiveBucket, [.. patches]);
    }

    // Ищет data.db рядом с exe, потом в pipeline/.
    public static string? FindDb()
    {
        var candidates = new[]
        {
            "data.db",
            Path.Combine("pipeline", "data.db"),
            Path.Combine(AppContext.BaseDirectory, "data.db"),
            Path.Combine(AppContext.BaseDirectory, "pipeline", "data.db"),
            @"C:\Counterplay\pipeline\data.db",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    // LCU position → ключ роли в БД
    public static string LcuToDbRole(string pos) => pos.ToLowerInvariant() switch
    {
        "top"     => "top",
        "jungle"  => "jungle",
        "middle"  => "mid",
        "bottom"  => "adc",
        "utility" => "support",
        _         => pos
    };

    public IReadOnlyList<Recommendation> Recommend(DraftState state)
    {
        var myRole = LcuToDbRole(state.MyPosition);
        if (string.IsNullOrEmpty(myRole)) return [];

        var candidates = GetCandidates(myRole);
        Console.WriteLine($"  [диаг] role={myRole}  tier={TierBucket}  патчи={PatchDisplay}  кандидатов={candidates.Count}");

        var taken = new HashSet<int>();
        foreach (var p in state.MyTeam.Concat(state.TheirTeam))
            if (p.ChampionId != 0) taken.Add(p.ChampionId);
        foreach (var b in state.MyTeamBans.Concat(state.TheirTeamBans))
            if (b != 0) taken.Add(b);

        var directOppId   = state.DirectOpponent?.ChampionId ?? 0;
        var otherEnemyIds = state.TheirTeam
            .Where(p => p.ChampionId != 0 && p.ChampionId != directOppId)
            .Select(p => p.ChampionId).ToList();
        var allyData = state.MyTeam
            .Where(p => p.ChampionId != 0 && !p.IsLocalPlayer)
            .Select(p => (Id: p.ChampionId, Role: LcuToDbRole(p.Position))).ToList();

        return candidates
            .Where(id => !taken.Contains(id))
            .Select(champId =>
            {
                var baseDelta   = (SmoothedWr(champId, myRole) - PRIOR) * 100;
                var directDelta = directOppId != 0
                    ? (SmoothedMatchup(champId, myRole, directOppId) - PRIOR) * 100 : 0.0;
                var otherDelta = otherEnemyIds.Count > 0
                    ? otherEnemyIds.Average(e => (SmoothedMatchup(champId, myRole, e) - PRIOR) * 100) : 0.0;
                var synDelta = allyData.Count > 0
                    ? allyData.Average(a => (SmoothedSynergy(champId, myRole, a.Id, a.Role) - PRIOR) * 100) : 0.0;

                var score   = W_BASE * baseDelta + W_DIRECT * directDelta + W_OTHER * otherDelta + W_SYNERGY * synDelta;
                var reasons = BuildReasons(champId, directDelta, directOppId, synDelta, allyData, otherDelta, otherEnemyIds.Count, baseDelta);
                return new Recommendation(champId, score, baseDelta, directDelta, otherDelta, synDelta, reasons);
            })
            .OrderByDescending(r => r.Score)
            .Take(4)
            .ToList();
    }

    // ---------- Запросы к БД — агрегируют по 3 патчам ----------

    private List<int> GetCandidates(string role)
    {
        var cmd = _db.CreateCommand();
        // Суммируем игры по всем 3 патчам — кандидат проходит если набрал MIN_GAMES суммарно.
        cmd.CommandText = @"
            SELECT champion_id FROM base_wr
            WHERE role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)
            GROUP BY champion_id
            HAVING SUM(games) >= @min";
        cmd.Parameters.AddWithValue("@r",   role);
        cmd.Parameters.AddWithValue("@t",   TierBucket);
        cmd.Parameters.AddWithValue("@p1",  _p1);
        cmd.Parameters.AddWithValue("@p2",  _p2);
        cmd.Parameters.AddWithValue("@p3",  _p3);
        cmd.Parameters.AddWithValue("@min", MIN_GAMES);
        var list = new List<int>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(rd.GetInt32(0));
        return list;
    }

    private double SmoothedWr(int champId, string role)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM base_wr
            WHERE champion_id=@c AND role=@r AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return SmoothedAgg(cmd);
    }

    private double SmoothedMatchup(int champId, string role, int vsId)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM matchup
            WHERE champion_id=@c AND role=@r AND vs_champion_id=@v
              AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@v",  vsId);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return SmoothedAgg(cmd);
    }

    private double SmoothedSynergy(int champId, string role, int allyId, string allyRole)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(SUM(games),0), COALESCE(SUM(wins),0) FROM synergy
            WHERE champion_id=@c AND role=@r AND ally_id=@a AND ally_role=@ar
              AND tier_bucket=@t AND patch IN (@p1,@p2,@p3)";
        cmd.Parameters.AddWithValue("@c",  champId);
        cmd.Parameters.AddWithValue("@r",  role);
        cmd.Parameters.AddWithValue("@a",  allyId);
        cmd.Parameters.AddWithValue("@ar", allyRole);
        cmd.Parameters.AddWithValue("@t",  TierBucket);
        cmd.Parameters.AddWithValue("@p1", _p1);
        cmd.Parameters.AddWithValue("@p2", _p2);
        cmd.Parameters.AddWithValue("@p3", _p3);
        return SmoothedAgg(cmd);
    }

    private static double SmoothedAgg(SqliteCommand cmd)
    {
        using var rd = cmd.ExecuteReader();
        if (!rd.Read()) return PRIOR;
        var games = rd.GetDouble(0);
        var wins  = rd.GetDouble(1);
        return (wins + K / 2.0) / (games + K); // Лаплас
    }

    // ---------- Обоснование ----------

    private static string[] BuildReasons(
        int    champId,
        double directDelta, int    directOppId,
        double synDelta,    IReadOnlyList<(int Id, string Role)> allies,
        double otherDelta,  int    otherCount,
        double baseDelta)
    {
        var lines = new List<string>();

        // Семантические метки синергии с союзниками (теги чемпионов)
        var tagLabels = ChampionTags.DetectSynergies(champId, allies);
        lines.AddRange(tagLabels);

        // Матчап с прямым оппонентом
        if (directOppId != 0)
        {
            var oppName = DataDragon.Name(directOppId);
            if      (directDelta >=  2.0) lines.Add($"Выигрывает линию против {oppName} (+{directDelta:F1}%)");
            else if (directDelta >=  0.5) lines.Add($"Небольшое преимущество против {oppName} (+{directDelta:F1}%)");
            else if (directDelta <= -2.0) lines.Add($"Сложный матчап vs {oppName} ({directDelta:F1}%) — компенсируется составом");
            else                          lines.Add($"Нейтральный матчап против {oppName}");
        }

        // Статистическая синергия (если не покрыта тегами)
        if (tagLabels.Count == 0 && allies.Count > 0)
        {
            if      (synDelta >= 1.5) lines.Add($"Хорошая синергия с союзниками (+{synDelta:F1}%)");
            else if (synDelta >  0.3) lines.Add($"Комфортно работает с командой (+{synDelta:F1}%)");
        }

        if (otherCount > 0 && otherDelta >= 1.5)
            lines.Add($"Выгодные матчапы против состава врагов (+{otherDelta:F1}%)");

        if      (baseDelta >=  1.5) lines.Add($"Один из сильнейших в патче — WR {50 + baseDelta:F1}%");
        else if (baseDelta >=  0.5) lines.Add($"Хороший базовый WR {50 + baseDelta:F1}%");
        else if (baseDelta <= -1.5) lines.Add($"Низкий WR {50 + baseDelta:F1}% — нужно хорошо знать чемпиона");

        if (lines.Count == 0) lines.Add($"Нейтральный пик, WR ≈{50 + baseDelta:F1}%");
        return [.. lines];
    }

    public void Dispose() => _db.Dispose();
}
