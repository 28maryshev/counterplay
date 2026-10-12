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
// Окно пулов — каркас: области обычных и дуо-пулов, выбор, правка и удаление.
// Винрейты, плитки и мелкие элементы — в соседних PoolSettingsWindow.*.cs.
public sealed partial class PoolSettingsWindow : Window
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
        MinWidth = 700; MinHeight = 460;
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

        // Низ окна: «?» с инструкцией, черта, а под ней — мой винрейт по
        // чемпионам. Пулы занимают верх, здесь остаётся пустое место — показываем
        // в нём, на ком реально идёт игра. Подсказка выше винрейтов: она про то,
        // ради чего окно открыли, а винрейты — справка на посмотреть.
        var bottom = new DockPanel { Margin = new Thickness(16, 0, 16, 10) };
        // «?» слева, пароль синхронизации справа — одной строкой: оба про то,
        // «как этим пользоваться», а не про сами пулы.
        var helpRow = new DockPanel();
        _syncBox = SyncBadge.Row(() => AskSyncPassword(this));
        DockPanel.SetDock(_syncBox, Dock.Right);
        helpRow.Children.Add(_syncBox);
        helpRow.Children.Add(HelpBadge());
        var help = helpRow;
        DockPanel.SetDock(help, Dock.Top);
        bottom.Children.Add(help);
        var hr = new Border
        {
            Height = 1, Background = new SolidColorBrush(Line), Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(hr, Dock.Top);
        bottom.Children.Add(hr);
        bottom.Children.Add(_wrStrip);

        // Две части с подвижной границей: сверху пулы, снизу винрейты. Доли
        // были жёсткими, и на низком окне винрейты почти не помещались.
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(AppSettings.Current.PoolSplit, GridUnitType.Star),
            MinHeight = 120
        });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(1 - AppSettings.Current.PoolSplit, GridUnitType.Star),
            MinHeight = 120
        });

        Grid.SetRow(grid, 0);
        layout.Children.Add(grid);

        // Сама полоса: широкая настолько, чтобы в неё попадали мышью, но
        // видимая часть тонкая — толстая черта посреди окна мозолила бы глаз.
        var splitter = new GridSplitter
        {
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeNS,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ToolTip = Loc.T("pool.splitHint"),
        };
        Grid.SetRow(splitter, 1);
        layout.Children.Add(splitter);

        var splitLine = new Border
        {
            Height = 1, Margin = new Thickness(16, 4, 16, 4),
            Background = new SolidColorBrush(Line),
            IsHitTestVisible = false,        // тянут полосу, а не черту
        };
        Grid.SetRow(splitLine, 1);
        layout.Children.Add(splitLine);

        Grid.SetRow(bottom, 2);
        layout.Children.Add(bottom);

        // Запоминаем, где человек оставил границу.
        Closing += (_, _) =>
        {
            var top = layout.RowDefinitions[0].ActualHeight;
            var all = top + layout.RowDefinitions[2].ActualHeight;
            if (all > 0)
                AppSettings.Current.PoolSplit = Math.Clamp(top / all, 0.25, 0.85);
            AppSettings.SaveQuiet();
        };

        // Декоративный фон как на драфте: синий сверху-слева → красный снизу-справа.
        var decorated = new Grid();
        decorated.Children.Add(DecorBackground());
        decorated.Children.Add(layout);

        var chrome = PoolUi.Chrome(this, Title, decorated, resizable: true);
        Content = chrome;
        // Высота на треть больше прежней (560): обе половины винрейтов ютились
        // в паре рядов. Место и размер запоминаются, как у окна подбора.
        WindowScale.Apply(this, chrome, 820, 750, 700, 460, new WindowPlace(
            () => (AppSettings.Current.PoolWinLeft, AppSettings.Current.PoolWinTop,
                   AppSettings.Current.PoolWinWidth, AppSettings.Current.PoolWinHeight),
            (l, t, w, h) =>
            {
                AppSettings.Current.PoolWinLeft = l;
                AppSettings.Current.PoolWinTop = t;
                AppSettings.Current.PoolWinWidth = w;
                AppSettings.Current.PoolWinHeight = h;
                AppSettings.SaveQuiet();
            }));
        Refresh();
    }

    private FrameworkElement Area(string title, WrapPanel area, bool duo, int col)
    {
        var grid = new Grid();

        // Название половины — крупным полупрозрачным «водяным знаком» по центру,
        // за тайлами. Текст из Loc.T → меняется вместе с языком (окно пересоздаётся).
        grid.Children.Add(new TextBlock
        {
            Text = title, Foreground = new SolidColorBrush(Color.FromArgb(0x1C, 0xC9, 0xD2, 0xDC)),
            FontWeight = FontWeights.Bold, FontSize = 64,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        });

        grid.Children.Add(new ScrollViewer
        {
            Content = area, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        Grid.SetColumn(grid, col);
        return grid;
    }

    /// Перечитать списки (пул мог приехать файлом, пока окно открыто).
    public void ReloadPools() => Refresh();

    private void Refresh()
    {
        RefreshWinrates();
        var a = PoolStore.Current();
        _poolArea.Children.Clear();
        foreach (var p in a.Pools)
        {
            var champs = p.ByRole.Values.SelectMany(l => l).Where(id => id != 0).Distinct().ToList();
            _poolArea.Children.Add(Tile(p.Name, a.FavPoolId == p.Id,
                () => EditPool(p), () => DeletePool(p), () => Select(PoolKind.Pool, p.Id), champs));
        }
        _poolArea.Children.Add(PlusTile(() => EditPool(null)));
        _poolArea.Children.Add(ImportTile(intoDuo: false));

        _duoArea.Children.Clear();
        foreach (var d in a.DuoPools)
        {
            // Мозаика — по чемпионам АКТИВНОГО режима пула: ручной → фикс-связки,
            // авто → наборы по ролям. Иначе в ручном лезут остатки авто-наборов.
            var champs = (d.Manual
                    ? d.ManualPairs.SelectMany(mp => new[] { mp.Mine, mp.Friend })
                    : d.Mine.Values.SelectMany(l => l).Concat(d.Friend.Values.SelectMany(l => l)))
                .Where(id => id != 0).Distinct().ToList();
            // Ник берём тем же путём, что и заголовок раздела: из файла, иначе
            // из связок. Называть некого — строки просто не будет.
            var nick = DuoNaming.TileNick(d, SessionTracker.TopPairs(d.FriendPuuid, 1));
            var duo = d;   // замыкание в цикле
            _duoArea.Children.Add(Tile(d.FriendName, a.FavDuoId == d.Id,
                () => EditDuo(d), () => DeleteDuo(d), () => Select(PoolKind.Duo, d.Id), champs,
                Loc.T(d.Manual ? "pool.duoManual" : "pool.duoAuto"), nick,
                () => PickMate(duo)));
        }
        _duoArea.Children.Add(PlusTile(() => EditDuo(null)));
        _duoArea.Children.Add(ImportTile(intoDuo: true));
    }

    // Звёздочка — избранное в СВОЕЙ половине. Личный пул и дуо могут быть
    // отмечены одновременно; какой из них в деле, решает кнопка в сайдбаре.
    // Повторный клик по отмеченному снимает отметку.
    private void Select(PoolKind kind, string id)
    {
        PoolStore.SetFavourite(kind, id);
        _onChange();
        Refresh();
    }

    private void EditPool(ChampPool? existing) => OpenEditor(new PoolEditorWindow(existing, duo: false, _engine));

    private void EditDuo(DuoPool? existing)     => OpenEditor(new PoolEditorWindow(existing, duo: true,  _engine));

    // Редактор открывается НЕ модально — иначе настройки под ним нельзя даже
    // отодвинуть в сторону. Открыт один за раз: два окна на один и тот же пул
    // писали бы поверх друг друга.
    private PoolEditorWindow? _editor;

    private void OpenEditor(PoolEditorWindow win)
    {
        if (_editor is { IsVisible: true }) { _editor.Activate(); return; }
        _editor = win;
        win.Owner = this;
        win.Closed += (_, _) =>
        {
            if (win.Saved) _onChange();
            if (IsVisible) Refresh();   // закрылись вместе с настройками — обновлять нечего
        };
        win.Show();
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
