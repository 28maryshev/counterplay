using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Выбор СВОЕЙ половины пары. Друг присылает свой личный пул — он становится
/// половиной друга, а моя берётся из уже собранного мной пула: так дуо-пул
/// складывается из двух личных, и никому не приходится набирать его заново.
/// </summary>
static class MyPoolPicker
{
    /// <summary>
    /// false — передумали (ничего не создаём). true и <paramref name="picked"/>
    /// null — свою половину заполнят позже, в редакторе.
    /// </summary>
    public static bool Ask(Window owner, IReadOnlyList<ChampPool> mine, string friendName, out ChampPool? picked)
    {
        picked = null;
        if (mine.Count == 0) return true;   // выбирать не из чего — спрашивать не о чем

        var dlg = new Window
        {
            Title = Loc.T("pool.pickMineTitle"), Width = 380, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        PoolUi.Apply(dlg);

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.pickMineHint", friendName),
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 0, 0, 12)
        });

        ChampPool? chosen = null;
        var ok = false;
        foreach (var p in mine)
        {
            // Число чемпионов — цифрой: подписи не нужны, а два пула с похожими
            // именами так различимы.
            var count = p.ByRole.Values.SelectMany(l => l).Count(id => id != 0);
            var b = PoolUi.Btn($"{p.Name}  ·  {count}");
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(0, 0, 0, 6);
            var self = p;
            b.Click += (_, _) => { chosen = self; ok = true; dlg.Close(); };
            sp.Children.Add(b);
        }

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        var later = PoolUi.Btn(Loc.T("pool.later"));
        later.Margin = new Thickness(0, 0, 8, 0);
        later.Click += (_, _) => { ok = true; dlg.Close(); };
        var cancel = PoolUi.Btn(Loc.T("pool.cancel"));
        cancel.Click += (_, _) => dlg.Close();
        btns.Children.Add(later); btns.Children.Add(cancel);
        sp.Children.Add(btns);

        dlg.Content = PoolUi.Chrome(dlg, dlg.Title, sp);
        dlg.ShowDialog();
        picked = chosen;
        return ok;
    }
}
