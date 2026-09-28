using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using ToolTip = System.Windows.Controls.ToolTip;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using DockPanel = System.Windows.Controls.DockPanel;
using Dock = System.Windows.Controls.Dock;

namespace Counterplay;

/// <summary>
/// Пароль синхронизации: состояние и кнопка. Один вид на все окна.
///
/// Жил в окне пулов — и это была ошибка. На НОВОМ компьютере человек идёт в
/// настройки, а не в пулы: пулов у него там ещё нет, он за ними и пришёл.
/// Поэтому вид общий, а окна его просто размещают у себя.
/// </summary>
public static class SyncBadge
{
    private static readonly Color Blue = Color.FromRgb(0x5A, 0x8A, 0xC8);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0xA0, 0xB2);

    /// <summary>
    /// Ряд «кнопка + знак вопроса». <paramref name="ask"/> открывает окно ввода
    /// (у каждого окна свои кнопки и стиль), <paramref name="changed"/> зовётся
    /// после смены пароля — перерисовать ряд.
    /// </summary>
    public static FrameworkElement Row(Func<bool> ask, Action? changed = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        Fill(row, ask, changed);
        return row;
    }

    private static void Fill(StackPanel row, Func<bool> ask, Action? changed)
    {
        row.Children.Clear();

        if (SyncPassword.IsSet)
        {
            row.Children.Add(new TextBlock
            {
                Text = "✓ " + Loc.T("sync.set"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A)),
                FontSize = 11, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            row.Children.Add(MakeButton(Loc.T("sync.change"), row, ask, changed));

            // Сам перенос. Только при заданном пароле: без него жать не на что.
            var now = MakeButton(Loc.T("sync.now"), row, () => false, null);
            now.Margin = new Thickness(6, 0, 0, 0);
            now.Click += async (_, _) => await RunSync(now, changed);
            row.Children.Add(now);
        }
        else row.Children.Add(MakeButton(Loc.T("sync.add"), row, ask, changed));

        // «?» нужен в обоих состояниях: и тому, кто ещё не завёл пароль, и тому,
        // кто забыл, зачем он.
        row.Children.Add(Help());
    }

    private static Button MakeButton(
        string text, StackPanel row, Func<bool> ask, Action? changed)
    {
        var b = new Button
        {
            Content = text, FontSize = 11, FontWeight = FontWeights.Bold,
            Padding = new Thickness(9, 2, 9, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            Foreground = System.Windows.Media.Brushes.White,
        };
        b.Click += (_, _) =>
        {
            if (!ask()) return;
            Fill(row, ask, changed);   // состояние поменялось — перерисовать
            changed?.Invoke();
        };
        return b;
    }

    /// <summary>
    /// Один проход синхронизации по нажатию.
    ///
    /// Кнопка на время работы гаснет: перенос идёт по сети, и второе нажатие
    /// посреди первого устроило бы гонку за одну и ту же строку.
    /// </summary>
    private static async Task RunSync(Button btn, Action? changed)
    {
        var was = btn.Content;
        btn.IsEnabled = false;
        btn.Content = Loc.T("sync.doing");
        try
        {
            var res = await SyncClient.SyncAsync(PoolStore.AccountPuuid);
            btn.Content = res.Ok ? "✓ " + Loc.T("sync.now") : was;

            if (!res.Ok)
                Confirm.Tell(Window.GetWindow(btn)!, Loc.T("sync.title"), res.Message);
            else if (res.Pulled > 0)
            {
                // Забрали чужое — перечитать всё, что уже в памяти, иначе на
                // экране останется прежнее, а на диске будет новое.
                PoolStore.Reload();
                changed?.Invoke();
                Confirm.Tell(Window.GetWindow(btn)!, Loc.T("sync.title"),
                             Loc.T("sync.pulled", res.Pulled));
            }
        }
        finally
        {
            btn.IsEnabled = true;
            // Галочку держим до следующего открытия окна — видно, что сходило.
            if ((string?)btn.Content == Loc.T("sync.doing")) btn.Content = was;
        }
    }

    /// «?» с пояснением: что это за пароль и зачем.
    private static FrameworkElement Help()
    {
        var body = new StackPanel { MaxWidth = 340 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.hint"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            FontSize = 12, LineHeight = 18
        });

        // Перечень — списком, а не абзацем: сюда заглядывают именно чтобы
        // увидеть, что попадёт на второй компьютер, и сплошной текст для этого
        // читать неудобно.
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.whatTitle"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x8A, 0xC8)),
            FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 5)
        });
        foreach (var item in Loc.TArray("sync.whatList"))
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var dot = new TextBlock
            {
                Text = "•", Foreground = new SolidColorBrush(Muted),
                FontSize = 11, Margin = new Thickness(2, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(dot, Dock.Left);
            line.Children.Add(dot);
            line.Children.Add(new TextBlock
            {
                Text = item, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
                FontSize = 11, LineHeight = 16
            });
            body.Children.Add(line);
        }

        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.warn"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Muted),
            FontSize = 11, LineHeight = 16, Margin = new Thickness(0, 10, 0, 0)
        });

        var tip = new ToolTip
        {
            Content = body, Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x48, 0x5A)),
            BorderThickness = new Thickness(1)
        };

        var q = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            ToolTip = tip,
            Child = new TextBlock
            {
                Text = "?", Foreground = new SolidColorBrush(Blue),
                FontWeight = FontWeights.Bold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTipService.SetShowDuration(q, 60000);
        ToolTipService.SetInitialShowDelay(q, 150);
        return q;
    }
}
