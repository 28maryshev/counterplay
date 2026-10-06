using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Counterplay;

/// <summary>
/// Перенос данных между компьютерами одного человека.
///
/// Что переносится: пулы, история матчей со связками, график панели профиля и
/// настройки — то есть три файла в папке программы.
///
/// Сервер выступает камерой хранения и содержимого не видит: наружу уходит
/// зашифрованный ком, ключ выводится из пароля (см. <see cref="SyncPassword"/>)
/// и остаётся на компьютере. Строка на сервере ищется по значению, которое без
/// пароля не вычислить, поэтому «прочитать чужое, зная ник» здесь невозможно.
/// </summary>
public static class SyncClient
{
    private const string Endpoint = "https://counterplays.com/api/sync/";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");

    /// Файлы, которые едут целиком. Порядок не важен, важен состав.
    private static readonly string[] Files = ["pools.json", "ui.json", "session.json"];

    /// Номер версии, который мы видели в прошлый раз, — чтобы поймать чужую запись.
    private static string RevPath => Path.Combine(Dir, "sync.rev");

    public sealed record Result(bool Ok, string Message, int Pulled = 0, int Pushed = 0);

    /// <summary>
    /// Адрес строки: от пароля и игрового профиля сразу. Ни то ни другое по
    /// отдельности сюда не приводит.
    /// </summary>
    private static string? IdFor(string? puuid)
    {
        var key = SyncPassword.KeyFor(puuid);
        if (key is null) return null;
        // Из ключа, а не из пароля напрямую: пароль так и не покидает машину
        // даже в виде хэша, а ключ уже прошёл 200 000 проходов.
        return Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();
    }

    /// <summary>
    /// Забрать чужое, слить со своим, выложить обратно. Один проход.
    /// </summary>
    public static async Task<Result> SyncAsync(string? puuid, CancellationToken ct = default,
                                              int tries = 3)
    {
        // Песочница не синхронизируется: синхронизация возит НАСТОЯЩИЕ файлы
        // игрока, а песочница сидит под аккаунтом «test-account» — его файлы
        // уезжали бы в чужую строку и сливались оттуда обратно.
        if (Sandbox.Active) return new Result(false, "песочница не синхронизируется");
        if (!SyncPassword.IsSet) return new Result(false, Loc.T("sync.noPassword"));
        var id = IdFor(puuid);
        if (id is null) return new Result(false, Loc.T("sync.noAccount"));

        var key = SyncPassword.KeyFor(puuid)!;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        try
        {
            // 1. Что лежит на сервере.
            var (rev, theirs) = await FetchAsync(http, id, key, ct);

            // 2. Слить с тем, что на этом компьютере.
            var mine = Collect();
            var merged = theirs is null ? mine : Merge(mine, theirs);
            var pulled = theirs is null ? 0 : Apply(merged);

            // 3. Выложить обратно.
            var pushed = await PushAsync(http, id, key, merged, rev, ct);
            if (!pushed)
            {
                // Пока сливали, записали с другого компьютера — проходим ещё раз
                // с их версией.
                //
                // Число заходов ограничено: раньше их не считали вовсе, и пара
                // компьютеров, пишущих одновременно, могла гонять друг друга по
                // кругу без конца. С автоматической синхронизацией заходов стало
                // больше, а значит и совпадений.
                if (tries <= 1) return new Result(false, Loc.T("sync.failedNet"));
                return await SyncAsync(puuid, ct, tries - 1);
            }

            return new Result(true, Loc.T("sync.done"), pulled, Files.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            Log.Write($"синхронизация не вышла: {e.Message}");
            return new Result(false, Loc.T("sync.failedNet"));
        }
    }

    /// <summary>
    /// Что сделать, когда с сервера пришли ЧУЖИЕ правки.
    ///
    /// Интерфейс держит пулы в памяти и о подмене файлов сам не узнает: на
    /// экране осталось бы прежнее, а на диске уже новое. Ручной проход делает
    /// то же самое, только там есть кому нажать и обновить.
    ///
    /// Зовётся из фонового потока — перечитывать и перерисовывать надо в потоке
    /// окна, это забота того, кто ставит обработчик.
    /// </summary>
    public static Action? Pulled { get; set; }

    /// Когда последний раз сходили сами, и не идёт ли проход прямо сейчас.
    private static DateTime _lastAuto = DateTime.MinValue;
    private static int _autoRunning;

    /// <summary>
    /// Не чаще раза в минуту. События идут пачками — конец игры, правка пула,
    /// переподключение к клиенту, — и без этого порога десять событий подряд
    /// устроили бы десять заходов за одну и ту же строку.
    /// </summary>
    private static readonly TimeSpan AutoQuiet = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Тихий проход: сам решает, пора ли, и молчит, если не пора.
    ///
    /// Заведён потому, что кнопка сама себя не нажимает. Человек задавал
    /// пароль и играл неделю, а на сервер не уезжало ничего: получалась не
    /// синхронизация, а выгрузка по памяти. Забыл нажать — и переустановка
    /// Windows уносит историю, связки и пулы.
    ///
    /// Ничего не показывает и не спрашивает: следы — только в журнале. Кнопка
    /// остаётся для тех, кому надо прямо сейчас, и порог её не касается.
    /// </summary>
    public static async Task AutoAsync(string? puuid, string reason, CancellationToken ct = default)
    {
        if (Sandbox.Active || !SyncPassword.IsSet || string.IsNullOrEmpty(puuid)) return;
        if (DateTime.UtcNow - _lastAuto < AutoQuiet) return;
        // Уже идём — второй заход только подрался бы с первым за ту же строку.
        if (Interlocked.Exchange(ref _autoRunning, 1) == 1) return;
        try
        {
            var r = await SyncAsync(puuid, ct);
            _lastAuto = DateTime.UtcNow;   // и на неудаче: иначе будем долбиться
            Log.Write(r.Ok
                ? $"синхронизация ({reason}): забрано {r.Pulled} из {r.Pushed}"
                : $"синхронизация ({reason}) не вышла: {r.Message}");
            if (r is { Ok: true, Pulled: > 0 }) Pulled?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Write($"синхронизация ({reason}): {e.Message}"); }
        finally { Interlocked.Exchange(ref _autoRunning, 0); }
    }

    // ── сеть ───────────────────────────────────────────────────────────────

    private static async Task<(int Rev, JsonObject? Data)> FetchAsync(
        HttpClient http, string id, byte[] key, CancellationToken ct)
    {
        var resp = await http.GetAsync(Endpoint + id, ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
        var rev = (int?)json["rev"] ?? 0;
        var blob = (string?)json["blob"];
        if (string.IsNullOrEmpty(blob)) return (rev, null);

        var plain = Decrypt(Convert.FromBase64String(blob), key);
        return (rev, plain is null ? null : JsonNode.Parse(plain)?.AsObject());
    }

    private static async Task<bool> PushAsync(HttpClient http, string id, byte[] key,
                                              JsonObject data, int rev, CancellationToken ct)
    {
        var blob = Encrypt(data.ToJsonString(), key);
        var body = new JsonObject
        {
            ["rev"] = rev,
            ["blob"] = Convert.ToBase64String(blob)
        };
        var resp = await http.PutAsync(Endpoint + id,
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict) return false;
        resp.EnsureSuccessStatusCode();

        var got = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
        try { await File.WriteAllTextAsync(RevPath, ((int?)got["rev"] ?? 0).ToString(), ct); }
        catch { /* не записалось — в следующий раз просто заберём заново */ }
        return true;
    }

    // ── шифрование ─────────────────────────────────────────────────────────
    //
    // AES-GCM: он же и проверяет целостность, поэтому подменённый по дороге ком
    // не расшифруется, а не выдаст мусор. Одноразовое число кладём в начало —
    // оно не секрет, но обязано быть разным у каждой записи.

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
            // Ключ не тот: пароль на этом компьютере другой. Не ошибка сети и
            // не повод что-то затирать — просто молчим и ничего не принимаем.
            Log.Write("синхронизация: ком не расшифровался — пароль отличается");
            return null;
        }
    }

    // ── файлы и слияние ────────────────────────────────────────────────────

    /// Снимок этого компьютера: содержимое файлов и когда их меняли.
    private static JsonObject Collect()
    {
        var o = new JsonObject();
        foreach (var name in Files)
        {
            var path = Path.Combine(Dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                o[name] = new JsonObject
                {
                    ["at"] = new FileInfo(path).LastWriteTimeUtc.ToUnixTimeSecondsSafe(),
                    ["body"] = File.ReadAllText(path)
                };
            }
            catch { /* занят или битый — пропускаем, остальное поедет */ }
        }
        return o;
    }

    /// <summary>
    /// Слияние. Настройки и пулы берём те, что новее: их правят целиком, и
    /// «победил свежий» здесь верно. Журнал сессий сливаем по-настоящему —
    /// см. <see cref="MergeSession"/>.
    /// </summary>
    private static JsonObject Merge(JsonObject mine, JsonObject theirs)
    {
        var res = new JsonObject();
        foreach (var name in Files)
        {
            var a = mine[name]?.AsObject();
            var b = theirs[name]?.AsObject();
            if (a is null && b is null) continue;
            if (a is null) { res[name] = b!.DeepClone(); continue; }
            if (b is null) { res[name] = a.DeepClone(); continue; }

            if (name == "session.json")
            {
                var merged = MergeSession((string)a["body"]!, (string)b["body"]!);
                res[name] = new JsonObject
                {
                    ["at"] = Math.Max((long)a["at"]!, (long)b["at"]!),
                    ["body"] = merged
                };
                continue;
            }

            // Пулы: пустое НИКОГДА не побеждает непустое, как бы свежо оно ни
            // было. Иначе выходит так: локальный файл обнулился (сбой чтения,
            // падение посреди записи), он же свежее — и пустота уезжает на
            // сервер, добивая последнюю копию. Владелец на это и указал:
            // «нельзя делать пустой», «если ничего нет и нажать
            // синхронизировать — должно выкачивать».
            if (name == "pools.json")
            {
                bool mineEmpty = PoolsEmpty(a), theirsEmpty = PoolsEmpty(b);
                if (mineEmpty != theirsEmpty)
                {
                    res[name] = (mineEmpty ? b : a).DeepClone();
                    continue;
                }
            }

            res[name] = ((long)a["at"]! >= (long)b["at"]! ? a : b).DeepClone();
        }
        return res;
    }

    /// <summary>
    /// Нет ли в этой копии пулов вовсе.
    ///
    /// Файл с одними пустыми аккаунтами — это не «человек всё удалил», а почти
    /// всегда сбой: удаляют пулы по одному, а не все пять аккаунтов разом.
    /// Разобрать не вышло — считаем непустым: на догадке затирать нельзя.
    /// </summary>
    private static bool PoolsEmpty(JsonObject side)
    {
        try
        {
            var body = (string?)side["body"];
            if (string.IsNullOrWhiteSpace(body)) return true;
            var root = JsonNode.Parse(body)?.AsObject();
            if (root is null || root.Count == 0) return true;
            foreach (var acc in root)
            {
                var o = acc.Value?.AsObject();
                if (o is null) continue;
                if ((o["Pools"]?.AsArray()?.Count ?? 0) > 0) return false;
                if ((o["DuoPools"]?.AsArray()?.Count ?? 0) > 0) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Журнал сессий: «победил свежий» здесь не годится.
    ///
    /// На двух компьютерах играют РАЗНЫЕ игры, и взять файл целиком означает
    /// потерять половину истории. Поэтому сливаем по составу: игры — по номеру,
    /// связки — объединением отметок времени.
    /// </summary>
    private static string MergeSession(string mine, string theirs)
    {
        try
        {
            var a = JsonNode.Parse(mine)!.AsObject();
            var b = JsonNode.Parse(theirs)!.AsObject();
            var accА = a["Accounts"]?.AsObject();
            var accB = b["Accounts"]?.AsObject();
            if (accА is null || accB is null) return mine;

            foreach (var (puuid, their) in accB)
            {
                if (their is not JsonObject t) continue;
                if (accА[puuid] is not JsonObject m) { accА[puuid] = t.DeepClone(); continue; }

                MergeQueues(m, t);
                MergePairs(m, t);
            }
            return a.ToJsonString();
        }
        catch
        {
            // Чужой журнал не разобрался — свой не трогаем. Потерять историю
            // хуже, чем не получить чужую.
            Log.Write("синхронизация: журнал не разобрался — оставляю свой");
            return mine;
        }
    }

    /// Игры по очередям: объединяем по времени начала, повторы отбрасываем.
    private static void MergeQueues(JsonObject mine, JsonObject theirs)
    {
        var qm = mine["Queues"]?.AsObject();
        var qt = theirs["Queues"]?.AsObject();
        if (qt is null) return;
        if (qm is null) { mine["Queues"] = qt.DeepClone(); return; }

        foreach (var (queue, node) in qt)
        {
            if (node is not JsonObject t) continue;
            if (qm[queue] is not JsonObject m) { qm[queue] = t.DeepClone(); continue; }

            var gm = m["Games"]?.AsArray();
            var gt = t["Games"]?.AsArray();
            if (gt is null) continue;
            if (gm is null) { m["Games"] = gt.DeepClone(); continue; }

            var seen = new HashSet<long>();
            foreach (var g in gm) if (g?["Ts"] is { } ts) seen.Add((long)ts);
            foreach (var g in gt)
                if (g?["Ts"] is { } ts && seen.Add((long)ts))
                    gm.Add(g.DeepClone());
        }
    }

    /// Связки: у каждой два списка отметок времени — складываем и чистим повторы.
    private static void MergePairs(JsonObject mine, JsonObject theirs)
    {
        var pm = mine["Pairs"]?.AsObject();
        var pt = theirs["Pairs"]?.AsObject();
        if (pt is null) return;
        if (pm is null) { mine["Pairs"] = pt.DeepClone(); return; }

        foreach (var (key, node) in pt)
        {
            if (node is not JsonObject t) continue;
            if (pm[key] is not JsonObject m) { pm[key] = t.DeepClone(); continue; }
            foreach (var side in new[] { "Won", "Lost" })
            {
                var am = m[side]?.AsArray();
                var at = t[side]?.AsArray();
                if (at is null) continue;
                if (am is null) { m[side] = at.DeepClone(); continue; }
                var seen = new HashSet<long>();
                foreach (var x in am) if (x is not null) seen.Add((long)x);
                foreach (var x in at) if (x is not null && seen.Add((long)x)) am.Add((long)x);
            }
        }
    }

    /// Записать слитое на диск. Возвращает, сколько файлов изменилось.
    private static int Apply(JsonObject data)
    {
        var n = 0;
        foreach (var name in Files)
        {
            var body = (string?)data[name]?["body"];
            if (body is null) continue;
            var path = Path.Combine(Dir, name);
            try
            {
                if (File.Exists(path) && File.ReadAllText(path) == body) continue;
                // Копия рядом: если слияние ошиблось, прежнее не потеряно.
                if (File.Exists(path)) File.Copy(path, path + ".before-sync", overwrite: true);
                File.WriteAllText(path, body);
                n++;
            }
            catch (Exception e) { Log.Write($"синхронизация: {name} не записался ({e.Message})"); }
        }
        return n;
    }
}

internal static class TimeExt
{
    public static long ToUnixTimeSecondsSafe(this DateTime utc) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
