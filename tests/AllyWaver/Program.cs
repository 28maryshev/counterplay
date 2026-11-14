using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Союзник быстро перебирает чемпионов (14.11.2026). Владелец: «если человек
/// делает три смены и между сменами меньше секунды — выключаем переключение,
/// ждём, пока остановится, и если не переключает 5 секунд — адаптируем драфт
/// под его последний показанный пик»; следом — «если переключает быстрее, чем
/// раз в 3 секунды».
///
/// 1. AllyWaver на подставном времени: три смены через полсекунды и через
///    2,9 секунды — подбор держится на том, кто стоял до третьей; дальнейшие
///    смены его не трогают и отодвигают покой; 5 секунд без смен — отпускается
///    под последнего; промежуток в 3 секунды и больше, свой слот, «снял и навёл
///    того же» — не метания; в свой ход (чемпион в championId) — тоже
///    метания, лок отпускает.
/// 2. Отпечаток драфта видит, держим подбор или нет.
/// 3. Будильник сокета на подставном WebSocket: приходит в срок без единого
///    события, события клиента идут как шли и его не сбивают, снятый не звонит.
/// 4. Движок на живой базе: без удержания центр скачет за наведением; с ним —
///    тот же список до знака на любом наведении, ровно как при союзнике на
///    удержанном чемпионе; залочивший — по себе.
///
/// База — живая, только чтение; всё остальное — в своей папке.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Джанглеры, которых перебирает союзник: Ли Син, Вай, Амуму, Ка'Зикс, Джарван IV.
    private static readonly int[] Jg = [64, 254, 32, 121, 59];
    private const int Cell = 1;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "AllyWaver");
        try { Directory.Delete(root, recursive: true); } catch { /* не было */ }
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        Names(playerDir);

        Tracker();
        Hash();
        Wake().GetAwaiter().GetResult();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — движок проверять нечем"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Engine(engine);
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: центр держится, пока союзник щёлкает, и встаёт под его выбор через 5 секунд"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Кто мечется ──────────────────────────────────────────────────────

    private static void Tracker()
    {
        Console.WriteLine("кто мечется (AllyWaver):");
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        var w = new AllyWaver();
        IReadOnlyDictionary<int, int>? Hover(int champ, double sec, bool locked = false, bool inTurn = false) =>
            w.Observe(Slots(Cell, champ, locked, inTurn: inTurn), t0.AddSeconds(sec));
        string Held(IReadOnlyDictionary<int, int>? r) =>
            r is null ? "не держим" : string.Join("; ", r.Select(kv => $"{kv.Key}: {Name(kv.Value)}"));
        bool On(IReadOnlyDictionary<int, int>? r, int champ) => r?.GetValueOrDefault(Cell, -1) == champ;

        // Ли Син стоял долго, потом три щелчка через полсекунды.
        Hover(Jg[0], 0);
        Hover(Jg[1], 10);
        var two = Hover(Jg[2], 10.5);
        Check("две смены — ещё не держим", two is null, Held(two));
        var three = Hover(Jg[3], 11);
        Check("третья смена через полсекунды — держим на том, кто стоял до неё (центр под него и посчитан)",
              On(three, Jg[2]), Held(three));
        Check("покой — через 5 секунд после последней смены", w.NextCalm == t0.AddSeconds(11) + AllyWaver.Calm,
              $"{(w.NextCalm - t0).TotalSeconds} с");
        var more = Hover(Jg[4], 13);
        Check("щёлкает дальше — подбор не трогается", On(more, Jg[2]), Held(more));
        Check("…а покой отодвигается", w.NextCalm == t0.AddSeconds(13) + AllyWaver.Calm, "");
        Hover(0, 14);
        var back = Hover(Jg[4], 15);
        Check("снял и навёл снова — держим, покой с последнего движения",
              On(back, Jg[2]) && w.NextCalm == t0.AddSeconds(15) + AllyWaver.Calm, Held(back));
        var almost = Hover(Jg[4], 15 + AllyWaver.Calm.TotalSeconds - 0.1);
        Check("4,9 секунды без смен — ещё держим", On(almost, Jg[2]), Held(almost));
        var calm = Hover(Jg[4], 15 + AllyWaver.Calm.TotalSeconds);
        Check("5 секунд без смен — отпускаем, подбор под последнего показанного", calm is null, Held(calm));
        Check("и будильник больше не нужен", w.NextCalm == DateTime.MaxValue, "");

        // После покоя — снова три быстрых.
        Hover(Jg[0], 30); Hover(Jg[1], 30.4);
        var again = Hover(Jg[2], 30.8);
        Check("после покоя три быстрые смены — снова держим", On(again, Jg[1]), Held(again));
        var locked = Hover(Jg[2], 31.5, locked: true);
        Check("залочил — отпускаем сразу", locked is null, Held(locked));
        Check("и после лока не начинает", Hover(Jg[2], 32, locked: true) is null, "");

        w.Reset();
        Hover(Jg[0], 0); Hover(Jg[1], 1); Hover(Jg[2], 3.9);
        var under = Hover(Jg[3], 6.8);
        Check("смены через 2,9 секунды — держим", On(under, Jg[2]), Held(under));
        w.Reset();
        Hover(Jg[0], 0); Hover(Jg[1], 1); Hover(Jg[2], 4);
        var edge = Hover(Jg[3], 7);
        Check($"промежуток ровно {AllyWaver.FastGap.TotalSeconds:0} секунды — не метания", edge is null, Held(edge));
        w.Reset();
        for (var i = 0; i < 8; i++) Hover(Jg[i % 4], i * 3.5);
        Check("смена раз в 3,5 секунды — не метания", Hover(Jg[1], 30) is null, "");

        w.Reset();
        for (var i = 0; i < 6; i++) w.Observe(Slots(0, Jg[i % 4], false, mine: true), t0.AddSeconds(i * 0.3));
        Check("свой слот не в счёт", w.Observe(Slots(0, Jg[1], false, mine: true), t0.AddSeconds(2)) is null, "");

        w.Reset();
        for (var i = 0; i < 8; i++) Hover(i % 2 == 0 ? Jg[0] : 0, i * 0.3);
        Check("навёл, снял, навёл того же — не метания", Hover(Jg[0], 3) is null, "");

        // Свой ход: наведённый стоит в championId ещё до лока — это не лок.
        w.Reset();
        Hover(Jg[0], 0, locked: true, inTurn: true);
        Hover(Jg[1], 0.3, locked: true, inTurn: true);
        Hover(Jg[2], 0.6, locked: true, inTurn: true);
        var turn = Hover(Jg[3], 0.9, locked: true, inTurn: true);
        Check("щёлкает в свой ход (чемпион в championId) — тоже держим", On(turn, Jg[2]), Held(turn));
        var done = Hover(Jg[3], 2, locked: true);
        Check("ход кончился с чемпионом — залочил, отпускаем", done is null, Held(done));

        w.Reset();
        Check("новый драфт — с чистого листа", Hover(Jg[0], 100) is null && w.NextCalm == DateTime.MaxValue, "");
    }

    /// Мой слот 0 (мид, пусто), союзник в ячейке cell наводит champ или держит
    /// его в championId (locked): залочил, а если inTurn — это его ход и наведение.
    private static DraftState Slots(int cell, int champ, bool locked, bool mine = false, bool inTurn = false)
    {
        var team = Enumerable.Range(0, 5).Select(i => new DraftPlayer(i, 0, 0, "", i == 0)).ToList();
        team[cell] = new DraftPlayer(cell, locked ? champ : 0, locked ? 0 : champ, "", mine);
        return Draft(team, []) with { ActiveCells = inTurn ? [cell] : [] };
    }

    // ── 2. Отпечаток драфта ─────────────────────────────────────────────────

    private static void Hash()
    {
        Console.WriteLine();
        Console.WriteLine("отпечаток драфта:");
        var m = typeof(OverlayWindow).Assembly.GetType("Program")!
            .GetMethod("DraftHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        string H(DraftState s) => (string)m.Invoke(null, [s])!;
        var d = Slots(Cell, Jg[3], false);
        var held = d with { HeldPicks = new Dictionary<int, int> { [Cell] = Jg[2] } };
        var other = d with { HeldPicks = new Dictionary<int, int> { [Cell] = Jg[1] } };
        Check("начали или перестали держать — отпечаток другой", H(d) != H(held), "");
        Check("держим на другом — тоже другой", H(held) != H(other), "");
    }

    // ── 3. Будильник сокета ─────────────────────────────────────────────────

    private static async Task Wake()
    {
        Console.WriteLine();
        Console.WriteLine("будильник сокета (подставной WebSocket):");
        using var server = new FakeWs();
        await using var socket = new LcuEventSocket(server.Credentials);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await socket.ConnectAsync(cts.Token);
        await using var events = socket.ReadEventsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var sw = Stopwatch.StartNew();
        Task<bool>? pending = null;

        // Ждать следующее событие не дольше timeoutMs. Не дождались — чтение
        // остаётся висеть (как в цикле программы) и отдаст своё в следующий раз.
        async Task<(LcuEvent? Ev, long Ms)> Next(int timeoutMs)
        {
            var start = sw.ElapsedMilliseconds;
            pending ??= events.MoveNextAsync().AsTask();
            if (await Task.WhenAny(pending, Task.Delay(timeoutMs)) != pending)
                return (null, sw.ElapsedMilliseconds - start);
            var ok = await pending;
            pending = null;
            return (ok ? events.Current : null, sw.ElapsedMilliseconds - start);
        }

        socket.WakeAt(DateTime.UtcNow.AddMilliseconds(400));
        var (w1, ms1) = await Next(3000);
        Check("без единого события будильник приходит в срок", w1?.Uri == LcuEventSocket.WakeUri && ms1 is >= 350 and < 1500,
              $"{w1?.Uri ?? "ничего"} через {ms1} мс");

        socket.WakeAt(DateTime.UtcNow.AddMilliseconds(800));
        _ = Task.Delay(200).ContinueWith(_ => server.Send("/lol-champ-select/v1/session", "{\"n\":1}"));
        var (e1, _) = await Next(3000);
        Check("событие клиента до срока приходит как пришло", e1?.Uri == "/lol-champ-select/v1/session"
              && e1.Data.GetProperty("n").GetInt32() == 1, e1?.Uri ?? "ничего");
        var (w2, ms2) = await Next(3000);
        Check("и будильник после него всё равно звонит в свой срок", w2?.Uri == LcuEventSocket.WakeUri && ms2 < 1200,
              $"{w2?.Uri ?? "ничего"} через {ms2} мс");

        socket.WakeAt(DateTime.UtcNow.AddMilliseconds(300));
        socket.WakeAt(DateTime.MaxValue);
        var (w3, _) = await Next(900);
        Check("снятый будильник не звонит", w3 is null, w3?.Uri ?? "");

        // Чтение, оставшееся висеть, отдаёт событие.
        server.Send("/lol-champ-select/v1/session", "{\"n\":2}");
        var (e2, _) = await Next(3000);
        Check("после будильников события идут дальше", e2?.Data.GetProperty("n").GetInt32() == 2, e2?.Uri ?? "ничего");
        cts.Cancel();
        try { if (pending is not null) await pending; } catch { /* отменили */ }
    }

    // ── 4. Движок ───────────────────────────────────────────────────────────

    private static void Engine(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("подбор (мид; союзники: Гарен топ, Джинкс бот залочены, лес щёлкает; враги Зед, Ясуо):");
        var held = new Dictionary<int, int> { [Cell] = Jg[2] };

        var plain = Jg[..4].Select(c => engine.Recommend(Mid(c), 6)).ToList();
        for (var i = 0; i < 4; i++)
            Console.WriteLine($"  лес навёл {Name(Jg[i]),-10}: {NameList(Ids(plain[i]))}");
        var jumps = plain.Skip(1).Count(p => !Ids(p).SequenceEqual(Ids(plain[0])));
        Check("без удержания центр меняется вслед за наведением (иначе проверять нечего)", jumps > 0,
              $"другой список у {jumps} из 3");

        var want = engine.Recommend(Mid(Jg[2]), 6);
        var sw = Stopwatch.StartNew();
        var all = Jg.Append(0).Select(c => engine.Recommend(Mid(c) with { HeldPicks = held }, 6)).ToList();
        var ms = sw.ElapsedMilliseconds;
        Check("держим на Амуму — на любом его наведении и на пустом центр как при Амуму, до знака",
              all.All(r => Same(r, want)), NameList(Ids(all[0])));
        Check("и по одному кандидату (наведение мыши на карточку) — те же числа",
              Ids(want).All(id => Same(engine.Recommend(Mid(Jg[4]) with { HeldPicks = held }, 1, [id]),
                                       engine.Recommend(Mid(Jg[2]), 1, [id]))), "");
        var inTurn = Mid(Jg[4], locked: true) with { ActiveCells = [Cell], HeldPicks = held };
        Check("в свой ход (чемпион в championId) — так же", Same(engine.Recommend(inTurn, 6), want), "");
        var lockedState = Mid(Jg[4], locked: true);
        Check("залочивший — по своему чемпиону, удержание забыто",
              Same(engine.Recommend(lockedState with { HeldPicks = held }, 6), engine.Recommend(lockedState, 6)), "");
        Console.WriteLine($"  время: шесть подборов с удержанием — {ms} мс");
    }

    /// Мид; Гарен (топ) и Джинкс (бот) залочены, лес навёл forest; враги Зед и Ясуо.
    private static DraftState Mid(int forest, bool locked = false)
    {
        var team = new List<DraftPlayer>
        {
            new(0, 0, 0, "middle", true),
            new(Cell, locked ? forest : 0, locked ? 0 : forest, "jungle", false),
            new(2, 86, 0, "top", false),
            new(3, 222, 0, "bottom", false),
            new(4, 0, 0, "utility", false),
        };
        var their = new List<DraftPlayer> { new(5, 238, 0, "", false), new(6, 157, 0, "", false) };
        return Draft(team, their);
    }

    private static DraftState Draft(List<DraftPlayer> team, List<DraftPlayer> their) =>
        new(MyTeam: team, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: team[0], MyPosition: team[0].Position, DirectOpponent: null, ExposedToCounter: true,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);

    private static bool Same(IReadOnlyList<Recommendation> a, IReadOnlyList<Recommendation> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.ChampionId == x.Second.ChampionId
                                                && x.First.Score == x.Second.Score
                                                && x.First.SynergyDelta == x.Second.SynergyDelta
                                                && x.First.Reasons.SequenceEqual(x.Second.Reasons));

    private static List<int> Ids(IEnumerable<Recommendation> r) => [.. r.Select(x => x.ChampionId)];

    /// Имена чемпионов в выводе: кэш Data Dragon игрока копируем к себе.
    private static void Names(string playerDir)
    {
        var from = Path.Combine(playerDir, "ddragon");
        if (Directory.Exists(from))
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static string Name(int id) => DataDragon.Name(id);
    private static string NameList(IEnumerable<int> ids) => string.Join(", ", ids.Select(Name));

    private static void Check(string what, bool ok, string detail)
    {
        if (!ok) _fails++;
        Console.WriteLine($"  {(ok ? "ок  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    }

    /// <summary>
    /// Подставной WebSocket клиента: TLS с самоподписанным сертификатом на
    /// 127.0.0.1, как у настоящего LCU, одно соединение. Шлёт события WAMP
    /// (<c>[8,"OnJsonApiEvent",{…}]</c>) по команде проверки, присланное
    /// программой (подписка, пинги) читает и выбрасывает.
    /// </summary>
    private sealed class FakeWs : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _cert;
        private readonly TaskCompletionSource<WebSocket> _ws = new();

        public LcuCredentials Credentials { get; }

        public FakeWs()
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
            _listener.Start();
            Credentials = new LcuCredentials("LeagueClient", 0, ((IPEndPoint)_listener.LocalEndpoint).Port, "test", "https");
            _ = Task.Run(Serve);
        }

        private async Task Serve()
        {
            var c = await _listener.AcceptTcpClientAsync();
            var ssl = new SslStream(c.GetStream());
            await ssl.AuthenticateAsServerAsync(_cert);
            var head = new StringBuilder();
            var one = new byte[1];
            while (!head.ToString().EndsWith("\r\n\r\n") && await ssl.ReadAsync(one) == 1) head.Append((char)one[0]);
            var key = head.ToString().Split("\r\n")
                .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))[18..].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n"));
            var ws = WebSocket.CreateFromStream(ssl, new WebSocketCreationOptions { IsServer = true });
            _ws.SetResult(ws);
            // Закрытие — ответным закрытием: программа при выходе ждёт его без
            // срока (CloseAsync), и без ответа проверка висела бы на Dispose.
            var buf = new byte[4096];
            try
            {
                while (ws.State == WebSocketState.Open)
                    if ((await ws.ReceiveAsync(buf, CancellationToken.None)).MessageType == WebSocketMessageType.Close)
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { /* оборвали */ }
        }

        public void Send(string uri, string data)
        {
            var ws = _ws.Task.GetAwaiter().GetResult();
            var msg = $"[8,\"OnJsonApiEvent\",{{\"uri\":\"{uri}\",\"eventType\":\"Update\",\"data\":{data}}}]";
            ws.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, CancellationToken.None)
              .GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            _cert.Dispose();
        }
    }
}
