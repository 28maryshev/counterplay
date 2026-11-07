using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Теги и иконки на карточке пика вместо строк доводов.
///
/// Владелец (07.11): «хочу переработать тонну текста в центральном блоке …
/// в виде тегов и иконок (плюсы и минусы пика)». Договорились: четыре тега с
/// цифрой (линия, вслепую, против команды, синергия) и много иконок — зелёные
/// за пик, красные против, объяснение при наведении.
///
/// Меряет:
/// - у каждого показанного кандидата есть значки; тегов не больше трёх,
///   «линия» и «вслепую» вместе не бывают;
/// - цифра тега — та же, что в подборе (DirectDelta, OtherDelta, SynergyDelta),
///   знак тега — знак цифры;
/// - иконки по порядку: плюсы, связки с союзниками, минусы;
/// - у каждого вида есть значок и строка (русская и английская);
/// - случай владельца: Мальфит с Ясуо в команде — связка «подброс под ульт»
///   на карточке есть (тегом или иконкой);
/// - вслепую тег совпадает с выводом движка (надёжен / рискован);
/// - красное правда бывает: хотя бы один красный тег и красная иконка;
/// - окно: на карточках теги и иконки, строк доводов и ряда предметов нет.
/// С CP_SNAP — снимки карточек в эту папку.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Malphite = 54, Yasuo = 157, LeeSin = 64, Jinx = 222, Lulu = 117,
                      Darius = 122, Zed = 238, Viego = 234, Caitlyn = 51, Morgana = 25,
                      Ahri = 103, Orianna = 61, Garen = 86, Leona = 89, Ezreal = 81, Thresh = 412;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PickBadges");
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

        Strings();

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — дальше проверять нечем"); return Finish(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Assets(playerDir);

        var scenes = new (string Name, DraftState Draft)[]
        {
            ("топ против Дариуса, Ясуо в команде", TopVsDarius()),
            ("мид первым пиком, вслепую", BlindMid()),
            ("саппорт против Кейтлин и Морганы", SupportVsBot()),
        };
        var all = new List<(string Scene, Recommendation R)>();
        foreach (var (name, draft) in scenes)
        {
            var recs = engine.Recommend(draft, 8);
            Console.WriteLine($"\n{name}:");
            foreach (var r in recs) Print(r);
            all.AddRange(recs.Select(r => (name, r)));
        }
        Console.WriteLine();
        Engine(all);
        Owner(engine);
        Blind(all.Where(x => x.Scene.Contains("вслепую")).Select(x => x.R).ToList());

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window(engine, scenes);
        app.Shutdown();

        try { Directory.Delete(root, recursive: true); } catch { }
        return Finish();
    }

    // ── Строки и значки ───────────────────────────────────────────────────

    private static readonly string[] TagKinds = ["lane", "blind", "team", "synergy"];
    private static readonly string[] PairKinds =
        ["airborne", "orianna", "immortal", "enchanter", "hook", "burst_pick", "isolate",
         "artillery", "channel", "peel", "frontline", "double_engage"];

    private static void Strings()
    {
        Console.WriteLine("значки и строки:");
        var repo = FindRepo();
        var ru = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "assets/i18n/ru.json"))).RootElement;
        var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "assets/i18n/en.json"))).RootElement;
        var icons = ru.GetProperty("pickIcon").EnumerateObject().Select(p => p.Name).Where(n => n != "common").ToList();
        var kinds = TagKinds.Concat(icons).Concat(PairKinds.Select(k => "pair." + k)).ToList();

        var noGlyph = kinds.Where(k => Glyphs.Of(k) is null).ToList();
        Check($"у каждого вида есть значок ({kinds.Count})", noGlyph.Count == 0, noGlyph.Count == 0 ? "все" : string.Join(", ", noGlyph));
        var outside = Glyphs.Kinds.Where(k => Glyphs.Of(k) is { } g
            && (g.Bounds.Left < -0.5 || g.Bounds.Top < -0.5 || g.Bounds.Right > 24.5 || g.Bounds.Bottom > 24.5)).ToList();
        Check("значки в сетке 24×24", outside.Count == 0, outside.Count == 0 ? "все" : string.Join(", ", outside));
        var unused = Glyphs.Kinds.Except(kinds).ToList();
        Check("в Glyphs нет лишних видов", unused.Count == 0, unused.Count == 0 ? "нет" : string.Join(", ", unused));

        string[] keys =
        [
            .. TagKinds.Where(k => k != "synergy").Select(k => k == "blind" ? "pickTag.blindSafe" : $"pickTag.{k}"),
            "pickTag.blindRisky", "pickTag.synergy", "pickTag.synergyWord", "pickTag.teamTip",
            .. icons.Select(k => $"pickIcon.{k}"), "pickIcon.common",
            .. PairKinds.Select(k => $"pairShort.{k}"),
            "reason.hardMatchups", "reason.synBad", "reason.pairBad", "reason.fewGames", "reason.patchUp",
            "reason.patchDown", "reason.noDive", "reason.noPoke", "reason.noFront", "reason.notOwned",
            "reason.counterItems", "reason.offStyle", "reason.personalUp", "reason.personalDown",
            "reason.exploitCc", "reason.exploitAuto", "reason.vulnCc", "reason.vulnAuto",
            "reason.vulnShield", "reason.vulnHeal",
        ];
        foreach (var (lang, doc) in new[] { ("ru", ru), ("en", en) })
        {
            var missing = keys.Where(k => Get(doc, k) is null).ToList();
            Check($"{lang}: все строки тегов и иконок ({keys.Length})", missing.Count == 0,
                  missing.Count == 0 ? "есть" : string.Join(", ", missing));
        }
    }

    // ── Движок ────────────────────────────────────────────────────────────

    private static string Points(double v) => (v >= 0 ? "+" : "−") + Math.Abs(v).ToString("F1");

    private static void Engine(List<(string Scene, Recommendation R)> all)
    {
        Console.WriteLine("движок:");
        var noBadges = all.Where(x => x.R.Badges is null).Select(x => Who(x.R)).ToList();
        Check("у каждого показанного есть значки", noBadges.Count == 0, noBadges.Count == 0 ? $"{all.Count} кандидатов" : string.Join(", ", noBadges));
        var recs = all.Select(x => x.R).Where(r => r.Badges is not null).ToList();

        var many = recs.Where(r => r.Badges!.Tags.Count > 3).Select(Who).ToList();
        Check("тегов не больше трёх", many.Count == 0, many.Count == 0 ? "да" : string.Join(", ", many));
        var both = recs.Where(r => r.Badges!.Tags.Any(t => t.Kind == "lane") && r.Badges.Tags.Any(t => t.Kind == "blind")).Select(Who).ToList();
        Check("«линия» и «вслепую» вместе не бывают", both.Count == 0, both.Count == 0 ? "да" : string.Join(", ", both));

        var wrong = new List<string>();
        foreach (var r in recs)
            foreach (var t in r.Badges!.Tags)
            {
                double? v = t.Kind switch
                {
                    "lane" => r.DirectDelta, "team" => r.OtherDelta, "synergy" => r.SynergyDelta, _ => null,
                };
                if (v is not { } x) continue;
                if (!t.Text.Contains(Points(x) + "%") || Math.Sign(x) != t.Sign)
                    wrong.Add($"{Who(r)}: «{t.Text}» при {Points(x)}");
            }
        Check("цифра тега — та же, что в подборе, знак — её знак", wrong.Count == 0,
              wrong.Count == 0 ? "да" : string.Join("; ", wrong.Take(4)));

        static int Group(PickIcon i) => i.Sign < 0 ? 2 : IsLink(i.Kind) ? 1 : 0;
        var order = recs.Where(r => r.Badges!.Icons.Select(Group).Zip(r.Badges.Icons.Select(Group).Skip(1)).Any(p => p.First > p.Second))
                        .Select(Who).ToList();
        Check("иконки: плюсы, связки, минусы", order.Count == 0, order.Count == 0 ? "по порядку" : string.Join(", ", order));

        var raw = recs.SelectMany(r => r.Badges!.Icons.Select(i => i.Title).Concat(r.Badges.Tags.Select(t => t.Text)))
                      .Where(s => s.StartsWith("pickIcon.") || s.StartsWith("pairShort.") || s.StartsWith("pickTag.")).Distinct().ToList();
        Check("подписи переведены, а не ключи", raw.Count == 0, raw.Count == 0 ? "да" : string.Join(", ", raw));
        var noGlyph = recs.SelectMany(r => r.Badges!.Icons.Select(i => i.Kind).Concat(r.Badges.Tags.Select(t => t.Kind)))
                          .Distinct().Where(k => Glyphs.Of(k) is null).ToList();
        Check("у каждого тега и иконки в выдаче есть значок", noGlyph.Count == 0, noGlyph.Count == 0 ? "да" : string.Join(", ", noGlyph));

        var redTags  = recs.SelectMany(r => r.Badges!.Tags).Count(t => t.Sign < 0);
        var redIcons = recs.SelectMany(r => r.Badges!.Icons).Count(i => i.Sign < 0);
        Check("минусы видны: красные теги и иконки", redTags > 0 && redIcons > 0,
              $"красных тегов {redTags}, иконок {redIcons} на {recs.Count} карточек");
        var kinds = recs.SelectMany(r => r.Badges!.Icons).Select(i => i.Kind).Distinct().Count();
        Console.WriteLine($"  (видов иконок в выдаче: {kinds})");
    }

    /// Связка владельца: Мальфит при Ясуо в команде — подброс под ульт.
    private static void Owner(RecommendationEngine engine)
    {
        var r = engine.Recommend(TopVsDarius(), 1, [Malphite]).FirstOrDefault();
        var b = r?.Badges;
        var tag = b?.Tags.FirstOrDefault(t => t.Kind == "synergy" && t.Text.StartsWith(Loc.T("pairShort.airborne")));
        var icon = b?.Icons.FirstOrDefault(i => i.Kind == "pair.airborne");
        Check("Мальфит с Ясуо: подброс под ульт на карточке", tag is not null || icon is not null,
              tag is not null ? $"тег «{tag.Text}»: {tag.Tip}" : icon is not null ? $"иконка: {icon.Tip}"
              : r is null ? "нет кандидата" : Line(r));
    }

    /// Вслепую тег совпадает с выводом движка — со строкой «надёжен / рискован».
    private static void Blind(List<Recommendation> recs)
    {
        var tags = recs.Count(r => r.Badges?.Tags.Any(t => t.Kind == "blind") == true);
        Check("вслепую: у кого-то есть тег «вслепую»", tags > 0, $"{tags} из {recs.Count}");
        var mismatch = recs.Where(r =>
        {
            var tag = r.Badges?.Tags.FirstOrDefault(t => t.Kind == "blind");
            var safe = r.Reasons.Any(x => x.Contains("Надёжен вслепую"));
            var risky = r.Reasons.Any(x => x.Contains("Рискован вслепую"));
            return tag is null ? safe || risky : tag.Sign > 0 ? !safe : !risky;
        }).Select(Who).ToList();
        Check("тег «вслепую» — тот же вывод, что у движка", mismatch.Count == 0,
              mismatch.Count == 0 ? "совпадает" : string.Join(", ", mismatch));
        var lane = recs.Where(r => r.Badges?.Tags.Any(t => t.Kind == "lane") == true).Select(Who).ToList();
        Check("вслепую тега «линия» нет", lane.Count == 0, lane.Count == 0 ? "нет" : string.Join(", ", lane));
    }

    // ── Окно ──────────────────────────────────────────────────────────────

    private static void Window(RecommendationEngine engine, (string Name, DraftState Draft)[] scenes)
    {
        Console.WriteLine("окно:");
        var s = AppSettings.Current;
        s.DraftWidth = 1432; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;
        s.DraftReasons = true;
        var snap = Environment.GetEnvironmentVariable("CP_SNAP");

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();
        var list = (ItemsControl)w.FindName("FullRecList");
        for (var i = 0; i < scenes.Length; i++)
        {
            var draft = scenes[i].Draft;
            var recs = engine.Recommend(draft, s.DraftCount);
            w.UpdateRecommendations(recs, draft, engine);
            Pump();
            var cards = list.Items.OfType<FullRecCard>().ToList();
            var withTags = cards.Count(c => c.Tags.Count > 0);
            var withIcons = cards.Count(c => c.Icons.Count > 0);
            Check($"{scenes[i].Name}: на карточках теги и иконки", cards.Count > 0 && withIcons > 0,
                  $"карточек {cards.Count}, с тегами {withTags}, с иконками {withIcons}");
            var faded = cards.SelectMany(c => c.Icons).Count(x => x.Faded);
            Console.WriteLine($"  (бледных иконок: {faded})");

            // Текста доводов в карточках больше нет — только теги и иконки.
            var old = Enumerable.Range(0, list.Items.Count)
                .Select(k => list.ItemContainerGenerator.ContainerFromIndex(k) as DependencyObject)
                .Where(c => c is not null).SelectMany(c => Descendants(c!)).OfType<TextBlock>()
                .Count(t => t.Text.StartsWith("•"));
            Check($"{scenes[i].Name}: строк доводов нет", old == 0, $"{old}");
            if (snap is { Length: > 0 }) Snap(list, snap, $"pick-badges-{i + 1}.png");
        }
        w.Close();
    }

    // ── Сцены ─────────────────────────────────────────────────────────────

    private static DraftState Draft(List<DraftPlayer> my, List<DraftPlayer> their, int me, string pos, bool exposed) =>
        new(MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[me], MyPosition: pos, DirectOpponent: null, ExposedToCounter: exposed,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: true, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);

    private static DraftState TopVsDarius() => Draft(
        [new(0, 0, 0, "top", true), new(1, LeeSin, 0, "jungle", false), new(2, Yasuo, 0, "middle", false),
         new(3, Jinx, 0, "bottom", false), new(4, Lulu, 0, "utility", false)],
        [new(5, Darius, 0, "top", false), new(6, Viego, 0, "jungle", false), new(7, Zed, 0, "middle", false),
         new(8, Caitlyn, 0, "bottom", false), new(9, Morgana, 0, "utility", false)],
        0, "top", exposed: false);

    private static DraftState BlindMid() => Draft(
        [new(0, 0, 0, "middle", true), new(1, LeeSin, 0, "jungle", false), new(2, 0, 0, "top", false),
         new(3, 0, 0, "bottom", false), new(4, 0, 0, "utility", false)],
        [new(5, Garen, 0, "", false), new(6, 0, 0, "", false), new(7, 0, 0, "", false),
         new(8, 0, 0, "", false), new(9, 0, 0, "", false)],
        0, "middle", exposed: true);

    private static DraftState SupportVsBot() => Draft(
        [new(0, Orianna, 0, "middle", false), new(1, LeeSin, 0, "jungle", false), new(2, Garen, 0, "top", false),
         new(3, Ezreal, 0, "bottom", false), new(4, 0, 0, "utility", true)],
        [new(5, Darius, 0, "", false), new(6, Viego, 0, "", false), new(7, Ahri, 0, "", false),
         new(8, Caitlyn, 0, "", false), new(9, Morgana, 0, "", false)],
        4, "utility", exposed: false);

    // ── Помощники ─────────────────────────────────────────────────────────

    private static bool IsLink(string kind) =>
        kind.StartsWith("pair.") || kind is "jgSetup" or "adcEngage" or "adcScale" or "wombo";

    private static string Who(Recommendation r) => DataDragon.Name(r.ChampionId);

    private static string Line(Recommendation r)
    {
        var b = r.Badges;
        if (b is null) return "без значков";
        var tags = string.Join(" ", b.Tags.Select(t => $"[{(t.Sign < 0 ? "−" : "+")}{t.Text}]"));
        var icons = string.Join(" ", b.Icons.Select(i => (i.Sign < 0 ? "−" : IsLink(i.Kind) ? "~" : "+") + i.Title));
        return $"{tags}  {icons}";
    }

    private static void Print(Recommendation r) =>
        Console.WriteLine($"  {r.Rank,2}. {Who(r),-12} {r.Score,5:F1}  {Line(r)}");

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

    private static IEnumerable<DependencyObject> Descendants(DependencyObject o)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            yield return c;
            foreach (var d in Descendants(c)) yield return d;
        }
    }

    private static void Snap(FrameworkElement e, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)e.ActualWidth, (int)e.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x07, 0x12, 0x23)), null,
                             new Rect(0, 0, e.ActualWidth, e.ActualHeight));
            dc.DrawRectangle(new VisualBrush(e), null, new Rect(0, 0, e.ActualWidth, e.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
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

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
        }
    }

    private static JsonElement? Get(JsonElement root, string key)
    {
        var cur = root;
        foreach (var part in key.Split('.'))
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) return d.FullName;
        throw new InvalidOperationException("не нашёл Counterplay.csproj");
    }

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: теги и иконки на месте, цифры те же, что в подборе"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
