using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Counterplay;

/// <summary>
/// Централизованная база данных. Источник — отдельный «дата-релиз» на GitHub
/// (его обновляют раз в патч). При каждом запуске программа сверяет версию и,
/// если на сервере новее, тихо подкачивает свежую base в папку пользователя.
/// Так данные мета держатся актуальными без действий пользователя.
/// </summary>
public static class DataDb
{
    // Постоянное место БД — вне каталога установки, переживает обновления приложения.
    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
    public static string LocalPath        => Path.Combine(Dir, "data.db");
    private static string LocalVersionPath => Path.Combine(Dir, "data-version.txt");

    // Дата-релиз: ассеты обновляются независимо от релизов приложения.
    private const string BaseUrl    = "https://github.com/28maryshev/counterplay/releases/download/data";
    private const string VersionUrl = BaseUrl + "/data-version.json";
    // Побакетная база (data-<bucket>.db, ~50 МБ) — качаем только свой ранг. Если
    // бакет неизвестен — общая тонкая data.db (все бакеты, ~177 МБ).
    private static string DataUrlFor(string? bucket) =>
        string.IsNullOrEmpty(bucket) ? BaseUrl + "/data.db" : $"{BaseUrl}/data-{bucket}.db";

    // Локальные dev-базы (рядом с проектом). Используются как есть, БЕЗ скачивания,
    // ТОЛЬКО в dev-режиме (см. DevDbEnabled). По умолчанию их игнорируем — база
    // качается из облака, как у всех пользователей.
    public static readonly string[] DevCandidates =
    [
        "data.db",
        Path.Combine("pipeline", "data.db"),
        Path.Combine(AppContext.BaseDirectory, "data.db"),
        Path.Combine(AppContext.BaseDirectory, "pipeline", "data.db"),
        @"C:\Counterplay\pipeline\data.db",
    ];

    /// Использовать локальную dev-базу вместо облачной? По умолчанию НЕТ —
    /// приложение качает базу из релиза, как у обычных пользователей. Включить
    /// dev-режим (для отладки пайплайна на своей базе): переменная окружения
    /// COUNTERPLAY_DEV_DB=1.
    public static bool DevDbEnabled =>
        Environment.GetEnvironmentVariable("COUNTERPLAY_DEV_DB") is "1" or "true";

    // Какой бакет сейчас на диске: "all" (общая), "silver".. или null (базы нет).
    // Хранится префиксом в data-version.txt ("bucket:version").
    public static string? CurrentBucket
    {
        get
        {
            try
            {
                if (!File.Exists(LocalVersionPath)) return null;
                var tag = File.ReadAllText(LocalVersionPath).Trim();
                var i = tag.IndexOf(':');
                return i > 0 ? tag[..i] : "all"; // старый формат без бакета = общая
            }
            catch { return null; }
        }
    }

    // Таблицы движка + число последних патчей, которые держим локально.
    private static readonly string[] EngineTables =
        ["base_wr", "matchup", "synergy", "botlane_matchup", "champion_bans"];
    private const int SelfCleanKeep = 5;

    /// Самоочистка локальной базы: держим только последние SelfCleanKeep патчей
    /// (движку нужно 3). Обычно no-op — скачанная база уже тонкая, — но страхует
    /// от старой раздутой базы, оставшейся от прежней версии программы.
    public static void SelfClean()
    {
        try
        {
            if (!File.Exists(LocalPath) || !RecommendationEngine.HasData(LocalPath)) return;
            using var db = new SqliteConnection($"Data Source={LocalPath}");
            db.Open();

            var patches = new List<string>();
            using (var c = db.CreateCommand())
            {
                c.CommandText = @"SELECT DISTINCT patch FROM base_wr
                    ORDER BY CAST(SUBSTR(patch,1,INSTR(patch,'.')-1) AS INTEGER) DESC,
                             CAST(SUBSTR(patch,INSTR(patch,'.')+1)   AS INTEGER) DESC
                    LIMIT @k";
                c.Parameters.AddWithValue("@k", SelfCleanKeep);
                using var rd = c.ExecuteReader();
                while (rd.Read()) patches.Add(rd.GetString(0));
            }
            if (patches.Count < SelfCleanKeep) return; // старых патчей нет — чистить нечего

            var placeholders = string.Join(",", patches.Select((_, i) => $"@p{i}"));
            long removed = 0;
            foreach (var t in EngineTables)
            {
                using var del = db.CreateCommand();
                del.CommandText = $"DELETE FROM {t} WHERE patch NOT IN ({placeholders})";
                for (int i = 0; i < patches.Count; i++)
                    del.Parameters.AddWithValue($"@p{i}", patches[i]);
                try { removed += del.ExecuteNonQuery(); } catch { /* таблицы может не быть */ }
            }
            if (removed > 0)
                using (var vac = db.CreateCommand()) { vac.CommandText = "VACUUM"; vac.ExecuteNonQuery(); }
        }
        catch { /* очистка необязательна — не мешаем запуску */ }
    }

    public static string FormatSpeed(double bytesPerSec) =>
        bytesPerSec >= 1_000_000 ? $"{bytesPerSec / 1_048_576.0:0.0} МБ/с"
        : bytesPerSec > 0        ? $"{bytesPerSec / 1024.0:0} КБ/с"
        : "";

    /// Гарантирует наличие актуальной базы. Для dev — берёт локальную как есть.
    /// Для установленной версии — сверяет версию с сервером и подкачивает свежую.
    /// progress: (текст со статусом+скоростью, доля 0..1).
    /// bucket — эло игрока (silver/gold/emerald/master) → качаем маленькую базу
    /// только его ранга; null — общая тонкая база (все бакеты). Версию сверяем по
    /// «bucket:version», поэтому смена ранга сама триггерит подкачку нужной базы.
    public static async Task EnsureAsync(string? bucket, Action<string, double>? progress, CancellationToken ct)
    {
        // 1. Dev-режим (по opt-in): локальная база рядом с проектом — не качаем.
        if (DevDbEnabled && DevCandidates.Any(p => File.Exists(p) && RecommendationEngine.HasData(p)))
            return;

        // 2. Установленная версия — версионная подкачка.
        try
        {
            Directory.CreateDirectory(Dir);

            var remoteVer = await FetchVersionAsync(bucket, ct);
            var wantedTag = remoteVer is null ? null : $"{bucket ?? "all"}:{remoteVer}";
            var localTag  = File.Exists(LocalVersionPath) ? File.ReadAllText(LocalVersionPath).Trim() : null;
            var haveDb    = File.Exists(LocalPath) && new FileInfo(LocalPath).Length > 0;

            // База есть и совпадают бакет+версия (или сервер недоступен) — не качаем.
            if (haveDb && (wantedTag is null || wantedTag == localTag)) return;

            var label = haveDb ? Loc.T("status.updatingDb") : Loc.T("status.downloadingDb");
            progress?.Invoke(label, 0);
            await DownloadAsync(DataUrlFor(bucket), label, progress, ct);

            if (wantedTag is not null)
                await File.WriteAllTextAsync(LocalVersionPath, wantedTag, ct);
        }
        catch
        {
            // Офлайн / нет дата-релиза — работаем на том, что уже скачано (если есть).
        }
    }

    // Версия целевой базы: для бакета — из manifest.buckets[bucket].version,
    // иначе общая version. Если побакетной секции нет (старый релиз) — вернём
    // общую и качнём общую data.db (обратная совместимость).
    private static async Task<string?> FetchVersionAsync(string? bucket, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = (await http.GetStringAsync(VersionUrl, ct)).TrimStart('﻿');
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!string.IsNullOrEmpty(bucket)
                && root.TryGetProperty("buckets", out var bs)
                && bs.TryGetProperty(bucket, out var b)
                && b.TryGetProperty("version", out var bv))
                return bv.GetString();
            return root.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task DownloadAsync(string url, string label, Action<string, double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp   = LocalPath + ".tmp";

        var sw = Stopwatch.StartNew();
        long lastBytes = 0;
        var  lastT = TimeSpan.Zero;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;

                var now = sw.Elapsed;
                if ((now - lastT).TotalSeconds >= 0.2 || (total > 0 && read >= total))
                {
                    var dt   = (now - lastT).TotalSeconds;
                    var bps  = dt > 0 ? (read - lastBytes) / dt : 0;
                    var frac = total > 0 ? (double)read / total : 0;
                    lastBytes = read; lastT = now;
                    var pctTxt = total > 0 ? $" {frac * 100:0}%" : "";
                    progress?.Invoke($"{label}{pctTxt} · {FormatSpeed(bps)}", frac);
                }
            }
        }
        File.Move(tmp, LocalPath, overwrite: true);
    }
}
