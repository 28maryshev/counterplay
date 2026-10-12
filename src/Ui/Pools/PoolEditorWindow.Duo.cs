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

// Редактор пула — дуо-пул: переключатель режима, пары вручную, статистика пар, напарник.
public sealed partial class PoolEditorWindow
{
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
            : PlusChamp(() => OpenPicker(
                  new ChampionPickerWindow(_names, _idByName, Array.Empty<int>(), _engine),
                  pick => { if (pick.Result > 0) setChamp(pick.Result); }), DuoSlot, zero);
        wrap.Children.Add(tile);
        if (id != 0 && setRole != null) wrap.Children.Add(RoleCombo(role, setRole));
        return wrap;
    }

    /// <summary>
    /// Кнопка «кто это» рядом с ником: выбор напарника из тех, с кем правда
    /// играли вдвоём. Ник при этом подставляется сам — его можно потом
    /// поправить руками, подпись и человек живут отдельно.
    /// </summary>
    /// <summary>
    /// Строка «Ник напарника»: плашка с именем — она же выбор из списка — и
    /// «Сбросить» рядом.
    ///
    /// Поля ввода тут нет нарочно. Ник — не подпись, которую придумывают, а имя
    /// конкретного человека: пул следует за ним по puuid, по нему же считаются
    /// винрейты связки. Вписанный руками ник ни с кем не связан и только путает.
    /// </summary>
    private FrameworkElement MateRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 14),
        };
        row.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.friendNick"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            FontSize = 10, FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });

        var holder = new Border { VerticalAlignment = VerticalAlignment.Center };
        var clear  = PoolUi.Btn(Loc.T("pool.mateReset"));
        clear.Margin = new Thickness(10, 0, 0, 0);
        clear.ToolTip = Loc.T("pool.mateForget");
        // Ростом с плашку: у обычной кнопки отступы крупнее, и рядом с плашкой
        // она выглядела башней. Высота складывается одинаково — рамка 1+1,
        // отступы 1+2 и тот же кегль.
        clear.FontSize = 10;
        clear.FontWeight = FontWeights.Bold;
        clear.Padding = new Thickness(10, 1, 10, 2);
        clear.VerticalAlignment = VerticalAlignment.Center;
        clear.Tag = "mate-reset";

        void Draw()
        {
            var who  = PoolSettingsWindow.MateList().FirstOrDefault(m => m.Puuid == _matePuuid);
            // Ник из файла обмена оставляем видимым, даже если такого человека
            // нет в списке: пул приехал от друга, и назвать его всё равно есть чем.
            var text = who.Name is { Length: > 0 } n ? n
                     : _nick.Length > 0 ? _nick
                     : _matePuuid.Length > 0 ? _matePuuid[..Math.Min(8, _matePuuid.Length)]
                     : Loc.T("pool.mateUnknown");

            holder.Child = PoolSettingsWindow.MateBadge(
                text, _matePuuid.Length > 0 || _nick.Length > 0, () => PickMate(holder, Draw));
            clear.Visibility = _matePuuid.Length > 0 || _nick.Length > 0
                ? Visibility.Visible : Visibility.Collapsed;
        }

        clear.Click += (_, _) =>
        {
            _matePuuid   = "";
            _nick        = "";
            _mateCleared = true;   // стираем хозяина ТОЛЬКО по этой кнопке
            _dirty = true;
            Draw();
        };

        Draw();
        row.Children.Add(holder);
        row.Children.Add(clear);
        return row;
    }

    /// Список тех, с кем стояли в очереди соло/дуо. «Забыть» здесь нет —
    /// для этого рядом стоит своя кнопка.
    private void PickMate(UIElement under, Action redraw)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = under,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        foreach (var (puuid, nick, games) in PoolSettingsWindow.MateList().Take(12))
        {
            var name = nick.Length > 0 ? nick : puuid[..Math.Min(8, puuid.Length)];
            var id = puuid; var shown = nick;
            menu.Items.Add(PoolSettingsWindow.MateRow($"{name} · {games}", puuid == _matePuuid, () =>
            {
                _matePuuid   = id;
                _nick        = shown;
                _mateCleared = false;
                _dirty = true;
                redraw();
            }));
        }
        if (menu.Items.Count == 0)
            menu.Items.Add(PoolSettingsWindow.MateRow(Loc.T("pool.mateNone"), false, () => { }));
        menu.IsOpen = true;
    }
}
