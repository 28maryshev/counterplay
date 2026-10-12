using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон строки сборки предметов. События — окну.</summary>
public partial class BuildRowTemplates : ResourceDictionary
{
    public BuildRowTemplates() => InitializeComponent();

    private void BuildRow_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.BuildRow_Click(sender, e);

    private void ExportBuild_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.ExportBuild_Click(sender, e);
}
