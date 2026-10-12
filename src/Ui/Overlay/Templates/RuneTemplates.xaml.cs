using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон кнопки варианта рун. События — окну.</summary>
public partial class RuneTemplates : ResourceDictionary
{
    public RuneTemplates() => InitializeComponent();

    private void RuneOption_Click(object sender, MouseButtonEventArgs e) =>
        OverlayTemplates.Owner(sender)?.RuneOption_Click(sender, e);
}
