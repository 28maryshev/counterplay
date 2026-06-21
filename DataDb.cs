namespace Counterplay;

/// <summary>
/// Гарантирует наличие data.db: использует локальную (dev/рядом), иначе
/// скачивает один раз из GitHub Releases в папку пользователя (переживает
/// обновления приложения).
/// </summary>
public static class DataDb
{
    // Постоянное место БД — вне каталога установки, чтобы не стиралась обновлениями.
    public static string LocalPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "Counterplay", "data.db");

    // Ассет последнего релиза на GitHub (заливать data.db в каждый релиз).
    private const string DownloadUrl =
        "https://github.com/28maryshev/counterplay/releases/latest/download/data.db";

    /// Путь к рабочей БД: существующую (валидную) или скачанную. null — если не удалось.
    public static async Task<string?> EnsureAsync(Action<string>? progress, CancellationToken ct)
    {
        var existing = RecommendationEngine.FindDb();
        if (existing != null) return existing;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LocalPath)!);
            progress?.Invoke("Скачиваю базу данных…");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var resp = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? 0;
            var tmp   = LocalPath + ".tmp";

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
                    if (total > 0) progress?.Invoke($"Скачиваю базу данных… {read * 100 / total}%");
                }
            }

            File.Move(tmp, LocalPath, overwrite: true);
            return RecommendationEngine.FindDb(); // перепроверит валидность (есть base_wr)
        }
        catch
        {
            return null;
        }
    }
}
