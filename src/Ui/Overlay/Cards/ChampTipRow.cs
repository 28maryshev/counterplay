using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Строка контрпика или напарника в карточке чемпиона: портрет, имя, винрейт
/// пары. У напарника ещё значок его роли — список сведён из четырёх ролей.
/// </summary>
public sealed class ChampTipRow
{
    public ImageSource? Icon     { get; init; }
    public string       Name     { get; init; } = "";
    public ImageSource? RoleIcon { get; init; }
    public Visibility   RoleVis  => RoleIcon != null ? Visibility.Visible : Visibility.Collapsed;
    public string       WrText   { get; init; } = "";
    public string       WrBrush  { get; init; } = "#8AA0B2";
}
