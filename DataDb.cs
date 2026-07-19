using System.Diagnostics;
using System.Text.Json;

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
    private const string DataUrl    = "https://github.com/28maryshev/counterplay/releases/download/data/data.db";
    private const string VersionUrl = "https://github.com/28maryshev/counterplay/releases/download/data/data-version.json";

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

    public static string FormatSpeed(double bytesPerSec) =>
        bytesPerSec >= 1_000_000 ? $"{bytesPerSec / 1_048_576.0:0.0} МБ/с"
        : bytesPerSec > 0        ? $"{bytesPerSec / 1024.0:0} КБ/с"
        : "";

    /// Гарантирует наличие актуальной базы. Для dev — берёт локальную как есть.
    /// Для установленной версии — сверяет версию с сервером и подкачивает свежую.
    /// progress: (текст со статусом+скоростью, доля 0..1).
    public static async Task EnsureAsync(Action<string, double>? progress, CancellationToken ct)
    {
        // 1. Dev-режим (по opt-in): локальная база рядом с проектом — не качаем.
        if (DevDbEnabled && DevCandidates.Any(p => File.Exists(p) && RecommendationEngine.HasData(p)))
            return;

        // 2. Установленная версия — версионная подкачка.
        try
        {
            Directory.CreateDirectory(Dir);

            var remoteVer = await FetchVersionAsync(ct);
            var localVer  = File.Exists(LocalVersionPath) ? File.ReadAllText(LocalVersionPath).Trim() : null;
            var haveDb    = File.Exists(LocalPath) && new FileInfo(LocalPath).Length > 0;

            // База есть и версия совпадает (или сервер недоступен) — ничего не делаем.
            if (haveDb && (remoteVer is null || remoteVer == localVer)) return;

            var label = haveDb ? Loc.T("status.updatingDb") : Loc.T("status.downloadingDb");
            progress?.Invoke(label, 0);
            await DownloadAsync(label, progress, ct);

            if (remoteVer is not null)
                await File.WriteAllTextAsync(LocalVersionPath, remoteVer, ct);
        }
        catch
        {
            // Офлайн / нет дата-релиза — работаем на том, что уже скачано (если есть).
        }
    }

    private static async Task<string?> FetchVersionAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var json = (await http.GetStringAsync(VersionUrl, ct)).TrimStart('﻿');
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static async Task DownloadAsync(string label, Action<string, double>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var resp = await http.GetAsync(DataUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
