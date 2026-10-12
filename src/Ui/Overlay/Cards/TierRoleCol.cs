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

public sealed class TierRoleCol
{
    public string           RoleLabel { get; init; } = "";
    public ImageSource?     RoleIcon  { get; init; }
    public List<TierCell>   WrCells   { get; init; } = [];   // по винрейту, без грейдов
    public List<TierCell>   TierCells { get; init; } = [];   // по meta-score, с грейдами
}
