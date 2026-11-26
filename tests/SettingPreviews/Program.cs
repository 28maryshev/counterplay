using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Counterplay;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;
using Pen = System.Windows.Media.Pen;

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
    /// Focus — что обвести (владелец: «выдели область, где изменение, чтобы
    /// сразу было понятно, куда смотреть»): имя элемента окна, «bind:Путь» —
    /// часть шаблона карточки по привязке (у них нет имён), «card:NotOwned» —
    /// карточка чемпиона не с аккаунта. Где элемента в кадре нет — черта на
    /// месте, где он был: всё ниже него съезжает вверх.
    private sealed record Spec(string Key, string Scene, string Crop, string[] Values,
                               double Pad = 0, double MaxHeight = 0,
                               (string Key, string Value)[]? Base = null, bool Alone = false,
                               string[]? Focus = null);

    private static readonly string[] OnOff = ["on", "off"];

    /// «Предметы против пика» здесь нет: иконка строится по настоящим сборкам
    /// врагов (RunesClient), а их в проверке нет — кадры вышли бы одинаковые.
    /// Подсветка при наведении и карточка чемпиона — от мыши, «Личное
    /// мастерство» — сдвиг оценок: показать это картинкой нечем.
    private static readonly Spec[] Specs =
    [
        // Профиль — весь сайдбар: видно, что пропало и как сдвинулось остальное.
        new("ReadyRank",    "profile", "RootGrid", OnOff, Focus: ["RankCard"]),
        new("ReadyLast5",   "profile", "RootGrid", OnOff, Focus: ["Last5Panel"]),
        new("ReadyWinrate", "profile", "RootGrid", OnOff, Focus: ["WinrateRow"]),
        new("ChartMode",    "profile", "RootGrid", ["winrate", "rating", "off"], Focus: ["WinrateRow"]),
        new("ChartDays",    "profile", "RootGrid", ["30", "90"], Base: [("ChartMode", "winrate")], Focus: ["WrChart"]),
        new("ReadyCompact", "profile", "RootGrid", OnOff, Focus: ["RankCard"]),
        new("ReadyPool",    "profile", "RootGrid", OnOff, Focus: ["PoolButtonsRow"]),
        new("ReadyChamps",  "profile", "RootGrid", OnOff, Focus: ["MyChampsBox"]),
        new("ReadyPhase",   "profile", "RootGrid", OnOff, Focus: ["ReadyStatusText"]),
        new("ReadyBeta",    "profile", "RootGrid", OnOff, Focus: ["BetaCard"]),
        new("DraftEnabled", "profile", "RootGrid", OnOff, Focus: ["PoolButtonsRow", "DraftOffNotice"]),
        new("FontScale",    "profile", "RootGrid", ["0.9", "1", "1.15"]),
        // Карточки пика — верх списка.
        new("DraftReasons", "draft", "FullRecList", OnOff, MaxHeight: 330, Focus: ["bind:Tags", "bind:Icons"]),
        new("DraftMetrics", "draft", "FullRecList", OnOff, MaxHeight: 330, Focus: ["bind:Metrics"]),
        new("DraftUnowned", "draft", "FullRecList", OnOff, MaxHeight: 330, Focus: ["card:NotOwned"]),
        // Полоса под карточками.
        new("DraftRolePool", "draft", "RolePoolBar", OnOff, Pad: 60, Focus: ["RolePoolBar"]),
        // Своя колонка: связки, значки стиля, иконки у игроков, полоса урона.
        new("DraftCombos",    "draft", "MyTeamPanel", OnOff, Focus: ["kids:MyTeamLines", "MyTeamCombos"]),
        new("DraftArch",      "draft", "MyTeamPanel", OnOff, Focus: ["bind:ArchGlyph"]),
        new("DraftSideIcons", "draft", "MyTeamPanel", OnOff, Focus: ["bind:SideIcons"]),
        new("DraftDamage",    "draft", "MyTeamPanel", OnOff, Focus: ["MyDmgPanel"]),
        // Всё окно драфта: длина списка и стороны команд.
        new("DraftCount",  "draft", "FullRecList", ["6", "8", "10"], Alone: true),
        new("DraftMirror", "draft", "FullView", OnOff, Focus: ["MyTeamPanel"]),
        // Баны — тир-лист под советами.
        new("BansTierList", "bans", "TierListBar", OnOff, Pad: 60, Focus: ["TierListBar"]),
        new("BansTierMode", "bans", "TierListBar", ["picks", "bans"], Pad: 8, Focus: ["TierModePicks", "TierModeBans"]),
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
            // Init грузит английский — запасной словарь для всех языков. Без
            // него у английского вместо подписей стояли ключи («session.wl»,
            // «pool.sectionTitle»), а у русского — у любой непереведённой строки.
            Loc.Init();
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
        // Живой саппорт, а не набор цифр: пять чемпионов, винрейты 50–75%
        // (без 0% и 100% у горстки игр), игры раз в день-два, флекс изредка.
        const int Sona = 37, Nami = 267, Braum = 201, Lulu = 117, Thresh = 412;
        (int Champ, bool Win)[] recent =
        [
            (Sona, true), (Nami, true), (Sona, false), (Braum, true), (Lulu, true), (Sona, true),
            (Thresh, false), (Nami, true), (Sona, true), (Braum, false), (Lulu, true), (Sona, false),
            (Nami, false), (Sona, true), (Braum, true), (Lulu, false), (Sona, true), (Thresh, true),
            (Nami, true), (Braum, false), (Sona, true), (Lulu, true),
        ];
        var now = DateTimeOffset.UtcNow;
        var games = new List<(int, int, bool, DateTimeOffset)>();
        for (var k = 0; k < recent.Length; k++)
            games.Add((420, recent[k].Champ, recent[k].Win, now.AddHours(-19 * k - 2)));
        (int Champ, bool Win)[] flex = [(Sona, true), (Braum, false), (Sona, true), (Nami, false), (Lulu, true)];
        for (var k = 0; k < flex.Length; k++)
            games.Add((440, flex[k].Champ, flex[k].Win, now.AddDays(-3 - 1.5 * k)));
        // Месяц-два назад — полоса похуже: линия за 90 дней не та, что за 30.
        int[] older = [Sona, Braum, Nami, Sona, Lulu, Braum, Sona, Thresh];
        for (var k = 0; k < 16; k++)
            games.Add((420, older[k % older.Length], k % 3 == 0, now.AddDays(-35 - 3 * k)));
        var ranks = new Dictionary<string, FakeLcu.Rank>
        {
            ["RANKED_SOLO_5x5"] = new("GOLD", "II", 62, 48, 41),
            ["RANKED_FLEX_SR"]  = new("SILVER", "I", 30, 12, 10),
        };
        // Ник как у живого игрока, без отсылки к кому-то настоящему.
        using var lcu = new FakeLcu("TEST-previews-0000000000000000000000000000000000000000000000000000000000",
                                    "MoonlitSona", games, ranks);
        using var http = new LcuHttpClient(lcu.Credentials);
        SessionTracker.DropCache();
        Task.Run(() => SessionTracker.RefreshAsync(http, CancellationToken.None)).GetAwaiter().GetResult();
        // LP за игру программа пишет только живьём — видя, как ранг сменился
        // между обновлениями. Подставному игроку дописываем их в журнал сами:
        // без них график рейтинга — одна точка, а у последних игр нет очков.
        var file = Path.Combine(AppPaths.Root, "session.json");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var queue in new[] { "solo", "flex" })
        {
            var list = doc["Accounts"]?.AsObject().First().Value?["Queues"]?[queue]?["Games"]?.AsArray();
            var i = 0;
            foreach (var g in list ?? [])
            {
                var win = g!["Win"]?.GetValue<bool>() == true;
                g["Lp"] = win ? 17 + i % 6 : -(15 + i % 5);
                i++;
            }
        }
        File.WriteAllText(file, doc.ToJsonString());
        SessionTracker.DropCache();
        var d = Task.Run(() => SessionTracker.RefreshAsync(http, CancellationToken.None)).GetAwaiter().GetResult();

        Defaults();
        // Настоящий клиент LoL проверке не указ: свёрнут — окно ушло бы в трей,
        // и кадры вышли бы пустыми (так падала SettingPreviews 26.11).
        var w = new OverlayWindow { FollowClient = false };
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
        // Настоящий клиент LoL проверке не указ: свёрнут — окно ушло бы в трей,
        // и кадры вышли бы пустыми (так падала SettingPreviews 26.11).
        var w = new OverlayWindow { FollowClient = false };
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
        var focus = new List<List<Rect>>();
        var raw = new HashSet<string>();
        foreach (var value in spec.Values)
        {
            Defaults();
            foreach (var (bk, bv) in spec.Base ?? []) Set(bk, bv);
            Set(spec.Key, value);
            Apply(w);
            render();
            shots.Add(Shoot(w, spec.Crop, spec.Alone));
            focus.Add(spec.Focus is { } f && !spec.Alone ? FocusRects(w, f) : []);
            raw.UnionWith(RawKeys(w));
        }
        Check($"{spec.Key}: подписи — текст, а не ключи строк", raw.Count == 0, string.Join(", ", raw.Take(5)));
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
        var cut = new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        var plain = fulls.Select(f => Draw(new CroppedBitmap(f, cut), k, r.Width * k, r.Height * k)).ToList();

        // Различие — по кадрам БЕЗ рамки: рамка в одном и черта в другом
        // сделали бы кадры разными и у тумблера, который ничего не делает.
        var same = Enumerable.Range(1, plain.Count - 1).Where(i => Diff(plain[0], plain[i]) < 0.002).ToList();
        Check($"{spec.Key}: кадры различаются", same.Count == 0,
              same.Count == 0 ? $"{plain.Count} кадра {plain[0].PixelWidth}×{plain[0].PixelHeight}"
                              : "одинаковы: " + string.Join(", ", same.Select(i => $"{spec.Values[0]} = {spec.Values[i]}")));

        // Что обвести: в кадре, где элемент есть, — рамка; где его нет —
        // черта на месте, где он стоял в первом кадре с ним.
        var frames = plain;
        if (spec.Focus is not null && !spec.Alone)
        {
            var area = new Rect(r.X, r.Y, r.Width, r.Height);
            var inside = focus.Select(l => l.Where(f => f.IntersectsWith(area)).ToList()).ToList();
            var refRects = inside.FirstOrDefault(l => l.Count > 0) ?? [];
            Check($"{spec.Key}: есть что обвести ({string.Join(", ", spec.Focus)})", refRects.Count > 0,
                  refRects.Count == 0 ? "ни в одном кадре" : "");
            frames = [.. fulls.Select((f, i) => Draw(new CroppedBitmap(
                Mark(f, inside[i], inside[i].Count == 0 ? refRects : [], k), cut), k, r.Width * k, r.Height * k))];
        }

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

    private static readonly System.Text.RegularExpressions.Regex KeyLike =
        new(@"^[a-z][a-zA-Z0-9]*(\.[a-zA-Z0-9]+)+$");

    /// <summary>
    /// Видимые подписи, похожие на ключ строки («session.wl»): так Loc.T
    /// отвечает, когда строки нет ни в языке, ни в запасном английском. Так
    /// 19.11 ушли английские картинки — генератор не звал Loc.Init.
    /// </summary>
    private static IEnumerable<string> RawKeys(Window w)
    {
        var stack = new Stack<DependencyObject>([w]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is UIElement { IsVisible: false }) continue;
            if (d is TextBlock { Text: { Length: > 0 } t } && KeyLike.IsMatch(t)) yield return t;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// Бирюза тумблера: рамка того же цвета, что включённый переключатель.
    private static readonly Color Accent = Color.FromRgb(0x36, 0xD6, 0xE7);

    /// <summary>
    /// Рамка вокруг того, что меняет настройка, и черта там, где оно было.
    /// Толщины — в точках готовой картинки: кадр потом уменьшается в k раз.
    /// </summary>
    private static BitmapSource Mark(BitmapSource full, List<Rect> boxes, List<Rect> gaps, double k)
    {
        double px = 1 / k;
        var glow = new Pen(new SolidColorBrush(Color.FromArgb(0x55, Accent.R, Accent.G, Accent.B)), 7 * px);
        var line = new Pen(new SolidColorBrush(Accent), 2.2 * px);
        var dash = new Pen(new SolidColorBrush(Accent), 2.2 * px) { DashStyle = new DashStyle(new double[] { 3, 2 }, 0) };
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen())
        {
            dc.DrawImage(full, new Rect(0, 0, full.PixelWidth, full.PixelHeight));
            foreach (var b in boxes)
            {
                var o = Rect.Inflate(b, 4 * px, 4 * px);
                dc.DrawRoundedRectangle(null, glow, o, 6 * px, 6 * px);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x10, Accent.R, Accent.G, Accent.B)),
                                        line, o, 6 * px, 6 * px);
            }
            foreach (var g in gaps)
            {
                var a = new System.Windows.Point(g.Left, g.Top);
                var z = new System.Windows.Point(g.Right, g.Top);
                dc.DrawLine(glow, a, z);
                dc.DrawLine(dash, a, z);
                dc.DrawEllipse(new SolidColorBrush(Accent), null, a, 3.5 * px, 3.5 * px);
                dc.DrawEllipse(new SolidColorBrush(Accent), null, z, 3.5 * px, 3.5 * px);
            }
        }
        var bmp = new RenderTargetBitmap(full.PixelWidth, full.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(v);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Места видимых элементов по селекторам Spec.Focus — в точках окна.
    /// «bind:Путь» — элемент, у которого ItemsSource или Text привязан к Путь
    /// (части шаблонов карточек и слотов имён не имеют); «card:NotOwned» —
    /// карточка подбора, чей чемпион не с аккаунта; иначе — имя элемента.
    /// </summary>
    private static List<Rect> FocusRects(Window w, string[] selectors)
    {
        var root = (FrameworkElement)w.Content;
        var visible = Visible(root).ToList();
        var found = new List<FrameworkElement>();
        foreach (var sel in selectors)
        {
            if (sel.StartsWith("bind:"))
            {
                var path = sel[5..];
                found.AddRange(visible.Where(e =>
                    BindingOperations.GetBinding(e, ItemsControl.ItemsSourceProperty)?.Path?.Path == path
                    || (e is TextBlock { Text.Length: > 0 } && BindingOperations.GetBinding(e, TextBlock.TextProperty)?.Path?.Path == path)));
            }
            else if (sel.StartsWith("kids:"))
            {
                // Холст линий связок растянут на всю колонку — обводим сами линии.
                if (w.FindName(sel[5..]) is System.Windows.Controls.Panel host && visible.Contains(host))
                    found.AddRange(host.Children.OfType<FrameworkElement>().Where(c => c.IsVisible));
            }
            else if (sel == "card:NotOwned")
                found.AddRange(visible.OfType<ContentPresenter>().Where(c => c.Content is FullRecCard { NotOwned: true }));
            else if (w.FindName(sel) is FrameworkElement named && visible.Contains(named))
                found.Add(named);
        }
        var rects = found.Where(e => e.ActualWidth > 0 || e.ActualHeight > 0)
            .Select(e => e is System.Windows.Shapes.Shape sh
                ? sh.TransformToAncestor(root).TransformBounds(sh.RenderedGeometry.Bounds)   // линия: её отрезок, не холст
                : e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize)))
            .Where(r => !r.IsEmpty).ToList();
        return Merge(rects);
    }

    /// Рамки, что касаются друг друга (плашки и значки одной карточки,
    /// линии одной связки), — одной рамкой: иначе они налезают друг на друга.
    private static List<Rect> Merge(List<Rect> rects)
    {
        var list = new List<Rect>(rects);
        for (var merged = true; merged;)
        {
            merged = false;
            for (var i = 0; i < list.Count && !merged; i++)
                for (var j = i + 1; j < list.Count && !merged; j++)
                    if (Rect.Inflate(list[i], 8, 8).IntersectsWith(list[j]))
                    {
                        list[i] = Rect.Union(list[i], list[j]);
                        list.RemoveAt(j);
                        merged = true;
                    }
        }
        return list;
    }

    /// Видимые элементы дерева: скрытая ветка пропускается целиком.
    private static IEnumerable<FrameworkElement> Visible(DependencyObject top)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(top);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is UIElement { IsVisible: false }) continue;
            if (d is FrameworkElement fe) yield return fe;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
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
