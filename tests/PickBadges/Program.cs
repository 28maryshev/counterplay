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
/// - у каждого показанного кандидата есть значки; тегов с цифрой не больше
///   трёх, «линия» и «вслепую» вместе не бывают;
/// - цифра тега — та же, что в подборе (DirectDelta, OtherDelta, SynergyDelta),
///   знак тега — знак цифры;
/// - иконки по порядку: плюсы, связки с союзниками, минусы;
/// - у каждого вида есть значок и строка (русская и английская);
/// - случай владельца: Мальфит с Ясуо в команде — связка «подброс под ульт»
///   на карточке есть (тегом или иконкой);
/// - вслепую тег совпадает с выводом движка (надёжен / рискован);
/// - красное правда бывает: хотя бы один красный тег и красная иконка;
/// - окно: на карточках теги и иконки, строк доводов и ряда предметов нет;
/// - дыры состава (владелец, 09.11: «если команда фронт-ту-бэк — нужен фронт»,
///   и все варианты из поиска — «делай»): у каждой сцены свой список дыр по
///   важности и красная дыра последнего пика; теги каждой карточки — ровно те,
///   что из него следуют; надбавка в счёт — у закрывающих и только у них;
///   «не тот темп» у гиперкерри в ранней команде; у закрывающих окно не ставит
///   «не в стиле команды».
/// С CP_SNAP — снимки карточек в эту папку.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Malphite = 54, Yasuo = 157, LeeSin = 64, Jinx = 222, Lulu = 117,
                      Darius = 122, Zed = 238, Viego = 234, Caitlyn = 51, Morgana = 25,
                      Ahri = 103, Orianna = 61, Garen = 86, Leona = 89, Ezreal = 81, Thresh = 412,
                      Kayn = 141, Aurora = 893, Evelynn = 28, Mel = 800, Vayne = 67,
                      Kayle = 10, Sejuani = 113, Jayce = 126, Nidalee = 76, Xerath = 101,
                      Riven = 92, Samira = 360, Pyke = 555, Janna = 40, Blitzcrank = 53,
                      JarvanIV = 59, KhaZix = 121, Alistar = 12, Poppy = 78, Taric = 44, Soraka = 16,
                      Renekton = 58, KogMaw = 96, Karthus = 30, AurelionSol = 136, Malphite2 = 54,
                      Galio = 3, Fiora = 114, Graves = 104, Draven = 119;

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
            ("стрелок против Вейн, Эвелинн, Мэл и Лулу", AdcVsVayne()),
            ("топ во фронт-ту-бэк без танка", TopF2bNoTank()),
            ("саппорт в пик/пок без дизэнгейджа", SupportPokeNoDisengage()),
            ("лес в дайв без захода", JungleDiveNoEngage()),
            ("саппорт к Ког'Мао без защиты", SupportKogNoPeel()),
            ("топ к поздней команде против танков", TopLateVsTanks()),
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
        Front(engine, scenes);
        Needs(engine, scenes);
        Tempo(engine);
        Blind(all.Where(x => x.Scene.Contains("вслепую")).Select(x => x.R).ToList());

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window(engine, scenes);
        app.Shutdown();

        try { Directory.Delete(root, recursive: true); } catch { }
        return Finish();
    }

    // ── Строки и значки ───────────────────────────────────────────────────

    private static readonly string[] NeedKinds =
        ["needFront", "needDisengage", "needEngage", "needPeel", "needCc", "needAp", "needAd",
         "needAntiTank", "needWaveclear", "needEarly"];
    private static readonly string[] TagKinds = ["lane", "blind", "team", "synergy", .. NeedKinds];
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
            .. NeedKinds.Select(k => $"reason.{k}"), "reason.needFrontAny", "reason.needOpen",
            "reason.offTempo",
        ];
        // Все 15 языков: строка есть (без неё язык молча берёт английскую) и
        // плейсхолдеры те же, что в английской, — иначе подстановка уронит
        // строку или потеряет цифру.
        static string Holes(string s) =>
            string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
        foreach (var file in Directory.GetFiles(Path.Combine(repo, "assets/i18n"), "*.json").Order())
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            var doc = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var missing = keys.Where(k => Get(doc, k) is null).ToList();
            var holes = keys.Where(k => Get(doc, k) is { } v && Get(en, k) is { } e
                                        && Holes(v.GetString() ?? "") != Holes(e.GetString() ?? "")).ToList();
            Check($"{lang}: все строки тегов и иконок ({keys.Length}), плейсхолдеры как в английском",
                  missing.Count == 0 && holes.Count == 0,
                  missing.Count + holes.Count == 0 ? "да"
                  : string.Join(", ", missing.Take(4).Concat(holes.Take(4).Select(h => h + " — плейсхолдеры"))));
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

        var many = recs.Where(r => r.Badges!.Tags.Count(t => !IsNeed(t.Kind)) > 3
                                || r.Badges.Tags.Count(t => IsNeed(t.Kind) && t.Sign > 0) > 2
                                || r.Badges.Tags.Count(t => IsNeed(t.Kind) && t.Sign < 0) > 1).Select(Who).ToList();
        Check("тегов с цифрой не больше трёх, дыр — не больше двух зелёных и одного красного", many.Count == 0,
              many.Count == 0 ? "да" : string.Join(", ", many));
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

    /// <summary>
    /// «Их фронт»: владелец (07.11) увидел «не пробивает их фронт» у Джинкс против
    /// Вейн, Эвелинн, Мэл и Лулу — «они даже фронтлайн не пикнули, и у Джинкс
    /// есть дальность». Иконки про их фронт — только если у врагов есть танк;
    /// «не пробьёт» — никогда у тех, кто бьёт издалека.
    /// </summary>
    private static void Front(RecommendationEngine engine, (string Name, DraftState Draft)[] scenes)
    {
        var bad = new List<string>();
        foreach (var (name, draft) in scenes)
        {
            var enemies = draft.TheirTeam.Select(p => p.EffectiveChampionId).Where(id => id != 0).ToList();
            var tank = enemies.Any(ChampionTraits.IsTanky);
            foreach (var r in engine.Recommend(draft, 8))
                foreach (var i in r.Badges?.Icons ?? [])
                {
                    if (i.Kind is "antiFront" or "noFront" && !tank) bad.Add($"{Who(r)}: «{i.Title}» без танка у врагов ({name})");
                    if (i.Kind == "noFront" && DataDragon.IsRanged(r.ChampionId)) bad.Add($"{Who(r)}: «{i.Title}» при дальней атаке");
                }
        }
        Check("иконки «их фронт» — только при танке у врагов, «не пробьёт» — не у дальних", bad.Count == 0,
              bad.Count == 0 ? "да" : string.Join("; ", bad.Take(4)));
        var jinx = engine.Recommend(AdcVsVayne(), 1, [Jinx]).FirstOrDefault();
        var front = jinx?.Badges?.Icons.Where(i => i.Kind is "antiFront" or "noFront").Select(i => i.Title).ToList() ?? [];
        Check("Джинкс против Вейн, Эвелинн, Мэл и Лулу — про их фронт ни слова", jinx is not null && front.Count == 0,
              jinx is null ? "нет кандидата" : front.Count == 0 ? Line(jinx) : string.Join(", ", front));
    }

    // Виды дыр и кто их закрывает — те же признаки, что у движка.
    private static readonly (string Kind, Func<int, bool> Fills) GFront = ("needFront", ChampionTraits.Frontline),
        GDis = ("needDisengage", ChampionTraits.Disengager), GEng = ("needEngage", ChampionTraits.Initiator),
        GPeel = ("needPeel", ChampionTraits.Peeler), GCc = ("needCc", ChampionTraits.HasHardCc),
        GAp = ("needAp", DataDragon.IsApChampion), GTank = ("needAntiTank", ChampionTraits.AntiTank),
        GWave = ("needWaveclear", ChampionTraits.Waveclear), GEarly = ("needEarly", id => ChampionTraits.EarlyPower(id) >= 3);

    /// <summary>
    /// Чего сцене не хватает — по важности — и какая дыра у последнего пика
    /// останется красной. Списки выписаны руками из состава сцены (разбор — в
    /// комментариях у сцен), а не повторяют код движка.
    /// </summary>
    private static ((string Kind, Func<int, bool> Fills)[] Gaps, string? Red) Expected(string scene) => scene switch
    {
        "топ против Дариуса, Ясуо в команде"        => ([GFront], "needFront"),
        "мид первым пиком, вслепую"                => ([], null),
        "саппорт против Кейтлин и Морганы"          => ([GDis], "needDisengage"),
        "стрелок против Вейн, Эвелинн, Мэл и Лулу"  => ([], null),
        "топ во фронт-ту-бэк без танка"             => ([GFront], null),
        "саппорт в пик/пок без дизэнгейджа"         => ([GDis, GFront], "needDisengage"),
        "лес в дайв без захода"                     => ([GEng, GFront, GAp, GWave], "needEngage"),
        "саппорт к Ког'Мао без защиты"              => ([GEng, GPeel, GCc, GAp], "needEngage"),
        "топ к поздней команде против танков"       => ([GFront, GPeel, GTank, GEarly], null),
        _ => throw new ArgumentException(scene),
    };

    /// <summary>
    /// Дыры состава (владелец, 09.11: «если например команда фронт-ту-бэк, нужен
    /// фронт», «если собирается определённый стиль — добавить то, чего не
    /// хватает», затем все варианты из поиска — «делай»). По каждой сцене:
    /// зелёные теги карточки — первые две дыры из списка, которые она
    /// закрывает («контроль» рядом с «инициацией» не повторяется), красный — дыра
    /// последнего пика, если карточка её не закрывает; надбавка в счёт — только
    /// у закрывающих взвешенную дыру. Поимённо — у кого тег обязан быть и у кого
    /// его быть не должно (Алистар и Поппи — слово владельца). С танком в
    /// фронт-ту-бэке дыр нет вовсе.
    /// </summary>
    private static void Needs(RecommendationEngine engine, (string Name, DraftState Draft)[] scenes)
    {
        foreach (var (scene, draft) in scenes.Append(("фронт-ту-бэк с Сежуани в лесу", TopF2bNoTank(withTank: true))))
        {
            var (gaps, red) = scene.StartsWith("фронт-ту-бэк с Сежуани") ? ([], null) : Expected(scene);
            var recs = engine.Recommend(draft, 8);
            var wrong = new List<string>();
            foreach (var r in recs)
            {
                var green = new List<string>();
                foreach (var g in gaps.Where(g => g.Fills(r.ChampionId)))
                {
                    if (green.Count >= 2) break;
                    if (g.Kind == "needCc" && green.Contains("needEngage")) continue;
                    green.Add(g.Kind);
                }
                var want = green.Select(k => $"+{k}").ToList();
                if (red is { } rk && !gaps.First(g => g.Kind == rk).Fills(r.ChampionId)) want.Add($"−{rk}");
                var got = r.Badges?.Tags.Where(t => IsNeed(t.Kind)).Select(t => $"{(t.Sign > 0 ? "+" : "−")}{t.Kind}").ToList() ?? [];
                if (!want.SequenceEqual(got))
                    wrong.Add($"{Who(r)}: ждали [{string.Join(" ", want)}], есть [{string.Join(" ", got)}]");
                var weighted = green.Any(k => k is not ("needAp" or "needAd"));
                var bonus = r.Facts?.NeedBonus ?? 0;
                if (weighted != bonus > 0) wrong.Add($"{Who(r)}: надбавка {bonus:0.00} при [{string.Join(" ", green)}]");
            }
            var shown = recs.SelectMany(r => r.Badges?.Tags ?? []).Where(t => IsNeed(t.Kind))
                            .GroupBy(t => (t.Sign > 0 ? "+" : "−") + t.Text).Select(g => $"{g.Key}×{g.Count()}");
            Check($"{scene}: теги дыр и надбавка — как у состава", wrong.Count == 0,
                  wrong.Count == 0 ? (gaps.Length == 0 ? "дыр нет" : string.Join(", ", shown))
                  : string.Join("; ", wrong.Take(3)) + (wrong.Count > 3 ? $" и ещё {wrong.Count - 3}" : ""));
        }

        // Поимённо: тег обязан быть / быть не должно.
        var named = new (string Scene, DraftState Draft, string Kind, int Sign, int[] Yes, int No)[]
        {
            ("фронт-ту-бэк без танка", TopF2bNoTank(), "needFront", 1, [Malphite], Jayce),
            ("пик/пок без дизэнгейджа", SupportPokeNoDisengage(), "needDisengage", 1,
             [Janna, Alistar, Poppy, Taric, Soraka], Blitzcrank),
            ("дайв без захода", JungleDiveNoEngage(), "needEngage", 1, [JarvanIV], KhaZix),
            ("Ког'Мао без защиты", SupportKogNoPeel(), "needPeel", 1, [Janna, Lulu], Blitzcrank),
            ("поздние против танков", TopLateVsTanks(), "needAntiTank", 1, [Fiora, Darius], Garen),
            ("поздние против танков", TopLateVsTanks(), "needEarly", 1, [Renekton], Garen),
            // Последний пик: свой чемпион, посчитанный один (через only), тоже краснеет.
            ("последний пик в пик/поке", SupportPokeNoDisengage(), "needDisengage", -1, [Blitzcrank], Janna),
        };
        foreach (var (scene, draft, kind, sign, yes, no) in named)
        {
            Recommendation? One(int id) => engine.Recommend(draft, 1, [id]).FirstOrDefault();
            bool Has(int id) => One(id)?.Badges?.Tags.Any(t => t.Kind == kind && t.Sign == sign) == true;
            var missed = yes.Where(id => !Has(id)).Select(id => DataDragon.Name(id)).ToList();
            var extra = Has(no);
            var label = $"{(sign > 0 ? "" : "красный ")}«{Loc.T($"pickTag.{kind}")}»";
            Check($"{scene}: {label} есть у {string.Join(", ", yes.Select(id => DataDragon.Name(id)))}, у {DataDragon.Name(no)} — нет",
                  missed.Count == 0 && !extra,
                  missed.Count > 0 ? $"без тега: {string.Join(", ", missed)}"
                  : extra ? $"у {DataDragon.Name(no)} есть"
                  : One(yes[0])?.Badges?.Tags.First(t => t.Kind == kind && t.Sign == sign).Tip ?? "");
        }
    }

    /// <summary>
    /// «Не тот темп»: команда ранняя (Ренектон и Грейвз — агрессоры, поздних нет)
    /// — гиперкерри Джинкс получает красную иконку, Дрейвен (сам агрессор) — нет.
    /// </summary>
    private static void Tempo(RecommendationEngine engine)
    {
        bool Off(int id) => engine.Recommend(AdcEarlyTeam(), 1, [id]).FirstOrDefault()?.Badges?.Icons
                                  .Any(i => i.Kind == "offTempo" && i.Sign < 0) == true;
        Check("ранняя команда: «не тот темп» у Джинкс, у Дрейвена — нет", Off(Jinx) && !Off(Draven),
              $"Джинкс {(Off(Jinx) ? "с иконкой" : "без иконки")}, Дрейвен {(Off(Draven) ? "с иконкой" : "без иконки")}");
        var calm = engine.Recommend(TopVsDarius(), 8).Where(r => r.Badges?.Icons.Any(i => i.Kind == "offTempo") == true)
                         .Select(Who).ToList();
        Check("команда не ранняя — «не тот темп» ни у кого", calm.Count == 0, calm.Count == 0 ? "нет" : string.Join(", ", calm));
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
        TagsFit(w);
        var needCards = 0;
        var offStyleOnNeed = new List<string>();
        var staleStyle = new List<string>();
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
            Brightness(scenes[i].Name, cards);
            // Закрывает дыру — зелёный тег; с одним красным «не в стиле» законно.
            needCards += cards.Count(c => c.Tags.Any(t => IsNeed(t.Kind) && t.Sign > 0));
            offStyleOnNeed.AddRange(cards.Where(c => c.Tags.Any(t => IsNeed(t.Kind) && t.Sign > 0)
                                                  && c.Icons.Any(x => x.Title == Loc.T("pickIcon.offStyle")))
                                         .Select(c => c.Name));
            // «Не в стиле команды» — по стилю союзников ЭТОГО драфта. До 09.11
            // карточки судили по стилю с прошлого обновления: _allyStyle
            // обновлял RenderTeams в самом конце RenderFull.
            var style = ChampionTraits.DominantStyle(
                [.. draft.MyTeam.Select(p => p.EffectiveChampionId).Where(id => id != 0)]);
            staleStyle.AddRange(cards.Where(c => !c.Tags.Any(t => IsNeed(t.Kind) && t.Sign > 0))
                .Where(c => c.Icons.Any(x => x.Title == Loc.T("pickIcon.offStyle"))
                            != (style is { } st && ChampionTraits.ChampArch(c.ChampionId) is { } a && a != st))
                .Select(c => $"{c.Name} ({scenes[i].Name})"));

            // Текста доводов в карточках больше нет — только теги и иконки.
            var old = Enumerable.Range(0, list.Items.Count)
                .Select(k => list.ItemContainerGenerator.ContainerFromIndex(k) as DependencyObject)
                .Where(c => c is not null).SelectMany(c => Descendants(c!)).OfType<TextBlock>()
                .Count(t => t.Text.StartsWith("•"));
            Check($"{scenes[i].Name}: строк доводов нет", old == 0, $"{old}");
            if (snap is { Length: > 0 }) Snap(list, snap, $"pick-badges-{i + 1}.png");
        }
        Check("окно: у закрывающих дыру стиля нет «не в стиле команды»", needCards > 0 && offStyleOnNeed.Count == 0,
              offStyleOnNeed.Count > 0 ? string.Join(", ", offStyleOnNeed)
              : needCards == 0 ? "ни одной карточки с тегом нужды" : $"карточек с тегом нужды {needCards}");
        Check("окно: «не в стиле команды» — по стилю этой команды, а не прошлого обновления", staleStyle.Count == 0,
              staleStyle.Count == 0 ? "да" : string.Join(", ", staleStyle.Take(4)) + (staleStyle.Count > 4 ? $" и ещё {staleStyle.Count - 4}" : ""));
        w.Close();
    }

    /// <summary>
    /// Каждая подпись тега на каждом из 15 языков (с числом) влезает в колонку
    /// карточки: длиннее — WrapPanel обрежет плашку. Мерка — тот же шрифт и
    /// кегль, что в шаблоне (UiFont, 12, SemiBold), плюс значок, отступы и рамка.
    /// </summary>
    private static void TagsFit(Window w)
    {
        const double Column = 230, Chrome = 13 + 5 + 6 + 9 + 2;   // значок, зазор, отступы, рамка
        var font = (System.Windows.Media.FontFamily)w.FindResource("UiFont");
        var face = new Typeface(font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(w).PixelsPerDip;
        var repo = FindRepo();
        var bad = new List<string>();
        double widest = 0; var widestText = "";
        foreach (var file in Directory.GetFiles(Path.Combine(repo, "assets/i18n"), "*.json").Order())
        {
            var lang = Path.GetFileNameWithoutExtension(file);
            var doc = JsonDocument.Parse(File.ReadAllText(file)).RootElement;
            var labels = new List<string>();
            foreach (var sec in new[] { "pickTag", "pairShort" })
                if (doc.TryGetProperty(sec, out var s))
                    foreach (var p in s.EnumerateObject().Where(p => p.Name != "teamTip"))
                    {
                        var v = p.Value.GetString() ?? "";
                        labels.Add(sec == "pairShort" || p.Name == "synergyWord" ? v + " +0,0%"
                                   : v.Replace("{0}", p.Name.StartsWith("blind") ? "00" : "+0,0"));
                    }
            foreach (var text in labels)
            {
                var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Windows.FlowDirection.LeftToRight, face, 12, System.Windows.Media.Brushes.White, dpi);
                var width = ft.WidthIncludingTrailingWhitespace + Chrome;
                if (width > widest) { widest = width; widestText = $"{lang}: «{text}»"; }
                if (width > Column) bad.Add($"{lang}: «{text}» {width:0} px");
            }
        }
        Check($"подписи тегов на всех языках влезают в колонку ({Column:0} px)", bad.Count == 0,
              bad.Count == 0 ? $"самая широкая — {widestText}, {widest:0} px" : string.Join("; ", bad.Take(4)));
    }

    /// <summary>
    /// Яркость тегов (владелец, 07.11): «линия у Кейтлин ярче всех — её тег
    /// самый яркий, у остальных меньше, но не совсем плохо различимо». Внутри
    /// вида и цвета: у сильнейшего — самая яркая, яркость растёт с цифрой,
    /// заметно слабый — заметно тусклее, а текст не бледнее нижнего края.
    /// Лидер во всю силу — с мягким свечением своего цвета (владелец, тот же
    /// день: «лучший тег с самым высоким процентом — небольшое свечение в цвет»).
    /// </summary>
    private static void Brightness(string scene, List<FullRecCard> cards)
    {
        static byte Alpha(System.Windows.Media.Brush b) => ((SolidColorBrush)b).Color.A;
        var problems = new List<string>();
        var shown = new List<string>();
        foreach (var g in cards.SelectMany(c => c.Tags).GroupBy(t => (t.Kind, t.Sign)))
        {
            var byValue = g.OrderBy(t => Math.Abs(t.Value)).ToList();
            var top = byValue[^1];
            if (byValue.Any(t => Alpha(t.Fg) > Alpha(top.Fg))) problems.Add($"{g.Key.Kind}: ярче лидера");
            for (var k = 1; k < byValue.Count; k++)
                if (byValue[k].Strength + 1e-9 < byValue[k - 1].Strength)
                    problems.Add($"{g.Key.Kind}: «{byValue[k].Text}» тусклее «{byValue[k - 1].Text}»");
            if (byValue.Any(t => Alpha(t.Fg) < 0x88)) problems.Add($"{g.Key.Kind}: текст бледнее нижнего края");
            // Свечение — у лидера вида и цвета во всю силу, и только у него.
            var glowing = byValue.Where(t => t.Glow is not null).ToList();
            if (glowing.Any(t => Math.Abs(t.Value) < Math.Abs(top.Value) - 1e-9 || t.Strength < 1 - 1e-9))
                problems.Add($"{g.Key.Kind}: светится не лидер — «{glowing[0].Text}»");
            if (top.Strength >= 1 - 1e-9 && top.Glow is null) problems.Add($"{g.Key.Kind}: лидер «{top.Text}» не светится");
            var low = byValue[0];
            if (byValue.Count > 1 && Math.Abs(top.Value) - Math.Abs(low.Value) >= 1.0 && top.Strength - low.Strength < 0.2)
                problems.Add($"{g.Key.Kind}: «{low.Text}» почти как «{top.Text}»");
            shown.Add($"{g.Key.Kind}{(g.Key.Sign < 0 ? "−" : "+")} " +
                      string.Join(" ", byValue.Select(t => $"{Math.Abs(t.Value):0.0}→{t.Strength:0.00}")));
        }
        Console.WriteLine($"  (сила → яркость: {string.Join("; ", shown)})");
        Check($"{scene}: яркость тегов по силе", problems.Count == 0,
              problems.Count == 0 ? $"сильнейший ярче всех и светится (свечений {cards.SelectMany(c => c.Tags).Count(x => x.Glow is not null)}), слабые тусклее, но читаются"
                                  : string.Join("; ", problems.Take(3)));
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

    // Снимок владельца 07.11: я — стрелок; у врагов Вейн, Эвелинн, Мэл, Лулу (топа нет).
    private static DraftState AdcVsVayne() => Draft(
        [new(0, Darius, 0, "top", false), new(1, Kayn, 0, "jungle", false), new(2, Aurora, 0, "middle", false),
         new(3, 0, 0, "bottom", true), new(4, Leona, 0, "utility", false)],
        [new(5, 0, 0, "top", false), new(6, Evelynn, 0, "jungle", false), new(7, Mel, 0, "middle", false),
         new(8, Vayne, 0, "bottom", false), new(9, Lulu, 0, "utility", false)],
        3, "bottom", exposed: false);

    // Чего не хватает стилю. Враги одни и те же — дело в своих.
    private static List<DraftPlayer> Enemies() =>
        [new(5, Darius, 0, "top", false), new(6, Viego, 0, "jungle", false), new(7, Ahri, 0, "middle", false),
         new(8, Caitlyn, 0, "bottom", false), new(9, Morgana, 0, "utility", false)];

    // Кейл, Джинкс, Лулу — фронт-ту-бэк без танка; лес ещё не выбрал (или Сежуани).
    private static DraftState TopF2bNoTank(bool withTank = false) => Draft(
        [new(0, 0, 0, "top", true), new(1, withTank ? Sejuani : 0, 0, "jungle", false), new(2, Kayle, 0, "middle", false),
         new(3, Jinx, 0, "bottom", false), new(4, Lulu, 0, "utility", false)],
        Enemies(), 0, "top", exposed: false);

    // Джейс, Нидали, Ксерат, Эзреаль — пик/пок, отбиться от захода нечем; танка
    // тоже нет. Контроль у Джейса и Ксерата, урон смешанный, волны чистит Ксерат.
    private static DraftState SupportPokeNoDisengage() => Draft(
        [new(0, Jayce, 0, "top", false), new(1, Nidalee, 0, "jungle", false), new(2, Xerath, 0, "middle", false),
         new(3, Ezreal, 0, "bottom", false), new(4, 0, 0, "utility", true)],
        Enemies(), 4, "utility", exposed: false);

    // Ривен, Зед, Самира, Пайк — дайв, начать драку некому. И танка нет, урон
    // весь физический, волны чистить некому; контроль есть у Ривен и Пайка.
    private static DraftState JungleDiveNoEngage() => Draft(
        [new(0, Riven, 0, "top", false), new(1, 0, 0, "jungle", true), new(2, Zed, 0, "middle", false),
         new(3, Samira, 0, "bottom", false), new(4, Pyke, 0, "utility", false)],
        Enemies(), 1, "jungle", exposed: false);

    // Ренектон, Ка'Зикс, Зед, Ког'Мао — дайв без инициатора; гиперкерри Ког'Мао
    // прикрыть некому; жёсткий контроль один (Ренектон); урон весь физический.
    // Фронт есть — Ренектон.
    private static DraftState SupportKogNoPeel() => Draft(
        [new(0, Renekton, 0, "top", false), new(1, KhaZix, 0, "jungle", false), new(2, Zed, 0, "middle", false),
         new(3, KogMaw, 0, "bottom", false), new(4, 0, 0, "utility", true)],
        Enemies(), 4, "utility", exposed: false);

    // Картус, Аурелион Сол, Джинкс — все поздние, танка нет, Джинкс прикрыть
    // некому; у врагов четыре танка (Мальфит, Сежуани, Галио, Леона), а
    // пробивать их нечем. Стиля явного нет (фронт-ту-бэк лишь чуть впереди).
    private static DraftState TopLateVsTanks() => Draft(
        [new(0, 0, 0, "top", true), new(1, Karthus, 0, "jungle", false), new(2, AurelionSol, 0, "middle", false),
         new(3, Jinx, 0, "bottom", false), new(4, 0, 0, "utility", false)],
        [new(5, Malphite2, 0, "top", false), new(6, Sejuani, 0, "jungle", false), new(7, Galio, 0, "middle", false),
         new(8, Caitlyn, 0, "bottom", false), new(9, Leona, 0, "utility", false)],
        0, "top", exposed: false);

    // Ренектон и Грейвз сильны рано, Орианна и Леона — ровно, поздних нет.
    private static DraftState AdcEarlyTeam() => Draft(
        [new(0, Renekton, 0, "top", false), new(1, Graves, 0, "jungle", false), new(2, Orianna, 0, "middle", false),
         new(3, 0, 0, "bottom", true), new(4, Leona, 0, "utility", false)],
        Enemies(), 3, "bottom", exposed: false);

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

    private static bool IsNeed(string kind) => kind.StartsWith("need");

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
