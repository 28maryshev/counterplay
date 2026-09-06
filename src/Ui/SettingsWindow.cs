using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace Counterplay;

/// <summary>
/// Настройки интерфейса: что показывать на каждом экране программы.
/// Разделы совпадают с экранами (ожидание / драфт / баны), чтобы искать
/// переключатель там же, где видишь мешающий блок.
///
/// Изменения применяются сразу — окно можно не закрывать: включил, посмотрел
/// на оверлей, выключил обратно.
/// </summary>
sealed class SettingsWindow : Window
{
    private static readonly Color Bg   = Color.FromRgb(0x0E, 0x14, 0x1D);
    private static readonly Color Gold = Color.FromRgb(0xC8, 0x9B, 0x3C);
    private static readonly Color Text = Color.FromRgb(0xC9, 0xD2, 0xDC);
    private static readonly Color Mute = Color.FromRgb(0x8A, 0xA0, 0xB2);

    public SettingsWindow()
    {
        Title = Loc.T("settings.title");
        Width = 560; Height = 640;
        Background = new SolidColorBrush(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PoolUi.Apply(this);

        var s = AppSettings.Current;
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        body.Children.Add(Section(Loc.T("settings.ready")));
        body.Children.Add(Row(Loc.T("settings.readyRank"),    Loc.T("settings.readyRankHint"),
            s.ReadyRank,    v => { s.ReadyRank = v;    AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.readyLast5"),   Loc.T("settings.readyLast5Hint"),
            s.ReadyLast5,   v => { s.ReadyLast5 = v;   AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.readyWinrate"), Loc.T("settings.readyWinrateHint"),
            s.ReadyWinrate, v => { s.ReadyWinrate = v; AppSettings.Save(); }));

        // Переключатель данных графика — единственная настройка, где выбор не
        // «да/нет», а «что показывать».
        body.Children.Add(Choice(Loc.T("settings.chart"), Loc.T("settings.chartHint"),
            [("winrate", Loc.T("settings.chartWinrate")),
             ("rating",  Loc.T("settings.chartRating")),
             ("off",     Loc.T("settings.chartOff"))],
            s.ChartMode, v => { s.ChartMode = v; AppSettings.Save(); }));

        body.Children.Add(Row(Loc.T("settings.readyPool"),   Loc.T("settings.readyPoolHint"),
            s.ReadyPool,   v => { s.ReadyPool = v;   AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.readyChamps"), Loc.T("settings.readyChampsHint"),
            s.ReadyChamps, v => { s.ReadyChamps = v; AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.readyPhase"),  Loc.T("settings.readyPhaseHint"),
            s.ReadyPhase,  v => { s.ReadyPhase = v;  AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.readyBeta"),   Loc.T("settings.readyBetaHint"),
            s.ReadyBeta,   v => { s.ReadyBeta = v;   AppSettings.Save(); }));

        body.Children.Add(Section(Loc.T("settings.draft")));
        body.Children.Add(Row(Loc.T("settings.draftRolePool"), Loc.T("settings.draftRolePoolHint"),
            s.DraftRolePool, v => { s.DraftRolePool = v; AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftReasons"),  Loc.T("settings.draftReasonsHint"),
            s.DraftReasons,  v => { s.DraftReasons = v;  AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftMetrics"),  Loc.T("settings.draftMetricsHint"),
            s.DraftMetrics,  v => { s.DraftMetrics = v;  AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftItems"),    Loc.T("settings.draftItemsHint"),
            s.DraftItems,    v => { s.DraftItems = v;    AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftHover"),    Loc.T("settings.draftHoverHint"),
            s.DraftHover,    v => { s.DraftHover = v;    AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftCombos"),   Loc.T("settings.draftCombosHint"),
            s.DraftCombos,   v => { s.DraftCombos = v;   AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftArch"),     Loc.T("settings.draftArchHint"),
            s.DraftArch,     v => { s.DraftArch = v;     AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftSideIcons"), Loc.T("settings.draftSideIconsHint"),
            s.DraftSideIcons, v => { s.DraftSideIcons = v; AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftDamage"),   Loc.T("settings.draftDamageHint"),
            s.DraftDamage,   v => { s.DraftDamage = v;   AppSettings.Save(); }));
        body.Children.Add(Row(Loc.T("settings.draftRunes"),    Loc.T("settings.draftRunesHint"),
            s.DraftRunes,    v => { s.DraftRunes = v;    AppSettings.Save(); }));

        body.Children.Add(Section(Loc.T("settings.bans")));
        body.Children.Add(Row(Loc.T("settings.bansTier"), Loc.T("settings.bansTierHint"),
            s.BansTierList, v => { s.BansTierList = v; AppSettings.Save(); }));

        var reset = new Button
        {
            Content = Loc.T("settings.reset"),
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 6, 14, 6),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0xC8, 0x9B, 0x3C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x6D, 0x2F)),
            Foreground = new SolidColorBrush(Gold),
            Cursor = Cursors.Hand
        };
        reset.Click += (_, _) => { AppSettings.Reset(); Rebuild(); };
        body.Children.Add(reset);

        Content = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    // Настройки сбросили — проще пересобрать окно, чем разыскивать все тумблеры.
    private void Rebuild()
    {
        var pos = new { Left, Top };
        var fresh = new SettingsWindow { Left = pos.Left, Top = pos.Top, Owner = Owner };
        fresh.Show();
        Close();
    }

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
        FontSize = 15, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Gold),
        Margin = new Thickness(0, 18, 0, 6)
    };

    /// Строка настройки: название и пояснение слева, тумблер справа.
    private static UIElement Row(string title, string hint, bool value, Action<bool> onSet)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13, Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
        });
        if (hint.Length > 0)
            texts.Children.Add(new TextBlock
            {
                Text = hint,
                FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
                FontSize = 11, Foreground = new SolidColorBrush(Mute),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0)
            });
        grid.Children.Add(texts);

        var toggle = Toggle(value, onSet);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
        return grid;
    }

    /// Тумблер: скруглённая дорожка и кружок, который переезжает вправо.
    private static UIElement Toggle(bool value, Action<bool> onSet)
    {
        var knob = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0)
        };
        var track = new Border
        {
            Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand, Child = knob
        };

        void Paint(bool on)
        {
            track.Background = new SolidColorBrush(on
                ? Color.FromRgb(0x36, 0xD6, 0xE7)
                : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = on ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }
        Paint(value);

        var state = value;
        track.MouseLeftButtonDown += (_, _) => { state = !state; Paint(state); onSet(state); };
        return track;
    }

    /// Выбор из нескольких значений — сегментированная кнопка.
    private static UIElement Choice(
        string title, string hint, (string Key, string Label)[] options,
        string value, Action<string> onSet)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        wrap.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
            FontSize = 13, Foreground = new SolidColorBrush(Text)
        });
        if (hint.Length > 0)
            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
                FontSize = 11, Foreground = new SolidColorBrush(Mute),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var buttons = new List<Border>();
        var state = value;

        void Paint()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var on = options[i].Key == state;
                buttons[i].Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x33, 0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                buttons[i].BorderBrush = new SolidColorBrush(on
                    ? Color.FromRgb(0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2));
                ((TextBlock)buttons[i].Child).Foreground =
                    new SolidColorBrush(on ? Colors.White : Mute);
            }
        }

        foreach (var (key, label) in options)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
                    FontSize = 12, FontWeight = FontWeights.Bold
                }
            };
            b.MouseLeftButtonDown += (_, _) => { state = key; Paint(); onSet(key); };
            buttons.Add(b);
            row.Children.Add(b);
        }
        Paint();
        wrap.Children.Add(row);
        return wrap;
    }
}
