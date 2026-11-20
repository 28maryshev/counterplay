namespace Counterplay;

// Подбор рун — сверка правил по линии с цифрами. Владелец (20.11): «бьёт
// издалека, бьёт с наскока — сейчас похоже перевешивают руны с самым высоким
// винрейтом. А что если те руны сильнее были бы в конкретном случае, даже
// учитывая, что там кто-то бьёт с наскока, — сопоставить и определить, стоит
// ли поменять».
//
// Правило «против наскока — Костяная броня» — из гайдов. Сбор с 19.11 пишет,
// как чемпион выигрывает с каждой мелкой руной против каждого соперника по
// линии (rune_matchup → perkVs в файле связки). Сравниваем в той же ситуации:
// игры против соперников того же склада (наскок / поук — по ChampionTraits),
// с предлагаемой руной и с той, что стоит на странице в её ряду (её ряда на
// странице нет, дерево меняется — со всеми играми без неё). Общие винрейты не
// годятся: ситуативную руну берут в плохих ситуациях, и в общем зачёте она
// всегда хуже.
//
// Цифры и правило смешиваются по объёму: игр мало — меняем по правилу, тысячи
// — решают цифры, между — смесь. Правила по составу (Резня против танков,
// Неудержимость против контроля) так не сверить: данные — только против
// соперников по линии.
public static partial class RuneAdvisor
{
    /// Сколько игр весит правило из гайдов: итог = (n·разница + DataWeight·
    /// RulePrior) / (n + DataWeight). Разброс разницы винрейтов на n играх с
    /// каждой стороны — около 70/√n п.п., а настоящий эффект мелкой руны —
    /// пункт-полтора: на ~2000 играх шум и эффект сравниваются.
    public const double DataWeight = 2000;

    /// На сколько, по гайдам, правильная против такой линии руна лучше, п.п.
    public const double RulePrior = 1.0;

    /// С какого объёма (среднее гармоническое игр двух сторон) цифры
    /// показываются в причине; меньше — это ещё шум, хоть и учтённый.
    public const double MinCite = 500;

    /// Склад соперника по линии, против которого поставлено правило.
    public enum LaneKind { None, AllIn, Poke }

    /// Итог сверки: разница винрейтов «с руной — с прежней» (или «без неё»),
    /// п.п.; объём — среднее гармоническое игр двух сторон; итог — смесь с
    /// правилом; Against — с какой руной сравнивали (0 — с играми без неё);
    /// Total — игр в сравнении всего.
    public sealed record Evidence(double Raw, double Games, double Mixed, int Against, int Total);

    /// Сравнить rune с from (0 — с играми без rune) в играх против соперников
    /// склада kind. null — сравнивать не по чему: правило решает само.
    public static Evidence? Compare(ChampStats stats, int rune, int from, LaneKind kind)
    {
        Func<int, bool> of = kind == LaneKind.AllIn ? ChampionTraits.LaneAllIn : ChampionTraits.LanePoke;
        long gT = 0, wT = 0, gF = 0, wF = 0, gA = 0, wA = 0;
        foreach (var (foe, v) in stats.PerkVs)
        {
            if (!of(foe)) continue;
            gA += v.Games;
            wA += v.Wins;
            if (v.Perks.TryGetValue(rune, out var t)) { gT += t.Games; wT += t.Wins; }
            if (from != 0 && v.Perks.TryGetValue(from, out var f)) { gF += f.Games; wF += f.Wins; }
        }
        // Прежней руны в данных нет (её берут реже 3%) — сравниваем с играми
        // без предлагаемой.
        if (from == 0 || gF == 0)
        {
            from = 0;
            gF = gA - gT;
            wF = wA - wT;
        }
        if (gT == 0 || gF <= 0) return null;
        var raw = 100.0 * wT / gT - 100.0 * wF / gF;
        var n = 2.0 / (1.0 / gT + 1.0 / gF);
        return new Evidence(raw, n, (n * raw + DataWeight * RulePrior) / (n + DataWeight), from, (int)(gT + gF));
    }

    /// Нужное соперникам по линии — сверить с цифрами. Подтверждено или
    /// данных мало — остаётся (цифры «за», если их много, — в evidence: руна →
    /// строка для подсказки); цифры против — убирается, а в kept — что
    /// осталось на странице и почему (Why — правило, Data — цифры).
    private static List<Need> CheckByData(List<Need> needs, ChampStats stats, RunePage page,
                                          Func<int, (int Style, int Row)?> slotOf,
                                          List<string> trace, List<Swap> kept,
                                          Dictionary<int, string> evidence)
    {
        if (stats.PerkVs.Count == 0)
        {
            if (needs.Any(n => n.Lane != LaneKind.None))
                trace.Add("цифр «руна против соперника» в файле нет — руны по линии по правилам");
            return needs;
        }
        var onPage = page.Perks.Skip(1).Concat(page.Secondary).ToList();
        var result = new List<Need>();
        foreach (var n in needs)
        {
            if (n.Lane == LaneKind.None || onPage.Contains(n.Rune))
            {
                result.Add(n);
                continue;
            }
            var slot = slotOf(n.Rune);
            var from = slot is null ? 0 : onPage.FirstOrDefault(p => slotOf(p) == slot);
            var kind = n.Lane == LaneKind.AllIn ? "наскока" : "поука";
            if (Compare(stats, n.Rune, from, n.Lane) is not { } e)
            {
                trace.Add($"{Rune(n.Rune)} против {kind}: игр нет — по правилу");
                result.Add(n);
                continue;
            }
            var vs = e.Against != 0 ? Rune(e.Against) : "игр без неё";
            trace.Add($"{Rune(n.Rune)} против {kind}: {e.Raw:+0.0;-0.0;0.0} п.п. к {vs} "
                      + $"({e.Total:N0} игр, вес {e.Games:N0}) → с правилом {e.Mixed:+0.0;-0.0;0.0} → "
                      + (e.Mixed > 0 ? "ставим" : "не ставим"));
            if (e.Mixed > 0)
            {
                // Цифры «за» и их много — строкой под причиной: так видно, что
                // это не только гайд.
                if (e.Raw > 0 && e.Games >= MinCite)
                    evidence[n.Rune] = e.Against != 0
                        ? Loc.T("runes.dataVs", Pts(e.Raw), Rune(e.Against), e.Total.ToString("N0"))
                        : Loc.T("runes.dataVsRest", Pts(e.Raw), e.Total.ToString("N0"));
                result.Add(n);
                continue;
            }
            kept.Add(e.Against != 0
                ? new Swap(n.Rune, e.Against, n.Why)
                {
                    Data = Loc.T("runes.dataKept", Rune(n.Rune), Pts(-e.Raw), e.Total.ToString("N0")),
                }
                : new Swap(n.Rune, n.Rune, n.Why)
                {
                    Data = Loc.T("runes.dataSkip", Pts(e.Raw), e.Total.ToString("N0")),
                });
        }
        return result;
    }

    /// «+1,8» / «−1,1».
    private static string Pts(double v) => v.ToString("+0.0;−0.0;0.0");
}
