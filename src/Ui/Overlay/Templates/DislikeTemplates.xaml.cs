using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон полосы «Скрыты» (не советовать). События — окну.</summary>
public partial class DislikeTemplates : ResourceDictionary
{
    public DislikeTemplates() => InitializeComponent();

    private void DislikeRestore_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OverlayTemplates.Owner(sender)?.DislikeRestore_Click(sender);
    }
}
