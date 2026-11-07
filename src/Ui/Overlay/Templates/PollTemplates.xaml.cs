using System.Windows;

namespace Counterplay;

/// <summary>Шаблон варианта в опросе на плашке сообщений. События — окну.</summary>
public partial class PollTemplates : ResourceDictionary
{
    public PollTemplates() => InitializeComponent();

    private void PollOption_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        OverlayTemplates.Owner(sender)?.PollOption_Click(sender);
    }
}
