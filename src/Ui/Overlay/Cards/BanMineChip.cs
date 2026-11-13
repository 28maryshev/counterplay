using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Мой чемпион в полосе над советами банов: иконка, имя, мой винрейт и игры
/// на роли за месяц (у чемпиона из пула без игр — «из пула»). Забанен или взят
/// другим — затемнён и перечёркнут: банить против него уже незачем.
/// </summary>
public sealed class BanMineChip
{
    public int          ChampionId { get; init; }
    public ImageSource? Icon       { get; init; }
    public string       Name       { get; init; } = "";
    public string       WrText     { get; init; } = "";
    public string       WrBrush    { get; init; } = "#8AA0B2";
    public string       GamesText  { get; init; } = "";
    public string?      Tip        { get; init; }
    public bool         Gone       { get; init; }
    public double       ChipOpacity    => Gone ? 0.45 : 1.0;
    public Visibility   GoneVisibility => Gone ? Visibility.Visible : Visibility.Collapsed;
}
