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

// Окно пулов — мелкие элементы: пароль синхронизации, подписи, значок «?», фон.
public sealed partial class PoolSettingsWindow
{
    private FrameworkElement? _syncBox;

    /// <summary>
    /// Окно ввода пароля. Пустое поле — забыть пароль (синхронизация выключается).
    /// Просим ввести дважды: опечатку в пароле не видно, а исправить её потом
    /// негде — на другом компьютере просто не подойдёт.
    /// </summary>
    /// Окно ввода пароля. Зовётся из обоих окон — настроек и пулов.
    public static bool AskSyncPassword(Window owner)
    {
        var body = new StackPanel { MinWidth = 300 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.hint"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xB6, 0xC4)),
            FontSize = 11, Margin = new Thickness(0, 0, 0, 10)
        });

        var first = new PasswordBox { FontSize = 14, Margin = new Thickness(0, 0, 0, 8) };
        var again = new PasswordBox { FontSize = 14 };
        body.Children.Add(Labeled(Loc.T("sync.password"), first));
        body.Children.Add(Labeled(Loc.T("sync.again"), again));

        var err = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x68, 0x4F)),
            FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };
        body.Children.Add(err);

        return Confirm.Form(owner, Loc.T("sync.title"), body, Loc.T("pool.save"), () =>
        {
            if (first.Password != again.Password)
            {
                err.Text = Loc.T("sync.mismatch");
                err.Visibility = Visibility.Visible;
                return false;   // окно не закрываем
            }
            if (!SyncPassword.Set(first.Password))
            {
                err.Text = Loc.T("sync.failed");
                err.Visibility = Visibility.Visible;
                return false;
            }
            return true;
        });
    }

    /// Подпись над полем — как у имени пула.
    private static FrameworkElement Labeled(string label, FrameworkElement field)
    {
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontSize = 10, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 3)
        });
        sp.Children.Add(field);
        return sp;
    }

    // Иконка «?» внизу: при наведении — пояснение, что такое пулы и зачем.
    private static FrameworkElement HelpBadge()
    {
        var body = new StackPanel { MaxWidth = 340 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.help"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)), FontSize = 12, LineHeight = 18
        });

        // Ниже — как пул завести, по шагам. Одним абзацем это не читалось:
        // человек открывает подсказку именно чтобы понять, куда нажимать.
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.howAdd"), Foreground = new SolidColorBrush(Blue),
            FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 12, 0, 6)
        });
        var steps = Loc.TArray("pool.helpSteps");
        for (var i = 0; i < steps.Length; i++)
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var num = new TextBlock
            {
                Text = (i + 1) + ".", Width = 16, Foreground = new SolidColorBrush(Blue),
                FontSize = 12, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(num, Dock.Left);
            line.Children.Add(num);
            line.Children.Add(new TextBlock
            {
                Text = steps[i], TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6))
            });
            body.Children.Add(line);
        }

        var tip = new System.Windows.Controls.ToolTip
        {
            Content = body, Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x48, 0x5A)), BorderThickness = new Thickness(1)
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 10),
            Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, ToolTip = tip
        };
        System.Windows.Controls.ToolTipService.SetShowDuration(row, 60000);
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(row, 150);

        var q = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            Child = new TextBlock { Text = "?", Foreground = new SolidColorBrush(Blue), FontWeight = FontWeights.Bold,
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        row.Children.Add(q);
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.helpLabel"), Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0)
        });
        return row;
    }

    // Фон в стиле оверлея: диагональная сине-красная подсветка + мягкие свечения.
    internal static UIElement DecorBackground()
    {
        var layer = new Grid { IsHitTestVisible = false, ClipToBounds = true };

        var diag = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
        diag.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0x36, 0xD6, 0xE7), 0));
        diag.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x07, 0x12, 0x23), 0.45));
        diag.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 0xFF, 0x5A, 0x4D), 1));
        layer.Children.Add(new System.Windows.Shapes.Rectangle { Fill = diag });

        var blue = new RadialGradientBrush();
        blue.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0x36, 0xD6, 0xE7), 0));
        blue.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x36, 0xD6, 0xE7), 1));
        layer.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 520, Height = 340, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-150, -130, 0, 0), Fill = blue
        });

        var red = new RadialGradientBrush();
        red.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 0xFF, 0x5A, 0x4D), 0));
        red.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0x5A, 0x4D), 1));
        layer.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 640, Height = 400, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -170, -130), Fill = red
        });

        return layer;
    }
}
