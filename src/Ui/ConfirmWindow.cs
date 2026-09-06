using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Application = System.Windows.Application;

namespace Counterplay;

/// <summary>
/// Маленькое окно «да / нет» в стиле программы. Системный MessageBox выглядит
/// чужеродно на тёмном оверлее и не переводится вместе с интерфейсом.
/// </summary>
sealed class ConfirmWindow : Window
{
    private static readonly Color Bg   = Color.FromRgb(0x0E, 0x14, 0x1D);
    private static readonly Color Text = Color.FromRgb(0xC9, 0xD2, 0xDC);

    public ConfirmWindow(string question, string yes, string no, Window? owner)
    {
        Title = yes;
        Width = 380; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        // Своя рамка, как у настроек и оверлея: системная шапка тут белая и чужая.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;      // тот же случай, что и у настроек: не прятаться под клиент
        Owner = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        body.Children.Add(new TextBlock
        {
            Text = question,
            FontFamily = Font("UiFont"), FontSize = 14,
            Foreground = new SolidColorBrush(Text),
            TextWrapping = TextWrapping.Wrap
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var yesBtn = Btn(yes, "#0E141D", "#E0544A", filled: true);
        var noBtn  = Btn(no,  "#C9D2DC", "#35485A", filled: false);
        yesBtn.Click += (_, _) => { DialogResult = true; Close(); };
        noBtn.Click  += (_, _) => { DialogResult = false; Close(); };
        row.Children.Add(noBtn);
        row.Children.Add(yesBtn);
        body.Children.Add(row);

        body.MouseLeftButtonDown += (_, e) =>
        { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };

        Content = new Border
        {
            Background = new SolidColorBrush(Bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xC8, 0x9B, 0x3C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = body
        };
    }

    /// Шрифты объявлены в ресурсах окна оверлея — ищем там же, где и настройки.
    private static FontFamily Font(string key)
    {
        if (Application.Current is { } app)
        {
            if (app.TryFindResource(key) is FontFamily appFont) return appFont;
            foreach (Window w in app.Windows)
                if (w.TryFindResource(key) is FontFamily winFont) return winFont;
        }
        return new FontFamily("Segoe UI");
    }

    private static Button Btn(string text, string fg, string accent, bool filled)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(accent)!;
        return new Button
        {
            Content = text,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(18, 7, 18, 7),
            Background = filled ? brush : new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderBrush = brush,
            Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(fg)!,
            FontFamily = Font("UiFont"),
            FontWeight = FontWeights.Bold,
            Cursor = Cursors.Hand
        };
    }

    /// Спросить и вернуть ответ. true — пользователь подтвердил.
    public static bool Ask(string question, string yes, string no, Window? owner) =>
        new ConfirmWindow(question, yes, no, owner).ShowDialog() == true;
}
