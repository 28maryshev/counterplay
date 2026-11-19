namespace Counterplay;

/// <summary>
/// Подбор рун под состав — то же, что <see cref="BuildAdvisor"/> делает с
/// предметами. Владелец (19.11): третьим вариантом в панели рун — Counterplay
/// AI, который подбирает руны, когда пики закончились.
///
/// Основа — первый вариант панели: кейстоун с лучшим винрейтом с поправкой на
/// оппонента и лучшая ходовая страница при нём. Кейстоун подбор не меняет: по
/// составу целиком данных о кейстоунах нет (поправка есть только на прямого
/// оппонента, и она уже в основе), а гадать о нём правилами — значит советовать
/// то, чего никто не играет. Меняются мелкие руны и осколок — так, как их
/// меняют в гайдах под состав:
///   • два танка и больше — Резня вместо соседки по ряду;
///   • ряд защиты Решимости: оппонент по линии бьёт с наскока или у них
///     убийцы — Костяная броня; бьёт издалека или состав на подлове —
///     Второе дыхание. Линия решает раньше состава: этот ряд работает в лейне;
///   • много контроля — Неудержимость и осколок стойкости.
///
/// Два ограничения, без которых совет видно как машинный:
///   • руна меняется только на соседку по РЯДУ того же дерева, что уже на
///     странице: деревья не перестраиваем, страница остаётся такой, какую
///     клиент примет;
///   • только на руну, которую на этом чемпионе с этим кейстоуном правда берут
///     (не реже <see cref="MinShare"/>, доли из выгрузки). Хватка нежити на Эш
///     стрелком — ровно та жалоба, с которой подбор начался. Осколок — не
///     руна чемпиона: стойкость под контроль берут все, и у половины связок
///     она реже 3% только потому, что такой состав встречается не каждую игру.
/// </summary>
public static class RuneAdvisor
{
    /// Замена в странице: что было, что стало и почему. Shard — осколок.
    public sealed record Swap(int From, int To, string Why, bool Shard = false);

    /// Страница под состав. Swaps пуст — менять нечего; Covered — чего состав
    /// требует и что на странице уже стоит: «менять нечего» без объяснения
    /// читалось бы как отказ, а не как вывод.
    public sealed record Advice(RuneChoice Choice, IReadOnlyList<Swap> Swaps, IReadOnlyList<Swap> Covered);

    /// Реже этого руну на чемпионе не советуем, % игр (кейстоуна или вторичного дерева).
    public const double MinShare = 3.0;

    // Руны по номерам Data Dragon. Ряд каждой проверяет справочник (slotOf):
    // переставит Riot руну или заменит — правило просто не сработает.
    public const int CutDown = 8017, BonePlating = 8473, SecondWind = 8444, Unflinching = 8242;

    /// Осколок «стойкость и сопротивление замедлению» — третья ячейка.
    public const int ShardTenacity = 5013;
    private const int DefenseShard = 2;

    /// Подобрать страницу. null — судить не по чему (врагов нет, справочник
    /// рун не загружен).
    public static Advice? Adapt(
        RuneChoice baseChoice, int me, IReadOnlyList<int> enemies, int? laneOpponent,
        Func<int, (double Phys, double Magic, double True)?> damageShare,
        Func<int, (int Style, int Row)?> slotOf)
    {
        if (EnemyComp.Read(enemies, damageShare) is not { } comp) return null;
        if (slotOf(baseChoice.Keystone) is null) return null;

        // ── Что состав требует ────────────────────────────────────────────
        var wants = new List<(int Rune, string Why, bool Shard)>();

        if (comp.Tanks >= 2)
            wants.Add((CutDown, Loc.T("build.vsTank", comp.Tanks), false));

        // Ряд защиты Решимости. Обе руны про размен: Костяная броня режет
        // серию ударов с наскока, Второе дыхание отхиливает то, что прилетело
        // издалека. Решает прежде всего оппонент по линии.
        var opp = laneOpponent is { } o && o != 0 ? o : (int?)null;
        var oppName = opp is { } x ? DataDragon.Name(x) : "";
        if (opp is { } a && ChampionTraits.BurstThreat(a))
            wants.Add((BonePlating, Loc.T("runes.laneAllIn", oppName), false));
        else if (opp is { } p && ChampionTraits.LongRange(p))
            wants.Add((SecondWind, Loc.T("runes.lanePoke", oppName), false));
        else if (comp.Burst >= 2)
            wants.Add((BonePlating, Loc.T("build.vsBurst", comp.Burst), false));
        else if (comp.Style == ChampionTraits.Arch.Dive)
            wants.Add((BonePlating, Loc.T("build.vsDive"), false));
        else if (comp.Style == ChampionTraits.Arch.PickPoke)
            wants.Add((SecondWind, Loc.T("build.vsPoke"), false));

        if (comp.ManyCc(me))
        {
            var why = Loc.T("build.vsCc", comp.Cc);
            wants.Add((Unflinching, why, false));
            wants.Add((ShardTenacity, why, true));
        }

        // ── Что из этого ставится на страницу ─────────────────────────────
        var page = baseChoice.Page;
        var perks = page.Perks.ToList();
        var second = page.Secondary.ToList();
        var shards = page.Shards.ToList();
        var swaps = new List<Swap>();
        var covered = new List<Swap>();

        foreach (var (rune, why, shard) in wants)
        {
            if (shard)
            {
                if (shards.Count <= DefenseShard) continue;
                var was = shards[DefenseShard];
                if (was == rune) { covered.Add(new Swap(was, rune, why, true)); continue; }
                shards[DefenseShard] = rune;
                swaps.Add(new Swap(was, rune, why, true));
                continue;
            }

            if (slotOf(rune) is not { Row: > 0 } slot) continue;
            List<int>? row = null;
            IReadOnlyDictionary<int, double>? share = null;
            if (slot.Style == page.Primary)
            {
                row = perks;
                share = baseChoice.Share;
            }
            else if (slot.Style == page.Sub)
            {
                row = second;
                // Доли есть, а этого дерева в них нет — значит, его не берут.
                share = baseChoice.SubShare is { } sub ? sub.GetValueOrDefault(page.Sub) ?? NoShare : null;
            }
            if (row is null) continue;                  // этого дерева на странице нет

            var i = row.FindIndex(r => slotOf(r) == slot);
            if (i < 0) continue;                        // вторичное дерево без этого ряда
            if (row[i] == rune) { covered.Add(new Swap(rune, rune, why)); continue; }
            if (!Taken(share, rune)) continue;

            swaps.Add(new Swap(row[i], rune, why));
            row[i] = rune;
        }

        var choice = swaps.Count == 0 ? baseChoice : baseChoice with
        {
            Page = page with { Perks = perks, Secondary = second, Shards = shards },
        };
        return new Advice(choice, swaps, covered);
    }

    private static readonly IReadOnlyDictionary<int, double> NoShare = new Dictionary<int, double>();

    /// Берут ли эту руну на чемпионе. Долей нет (файл до 19.11) — верим правилу.
    private static bool Taken(IReadOnlyDictionary<int, double>? share, int rune) =>
        share is null || share.GetValueOrDefault(rune) >= MinShare;
}
