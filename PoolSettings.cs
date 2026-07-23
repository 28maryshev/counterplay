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
/// Окно настройки пулов чемпионов. Две области через тонкую полупрозрачную черту:
/// слева (шире) — обычные пулы, справа — дуо-пулы. В каждой — квадратная «+» и
/// плитки существующих пулов. «+» / клик по плитке открывает редактор пула.
/// </summary>
sealed class PoolSettingsWindow : Window
{
    internal static readonly string[] Roles     = ["top", "jungle", "mid", "adc", "support"];
    internal static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];
    // Полные названия ролей (для выпадающего списка роли в связках).
    internal static readonly string[] RoleFull  = ["Top", "Jungle", "Mid", "Bot", "Support"];
    // db-роль → LCU-позиция (для иконок ролей, как в тир-листе).
    internal static readonly Dictionary<string, string> DbToLcu = new()
        { ["top"] = "top", ["jungle"] = "jungle", ["mid"] = "middle", ["adc"] = "bottom", ["support"] = "utility" };

    internal static readonly Color Bg     = Color.FromRgb(0x0E, 0x14, 0x1D);
    internal static readonly Color Blue   = Color.FromRgb(0x5A, 0x8A, 0xC8);
    internal static readonly Color Line   = Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2);

    private readonly Action _onChange;
    private readonly RecommendationEngine? _engine;
    private readonly WrapPanel _poolArea = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _duoArea  = new() { Margin = new Thickness(0, 8, 0, 0) };

    public PoolSettingsWindow(Action onChange, RecommendationEngine? engine = null)
    {
        _onChange = onChange;
        _engine   = engine;
        Title  = Loc.T("pool.settings");
        Width  = 820; Height = 560;
        Background = new SolidColorBrush(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PoolUi.Apply(this);

        var grid = new Grid { Margin = new Thickness(16) };
        // Колонки равные — разделительная черта строго по центру окна.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(Area(Loc.T("pool.pool"), _poolArea, duo: false, 0));

        // Тонкая полупрозрачная разделительная черта.
        var divider = new Border { Width = 1, Background = new SolidColorBrush(Line), Margin = new Thickness(14, 0, 14, 0) };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        grid.Children.Add(Area(Loc.T("pool.duo"), _duoArea, duo: true, 2));

        Content = PoolUi.Chrome(this, Title, grid);
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
        foreach (var p in a.Pools)
            _poolArea.Children.Add(Tile(p.Name, a.ActiveKind == PoolKind.Pool && a.ActiveId == p.Id,
                () => EditPool(p), () => DeletePool(p), () => Select(PoolKind.Pool, p.Id)));
        _poolArea.Children.Add(PlusTile(() => EditPool(null)));

        _duoArea.Children.Clear();
        foreach (var d in a.DuoPools)
            _duoArea.Children.Add(Tile(d.FriendName, a.ActiveKind == PoolKind.Duo && a.ActiveId == d.Id,
                () => EditDuo(d), () => DeleteDuo(d), () => Select(PoolKind.Duo, d.Id),
                Loc.T(d.Manual ? "pool.duoManual" : "pool.duoAuto")));
        _duoArea.Children.Add(PlusTile(() => EditDuo(null)));
    }

    // Сделать пул активным (звёздочка). Повторный клик по активному — снять выбор
    // (возврат в обычный режим подбора).
    private void Select(PoolKind kind, string id)
    {
        var a = PoolStore.Current();
        if (a.ActiveKind == kind && a.ActiveId == id) PoolStore.SetActive(PoolKind.Normal, null);
        else                                          PoolStore.SetActive(kind, id);
        _onChange();
        Refresh();
    }

    // Плитка существующего пула: имя + клик (редактировать) + × (удалить) + ★ выбора.
    // Активный (выбранный сейчас) пул выделен синей рамкой и залитой звездой.
    private FrameworkElement Tile(string name, bool active, Action open, Action del, Action select, string mode = "")
    {
        var b = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
            Background = new SolidColorBrush(active ? Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8) : Color.FromRgb(0x16, 0x20, 0x2C)),
            BorderBrush = new SolidColorBrush(active ? Blue : Color.FromRgb(0x30, 0x42, 0x54)),
            BorderThickness = new Thickness(active ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var g = new Grid();
        g.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(name) ? "—" : name, Foreground = Brushes.White,
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            // Есть пометка режима внизу — оставляем ей место, чтобы имя не наезжало.
            Margin = mode.Length > 0 ? new Thickness(6, 6, 6, 20) : new Thickness(6)
        });
        // Режим подбора пары дуо-пула — снизу плитки.
        if (mode.Length > 0)
            g.Children.Add(new TextBlock
            {
                Text = mode, FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 7)
            });
        // Звезда выбора — левый верхний угол.
        var star = new Button
        {
            Content = active ? "★" : "☆", Width = 22, Height = 22, FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(active ? Color.FromRgb(0xC8, 0x9B, 0x3C) : Color.FromRgb(0x60, 0x70, 0x80)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T(active ? "pool.selected" : "pool.select")
        };
        star.Click += (_, e) => { e.Handled = true; select(); };
        g.Children.Add(star);
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
        var win = new PoolEditorWindow(existing, duo: false, _engine) { Owner = this };
        if (win.ShowDialog() == true) _onChange();
        Refresh();
    }

    private void EditDuo(DuoPool? existing)
    {
        var win = new PoolEditorWindow(existing, duo: true, _engine) { Owner = this };
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
    private readonly RecommendationEngine? _engine;
    private readonly ChampPool? _srcPool;
    private readonly DuoPool?   _srcDuo;
    private readonly Dictionary<string, int> _idByName;
    private readonly List<string> _names;

    // Рабочие копии.
    private string _name;
    private readonly Dictionary<string, List<int>> _mine   = NewRoles();
    private readonly Dictionary<string, List<int>> _friend = NewRoles();
    private bool _dirty;

    // Дуо: способ подбора. Manual — фиксированные связки (список пар), иначе автоподбор.
    // По умолчанию Manual (для нового пула); существующий грузит сохранённый выбор.
    private bool _manual = true;
    private readonly List<ManualDuoPair> _manualPairs = [];

    private readonly TextBox _nameBox = new() { FontSize = 15, FontWeight = FontWeights.Bold, MinWidth = 240 };
    private readonly StackPanel _body = new();

    private static Dictionary<string, List<int>> NewRoles() =>
        PoolSettingsWindow.Roles.ToDictionary(r => r, _ => new List<int>());

    public PoolEditorWindow(object? existing, bool duo, RecommendationEngine? engine = null)
    {
        _duo = duo;
        _engine = engine;
        _idByName = DataDragon.GetAllIconUrls().Keys.ToDictionary(id => DataDragon.Name(id), id => id);
        _names = _idByName.Keys.Where(n => !string.IsNullOrEmpty(n))
                          .OrderBy(n => n, StringComparer.CurrentCulture).ToList();

        if (existing is ChampPool p)
        { _srcPool = p; _name = p.Name; CopyInto(_mine, p.ByRole); }
        else if (existing is DuoPool d)
        { _srcDuo = d; _name = d.FriendName; CopyInto(_mine, d.Mine); CopyInto(_friend, d.Friend);
          _manual = d.Manual;
          _manualPairs.AddRange(d.ManualPairs.Select(p => new ManualDuoPair {
              Mine = p.Mine, MineRole = p.MineRole, Friend = p.Friend, FriendRole = p.FriendRole })); }
        else
            _name = "";

        Title  = _duo ? Loc.T("pool.duo") : Loc.T("pool.pool");
        Width  = 620; Height = 560;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        PoolUi.Apply(this);

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

        Content = PoolUi.Chrome(this, Title, root);
        RenderBody();
    }

    private void RenderBody()
    {
        _body.Children.Clear();
        if (_duo)
        {
            _body.Children.Add(ModeToggle());
            if (_manual)
            {
                // Ручные связки: список пар (мой + друга), каждая строка — одна связка.
                _body.Children.Add(SectionLabel(Loc.T("pool.duoManualHint")));
                _body.Children.Add(ManualHeader());
                foreach (var mp in _manualPairs.ToList()) _body.Children.Add(ManualPairRow(mp));
                _body.Children.Add(AddPairRow());   // пустая строка «+ +» — добавить связку
            }
            else
            {
                // Авто: наборы по ролям, лучшая пара подбирается движком.
                _body.Children.Add(SectionLabel(Loc.T("pool.mine")));
                foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
                _body.Children.Add(SectionLabel(Loc.T("pool.friendPool")));
                foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _friend));
            }
        }
        else
        {
            foreach (var r in PoolSettingsWindow.Roles) _body.Children.Add(RoleRow(r, _mine));
        }
    }

    // Переключатель «Авто / Ручной» подбора дуо-пары.
    private FrameworkElement ModeToggle()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 6),
            VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.duoMode"), Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0)
        });
        row.Children.Add(ModeBtn(Loc.T("pool.duoManual"),  _manual, () => { if (!_manual) { _manual = true;  _dirty = true; RenderBody(); } }));
        row.Children.Add(ModeBtn(Loc.T("pool.duoAuto"),   !_manual, () => { if (_manual)  { _manual = false; _dirty = true; RenderBody(); } }));
        return row;
    }

    private static Button ModeBtn(string text, bool active, Action click)
    {
        var b = PoolUi.Btn(text);
        b.Margin = new Thickness(0, 0, 6, 0);
        if (active)
        {
            b.Background  = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
            b.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
            b.Foreground  = Brushes.White;
            b.FontWeight  = FontWeights.Bold;
        }
        b.Click += (_, _) => click();
        return b;
    }

    // Фиксированные ширины колонок связки: слот = самой длинной подписи роли
    // (Support), чтобы иконки не сдвигались и не закрывали WR/дельту.
    private const double DuoSlot = 82, DuoPlus = 30, DuoStats = 80;

    // Заголовки колонок связок: «Мой» + «Друг» + «WR · Δ», выровнены под слоты.
    private static FrameworkElement ManualHeader()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        TextBlock H(string t, double w) => new()
        {
            Text = t, Width = w, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontSize = 10, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Bottom
        };
        row.Children.Add(H(Loc.T("pool.mine"),   DuoSlot));
        row.Children.Add(H("",                   DuoPlus));
        row.Children.Add(H(Loc.T("pool.friend"), DuoSlot));
        row.Children.Add(H(Loc.T("pool.duoStatsHdr"), DuoStats));
        return row;
    }

    // Сетка одной связки с ФИКСИРОВАННЫМИ колонками — ничего не сдвигается.
    private static Grid PairGrid()
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DuoSlot) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DuoPlus) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DuoSlot) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DuoStats) });
        return g;
    }

    private static void Place(Grid g, FrameworkElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    // Строка одной связки: слот моего (чемпион+роль) + «+» + слот друга + статистика.
    private FrameworkElement ManualPairRow(ManualDuoPair mp)
    {
        // Роль пустая (старая связка / только что выбран чемпион) → авто-роль по частоте.
        if (mp.Mine   != 0 && string.IsNullOrEmpty(mp.MineRole))   mp.MineRole   = DefaultRole(mp.Mine);
        if (mp.Friend != 0 && string.IsNullOrEmpty(mp.FriendRole)) mp.FriendRole = DefaultRole(mp.Friend);

        var g = PairGrid();
        Place(g, ManualSlot(mp.Mine, mp.MineRole,
            id   => { mp.Mine = id; mp.MineRole = id != 0 ? DefaultRole(id) : ""; DropIfEmpty(mp); _dirty = true; RenderBody(); },
            role => { mp.MineRole = role; _dirty = true; RenderBody(); }), 0);
        Place(g, PlusGlyph(), 1);
        Place(g, ManualSlot(mp.Friend, mp.FriendRole,
            id   => { mp.Friend = id; mp.FriendRole = id != 0 ? DefaultRole(id) : ""; DropIfEmpty(mp); _dirty = true; RenderBody(); },
            role => { mp.FriendRole = role; _dirty = true; RenderBody(); }), 2);
        Place(g, PairStatsBlock(mp), 3);
        return g;
    }

    private void DropIfEmpty(ManualDuoPair mp)
    { if (mp.Mine == 0 && mp.Friend == 0) _manualPairs.Remove(mp); }

    // Роль по умолчанию — самая частая роль чемпиона (движок), иначе пусто.
    private string DefaultRole(int champId) => _engine?.PrimaryRole(champId) ?? "";

    // Пустая строка «+ + +» — заполнение любого слота создаёт новую связку.
    private FrameworkElement AddPairRow()
    {
        var g = PairGrid();
        Place(g, ManualSlot(0, "", id => { _manualPairs.Add(new ManualDuoPair { Mine   = id, MineRole   = DefaultRole(id) }); _dirty = true; RenderBody(); }, null), 0);
        Place(g, PlusGlyph(), 1);
        Place(g, ManualSlot(0, "", id => { _manualPairs.Add(new ManualDuoPair { Friend = id, FriendRole = DefaultRole(id) }); _dirty = true; RenderBody(); }, null), 2);
        return g;
    }

    // Разделитель-«+» между двумя чемпионами связки (не кликается), по центру иконки.
    private static FrameworkElement PlusGlyph() => new TextBlock
    {
        Text = "+", FontSize = 22, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(PoolSettingsWindow.Blue), TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, DuoSlot / 2 - 14, 0, 0)
    };

    // Статистика связки: винрейт вместе + дельта (синергия), с пояснениями в тултипах.
    private FrameworkElement PairStatsBlock(ManualDuoPair mp)
    {
        var wrap = new StackPanel { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, DuoSlot / 2 - 16, 0, 0) };
        if (_engine == null || mp.Mine == 0 || mp.Friend == 0) return wrap;

        var (games, wr, delta) = _engine.PairStats(mp.Mine, mp.MineRole, mp.Friend, mp.FriendRole);
        if (games <= 0)
        {
            wrap.Children.Add(new TextBlock
            {
                Text = "—", Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x78, 0x86)), FontSize = 12,
                ToolTip = Loc.T("pool.duoNoData")
            });
            return wrap;
        }

        wrap.Children.Add(new TextBlock
        {
            Text = $"{wr:F1}%", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
            ToolTip = Loc.T("pool.duoWrTip", games)
        });
        var dCol = delta > 0.2  ? Color.FromRgb(0x5A, 0xC0, 0x8A)
                 : delta < -0.2 ? Color.FromRgb(0xE0, 0x50, 0x50)
                                : Color.FromRgb(0x9F, 0xB3, 0xC8);
        wrap.Children.Add(new TextBlock
        {
            Text = "Δ " + (delta >= 0 ? "+" : "") + delta.ToString("F1"),
            Foreground = new SolidColorBrush(dCol), FontSize = 11, FontWeight = FontWeights.Bold,
            ToolTip = Loc.T("pool.duoDeltaTip")
        });
        return wrap;
    }

    // Слот связки: квадрат чемпиона (клик очищает) или «+» (клик выбирает) шириной
    // с колонку, а СНИЗУ той же ширины — выпадающий список роли (иконка + название).
    private FrameworkElement ManualSlot(int id, string role, Action<int> setChamp, Action<string>? setRole)
    {
        var wrap = new StackPanel { Width = DuoSlot, VerticalAlignment = VerticalAlignment.Top };
        var zero = new Thickness(0);
        FrameworkElement tile = id != 0
            ? ChampIcon(id, () => setChamp(0), DuoSlot, zero)
            : PlusChamp(() =>
              {
                  var pick = new ChampionPickerWindow(_names, _idByName, Array.Empty<int>()) { Owner = this };
                  if (pick.ShowDialog() == true && pick.Result > 0) setChamp(pick.Result);
              }, DuoSlot, zero);
        wrap.Children.Add(tile);
        if (id != 0 && setRole != null) wrap.Children.Add(RoleCombo(role, setRole));
        return wrap;
    }

    // Выпадающий список роли (под квадратом чемпиона, той же ширины): пункты —
    // иконка роли + полное название. Выбор роли пересчитывает статистику связки.
    private static FrameworkElement RoleCombo(string role, Action<string> setRole)
    {
        var opts = PoolSettingsWindow.Roles.Select((r, i) => new RoleOption
        {
            Role = r,
            Name = PoolSettingsWindow.RoleFull[i],
            Icon = RoleIcons.Get(PoolSettingsWindow.DbToLcu[r]),
        }).ToList();

        var cb = new ComboBox
        {
            Width = DuoSlot, Margin = new Thickness(0, 3, 0, 0),
            ItemsSource = opts, ItemTemplate = PoolUi.RoleItemTemplate,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        // Начальный выбор ставим ДО подписки — чтобы не сработал ложный пересчёт.
        cb.SelectedItem = opts.FirstOrDefault(o => o.Role == role);
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is RoleOption o) setRole(o.Role); };
        return cb;
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
        // DockPanel, а не горизонтальный StackPanel: тот даёт детям БЕСКОНЕЧНУЮ
        // ширину, из-за чего WrapPanel никогда не переносил чемпионов и длинный
        // ряд просто уезжал за край окна. Здесь список получает остаток ширины
        // и переносится на новую строку.
        var row = new DockPanel { Margin = new Thickness(0, 5, 0, 5), LastChildFill = true };

        var roleIcon = RoleIcons.Get(PoolSettingsWindow.DbToLcu[role]);
        if (roleIcon != null)
        {
            var ri = new Image { Source = roleIcon, Width = 26, Height = 26, Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Top, Opacity = 0.9 };
            DockPanel.SetDock(ri, Dock.Left);
            row.Children.Add(ri);
        }
        var roleLbl = new TextBlock
        {
            Text = PoolSettingsWindow.RoleNames[Array.IndexOf(PoolSettingsWindow.Roles, role)],
            Width = 38, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontWeight = FontWeights.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0)
        };
        DockPanel.SetDock(roleLbl, Dock.Left);
        row.Children.Add(roleLbl);

        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Top };
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

    private static FrameworkElement ChampIcon(int id, Action remove, double size = 48, Thickness? margin = null)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6), Margin = margin ?? new Thickness(0, 0, 5, 5),
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

    private static FrameworkElement PlusChamp(Action add, double size = 48, Thickness? margin = null)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(6), Margin = margin ?? new Thickness(0, 0, 5, 5),
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
        _manual = false; _manualPairs.Clear();
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
            d.FriendName   = _name;
            d.Mine         = Clone(_mine);
            d.Friend       = Clone(_friend);
            d.Manual      = _manual;
            // Сохраняем только заполненные связки (хотя бы один чемпион).
            d.ManualPairs = _manualPairs.Where(p => p.Mine != 0 || p.Friend != 0)
                                        .Select(p => new ManualDuoPair { Mine = p.Mine, MineRole = p.MineRole,
                                                                         Friend = p.Friend, FriendRole = p.FriendRole }).ToList();
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

    // Кнопки — единый тёмный стиль (PoolUi). Сохранить — с синим акцентом.
    private static Button ActionBtn(string text, bool primary = false)
    {
        var b = PoolUi.Btn(text);
        b.Margin = new Thickness(6, 0, 0, 0);
        b.FontWeight = primary ? FontWeights.Bold : FontWeights.Normal;
        if (primary)
        {
            b.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x5A, 0x8A, 0xC8));
            b.BorderBrush = new SolidColorBrush(PoolSettingsWindow.Blue);
            b.Foreground = Brushes.White;
        }
        return b;
    }
}

/// <summary>Пункт выпадающего списка роли: иконка + полное название (шаблон RoleItem).</summary>
sealed class RoleOption
{
    public ImageSource? Icon { get; init; }
    public string Name { get; init; } = "";
    public string Role { get; init; } = "";
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
        PoolUi.Apply(this);

        var root = new DockPanel { Margin = new Thickness(14) };
        _search.TextChanged += (_, _) => Render();
        DockPanel.SetDock(_search, Dock.Top);
        root.Children.Add(_search);
        root.Children.Add(new ScrollViewer
        {
            Content = _grid, Margin = new Thickness(0, 10, 0, 0), VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        });
        Content = PoolUi.Chrome(this, Title, root);
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

/// <summary>Единый тёмный стиль окон пулов: кнопки, поля ввода, верхушка окна.</summary>
static class PoolUi
{
    private static ResourceDictionary? _rd;
    public static Style ButtonStyle { get; private set; } = null!;
    public static DataTemplate RoleItemTemplate { get { Ensure(); return _roleItem!; } }
    private static DataTemplate? _roleItem;

    private const string Xaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='CpButton' TargetType='Button'>
    <Setter Property='Foreground' Value='#C9D2DC'/>
    <Setter Property='Background' Value='#1A2430'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,5'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='b' CornerRadius='5' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center' Margin='{TemplateBinding Padding}'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#22364F'/>
              <Setter TargetName='b' Property='BorderBrush' Value='#5A8AC8'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='b' Property='Background' Value='#2C445F'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Пункт списка роли: иконка роли + полное название мелким шрифтом -->
  <DataTemplate x:Key='RoleItem'>
    <StackPanel Orientation='Horizontal'>
      <Image Source='{Binding Icon}' Width='14' Height='14' Margin='0,0,5,0' VerticalAlignment='Center'/>
      <TextBlock Text='{Binding Name}' FontSize='10' VerticalAlignment='Center'/>
    </StackPanel>
  </DataTemplate>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='#D7DEE6'/>
    <Setter Property='Padding' Value='7,4'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBoxItem'>
          <Border x:Name='ib' Background='Transparent' CornerRadius='3' Padding='{TemplateBinding Padding}'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='ib' Property='Background' Value='#22364F'/>
            </Trigger>
            <Trigger Property='IsSelected' Value='True'>
              <Setter TargetName='ib' Property='Background' Value='#2C445F'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='#E6EDF3'/>
    <Setter Property='Background' Value='#0F1822'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='SnapsToDevicePixels' Value='True'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ComboBox'>
          <Grid>
            <ToggleButton x:Name='tb' Focusable='False' ClickMode='Press'
                Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}'
                IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
              <ToggleButton.Template>
                <ControlTemplate TargetType='ToggleButton'>
                  <Border x:Name='cb' CornerRadius='5' Background='{TemplateBinding Background}'
                          BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>
                    <Path HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,7,0'
                          Data='M0,0 L4,4 L8,0 Z' Fill='#9FB3C8'/>
                  </Border>
                  <ControlTemplate.Triggers>
                    <Trigger Property='IsMouseOver' Value='True'>
                      <Setter TargetName='cb' Property='BorderBrush' Value='#5A8AC8'/>
                      <Setter TargetName='cb' Property='Background' Value='#16202C'/>
                    </Trigger>
                  </ControlTemplate.Triggers>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Content='{TemplateBinding SelectionBoxItem}'
                ContentTemplate='{TemplateBinding SelectionBoxItemTemplate}'
                Margin='7,3,20,3' VerticalAlignment='Center' HorizontalAlignment='Left'
                IsHitTestVisible='False'/>
            <Popup x:Name='PART_Popup' Placement='Bottom' AllowsTransparency='True' Focusable='False'
                   PopupAnimation='Slide'
                   IsOpen='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}}'>
              <Border Background='#121A24' BorderBrush='#35485A' BorderThickness='1' CornerRadius='5'
                      Margin='0,2,0,0'
                      MinWidth='{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}'>
                <ScrollViewer MaxHeight='240'>
                  <ItemsPresenter/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <!-- Полоса прокрутки: тонкая, без стрелок, в тон окну -->
  <Style x:Key='CpScrollThumb' TargetType='Thumb'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Thumb'>
          <Border x:Name='th' Background='#35485A' CornerRadius='4' Margin='2'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='th' Property='Background' Value='#5A8AC8'/>
            </Trigger>
            <Trigger Property='IsDragging' Value='True'>
              <Setter TargetName='th' Property='Background' Value='#6FA0DC'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style x:Key='CpScrollPage' TargetType='RepeatButton'>
    <Setter Property='OverridesDefaultStyle' Value='True'/>
    <Setter Property='Focusable' Value='False'/>
    <Setter Property='IsTabStop' Value='False'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='RepeatButton'>
          <Border Background='Transparent'/>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Background' Value='Transparent'/>
    <Setter Property='Width' Value='11'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ScrollBar'>
          <Grid Background='Transparent'>
            <Border Background='#14FFFFFF' CornerRadius='4' Margin='3'/>
            <Track x:Name='PART_Track' IsDirectionReversed='True'>
              <Track.DecreaseRepeatButton>
                <RepeatButton Style='{StaticResource CpScrollPage}' Command='ScrollBar.PageUpCommand'/>
              </Track.DecreaseRepeatButton>
              <Track.Thumb>
                <Thumb Style='{StaticResource CpScrollThumb}'/>
              </Track.Thumb>
              <Track.IncreaseRepeatButton>
                <RepeatButton Style='{StaticResource CpScrollPage}' Command='ScrollBar.PageDownCommand'/>
              </Track.IncreaseRepeatButton>
            </Track>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property='Orientation' Value='Horizontal'>
              <Setter TargetName='PART_Track' Property='IsDirectionReversed' Value='False'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
    <Style.Triggers>
      <Trigger Property='Orientation' Value='Horizontal'>
        <Setter Property='Width'  Value='Auto'/>
        <Setter Property='Height' Value='11'/>
      </Trigger>
    </Style.Triggers>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='#E6EDF3'/>
    <Setter Property='CaretBrush' Value='#E6EDF3'/>
    <Setter Property='Background' Value='#0F1822'/>
    <Setter Property='BorderBrush' Value='#35485A'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='7,5'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border CornerRadius='5' Background='{TemplateBinding Background}'
                  BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}'/>
          </Border>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>";

    private static void Ensure()
    {
        if (_rd != null) return;
        _rd = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(Xaml);
        ButtonStyle = (Style)_rd["CpButton"];
        _roleItem   = (DataTemplate)_rd["RoleItem"];
    }

    // Тёмные поля ввода (неявный стиль) + доступ к стилю кнопок.
    public static void Apply(Window w)
    {
        Ensure();
        w.Resources.MergedDictionaries.Add(_rd!);
    }

    public static Button Btn(string text) { Ensure(); return new Button { Content = text, Style = ButtonStyle }; }

    // Кастомная тёмная верхушка окна: заголовок + крестик, перетаскивание, рамка.
    public static FrameworkElement Chrome(Window w, string title, FrameworkElement inner)
    {
        w.WindowStyle = WindowStyle.None;
        w.ResizeMode = ResizeMode.NoResize;

        var root = new DockPanel();
        var bar = new Border { Height = 36, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)) };
        bar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) w.DragMove(); };
        var barDock = new DockPanel { LastChildFill = true };

        var close = new Button
        {
            Content = "✕", Width = 44, FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8))
        };
        Ensure();
        close.Style = CloseStyle();
        close.Click += (_, _) => w.Close();
        DockPanel.SetDock(close, Dock.Right);
        barDock.Children.Add(close);
        barDock.Children.Add(new TextBlock
        {
            Text = title, Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(13, 0, 0, 0)
        });
        bar.Child = barDock;
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(inner);

        return new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4A)), BorderThickness = new Thickness(1),
            Child = root
        };
    }

    // Крестик закрытия: прозрачный, красный при наведении.
    private static Style CloseStyle()
    {
        var s = new Style(typeof(Button));
        var t = new ControlTemplate(typeof(Button));
        var b = new System.Windows.FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        b.Name = "cb";
        var cp = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        b.AppendChild(cp);
        t.VisualTree = b;
        var tr = new Trigger { Property = System.Windows.Controls.Control.IsMouseOverProperty, Value = true };
        tr.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xC0, 0x41, 0x3B)), "cb"));
        tr.Setters.Add(new Setter(System.Windows.Controls.Control.ForegroundProperty, Brushes.White));
        t.Triggers.Add(tr);
        s.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, t));
        return s;
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
}
