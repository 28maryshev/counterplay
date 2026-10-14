using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
// Синхронизация — проходы, сеть и шифрование. Сбор файлов и слияние — в SyncClient.Merge.cs.
public static partial class SyncClient
{
    private const string Endpoint = "https://counterplays.com/api/sync/";

    /// <summary>Шов для проверок: свой адрес сервера.</summary>
    public static string? EndpointOverride { get; set; }

    private static string Api => EndpointOverride ?? Endpoint;

    private static string Dir => AppPaths.Root;

    /// Файлы, которые едут целиком. Порядок не важен, важен состав.
    private static readonly string[] Files = ["pools.json", "ui.json", "session.json"];

    /// Номер версии, который мы видели в прошлый раз, — чтобы поймать чужую запись.
    private static string RevPath => Path.Combine(Dir, "sync.rev");

    /// <summary>
    /// Что пришло с другого компьютера за проход: новые игры, пулы, настройки.
    /// Не «какие файлы переписаны»: журнал игр переписывается и от своей
    /// чистки, а человеку важно, доехало ли то, что он играл на ноутбуке.
    /// </summary>
    public sealed record Incoming(int Games, bool Pools, bool Settings)
    {
        public static readonly Incoming None = new(0, false, false);

        public bool Any => Games > 0 || Pools || Settings;

        public Incoming Plus(Incoming o) =>
            new(Games + o.Games, Pools || o.Pools, Settings || o.Settings);
    }

    /// <summary>
    /// Итог прохода.
    /// </summary>
    /// <param name="Changed">Какие файлы на этом компьютере переписаны — их
    /// надо перечитать (<see cref="Pulled"/>).</param>
    /// <param name="Got">Что из этого пришло с другого компьютера.</param>
    /// <param name="ServerEmpty">На сервере не было ничего: первый компьютер с
    /// этим паролем — или пароль набран не так, как на первом.</param>
    /// <param name="ErrorKey">Причина неудачи — ключ строки, для строки
    /// состояния (<see cref="SyncStatus"/>).</param>
    public sealed record Result(bool Ok, string Message, IReadOnlyList<string>? Changed = null,
                                Incoming? Got = null, bool ServerEmpty = false, string? ErrorKey = null)
    {
        /// Сколько файлов на этом компьютере переписано.
        public int Pulled => Changed?.Count ?? 0;
    }

    private static Result Fail(string key, IReadOnlyList<string>? changed = null, Incoming? got = null) =>
        new(false, Loc.T(key), changed, got, ErrorKey: key);

    /// <summary>
    /// Адрес строки: от пароля и игрового профиля сразу. Ни то ни другое по
    /// отдельности сюда не приводит.
    /// </summary>
    private static string IdFor(byte[] key) =>
        // Из ключа, а не из пароля напрямую: пароль так и не покидает машину
        // даже в виде хэша, а ключ уже прошёл 200 000 проходов.
        Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant();

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
        if (!SyncPassword.IsSet) return Fail("sync.noPassword");
        if (string.IsNullOrEmpty(puuid)) return Fail("sync.noAccount");
        // Пароль задан, но не читается (файл унесли с другой машины) — его
        // надо просто ввести заново.
        var key = SyncPassword.KeyFor(puuid);
        if (key is null) return Fail("sync.noPassword");
        var id = IdFor(key);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Копим за все заходы: если сервер ответил 409 уже после того, как
        // пришедшее легло на диск, следующий заход его не увидит — оно уже своё.
        var changed = new List<string>();
        var got = Incoming.None;
        try
        {
            for (var left = tries; left > 0; left--)
            {
                // 1. Что лежит на сервере.
                var (rev, theirs, empty) = await FetchAsync(http, id, key, ct);

                // 2. Слить с тем, что на этом компьютере.
                var mine = Collect();
                var merged = theirs is null ? mine : Merge(mine, theirs);
                if (theirs is not null)
                {
                    foreach (var f in Apply(merged))
                        if (!changed.Contains(f)) changed.Add(f);
                    got = got.Plus(Came(mine, merged));
                }

                // 3. Выложить обратно.
                if (await PushAsync(http, id, key, merged, rev, ct))
                    return new Result(true, Loc.T("sync.done"), changed, got, empty);

                // Пока сливали, записали с другого компьютера — проходим ещё раз
                // с их версией.
                //
                // Число заходов ограничено: раньше их не считали вовсе, и пара
                // компьютеров, пишущих одновременно, могла гонять друг друга по
                // кругу без конца. С автоматической синхронизацией заходов стало
                // больше, а значит и совпадений.
            }
            return Fail("sync.failedNet", changed, got);
        }
        // Отмена — только настоящая: тайм-аут HttpClient тоже приходит
        // OperationCanceledException, но это сеть, а не закрытие программы, и
        // строка состояния должна сказать «не удалось связаться».
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Log.Write($"синхронизация не вышла: {e.Message}");
            return Fail("sync.failedNet", changed, got);
        }
    }

    /// <summary>
    /// Что сделать, когда проход переписал файлы на этом компьютере (их имена —
    /// в аргументе).
    ///
    /// Интерфейс держит пулы, настройки и профиль в памяти и о подмене файлов
    /// сам не узнает: на экране осталось бы прежнее, а на диске уже новое.
    /// Хуже того, настройки из памяти первым же сохранением затёрли бы
    /// пришедшие и уехали бы обратно старыми.
    ///
    /// Зовётся из фонового потока — перечитывать и перерисовывать надо в потоке
    /// окна, это забота того, кто ставит обработчик.
    /// </summary>
    public static Action<IReadOnlyList<string>>? Pulled { get; set; }

    /// Когда последний раз сходили.
    private static DateTime _lastAuto = DateTime.MinValue;

    /// Один проход за раз — и тихий, и по кнопке: два сразу подрались бы за
    /// одну и ту же строку на сервере.
    private static readonly SemaphoreSlim Gate = new(1, 1);

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
    /// Ничего не спрашивает; чем кончилось — видно в строке под кнопкой
    /// (<see cref="SyncStatus"/>) и в журнале.
    /// </summary>
    public static async Task AutoAsync(string? puuid, string reason, CancellationToken ct = default)
    {
        if (Sandbox.Active || !SyncPassword.IsSet || string.IsNullOrEmpty(puuid)) return;
        if (DateTime.UtcNow - _lastAuto < AutoQuiet) return;
        // Уже идём — второй заход только подрался бы с первым за ту же строку.
        if (!Gate.Wait(0)) return;
        try { await RunAsync(puuid, reason, ct); }
        catch (OperationCanceledException) { }
        catch (Exception e) { Log.Write($"синхронизация ({reason}): {e.Message}"); }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Проход по просьбе человека: кнопка «синхронизировать» и только что
    /// заданный пароль. Порог в минуту не действует, идущий проход —
    /// дожидаемся.
    ///
    /// Пароль раньше не запускал ничего: данные ждали первого события — конца
    /// игры или закрытия окна пулов. Владелец на втором компьютере: «добавил
    /// пароль, и дальше ничего не происходило пару минут».
    /// </summary>
    public static async Task<Result> NowAsync(string? puuid, string reason, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try { return await RunAsync(puuid, reason, ct); }
        finally { Gate.Release(); }
    }

    /// Проход со всем, что вокруг него: строка состояния, журнал, перечитать
    /// переписанное. Только под <see cref="Gate"/>.
    private static async Task<Result> RunAsync(string? puuid, string reason, CancellationToken ct)
    {
        // Песочница данных игрока не трогает — и строку состояния тоже.
        if (Sandbox.Active) return await SyncAsync(puuid, ct);

        SyncStatus.Started();
        Result? r = null;
        try { r = await SyncAsync(puuid, ct); }
        finally { SyncStatus.Finished(r); }

        // Порог — от долбёжки сервера, поэтому ставится и на неудаче. Но не
        // тогда, когда до сервера не дошли: пароль задали до входа в клиент —
        // проход кончился «профиль не определён», и вход в аккаунт через
        // полминуты пропустился бы по порогу, а данные снова ждали бы конца игры.
        if (r.ErrorKey is not ("sync.noAccount" or "sync.noPassword"))
            _lastAuto = DateTime.UtcNow;

        Log.Write(r.Ok
            ? $"синхронизация ({reason}): {Describe(r)}"
            : $"синхронизация ({reason}) не вышла: {r.Message}");
        // И на неудаче: пришедшее могло лечь на диск до того, как сервер не
        // принял своё.
        if (r.Pulled > 0) Pulled?.Invoke(r.Changed!);
        return r;
    }

    /// Для журнала: что пришло с другого компьютера.
    private static string Describe(Result r)
    {
        var what = r.Got is { Any: true } g
            ? $"пришло игр {g.Games}{(g.Pools ? ", пулы" : "")}{(g.Settings ? ", настройки" : "")}"
            : "нового нет";
        if (r.ServerEmpty) what += ", на сервере было пусто";
        return $"{what}; переписано файлов {r.Pulled}";
    }

    // ── сеть ───────────────────────────────────────────────────────────────

    /// Что лежит на сервере. Empty — строки нет вовсе (первый заход с этим
    /// паролем); не расшифровалось — Data null, но не Empty.
    private static async Task<(int Rev, JsonObject? Data, bool Empty)> FetchAsync(
        HttpClient http, string id, byte[] key, CancellationToken ct)
    {
        var resp = await http.GetAsync(Api + id, ct);
        resp.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject();
        var rev = (int?)json["rev"] ?? 0;
        var blob = (string?)json["blob"];
        if (string.IsNullOrEmpty(blob)) return (rev, null, true);

        var plain = Decrypt(Convert.FromBase64String(blob), key);
        return (rev, plain is null ? null : JsonNode.Parse(plain)?.AsObject(), false);
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
        var resp = await http.PutAsync(Api + id,
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
}
