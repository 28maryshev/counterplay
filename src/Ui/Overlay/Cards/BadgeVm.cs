using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace Counterplay;

/// <summary>
/// Тег карточки пика: плашка со значком и текстом с цифрой («Линия +1,3%»).
/// Цвет — по знаку: зелёный за пик, красный против. Объяснение — в подсказке.
/// </summary>
public sealed record TagVm(Geometry? Glyph, string Text, string Tip, Brush Fg, Brush Bg, Brush Border);

/// <summary>
/// Иконка карточки пика: линейный значок цветом знака, имя и фраза — в
/// подсказке. Faded — признак есть почти у всех кандидатов: иконка бледнеет,
/// а не пропадает, и Note объясняет почему.
/// </summary>
public sealed record IconVm(Geometry? Glyph, Brush Fg, string Title, string Tip, bool Faded, string Note)
{
    public double     Opacity        => Faded ? 0.38 : 1.0;
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
}
