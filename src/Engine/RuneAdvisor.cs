namespace Counterplay;

/// <summary>
/// Подбор рун под соперников — то же, что <see cref="BuildAdvisor"/> делает с
/// предметами. Владелец (19.11): третьим вариантом в панели рун — Counterplay
/// AI, который подбирает руны после финальных пиков; «относительно соперников,
/// и не только основные руны, но и вспомогательные».
///
/// Что откуда берётся:
///   • кейстоун — по ДАННЫМ: винрейт кейстоуна плюс поправка против оппонента
///     по линии (keystone_matchup). Против другого оппонента лучшим может
///     оказаться другой кейстоун, и вместе с ним — другая ходовая страница;
///   • мелкие руны основного дерева — правилами по составу и оппоненту, на
///     соседку по ряду;
///   • вторичное дерево и обе его руны — так же, и дерево можно сменить: против
///     Зеда на линии Вдохновение уступает Решимости с Костяной бронёй, если
///     чемпион с этим кейстоуном Решимость играет;
///   • осколок защиты — стойкость против контроля.
/// Цифр «руна против соперника» для мелких рун в матчах нет (собирается только
/// кейстоун против оппонента), поэтому их выбор — правила, как в гайдах:
///   • танки — Резня; толстых нет совсем — Удар милосердия;
///   • оппонент по линии бьёт с наскока, у них убийцы или дайв — Костяная
///     броня; бьёт издалека или состав на подлове — Второе дыхание, а у
///     Доминирования и Вдохновения — Вкус крови и Печенье. Кто как бьёт —
///     ручные списки ChampionTraits.LanePoke / LaneAllIn, собранные по гайдам;
///   • много контроля — Неудержимость и осколок стойкости; иначе против
///     оппонента с наскока — осколок плоского здоровья вместо растущего.
/// Оппонент по линии весомее состава: эти руны работают в лейне.
///
/// Два ограничения, без которых совет видно как машинный:
///   • руна — только на своё место (ряд своего дерева): страница остаётся
///     такой, какую клиент примет;
///   • только то, что на чемпионе с этим кейстоуном правда берут: руна — не
///     реже <see cref="MinShare"/>, вторичное дерево — не реже
///     <see cref="MinTree"/> (доли из выгрузки; в старом файле долей нет —
///     правило работает, а дерево не меняется). Хватка нежити на Эш стрелком —
///     ровно та жалоба, с которой подбор начался. Осколок — не руна чемпиона:
///     стойкость под контроль берут все.
/// </summary>
public static partial class RuneAdvisor
{
    /// Замена в странице: что было, что стало и почему. Why пустой — руна
    /// встала вместе со сменой дерева, своей причины у неё нет.
    public sealed record Swap(int From, int To, string Why, bool Shard = false)
    {
        /// Цифры к причине отдельной строкой (RuneAdvisor.Data.cs): «против
        /// таких с ней +1,8 п.п. к «Второе дыхание»», «оставили: …». Пусто —
        /// причина только по правилу.
        public string Data { get; init; } = "";
    }

    /// Страница под соперников. Swaps пуст — менять нечего; Covered — чего
    /// состав требует и что на странице уже стоит: «менять нечего» без
    /// объяснения читалось бы как отказ. KeystoneWhy — чем выбранный
    /// кейстоун лучше: против оппонента или винрейтом (null — он и так ходовой);
    /// Trace — разбор для песочницы.
    public sealed record Advice(
        RuneChoice Choice, IReadOnlyList<Swap> Swaps, IReadOnlyList<Swap> Covered,
        string? KeystoneWhy, IReadOnlyList<string> Trace)
    {
        /// Что правило по линии требовало, а цифры против таких соперников не
        /// подтвердили: руна страницы осталась (To — она, From — непоставленная)
        /// или руну не ставим (From = To) — с причиной (RuneAdvisor.Data.cs).
        public IReadOnlyList<Swap> Kept { get; init; } = [];
    }

    /// Кейстоун лучше прочих против оппонента хотя бы на столько, п.п., —
    /// тогда это и причина. Меньше — шум: поправки уже ужаты по объёму.
    public const double MinKeystoneEdge = 0.5;

    /// Реже этого руну на чемпионе не советуем, % игр (кейстоуна или вторичного дерева).
    public const double MinShare = 3.0;

    /// Вторичное дерево — не реже этого, % игр кейстоуна.
    public const double MinTree = 10.0;

    // Смена дерева должна чего-то стоить: иначе подбор менял бы его на равных
    // ради одной слабой причины, и страница выглядела бы случайной.
    private const double TreeSwitchGain = 0.3;

    // Руны по номерам Data Dragon. Ряд каждой проверяет справочник (slotOf):
    // переставит Riot руну или заменит — правило просто не сработает.
    public const int CutDown = 8017, CoupDeGrace = 8014;
    public const int BonePlating = 8473, SecondWind = 8444, Unflinching = 8242;
    public const int TasteOfBlood = 8139, Biscuits = 8345;

    /// Осколки третьей ячейки: «стойкость и сопротивление замедлению»,
    /// плоское здоровье, здоровье с ростом по уровню.
    public const int ShardTenacity = 5013, ShardHealth = 5011, ShardHealthScaling = 5001;
    private const int DefenseShard = 2;

    /// Lane — правило по линии (против наскока / поука): его сверяют с цифрами.
    private sealed record Need(int Rune, double Weight, string Why, LaneKind Lane = LaneKind.None);

    /// Подобрать страницу. laneOpponent — прямой оппонент (по нему кейстоун:
    /// поправки в данных — против него); laneFoes — кто бьёт меня на линии (по
    /// ним руны защиты): на топе и в миде — он же, на боте — вражеские стрелок
    /// и саппорт (линия два на два: Леона рядом с Кейтлин — это наскок), в лесу
    /// — никого (линии нет, руны защиты — по составу). null — судить не по
    /// чему (врагов нет, рун нет, справочник рун не загружен).
    public static Advice? Adapt(
        ChampStats stats, int me, IReadOnlyList<int> enemies, int? laneOpponent,
        IReadOnlyList<int> laneFoes,
        Func<int, (double Phys, double Magic, double True)?> damageShare,
        Func<int, (int Style, int Row)?> slotOf)
    {
        if (stats.Keystones.Count == 0) return null;
        if (EnemyComp.Read(enemies, damageShare) is not { } comp) return null;
        var opp = laneOpponent is { } o && o != 0 ? o : (int?)null;
        var foes = laneFoes.Where(f => f != 0).Distinct().ToList();
        var trace = new List<string>();

        // ── Кейстоун: по данным против соперников на линии ────────────────
        // На боте их двое (владелец, 20.11: «руны на боте должны учитывать
        // пики двух врагов»): к поправке против оппонента той же роли
        // прибавляется поправка против второго бот-лейнера (VsDuo).
        int? duo = stats.Role is "adc" or "support"
            && foes.FirstOrDefault(f => f != opp) is var d and not 0 ? d : null;
        var duoKnown = duo is { } dk && stats.VsDuo.ContainsKey(dk);
        // Все кейстоуны по порядку (база + поправки): первый — подбор, самый
        // ходовой — то, с чем его сравнивать в причине.
        var all = RunesClient.Choices(stats, opp, int.MaxValue, duo);
        var choice = all[0];
        if (slotOf(choice.Keystone) is null) return null;
        var meta = all.MaxBy(k => k.PickRate)!;
        string? ksWhy = null;
        // Причина — чем он правда лучше. Не сырая поправка против оппонента:
        // в ней и сама линия, и «Завоеватель — против Зиггса −2,3» читалось
        // как совет взять руну в минусе (владелец, 20.11), хотя в минусе там
        // все. Лучше прочих против оппонента — так и пишем; иначе он взят за
        // винрейт — пишем, чем лучше ходового; он и есть ходовой — молчим.
        if (opp is { } x && choice.VsEdge >= MinKeystoneEdge)
            ksWhy = Loc.T("runes.aiKsEdge",
                          duoKnown ? Both(x, duo!.Value) : DataDragon.Name(x),
                          choice.VsEdge.ToString("0.0"));
        else if (choice.Keystone != meta.Keystone)
            ksWhy = Loc.T("runes.aiKsWr", Rune(meta.Keystone),
                          (choice.Winrate + choice.VsDelta).ToString("0.0"),
                          (meta.Winrate + meta.VsDelta).ToString("0.0"));
        trace.Add(opp is { } y
            ? $"оппонент по линии: {DataDragon.Name(y)}"
              + (stats.Vs.ContainsKey(y) ? "" : " (поправок кейстоунов против него нет)")
              + (duo is { } dd ? $"; второй бот-лейнер: {DataDragon.Name(dd)}"
                                 + (duoKnown ? "" : " (поправок против него нет)") : "")
            : "оппонент по линии не известен");
        trace.Add(foes.Count == 0
            ? (stats.Role == "jungle" ? "лес: линии нет, руны защиты — по составу" : "соперников на линии не знаем")
            : "на линии: " + string.Join(", ", foes.Select(f => DataDragon.Name(f)
                + (ChampionTraits.LanePoke(f) ? " (издалека)" : ChampionTraits.LaneAllIn(f) ? " (с наскока)" : " (тихо)")
                + (ChampionTraits.LaneKnown(f) ? "" : " — нет в списках, по меткам"))));
        trace.Add("кейстоуны: " + string.Join(", ", all.Select(k =>
                      $"{Rune(k.Keystone)} {k.Winrate:0.0}%"
                      + (k.VsGames > 0 ? $" {k.VsDelta:+0.0;-0.0;0.0} (лучше прочих {k.VsEdge:+0.0;-0.0;0.0})" : "")
                      + $", берут {k.PickRate:0}%"))
                  + $" → {Rune(choice.Keystone)}");
        trace.Add($"состав: танков {comp.Tanks}, контроля {comp.Cc} (стойкость от {(EnemyComp.Close(me) ? 2 : 3)}), "
                  + $"убийц {comp.Burst}, стиль {comp.Style?.ToString() ?? "смешанный"}");

        var needs = Needs(comp, enemies, me, foes, choice.Page);
        // Правила по линии — сверить с цифрами: вдруг руна страницы против
        // таких соперников на этом чемпионе выигрывает чаще (RuneAdvisor.Data.cs).
        var kept = new List<Swap>();
        var evidence = new Dictionary<int, string>();
        needs = CheckByData(needs, stats, choice.Page, slotOf, trace, kept, evidence);
        trace.Add(needs.Count == 0 ? "состав ничего не требует"
            : "нужно: " + string.Join(", ", needs.Select(n => $"{Rune(n.Rune)} {n.Weight:0.#}")));

        var page = choice.Page;
        var swaps = new List<Swap>();
        var covered = new List<Swap>();

        // ── Основное дерево: по ряду ──────────────────────────────────────
        var perks = page.Perks.ToList();
        for (var i = 1; i < perks.Count; i++)
        {
            if (slotOf(perks[i]) is not { } slot) continue;
            var best = needs.Where(n => slotOf(n.Rune) == slot)
                            .OrderByDescending(n => n.Weight).FirstOrDefault();
            if (best is null) continue;
            if (best.Rune == perks[i]) { covered.Add(new Swap(best.Rune, best.Rune, best.Why)); continue; }
            if (!Taken(choice.Share, best.Rune))
            {
                trace.Add($"{Rune(best.Rune)} на чемпионе почти не берут — не ставим");
                continue;
            }
            swaps.Add(new Swap(perks[i], best.Rune, best.Why));
            perks[i] = best.Rune;
        }

        // ── Вторичное дерево: какое и какие две руны ──────────────────────
        var trees = new List<int> { page.Sub };
        if (choice.SubPick is { } picks)
            trees.AddRange(picks.Where(t => t.Value >= MinTree && t.Key != page.Sub && t.Key != page.Primary)
                                .Select(t => t.Key));
        var plans = trees.Select(t => Secondary(t, page, choice, needs, slotOf))
                         .OfType<Plan>().ToList();
        var stay = plans.FirstOrDefault(p => p.Tree == page.Sub);
        var go = plans.Where(p => p.Tree != page.Sub).OrderByDescending(p => p.Score).FirstOrDefault();
        var plan = go is not null && go.Score > (stay?.Score ?? 0) + TreeSwitchGain ? go : stay;
        if (plans.Count > 1)
            trace.Add("вторичное: " + string.Join(", ", plans.Select(p => $"{Tree(p.Tree)} {p.Score:0.#}"))
                      + (plan == go ? $" → меняем на {Tree(go!.Tree)}" : " → остаётся"));
        else if (choice.SubPick is null)
            trace.Add("долей деревьев в файле нет — вторичное дерево не меняем");

        var sub = page.Sub;
        var second = page.Secondary.ToList();
        if (plan is not null)
        {
            foreach (var n in plan.NotTaken) trace.Add($"{Rune(n)} с этим деревом на чемпионе почти не берут — не ставим");
            var same = plan.Tree == page.Sub;
            for (var k = 0; k < plan.Perks.Count; k++)
            {
                var (rune, why) = plan.Perks[k];
                if (same && page.Secondary.Contains(rune))
                {
                    if (why.Length > 0) covered.Add(new Swap(rune, rune, why));
                    continue;
                }
                swaps.Add(new Swap(k < page.Secondary.Count ? page.Secondary[k] : 0, rune, why));
            }
            sub = plan.Tree;
            second = [.. plan.Perks.Select(p => p.Rune)];
        }

        // ── Осколок защиты ────────────────────────────────────────────────
        // Много контроля — стойкость. Иначе, если оппонент по линии заходит с
        // наскока, — плоское здоровье вместо растущего: оно есть с первой
        // минуты, когда и решается линия (так советуют против агрессивных
        // линий). Растущее на странице остаётся у тех, против кого линия тихая.
        var shards = page.Shards.ToList();
        var bullies = foes.Where(ChampionTraits.LaneAllIn).ToList();
        var shardWant = comp.ManyCc(me) ? (ShardTenacity, Loc.T("build.vsCc", comp.Cc))
            : bullies.Count > 0 && shards.Count > DefenseShard && shards[DefenseShard] == ShardHealthScaling
                ? (ShardHealth, LaneWhy(bullies, "runes.laneAllIn", "runes.laneAllInTwo"))
                : ((int, string)?)null;
        if (shardWant is (int shard, string shardWhy) && shards.Count > DefenseShard)
        {
            var was = shards[DefenseShard];
            if (was == shard) covered.Add(new Swap(was, shard, shardWhy, true));
            else
            {
                shards[DefenseShard] = shard;
                swaps.Add(new Swap(was, shard, shardWhy, true));
            }
        }

        // Цифры, подтвердившие правило, — к своей замене.
        swaps = [.. swaps.Select(s => !s.Shard && evidence.TryGetValue(s.To, out var d) ? s with { Data = d } : s)];

        var result = swaps.Count == 0 ? choice : choice with
        {
            Page = page with { Sub = sub, Perks = perks, Secondary = second, Shards = shards },
        };
        return new Advice(result, swaps, covered, ksWhy, trace) { Kept = kept };
    }

    /// Чего требуют соперники: руна, вес, причина. Одна руна — один раз, с
    /// самым весомым поводом.
    private static List<Need> Needs(EnemyComp comp, IReadOnlyList<int> enemies, int me, IReadOnlyList<int> foes,
                                    RunePage page)
    {
        var list = new List<Need>();
        var onPage = page.Perks.Concat(page.Secondary).ToHashSet();

        // Руны урона — по всему составу: в драках бьёшь всех, а не только
        // соперника по линии (руны защиты ниже — по линии). Владелец (21.11):
        // «а если будет 3 танка, а на боте сквиши?» — стрелку Реванш будет.
        // Уточнения того же дня («примени свои предложения»):
        //  • Резня — от двух танков (владелец: «2 танка мне кажется достаточно,
        //    чтоб брать ту руну»), вместо любой руны своего ряда;
        //  • убийцы и взрывные маги (BurstThreat) бьют хлипких в тылу — Резня
        //    им, только когда хлипких почти не осталось (от 4 танков);
        //  • танков нет — Удар милосердия только вместо Резни: Последний
        //    рубеж не руна «против толстых», и гайды менять его не требуют
        //    (прогон по всем файлам 21.11: «Последний рубеж → Удар милосердия»
        //    был второй по частоте заменой).
        var tanksFor = ChampionTraits.BurstThreat(me) ? 4 : 2;
        if (comp.Tanks >= tanksFor)
            list.Add(new(CutDown, 1.0 + 0.2 * (comp.Tanks - tanksFor), Loc.T("build.vsTank", comp.Tanks)));
        else if (enemies.Count > 0 && !enemies.Any(ChampionTraits.IsTanky) && onPage.Contains(CutDown))
            list.Add(new(CoupDeGrace, 0.5, Loc.T("runes.vsSquishy")));

        // Линия: Костяная броня режет серию ударов с наскока, Второе дыхание,
        // Вкус крови и Печенье отхиливают то, что прилетело издалека.
        // Кто как бьёт — ручные списки ChampionTraits.LanePoke / LaneAllIn (как
        // в гайдах), важнее меток: «взрывные» артиллерийские маги — это поук.
        // Соперников двое (бот) — наскок важнее: он и убивает на линии, а поук
        // второго переживается отхилом после. Бьют так оба — причина называет
        // обоих.
        var allIn = foes.Where(ChampionTraits.LaneAllIn).ToList();
        var pokers = foes.Where(ChampionTraits.LanePoke).ToList();
        if (allIn.Count > 0)
            list.Add(new(BonePlating, 1.5, LaneWhy(allIn, "runes.laneAllIn", "runes.laneAllInTwo"), LaneKind.AllIn));
        else if (pokers.Count > 0)
        {
            // Три руны — один ответ (отхил после размена), и вес у них один:
            // иначе подбор менял бы Вдохновение с Печеньем на Решимость ради
            // Второго дыхания, хотя Печенье закрывает то же самое.
            var why = LaneWhy(pokers, "runes.lanePoke", "runes.lanePokeTwo");
            list.Add(new(SecondWind, 1.5, why, LaneKind.Poke));
            list.Add(new(TasteOfBlood, 1.5, why, LaneKind.Poke));
            list.Add(new(Biscuits, 1.5, why, LaneKind.Poke));
        }

        // Состав — слабее линии.
        if (comp.Burst >= 2)
            list.Add(new(BonePlating, 1.0, Loc.T("build.vsBurst", comp.Burst)));
        else if (comp.Style == ChampionTraits.Arch.Dive)
            list.Add(new(BonePlating, 0.8, Loc.T("build.vsDive")));
        else if (comp.Style == ChampionTraits.Arch.PickPoke)
            list.Add(new(SecondWind, 0.8, Loc.T("build.vsPoke")));

        if (comp.ManyCc(me))
            list.Add(new(Unflinching, 1.0 + 0.2 * (comp.Cc - (EnemyComp.Close(me) ? 2 : 3)),
                         Loc.T("build.vsCc", comp.Cc)));

        return [.. list.GroupBy(n => n.Rune).Select(g => g.OrderByDescending(n => n.Weight).First())];
    }

    private sealed record Plan(int Tree, IReadOnlyList<(int Rune, string Why)> Perks, double Score,
                               IReadOnlyList<int> NotTaken);

    private sealed record Pick(int Rune, string Why, double Weight, double Keep);

    /// Две руны вторичного дерева tree из разных рядов. В каждом ряду — нужная
    /// соперникам руна (если её на чемпионе с этим деревом берут), иначе
    /// прежняя руна страницы, иначе самая ходовая. Пара рядов — та, что
    /// закрывает больше нужного; при равенстве — где больше прежних рун. null —
    /// дерево не собрать (долей нет, а прежних рун в нём нет).
    private static Plan? Secondary(int tree, RunePage page, RuneChoice choice, List<Need> needs,
                                   Func<int, (int Style, int Row)?> slotOf)
    {
        // Доли есть, а этого дерева в них нет — значит, его не берут.
        var share = choice.SubShare is { } all ? all.GetValueOrDefault(tree) ?? NoShare : null;
        var same = tree == page.Sub;
        var rows = new SortedDictionary<int, Pick>();
        var notTaken = new List<int>();
        for (var r = 1; r <= 3; r++)
        {
            var row = (tree, r);
            var need = needs.Where(n => slotOf(n.Rune) == row)
                            .OrderByDescending(n => n.Weight).FirstOrDefault();
            if (need is not null && !Taken(share, need.Rune))
            {
                notTaken.Add(need.Rune);
                need = null;
            }
            var kept = same ? page.Secondary.FirstOrDefault(s => slotOf(s) == row) : 0;
            if (need is not null)
            {
                rows[r] = new(need.Rune, need.Why, need.Weight, need.Rune == kept ? 0.02 : 0);
                continue;
            }
            if (kept != 0)
            {
                rows[r] = new(kept, "", 0, 0.02);
                continue;
            }
            // Самая ходовая руна ряда — только по долям: без них не знаем, что ставят.
            if (share is null) continue;
            var top = share.Where(kv => slotOf(kv.Key) == row && kv.Value >= MinShare)
                           .OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Key != 0) rows[r] = new(top.Key, "", 0, top.Value / 10000);
        }
        if (rows.Count < 2) return null;

        double Value(Pick p) => p.Weight + p.Keep;
        var keys = rows.Keys.ToList();
        var pair = keys.SelectMany((a, i) => keys.Skip(i + 1).Select(b => (a, b)))
                       .OrderByDescending(ab => Value(rows[ab.a]) + Value(rows[ab.b]))
                       .First();
        // Порядок — как в странице клиента: ряд выше — раньше.
        var perks = new List<(int, string)> { (rows[pair.a].Rune, rows[pair.a].Why), (rows[pair.b].Rune, rows[pair.b].Why) };
        return new Plan(tree, perks, rows[pair.a].Weight + rows[pair.b].Weight, notTaken);
    }

    private static readonly IReadOnlyDictionary<int, double> NoShare = new Dictionary<int, double>();

    /// Берут ли эту руну на чемпионе. Долей нет (файл до 19.11) — верим правилу.
    private static bool Taken(IReadOnlyDictionary<int, double>? share, int rune) =>
        share is null || share.GetValueOrDefault(rune) >= MinShare;

    // Названия — только для разбора в песочнице.
    private static string Rune(int id) => RuneIcons.NameOf(id);

    /// «Зиггс и Вел'Коз» — двое соперников на боте.
    private static string Both(int a, int b) => Loc.T("runes.both", DataDragon.Name(a), DataDragon.Name(b));

    /// Причина по линии: один соперник — «Зиггс на линии бьёт издалека», двое
    /// так бьют — «Зиггс и Вел'Коз на линии бьют издалека».
    private static string LaneWhy(List<int> who, string one, string two) =>
        who.Count >= 2 ? Loc.T(two, DataDragon.Name(who[0]), DataDragon.Name(who[1]))
                       : Loc.T(one, DataDragon.Name(who[0]));
    private static string Tree(int style) => RuneIcons.StyleName(style);
}
