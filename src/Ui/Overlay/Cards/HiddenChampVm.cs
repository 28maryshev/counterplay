using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Иконка в полосе «Скрыты»: чемпион, которого игрок попросил не советовать.
/// Kind и Role — чтобы клик вернул именно эту запись.
/// </summary>
public sealed class HiddenChampVm
{
    public int           ChampionId { get; init; }
    public Dislikes.Kind Kind       { get; init; }
    public string        Role       { get; init; } = "";
    public ImageSource?  Icon       { get; init; }
    public string        Tip        { get; init; } = "";
    // Кольцо — цвет вида: баны красные, пики золотые (как сами карточки).
    public string        Ring       => Kind == Dislikes.Kind.Ban ? "#E0584F" : "#C89B3C";
}
