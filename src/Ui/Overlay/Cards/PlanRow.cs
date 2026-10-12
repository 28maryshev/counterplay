using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

/// <summary>
/// Строка «мои чемпионы» в фазе объявления: мой винрейт и игры на роли за
/// месяц, форма, LP, винрейт в патче со сдвигом, метка частого бана.
/// </summary>
public sealed class PlanRow
{
    public int          ChampionId { get; init; }
    public bool         IsSelected { get; init; }   // показан союзникам (рамка)
    public string       CardBg     => IsSelected ? "#26C89B3C" : "#0CFFFFFF";
    public string       CardBorder => IsSelected ? "#F0C24B" : "#00000000";
    public string       Ring       => IsSelected ? "#F0C24B" : "#3A4B5F";
    public ImageSource? Icon       { get; init; }
    public string       Name       { get; init; } = "";
    public int          Games      { get; init; }   // 0 — чемпион из пула без игр
    public string       Note       { get; init; } = "";   // «банят 24%» / «из пула»
    public string       NoteBrush  { get; init; } = "#6E8598";
    public Visibility   NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string       WrText     { get; init; } = "";
    public string       WrBrush    { get; init; } = "#8AA0B2";
    public string       GamesText  { get; init; } = "";
    public IReadOnlyList<string> Form { get; init; } = [];   // цвета квадратиков, старые слева
    public string       LpText     { get; init; } = "";
    public string       LpBrush    { get; init; } = "#8AA0B2";
    public string       PatchText  { get; init; } = "";
    public string       PatchBrush { get; init; } = "#8AA0B2";
    public string       DeltaText  { get; init; } = "";
    public string       DeltaBrush { get; init; } = "#6E8598";
    public string?      Tip        { get; init; }
}
