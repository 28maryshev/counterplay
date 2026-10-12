using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон строки фазы объявления. События — окну.</summary>
public partial class PlanTemplates : ResourceDictionary
{
    public PlanTemplates() => InitializeComponent();

    private void PlanCard_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.PlanCard_Click(sender, e);
}
