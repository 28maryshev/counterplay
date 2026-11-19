using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Counterplay;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Counterplay.Tests;

/// <summary>
/// Картинки к настройкам (владелец 19.11: «наводишь на "Компактная карточка
/// ранга" — и видно, что на сайдбаре меняется»).
///
/// Каждую настройку из списка <see cref="Specs"/> окно программы рисует по
/// разу на каждое значение — невидимым, на подставном профиле (подставной
/// клиент) и живой базе матчей (только чтение), — и вырезает тот кусок, где
/// разница. Окно настроек показывает кадры при наведении на строку, сменяя их
/// плавно (SettingsWindow.Preview).
///
/// Без ключа — проверка: у каждой настройки кадры различаются (иначе тумблер
/// ничего не делает — так 19.11 нашлась карточка пула, глухая к «Полоскам») и
/// картинки лежат в assets/previews для ru и en. С ключом <c>write</c> —
/// перерисовать картинки (после правки вида окна). Нет базы — драфт не
/// проверить, проверка это пишет и считает провалом только профиль.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Языки картинок: русский — для русского, английский — для всех прочих
    /// (SettingsWindow.PreviewLang). Пятнадцать наборов весили бы в 7 раз
    /// больше, а на кадре важно, ЧТО появилось, а не подпись.
    private static readonly string[] Langs = ["ru", "en"];

    /// Сцена, на которой видна настройка; что вырезать — элемент окна (его
    /// место объединяется по всем кадрам: в одном он виден, в другом на его
    /// месте то, что заняло пустоту) с запасом Pad вокруг и не выше MaxHeight
    /// сверху; значения по порядку кадров.
    /// Base — что поставить до значения (период графика виден только на
    /// графике винрейта: у рейтинга LP есть лишь у свежих игр). Alone — снять
    /// элемент целиком, а не видимую часть окна (длина списка уходит под
    /// прокрутку).
    private sealed record Spec(string Key, string Scene, string Crop, string[] Values,
                               double Pad = 0, double MaxHeight = 0,
                               (string Key, string Value)[]? Base = null, bool Alone = false);

    private static readonly string[] OnOff = ["on", "off"];

    /// «Предметы против пика» здесь нет: иконка строится по настоящим сборкам
    /// врагов (RunesClient), а их в проверке нет — кадры вышли бы одинаковые.
    /// Подсветка при наведении и карточка чемпиона — от мыши, «Личное
    /// мастерство» — сдвиг оценок: показать это картинкой нечем.
    private static readonly Spec[] Specs =
    [
        // Профиль — весь сайдбар: видно, что пропало и как сдвинулось остальное.
        new("ReadyRank",    "profile", "RootGrid", OnOff),
        new("ReadyLast5",   "profile", "RootGrid", OnOff),
        new("ReadyWinrate", "profile", "RootGrid", OnOff),
        new("ChartMode",    "profile", "RootGrid", ["winrate", "rating", "off"]),
        new("ChartDays",    "profile", "RootGrid", ["30", "90"], Base: [("ChartMode", "winrate")]),
        new("ReadyCompact", "profile", "RootGrid", OnOff),
        new("ReadyPool",    "profile", "RootGrid", OnOff),
        new("ReadyChamps",  "profile", "RootGrid", OnOff),
        new("ReadyPhase",   "profile", "RootGrid", OnOff),
        new("ReadyBeta",    "profile", "RootGrid", OnOff),
        new("DraftEnabled", "profile", "RootGrid", OnOff),
        new("FontScale",    "profile", "RootGrid", ["0.9", "1", "1.15"]),
        // Карточки пика — верх списка.
        new("DraftReasons", "draft", "FullRecList", OnOff, MaxHeight: 330),
        new("DraftMetrics", "draft", "FullRecList", OnOff, MaxHeight: 330),
        new("DraftUnowned", "draft", "FullRecList", OnOff, MaxHeight: 330),
        // Полоса под карточками.
        new("DraftRolePool", "draft", "RolePoolBar", OnOff, Pad: 60),
        // Своя колонка: связки, значки стиля, иконки у игроков, полоса урона.
        new("DraftCombos",    "draft", "MyTeamPanel", OnOff),
        new("DraftArch",      "draft", "MyTeamPanel", OnOff),
        new("DraftSideIcons", "draft", "MyTeamPanel", OnOff),
        new("DraftDamage",    "draft", "MyTeamPanel", OnOff),
        // Всё окно драфта: длина списка и стороны команд.
        new("DraftCount",  "draft", "FullRecList", ["6", "8", "10"], Alone: true),
        new("DraftMirror", "draft", "FullView", OnOff),
        // Баны — тир-лист под советами.
        new("BansTierList", "bans", "TierListBar", OnOff, Pad: 60),
        new("BansTierMode", "bans", "TierListBar", ["picks", "bans"], Pad: 8),
    ];

    /// Предел картинки: окно настроек показывает её в рамке до 480 × 640.
    private const double MaxWidth = 480, MaxBoxHeight = 640;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var write = args.Contains("write");

        // Каждый язык — своим процессом: закрытые окна прошлого языка
        // подписаны на смену языка и падают, когда его меняют снова.
        var one = args.FirstOrDefault(a => a.StartsWith("--lang="))?["--lang=".Length..];
        if (one is null)
        {
            var failed = 0;
            foreach (var lang in Langs)
            {
                using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    Environment.ProcessPath!, [.. args, $"--lang={lang}"]) { UseShellExecute = false })!;
                child.WaitForExit();
                if (child.ExitCode != 0) failed++;
            }
            Console.WriteLine();
            Console.WriteLine(failed == 0
                ? (write ? "ИТОГ: картинки перерисованы — assets/previews" : "ИТОГ: каждая настройка меняет то, что показано на её картинке, картинки на месте")
                : $"ИТОГ: провалено языков — {failed}");
            return failed == 0 ? 0 : 1;
        }
        return Run(one, write);
    }

    private static int Run(string lang, bool write)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SettingPreviews-" + lang);
        try { Directory.Delete(baseDir, true); } catch { }
        Directory.CreateDirectory(baseDir);
        AppPaths.RootOverride = baseDir;
        Log.FileDisabled = true;
        var player = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = player;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(baseDir, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        CopyAssets(player, baseDir);
        var outDir = Path.Combine(Repo(), "assets", "previews");

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RecommendationEngine? engine = null;
        try
        {
            var db = RecommendationEngine.FindDb();
            if (db is not null)
            {
                engine = RecommendationEngine.Create(db, "emerald");
                engine.Mastery = new Dictionary<int, long>();
            }
            else Console.WriteLine("базы нет — драфт и баны не нарисовать");

            Console.WriteLine($"\n── {lang}");
            Loc.SetLanguage(lang);
            if (!DataDragon.LoadFromCache(Loc.DDragonLocale))
                DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
            IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
            Profile(lang, write, outDir);
            if (engine is not null) Draft(engine, lang, write, outDir);
        }
        finally
        {
            engine?.Dispose();
            app.Shutdown();
            SessionTracker.HistoryOverride = null;
            try { Directory.Delete(baseDir, true); } catch { }
        }

        Console.WriteLine(_fails == 0 ? $"  {lang}: всё в порядке" : $"  {lang}: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── Профиль ────────────────────────────────────────────────────────────

    private static void Profile(string lang, bool write, string outDir)
    {
        Console.WriteLine("профиль:");
        var now = DateTimeOffset.UtcNow;
        var games = new List<(int, int, bool, DateTimeOffset)>();
        int[] champs = [37, 37, 201, 37, 412, 201, 37, 117, 37, 201, 16, 37];
        for (var k = 0; k < 24; k++)
            games.Add((420, champs[k % champs.Length], k % 3 != 1, now.AddHours(-3 * k - 1)));
        for (var k = 0; k < 6; k++)
            games.Add((440, champs[(k + 3) % champs.Length], k % 2 == 0, now.AddDays(-2).AddHours(-3 * k)));
        // Месяц-два назад — серия поражений: линия за 90 дней не та, что за 30.
        for (var k = 0; k < 16; k++)
            games.Add((420, champs[k % champs.Length], k % 4 == 0, now.AddDays(-35 - 3 * k)));
        var ranks = new Dictionary<string, FakeLcu.Rank>
        {
            ["RANKED_SOLO_5x5"] = new("GOLD", "II", 62, 48, 41),
            ["RANKED_FLEX_SR"]  = new("SILVER", "I", 30, 12, 10),
        };
        using var lcu = new FakeLcu("TEST-previews-0000000000000000000000000000000000000000000000000000000000",
                                    lang == "ru" ? "Игрок" : "Player", games, ranks);
        using var http = new LcuHttpClient(lcu.Credentials);
        SessionTracker.DropCache();
        var d = Task.Run(() => SessionTracker.RefreshAsync(http, CancellationToken.None)).GetAwaiter().GetResult();

        Defaults();
        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            w.ShowReady();
            Pump();
            void Render() { w.ShowSession(d); w.ShowReady(); Pump(); }
            foreach (var spec in Specs.Where(s => s.Scene == "profile"))
                Frames(w, spec, Render, lang, write, outDir);
        }
        finally { w.Close(); Pump(); }
    }

    // ── Драфт и баны ───────────────────────────────────────────────────────

    private const int Malphite = 54, LeeSin = 64, Yasuo = 157, Ezreal = 81, Darius = 122,
                      Viego = 234, Ahri = 103, Caitlyn = 51, Morgana = 25;

    private static void Draft(RecommendationEngine engine, string lang, bool write, string outDir)
    {
        Console.WriteLine("драфт:");
        // Мой журнал: саппорт на Соне, Брауме, Нами — для пула роли и наигранности.
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var times = Enumerable.Range(0, SessionTracker.PlayHistory.KeepTimes).Select(i => ts - i * 86400L).ToArray();
        var played = new Dictionary<int, (int, int)> { [37] = (18, 11), [201] = (9, 5), [267] = (5, 3) };
        SessionTracker.HistoryOverride = new SessionTracker.PlayHistory(
            played, played.ToDictionary(kv => kv.Key, _ => times), 60, ts);

        var draft = new DraftState(
            // Мальфит с Ясуо — связка команды (подброс под ульт).
            MyTeam: [new(0, Yasuo, 0, "middle", false), new(1, LeeSin, 0, "jungle", false), new(2, Malphite, 0, "top", false),
                     new(3, Ezreal, 0, "bottom", false), new(4, 0, 0, "utility", true)],
            TheirTeam: [new(5, Darius, 0, "", false), new(6, Viego, 0, "", false), new(7, Ahri, 0, "", false),
                        new(8, Caitlyn, 0, "", false), new(9, Morgana, 0, "", false)],
            MyTeamBans: [], TheirTeamBans: [], Me: new(4, 0, 0, "utility", true), MyPosition: "utility",
            DirectOpponent: null, ExposedToCounter: false, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: true, ActiveCells: [4], FirstPickCell: -1,
            MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
        var bans = new DraftState(
            MyTeam: [new(0, 0, 0, "top", false), new(1, 0, 0, "jungle", false), new(2, 0, 0, "middle", false),
                     new(3, 0, 0, "bottom", false), new(4, 0, 0, "utility", true)],
            TheirTeam: [], MyTeamBans: [], TheirTeamBans: [], Me: new(4, 0, 0, "utility", true), MyPosition: "utility",
            DirectOpponent: null, ExposedToCounter: false, InBanPhase: true, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [], FirstPickCell: -1,
            MyBanActionId: 0, MyBanInProgress: true);

        Defaults();
        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();
        try
        {
            // Что есть на аккаунте: всё, кроме второго в подборе, — у него
            // красная рамка, а без «Чемпионы не с аккаунта» он пропадает.
            var first = engine.Recommend(draft, 8);
            var allIds = DataDragon.GetAllIconUrls().Keys.ToList();
            w.SetOwnedChampions(allIds.Where(id => first.Count < 2 || id != first[1].ChampionId));
            Pump();

            void Picks() { w.UpdateRecommendations(engine.Recommend(draft, AppSettings.Current.DraftCount), draft, engine); Pump(); }
            void Bans()  { w.UpdateBans(engine.RecommendBans(bans), bans, engine); Pump(); }
            foreach (var spec in Specs.Where(s => s.Scene == "draft")) Frames(w, spec, Picks, lang, write, outDir);
            foreach (var spec in Specs.Where(s => s.Scene == "bans"))  Frames(w, spec, Bans, lang, write, outDir);
        }
        finally { w.Close(); Pump(); }
    }

    // ── Кадры ──────────────────────────────────────────────────────────────

    /// Кадр на каждое значение настройки: поставить значение, перерисовать
    /// сцену, снять окно целиком и место элемента. Вырезаем по месту,
    /// объединённому по всем кадрам: элемент, который пропадает, в другом кадре
    /// уступает место соседям — разница видна в одной и той же рамке.
    private static void Frames(OverlayWindow w, Spec spec, Action render, string lang, bool write, string outDir)
    {
        var shots = new List<(BitmapSource Full, Rect? At)>();
        foreach (var value in spec.Values)
        {
            Defaults();
            foreach (var (bk, bv) in spec.Base ?? []) Set(bk, bv);
            Set(spec.Key, value);
            Apply(w);
            render();
            shots.Add(Shoot(w, spec.Crop, spec.Alone));
        }
        Defaults();
        Apply(w);

        var seen = shots.Where(s => s.At is not null).Select(s => s.At!.Value).ToList();
        if (seen.Count == 0) { Check($"{spec.Key}: {spec.Crop} виден хоть в одном кадре", false, "ни в одном"); return; }
        var r = seen.Aggregate(Rect.Union);
        if (spec.MaxHeight > 0 && r.Height > spec.MaxHeight) r.Height = spec.MaxHeight;
        r.Inflate(spec.Pad, spec.Pad);
        // Окно профиля по высоте — по содержимому: кадры разной высоты
        // доводим до наибольшего на фоне окна, потом режем одной рамкой.
        int wMax = shots.Max(s => s.Full.PixelWidth), hMax = shots.Max(s => s.Full.PixelHeight);
        var fulls = shots.Select(s => Draw(s.Full, 1.0, wMax, hMax)).ToList();
        r.Intersect(new Rect(0, 0, wMax, hMax));
        var k = Math.Min(1.0, Math.Min(MaxWidth / r.Width, MaxBoxHeight / r.Height));
        var frames = fulls.Select(f => Draw(new CroppedBitmap(f,
            new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height)), k, r.Width * k, r.Height * k)).ToList();

        var same = Enumerable.Range(1, frames.Count - 1).Where(i => Diff(frames[0], frames[i]) < 0.002).ToList();
        Check($"{spec.Key}: кадры различаются", same.Count == 0,
              same.Count == 0 ? $"{frames.Count} кадра {frames[0].PixelWidth}×{frames[0].PixelHeight}"
                              : "одинаковы: " + string.Join(", ", same.Select(i => $"{spec.Values[0]} = {spec.Values[i]}")));

        var dir = Path.Combine(outDir, lang);
        for (var i = 0; i < frames.Count; i++)
        {
            var path = Path.Combine(dir, $"{spec.Key}.{spec.Values[i]}.jpg");
            if (write) Save(frames[i], path);
            else if (!File.Exists(path)) Check($"{spec.Key}: картинка {lang}/{Path.GetFileName(path)}", false, "нет — перерисовать с ключом write");
        }
    }

    private static readonly System.Reflection.MethodInfo ApplySettings =
        typeof(OverlayWindow).GetMethod("ApplySettings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// Применить настройки, как после «Применить». Не через AppSettings.Save:
    /// окно ставит себе прозрачность из настроек и стало бы видно на экране —
    /// гасим его тут же, в том же проходе, до отрисовки.
    private static void Apply(OverlayWindow w)
    {
        ApplySettings.Invoke(w, null);
        w.Opacity = 0;
        Pump();
    }

    /// Значение настройки по имени: on/off — да/нет, иначе по типу поля.
    private static void Set(string key, string value)
    {
        var p = typeof(AppSettings).GetProperty(key)!;
        object v = p.PropertyType == typeof(bool) ? value == "on"
                 : p.PropertyType == typeof(int) ? int.Parse(value)
                 : p.PropertyType == typeof(double) ? double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)
                 : value;
        p.SetValue(AppSettings.Current, v);
    }

    /// Настройки по умолчанию и место окна драфта — как в проверках окна.
    private static void Defaults()
    {
        var s = AppSettings.Current;
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(s, p.GetValue(AppSettings.Defaults()));
        s.DraftWidth = 1432; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;
    }

    /// Окно целиком и место элемента в нём (null — элемент не виден).
    private static (BitmapSource Full, Rect? At) Shoot(Window w, string name, bool alone)
    {
        var root = (FrameworkElement)w.Content;
        if (alone)
        {
            // Элемент сам по себе — со всем, что ушло под прокрутку.
            var e = (FrameworkElement)w.FindName(name);
            var own = new RenderTargetBitmap((int)Math.Ceiling(e.ActualWidth), (int)Math.Ceiling(e.ActualHeight),
                                             96, 96, PixelFormats.Pbgra32);
            own.Render(e);
            own.Freeze();
            return (own, new Rect(0, 0, own.PixelWidth, own.PixelHeight));
        }
        var full = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight),
                                          96, 96, PixelFormats.Pbgra32);
        full.Render(root);
        full.Freeze();
        var el = (FrameworkElement)w.FindName(name);
        Rect? at = el.IsVisible && el.ActualWidth > 0 && el.ActualHeight > 0
            ? el.TransformToAncestor(root).TransformBounds(new Rect(el.RenderSize)) : null;
        return (full, at);
    }

    private static readonly Color Bg = Color.FromRgb(0x0B, 0x12, 0x1C);

    private static BitmapSource Draw(BitmapSource src, double k, double width, double height)
    {
        var v = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(v, BitmapScalingMode.HighQuality);
        using (var dc = v.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Bg), null, new Rect(0, 0, width, height));
            dc.DrawImage(src, new Rect(0, 0, src.PixelWidth * k, src.PixelHeight * k));
        }
        var bmp = new RenderTargetBitmap((int)Math.Round(width), (int)Math.Round(height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    /// Доля точек, что отличаются заметно (больше 24 по любому каналу).
    private static double Diff(BitmapSource a, BitmapSource b)
    {
        int w = a.PixelWidth, h = a.PixelHeight, stride = w * 4;
        var pa = new byte[stride * h];
        var pb = new byte[stride * h];
        a.CopyPixels(pa, stride, 0);
        b.CopyPixels(pb, stride, 0);
        var diff = 0;
        for (var i = 0; i < pa.Length; i += 4)
            if (Math.Abs(pa[i] - pb[i]) > 24 || Math.Abs(pa[i + 1] - pb[i + 1]) > 24 || Math.Abs(pa[i + 2] - pb[i + 2]) > 24)
                diff++;
        return (double)diff / (w * h);
    }

    private static void Save(BitmapSource bmp, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var enc = new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    // ── подручное ──────────────────────────────────────────────────────────

    private static void CopyAssets(string player, string root)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(player, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(root, Path.GetRelativePath(player, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(f, to, overwrite: true);
            }
        }
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    private static void Pump()
    {
        for (var i = 0; i < 12; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
