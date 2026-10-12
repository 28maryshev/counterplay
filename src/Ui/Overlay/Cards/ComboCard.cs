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

public sealed class ComboCard
{
    public string            Title       { get; init; } = "";
    public List<ImageSource> Icons       { get; init; } = [];
    public string            Description { get; init; } = "";
    public string            Tip         { get; init; } = "";
    public string            TipLabel    { get; init; } = "";
    public string            AccentColor { get; init; } = "#C89B3C";
    public string            LineColor   { get; init; } = "#8B7CF6"; // цвет коннектор-линии
    // Связка появится, только если взять наведённого кандидата — показываем её
    // бледной, чтобы не путать с уже собранными.
    public bool              Ghost       { get; init; }
    public double            CardOpacity => Ghost ? 0.55 : 1.0;
}
