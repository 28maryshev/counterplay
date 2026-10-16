using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Windows;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Руны и сборка после пика — и тогда, когда сеть подвела.
///
/// Владелец: «после пика не отобразились руны и предметы». В журнале
/// программы — «руны не показаны: Сона… манифест НЕ ЗАГРУЖЕН». Программу
/// запустили без интернета; список связок (что есть на сайте) грузился один
/// раз на запуске и больше не спрашивался, хотя сеть вернулась через пару
/// минут. Тот же итог 6 октября, дважды: программу открыли посреди драфта, и
/// пик опередил загрузку списка — он стоял последним в разогреве, за разбором
/// предметов, а тот шёл минуту с лишним. И ещё раз в тот же вечер: файл
/// связки не пришёл, и «данных нет» запомнилось до перезапуска.
///
/// Проверка ставит СВОЙ сервер вместо сайта и гасит его, изображая пропавшую
/// сеть. Пик подаётся ОДИН раз: в настоящем драфте после пика событий может и
/// не быть, и ждать следующего нельзя.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Что изображает сервер: сайт недоступен целиком или только файлы связок.
    private static volatile bool _down;
    private static volatile bool _champDown;

    private const int Sona = 37, Braum = 201, Pyke = 555, Lux = 99;

    private static readonly Type ProgramType = typeof(OverlayWindow).Assembly.GetType("Program")!;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к настройкам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "RunesRetry");
        Try(() => Directory.Delete(root, true));
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        _ = Task.Run(() => Serve(listener));
        RunesClient.BaseUrlOverride = $"http://localhost:{port}/v1";

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { Run(); }
        finally
        {
            RunesClient.BaseUrlOverride = null;
            try { listener.Stop(); } catch { }
            app.Shutdown();
            Try(() => Directory.Delete(root, true));
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: руны после пика доезжают, как только сеть вернулась"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var ct = CancellationToken.None;
        var w = new OverlayWindow();
        ShowHidden(w);
        Pump();

        // ── 1. Запуск без сети ────────────────────────────────────────────
        _down = true;
        RunesClient.LoadManifestAsync(ct).GetAwaiter().GetResult();
        Check("1. сайт молчит на запуске — списка связок нет", !RunesClient.ManifestLoaded, "");
        Pick(w, Sona);
        Wait(1.5);
        Check("   пик без сети — панели нет", !Shown(w, Sona), "");
        _down = false;
        var t = DateTime.UtcNow;
        var ok = WaitFor(() => Shown(w, Sona), 15);
        Check("   сеть вернулась — руны появились сами, без нового события драфта", ok,
              ok ? $"через {(DateTime.UtcNow - t).TotalSeconds:0.0} с"
                 : "так и не появились: список связок после неудачи на запуске больше не спрашивается");

        // ── 2. Пик опередил загрузку списка ───────────────────────────────
        // Программу открыли посреди драфта: сеть есть, но разогрев до списка
        // ещё не дошёл. Пик не должен ждать разогрева — спросить список сам.
        ForgetManifest();
        Pick(w, Braum);
        t = DateTime.UtcNow;
        ok = WaitFor(() => Shown(w, Braum), 3);
        Check("2. список не успел загрузиться к пику — пик загружает его сам", ok,
              ok ? $"через {(DateTime.UtcNow - t).TotalSeconds:0.0} с"
                 : "за 3 с не появились: пик не спрашивает список, ждёт разогрева");

        // ── 3. Файл связки не пришёл с первого раза ───────────────────────
        RunesClient.LoadManifestAsync(ct).GetAwaiter().GetResult();
        _champDown = true;
        // Как в драфте: кандидата подгружают заранее, ещё до пика.
        RunesClient.Prefetch([Pyke], "support", ct);
        Wait(1);
        Pick(w, Pyke);
        Wait(1.5);
        Check("3. файл связки не пришёл — панели нет", !Shown(w, Pyke), "");
        _champDown = false;
        t = DateTime.UtcNow;
        ok = WaitFor(() => Shown(w, Pyke), 15);
        Check("   сеть вернулась — руны появились сами", ok,
              ok ? $"через {(DateTime.UtcNow - t).TotalSeconds:0.0} с"
                 : "так и не появились: неудачный ответ запомнен как «данных нет» до перезапуска");

        // ── 4. Драфт кончился — повторы прекращаются ──────────────────────
        // Иначе руны прошлого драфта всплыли бы в лобби, когда сеть вернётся.
        ForgetManifest();
        _down = true;
        Pick(w, Lux);
        Wait(1.5);
        ProgramType.GetMethod("RunesDraftEnded", BindingFlags.NonPublic | BindingFlags.Static)
                   ?.Invoke(null, null);
        _down = false;
        Wait(8);
        Check("4. драфт кончился — повторы прекратились, руны не всплыли", !Shown(w, Lux),
              Shown(w, Lux) ? "панель Люкс появилась уже после конца драфта" : "");

        w.Close();
        Pump();
    }

    // ── Драфт: я в саппорте, чемпион залочен ────────────────────────────
    private static void Pick(OverlayWindow w, int champ)
    {
        var me = new DraftPlayer(0, champ, 0, "utility", true);
        var draft = new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false, MyPickActionId: 1,
            MyPickInProgress: false, ActiveCells: [], FirstPickCell: -1,
            MyBanActionId: -1, MyBanInProgress: false);
        // Как в программе: зовётся из потока сессии, без ожидания.
        var m = ProgramType.GetMethod("UpdateRunesAsync", BindingFlags.Public | BindingFlags.Static)!;
        _ = Task.Run(() => (Task)m.Invoke(null, [w, draft, CancellationToken.None])!);
    }

    /// Панель получила руны этого чемпиона.
    private static bool Shown(OverlayWindow w, int champ) =>
        Get<ChampStats>(w, "_runeStats") is { Keystones.Count: > 0 } s && s.ChampionId == champ;

    /// Как будто список связок ещё не загружался (и не спрашивался).
    private static void ForgetManifest()
    {
        var t = typeof(RunesClient);
        const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static;
        t.GetField("_available", F)!.SetValue(null, null);
        t.GetField("_manifestTriedAt", F)?.SetValue(null, DateTime.MinValue);
    }

    // ── Свой сервер вместо сайта ─────────────────────────────────────────
    private static void Serve(HttpListener l)
    {
        int[] champs = [Sona, Braum, Pyke, Lux];
        while (l.IsListening)
        {
            HttpListenerContext c;
            try { c = l.GetContext(); } catch { return; }
            try
            {
                var path = c.Request.Url!.AbsolutePath;
                string? body = null;
                if (!_down && path == "/v1/manifest.json")
                {
                    body = "{\"patch\":\"16.20\",\"available\":[" +
                           string.Join(",", champs.Select(x => $"\"{x}-support\"")) + "]," +
                           "\"mainRole\":{" + string.Join(",", champs.Select(x => $"\"{x}\":\"support\"")) + "}}";
                }
                else if (!_down && !_champDown && path.StartsWith("/v1/16.20/") && path.EndsWith("-support.json"))
                {
                    var id = int.Parse(path["/v1/16.20/".Length..].Split('-')[0]);
                    body = "{\"champion\":" + id + ",\"role\":\"support\",\"patches\":[\"16.20\"],\"games\":5000," +
                           "\"keystones\":[{\"id\":8214,\"games\":3000,\"wr\":52.0,\"pick\":60.0," +
                           "\"page\":{\"primary\":8200,\"sub\":8300,\"perks\":[8214,8226,8210,8237]," +
                           "\"secondary\":[8306,8345],\"shards\":[5008,5008,5001],\"games\":1500,\"wr\":52.1}}]," +
                           "\"spells\":[],\"builds\":[],\"items\":[]}";
                }

                if (body is null) { c.Response.StatusCode = 503; c.Response.Close(); continue; }
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Response.ContentType = "application/json";
                c.Response.ContentLength64 = bytes.Length;
                c.Response.OutputStream.Write(bytes, 0, bytes.Length);
                c.Response.Close();
            }
            catch { try { c.Response.Abort(); } catch { } }
        }
    }

    // ── Помощники ────────────────────────────────────────────────────────
    private static T? Get<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o) as T;

    private static bool WaitFor(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Pump();
            if (done()) return true;
        }
        return done();
    }

    private static void Wait(double seconds) => WaitFor(() => false, seconds);

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static void Try(Action a) { try { a(); } catch { /* нечего чистить */ } }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
