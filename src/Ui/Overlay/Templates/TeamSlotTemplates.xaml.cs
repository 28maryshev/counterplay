using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон слота состава (союзники и враги). События — окну.</summary>
public partial class TeamSlotTemplates : ResourceDictionary
{
    public TeamSlotTemplates() => InitializeComponent();

    private void EnemyRole_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.EnemyRole_Click(sender, e);
}
