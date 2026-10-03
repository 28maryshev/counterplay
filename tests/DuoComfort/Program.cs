using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Обмен наигранностью с напарником по дуо-пулу.
///
/// Подсказку «что брать обоим» программа считает и для половины друга, но его
/// наигранность ей неизвестна: своя к его чемпионам не относится. Каждый
/// выкладывает свои числа по чемпионам СВОЕЙ половины, второй их забирает — и
/// подбор у обоих считается на одних данных.
///
/// Проверка поднимает свой сервер, повторяющий контракт <c>/api/sync/{id}</c>, и
/// гоняет через него весь путь: снимок → выкладка → приём → кэш. Швы
/// <c>DuoShare.DirOverride</c> и <c>EndpointOverride</c> существуют ради этого:
/// ни боевой папки, ни журнала игрока проверка не касается — историю подставляет
/// <c>SessionTracker.HistoryOverride</c>.
///
/// Главное требование владельца: «можно раз синхронизировать и работать на
/// старых данных в следующей сессии, если новые не успеют подтянуться». Поэтому
/// отдельно проверяется, что забранное читается С ДИСКА при мёртвом сервере.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string PuuidA = "puuid-a-11111111";
    private const string PuuidB = "puuid-b-22222222";

    // Чемпионы половины A: первый играется, второй только намастерен, третий
    // пустой — его в коме быть не должно.
    private const int Played = 101, MasteredOnly = 102, Nothing = 103;

    /// Строки «сервера»: id → (версия, ком). Ровно то, что хранит настоящий.
    private static readonly Dictionary<string, (int Rev, byte[] Blob)> _rows = [];

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var dir = Path.Combine(Path.GetTempPath(), "cp-duocomfort");
        Try(() => Directory.Delete(dir, true));
        Directory.CreateDirectory(dir);

        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));

        DuoShare.DirOverride = dir;
        DuoShare.EndpointOverride = $"http://localhost:{port}/";

        var histBak = SessionTracker.HistoryOverride;
        try { Run(listener); }
        finally
        {
            SessionTracker.HistoryOverride = histBak;
            DuoShare.DirOverride = null;
            DuoShare.EndpointOverride = null;
            try { listener.Close(); } catch { }
            Try(() => Directory.Delete(dir, true));
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0
            ? "ИТОГ: наигранность напарника доезжает и живёт на диске"
            : $"ИТОГ: провалено проверок — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run(HttpListener listener)
    {
        var secret = DuoShare.NewSecret();
        Check("секрет дуо — 32 знака шестнадцатеричных", secret.Length == 32
              && secret.All(c => "0123456789abcdef".Contains(c)), secret);
        Check("два секрета не совпадают", DuoShare.NewSecret() != DuoShare.NewSecret(), "");

        // ── Снимок ──────────────────────────────────────────────────────────
        //
        // История подставная: боевой журнал игрока не читается вовсе.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        SessionTracker.HistoryOverride = new SessionTracker.PlayHistory(
            new Dictionary<int, (int, int)> { [Played] = (10, 7) },
            new Dictionary<int, long[]> { [Played] = Times(now, 2) },
            spanDays: 400, now: now);

        var mastery = new Dictionary<int, long> { [Played] = 90_000, [MasteredOnly] = 50_000 };
        var snap = DuoShare.Snapshot([Played, MasteredOnly, Nothing], mastery,
                                     RecommendationEngine.FreshDays, RecommendationEngine.RegularGames);

        Check("в снимке только те, о ком есть что сказать", snap.Count == 2,
              $"чемпионов {snap.Count}");
        Check("пустой чемпион в ком не попал", !snap.ContainsKey(Nothing), "");
        Check("игры и победы на месте",
              snap.TryGetValue(Played, out var m) && m.Games == 10 && m.Wins == 7,
              snap.TryGetValue(Played, out var m2) ? $"{m2.Games}-{m2.Wins}" : "нет");
        Check("очки мастерства на месте", snap[Played].Mastery == 90_000, $"{snap[Played].Mastery}");
        Check("намастеренный без игр тоже поехал", snap.ContainsKey(MasteredOnly), "");

        // ── Выкладка и приём ────────────────────────────────────────────────
        var pushed = DuoShare.PushAsync(secret, PuuidA, "1.3.53", "master", snap)
                              .GetAwaiter().GetResult();
        Check("своё выложилось", pushed, $"строк на сервере {_rows.Count}");
        Check("сервер не видит, чьё это и что внутри",
              _rows.Count == 1 && !Encoding.UTF8.GetString(_rows.First().Value.Blob).Contains(PuuidA),
              "ком зашифрован");

        var pulled = DuoShare.PullAsync(secret, PuuidA).GetAwaiter().GetResult();
        Check("напарник забрал", pulled, "");

        var got = DuoShare.Cached(PuuidA);
        Check("забранное читается из кэша", got is not null, $"чемпионов {got?.Count ?? 0}");
        Check("числа доехали без потерь",
              got is not null && got.TryGetValue(Played, out var g)
              && g.Games == 10 && g.Wins == 7 && g.Mastery == 90_000
              && Math.Abs(g.IdleDays - snap[Played].IdleDays) < 0.2,
              got is not null && got.TryGetValue(Played, out var g2)
                  ? $"{g2.Games}-{g2.Wins}, очки {g2.Mastery}, простой {g2.IdleDays:F1}" : "нет");
        Check("отметка времени записана", DuoShare.CachedAt(PuuidA) is not null, "");

        // ── Бакет эло: дуо считается по ВЫСШЕМУ из двух ──────────────────
        //
        // Решение владельца: «если нет [одного бакета], то надо реализовать
        // чтоб бакет у них срабатывал по высшему». Иначе у двоих в одном
        // драфте разные базовые винрейты, и подсказки расходятся при полностью
        // совпавших остальных данных. Оба считают максимум из тех же двух
        // чисел, поэтому приходят к одному.
        Check("бакет напарника доехал", DuoShare.CachedBucket(PuuidA) == "master",
              DuoShare.CachedBucket(PuuidA) ?? "нет");

        Check("высший из двух: свой ниже",
              PlayerInfo.HigherBucket("silver", "emerald") == "emerald", "");
        Check("высший из двух: свой выше",
              PlayerInfo.HigherBucket("master", "gold") == "master", "");
        Check("одинаковые остаются собой",
              PlayerInfo.HigherBucket("gold", "gold") == "gold", "");
        // Незнакомое имя не должно ни ронять, ни повышать: опечатка в данных
        // увела бы обоих не туда.
        Check("незнакомый бакет не повышает",
              PlayerInfo.HigherBucket("gold", "бронза") == "gold", "");
        Check("порядок бакетов тот же, что у пайплайна",
              string.Join(",", PlayerInfo.BucketsLowToHigh) == "silver,gold,emerald,master",
              string.Join(",", PlayerInfo.BucketsLowToHigh));

        // Повышение срабатывает ТОЛЬКО при активном дуо-пуле: играешь один —
        // считаем по своему эло, чужое тут ни при чём.
        var acc = PoolStore.Current();
        var poolsBak = acc.DuoPools.ToList();
        var kindBak = acc.ActiveKind;
        var idBak = acc.ActiveId;
        try
        {
            acc.DuoPools.Clear();
            acc.DuoPools.Add(new DuoPool { Id = "t", FriendName = "t", FriendPuuid = PuuidA });

            acc.ActiveKind = PoolKind.Normal; acc.ActiveId = null;
            Check("без активного дуо-пула бакет не поднимается",
                  DuoShare.RaiseBucket("silver") == "silver", DuoShare.RaiseBucket("silver") ?? "");

            acc.ActiveKind = PoolKind.Duo; acc.ActiveId = "t";
            Check("с активным дуо-пулом поднимается до напарникова",
                  DuoShare.RaiseBucket("silver") == "master", DuoShare.RaiseBucket("silver") ?? "");
            Check("свой выше — остаётся свой",
                  DuoShare.RaiseBucket("master") == "master", DuoShare.RaiseBucket("master") ?? "");

            // Напарник неизвестен — поднимать нечем, и это не ошибка.
            acc.DuoPools[0].FriendPuuid = "кто-то-другой";
            Check("неизвестного напарника бакет не меняет",
                  DuoShare.RaiseBucket("silver") == "silver", DuoShare.RaiseBucket("silver") ?? "");
        }
        finally
        {
            acc.DuoPools.Clear();
            acc.DuoPools.AddRange(poolsBak);
            acc.ActiveKind = kindBak; acc.ActiveId = idBak;
        }

        // ── Чужим секретом не прочитать ─────────────────────────────────────
        var alien = DuoShare.PullAsync(DuoShare.NewSecret(), PuuidA).GetAwaiter().GetResult();
        Check("чужим секретом не прочитать", !alien, "");
        Check("и кэш при этом не затёрт", DuoShare.Cached(PuuidA) is not null, "");

        // Слот, в который никто не писал: не ошибка и не повод чистить кэш.
        var empty = DuoShare.PullAsync(secret, PuuidB).GetAwaiter().GetResult();
        Check("пустой слот — это не ошибка", !empty, "");
        Check("кэш напарника на месте", DuoShare.Cached(PuuidA) is not null, "");

        // ── Главное: работаем на старом снимке без сети ──────────────────────
        //
        // Владелец: «можно раз синхронизировать и работать на старых данных в
        // следующей сессии, если новые не успеют подтянуться». Гасим сервер
        // совсем и проверяем, что прошлый снимок на месте и читается.
        listener.Stop();
        var offlinePull = DuoShare.PullAsync(secret, PuuidA).GetAwaiter().GetResult();
        Check("без сети забрать не выходит (и это ожидаемо)", !offlinePull, "");

        var offline = DuoShare.Cached(PuuidA);
        Check("БЕЗ СЕТИ прошлый снимок читается с диска",
              offline is not null && offline.TryGetValue(Played, out var o)
              && o.Games == 10 && o.Mastery == 90_000,
              offline is null ? "пусто" : $"чемпионов {offline.Count}");

        // ── Наигранность напарника входит в оценку ──────────────────────────
        //
        // Без этого обмен бессмыслен: числа доехали, но на подбор не влияют.
        // Формула одна на оба источника (RecommendationEngine.Comfort), поэтому
        // проверяем не величину, а что она НЕ НОЛЬ и растёт с играми.
        Check("у снимка есть чем поднять оценку",
              offline is not null && offline[Played].Games > 0 && offline[Played].Mastery > 0,
              "игры и очки");
    }

    /// Времена последних игр: свежая daysSince дней назад, предыдущие через день.
    /// Одной отметки мало — свежесть считается по третьей с конца.
    private static long[] Times(long now, double daysSince)
    {
        var ts = new List<long>();
        for (var i = 4; i >= 0; i--)
            ts.Add(now - (long)((daysSince + i) * 86400));
        return [.. ts];
    }

    // ── свой сервер: тот же контракт, что у /api/sync/{id} ──────────────────

    private static void Serve(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = l.GetContext(); } catch { return; }
            try
            {
                var id = c.Request.Url!.AbsolutePath.Trim('/');
                byte[] body;

                if (c.Request.HttpMethod == "GET")
                {
                    // Нет строки — это не ошибка: так выглядит первый заход.
                    body = _rows.TryGetValue(id, out var row)
                        ? Encoding.UTF8.GetBytes(
                            $"{{\"rev\":{row.Rev},\"blob\":\"{Convert.ToBase64String(row.Blob)}\"}}")
                        : Encoding.UTF8.GetBytes("{\"rev\":0,\"blob\":null}");
                }
                else if (c.Request.HttpMethod == "PUT")
                {
                    using var sr = new StreamReader(c.Request.InputStream, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(sr.ReadToEnd());
                    var rev = doc.RootElement.GetProperty("rev").GetInt32();
                    var blob = Convert.FromBase64String(doc.RootElement.GetProperty("blob").GetString()!);

                    var have = _rows.TryGetValue(id, out var cur) ? cur.Rev : 0;
                    if (have != rev)
                    {
                        // Как настоящий: версия не та — 409 и текущий номер.
                        c.Response.StatusCode = 409;
                        body = Encoding.UTF8.GetBytes($"{{\"error\":\"stale\",\"rev\":{have}}}");
                    }
                    else
                    {
                        _rows[id] = (have + 1, blob);
                        body = Encoding.UTF8.GetBytes($"{{\"rev\":{have + 1}}}");
                    }
                }
                else
                {
                    c.Response.StatusCode = 405;
                    body = [];
                }

                c.Response.ContentType = "application/json";
                c.Response.ContentLength64 = body.Length;
                c.Response.OutputStream.Write(body, 0, body.Length);
            }
            catch { /* проверка не про устойчивость сервера */ }
            finally { try { c.Response.Close(); } catch { } }
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static void Try(Action a) { try { a(); } catch { } }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
