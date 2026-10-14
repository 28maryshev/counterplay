using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Драфт без роли (вслепую, своя игра). Подбору нужна линия, и центр стоял
/// заглушкой до конца драфта. Владелец (15.10): показывать там пул с учётом
/// синергии с теми, кого пикают союзники, а пока никто не пикнул — наигранных
/// за месяц.
///
/// 1. Строки на всех языках.
/// 2. Мои чемпионы за месяц на всех ролях — с ролью, на которой я играл чаще.
/// 3. Окно: никто не показал — наигранные за месяц (без игр — пул); союзник
///    показал Ясуо — пул в порядке синергии с ним, как её считает движок
///    (подбор пика); взятые и забаненные не предлагаются; клик — показ пика, плашка
///    «Выбрать» под панелью; руны моего чемпиона в нижней строке, без них —
///    тир-лист; без базы — заглушка.
///
/// История подменная (SessionTracker.PreviewGames), пул и настройки — в своей
/// папке, база матчей — настоящая, только чтение.
/// </summary>
internal static class Program
{
    private static int _fails;
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private const int Sona = 37, Ashe = 22, Amumu = 32, Malphite = 54, Garen = 86, Yuumi = 350, Yasuo = 157;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "BlindPool");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        try
        {
            Strings();
            SessionTracker.PreviewGames = History();
            Recent();

            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — окно проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Window(engine, Environment.GetEnvironmentVariable("CP_SNAP"));
            app.Shutdown();
        }
        finally
        {
            SessionTracker.PreviewGames = null;
            AppPaths.RootOverride = null;
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: драфт без роли показывает мой пул под союзников"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Строки ────────────────────────────────────────────────────────────

    private static readonly string[] NewKeys =
    [
        "blind.status", "blind.statusMonth", "blind.title", "blind.titleMonth", "blind.empty",
        "blind.synGood", "blind.synBad", "blind.tipAlly", "status.pickPhase",
    ];

    private static void Strings()
    {
        Console.WriteLine("строки на всех языках:");
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "i18n");
        if (!Directory.Exists(dir)) dir = Path.Combine(Repo(), "assets", "i18n");
        var docs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        Check("языков 15", docs.Count == 15, $"{docs.Count}");
        foreach (var key in NewKeys)
        {
            var holes = Holes(Get(docs["ru"], key));
            var bad = docs.Where(kv => Get(kv.Value, key) is not { } s || !Holes(s).SequenceEqual(holes))
                          .Select(kv => kv.Key).ToList();
            // Перевод, совпавший с английским, — забытый перевод (кроме коротких).
            var english = docs.Where(kv => kv.Key != "en" && Get(kv.Value, key) is { Length: > 12 } s
                                           && s == Get(docs["en"], key)).Select(kv => kv.Key).ToList();
            Check($"  {key}", bad.Count == 0 && english.Count == 0,
                  bad.Count > 0 ? string.Join(",", bad) : english.Count > 0 ? "по-английски: " + string.Join(",", english)
                  : $"{holes.Count} подстановки");
        }
    }

    private static List<string> Holes(string? s) =>
        Regex.Matches(s ?? "", @"\{\d\}").Select(m => m.Value).OrderBy(x => x).ToList();

    // ── 2. Мои чемпионы за месяц на всех ролях ───────────────────────────────

    /// Сона — пять игр саппортом, и одна старая (вне окна); Эш — две саппортом
    /// и одна стрелком; Амуму — две в лесу.
    private static List<SessionTracker.PreviewGame> History()
    {
        var list = new List<SessionTracker.PreviewGame>();
        void Game(double day, int champ, string role, bool win) =>
            list.Add(new("ally", "Друг", "solo", champ, 51, role, "adc", Now - (long)(day * 86400), win));
        bool[] sona = [true, true, false, true, true];
        for (int i = 0; i < sona.Length; i++) Game(1 + i, Sona, "support", sona[i]);
        Game(40, Sona, "support", true);
        Game(7, Ashe, "support", true);
        Game(8, Ashe, "support", false);
        Game(9, Ashe, "adc", true);
        Game(2.5, Amumu, "jungle", true);
        Game(3.5, Amumu, "jungle", false);
        return list;
    }

    private static void Recent()
    {
        Console.WriteLine();
        Console.WriteLine("мои чемпионы за месяц на всех ролях:");
        var r = SessionTracker.RecentChamps(null);
        Check("самые игранные первыми, старая игра не в счёт",
              r.Select(c => c.ChampionId).SequenceEqual([Sona, Ashe, Amumu]) && r[0].Games == 5,
              string.Join(", ", r.Select(c => $"{c.ChampionId}:{c.Games}")));
        Check("роль — где играл чаще: Эш саппорт (2 из 3), Амуму лес",
              r.Select(c => c.Role).SequenceEqual(["support", "support", "jungle"]),
              string.Join(", ", r.Select(c => c.Role)));
        Check("на одной роли — по-прежнему только её игры",
              SessionTracker.LaneChamps("adc", null).Select(c => c.ChampionId).SequenceEqual([Ashe]), "");
    }

    // ── 3. Окно ──────────────────────────────────────────────────────────────

    private static void Window(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine();
        Console.WriteLine("окно:");
        var s = AppSettings.Current;
        s.BansTierMode = "picks"; s.BansTierList = true; s.DraftRunes = true;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        var hovered = new List<int>();
        w.HoverHandler = id => { hovered.Add(id); return Task.FromResult(200); };
        w.LockHandler  = _ => Task.FromResult(200);
        Pump();

        List<PlanRow> Rows() => (((ItemsControl)w.FindName("PlanList")).ItemsSource as IEnumerable<PlanRow>)?.ToList() ?? [];
        string Names(IEnumerable<PlanRow> rows) => string.Join(", ", rows.Select(r => r.Name));

        // Никто не показал — наигранные за месяц.
        w.UpdateRecommendations([], Blind([]), engine);
        Pump();
        var rows = Rows();
        Check("без роли — мои чемпионы, а не заглушка и не экран ожидания",
              Vis(w, "PlanPanel") && !Vis(w, "CenterSkeleton") && !Vis(w, "IdlePanel"), "");
        Check("никто не показал — самые игранные за месяц", rows.Select(r => r.ChampionId).SequenceEqual([Sona, Ashe, Amumu]),
              Names(rows));
        Check("в заметке — роль, на которой я его играл",
              rows.Count == 3 && rows[0].Note == Loc.T("roles.support") && rows[2].Note == Loc.T("roles.jungle"),
              string.Join(" | ", rows.Select(r => r.Note)));
        Check("заголовок и строка статуса — «за месяц»",
              Text(w, "PlanTitle") == Loc.T("blind.titleMonth") && Text(w, "StatusText") == Loc.T("blind.statusMonth"),
              $"{Text(w, "PlanTitle")} / {Text(w, "StatusText")}");
        Check("внизу тир-лист", Vis(w, "TierListBar") && !Vis(w, "RunesBar"), "");
        if (snap is { Length: > 0 }) Snap(w, snap, "blind-1-month.png");

        // Пул: топ Мальфит и Гарен, стрелок Эш, саппорт Сона и Юми.
        var pools = PoolStore.Current();
        var pool = new ChampPool
        {
            Name = "Тест",
            ByRole = new() { ["top"] = [Malphite, Garen], ["adc"] = [Ashe], ["support"] = [Sona, Yuumi] },
        };
        pools.Pools.Add(pool);
        PoolStore.Persist();
        PoolStore.SetActive(PoolKind.Pool, pool.Id);

        // Игр за месяц нет — пул.
        var history = SessionTracker.PreviewGames;
        SessionTracker.PreviewGames = [];
        w.UpdateRecommendations([], Blind([]), engine);
        Pump();
        rows = Rows();
        Check("игр за месяц нет — пул по ролям, «из пула»",
              rows.Select(r => r.ChampionId).SequenceEqual([Malphite, Garen, Ashe, Sona, Yuumi])
              && rows[0].Note == $"{Loc.T("roles.top")} · {Loc.T("plan.fromPool")}",
              $"{Names(rows)} ({rows.FirstOrDefault()?.Note})");
        SessionTracker.PreviewGames = history;

        // Союзник показал Ясуо — пул по синергии с ним.
        w.UpdateRecommendations([], Blind([Yasuo]), engine);
        Pump();
        rows = Rows();
        var played = SessionTracker.RecentChamps(null).ToDictionary(c => c.ChampionId, c => c.Games);
        var expected = new[] { (Malphite, "top"), (Garen, "top"), (Ashe, "adc"), (Sona, "support"), (Yuumi, "support") }
            .Select(x => (Id: x.Item1, Syn: engine.BlindSynergy(x.Item1, x.Item2, [(Yasuo, "")]).Delta))
            .OrderByDescending(x => x.Syn).ThenByDescending(x => played.GetValueOrDefault(x.Id))
            .ToList();
        Console.WriteLine("  синергия с Ясуо: " + string.Join(", ", expected.Select(x => $"{x.Id}:{x.Syn:+0.00;-0.00}")));
        // Синергия живая: разброс по пулу — пункты, а не ноль у всех.
        Check("союзник показал Ясуо — пул в порядке синергии с ним",
              rows.Select(r => r.ChampionId).SequenceEqual(expected.Select(x => x.Id))
              && expected[0].Syn - expected[^1].Syn > 1,
              Names(rows));
        var best  = rows.FirstOrDefault();
        var worst = rows.LastOrDefault();
        var yasuo = DataDragon.Name(Yasuo);
        Check("у лучшего — «синергия +N с Ясуо» зелёным, вклад союзника в подсказке",
              best is not null && best.Note == Loc.T("blind.synOne", $"+{expected[0].Syn:F1}", yasuo)
              && best.NoteBrush == "#57C98A"
              && best.Tip?.Contains(Loc.T("blind.tipAlly", yasuo, "").TrimEnd()) == true,
              $"{best?.Note} {best?.NoteBrush} | {(best?.Tip ?? "").Replace("\n", " | ")}");
        Check("у худшего — минус красным", worst is not null && worst.Note.Contains(yasuo) && worst.NoteBrush == "#E05B5B",
              $"{worst?.Note} {worst?.NoteBrush}");
        Check("заголовок и строка статуса — «под союзников»",
              Text(w, "PlanTitle") == Loc.T("blind.title") && Text(w, "StatusText") == Loc.T("blind.status"), "");
        if (snap is { Length: > 0 }) Snap(w, snap, "blind-2-synergy.png");

        // Взятые и забаненные — не предлагаются.
        w.UpdateRecommendations([], Blind([Yasuo, Sona], enemies: [Malphite], bans: [Yuumi]), engine);
        Pump();
        rows = Rows();
        Check("взятые союзником и врагом и забаненные не предлагаются",
              rows.Count > 0 && !rows.Any(r => r.ChampionId is Sona or Malphite or Yuumi), Names(rows));

        // Клик — показ пика союзникам; плашка «Выбрать» встаёт под панелью.
        var click = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent };
        Call(w, "PlanCard_Click", new Border { Tag = Garen }, click);
        Pump();
        Check("клик по Гарену — показ пика в клиенте, строка выделена",
              hovered.LastOrDefault() == Garen && Rows().FirstOrDefault(r => r.ChampionId == Garen)?.IsSelected == true,
              string.Join(",", hovered));
        var margin = ((FrameworkElement)w.FindName("PlanPanel")).Margin.Bottom;
        Check("плашка «Выбрать» видна и не ложится на строки", Vis(w, "PickBar") && margin >= 40, $"зазор {margin:0}");

        // Руны моего чемпиона — в нижней строке; без них — снова тир-лист.
        RunesClient.UseMock = true;
        var stats = RunesClient.GetAsync(Garen, "top", CancellationToken.None).GetAwaiter().GetResult();
        w.ShowRunes(stats, Garen, DataDragon.Name(Garen), "top", null);
        Pump();
        Check("руны пришли — встали внизу вместо тир-листа", Vis(w, "RunesBar") && !Vis(w, "TierListBar"), "");
        if (snap is { Length: > 0 }) Snap(w, snap, "blind-3-runes.png");
        w.HideRunes();
        Pump();
        Check("руны ушли — внизу снова тир-лист", !Vis(w, "RunesBar") && Vis(w, "TierListBar"), "");

        // Роль раздали — обычный подбор, панели «без роли» нет.
        var pick = Blind([Yasuo]) with { MyPosition = "top" };
        w.UpdateRecommendations(engine.Recommend(pick, 6), pick, engine);
        Pump();
        Check("с ролью — обычный подбор", Vis(w, "RecScroll") && !Vis(w, "PlanPanel"), "");
        w.Close();

        // Без базы — заглушка: считать синергию нечем.
        var bare = new OverlayWindow();
        ShowHidden(bare);
        Pump();
        bare.UpdateRecommendations([], Blind([Yasuo]), null);
        Pump();
        Check("без базы — заглушка и «Фаза пиков…»",
              Vis(bare, "CenterSkeleton") && Text(bare, "StatusText") == Loc.T("status.pickPhase"),
              Text(bare, "StatusText"));
        bare.Close();
        Pump();
    }

    /// Снимок драфта без роли: мой ход, союзники наводят чемпионов, враги взяли.
    private static DraftState Blind(int[] allies, int[]? enemies = null, int[]? bans = null)
    {
        var me = new DraftPlayer(0, 0, 0, "", true);
        var team = new List<DraftPlayer> { me };
        team.AddRange(allies.Select((id, i) => new DraftPlayer(i + 1, 0, id, "", false)));
        var their = (enemies ?? []).Select((id, i) => new DraftPlayer(5 + i, id, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: team, TheirTeam: their, MyTeamBans: bans ?? [], TheirTeamBans: [],
            Me: me, MyPosition: "", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: true, ActiveCells: [0],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    private static string? Get(JsonElement root, string key)
    {
        var e = root;
        foreach (var part in key.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    /// Имена и иконки чемпионов. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static string Text(Window w, string name) => (w.FindName(name) as TextBlock)?.Text ?? "";

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
