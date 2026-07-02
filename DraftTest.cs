namespace Counterplay;

/// Диагностический прогон движка на заготовленных сценариях (запуск: --drafttest).
/// Печатает топ рекомендаций с разбивкой скора и причинами — для настройки весов.
static class DraftTest
{
    static DraftState Build(string myPos, (int champ, string pos)[] allies, (int champ, string pos)[] enemies)
    {
        var my = new List<DraftPlayer> { new(0, 0, 0, myPos, true) };
        int c = 1;
        foreach (var (ch, pos) in allies) my.Add(new(c++, ch, 0, pos, false));
        var their = new List<DraftPlayer>();
        foreach (var (ch, pos) in enemies) their.Add(new(c++, ch, 0, pos, false));
        var opp = their.FirstOrDefault(p => p.Position == myPos);
        return new DraftState(my, their, [], [], my[0], myPos, opp, false, false, [], false);
    }

    static string Dmg(int id) =>
        DataDragon.IsApChampion(id) ? "AP" : DataDragon.IsAdChampion(id) ? "AD" : "mix";

    public static async Task Run(RecommendationEngine? engineOverride = null)
    {
        // Дублируем вывод в файл — консоль WPF-процесса ненадёжна при захвате.
        var outPath = Path.Combine(Path.GetTempPath(), "cp_drafttest.txt");
        Console.SetOut(new StreamWriter(outPath) { AutoFlush = true });

        Loc.Init();
        await DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("DB not found"); return; }
        using var eng = engineOverride ?? RecommendationEngine.Create(db, "emerald");

        void Print(string title, DraftState s, int top = 8)
        {
            Console.WriteLine($"\n===== {title} =====");
            var allies = s.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
                                 .Select(p => $"{DataDragon.Name(p.EffectiveChampionId)}[{Dmg(p.EffectiveChampionId)}]");
            var enemies = s.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                     .Select(p => $"{DataDragon.Name(p.EffectiveChampionId)}[{Dmg(p.EffectiveChampionId)}]");
            Console.WriteLine($"  role={s.MyPosition}  allies: {string.Join(", ", allies)}");
            if (s.IsAram)
                Console.WriteLine($"  bench: {string.Join(", ", s.Bench.Select(b => $"{DataDragon.Name(b)}[{Dmg(b)}]"))}");
            else
                Console.WriteLine($"  enemies: {string.Join(", ", enemies)}");
            var recs = s.IsAram ? eng.RecommendAram(s, top) : eng.Recommend(s, top);
            foreach (var r in recs)
            {
                Console.WriteLine(
                    $"  {r.Rank,2}. {DataDragon.Name(r.ChampionId),-13}[{Dmg(r.ChampionId)}] score={r.Score,6:F1} | " +
                    $"base={r.BaseDelta,5:F1} dir={r.DirectDelta,5:F1} oth={r.OtherDelta,5:F1} syn={r.SynergyDelta,5:F1} cmf={r.ComfortDelta,4:F1} sty={r.StyleDelta,5:F1}");
                foreach (var reason in r.Reasons.Take(2))
                    Console.WriteLine($"        · {reason}");
            }
        }

        // 1. ADC, команда уже 3 AP (Акали/Сона/Тимо) + Йорик AD, у врага танки → ждём AD-адк вверх.
        Print("ADC: team 3AP+1AD vs tanky enemies",
            Build("bottom",
                new[] { (84, "middle"), (37, "utility"), (17, "jungle"), (83, "top") },
                new[] { (54, "top"), (113, "jungle"), (103, "middle"), (222, "bottom"), (89, "utility") }));

        // 2. ADC, сбалансированная команда (2 AP + 2 AD) → баланс урона не должен доминировать.
        Print("ADC: balanced team 2AP+2AD",
            Build("bottom",
                new[] { (103, "middle"), (83, "top"), (64, "jungle"), (89, "utility") },
                new[] { (54, "top"), (113, "jungle"), (238, "middle"), (222, "bottom"), (412, "utility") }));

        // 3. MID, сильный прямой оппонент (проверка приоритета контра линии vs база/синергия).
        Print("MID: direct opponent Zed (assassin)",
            Build("middle",
                new[] { (222, "bottom"), (64, "jungle"), (83, "top"), (412, "utility") },
                new[] { (54, "top"), (64, "jungle"), (238, "middle"), (81, "bottom"), (89, "utility") }));

        // 4. SUPPORT, ADC-скейлер Джинкс — проверка структурной синергии с ботом.
        Print("SUPPORT: scaling ADC Jinx (peel synergy)",
            Build("utility",
                new[] { (222, "bottom"), (64, "jungle"), (238, "middle"), (24, "top") },
                new[] { (54, "top"), (60, "jungle"), (103, "middle"), (22, "bottom"), (111, "utility") }));

        // 5. Влияние мастерства (наигранности): те же условия, что в сценарии 1, но
        //    даём высокие очки мастерства на пару ADC — смотрим, как они поднимутся (cmf).
        eng.Mastery = new Dictionary<int, long>
        {
            { 18, 250000 },  // Tristana — очень наигранная (comfort ≈ +6.1)
            { 21, 90000 },   // Miss Fortune — средне наигранная (≈ +4.2)
            { 236, 40000 },  // Lucian — немного (≈ +2.7)
        };
        Print("ADC: MASTERY Tristana 250k / MF 90k / Lucian 40k (team as sc.1)",
            Build("bottom",
                new[] { (84, "middle"), (37, "utility"), (17, "jungle"), (83, "top") },
                new[] { (54, "top"), (113, "jungle"), (103, "middle"), (222, "bottom"), (89, "utility") }), 14);

        // 6. ARAM: команда почти вся AP, нет фронта/саста; на скамейке — микс.
        //    Ждём вверху: фронт+AD (Сион/Леона), затем AD/хил (баланс/дыры), 5-й AP — вниз.
        DraftState BuildAram(int myChamp, (int champ, string pos)[] allies, int[] bench)
        {
            var my = new List<DraftPlayer> { new(0, myChamp, 0, "middle", true) };
            int c = 1;
            foreach (var (ch, pos) in allies) my.Add(new(c++, ch, 0, pos, false));
            return new DraftState(my, [], [], [], my[0], "middle", null, false, false, bench.ToList(), true);
        }

        eng.Mastery = new Dictionary<int, long>(); // сбрасываем наигранность
        Console.WriteLine("\n########## ARAM ##########");
        Print("ARAM: team all-AP (no front/heal); bench = Sion/Caitlyn/Soraka/Leona/Ashe",
            BuildAram(134,  // мой текущий: Syndra (5-й AP)
                new[] { (103, "middle"), (99, "middle"), (63, "middle"), (45, "middle") },
                new[] { 14, 51, 16, 89, 22 }), 8);

        // 7. БАНЫ: мой ховер + союзники наведены, но роли НЕ раскрыты (соло/дуо).
        //    Ждём в списке контр-пики моего Джинкса и наведённых союзников (не только пул).
        eng.Mastery = new Dictionary<int, long>();
        void PrintBans(string title, DraftState s, int top = 6)
        {
            Console.WriteLine($"\n===== BANS: {title} =====");
            var mine = s.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;
            var allies = s.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
                .Select(p => $"{DataDragon.Name(p.EffectiveChampionId)}({(string.IsNullOrEmpty(p.Position) ? "?" : p.Position)})");
            Console.WriteLine($"  role={s.MyPosition} myPick={(mine == 0 ? "-" : DataDragon.Name(mine))} allies: {string.Join(", ", allies)}");
            foreach (var b in eng.RecommendBans(s, top))
            {
                Console.WriteLine($"  {DataDragon.Name(b.ChampionId),-14} score={b.Score,6:F1}");
                foreach (var r in b.Reasons.Take(2)) Console.WriteLine($"        · {r}");
            }
        }
        DraftState BuildBan(string myPos, int myHover, (int champ, string pos)[] allies)
        {
            var my = new List<DraftPlayer> { new(0, 0, myHover, myPos, true) };
            int c = 1;
            foreach (var (ch, pos) in allies) my.Add(new(c++, ch, 0, pos, false));
            return new DraftState(my, [], [], [], my[0], myPos, null, false, true, [], false);
        }
        // 8. Вомбо-состав: команда с AoE-ультами (Малфайт/Кеннен/Свейн/Сона) — ждём
        //    вверх AoE-ульт АДК (МФ/Твич/Сивир) с причиной womboAoe.
        Print("BOT: AoE-ult wombo team (Malphite/Kennen/Swain/Sona)",
            Build("bottom",
                new[] { (54, "jungle"), (85, "top"), (50, "middle"), (37, "utility") },
                new[] { (24, "top"), (64, "jungle"), (103, "middle"), (222, "bottom"), (412, "utility") }), 12);

        Console.WriteLine("\n########## BANS ##########");
        PrintBans("my hover Jinx + allies hovered (Ahri/LeeSin/Malphite), NO roles",
            BuildBan("bottom", 222, new[] { (103, ""), (64, ""), (54, "") }));

        Console.WriteLine("\n(готово)");
    }
}
