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

public sealed class RecCard
{
    public int          ChampionId { get; init; }   // для бана из оверлея
    public bool         IsSelected { get; init; }   // наведён кликом (рамка)
    // Совет бана, который уже забанил союзник: карточка на месте, но
    // затемнена и перечёркнута, как забаненные в тир-листе.
    public bool         Gone       { get; init; }
    public double       CardOpacity     => Gone ? 0.5 : 1;
    public Visibility   GoneVisibility  => Gone ? Visibility.Visible : Visibility.Collapsed;
    public Visibility   BadgeVisibility => Gone ? Visibility.Collapsed : Visibility.Visible;
    // Кнопка «не советовать» в углу (совет бана): у перечёркнутого её нет —
    // его и так уже забанили.
    public bool         CanDislike => !Gone && ChampionId > 0;
    public string       DislikeTip => Loc.T("dislike.tip", Dislikes.HideGames);
    public string       CardBg     => IsSelected ? "#2AC84040" : "#1EC84040";
    public string       CardBorder => IsSelected ? "#F0684F" : "#00000000";
    public string       Rank       { get; init; } = "";
    public string       Name       { get; init; } = "";
    public string       Score      { get; init; } = "";
    public string       ScoreColor { get; init; } = "#C89B3C";
    public string       Reason     { get; init; } = "";
    public ImageSource? Icon       { get; init; }
}
