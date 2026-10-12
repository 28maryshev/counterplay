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

/// <summary>Диалог подтверждения (Да/Нет) с предупреждением.</summary>
static class Confirm
{
    public static bool Ask(Window owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        PoolUi.Apply(dlg);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = "⚠ " + message, Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 0, 0, 14)
        });
        bool ok = false;
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = PoolUi.Btn(Loc.T("pool.no"));
        no.Margin = new Thickness(0, 0, 8, 0);
        no.Click += (_, _) => dlg.Close();
        var yes = PoolUi.Btn(Loc.T("pool.yes"));
        yes.FontWeight = FontWeights.Bold;
        yes.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
        yes.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
        yes.Foreground = Brushes.White;
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        btns.Children.Add(no); btns.Children.Add(yes);
        sp.Children.Add(btns);
        dlg.Content = PoolUi.Chrome(dlg, title, sp);
        dlg.ShowDialog();
        return ok;
    }

    /// <summary>
    /// Окно с произвольным содержимым и кнопкой подтверждения.
    ///
    /// <paramref name="onOk"/> возвращает false — окно остаётся открытым: так
    /// сообщение об ошибке видно рядом с полем, а введённое не пропадает.
    /// Возвращает true, если подтвердили и проверка прошла.
    /// </summary>
    public static bool Form(Window owner, string title, FrameworkElement body,
                            string okText, Func<bool> onOk)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        PoolUi.Apply(dlg);

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(body);

        bool ok = false;
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
        };
        var no = PoolUi.Btn(Loc.T("pool.cancel"));
        no.Margin = new Thickness(0, 0, 8, 0);
        no.Click += (_, _) => dlg.Close();
        var yes = PoolUi.Btn(okText);
        yes.FontWeight = FontWeights.Bold;
        yes.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
        yes.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
        yes.Foreground = Brushes.White;
        yes.Click += (_, _) => { if (onOk()) { ok = true; dlg.Close(); } };
        btns.Children.Add(no); btns.Children.Add(yes);
        sp.Children.Add(btns);

        dlg.Content = PoolUi.Chrome(dlg, title, sp);
        dlg.ShowDialog();
        return ok;
    }

    /// <summary>
    /// Выбор из двух действий. Возвращает 0 или 1; null — передумали (кнопка
    /// «Отмена» или крестик).
    /// </summary>
    public static int? Pick(Window owner, string title, string message, string first, string second)
    {
        var dlg = new Window
        {
            Title = title, Width = 400, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        PoolUi.Apply(dlg);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 0, 0, 12)
        });

        int? picked = null;
        foreach (var (text, index) in new[] { (first, 0), (second, 1) })
        {
            var b = PoolUi.Btn(text);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.Margin = new Thickness(0, 0, 0, 6);
            var self = index;
            b.Click += (_, _) => { picked = self; dlg.Close(); };
            sp.Children.Add(b);
        }

        var cancel = PoolUi.Btn(Loc.T("pool.cancel"));
        cancel.HorizontalAlignment = HorizontalAlignment.Right;
        cancel.Margin = new Thickness(0, 6, 0, 0);
        cancel.Click += (_, _) => dlg.Close();
        sp.Children.Add(cancel);

        dlg.Content = PoolUi.Chrome(dlg, title, sp);
        dlg.ShowDialog();
        return picked;
    }

    /// <summary>Сообщение с одной кнопкой: «получилось» / «файл не подошёл».</summary>
    public static void Tell(Window owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        PoolUi.Apply(dlg);
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = message, Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 0, 0, 14)
        });
        var ok = PoolUi.Btn(Loc.T("pool.close"));
        ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => dlg.Close();
        sp.Children.Add(ok);
        dlg.Content = PoolUi.Chrome(dlg, title, sp);
        dlg.ShowDialog();
    }
}
