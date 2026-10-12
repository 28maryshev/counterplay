using System.Windows;

namespace Counterplay;

/// <summary>
/// Общее для словарей шаблонов окна драфта (Templates/*.xaml).
///
/// Шаблон карточки или слота лежит в отдельном словаре, и обработчик события,
/// записанный в его разметке, ищется в классе словаря, а не окна. Поэтому у
/// каждого такого словаря есть код, который только передаёт событие окну, в
/// котором показан элемент, — сама логика остаётся в OverlayWindow.
/// </summary>
internal static class OverlayTemplates
{
    /// Окно драфта, в котором показан элемент шаблона (null — элемент вне окна).
    public static OverlayWindow? Owner(object sender) =>
        sender is DependencyObject d ? Window.GetWindow(d) as OverlayWindow : null;
}
