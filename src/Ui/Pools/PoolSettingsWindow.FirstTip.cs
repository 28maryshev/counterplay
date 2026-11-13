using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

// Окно пулов — первое открытие без единого пула: как собрать пул.
public sealed partial class PoolSettingsWindow
{
    private static readonly Color Gold = Color.FromRgb(0xC8, 0x9B, 0x3C);

    /// Предупреждение «как собрать пул» на экране (null — нет). Проверке —
    /// найти и закрыть, как это сделал бы человек кнопкой.
    public FrameworkElement? FirstTip { get; private set; }

    /// <summary>
    /// Пулов нет совсем (ни личного, ни дуо), и окно открыто впервые — поверх
    /// него затемнение и по центру карточка: брать в пул только тех, на ком
    /// играешь чаще всего в последнее время и у кого лучше получается, а не
    /// всех подряд (владелец, 13.11: «всех добавлять не надо, иначе это не имеет
    /// смысла» — пул из всех советует так же, как без пула). Показ помнится
    /// (`AppSettings.PoolFirstTipShown`, ставится сразу при показе: закрыли окно
    /// крестиком — тоже «видел»). Внутри окна, а не отдельным: модальное окно
    /// поверх нельзя проверить невидимым, а оно и не нужно — кроме этого окна
    /// предупреждению закрывать нечего.
    /// </summary>
    private void AddFirstTip(Grid host)
    {
        var a = PoolStore.Current();
        if (a.Pools.Count > 0 || a.DuoPools.Count > 0 || AppSettings.Current.PoolFirstTipShown) return;
        AppSettings.Current.PoolFirstTipShown = true;
        AppSettings.SaveQuiet();

        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.firstTipTitle"), FontSize = 17, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Gold), Margin = new Thickness(0, 0, 0, 12),
        });
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.firstTipText"), FontSize = 13.5, LineHeight = 20,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18),
        });
        var ok = PoolUi.Btn(Loc.T("pool.firstTipOk"));
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.MinWidth = 110;
        ok.FontWeight = FontWeights.Bold;
        ok.Click += (_, _) => CloseFirstTip();
        body.Children.Add(ok);

        var card = new Border
        {
            MaxWidth = 460, Margin = new Thickness(24), Padding = new Thickness(24, 20, 24, 20),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Gold), Background = new SolidColorBrush(Bg),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = body,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black, ShadowDepth = 0, BlurRadius = 24, Opacity = 0.6,
            },
        };
        // Затемнение ловит клики: до «Понятно» окно под ним не трогают.
        var veil = new Grid { Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x06, 0x0A, 0x10)) };
        veil.Children.Add(card);
        host.Children.Add(veil);
        FirstTip = veil;
    }

    /// «Понятно» — убрать предупреждение.
    public void CloseFirstTip()
    {
        if (FirstTip?.Parent is System.Windows.Controls.Panel p) p.Children.Remove(FirstTip);
        FirstTip = null;
    }
}
