using System.Windows;
using System.Windows.Input;

namespace Counterplay;

/// <summary>Шаблон кнопки фразы для чата драфта. События — окну.</summary>
public partial class PhraseTemplates : ResourceDictionary
{
    public PhraseTemplates() => InitializeComponent();

    private void Phrase_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OverlayTemplates.Owner(sender)?.Phrase_Click(sender);
    }
}
