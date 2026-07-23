using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MenuItem = System.Windows.Controls.MenuItem;
using Orientation = System.Windows.Controls.Orientation;
using TabControl = System.Windows.Controls.TabControl;
using TabItem = System.Windows.Controls.TabItem;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно настройки пулов чемпионов («ТВОЙ ПУЛ ПРОТИВ ВРАГОВ»): обычные пулы и
/// дуо-пулы (мой набор + набор друга) по ролям, плюс импорт пулов другого
/// аккаунта этого ПК. Изменения сохраняются сразу (PoolStore).
/// </summary>
sealed class PoolSettingsWindow : Window
{
    private static readonly string[] Roles     = ["top", "jungle", "mid", "adc", "support"];
    private static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];

    private readonly Action _onChange;
    private readonly Dictionary<string, int> _idByName;
    private readonly List<string> _names;

    private readonly ComboBox   _poolSel = new() { Width = 240, Margin = new Thickness(0, 0, 6, 0) };
    private readonly ComboBox   _duoSel  = new() { Width = 240, Margin = new Thickness(0, 0, 6, 0) };
    private readonly StackPanel _poolEditor = new();
    private readonly StackPanel _duoEditor  = new();

    public PoolSettingsWindow(Action onChange)
    {
        _onChange = onChange;
        _idByName = DataDragon.GetAllIconUrls().Keys.ToDictionary(id => DataDragon.Name(id), id => id);
        _names = _idByName.Keys.Where(n => !string.IsNullOrEmpty(n))
                          .OrderBy(n => n, StringComparer.CurrentCulture).ToList();

        Title  = Loc.T("pool.settings");
        Width  = 760; Height = 600;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(14), LastChildFill = true };

        // Низ: импорт + закрыть.
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var import = SmallButton("⇩ " + Loc.T("pool.import"));
        import.Click += (_, _) => ImportDialog();
        DockPanel.SetDock(import, Dock.Left);
        bottom.Children.Add(import);
        var close = SmallButton(Loc.T("pool.close"));
        close.Width = 110; close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        bottom.Children.Add(close);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        // Вкладки: Пулы / Дуо-пулы.
        var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabs.Items.Add(new TabItem { Header = Loc.T("pool.pool"), Content = BuildPoolsTab() });
        tabs.Items.Add(new TabItem { Header = Loc.T("pool.duo"),  Content = BuildDuoTab() });
        root.Children.Add(tabs);

        Content = root;
        RefreshPoolSel();
        RefreshDuoSel();
    }

    // ── Вкладка обычных пулов ────────────────────────────────────────────────
    private FrameworkElement BuildPoolsTab()
    {
        var panel = new StackPanel { Margin = new Thickness(6, 12, 6, 6) };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Label(Loc.T("pool.pool") + ":"));
        _poolSel.SelectionChanged += (_, _) => RenderPoolEditor();
        bar.Children.Add(_poolSel);
        var add = SmallButton("+ " + Loc.T("pool.new"));
        add.Click += (_, _) =>
        {
            var p = new ChampPool { Name = Loc.T("pool.pool") + " " + (PoolStore.Current().Pools.Count + 1) };
            PoolStore.Current().Pools.Add(p);
            PoolStore.Persist();
            RefreshPoolSel(p.Id);
            _onChange();
        };
        bar.Children.Add(add);
        var rename = SmallButton(Loc.T("pool.rename"));
        rename.Click += (_, _) => RenamePool();
        bar.Children.Add(rename);
        var del = SmallButton(Loc.T("pool.delete"));
        del.Click += (_, _) =>
        {
            if (SelectedPool() is { } p)
            {
                PoolStore.Current().Pools.Remove(p);
                PoolStore.Persist();
                RefreshPoolSel();
                _onChange();
            }
        };
        bar.Children.Add(del);
        panel.Children.Add(bar);

        panel.Children.Add(new ScrollViewer
        {
            Content = _poolEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420
        });
        return panel;
    }

    private ChampPool? SelectedPool() =>
        _poolSel.SelectedItem is ComboBoxItem { Tag: string id }
            ? PoolStore.Current().Pools.FirstOrDefault(p => p.Id == id) : null;

    private void RefreshPoolSel(string? select = null)
    {
        _poolSel.Items.Clear();
        foreach (var p in PoolStore.Current().Pools)
            _poolSel.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Id });
        if (_poolSel.Items.Count > 0)
        {
            _poolSel.SelectedIndex = 0;
            if (select != null)
                for (int i = 0; i < _poolSel.Items.Count; i++)
                    if (((ComboBoxItem)_poolSel.Items[i]).Tag as string == select) { _poolSel.SelectedIndex = i; break; }
        }
        RenderPoolEditor();
    }

    private void RenamePool()
    {
        if (SelectedPool() is not { } p) return;
        var name = Prompt(Loc.T("pool.rename"), p.Name);
        if (name != null) { p.Name = name; PoolStore.Persist(); RefreshPoolSel(p.Id); _onChange(); }
    }

    private void RenderPoolEditor()
    {
        _poolEditor.Children.Clear();
        if (SelectedPool() is not { } p) return;
        foreach (var (role, i) in Roles.Select((r, i) => (r, i)))
            _poolEditor.Children.Add(RoleEditor(RoleNames[i], role, p.ByRole, () => { PoolStore.Persist(); _onChange(); }));
    }

    // ── Вкладка дуо-пулов ────────────────────────────────────────────────────
    private FrameworkElement BuildDuoTab()
    {
        var panel = new StackPanel { Margin = new Thickness(6, 12, 6, 6) };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        bar.Children.Add(Label(Loc.T("pool.duo") + ":"));
        _duoSel.SelectionChanged += (_, _) => RenderDuoEditor();
        bar.Children.Add(_duoSel);
        var add = SmallButton("+ " + Loc.T("pool.new"));
        add.Click += (_, _) =>
        {
            var d = new DuoPool { FriendName = Loc.T("pool.friend") + " " + (PoolStore.Current().DuoPools.Count + 1) };
            PoolStore.Current().DuoPools.Add(d);
            PoolStore.Persist();
            RefreshDuoSel(d.Id);
            _onChange();
        };
        bar.Children.Add(add);
        var rename = SmallButton(Loc.T("pool.friendName"));
        rename.Click += (_, _) =>
        {
            if (SelectedDuo() is { } d)
            {
                var name = Prompt(Loc.T("pool.friendName"), d.FriendName);
                if (name != null) { d.FriendName = name; PoolStore.Persist(); RefreshDuoSel(d.Id); _onChange(); }
            }
        };
        bar.Children.Add(rename);
        var del = SmallButton(Loc.T("pool.delete"));
        del.Click += (_, _) =>
        {
            if (SelectedDuo() is { } d)
            { PoolStore.Current().DuoPools.Remove(d); PoolStore.Persist(); RefreshDuoSel(); _onChange(); }
        };
        bar.Children.Add(del);
        panel.Children.Add(bar);

        panel.Children.Add(new ScrollViewer
        {
            Content = _duoEditor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420
        });
        return panel;
    }

    private DuoPool? SelectedDuo() =>
        _duoSel.SelectedItem is ComboBoxItem { Tag: string id }
            ? PoolStore.Current().DuoPools.FirstOrDefault(d => d.Id == id) : null;

    private void RefreshDuoSel(string? select = null)
    {
        _duoSel.Items.Clear();
        foreach (var d in PoolStore.Current().DuoPools)
            _duoSel.Items.Add(new ComboBoxItem { Content = d.FriendName, Tag = d.Id });
        if (_duoSel.Items.Count > 0)
        {
            _duoSel.SelectedIndex = 0;
            if (select != null)
                for (int i = 0; i < _duoSel.Items.Count; i++)
                    if (((ComboBoxItem)_duoSel.Items[i]).Tag as string == select) { _duoSel.SelectedIndex = i; break; }
        }
        RenderDuoEditor();
    }

    private void RenderDuoEditor()
    {
        _duoEditor.Children.Clear();
        if (SelectedDuo() is not { } d) return;
        _duoEditor.Children.Add(Label(Loc.T("pool.mine")));
        foreach (var (role, i) in Roles.Select((r, i) => (r, i)))
            _duoEditor.Children.Add(RoleEditor(RoleNames[i], role, d.Mine, () => { PoolStore.Persist(); _onChange(); }));
        _duoEditor.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)), Margin = new Thickness(0, 10, 0, 10) });
        _duoEditor.Children.Add(Label(Loc.T("pool.friendPool")));
        foreach (var (role, i) in Roles.Select((r, i) => (r, i)))
            _duoEditor.Children.Add(RoleEditor(RoleNames[i], role, d.Friend, () => { PoolStore.Persist(); _onChange(); }));
    }

    // ── Редактор одной роли: подпись + добавление + чипы выбранных ────────────
    private FrameworkElement RoleEditor(string roleName, string role, Dictionary<string, List<int>> data, Action save)
    {
        var list = data.TryGetValue(role, out var l) ? l : (data[role] = []);

        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var lbl = new TextBlock
        {
            Text = roleName, Width = 40, VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontWeight = FontWeights.Bold, FontSize = 11
        };
        DockPanel.SetDock(lbl, Dock.Left);
        row.Children.Add(lbl);

        // Чипы выбранных чемпионов (объявляем до обработчиков, которые их обновляют).
        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };

        // Добавление чемпиона по имени.
        var add = new ComboBox
        {
            IsEditable = true, IsTextSearchEnabled = true, ItemsSource = _names,
            Width = 150, Margin = new Thickness(0, 0, 8, 0), Text = ""
        };
        DockPanel.SetDock(add, Dock.Left);
        void DoAdd()
        {
            var name = (add.SelectedItem as string ?? add.Text ?? "").Trim();
            if (_idByName.TryGetValue(name, out var id) && !list.Contains(id))
            { list.Add(id); save(); RefreshChips(); }
            add.Text = "";
        }
        add.SelectionChanged += (_, _) => DoAdd();
        row.Children.Add(add);
        row.Children.Add(chips);

        void RefreshChips()
        {
            chips.Children.Clear();
            foreach (var id in list.ToList())
            {
                var chip = new Border
                {
                    CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 4, 4),
                    Padding = new Thickness(6, 1, 3, 1),
                    Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x24, 0x30)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x48, 0x5A)),
                    BorderThickness = new Thickness(1)
                };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock
                {
                    Text = DataDragon.Name(id), Foreground = Brushes.White, FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
                });
                var x = new Button
                {
                    Content = "×", FontSize = 12, FontWeight = FontWeights.Bold, Padding = new Thickness(0),
                    Width = 16, Height = 16, Cursor = System.Windows.Input.Cursors.Hand,
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x60, 0x60))
                };
                var cid = id;
                x.Click += (_, _) => { list.Remove(cid); save(); RefreshChips(); };
                sp.Children.Add(x);
                chip.Child = sp;
                chips.Children.Add(chip);
            }
        }
        RefreshChips();
        return row;
    }

    // ── Импорт с другого аккаунта ────────────────────────────────────────────
    private void ImportDialog()
    {
        var others = PoolStore.OtherAccounts();
        if (others.Count == 0) { Prompt(Loc.T("pool.import"), Loc.T("pool.noOther"), readOnly: true); return; }
        var menu = new ContextMenu();
        foreach (var (puuid, name) in others)
        {
            var mi = new MenuItem { Header = name };
            mi.Click += (_, _) =>
            {
                PoolStore.ImportFrom(puuid);
                RefreshPoolSel(); RefreshDuoSel(); _onChange();
            };
            menu.Items.Add(mi);
        }
        menu.IsOpen = true;
    }

    // ── Мелочи UI ────────────────────────────────────────────────────────────
    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
        FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 4)
    };

    private static Button SmallButton(string text) => new()
    {
        Content = text, Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    // Простой однострочный диалог ввода/сообщения. Возвращает текст или null (отмена).
    private string? Prompt(string title, string initial, bool readOnly = false)
    {
        var dlg = new Window
        {
            Title = title, Width = 340, Height = 130, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D)), ResizeMode = ResizeMode.NoResize
        };
        var sp = new StackPanel { Margin = new Thickness(14) };
        var tb = new TextBox { Text = initial, IsReadOnly = readOnly, Margin = new Thickness(0, 0, 0, 12) };
        sp.Children.Add(tb);
        string? result = null;
        var ok = SmallButton("OK"); ok.HorizontalAlignment = HorizontalAlignment.Right;
        ok.Click += (_, _) => { result = readOnly ? null : tb.Text.Trim(); dlg.Close(); };
        sp.Children.Add(ok);
        dlg.Content = sp;
        tb.Focus(); tb.SelectAll();
        dlg.ShowDialog();
        return string.IsNullOrEmpty(result) ? (readOnly ? null : result) : result;
    }
}
