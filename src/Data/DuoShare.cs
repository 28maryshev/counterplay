using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Обмен наигранностью с напарником по дуо-пулу.
///
/// Зачем. Подсказку «что брать обоим» программа считает и для половины друга
/// (см. <c>RecommendationEngine.PartnerScores</c>), но его наигранность ей
/// неизвестна: своя к его чемпионам не относится, и половина судилась только по
/// мете и драфту. Теперь каждый выкладывает свои числа по чемпионам СВОЕЙ
/// половины, а второй их забирает — и подбор у обоих считается на одних данных.
///
/// Сервер не нужен новый. Тот же <c>/api/sync/{id}</c>, что у переноса между
/// своими компьютерами, — слепая камера хранения: адрес строки без секрета не
/// вычислить, ком зашифрован здесь, ключа у сервера нет. Разница одна: у
/// переноса секрет это личный пароль, а здесь — отдельный СЕКРЕТ ДУО.
///
/// Личный пароль синхронизации сюда брать нельзя категорически: он открывает
/// все свои настройки и историю, а делиться надо только наигранностью. Поэтому
/// секрет дуо случайный, рождается при выгрузке пула и едет внутри `.cpool` —
/// тем же путём, которым пул и так передаётся человеку.
///
/// Слотов два, по одному на человека: каждый ПИШЕТ только свой (адрес от своего
/// puuid) и ЧИТАЕТ только чужой. Двух писателей в одной строке нет, значит нет
/// и слияния версий — в отличие от переноса между своими компьютерами.
///
/// Старый снимок лучше никакого. Забранное кладётся на диск и берётся оттуда
/// СРАЗУ, ещё до всякой сети: драфт начинается быстрее, чем отвечает сервер, а
/// наигранность меняется медленно (окно месяц). Свежее подтягивается фоном — так
/// же, как подтягивается база.
/// </summary>
public static class DuoShare
{
    private const string Endpoint = "https://counterplays.com/api/sync/";

    /// Сколько чемпионов половины выкладываем. Половина больше десятка — редкость,
    /// а потолок не даёт превратить ком в выгрузку всей истории.
    private const int MaxChamps = 40;

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");


    /// <summary>Шов для проверок: своя папка вместо боевой.</summary>
    public static string? DirOverride { get; set; }

    /// <summary>Шов для проверок: свой адрес сервера.</summary>
    public static string? EndpointOverride { get; set; }

    private static string Folder => DirOverride ?? Dir;
    private static string Cache  => Path.Combine(Folder, "duo-comfort.json");
    private static string Api    => EndpointOverride ?? Endpoint;

    /// <summary>
    /// Новый секрет дуо: 128 случайных бит.
    ///
    /// Не пароль и не из пароля: человеку его не вводить и не запоминать, он
    /// просто едет в файле пула. Поэтому и длины хватает такой, какая не
    /// перебирается, а проходов KDF много не нужно.
    /// </summary>
    public static string NewSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    // Ключи выводятся раз и лежат в памяти: PBKDF2 нарочно медленный, а зовётся
    // это на каждый драфт.
    private static readonly Dictionary<string, byte[]> _keys = [];

    private static byte[] KeyFor(string secret, string puuid)
    {
        var cacheKey = secret + "|" + puuid;
        if (_keys.TryGetValue(cacheKey, out var k)) return k;

        // Соль из puuid владельца слота, как в переносе: у обоих она считается
        // одинаково, возить её вместе с данными не нужно.
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("counterplay-duo:" + puuid));
        k = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt,
                                      iterations: 200_000, HashAlgorithmName.SHA256, outputLength: 32);
        _keys[cacheKey] = k;
        return k;
    }

    /// Адрес слота: из ключа, а не из секрета напрямую — секрет не покидает
    /// машину даже в виде хэша.
    private static string SlotId(string secret, string puuid) =>
        Convert.ToHexString(SHA256.HashData(KeyFor(secret, puuid))).ToLowerInvariant();

    // ── снимок наигранности ─────────────────────────────────────────────────

    /// <summary>
    /// Мои числа по чемпионам СВОЕЙ половины: игры и победы за окно свежести,
    /// очки мастерства и простой (дней с третьей игры с конца).
    ///
    /// Возим исходные величины, а не готовый комфорт: формула менялась и будет
    /// меняться, а считать её должна принимающая сторона — тогда она остаётся
    /// одна на обоих. См. <see cref="MateComfort"/>.
    /// </summary>
    public static Dictionary<int, MateComfort> Snapshot(
        IEnumerable<int> champs, IReadOnlyDictionary<int, long>? mastery, int freshDays, int regularGames)
    {
        var hist = SessionTracker.History(freshDays,
            [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal]);

        var res = new Dictionary<int, MateComfort>();
        foreach (var id in champs.Where(c => c != 0).Distinct().Take(MaxChamps))
        {
            var (g, w) = hist.Recent(id);
            var pts = mastery is not null && mastery.TryGetValue(id, out var p) && p > 0 ? p : 0L;
            // Чемпион, на котором ни игр, ни очков, в ком не кладём: он ничего не
            // говорит, а ком раздувает.
            if (g == 0 && pts == 0) continue;
            res[id] = new MateComfort(g, w, pts, hist.DaysSinceNth(id, regularGames));
        }
        return res;
    }

    // ── кэш на диске ────────────────────────────────────────────────────────

    private sealed class CacheEntry
    {
        public long At { get; set; }                  // когда забрали, unix-секунды
        public string Version { get; set; } = "";     // версия программы напарника
        public Dictionary<string, int[]> Champs { get; set; } = [];  // id → [g, w, mastery, idleDays×10]
    }

    private static Dictionary<string, CacheEntry> ReadCache()
    {
        try
        {
            if (!File.Exists(Cache)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(
                       File.ReadAllText(Cache)) ?? [];
        }
        catch { return []; }   // битый кэш — не повод падать, считаем что его нет
    }

    private static void WriteCache(Dictionary<string, CacheEntry> all)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Cache, JsonSerializer.Serialize(all,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { Log.Write($"дуо-обмен: кэш не записался — {e.Message}"); }
    }

    /// <summary>
    /// Наигранность напарника ИЗ КЭША, без сети. Зовётся на старте драфта: нет
    /// ничего — вернёт null, и его половина посчитается без личных факторов.
    /// </summary>
    public static IReadOnlyDictionary<int, MateComfort>? Cached(string? matePuuid)
    {
        if (string.IsNullOrEmpty(matePuuid)) return null;
        var all = ReadCache();
        if (!all.TryGetValue(matePuuid, out var e) || e.Champs.Count == 0) return null;

        var res = new Dictionary<int, MateComfort>();
        foreach (var (k, v) in e.Champs)
            if (int.TryParse(k, out var id) && v.Length >= 4)
                res[id] = new MateComfort(v[0], v[1], v[2], v[3] / 10.0);
        return res.Count > 0 ? res : null;
    }

    /// <summary>Когда снимок напарника забирали последний раз (null — никогда).</summary>
    public static DateTimeOffset? CachedAt(string? matePuuid)
    {
        if (string.IsNullOrEmpty(matePuuid)) return null;
        var all = ReadCache();
        return all.TryGetValue(matePuuid, out var e) && e.At > 0
            ? DateTimeOffset.FromUnixTimeSeconds(e.At) : null;
    }

    // ── сеть ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Выложить СВОЙ снимок в свой слот. Пишем только свою строку, поэтому
    /// версии сверять не с кем: при расхождении просто повторяем с номером,
    /// который назвал сервер.
    /// </summary>
    public static async Task<bool> PushAsync(string secret, string myPuuid, string version,
                                             IReadOnlyDictionary<int, MateComfort> snapshot,
                                             CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(myPuuid) || snapshot.Count == 0)
            return false;

        var champs = new Dictionary<string, int[]>();
        foreach (var (champId, m) in snapshot)
            champs[champId.ToString()] = [m.Games, m.Wins, (int)Math.Min(m.Mastery, int.MaxValue),
                                          (int)Math.Round(m.IdleDays * 10)];

        var payload = JsonSerializer.Serialize(new CacheEntry
        {
            At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Version = version, Champs = champs
        });

        var key = KeyFor(secret, myPuuid);
        var blob = Convert.ToBase64String(Encrypt(payload, key));
        var id   = SlotId(secret, myPuuid);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var rev = await RevAsync(http, id, ct);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var res = await http.PutAsync(Api + id,
                new StringContent(JsonSerializer.Serialize(new { rev, blob }),
                                  Encoding.UTF8, "application/json"), ct);
            if (res.IsSuccessStatusCode) return true;
            if ((int)res.StatusCode != 409) break;
            // Версия разошлась — нашей же записью с другого компьютера. Берём ту,
            // что назвал сервер, и повторяем один раз.
            rev = await RevAsync(http, id, ct);
        }
        return false;
    }

    /// <summary>
    /// Забрать снимок НАПАРНИКА и положить в кэш. Ничего не забрали — кэш
    /// остаётся прежним: старые данные лучше пустых.
    /// </summary>
    public static async Task<bool> PullAsync(string secret, string matePuuid,
                                             CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(matePuuid)) return false;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var id = SlotId(secret, matePuuid);
            var res = await http.GetAsync(Api + id, ct);
            if (!res.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("blob", out var b) || b.ValueKind != JsonValueKind.String)
                return false;   // пусто — напарник ещё не выкладывал

            var plain = Decrypt(Convert.FromBase64String(b.GetString()!), KeyFor(secret, matePuuid));
            if (plain is null) return false;

            var entry = JsonSerializer.Deserialize<CacheEntry>(plain);
            if (entry is null || entry.Champs.Count == 0) return false;

            var all = ReadCache();
            all[matePuuid] = entry;
            WriteCache(all);
            Log.Write($"дуо-обмен: наигранность напарника забрана, чемпионов {entry.Champs.Count}");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"дуо-обмен: забрать не вышло — {e.Message}");
            return false;
        }
    }

    private static async Task<int> RevAsync(HttpClient http, string id, CancellationToken ct)
    {
        try
        {
            var res = await http.GetAsync(Api + id, ct);
            if (!res.IsSuccessStatusCode) return 0;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("rev", out var r) && r.TryGetInt32(out var v) ? v : 0;
        }
        catch { return 0; }
    }

    // ── шифрование ──────────────────────────────────────────────────────────
    //
    // То же, что в переносе между своими компьютерами: AES-GCM, одноразовое
    // число в начале. Он же проверяет целостность, поэтому подменённый по дороге
    // ком не расшифруется, а не выдаст мусор.

    private static byte[] Encrypt(string plain, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, data, cipher, tag);

        var outp = new byte[nonce.Length + tag.Length + cipher.Length];
        nonce.CopyTo(outp, 0);
        tag.CopyTo(outp, nonce.Length);
        cipher.CopyTo(outp, nonce.Length + tag.Length);
        return outp;
    }

    private static string? Decrypt(byte[] blob, byte[] key)
    {
        try
        {
            if (blob.Length < 28) return null;
            var nonce = blob[..12];
            var tag = blob[12..28];
            var cipher = blob[28..];
            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            // Секрет не тот: пул передавали другим файлом. Не ошибка сети и не
            // повод что-то затирать.
            Log.Write("дуо-обмен: ком не расшифровался — секрет отличается");
            return null;
        }
    }
}
