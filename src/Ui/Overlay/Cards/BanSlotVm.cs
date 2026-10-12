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

/// <summary>Слот в панели банов команд: забаненный чемпион с бан-рейтом или пустой кружок.</summary>
public sealed class BanSlotVm
{
    public ImageSource? Icon        { get; init; }
    public string       Name        { get; init; } = "";
    public string       Rate        { get; init; } = "";
    public string       RateBrush   { get; init; } = "#8AA0B2";
    public string       Frame       { get; init; } = "#3A4B5F";   // цвет команды
    public string       Placeholder { get; init; } = "";          // «…» банят / «?» скрыто
    public string?      Tip         { get; init; }                // у пустого нет: пустая подсказка — рамка без текста
    public bool         IsEmpty     => Name.Length == 0;
    // Забанили моего мейна: игр на нём за месяц (0 — не мейн). Золотая рамка
    // со скруглёнными углами и мягким свечением, золотое имя.
    public int          MainGames   { get; init; }
    public bool         IsMyMain    => MainGames > 0;
    public Visibility   MainVisibility => IsMyMain ? Visibility.Visible : Visibility.Collapsed;
    public string       NameBrush   => IsMyMain ? "#F5D77A" : "#9FB4C6";
    public Visibility   IconVisibility  => IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility   EmptyVisibility => IsEmpty ? Visibility.Visible   : Visibility.Collapsed;
}
