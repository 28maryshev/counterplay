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
/// Выбор чемпиона: поиск сверху + список, разложенный по ролям.
///
/// Два режима. Одиночный (дуо-партнёр): клик — выбрал и закрыл. Множественный
/// (пул роли): клик отмечает, окно не закрывается, внизу кнопка «Добавить (N)».
/// Пул набирают пачкой — закрывать окно после каждого чемпиона значило
/// открывать его заново пятнадцать раз подряд.
/// </summary>
sealed class ChampionPickerWindow : Window
{
    public int Result;
    /// Отмеченные в множественном режиме, в порядке нажатия.
    public List<int> Results { get; } = new();

    private readonly Dictionary<string, int> _idByName;
    private readonly HashSet<int> _exclude;
    private readonly List<string> _names;
    private readonly RecommendationEngine? _engine;   // для группировки по частой роли
    private readonly StackPanel _root = new();        // секции ролей сверху вниз
    private readonly TextBox _search = new() { FontSize = 13 };
    private readonly bool _multi;
    private readonly List<int> _picked = new();       // отмеченные, порядок важен
    private readonly Button? _addBtn;

    public ChampionPickerWindow(List<string> names, Dictionary<string, int> idByName, IEnumerable<int> exclude,
                                RecommendationEngine? engine = null, bool multi = false)
    {
        _names = names; _idByName = idByName; _exclude = [.. exclude]; _engine = engine; _multi = multi;
        Title  = Loc.T("pool.pickChamp");
        Width  = 460; Height = multi ? 560 : 520;   // в множественном внизу кнопки
        MinWidth = 360; MinHeight = 320;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PoolUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(14) };
        _search.TextChanged += (_, _) => Render();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);

        // Набирают пул — подсказка, кого в него класть. Пул чемпионы получают
        // первыми в подборе, и «всех, на ком когда-то сыграл» превращает его в
        // список случайных пиков. В выборе напарника по одному — не нужна.
        if (multi)
        {
            var hint = PickHint();
            DockPanel.SetDock(hint, Dock.Top);
            root.Children.Add(hint);
        }

        if (multi)
        {
            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };
            // Кнопки — общим тёмным стилем окна (PoolUi), «Добавить» синим акцентом,
            // как «Сохранить» в редакторе: иначе внизу висели две светлые системные.
            var cancel = PoolUi.Btn(Loc.T("pool.cancel"));
            cancel.Margin = new Thickness(0, 0, 8, 0);
            cancel.MinWidth = 90;
            cancel.Click += (_, _) => Close();
            _addBtn = PoolUi.Btn("");
            _addBtn.MinWidth = 120;
            _addBtn.IsDefault = true;
            _addBtn.FontWeight = FontWeights.Bold;
            _addBtn.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
            _addBtn.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
            _addBtn.Foreground = Brushes.White;
            _addBtn.Click += (_, _) =>
            {
                Results.AddRange(_picked);   // пусто = отменили или закрыли крестиком
                Close();
            };
            bar.Children.Add(cancel);
            bar.Children.Add(_addBtn);
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            UpdateAddButton();
        }

        root.Children.Add(new ScrollViewer
        {
            Content = _root, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        var chrome = PoolUi.Chrome(this, Title, root, resizable: true);
        Content = chrome;
        WindowScale.Apply(this, chrome, 460, multi ? 560 : 520, 360, 320);
        _search.Focus();
        Render();
    }

    /// Синий кружок с «!» и строка серым: «выбирай тех, на ком играешь уверенно».
    private static UIElement PickHint()
    {
        var row = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        var mark = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(PoolSettingsWindow.Blue),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 7, 0),
            Child = new TextBlock
            {
                Text = "!", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.pickHint"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
        });
        return row;
    }

    // Пока никого не отметили — добавлять нечего, и кнопка это показывает.
    private void UpdateAddButton()
    {
        if (_addBtn is null) return;
        _addBtn.Content = Loc.T("pool.addSelected", _picked.Count);
        _addBtn.IsEnabled = _picked.Count > 0;
    }

    private void Render()
    {
        var q = _search.Text.Trim();
        _root.Children.Clear();

        // Отфильтрованные по поиску имена.
        var visible = _names.Where(n =>
            !_exclude.Contains(_idByName[n]) &&
            (q.Length == 0 || n.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();

        // Без движка — плоский список (как раньше).
        if (_engine == null)
        {
            var flat = new WrapPanel();
            foreach (var n in visible) flat.Children.Add(ChampTile(n));
            _root.Children.Add(flat);
            return;
        }

        // Раскладываем по самой частой роли чемпиона; без данных — в «Прочие».
        var byRole = PoolSettingsWindow.Roles.ToDictionary(r => r, _ => new List<string>());
        var other  = new List<string>();
        foreach (var n in visible)
        {
            var role = _engine.PrimaryRole(_idByName[n]);
            if (byRole.TryGetValue(role, out var lst)) lst.Add(n);
            else other.Add(n);
        }

        foreach (var role in PoolSettingsWindow.Roles)
        {
            if (byRole[role].Count == 0) continue;
            _root.Children.Add(RoleHeader(role));
            var wrap = new WrapPanel();
            foreach (var n in byRole[role]) wrap.Children.Add(ChampTile(n));
            _root.Children.Add(wrap);
        }
        if (other.Count > 0)
        {
            _root.Children.Add(RoleHeader(null));
            var wrap = new WrapPanel();
            foreach (var n in other) wrap.Children.Add(ChampTile(n));
            _root.Children.Add(wrap);
        }
    }

    // Заголовок секции роли: иконка роли + название (null = «Прочие»).
    private static FrameworkElement RoleHeader(string? role)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 4) };
        if (role != null && RoleIcons.Get(PoolSettingsWindow.DbToLcu[role]) is { } icon)
            row.Children.Add(new Image { Source = icon, Width = 18, Height = 18, Opacity = 0.9,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        row.Children.Add(new TextBlock
        {
            Text = role != null ? PoolSettingsWindow.RoleNames[Array.IndexOf(PoolSettingsWindow.Roles, role)] : "—",
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    // Плитка чемпиона: иконка + имя. В одиночном режиме клик выбирает и
    // закрывает окно, в множественном — отмечает и оставляет открытым.
    private FrameworkElement ChampTile(string name)
    {
        var id = _idByName[name];
        bool marked = _multi && _picked.Contains(id);

        var b = new Border
        {
            Width = 74, Height = 92, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6),
            // Отмеченный подсвечен золотом — тем же, которым в этих окнах
            // помечается всё активное.
            Background = new SolidColorBrush(marked
                ? Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C)
                : Color.FromRgb(0x16, 0x20, 0x2C)),
            BorderBrush = new SolidColorBrush(marked
                ? Color.FromRgb(0xC8, 0x9B, 0x3C)
                : Color.FromRgb(0x2A, 0x3A, 0x4A)),
            BorderThickness = new Thickness(marked ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand
        };

        var sp = new StackPanel { Margin = new Thickness(4) };
        if (IconCache.Get(id) is { } src)
            sp.Children.Add(new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill } });
        sp.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 10,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

        if (!_multi)
        {
            b.Child = sp;
            b.MouseLeftButtonUp += (_, _) => { Result = id; Close(); };
            return b;
        }

        // Галочка в углу: одной подсветки мало — по ней не поймёшь, отмечено
        // это или просто наведена мышь.
        var check = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C)),
            Visibility = marked ? Visibility.Visible : Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = "✓", FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(PoolSettingsWindow.Bg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var cell = new Grid();
        cell.Children.Add(sp);
        cell.Children.Add(check);
        b.Child = cell;

        // Перекрашиваем саму плитку, а не перерисовываем список: перестройка
        // сбрасывала бы прокрутку, и отметив кого-нибудь внизу, человек каждый
        // раз улетал бы к началу.
        b.MouseLeftButtonUp += (_, _) =>
        {
            bool now = !_picked.Remove(id);
            if (now) _picked.Add(id);

            b.Background = new SolidColorBrush(now
                ? Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C)
                : Color.FromRgb(0x16, 0x20, 0x2C));
            b.BorderBrush = new SolidColorBrush(now
                ? Color.FromRgb(0xC8, 0x9B, 0x3C)
                : Color.FromRgb(0x2A, 0x3A, 0x4A));
            b.BorderThickness = new Thickness(now ? 2 : 1);
            check.Visibility = now ? Visibility.Visible : Visibility.Collapsed;
            UpdateAddButton();
        };
        return b;
    }
}
