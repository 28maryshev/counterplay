using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно «Скрытые чемпионы»: кого игрок убрал кнопкой 👎 («не советовать»).
/// Две половины через черту, как в окне пулов: слева — скрытые в банах, справа
/// — в пиках. Владелец 16.10: «общая кнопка, которая открывает окно,
/// разделённое на две части, как у нас в настройках пула». В строке — роль,
/// портрет, имя, сколько ждать и «Вернуть». Видны все роли; роль текущего
/// драфта — первой.
///
/// Открывает кнопка «👎 Скрытые» в шапке тир-листа (OverlayWindow.Dislike.cs).
/// Списки перерисовываются сами на любое скрытие и возврат (Dislikes.Changed).
/// </summary>
public sealed class DislikesWindow : Window
{
    private readonly string _role;
    private readonly Action<Dislikes.Kind> _restored;
    private readonly StackPanel _bans = new(), _picks = new();

    private static readonly Color BanColor  = Color.FromRgb(0xE0, 0x58, 0x4F);
    private static readonly Color PickColor = Color.FromRgb(0xC8, 0x9B, 0x3C);

    /// <param name="role">роль драфта (роль базы) — её скрытые идут первыми</param>
    /// <param name="restored">чемпиона вернули — окну драфта пересчитать советы</param>
    public DislikesWindow(string role, Action<Dislikes.Kind> restored)
    {
        _role = role;
        _restored = restored;
        Title = Loc.T("dislike.windowTitle");
        Width = 760; Height = 480;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PoolUi.Apply(this);

        var halves = new Grid { Margin = new Thickness(16, 14, 16, 8) };
        halves.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        halves.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        halves.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        halves.Children.Add(Half(Loc.T("dislike.titleBans"), Loc.T("dislike.markBans"), BanColor, _bans, 0));
        var divider = new Border
        {
            Width = 1, Background = new SolidColorBrush(PoolSettingsWindow.Line), Margin = new Thickness(14, 0, 14, 0)
        };
        Grid.SetColumn(divider, 1);
        halves.Children.Add(divider);
        halves.Children.Add(Half(Loc.T("dislike.titlePicks"), Loc.T("dislike.markPicks"), PickColor, _picks, 2));

        // Внизу — как это работает: сколько игр скрыт и как возвращается.
        var howTo = new TextBlock
        {
            Text = Loc.T("dislike.howTo", Dislikes.HideGames, Dislikes.ReturnGames),
            Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0x97, 0xAC)), FontSize = 11,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 12),
        };
        var layout = new DockPanel();
        DockPanel.SetDock(howTo, Dock.Bottom);
        layout.Children.Add(howTo);
        layout.Children.Add(halves);

        var decorated = new Grid();
        decorated.Children.Add(PoolSettingsWindow.DecorBackground());
        decorated.Children.Add(layout);
        var chrome = PoolUi.Chrome(this, Title, decorated, resizable: true);
        Content = chrome;
        WindowScale.Apply(this, chrome, 760, 480, 560, 340);

        Dislikes.Changed += Refresh;
        Closed += (_, _) => Dislikes.Changed -= Refresh;
        Refresh();
    }

    /// Половина: заголовок цвета вида и список, за ним — крупное полупрозрачное
    /// название («водяной знак», как в окне пулов).
    private static FrameworkElement Half(string title, string mark, Color color, StackPanel list, int col)
    {
        var grid = new Grid();
        grid.Children.Add(new TextBlock
        {
            Text = mark, Foreground = new SolidColorBrush(Color.FromArgb(0x1C, 0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 64,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        });
        var head = new TextBlock
        {
            Text = title, Foreground = new SolidColorBrush(color), FontWeight = FontWeights.Bold, FontSize = 13,
            Margin = new Thickness(2, 0, 0, 10),
        };
        var dock = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        grid.Children.Add(dock);
        Grid.SetColumn(grid, col);
        return grid;
    }

    /// Перерисовать обе половины.
    public void Refresh()
    {
        Fill(_bans, Dislikes.Kind.Ban);
        Fill(_picks, Dislikes.Kind.Pick);
    }

    private void Fill(StackPanel panel, Dislikes.Kind kind)
    {
        panel.Children.Clear();
        var rows = Dislikes.ActiveAll(kind)
            .OrderBy(r => r.Role == _role ? 0 : 1)
            .ThenBy(r => Array.IndexOf(PoolSettingsWindow.Roles, r.Role))
            .ThenBy(r => r.State.Played)
            .ToList();
        if (rows.Count == 0)
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("dislike.empty"), Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x85, 0x98)),
                FontSize = 12, Margin = new Thickness(2, 4, 0, 0),
            });
        foreach (var (role, state) in rows)
            panel.Children.Add(Row(kind, role, state));
    }

    /// Строка: роль, портрет в кольце цвета вида, имя, сколько ждать, «Вернуть».
    private FrameworkElement Row(Dislikes.Kind kind, string role, Dislikes.State s)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var roleIcon = new Image
        {
            Source = RoleIcons.Get(PoolSettingsWindow.DbToLcu.GetValueOrDefault(role, role)),
            Width = 18, Height = 18, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Opacity = role == _role ? 1 : 0.6,
        };
        RenderOptions.SetBitmapScalingMode(roleIcon, BitmapScalingMode.HighQuality);
        grid.Children.Add(roleIcon);

        var color = kind == Dislikes.Kind.Ban ? BanColor : PickColor;
        var portrait = new Ellipse
        {
            Width = 36, Height = 36, StrokeThickness = 1.5, Stroke = new SolidColorBrush(color),
            Fill = IconCache.Get(s.Champ) is { } icon ? new ImageBrush(icon) { Stretch = Stretch.UniformToFill } : null,
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(portrait, 1);
        grid.Children.Add(portrait);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = DataDragon.Name(s.Champ), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = s.Hidden ? Loc.T("dislike.statusHidden", s.GamesLeft) : Loc.T("dislike.statusReturning", s.Demote),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA6, 0xBC)), FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var restore = PoolUi.Btn(Loc.T("dislike.restore"));
        restore.Tag = s.Champ;   // проверкам: какой строке принадлежит кнопка
        restore.VerticalAlignment = VerticalAlignment.Center;
        restore.Margin = new Thickness(10, 0, 0, 0);
        restore.Click += (_, _) =>
        {
            Dislikes.Remove(kind, s.Champ, role);   // Changed перерисует списки
            _restored(kind);
        };
        Grid.SetColumn(restore, 3);
        grid.Children.Add(restore);

        return new Border
        {
            Child = grid, Padding = new Thickness(10, 7, 8, 7), Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, color.R, color.G, color.B)),
            ToolTip = role == _role ? null : Loc.T("dislike.otherRole"),
        };
    }
}
