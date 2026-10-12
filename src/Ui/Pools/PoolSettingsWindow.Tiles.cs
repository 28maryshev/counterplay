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

// Окно пулов — плитки пулов, плитка импорта, напарник на плитке дуо-пула.
public sealed partial class PoolSettingsWindow
{
    // Плитка загрузки пула из файла. Стоит в обеих половинах, и половина важна:
    // ЛИЧНЫЙ пул друга, загруженный в «Дуо», становится ЕГО половиной пары, а
    // свою мы берём из уже собранного своего пула. Готовый дуо-файл ложится
    // дуо-пулом всегда — что бы ни нажали.
    private FrameworkElement ImportTile(bool intoDuo)
    {
        var b = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
            // Золото — в пару к кнопке «Экспорт» в редакторе: обмен пулами
            // читается как одно дело и не теряется рядом с синей «+».
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xC8, 0xAA, 0x6E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xAA, 0x6E)), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Loc.T("pool.importFile"),
        };
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock
        {
            Text = "⭳", FontSize = 26, FontFamily = PoolUi.SymbolFont,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xAA, 0x6E)),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        sp.Children.Add(new TextBlock
        {
            Text = Loc.T("pool.importFile"), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(6, 2, 6, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xD6, 0xA8))
        });
        b.Child = sp;
        b.MouseLeftButtonUp += (_, _) => ImportFromFile(intoDuo);
        return b;
    }

    private void ImportFromFile(bool intoDuo)
    {
        if (PoolShare.Receive(this, intoDuo)) { _onChange(); Refresh(); }
    }

    // Плитка существующего пула: имя + клик (редактировать) + × (удалить) + ★ выбора.
    // Активный (выбранный сейчас) пул выделен синей рамкой и залитой звездой.
    /// Список напарников — правило живёт в DuoNaming, чтобы его можно было
    /// померить проверкой без окна.
    internal static List<(string Puuid, string Name, int Games)> MateList() => DuoNaming.Mates();

    /// <summary>
    /// Плашка «с кем этот пул» — она же кнопка.
    ///
    /// Одна на плитку и на редактор: человек там один и тот же, и выглядеть
    /// по-разному они не должны. Под наведением плашка заметно светлеет и
    /// обводится золотом — иначе по серому прямоугольнику не видно, что он
    /// нажимается, а владелец на это и указал.
    ///
    /// Раскладку (ширину, поля, выравнивание) ставит вызывающий: на плитке она
    /// прижата к низу, в редакторе стоит в строке рядом с ником.
    /// </summary>
    internal static Border MateBadge(string text, bool known, Action? onClick)
    {
        // Серая плашка, а не цветной текст: подпись «с кем» не должна перебивать
        // название пула и спорить с золотой рамкой отмеченной плитки.
        var calm      = Color.FromArgb(0xB8, 0x18, 0x21, 0x2B);
        var calmEdge  = Color.FromRgb(0x3A, 0x48, 0x58);
        var calmText  = known ? Color.FromRgb(0xA8, 0xB6, 0xC4) : Color.FromRgb(0x6A, 0x7C, 0x8C);
        var hotFill   = Color.FromRgb(0x7A, 0x8C, 0x9E);
        var hotEdge   = Color.FromRgb(0xC8, 0x9B, 0x3C);
        var hotText   = Color.FromRgb(0xFF, 0xFF, 0xFF);

        var label = new TextBlock
        {
            Text = text,
            FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(calmText),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
        };
        var badge = new Border
        {
            // Метка для проверки: плашку надо найти в дереве окна и померить,
            // где она оказалась, — один раз её уже срезало краем окна.
            Tag = "mate-badge",
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(calm),
            BorderBrush = new SolidColorBrush(calmEdge),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 1, 6, 2),
            Child = label,
        };

        if (onClick is null) return badge;

        badge.Cursor = System.Windows.Input.Cursors.Hand;
        badge.ToolTip = Loc.T("pool.mateChange") + Environment.NewLine + Loc.T("pool.mateWhy");
        badge.MouseEnter += (_, _) =>
        {
            badge.Background  = new SolidColorBrush(hotFill);
            badge.BorderBrush = new SolidColorBrush(hotEdge);
            label.Foreground  = new SolidColorBrush(hotText);
        };
        badge.MouseLeave += (_, _) =>
        {
            badge.Background  = new SolidColorBrush(calm);
            badge.BorderBrush = new SolidColorBrush(calmEdge);
            label.Foreground  = new SolidColorBrush(calmText);
        };
        badge.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return badge;
    }

    /// Пункт меню в фирменном виде (оформление — в src/Ui/Theme.xaml).
    internal static MenuItem MateRow(string head, bool chosen, Action act)
    {
        var mi = new MenuItem { Header = head, IsCheckable = false, IsChecked = chosen };
        mi.Click += (_, _) => act();
        return mi;
    }

    /// <summary>
    /// Выбор напарника для дуо-пула.
    ///
    /// Хозяин пула проставляется сам — первым, с кем окажешься в пати. Правило
    /// разумное, пока оно угадывает; когда промахивается, исправить было
    /// нечем. Владелец это и поймал: пул «Harribon DUO» достался человеку, с
    /// которым он играл в тот день, и вернуть прежнего можно было только
    /// удалив пул и собрав заново.
    ///
    /// Список — те, с кем правда играли вдвоём, по числу совместных игр из
    /// истории связок. Выдумывать некого: кто не попадался в играх, тот и в
    /// пуле не нужен.
    /// </summary>
    private void PickMate(DuoPool d)
    {
        var mates = MateList();

        // Вид не задаём: меню оформлено приложением (src/Ui/Theme.xaml).
        var menu = new ContextMenu();

        foreach (var (puuid, nick, games) in mates.Take(12))
        {
            var name = nick.Length > 0 ? nick : puuid[..Math.Min(8, puuid.Length)];
            var id = puuid;
            menu.Items.Add(MateRow($"{name} · {games}", puuid == d.FriendPuuid, () =>
            {
                d.FriendPuuid = id;
                d.FriendNick  = nick;
                PoolStore.Persist();
                Log.Write($"дуо «{d.FriendName}»: напарник выбран вручную — {name}");
                Refresh();
            }));
        }

        if (mates.Count == 0)
            menu.Items.Add(MateRow(Loc.T("pool.mateNone"), false, () => { }));

        // «Забыть» — чтобы вернуть прежнее поведение: следующий напарник по
        // пати снова займёт место сам.
        if (d.FriendPuuid.Length > 0 || d.FriendNick.Length > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MateRow(Loc.T("pool.mateForget"), false, () =>
            {
                d.FriendPuuid = "";
                d.FriendNick  = "";
                PoolStore.Persist();
                Log.Write($"дуо «{d.FriendName}»: напарник забыт");
                Refresh();
            }));
        }

        menu.IsOpen = true;
    }

    private FrameworkElement Tile(string name, bool active, Action open, Action del, Action select,
                                  IReadOnlyList<int>? champs = null, string mode = "", string nick = "",
                                  Action? pickMate = null)
    {
        var b = new Border
        {
            Width = 118, Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 10, 10),
            Background = new SolidColorBrush(active ? Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C) : Color.FromRgb(0x16, 0x20, 0x2C)),
            // Активная (выбранная) плитка — золотая рамка, в цвет звезды.
            BorderBrush = new SolidColorBrush(active ? Color.FromRgb(0xC8, 0x9B, 0x3C) : Color.FromRgb(0x30, 0x42, 0x54)),
            BorderThickness = new Thickness(active ? 2 : 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            // Режим подбора ушёл с плитки целиком — живёт здесь.
            ToolTip = mode.Length > 0 ? $"{name}\n{mode}" : null,
            ClipToBounds = true   // фон-мозаика не вылезает за скруглённые углы
        };
        var g = new Grid();

        // Фон: полупрозрачная мозаика иконок чемпионов пула — чтобы плитка не пустовала.
        if (champs is { Count: > 0 })
            g.Children.Add(PoolIconWash(champs));

        // Затемняющая вуаль поверх мозаики — чтобы имя читалось.
        g.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x66, 0x0E, 0x14, 0x1D)) });

        g.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(name) ? "—" : name, Foreground = Brushes.White,
            FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            // Внизу стоят пометка режима и ник — оставляем им место, чтобы
            // название не наезжало.
            Margin = new Thickness(6, 6, 6, nick.Length > 0 ? 20 : 6),
            // Тень — имя выделяется на мозаике.
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.9 }
        });
        // Нижняя строка плитки: если знаем, С КЕМ пул, — стоит ник. Он важнее
        // способа подбора: название склеено из половин и о человеке молчит, а
        // режим никуда не девается — он в подсказке к плитке.
        // Только ник. Режим подбора — настройка, а не подпись: строка под
        // названием полезнее занята тем, С КЕМ этот пул. Режим не теряется —
        // он в подсказке к плитке.
        // Плашка с ником — она же кнопка «с кем этот пул». Хозяин проставляется
        // сам по первому напарнику в пати, и если это оказался не тот человек,
        // поменять его было НЕЧЕМ: ни кнопки, ни сброса. Владелец на это и
        // наткнулся — пул «Harribon DUO» достался тому, с кем он играл сегодня.
        if (nick.Length > 0 || pickMate is not null)
        {
            var badge = MateBadge(nick.Length > 0 ? nick : Loc.T("pool.mateUnknown"),
                                  nick.Length > 0, pickMate);
            badge.MaxWidth = 104;
            badge.HorizontalAlignment = HorizontalAlignment.Center;
            badge.VerticalAlignment = VerticalAlignment.Bottom;
            badge.Margin = new Thickness(4, 0, 4, 6);
            g.Children.Add(badge);
        }
        // Звезда выбора — левый верхний угол.
        var star = new Button
        {
            Content = active ? "★" : "☆", Width = 30, Height = 30, FontSize = 20,
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

    // Полупрозрачная мозаика иконок чемпионов пула — фон плитки. Иконки
    // повторяются по сетке и ПОЛНОСТЬЮ закрывают плитку (без пустых мест).
    private static FrameworkElement PoolIconWash(IReadOnlyList<int> champs)
    {
        const int cols = 3, rows = 3;
        var grid = new System.Windows.Controls.Primitives.UniformGrid
        { Columns = cols, Rows = rows, Opacity = 0.13 };
        var withIcon = champs.Where(id => IconCache.Get(id) != null).ToList();
        if (withIcon.Count == 0) return grid;
        for (int i = 0; i < cols * rows; i++)
            grid.Children.Add(new Image
            { Source = IconCache.Get(withIcon[i % withIcon.Count]), Stretch = Stretch.UniformToFill });
        return grid;
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
}
