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

// Редактор пула — строки ролей: выбор роли, чемпионы с винрейтом, «+ чемпион».
public sealed partial class PoolEditorWindow
{
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
                chips.Children.Add(ChampWithWr(id, () => { list.Remove(id); _dirty = true; RefreshChips(); }));
            // Квадратная «+» того же размера, что иконка — не пропадает.
            // Пул набирают пачкой, поэтому здесь выбор множественный.
            chips.Children.Add(PlusChamp(() => OpenPicker(
                new ChampionPickerWindow(_names, _idByName, list, _engine, multi: true),
                pick =>
                {
                    var added = 0;
                    foreach (var id in pick.Results)
                        if (!list.Contains(id)) { list.Add(id); added++; }
                    // Перерисовываем всё тело, а не одну строку: пока окно выбора
                    // было открыто, редактор могли перерисовать, и та строка уже
                    // не та, что на экране.
                    if (added > 0) { _dirty = true; RenderBody(); }
                })));
        }
        RefreshChips();
        return row;
    }

    // Иконка чемпиона + МОЙ винрейт на нём: ранкед (соло+флекс) и нормалы.
    // ARAM не считаем — там пик случайный. Нет своих игр — строка не рисуется.
    private static FrameworkElement ChampWithWr(int id, Action remove)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 0, 5, 5) };
        wrap.Children.Add(ChampIcon(id, remove, 48, new Thickness(0)));

        if (WrChip(id) is { } chip)
        {
            chip.Margin = new Thickness(0, 2, 0, 0);
            wrap.Children.Add(chip);
        }
        return wrap;
    }

    /// Винрейт чемпиона одной цифрой — ранкед и нормалы ВМЕСТЕ (так виднее общая
    /// форма), а разбивка по очередям показывается при наведении.
    /// Возвращает null, если своих игр на чемпионе нет.
    internal static FrameworkElement? WrChip(int championId) => WrChip(
        SessionTracker.ChampStats(championId, SessionTracker.QueuesRanked),
        SessionTracker.ChampStats(championId, SessionTracker.QueuesNormal));

    /// То же по уже посчитанным числам. Нижняя часть окна пулов красит рамку
    /// слота по выбранному периоду, и подпись обязана стоять на тех же числах:
    /// когда подпись считала месяц, а рамка всё время, «25% / 4» сидело в
    /// оранжевой рамке.
    internal static FrameworkElement? WrChip((int Games, int Wins) ranked, (int Games, int Wins) normal)
    {
        var (rg, rw) = ranked;
        var (ng, nw) = normal;
        var games = rg + ng;
        if (games == 0) return null;

        var wr = 100.0 * (rw + nw) / games;
        var tip = new List<string>();
        if (rg > 0) tip.Add(Loc.T("pool.wrRanked", $"{100.0 * rw / rg:F1}", rg));
        if (ng > 0) tip.Add(Loc.T("pool.wrNormal", $"{100.0 * nw / ng:F1}", ng));

        return new TextBlock
        {
            // «57% / 12» — винрейт и через слэш число сыгранных игр за окно.
            Text = $"{wr:F0}% / {games}", FontSize = 10, FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            // Общая шкала винрейта (WinrateColor); на 1–4 играх — нейтральный серый.
            Foreground = WinrateColor.BrushForSample(wr, games),
            ToolTip = string.Join(Environment.NewLine, tip)
        };
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
}
