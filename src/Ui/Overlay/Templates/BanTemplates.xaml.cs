using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблоны банов: карточка совета и слот бана. События — окну.</summary>
public partial class BanTemplates : ResourceDictionary
{
    public BanTemplates() => InitializeComponent();

    private void BanCard_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.BanCard_Click(sender, e);
}
