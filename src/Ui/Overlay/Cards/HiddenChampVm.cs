using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Скрытый кнопкой «не советовать»: иконка в полосе «Скрыты» и строка в
/// списке под кнопками шапки тир-листа. Kind и Role — чтобы клик вернул
/// именно эту запись.
/// </summary>
public sealed class HiddenChampVm
{
    public int           ChampionId { get; init; }
    public Dislikes.Kind Kind       { get; init; }
    public string        Role       { get; init; } = "";
    public ImageSource?  Icon       { get; init; }
    public string        Tip        { get; init; } = "";
    public string        Name       { get; init; } = "";
    public string        Status     { get; init; } = "";   // «игр до возвращения: 7» / «возвращается…»
    // Кольцо — цвет вида: баны красные, пики золотые (как сами карточки).
    public string        Ring       => Kind == Dislikes.Kind.Ban ? "#E0584F" : "#C89B3C";
}
