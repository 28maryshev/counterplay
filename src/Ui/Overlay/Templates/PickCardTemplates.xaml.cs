using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон карточки кандидата в списке пиков. События — окну.</summary>
public partial class PickCardTemplates : ResourceDictionary
{
    public PickCardTemplates() => InitializeComponent();

    private void RecCard_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.RecCard_Click(sender, e);

    private void RecCard_Enter(object sender, System.Windows.Input.MouseEventArgs e) =>
        OverlayTemplates.Owner(sender)?.RecCard_Enter(sender, e);

    private void RecCard_Leave(object sender, System.Windows.Input.MouseEventArgs e) =>
        OverlayTemplates.Owner(sender)?.RecCard_Leave(sender, e);

    private void PickDislike_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // до карточки клик не доходит — чемпион не наводится
        OverlayTemplates.Owner(sender)?.Dislike_Click(sender, Dislikes.Kind.Pick);
    }
}
