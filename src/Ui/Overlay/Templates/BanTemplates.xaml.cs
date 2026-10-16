using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблоны банов: карточка совета и слот бана. События — окну.</summary>
public partial class BanTemplates : ResourceDictionary
{
    public BanTemplates() => InitializeComponent();

    private void BanCard_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.BanCard_Click(sender, e);

    private void BanDislike_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // до карточки клик не доходит — бан не наводится
        OverlayTemplates.Owner(sender)?.Dislike_Click(sender, Dislikes.Kind.Ban);
    }
}
