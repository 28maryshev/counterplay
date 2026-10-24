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
/// - одна игра, записанная двумя компьютерами по-разному (по концу и по
///   началу), остаётся одной записью — с LP того, кто её видел;
/// - игры одного аккаунта, легшие при смене аккаунта в журнал другого,
///   убираются и не возвращаются синхронизацией;
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
        // Компьютеры одного человека: пароль — его (под ним едет всё).
        SyncPassword.Set("проверка-слияния", Me);
        SyncClient.Pulled = files => { lock (_pulled) _pulled.AddRange(files); };
        try
        {
            PairsStayCleaned().GetAwaiter().GetResult();
            SettingsKeepOwnWindows().GetAwaiter().GetResult();
            StatusLine().GetAwaiter().GetResult();
            SameGameRule();
            SameGameSync().GetAwaiter().GetResult();
            ForeignRule();
            ForeignSync().GetAwaiter().GetResult();
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
        SyncPassword.Set("новый-пароль-" + Guid.NewGuid().ToString("N"), Me);
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

    // ── 5. Та же игра с двух компьютеров ────────────────────────────────────

    /// <summary>
    /// Как у владельца 14 октября: компьютер А играл и записал игры по их концу
    /// с LP; новый компьютер Б взял те же игры из истории клиента со временем
    /// начала и без LP. После синхронизации каждая игра лежала дважды, и
    /// «последние пять» показывали копии без LP.
    /// </summary>
    private static void SameGameRule()
    {
        Console.WriteLine("\nта же игра — правило (по истории клиента):");
        long s1 = Now - 86400, s2 = s1 + 2400, s3 = s1 - 86400, s4 = s1 + 7200, s5 = s4 + 2400;
        const int Jinx = 222, Lux = 99, Ahri = 103;
        var games = new List<SessionTracker.GameRef>
        {
            // А: записи по концу игры, номеров нет (до 1.3.63).
            new("solo", 0, s1 + 2280, Jinx, true, null, null),
            new("solo", 1, s2 + 2200, Jinx, true, null, null),   // подряд тот же чемпион и исход
            // Две игры, пока программа была закрыта: обе записаны одним «сейчас».
            new("solo", 2, s5 + 4000, Ahri, true, null, null),
            new("solo", 3, s5 + 4000, Ahri, true, null, null),
            // Б: из истории клиента, время начала.
            new("solo", 4, s1, Jinx, true, null, null),
            new("solo", 5, s2, Jinx, true, null, null),
            new("solo", 6, s3, Lux, false, null, null),          // этой игры у А нет
        };
        var history = new[]
        {
            new SessionTracker.GameAnchor(1, s1, "solo", Jinx, true),
            new SessionTracker.GameAnchor(2, s2, "solo", Jinx, true),
            new SessionTracker.GameAnchor(3, s3, "solo", Lux, false),
            new SessionTracker.GameAnchor(4, s4, "solo", Ahri, true),
            new SessionTracker.GameAnchor(5, s5, "solo", Ahri, true),
        };
        var plan = SessionTracker.SameGames(games, history);
        var dropped = plan.SelectMany(p => p.Drop).ToHashSet();
        var kept = games.Where(g => !dropped.Contains(g.Index)).ToList();

        Check("копии из истории убраны, остались записи с концом игры",
              dropped.SetEquals([4, 5]), string.Join(",", dropped.Order()));
        Check("две игры подряд на одном чемпионе не слились в одну",
              plan.Any(p => p.Id == 1 && p.Keep == 0) && plan.Any(p => p.Id == 2 && p.Keep == 1),
              string.Join(" ", plan.Select(p => $"{p.Id}→{p.Keep}")));
        Check("игра, которая есть только у одного, осталась",
              kept.Any(g => g.Index == 6) && plan.Any(p => p.Id == 3 && p.Keep == 6), "");
        Check("две игры одним «сейчас» не съели друг друга",
              kept.Count(g => g.ChampionId == Ahri) == 2, $"{kept.Count(g => g.ChampionId == Ahri)}");
    }

    /// <summary>
    /// То же через синхронизацию. На сервере — записи Б: старые, из истории
    /// (время начала, без номера), и одна новая — игру А Б увидел в истории
    /// через сутки и записал своим «сейчас» с чужим LP. У А — его записи с
    /// номерами. После слияния — по одной записи на игру, LP от очевидца.
    /// </summary>
    private static async Task SameGameSync()
    {
        Console.WriteLine("\nта же игра — через синхронизацию:");
        long s1 = Now - 3 * 86400, s2 = s1 + 2400, s3 = s1 - 86400;

        SyncPassword.Set("та-же-игра-" + Guid.NewGuid().ToString("N"), Me);
        WipeLocal();
        Write("session.json", GamesSession(
            G(s1, 222, true, null, null, null),
            G(s2, 222, true, null, null, null),
            G(s3, 99, false, null, null, null),
            G(s2 + 86400, 222, true, 38, 2, s2)));
        var up = await SyncClient.SyncAsync(Me);
        Check("Б выложил свои записи", up.Ok, up.Message);

        WipeLocal();
        Write("session.json", GamesSession(
            G(s1 + 2280, 222, true, 19, 1, s1),
            G(s2 + 2200, 222, true, 19, 2, s2)));
        var r = await SyncClient.SyncAsync(Me);
        var games = Account()["Queues"]!["solo"]!["Games"]!.AsArray();
        var lp = games.Select(g => (int?)g!["Lp"]).ToList();
        Check("по одной записи на игру", games.Count == 3, $"{games.Count}");
        Check("у игр А осталось LP очевидца", lp.Count(x => x == 19) == 2 && !lp.Contains(38),
              string.Join(" ", lp.Select(x => x?.ToString() ?? "—")));
        Check("игра Б без копии у А тоже на месте",
              games.Any(g => (int?)g!["ChampionId"] == 99), "");
        Check("игры по времени", games.Select(g => (long)g!["Ts"]!).SequenceEqual(
              games.Select(g => (long)g!["Ts"]!).Order()), "");

        var again = await SyncClient.SyncAsync(Me);
        Check("следующий проход копий не возвращает", again.Pulled == 0
              && Account()["Queues"]!["solo"]!["Games"]!.AsArray().Count == 3,
              $"файлов {again.Pulled}");
    }

    private static JsonObject G(long ts, int champ, bool win, int? lp, long? id, long? start)
    {
        var o = new JsonObject { ["Ts"] = ts, ["ChampionId"] = champ, ["Win"] = win, ["Wr"] = 50.0 };
        if (lp is not null) o["Lp"] = lp;
        if (id is not null) o["Id"] = id;
        if (start is not null) o["Start"] = start;
        return o;
    }

    private static string GamesSession(params JsonObject[] games) => new JsonObject
    {
        ["Accounts"] = new JsonObject
        {
            [Me] = new JsonObject
            {
                ["Queues"] = new JsonObject
                {
                    ["solo"] = new JsonObject { ["Games"] = new JsonArray(games.Cast<JsonNode>().ToArray()) }
                }
            }
        }
    }.ToJsonString();

    // ── 6. Чужие игры в журнале другого аккаунта ────────────────────────────

    /// <summary>
    /// Как у владельца 14 октября: при входе в клиент на втором компьютере его
    /// последние игры легли в журнал аккаунта, под которым там заходили раньше, —
    /// разом, одним временем, без LP, а номера игр — в «виденные» того аккаунта.
    /// </summary>
    private static void ForeignRule()
    {
        Console.WriteLine("\nчужие игры — правило:");
        long t = Now - 3600, s = Now - 5 * 86400;
        var mine = new[]
        {
            new SessionTracker.GameAnchor(11, s, "solo", 37, true),
            new SessionTracker.GameAnchor(12, s + 3000, "solo", 37, true),
            new SessionTracker.GameAnchor(13, s + 6000, "solo", 161, false),
            new SessionTracker.GameAnchor(14, s + 9000, "flex", 22, true),
            new SessionTracker.GameAnchor(15, s + 99000, "solo", 99, true),   // общая игра в дуо
        };
        var theirs = new List<SessionTracker.GameRef>
        {
            // Свои игры того аккаунта — давние.
            new("normal", 0, Now - 90 * 86400, 876, false, null, null),
            new("solo", 0, Now - 80 * 86400, 37, true, null, null),
            // Пачка чужих: одно время записи.
            new("solo", 1, t, 37, true, null, null),
            new("solo", 2, t, 37, true, null, null),
            new("solo", 3, t, 161, false, null, null),
            new("flex", 0, t, 22, true, null, null),
            // Своя игра позже общей игры в дуо, на том же чемпионе и с тем же
            // исходом, что был у напарника, — не чужая.
            new("solo", 4, s + 99000 + 86400, 99, true, null, null),
        };
        var drop = SessionTracker.ForeignGames(theirs, new HashSet<long> { 11, 12, 13, 14, 15 }, mine);
        Check("пачка чужих игр найдена целиком", drop.SetEquals([("solo", 1), ("solo", 2), ("solo", 3), ("flex", 0)]),
              string.Join(" ", drop.Select(d => $"{d.Queue}:{d.Index}")));
        Check("свои игры того аккаунта не тронуты",
              !drop.Contains(("solo", 0)) && !drop.Contains(("normal", 0)), "");
        Check("своя игра после общей в дуо не принята за чужую", !drop.Contains(("solo", 4)), "");

        var two = SessionTracker.ForeignGames(theirs.Where(g => g.Queue != "flex" && g.Index != 3).ToList(),
                                              new HashSet<long> { 11, 12 }, mine);
        Check("две совпавшие записи — ещё не пачка", two.Count == 0, $"{two.Count}");
    }

    /// <summary>
    /// Через синхронизацию: на сервере журнал второго компьютера с чужой пачкой
    /// у аккаунта «другой»; у этого компьютера записи владельца с номерами.
    /// После слияния пачки нет и следующий проход её не возвращает.
    /// </summary>
    private static async Task ForeignSync()
    {
        Console.WriteLine("\nчужие игры — через синхронизацию:");
        const string Other = "TEST-syncmerge-other-0000000000000000000000000000000000000000000000";
        long t = Now - 3600, s = Now - 5 * 86400;

        SyncPassword.Set("чужие-игры-" + Guid.NewGuid().ToString("N"), Me);
        WipeLocal();
        Write("session.json", TwoAccounts(
            mine: [G(s + 2000, 37, true, 19, 11, s), G(s + 5000, 37, true, 20, 12, s + 3000),
                   G(s + 8000, 161, false, -21, 13, s + 6000)],
            other: [G(Now - 80 * 86400, 37, true, null, null, null),
                    G(t, 37, true, null, null, null), G(t, 37, true, null, null, null),
                    G(t, 161, false, null, null, null)],
            otherSeen: [11, 12, 13]));
        var up = await SyncClient.SyncAsync(Me);
        Check("второй компьютер выложил журнал с пачкой", up.Ok, up.Message);

        // Этот компьютер слился — и после следующего прохода пачки нет.
        var r = await SyncClient.SyncAsync(Me);
        var other = OtherGames(Other);
        Check("чужая пачка убрана, своя игра того аккаунта на месте",
              other.Count == 1 && other[0] == Now - 80 * 86400, $"{other.Count}");
        Check("игры владельца целы", Account()["Queues"]!["solo"]!["Games"]!.AsArray().Count == 3, "");

        var again = await SyncClient.SyncAsync(Me);
        Check("следующий проход пачку не возвращает", again.Pulled == 0 && OtherGames(Other).Count == 1,
              $"файлов {again.Pulled}, игр {OtherGames(Other).Count}");

        string TwoAccounts(JsonObject[] mine, JsonObject[] other, long[] otherSeen) => new JsonObject
        {
            ["Accounts"] = new JsonObject
            {
                [Me] = new JsonObject
                {
                    ["Queues"] = new JsonObject
                    { ["solo"] = new JsonObject { ["Games"] = new JsonArray(mine.Cast<JsonNode>().ToArray()) } }
                },
                [Other] = new JsonObject
                {
                    ["SeenGames"] = new JsonArray(otherSeen.Select(x => (JsonNode)x).ToArray()),
                    ["Queues"] = new JsonObject
                    { ["solo"] = new JsonObject { ["Games"] = new JsonArray(other.Cast<JsonNode>().ToArray()) } }
                }
            }
        }.ToJsonString();
    }

    private static List<long> OtherGames(string puuid) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Dir, "session.json")))!["Accounts"]![puuid]!
            ["Queues"]!["solo"]!["Games"]!.AsArray().Select(g => (long)g!["Ts"]!).ToList();

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
