namespace Counterplay;

// Окно драфта — почему предмет в сборке: текст подсказки над иконкой предмета.
public partial class OverlayWindow
{
    /// <summary>
    /// Почему предмет стоит в строке подбора под состав. Общие причины строки
    /// (у знака «?») отвечают «зачем вообще подбор», а наведя на иконку, игрок
    /// спрашивает про этот предмет: ради чего он, вместо чего встал и почему
    /// именно он, а не соседний такой же. Предмету из ходовой сборки — что из
    /// состава он уже закрывает или что менять его незачем.
    /// </summary>
    private static string AiItemWhy(int id, BuildAdvisor.ItemWhy? why, bool isCore, ChampStats stats)
    {
        // Подбор не считал этот предмет вовсе (старые данные) — молчать лучше,
        // чем придумывать причину.
        if (why is null) return "";

        var lines = new List<string>();
        if (!why.Added)
        {
            if (why.Answers.Count == 0)
                return Loc.T(isCore ? "build.whyCore" : "build.whyKept");
            lines.Add(Loc.T("build.whyCovers"));
            lines.AddRange(why.Answers.Select(a => "• " + a));
            return string.Join("\n", lines);
        }

        lines.AddRange(why.Answers.Select(a => "• " + a));
        if (why.Replaced != 0)
            lines.Add(why.ReplacedWhy.Length > 0
                ? Loc.T("build.whyInsteadBecause", ItemIcons.NameOf(why.Replaced), why.ReplacedWhy)
                : Loc.T("build.whyInstead", ItemIcons.NameOf(why.Replaced)));

        // Почему именно он: среди подходящих на этом чемпионе он выигрывает
        // чаще всех. Винрейт показываем сырой, а выбирали по сглаженному
        // объёмом, поэтому у запасного варианта процента нет: при малой выборке
        // он бывает выше, и рядом выглядел бы опровержением.
        if (stats.Items.FirstOrDefault(s => s.Id == id) is { } st)
            lines.Add(Loc.T(why.BestOfAll ? "build.whyBestAll" : "build.whyBest",
                            st.Winrate.ToString("0.0"), FormatGames(st.Games)));
        if (why.RunnerUp != 0)
            lines.Add(Loc.T("build.whyNext", ItemIcons.NameOf(why.RunnerUp)));
        return string.Join("\n", lines);
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
