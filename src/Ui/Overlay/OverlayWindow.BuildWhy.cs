using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Окно драфта — почему предмет в сборке: текст подсказки над иконкой предмета
// и список «предмет — почему он» в подсказке строки подбора.
public partial class OverlayWindow
{
    /// Предмет в подсказке строки подбора: иконка, название и почему именно он.
    /// Название бирюзовое у поставленного подбором (как его рамка в строке),
    /// приглушённое — у оставленного из стандартной сборки.
    public sealed record WhyItemVm(ImageSource? Icon, string Name, Brush NameBrush, string Text);

    private static readonly Brush KeptName = new SolidColorBrush(Color.FromRgb(0xC8, 0xD3, 0xDD));

    /// <summary>
    /// Почему предмет стоит в строке подбора под состав — для подсказки над
    /// иконкой. Наведя на иконку, игрок спрашивает про этот предмет: ради чего
    /// он, вместо чего встал и почему именно он, а не соседний такой же.
    /// Предмету из ходовой сборки — что из состава он уже закрывает или что
    /// менять его незачем.
    /// </summary>
    private static string AiItemWhy(int id, BuildAdvisor.ItemWhy? why, bool isCore, ChampStats stats) =>
        // Подбор не считал этот предмет вовсе — молчать лучше, чем придумывать.
        why is null ? "" : string.Join("\n", WhyLines(id, why, isCore, stats, compact: false));

    /// <summary>
    /// Строки причины. Полные — для подсказки иконки: задачи списком и
    /// запасной вариант. Короткие — для списка в подсказке строки: задачи
    /// через запятую, без запасного, а у оставленного из ходовой — одной
    /// строкой «уже в сборке: …».
    /// </summary>
    private static List<string> WhyLines(int id, BuildAdvisor.ItemWhy why, bool isCore,
                                         ChampStats stats, bool compact)
    {
        var lines = new List<string>();
        if (!why.Added)
        {
            if (why.Answers.Count == 0)
                lines.Add(Loc.T(isCore ? "build.whyCore" : "build.whyKept"));
            else if (compact)
                lines.Add(Loc.T("build.whyHas", string.Join(Loc.ListSep, why.Answers)));
            else
            {
                lines.Add(Loc.T("build.whyCovers"));
                lines.AddRange(why.Answers.Select(a => "• " + a));
            }
            return lines;
        }

        if (compact && why.Answers.Count > 0)
            lines.Add(Capitalize(string.Join(Loc.ListSep, why.Answers)));
        else if (!compact)
            lines.AddRange(why.Answers.Select(a => "• " + a));
        if (why.Replaced != 0)
            lines.Add(why.ReplacedWhy.Length > 0
                ? Loc.T("build.whyInsteadBecause", ItemIcons.NameOf(why.Replaced), why.ReplacedWhy)
                : Loc.T("build.whyInstead", ItemIcons.NameOf(why.Replaced)));
        // Ответа на задачу у чемпиона нет вовсе — так и сказать, иначе предмет
        // выглядит ответом на то, на что он не отвечает (Мандат «против магов»).
        if (why.NoFit.Length > 0)
            lines.Add(Capitalize(why.NoFit));

        // Почему именно он: среди подходящих на этом чемпионе он выигрывает
        // чаще всех. Винрейт показываем сырой, а выбирали по сглаженному
        // объёмом, поэтому у запасного варианта процента нет: при малой выборке
        // он бывает выше, и рядом выглядел бы опровержением.
        if (stats.Items.FirstOrDefault(s => s.Id == id) is { } st)
            lines.Add(Loc.T(why.BestOfAll ? "build.whyBestAll" : "build.whyBest",
                            st.Winrate.ToString("0.0"), FormatGames(st.Games)));
        if (!compact && why.RunnerUp != 0)
            lines.Add(Loc.T("build.whyNext", ItemIcons.NameOf(why.RunnerUp)));
        return lines;
    }

    /// <summary>
    /// Список для подсказки строки подбора (владелец, 20.11: «название
    /// предмета и почему именно он»). Общие причины («против маг. урона»)
    /// стояли рядом с набором, и предмет, на них не отвечающий, читался как
    /// ответ. Сначала поставленные подбором — в порядке покупки, — затем
    /// оставленные из ходовой, если они что-то из состава уже закрывают.
    /// </summary>
    private static List<WhyItemVm> RowWhy(IReadOnlyList<int> items,
                                          IReadOnlyDictionary<int, BuildAdvisor.ItemWhy>? why,
                                          ISet<int> core, ChampStats stats)
    {
        if (why is null) return [];
        return items.Take(6).Distinct()
            .Where(id => why.TryGetValue(id, out var w) && (w.Added || w.Answers.Count > 0))
            .OrderBy(id => why[id].Added ? 0 : 1)       // устойчивая: порядок покупки внутри
            .Select(id => new WhyItemVm(
                ItemIcons.GetOrLoad(id), ItemIcons.NameOf(id),
                why[id].Added ? AiBrush : KeptName,
                string.Join("\n", WhyLines(id, why[id], core.Contains(id), stats, compact: true))))
            .ToList();
    }

    /// <summary>
    /// Почему предмет в ходовой сборке. Основа — набор, который сыграли вместе:
    /// у него и винрейт сборки. Остальное — частые докупки, которыми набор
    /// добит до шести, со своим винрейтом на этом чемпионе. Нет числа — нет и
    /// подписи: «частая докупка» без цифр ничего не объясняет.
    /// </summary>
    private static string BuildItemWhy(int id, bool isCore, BuildData b, ChampStats stats)
    {
        if (isCore && b.Core.Count > 0)
            return Loc.T("build.whyBuildCore", b.Winrate.ToString("0.0"), FormatGames(b.Games));
        return stats.Items.FirstOrDefault(s => s.Id == id) is { } st
            ? Loc.T("build.whyBuildItem", st.Winrate.ToString("0.0"), FormatGames(st.Games))
            : "";
    }
}
