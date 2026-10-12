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

public sealed class ChampSlotCard
{
    public string            Name        { get; init; } = "";
    public string            Role        { get; init; } = "";
    // Кликабельная роль врага (ручное назначение): cellId слота, -1 = не кликается.
    public int               RoleCellId  { get; init; } = -1;
    public string            RoleColor   { get; init; } = "#8AA0B2";
    public string?           RoleTip     { get; init; }   // null — без тултипа (союзники)
    public ImageSource?      Icon        { get; init; }
    public double            Opacity     { get; init; } = 1.0;
    public string            BorderColor { get; init; } = "#C89B3C";
    public string            NameColor   { get; init; } = "White";
    public bool              IsEmpty     { get; init; }
    public string            SideLabel   { get; init; } = "";
    public List<ImageSource> SideIcons   { get; init; } = [];
    public Visibility        SideVisibility =>
        SideIcons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Значок архетипа чемпиона (камень/ножницы/бумага).
    public string      ArchGlyph      { get; init; } = "";
    public string      ArchColor      { get; init; } = "#888888";
    public string      ArchTip        { get; init; } = "";
    public Visibility  ArchVisibility => ArchGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool        ArchStrong     { get; init; }   // задаёт стиль команды
    public bool        ArchDim        { get; init; }   // выбивается из стиля
    public double      ArchOpacity    => ArchDim ? 0.75 : 1.0;
    public string      ArchRing       => ArchStrong ? "#F5D77A" : "#00000000";
    public double      ArchRingWidth  => ArchStrong ? 2.0 : 0.0;


    // Выделение слота локального игрока (мой пик)
    public bool         IsMe            { get; init; }
    public double       BorderThickness { get; init; } = 2.0;
    public ImageSource? RoleIcon        { get; init; }  // иконка роли в пустом моём слоте
    public Visibility   RoleIconVisibility =>
        RoleIcon != null ? Visibility.Visible : Visibility.Collapsed;

    // Ход этого игрока пикать прямо сейчас → подсветка строки цветом команды:
    // вспышка → волна-градиент от края (левый у союзников, правый у врагов).
    public bool         IsPicking       { get; init; }
    public Visibility   PickVisibility  => IsPicking ? Visibility.Visible : Visibility.Collapsed;
    public string       PickGradStrong  { get; init; } = "#00000000"; // край волны (ярко)
    public string       PickGradWeak    { get; init; } = "#00000000"; // центр (не до нуля)
    public string       PickAccentColor { get; init; } = "#00000000"; // полоска + вспышка
    public double       PickMirror      { get; init; } = 1.0;         // -1 = волна справа (враг)
    // Стартовые значения трансформов. Через свойства (Binding), чтобы WPF не
    // заморозил трансформы в шаблоне — замороженные анимировать нельзя.
    public double       PickScaleX      => 0;    // волна начинается с нулевой ширины
    public double       SweepStartX     => -90;  // блик стартует за левым краем
    // Бейдж «1st pick» у игрока, пикающего первым в очереди драфта.
    public bool         IsFirstPick     { get; init; }
    public string       FirstPickLabel  { get; init; } = "";
    public Visibility   FirstPickVisibility =>
        IsFirstPick ? Visibility.Visible : Visibility.Collapsed;
}
