using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблоны полосы чемпионов роли и тир-листа. События — окну.</summary>
public partial class TierTemplates : ResourceDictionary
{
    public TierTemplates() => InitializeComponent();

    private void TierCell_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.TierCell_Click(sender, e);

    private void PoolCell_Enter(object sender, System.Windows.Input.MouseEventArgs e) =>
        OverlayTemplates.Owner(sender)?.PoolCell_Enter(sender, e);

    private void PoolCell_Leave(object sender, System.Windows.Input.MouseEventArgs e) =>
        OverlayTemplates.Owner(sender)?.PoolCell_Leave(sender, e);
}
