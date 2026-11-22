using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Союзник показал чемпиона, но ещё не взял. Владелец (22.11.2026): «в
/// определённой степени будет срабатывать синергия с ним, а по большей части
/// работать блайндпик, потому что ты пикаешь первый»; на парах линий — стрелок
/// и саппорт, лес и мид, лес и топ — синергия повышенная, и в списке часть
/// чемпионов надёжны вслепую, а часть помечена «Хорош с показанным: …». До
/// того наведённый считался как взятый, и на раннем пике наведённый стрелок
/// решал список саппорта целиком.
///
/// 1. Строки тега и подсказки на всех языках.
/// 2. Всё, что оценка берёт от союзника, у наведённого напарника по паре
///    линий — ровно HOVER_TRUST_PAIR (0,6) от взятого, у прочих — HOVER_TRUST
///    (0,4): сдвиг оценки каждого кандидата против пустого слота и полоска
///    синергии. Пустой слот — с этим чемпионом в банах: наведённый и взятый
///    оба убраны из поля соперника вслепую, и это не часть веса.
/// 3. Два наведённых — напарник и прочий: каждый со своим весом (смесь
///    наборов в Shown.cs).
/// 4. Наведённый в свой ход (чемпион в слоте, ход не кончился) — наведённый.
/// 5. Союзник быстро перебирает (HeldPicks) — тот, на ком держим подбор, с
///    весом наведённого.
/// 6. Взявший рядом с наведённым: взявший — целиком.
/// 7. Напарник по пати — целиком, даже наведённый: договариваетесь голосом.
/// 8. Подсказка напарнику: мой наведённый пик — целиком, как взятый.
/// 9. Тег «Хорош с показанным: Юнара» — у тех, кто с ней заметно лучше, пока
///    она наведена; у взятой и у наведённого не напарника — нет.
///
/// Плюс замер для справки: по шести парам — сколько из первых шести под
/// показанного, сколько с тегом, сколько надёжных вслепую.
///
/// База матчей — настоящая, только чтение; пулы, пати и история — свои.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Yunara = 804, Jinx = 222, Samira = 360, Ezreal = 81, Kaisa = 145;
    private const int Braum = 201, LeeSin = 64, Malphite = 54, Ahri = 103;
    private const long MeId = 1001, FriendId = 1002;
    private const double Trust = 0.4, TrustPair = 0.6;   // HOVER_TRUST, HOVER_TRUST_PAIR в Weights.cs

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "HoverTrust");
        if (Directory.Exists(AppPaths.RootOverride)) Directory.Delete(AppPaths.RootOverride, recursive: true);
        Directory.CreateDirectory(AppPaths.RootOverride);
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = real;   // база матчей — настоящая, только чтение
        // Справочник имён — копией к себе: в выводе нужны имена, а не номера.
        var dd = Path.Combine(AppPaths.RootOverride, "ddragon");
        Directory.CreateDirectory(dd);
        foreach (var f in new[] { "champion-ru_RU.json", "champion-ru_RU.ver" })
        {
            var src = Path.Combine(real, "ddragon", f);
            if (File.Exists(src)) File.Copy(src, Path.Combine(dd, f), overwrite: true);
        }
        PoolStore.DirOverride = Path.Combine(AppPaths.RootOverride, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");
        DataDragon.LoadFromCache("ru_RU");

        Strings();
        var db = RecommendationEngine.FindDb();
        if (db is null) { Check("база матчей есть", false, "подбор не проверить"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        Weight(engine);
        TwoHovered(engine);
        OwnTurn(engine);
        Held(engine);
        LockedAndHovered(engine);
        Mate(engine);
        MateHint(engine);
        Tag(engine);
        Measure(engine);
        return Done();
    }

    // ── 1. Строки ───────────────────────────────────────────────────────────

    private static void Strings()
    {
        Console.WriteLine("── строки ──");
        // Строки встроены в программу ресурсом — читаем их из репозитория.
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "assets", "i18n")))
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (dir is null) { Check("папка строк найдена", false, AppContext.BaseDirectory); return; }
        var docs = Directory.GetFiles(Path.Combine(dir, "assets", "i18n"), "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        foreach (var key in new[] { "pickTag.shown", "reason.shownPair" })
        {
            var bad = docs.Where(kv => Get(kv.Value, key) is not { } v || Slots(v) != "{0},{1}")
                          .Select(kv => kv.Key).ToList();
            Check($"{key}: на всех языках, с двумя местами", docs.Count == 15 && bad.Count == 0,
                  bad.Count == 0 ? $"{docs.Count} языков" : string.Join(", ", bad));
        }

        static string? Get(JsonElement root, string key)
        {
            var parts = key.Split('.');
            return root.TryGetProperty(parts[0], out var sec) && sec.TryGetProperty(parts[1], out var v)
                ? v.GetString() : null;
        }
        static string Slots(string s) => string.Join(",", Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
    }

    // ── 2. Вес наведённого ──────────────────────────────────────────────────

    private static void Weight(RecommendationEngine engine)
    {
        Console.WriteLine("── наведённый весит долю взятого ──");
        foreach (var adc in new[] { Yunara, Jinx, Samira, Ezreal, Kaisa })
            Share(engine, $"саппорту — наведённый стрелок {DataDragon.Name(adc)}", Ally(1, adc, "bottom", false), TrustPair);
        // Прочие линии саппорту не пара: топ и мид — 0,4.
        Share(engine, "саппорту — наведённый топ (не пара)", Ally(3, Malphite, "top", false), Trust);
        Share(engine, "саппорту — наведённый мид (не пара)", Ally(4, Ahri, "middle", false), Trust);
        // Пары в обе стороны: стрелку — саппорт, миду и топу — лес, лесу — мид и топ.
        Share(engine, "стрелку — наведённый саппорт", Ally(1, Braum, "utility", false), TrustPair, "bottom");
        Share(engine, "миду — наведённый лес", Ally(1, LeeSin, "jungle", false), TrustPair, "middle");
        Share(engine, "топу — наведённый лес", Ally(1, LeeSin, "jungle", false), TrustPair, "top");
        Share(engine, "лесу — наведённый мид", Ally(1, Ahri, "middle", false), TrustPair, "jungle");
        Share(engine, "лесу — наведённый топ", Ally(1, Malphite, "top", false), TrustPair, "jungle");
        Share(engine, "лесу — наведённый саппорт (не пара)", Ally(1, Braum, "utility", false), Trust, "jungle");
    }

    /// Сдвиг оценки каждого кандидата от наведённого — ровно trust от сдвига
    /// от взятого; полоска синергии — так же.
    private static void Share(RecommendationEngine engine, string what, DraftPlayer hovered, double trust,
                              string myPos = "utility")
    {
        var champ = hovered.PickIntentId;
        var locked = hovered with { ChampionId = champ, PickIntentId = 0 };
        var none = Scores(engine, Draft(myPos) with { MyTeamBans = [champ] });
        var h    = Scores(engine, Draft(myPos, hovered));
        var l    = Scores(engine, Draft(myPos, locked));
        var ids = h.Keys.Intersect(l.Keys).Intersect(none.Keys).ToList();
        var worst = ids.Max(id => Math.Abs((h[id].Score - none[id].Score) - trust * (l[id].Score - none[id].Score)));
        var worstSyn = ids.Max(id => Math.Abs((h[id].SynergyDelta - none[id].SynergyDelta)
                                              - trust * (l[id].SynergyDelta - none[id].SynergyDelta)));
        var moved = ids.Count(id => Math.Abs(l[id].Score - none[id].Score) > 0.01);
        Check($"{what}: сдвиг и синергия — {trust:F1} от взятого",
              ids.Count > 20 && moved > 10 && worst < 1e-9 && worstSyn < 1e-9,
              $"кандидатов {ids.Count}, сдвинутых {moved}, расхождение {worst:E1} / {worstSyn:E1}");
    }

    // ── 3. Два наведённых ───────────────────────────────────────────────────

    private static void TwoHovered(RecommendationEngine engine)
    {
        Console.WriteLine("── наведены стрелок (пара) и мид (не пара) ──");
        var adcH = Ally(1, Yunara, "bottom", false);
        var midH = Ally(4, Ahri, "middle", false);
        var adcL = adcH with { ChampionId = Yunara, PickIntentId = 0 };
        var midL = midH with { ChampionId = Ahri, PickIntentId = 0 };
        var both    = Scores(engine, Draft("utility", adcH, midH));
        var all     = Scores(engine, Draft("utility", adcL, midL));
        var adcOnly = Scores(engine, Draft("utility", adcL) with { MyTeamBans = [Ahri] });
        var none    = Scores(engine, Draft("utility") with { MyTeamBans = [Yunara, Ahri] });
        // Вложенная смесь: оба — 0,4; только стрелок — 0,6 − 0,4; никого — 0,4.
        var ids = both.Keys.Intersect(all.Keys).Intersect(adcOnly.Keys).Intersect(none.Keys).ToList();
        var worst = ids.Max(id => Math.Abs(both[id].Score
            - (Trust * all[id].Score + (TrustPair - Trust) * adcOnly[id].Score + (1 - TrustPair) * none[id].Score)));
        Check("стрелок — 0,6, мид — 0,4, оба сразу", ids.Count > 20 && worst < 1e-9,
              $"кандидатов {ids.Count}, расхождение {worst:E1}");
    }

    // ── 4. Наведённый в свой ход ────────────────────────────────────────────

    private static void OwnTurn(RecommendationEngine engine)
    {
        Console.WriteLine("── наведённый в свой ход ──");
        // В свой ход клиент кладёт наведённого в championId ещё до лока.
        var ownTurn = Draft("utility", new DraftPlayer(1, Yunara, 0, "bottom", false)) with { ActiveCells = [1] };
        var hover   = Scores(engine, Draft("utility", Ally(1, Yunara, "bottom", false)));
        var turn    = Scores(engine, ownTurn);
        Check("в свой ход до лока — как наведённый", Same(turn, hover), Diff(turn, hover));
        var done   = Scores(engine, ownTurn with { ActiveCells = [] });
        var locked = Scores(engine, Draft("utility", Ally(1, Yunara, "bottom", true)));
        Check("ход кончился — взятый", Same(done, locked), Diff(done, locked));
    }

    // ── 5. Быстро перебирает ────────────────────────────────────────────────

    private static void Held(RecommendationEngine engine)
    {
        Console.WriteLine("── союзник быстро перебирает ──");
        var held = Draft("utility", Ally(1, Jinx, "bottom", false))
                   with { HeldPicks = new Dictionary<int, int> { [1] = Yunara } };
        var a = Scores(engine, held);
        var b = Scores(engine, Draft("utility", Ally(1, Yunara, "bottom", false)));
        Check("держим подбор на Юнаре — как наведённая Юнара", Same(a, b), Diff(a, b));
    }

    // ── 6. Взявший и наведённый вместе ──────────────────────────────────────

    private static void LockedAndHovered(RecommendationEngine engine)
    {
        Console.WriteLine("── взявший лесник и наведённый стрелок ──");
        var jg    = Ally(2, LeeSin, "jungle", true);
        var only  = Scores(engine, Draft("utility", jg) with { MyTeamBans = [Yunara] });
        var mixed = Scores(engine, Draft("utility", jg, Ally(1, Yunara, "bottom", false)));
        var both  = Scores(engine, Draft("utility", jg, Ally(1, Yunara, "bottom", true)));
        var ids = mixed.Keys.Intersect(both.Keys).Intersect(only.Keys).ToList();
        var worst = ids.Max(id => Math.Abs((mixed[id].Score - only[id].Score)
                                           - TrustPair * (both[id].Score - only[id].Score)));
        Check("от лесника — целиком, от наведённого стрелка — 0,6", worst < 1e-9,
              $"кандидатов {ids.Count}, расхождение {worst:E1}");
        var none = Scores(engine, Draft("utility") with { MyTeamBans = [Yunara] });
        var moved = ids.Count(id => none.ContainsKey(id) && Math.Abs(only[id].Score - none[id].Score) > 0.01);
        Check("взявший лесник двигает оценку (иначе пункт выше ничего не проверял)", moved > 20,
              $"сдвинутых {moved}");
    }

    // ── 7. Напарник по пати ─────────────────────────────────────────────────

    private static void Mate(RecommendationEngine engine)
    {
        Console.WriteLine("── напарник по пати ──");
        Party.Update(Json(Lobby(MeId, FriendId)));
        PoolStore.Current().DuoPools.Add(new DuoPool
        {
            Id = "t", FriendName = "друг",
            Mine = new() { ["support"] = [Braum] }, Friend = new() { ["adc"] = [Yunara] },
        });
        PoolStore.SetActive(PoolKind.Duo, "t");
        try
        {
            var hover  = Scores(engine, Draft("utility", Friend(locked: false)));
            var locked = Scores(engine, Draft("utility", Friend(locked: true)));
            Check("наведённый напарник — как взятый", Same(hover, locked), Diff(hover, locked));
            var stranger = Scores(engine, Draft("utility", Ally(1, Yunara, "bottom", false)));
            var strangerLocked = Scores(engine, Draft("utility", Ally(1, Yunara, "bottom", true)));
            Check("а не напарник — с весом наведённого", !Same(stranger, strangerLocked),
                  Diff(stranger, strangerLocked));
        }
        finally
        {
            PoolStore.SetActive(PoolKind.Duo, null);
            Party.Update(Json(Lobby(MeId)));
        }
    }

    // ── 8. Подсказка напарнику ──────────────────────────────────────────────

    private static void MateHint(RecommendationEngine engine)
    {
        Console.WriteLine("── подсказка напарнику ──");
        var half = new Dictionary<string, List<int>> { ["adc"] = [Yunara, Jinx, Samira, Ezreal, Kaisa] };
        DraftState Mine(bool locked)
        {
            var me = new DraftPlayer(0, locked ? Braum : 0, locked ? 0 : Braum, "utility", true);
            return Draft("utility") with { MyTeam = [me], Me = me };
        }
        var hover  = engine.PartnerScores(Mine(false), half, "adc");
        var locked = engine.PartnerScores(Mine(true), half, "adc");
        var worst = hover.Keys.Max(id => Math.Abs(hover[id] - locked[id]));
        Check("мой наведённый Браум — как взятый", hover.Count == 5 && worst < 1e-9,
              string.Join(", ", hover.Select(kv => $"{DataDragon.Name(kv.Key)} {kv.Value:F2}/{locked[kv.Key]:F2}")));
    }

    // ── 9. Тег «Хорош с показанным» ─────────────────────────────────────────

    /// Тег синергии с именем чемпиона: у обычного тега синергии имени нет
    /// («Синергия +2,1%», «Хук + добивание +1,4%»).
    private static PickTag? ShownTag(Recommendation r, int champ) =>
        r.Badges?.Tags.FirstOrDefault(t => t.Kind == "synergy" && t.Text.Contains(DataDragon.Name(champ)));

    private static void Tag(RecommendationEngine engine)
    {
        Console.WriteLine("── тег «Хорош с показанным» ──");
        var hover = engine.Recommend(Draft("utility", Ally(1, Yunara, "bottom", false)), 8);
        var tagged = hover.Where(r => ShownTag(r, Yunara) is not null).ToList();
        var braum = engine.Recommend(Draft("utility", Ally(1, Yunara, "bottom", false)), 1, [Braum]).Single();
        var bt = ShownTag(braum, Yunara);
        Check("у Браума с наведённой Юнарой — тег с её именем",
              bt is not null && bt.Text.StartsWith(Loc.T("pickTag.shown", DataDragon.Name(Yunara), "")[..10]),
              bt is not null ? $"«{bt.Text}», подсказка: {bt.Tip.Replace("\n", " | ")}"
                             : string.Join(", ", braum.Badges?.Tags.Select(t => t.Text) ?? []));
        Check("в восьмёрке помечена часть, а не все", tagged.Count > 0 && tagged.Count < hover.Count,
              $"{tagged.Count} из {hover.Count}: " + string.Join(", ", tagged.Select(r => DataDragon.Name(r.ChampionId))));

        var locked = engine.Recommend(Draft("utility", Ally(1, Yunara, "bottom", true)), 8);
        Check("взятая Юнара — тега нет, обычная синергия", locked.All(r => ShownTag(r, Yunara) is null),
              string.Join(", ", locked.SelectMany(r => r.Badges?.Tags.Where(t => t.Kind == "synergy").Select(t => t.Text) ?? [])));
        var top = engine.Recommend(Draft("utility", Ally(3, Malphite, "top", false)), 8);
        Check("наведённый топ саппорту не пара — тега нет", top.All(r => ShownTag(r, Malphite) is null), "");
    }

    // ── Замер для справки ───────────────────────────────────────────────────

    private static void Measure(RecommendationEngine engine)
    {
        Console.WriteLine("── замер: я пикаю первым, врагов нет, напарник по паре навёл ──");
        var cases = new (string Me, string Pos, string PPos, int[] Champs)[]
        {
            ("саппорт", "utility", "bottom",  [Yunara, Jinx, Samira, Ezreal, Kaisa]),
            ("стрелок", "bottom",  "utility", [117, 111, 412, 267, 89]),
            ("мид",     "middle",  "jungle",  [64, 59, 234, 60, 121]),
            ("лес",     "jungle",  "middle",  [157, 61, 103, 517, 163]),
            ("лес",     "jungle",  "top",     [122, 54, 24, 164, 516]),
            ("топ",     "top",     "jungle",  [64, 59, 234, 60, 121]),
        };
        static bool Safe(Recommendation r) => r.Badges?.Tags.Any(t => t.Kind == "blind" && t.Sign > 0) == true;
        foreach (var c in cases)
        {
            double fresh = 0, safe = 0, tag = 0;
            foreach (var ch in c.Champs)
            {
                var none = engine.Recommend(Draft(c.Pos) with { MyTeamBans = [ch] }, 6)
                                 .Select(r => r.ChampionId).ToHashSet();
                var h = engine.Recommend(Draft(c.Pos, Ally(1, ch, c.PPos, false)), 6);
                fresh += h.Count(r => !none.Contains(r.ChampionId));
                safe  += h.Count(Safe);
                tag   += h.Count(r => ShownTag(r, ch) is not null);
                if (ch == c.Champs[0])
                    Console.WriteLine($"   {c.Me} с наведённым {DataDragon.Name(ch)}: " + string.Join(", ", h.Select(r =>
                        DataDragon.Name(r.ChampionId)
                        + (ShownTag(r, ch) is not null ? " [с показанным]" : "")
                        + (Safe(r) ? " [надёжен вслепую]" : ""))));
            }
            var n = c.Champs.Length;
            Console.WriteLine($"   {c.Me,-7} напарник {c.PPos,-7}: из шести под показанного {fresh / n:F1}, " +
                              $"с тегом {tag / n:F1}, надёжных вслепую {safe / n:F1}");
        }
    }

    // ── драфты ──────────────────────────────────────────────────────────────

    private static DraftPlayer Ally(int cell, int champ, string pos, bool locked) =>
        new(cell, locked ? champ : 0, locked ? 0 : champ, pos, false);

    private static DraftPlayer Friend(bool locked) =>
        new(1, locked ? Yunara : 0, locked ? 0 : Yunara, "bottom", false, FriendId, $"puuid-{FriendId}");

    /// Я на позиции pos пикаю первым, врагов не видно.
    private static DraftState Draft(string pos, params DraftPlayer[] allies)
    {
        var me = new DraftPlayer(0, 0, 0, pos, true, MeId, $"puuid-{MeId}");
        return new DraftState(
            MyTeam: [me, .. allies], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: pos, DirectOpponent: null,
            ExposedToCounter: true, InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static Dictionary<int, Recommendation> Scores(RecommendationEngine e, DraftState s) =>
        e.Recommend(s, 300).ToDictionary(r => r.ChampionId);

    private static bool Same(Dictionary<int, Recommendation> a, Dictionary<int, Recommendation> b) =>
        a.Count == b.Count && a.Keys.All(id => b.TryGetValue(id, out var r) && Math.Abs(r.Score - a[id].Score) < 1e-9);

    private static string Diff(Dictionary<int, Recommendation> a, Dictionary<int, Recommendation> b)
    {
        var ids = a.Keys.Intersect(b.Keys).ToList();
        return ids.Count == 0 ? "пусто" : $"наибольшая разница {ids.Max(id => Math.Abs(a[id].Score - b[id].Score)):F3}";
    }

    private static string Lobby(long me, params long[] mates)
    {
        var members = string.Join(",", new[] { me }.Concat(mates).Select(
            id => $$"""{"summonerId":{{id}},"puuid":"puuid-{{id}}","gameName":"Игрок{{id}}","tagLine":"RU"}"""));
        return $$"""{"localMember":{"summonerId":{{me}},"puuid":"puuid-{{me}}"},"members":[{{members}}]}""";
    }

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: наведённый напарник по паре весит 0,6 от взятого, прочие — 0,4, тег на месте"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
