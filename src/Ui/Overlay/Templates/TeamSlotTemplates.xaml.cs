using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон слота состава (союзники и враги) и карточки чемпиона при наведении. События — окну.</summary>
public partial class TeamSlotTemplates : ResourceDictionary
{
    public TeamSlotTemplates() => InitializeComponent();

    private void EnemyRole_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.EnemyRole_Click(sender, e);

    private void TeamSlot_TipOpening(object sender, ToolTipEventArgs e)
    {
        if (OverlayTemplates.Owner(sender) is { } w) w.TeamSlot_TipOpening(sender, e);
        else e.Handled = true;   // вне окна драфта показывать нечего
    }
}
