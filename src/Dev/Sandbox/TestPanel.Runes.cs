using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Панель песочницы — подбор рун (Counterplay AI): что подбор увидел и почему
// выбрал такую страницу. Владелец (19.11): «добавь всё это в песочницу, я буду
// тестить». На карточке в окне видно только итог и причины; здесь — весь ход:
// кого подбор считает оппонентом по линии, какой кейстоун и с какой поправкой,
// что нашёл в составе, какие деревья сравнил и что отбросил как «не берут».
sealed partial class TestPanel
{
    private readonly TextBlock _runeAiText = new()
    {
        Text = "выбери себя и нажми «Выбрать» в окне — появятся руны; подбор — когда выбраны все десять",
        Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 15,
    };

    /// Тело карточки «Подбор рун» внизу панели: текст, который присылает окно.
    /// Длинный разбор прокручивается, а не растягивает панель за экран.
    private UIElement RuneAiBody()
    {
        var box = new ScrollViewer
        {
            Content = _runeAiText,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Окно зовёт из своего потока отрисовки — он же поток панели.
        _overlay.RuneAiTrace = text => Dispatcher.InvokeAsync(() => _runeAiText.Text = text);
        Closed += (_, _) => _overlay.RuneAiTrace = null;
        return box;
    }
}
