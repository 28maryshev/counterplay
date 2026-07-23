using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно настройки пулов чемпионов. Две области через тонкую полупрозрачную черту:
/// слева (шире) — обычные пулы, справа — дуо-пулы. В каждой — квадратная «+» и
/// плитки существующих пулов. «+» / клик по плитке открывает редактор пула.
/// </summary>
sealed class PoolSettingsWindow : Window
{
    internal static readonly string[] Roles     = ["top", "jungle", "mid", "adc", "support"];
    internal static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];
    // db-роль → LCU-позиция (для иконок ролей, как в тир-листе).
    internal static readonly Dictionary<string, string> DbToLcu = new()
        { ["top"] = "top", ["jungle"] = "jungle", ["mid"] = "middle", ["adc"] = "bottom", ["support"] = "utility" };

    internal static readonly Color Bg     = Color.FromRgb(0x0E, 0x14, 0x1D);
    internal static readonly Color Blue   = Color.FromRgb(0x5A, 0x8A, 0xC8);
    internal static readonly Color Line   = Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2);

    private readonly Action _onChange;
    private readonly WrapPanel _poolArea = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _duoArea  = new() { Margin = new Thickness(0, 8, 0, 0) };

    public PoolSettingsWindow(Action onChange)
    {
        _onChange = onChange;
        Title  = Loc.T("pool.settings");
        Width  = 820; Height = 560;
        Background = new SolidColorBrush(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.7, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(Area(Loc.T("pool.pool"), _poolArea, duo: false, 0));

        // Тонкая полупрозрачная разделительная черта.
        var divider = new Border { Width = 1, Background = new SolidColorBrush(Line), Margin = new Thickness(14, 0, 14, 0) };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        grid.Children.Add(Area(Loc.T("pool.duo"), _duoArea, duo: true, 2));

        Content = grid;
        Refresh();
    }

    private FrameworkElement Area(string title, WrapPanel area, bool duo, int col)
    {
        var panel = new DockPanel();
        var head = new TextBlock
        {
            Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(2, 0, 0, 0)
        };
        DockPanel.SetDock(head, Dock.Top);
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer
        {
            Content = area, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Grid.SetColumn(panel, col);
        return panel;
    }

    private void Refresh()
    {
        var a = PoolStore.Current();
        _poolArea.Children.Clear();
        foreach (var p in a.Pools) _poolArea.Children.Add(Tile(p.Name, () => EditPool(p), () => DeletePool(p)));
        _poolArea.Children.Add(PlusTile(() => EditPool(null)));

        _duoArea.Children.Clear();
        foreach (var d in a.DuoPools) _duoArea.Children.Add(Tile(d.FriendName, () => EditDuo(d), () => DeleteDuo(d)));
        _duoArea.Children.Add(PlusTile(() => EditDuo(null)));
    }

    // Плитка существующего пула: имя + клик (редактировать) + × (удалить).
    private FrameworkElement Tile(string name, Action open, Action del)
    {
        var b = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x2C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x42, 0x54)), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var g = new Grid();
        g.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(name) ? "—" : name, Foreground = Brushes.White,
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6)
        });
        var x = new Button
        {
            Content = "×", Width = 20, Height = 20, FontSize = 13, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60)), Cursor = System.Windows.Input.Cursors.Hand
        };
        x.Click += (_, e) => { e.Handled = true; del(); };
        g.Children.Add(x);
        b.Child = g;
        b.MouseLeftButtonUp += (_, _) => open();
        return b;
    }

    // Квадратная кнопка «+» создания нового пула.
    private FrameworkElement PlusTile(Action open)
    {
        var b = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Child = new TextBlock
        {
            Text = "+", FontSize = 40, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Blue),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        b.MouseLeftButtonUp += (_, _) => open();
        return b;
    }

    private void EditPool(ChampPool? existing)
    {
        var win = new PoolEditorWindow(existing, duo: false) { Owner = this };
        if (win.ShowDialog() == true) _onChange();
        Refresh();
    }

    private void EditDuo(DuoPool? existing)
    {
        var win = new PoolEditorWindow(existing, duo: true) { Owner = this };
        if (win.ShowDialog() == true) _onChange();
        Refresh();
    }

    private void DeletePool(ChampPool p)
    {
        if (Confirm.Ask(this, Loc.T("pool.delete"), Loc.T("pool.confirmDelete", p.Name)))
        { PoolStore.Current().Pools.Remove(p); PoolStore.Persist(); _onChange(); Refresh(); }
    }

    private void DeleteDuo(DuoPool d)
    {
        if (Confirm.Ask(this, Loc.T("pool.delete"), Loc.T("pool.confirmDelete", d.FriendName)))
        { PoolStore.Current().DuoPools.Remove(d); PoolStore.Persist(); _onChange(); Refresh(); }
    }
}

/// <summary>Редактор одного пула (обычного или дуо). Работает на КОПИИ — Назад/Сброс
/// не трогают исходные данные, пока не нажали Сохранить (с подтверждением).</summary>
sealed class PoolEditorWindow : Window
{
    private readonly bool _duo;
    private readonly ChampPool? _srcPool;
    private readonly DuoPool?   _srcDuo;
    private readonly Dictionary<string, int> _idByName;
    private readonly List<string> _names;

    // Рабочие копии.
    private string _name;
    private readonly Dictionary<string, List<int>> _mine   = NewRoles();
    private readonly Dictionary<string, List<int>> _friend = NewRoles();
    private bool _dirty;

    private readonly TextBox _nameBox = new() { FontSize = 15, FontWeight = FontWeights.Bold, MinWidth = 240 };
    private readonly StackPanel _body = new();

    private static Dictionary<string, List<int>> NewRoles() =>
        PoolSettingsWindow.Roles.ToDictionary(r => r, _ => new List<int>());

    public PoolEditorWindow(object? existing, bool duo)
    {
        _duo = duo;
        _idByName = DataDragon.GetAllIconUrls().Keys.ToDictionary(id => DataDragon.Name(id), id => id);
        _names = _idByName.Keys.Where(n => !string.IsNullOrEmpty(n))
                          .OrderBy(n => n, StringComparer.CurrentCulture).ToList();

        if (existing is ChampPool p)
        { _srcPool = p; _name = p.Name; CopyInto(_mine, p.ByRole); }
        else if (existing is DuoPool d)
        { _srcDuo = d; _name = d.FriendName; CopyInto(_mine, d.Mine); CopyInto(_friend, d.Friend); }
        else
            _name = "";

        Title  = _duo ? Loc.T("pool.duo") : Loc.T("pool.pool");
        Width  = 620; Height = 560;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildUi();
    }

    private static void CopyInto(Dictionary<string, List<int>> dst, Dictionary<string, List<int>> src)
    {
        foreach (var r in PoolSettingsWindow.Roles)
            dst[r] = src.TryGetValue(r, out var l) ? [.. l] : [];
    }

    private void BuildUi()
    {
        var root = new DockPanel { Margin = new Thickness(16) };

        // Верх: имя слева + Назад/Сброс/Сохранить справа.
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        _nameBox.Text = _name;
        _nameBox.TextChanged += (_, _) => { _name = _nameBox.Text; _dirty = true; };

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var back = ActionBtn(Loc.T("pool.back"));
        back.Click += (_, _) => Back();
        var reset = ActionBtn(Loc.T("pool.reset"));
        reset.Click += (_, _) => Reset();
        var save = ActionBtn(Loc.T("pool.save"), primary: true);
        save.Click += (_, _) => Save();
        btns.Children.Add(back); btns.Children.Add(reset); btns.Children.Add(save);
        DockPanel.SetDock(btns, Dock.Right);
        top.Children.Add(btns);

        var nameWrap = new StackPanel();
        nameWrap.Children.Add(new TextBlock
        {
            Text = _duo ? Loc.T("pool.friendName") : Loc.T("pool.name"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)), FontSize = 10, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 3)
        });
        nameWrap.Children.Add(_nameBox);
        top.Children.Add(nameWrap);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        root.Children.Add(new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Content = root;
        RenderBody();
    }

    private void RenderBody()
    {
        _body.Children.Clear();
        if (_duo)
        {
            _body.Children.Add(SectionLabel(Loc.T("pool.mine")));
            foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
            _body.Children.Add(SectionLabel(Loc.T("pool.friendPool")));
            foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _friend));
        }
        else
        {
            foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
        }
    }

    private static TextBlock SectionLabel(string t) => new()
    {
        Text = t, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
        FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 6)
    };

    // Ряд роли: иконка роли + имя + иконки чемпионов + квадратная «+».
    private FrameworkElement RoleRow(string role, Dictionary<string, List<int>> data)
    {
        var list = data[role];
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };

        var roleIcon = RoleIcons.Get(PoolSettingsWindow.DbToLcu[role]);
        if (roleIcon != null)
            row.Children.Add(new Image { Source = roleIcon, Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center, Opacity = 0.9 });
        row.Children.Add(new TextBlock
        {
            Text = PoolSettingsWindow.RoleNames[Array.IndexOf(PoolSettingsWindow.Roles, role)],
            Width = 38, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center
        });

        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(chips);

        void RefreshChips()
        {
            chips.Children.Clear();
            foreach (var id in list.ToList())
                chips.Children.Add(ChampIcon(id, () => { list.Remove(id); _dirty = true; RefreshChips(); }));
            // Квадратная «+» того же размера, что иконка — не пропадает.
            chips.Children.Add(PlusChamp(() =>
            {
                var pick = new ChampionPickerWindow(_names, _idByName, list) { Owner = this };
                if (pick.ShowDialog() == true && pick.Result > 0 && !list.Contains(pick.Result))
                { list.Add(pick.Result); _dirty = true; RefreshChips(); }
            }));
        }
        RefreshChips();
        return row;
    }

    private static FrameworkElement ChampIcon(int id, Action remove)
    {
        var b = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 5, 5),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x48, 0x5A)), BorderThickness = new Thickness(1),
            ToolTip = DataDragon.Name(id), Cursor = System.Windows.Input.Cursors.Hand
        };
        var g = new Grid();
        if (IconCache.Get(id) is { } src)
            g.Children.Add(new Image { Source = src, Stretch = Stretch.UniformToFill });
        else
            g.Children.Add(new TextBlock { Text = DataDragon.Name(id), Foreground = Brushes.White, FontSize = 9,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        // Крестик удаления в углу.
        g.Children.Add(new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x14, 0x1A)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = "×", FontSize = 11, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x70, 0x70)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        b.Child = g;
        b.MouseLeftButtonUp += (_, _) => remove();
        return b;
    }

    private static FrameworkElement PlusChamp(Action add)
    {
        var b = new Border
        {
            Width = 48, Height = 48, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 5, 5),
            Background = new SolidColorBrush(Color.FromArgb(0x18, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Child = new TextBlock { Text = "+", FontSize = 24, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(PoolSettingsWindow.Blue),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        b.MouseLeftButtonUp += (_, _) => add();
        return b;
    }

    // ── Действия (с подтверждением) ──────────────────────────────────────────
    private void Back()
    {
        if (_dirty && !Confirm.Ask(this, Loc.T("pool.back"), Loc.T("pool.confirmBack"))) return;
        DialogResult = false; Close();
    }

    private void Reset()
    {
        if (!Confirm.Ask(this, Loc.T("pool.reset"), Loc.T("pool.confirmReset"))) return;
        foreach (var r in PoolSettingsWindow.Roles) { _mine[r].Clear(); _friend[r].Clear(); }
        _dirty = true;
        RenderBody();
    }

    private void Save()
    {
        if (!Confirm.Ask(this, Loc.T("pool.save"), Loc.T("pool.confirmSave"))) return;
        var a = PoolStore.Current();
        if (_duo)
        {
            var d = _srcDuo ?? new DuoPool();
            d.FriendName = _name;
            d.Mine   = Clone(_mine);
            d.Friend = Clone(_friend);
            if (_srcDuo == null) a.DuoPools.Add(d);
        }
        else
        {
            var p = _srcPool ?? new ChampPool();
            p.Name  = _name;
            p.ByRole = Clone(_mine);
            if (_srcPool == null) a.Pools.Add(p);
        }
        PoolStore.Persist();
        DialogResult = true; Close();
    }

    private static Dictionary<string, List<int>> Clone(Dictionary<string, List<int>> src) =>
        src.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));

    // Кнопки — дефолтного стиля WPF, как выпадающие списки/кнопки по всей программе.
    private static Button ActionBtn(string text, bool primary = false) => new()
    {
        Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0),
        Cursor = System.Windows.Input.Cursors.Hand,
        FontWeight = primary ? FontWeights.Bold : FontWeights.Normal
    };
}

/// <summary>Выбор чемпиона: поиск сверху + общий список. Клик — выбрать.</summary>
sealed class ChampionPickerWindow : Window
{
    public int Result;
    private readonly Dictionary<string, int> _idByName;
    private readonly HashSet<int> _exclude;
    private readonly List<string> _names;
    private readonly WrapPanel _grid = new();
    private readonly TextBox _search = new() { FontSize = 13 };

    public ChampionPickerWindow(List<string> names, Dictionary<string, int> idByName, IEnumerable<int> exclude)
    {
        _names = names; _idByName = idByName; _exclude = [.. exclude];
        Title  = Loc.T("pool.pickChamp");
        Width  = 460; Height = 520;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new DockPanel { Margin = new Thickness(14) };
        _search.TextChanged += (_, _) => Render();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        root.Children.Add(new ScrollViewer
        {
            Content = _grid, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = root;
        _search.Focus();
        Render();
    }

    private void Render()
    {
        var q = _search.Text.Trim();
        _grid.Children.Clear();
        foreach (var name in _names)
        {
            var id = _idByName[name];
            if (_exclude.Contains(id)) continue;
            if (q.Length > 0 && name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0) continue;

            var b = new Border
            {
                Width = 74, Height = 92, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 6, 6),
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x20, 0x2C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)), BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var sp = new StackPanel { Margin = new Thickness(4) };
            if (IconCache.Get(id) is { } src)
                sp.Children.Add(new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill } });
            sp.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 10,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
            b.Child = sp;
            var cid = id;
            b.MouseLeftButtonUp += (_, _) => { Result = cid; DialogResult = true; Close(); };
            _grid.Children.Add(b);
        }
    }
}

/// <summary>Диалог подтверждения (Да/Нет) с предупреждением.</summary>
static class Confirm
{
    public static bool Ask(Window owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title, Width = 380, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg)
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = "⚠ " + message, Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 18, Margin = new Thickness(0, 0, 0, 14)
        });
        bool ok = false;
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var no = new Button { Content = Loc.T("pool.no"), Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand };
        no.Click += (_, _) => dlg.Close();
        var yes = new Button { Content = Loc.T("pool.yes"), Padding = new Thickness(12, 4, 12, 4), FontWeight = FontWeights.Bold,
            Cursor = System.Windows.Input.Cursors.Hand };
        yes.Click += (_, _) => { ok = true; dlg.Close(); };
        btns.Children.Add(no); btns.Children.Add(yes);
        sp.Children.Add(btns);
        dlg.Content = sp;
        dlg.ShowDialog();
        return ok;
    }
}
