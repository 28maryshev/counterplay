using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Counterplay;
using Button = System.Windows.Controls.Button;

namespace Counterplay.Tests;

/// <summary>
/// Синхронизация против СВОЕГО сервера: что именно получается после слияния.
///
/// <c>tests/Sync</c> гоняет настоящий сервер и держит главное — ничего не
/// пропадает. Здесь — то, что без сети проверяется точнее:
///
/// - чистка связок не отменяется синхронизацией. Журнал после каждой игры
///   забывает лишние связки (больше 400 и разовые старше месяца), а слияние
///   складывало их с копией на сервере и возвращало обратно: 400 → 461 после
///   каждой игры, копия на сервере только росла;
/// - игры с другого компьютера встают по времени, а не в конец: по порядку
///   списка считаются «последние 5» и график рейтинга;
/// - проход, после которого ничего не пришло, не переписывает файлы;
/// - пришедшие настройки берутся, но место окон остаётся своим;
/// - строка состояния под кнопкой знает, когда ходили, что пришло и почему
///   не вышло, а ввод пароля сразу запускает проход.
///
/// Папка своя (<c>AppPaths.RootOverride</c>), сервер свой
/// (<c>SyncClient.EndpointOverride</c>): данных игрока проверка не касается.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Me = "TEST-syncmerge-0000000000000000000000000000000000000000000000000000";

    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string Dir => AppPaths.Root;

    /// Какие файлы синхронизация велела перечитать (<see cref="SyncClient.Pulled"/>).
    private static readonly List<string> _pulled = [];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SyncMerge");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        using var server = new FakeSyncServer();
        SyncClient.EndpointOverride = server.Url;
        SyncPassword.Set("проверка-слияния");
        SyncClient.Pulled = files => { lock (_pulled) _pulled.AddRange(files); };
        try
        {
            PairsStayCleaned().GetAwaiter().GetResult();
            SettingsKeepOwnWindows().GetAwaiter().GetResult();
            StatusLine().GetAwaiter().GetResult();
            PasswordStartsSync();

            // Сеть пропала — причина видна, прошлый удачный проход не забыт.
            server.Stop();
            var down = SyncClient.NowAsync(Me, "проверка").GetAwaiter().GetResult();
            var s = SyncStatus.Current;
            Console.WriteLine();
            Console.WriteLine("без сети:");
            Check("неудача записана с причиной", !down.Ok && s is { Failed: true, FailKey: "sync.failedNet" },
                  s.FailKey ?? "нет");
            Check("удачный проход до неё не забыт", s.OkAt > 0, "");
        }
        finally
        {
            SyncClient.EndpointOverride = null;
            SyncClient.Pulled = null;
            try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: слияние держит порядок и чистку, строка состояния говорит правду"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Чистка связок, порядок игр ───────────────────────────────────────

    /// <summary>
    /// Компьютер Б выложил старое состояние: 461 связка и разовая связка
    /// сорокадневной давности. Компьютер А у себя уже почистил: 399 самых свежих
    /// и одна общая. После слияния у А должно остаться 400, а не 463 — и
    /// следующая чистка не должна отменяться следующим проходом.
    /// </summary>
    private static async Task PairsStayCleaned()
    {
        Console.WriteLine("чистка связок и порядок игр:");

        // ── Б ──
        WipeLocal();
        var b = Session(
            games: [Now - 4 * 86400, Now - 2 * 86400],
            pairs: Enumerable.Range(0, 461).Select(i => (Key(i), PairTimes(i)))
                .Append(("old1", new[] { Now - 40 * 86400L }))
                .Append(("shared", new[] { Now - 300L })));
        Write("session.json", b);
        var up = await SyncClient.SyncAsync(Me);
        Check("Б выложил своё", up.Ok, up.Message);

        // ── А ──
        WipeLocal();
        var a = Session(
            games: [Now - 5 * 86400, Now - 3 * 86400],
            pairs: Enumerable.Range(0, 399).Select(i => (Key(i), PairTimes(i)))
                .Append(("shared", new[] { Now - 600L, Now - 100L })));
        Write("session.json", a);
        var first = await SyncClient.SyncAsync(Me);
        Check("А слился", first.Ok, first.Message);

        var pairs = Pairs();
        Check("связок не больше 400", pairs.Count <= 400, $"{pairs.Count}");
        Check("почищенные не вернулись",
              !Enumerable.Range(399, 62).Any(i => pairs.ContainsKey(Key(i))),
              string.Join(", ", Enumerable.Range(399, 62).Where(i => pairs.ContainsKey(Key(i))).Take(3).Select(Key)));
        Check("разовая старше месяца не вернулась", !pairs.ContainsKey("old1"), "");
        var shared = pairs.GetValueOrDefault("shared")?["Won"]?.AsArray().Select(x => (long)x!).ToList() ?? [];
        Check("отметки общей связки сложились и идут по времени",
              shared.SequenceEqual([Now - 600, Now - 300, Now - 100]),
              string.Join(" ", shared.Select(t => Now - t)));

        var games = Games();
        Check("игры Б доехали", games.Count == 4, $"{games.Count}");
        Check("игры идут по времени, а не дописаны в конец",
              games.SequenceEqual(games.OrderBy(t => t)),
              string.Join(" ", games.Select(t => (Now - t) / 86400)));

        // ── А сыграл ещё, журнал снова почистил связки ──
        Prune(400);
        var second = await SyncClient.SyncAsync(Me);
        Check("следующий проход чистку не отменяет", Pairs().Count <= 400, $"{Pairs().Count}");
        Check("после чистки с сервера ничего не «пришло»", second.Pulled == 0, $"файлов {second.Pulled}");

        var third = await SyncClient.SyncAsync(Me);
        Check("проход без новостей файлов не трогает", third.Pulled == 0, $"файлов {third.Pulled}");
    }

    // ── 2. Настройки: чужие, но окна свои ───────────────────────────────────

    /// <summary>
    /// Б поменял график на 30 дней и стоит окном на втором мониторе. А берёт
    /// его настройки (они свежее), но место окна оставляет своё: у А другого
    /// монитора может не быть.
    /// </summary>
    private static async Task SettingsKeepOwnWindows()
    {
        Console.WriteLine("\nнастройки:");

        WipeLocal();
        Write("ui.json", """{ "ChartDays": 30, "DraftLeft": 2560, "DraftTop": 10, "Rev": 1 }""");
        var up = await SyncClient.SyncAsync(Me);
        Check("Б выложил настройки", up.Ok, up.Message);

        WipeLocal();
        Write("ui.json", """{ "ChartDays": 90, "DraftLeft": 100, "DraftTop": 20, "Rev": 1 }""");
        File.SetLastWriteTimeUtc(Path.Combine(Dir, "ui.json"), DateTime.UtcNow.AddHours(-1));
        var r = await SyncClient.NowAsync(Me, "проверка");
        var ui = JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "ui.json")))!;
        Check("настройки Б доехали", (int?)ui["ChartDays"] == 30, $"{ui["ChartDays"]}");
        Check("окно стоит, где стояло у А", (int?)ui["DraftLeft"] == 100 && (int?)ui["DraftTop"] == 20,
              $"{ui["DraftLeft"]};{ui["DraftTop"]}");
        Check("строка состояния знает, что пришли настройки", SyncStatus.Current.GotSettings, "");
        Check("и что пулов не приходило", !SyncStatus.Current.GotPools, "");
        Check("про файл сказано тем, кто перечитывает", _pulled.Contains("ui.json"),
              string.Join(",", _pulled));
    }

    // ── 3. Строка состояния ────────────────────────────────────────────────

    /// <summary>
    /// Что видно под кнопкой: удачный проход, пустой сервер, пришедшие игры,
    /// неудача, «профиль не определён» и сброс при смене пароля.
    /// </summary>
    private static async Task StatusLine()
    {
        Console.WriteLine("\nстрока состояния:");

        // Новый пароль — новая строка на сервере, пустая.
        SyncPassword.Set("новый-пароль-" + Guid.NewGuid().ToString("N"));
        Check("смена пароля забывает прежнее состояние", SyncStatus.Current.OkAt == 0, "");

        WipeLocal();
        Write("session.json", Session(games: [Now - 3600], pairs: []));
        var first = await SyncClient.NowAsync(Me, "проверка");
        var s = SyncStatus.Current;
        Check("удачный проход записан", first.Ok && s.OkAt > 0 && !s.Failed, first.Message);
        Check("пустой сервер замечен: компьютер первый с этим паролем", s.WasEmpty, "");

        // Второй компьютер сыграл две игры.
        WipeLocal();
        Write("session.json", Session(games: [Now - 1800, Now - 900], pairs: []));
        await SyncClient.SyncAsync(Me);
        WipeLocal();
        Write("session.json", Session(games: [Now - 3600], pairs: []));
        _pulled.Clear();
        var got = await SyncClient.NowAsync(Me, "проверка");
        s = SyncStatus.Current;
        Check("пришли две игры с другого компьютера", got.Got?.Games == 2 && s.GotGames == 2,
              $"{got.Got?.Games} / {s.GotGames}");
        Check("сервер уже не пуст", !s.WasEmpty, "");
        Check("журнал игр перечитывается", _pulled.Contains("session.json"), string.Join(",", _pulled));

        var quiet = await SyncClient.NowAsync(Me, "проверка");
        Check("проход без новостей не стирает, что приходило раньше",
              quiet.Got?.Any == false && SyncStatus.Current.GotGames == 2, $"{SyncStatus.Current.GotGames}");

        var noAcc = await SyncClient.NowAsync(null, "проверка");
        Check("без профиля — причина «зайди в клиент»",
              !noAcc.Ok && SyncStatus.Current is { Failed: true, FailKey: "sync.noAccount" },
              SyncStatus.Current.FailKey ?? "нет");
        Check("удачный проход при этом не забыт", SyncStatus.Current.OkAt > 0, "");
    }

    // ── 4. Пароль в окне — и сразу проход ──────────────────────────────────

    /// <summary>
    /// Ряд синхронизации в невидимом окне: ввели пароль — проход идёт сразу,
    /// не дожидаясь конца игры, и строка под кнопкой меняется сама.
    /// </summary>
    private static void PasswordStartsSync()
    {
        Console.WriteLine("\nввод пароля:");

        SyncPassword.Set("");
        WipeLocal();
        Write("session.json", Session(games: [Now - 600], pairs: []));
        // Ряд берёт профиль оттуда же, откуда программа: у пулов.
        PoolStore.SetAccount(Me, "проверка");
        var before = SyncStatus.Current.OkAt;

        var row = SyncBadge.Row(() => SyncPassword.Set("пароль-из-окна-" + Guid.NewGuid().ToString("N")));
        // Фон как у окна настроек — для снимка: текст светлый.
        var frame = new Border
        {
            Child = row, Padding = new Thickness(14),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x12, 0x1A, 0x24))
        };
        var w = new Window { Content = frame, SizeToContent = SizeToContent.WidthAndHeight };
        ShowHidden(w);
        try
        {
            var add = Find<Button>(row).FirstOrDefault();
            Check("без пароля — кнопка «добавить пароль»",
                  (string?)add?.Content == Loc.T("sync.add"), $"{add?.Content}");
            add?.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            // Проход идёт в фоне — ждём его, прокачивая очередь окна.
            var until = DateTime.UtcNow.AddSeconds(20);
            while ((SyncStatus.Current.OkAt == before || SyncStatus.Running) && DateTime.UtcNow < until)
            {
                Pump();
                Thread.Sleep(50);
            }
            Pump();

            Check("пароль сразу запустил синхронизацию", SyncStatus.Current.OkAt > before, "");
            var texts = Find<TextBlock>(row).Select(t => t.Text).ToList();
            var ok = Loc.T("sync.ok", Loc.T("sync.today", ""));
            Check("под кнопкой — «синхронизировано сегодня в …»",
                  texts.Any(t => t.StartsWith(ok.TrimEnd(), StringComparison.Ordinal)),
                  string.Join(" | ", texts.Where(t => t.Length > 2)));

            // CP_SNAP — папка для снимка ряда в PNG, посмотреть глазами.
            if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 } snap)
            {
                frame.UpdateLayout();
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight),
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(frame);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                Directory.CreateDirectory(snap);
                using var f = File.Create(Path.Combine(snap, "sync-row.png"));
                png.Save(f);
            }
        }
        finally { w.Close(); }
    }

    private static List<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        var res = new List<T>();
        void Walk(DependencyObject d)
        {
            if (d is T t) res.Add(t);
            foreach (var c in LogicalTreeHelper.GetChildren(d))
                if (c is DependencyObject x) Walk(x);
        }
        Walk(root);
        return res;
    }

    /// Выполнить всё, что встало в очередь окна.
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
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

    // ── подручное ──────────────────────────────────────────────────────────

    private static string Key(int i) => $"mate{i}|solo|1|2";

    /// Две игры: разовой такая связка не считается, а свежесть падает с номером.
    private static long[] PairTimes(int i) => [Now - 2000 - i * 600L, Now - 1000 - i * 600L];

    private static string Session(long[] games, IEnumerable<(string Key, long[] Won)> pairs)
    {
        var p = new JsonObject();
        foreach (var (key, won) in pairs)
            p[key] = new JsonObject
            {
                ["Won"] = new JsonArray(won.Select(t => (JsonNode)t).ToArray()),
                ["Lost"] = new JsonArray(),
                ["Name"] = "проверка"
            };
        return new JsonObject
        {
            ["Accounts"] = new JsonObject
            {
                [Me] = new JsonObject
                {
                    ["Queues"] = new JsonObject
                    {
                        ["solo"] = new JsonObject
                        {
                            ["Games"] = new JsonArray(games.Select(t => (JsonNode)new JsonObject
                                { ["Ts"] = t, ["ChampionId"] = 1, ["Win"] = true }).ToArray())
                        }
                    },
                    ["Pairs"] = p
                }
            }
        }.ToJsonString();
    }

    private static JsonObject Account() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "session.json")))!["Accounts"]![Me]!.AsObject();

    private static Dictionary<string, JsonNode?> Pairs() =>
        Account()["Pairs"]!.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value);

    private static List<long> Games() =>
        Account()["Queues"]!["solo"]!["Games"]!.AsArray().Select(g => (long)g!["Ts"]!).ToList();

    /// Как чистка журнала после игры: оставить самые свежие.
    private static void Prune(int keep)
    {
        var path = Path.Combine(Dir, "session.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        var pairs = root["Accounts"]![Me]!["Pairs"]!.AsObject();
        long Last(JsonNode? n) => n!["Won"]!.AsArray().Select(x => (long)x!).DefaultIfEmpty().Max();
        foreach (var key in pairs.OrderByDescending(kv => Last(kv.Value)).Skip(keep)
                                 .Select(kv => kv.Key).ToList())
            pairs.Remove(key);
        File.WriteAllText(path, root.ToJsonString());
    }

    private static void Write(string name, string body) =>
        File.WriteAllText(Path.Combine(Dir, name), body);

    private static void WipeLocal()
    {
        foreach (var f in new[] { "pools.json", "ui.json", "session.json", "sync.rev" })
        {
            var p = Path.Combine(Dir, f);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
