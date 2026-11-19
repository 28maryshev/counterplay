using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

// Окно пулов — мой винрейт по линиям: дуги пяти ролей над чемпионами и сами
// чемпионы, разложенные по линиям, с иконкой роли в углу портрета.
public sealed partial class PoolSettingsWindow
{
    // Дуга: победы синие, поражения красные — фирменная пара Counter/Play.
    // Красный здесь значит «поражения», а не «выдающийся винрейт», как у
    // цифр (WinrateColor): дуга — счёт игр, цвет процента внутри — по шкале.
    private static readonly Color ArcWin   = Color.FromRgb(0x6F, 0xA0, 0xE8);
    private static readonly Color ArcLoss  = Color.FromRgb(0xE5, 0x58, 0x5C);
    private static readonly Color ArcTrack = Color.FromRgb(0x26, 0x33, 0x42);
    private static readonly Color ArcMuted = Color.FromRgb(0x6A, 0x78, 0x86);

    // Радиус дуги по средней линии и её толщина.
    private const double ArcR = 22, ArcT = 5;

    /// <summary>
    /// Пять плиток в ряд — по одной на линию, всегда все пять: пустая линия
    /// тоже ответ («в лесу не играю»), и ряд не прыгает от периода к периоду.
    /// Плитки делят ширину поровну.
    /// </summary>
    private static UniformGrid RoleGauges(IReadOnlyList<SessionTracker.RoleGame> games)
    {
        // Поля плиток по 3 с каждой стороны, а у ряда −3: крайние плитки встают
        // вровень со слотами чемпионов под ними, а ячейки остаются равными.
        var grid = new UniformGrid { Rows = 1, Columns = Roles.Length, Margin = new Thickness(-3, 0, -3, 10) };
        foreach (var role in Roles)
        {
            var onRole = games.Where(g => g.Role == role).ToList();
            grid.Children.Add(RoleGauge(role, onRole.Count, onRole.Count(g => g.Win)));
        }
        return grid;
    }

    /// Плитка линии: иконка роли, дуга побед и поражений с процентом внутри,
    /// под ней счёт «13В – 17П».
    private static Border RoleGauge(string role, int games, int wins)
    {
        var wr = games > 0 ? 100.0 * wins / games : 0;
        var body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

        if (RoleIcons.Get(DbToLcu[role]) is { } icon)
            body.Children.Add(new Image
            {
                Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 0, 3),
                Opacity = games > 0 ? 0.95 : 0.35
            });

        // Центр окружности — середина нижнего края: видна верхняя половина.
        var w = 2 * ArcR + ArcT;
        var h = ArcR + ArcT / 2;
        var c = new Point(w / 2, h);
        var canvas = new Canvas { Width = w, Height = h };
        if (games == 0) canvas.Children.Add(ArcPath(c, 180, 360, ArcTrack));
        else
        {
            // Победы — слева направо от левого конца, поражения — остаток.
            var split = 180 + 180.0 * wins / games;
            if (wins > 0)     canvas.Children.Add(ArcPath(c, 180, split, ArcWin));
            if (wins < games) canvas.Children.Add(ArcPath(c, split, 360, ArcLoss));
        }
        var arc = new Grid { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center };
        arc.Children.Add(canvas);
        arc.Children.Add(new TextBlock
        {
            Text = games > 0 ? $"{wr:0}%" : "—",
            FontSize = 12, FontWeight = FontWeights.Bold,
            // Та же шкала и та же оговорка про малую выборку, что у слотов.
            Foreground = games > 0 ? WinrateColor.BrushForSample(wr, games) : new SolidColorBrush(ArcMuted),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -2)
        });
        body.Children.Add(arc);

        // Счёт в цвет своих половин дуги. У пустой линии его нет: «0В – 0П»
        // только шумит, а прочерк в дуге уже сказал то же.
        if (games > 0)
        {
            var wl = new TextBlock
            {
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            };
            wl.Inlines.Add(new Run(Loc.T("pool.winsShort", wins)) { Foreground = new SolidColorBrush(ArcWin) });
            wl.Inlines.Add(new Run(" – ") { Foreground = new SolidColorBrush(ArcMuted) });
            wl.Inlines.Add(new Run(Loc.T("pool.lossesShort", games - wins)) { Foreground = new SolidColorBrush(ArcLoss) });
            body.Children.Add(wl);
        }

        var name = Loc.T("roles." + role);
        return new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x4C)),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x16, 0x22, 0x30)),
            Padding = new Thickness(4, 6, 4, 6), Margin = new Thickness(3, 0, 3, 0),
            ToolTip = games > 0
                ? $"{name} · {wr:0}% · {games} {Loc.T("pool.games")}"
                  + (games < SmallSample ? "\n" + Loc.T("pool.fewGames") : "")
                : name,
            Child = body
        };
    }

    // Дуга окружности радиуса ArcR от угла from до to (градусы, по часовой:
    // 180 — левый край, 270 — верх, 360 — правый). Концы плоские: две части
    // дуги сходятся встык, без наплыва скругления.
    private static System.Windows.Shapes.Path ArcPath(Point c, double from, double to, Color color)
    {
        Point At(double deg) => new(
            c.X + ArcR * Math.Cos(deg * Math.PI / 180),
            c.Y + ArcR * Math.Sin(deg * Math.PI / 180));

        var fig = new PathFigure { StartPoint = At(from), IsClosed = false };
        fig.Segments.Add(new ArcSegment(At(to), new Size(ArcR, ArcR), 0, to - from > 180,
                                        SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geo, Stroke = new SolidColorBrush(color), StrokeThickness = ArcT,
            StrokeStartLineCap = PenLineCap.Flat, StrokeEndLineCap = PenLineCap.Flat
        };
    }

    /// <summary>
    /// Чемпионы по линиям: группа на линию в порядке линий, каждая с новой
    /// строки; внутри — больше игр выше, при равенстве — сыгранный позже.
    /// Чемпион, которого играли на двух линиях, — два слота, у каждого своя
    /// роль и свой счёт: Шен-саппорт и Шен-топ — разные истории.
    /// Игры, роль которых не нашлась (база ещё не скачана), — последней
    /// группой, без иконки роли.
    /// </summary>
    private static StackPanel ChampsByRole(IReadOnlyList<SessionTracker.RoleGame> games)
    {
        var panel = new StackPanel();
        string RoleOf(SessionTracker.RoleGame g) => Roles.Contains(g.Role) ? g.Role : "";
        foreach (var role in Roles.Append(""))
        {
            var champs = games.Where(g => RoleOf(g) == role)
                .GroupBy(g => g.ChampionId)
                .OrderByDescending(x => x.Count())
                .ThenByDescending(x => x.Max(g => g.Ts))
                .ToList();
            if (champs.Count == 0) continue;

            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            foreach (var x in champs) row.Children.Add(ChampRoleSlot(x.Key, role, [.. x]));
            panel.Children.Add(row);
        }
        return panel;
    }

    /// Слот «чемпион на линии»: портрет с иконкой роли в левом верхнем углу,
    /// под ним винрейт; рамка — цвет этого винрейта, как и раньше.
    private static Border ChampRoleSlot(int id, string role, List<SessionTracker.RoleGame> games)
    {
        (int Games, int Wins) Tally(string[] queues)
        {
            var q = games.Where(g => queues.Contains(g.Queue)).ToList();
            return (q.Count, q.Count(g => g.Win));
        }
        var r = Tally(SessionTracker.QueuesRanked);
        var n = Tally(SessionTracker.QueuesNormal);
        var gAll  = r.Games + n.Games;
        var wrAll = gAll > 0 ? 100.0 * (r.Wins + n.Wins) / gAll : 50.0;
        var frame = WinrateColor.BrushForSample(wrAll, gAll);

        var face = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center };
        if (IconCache.Get(id) is { } src)
            face.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = new ImageBrush { ImageSource = src, Stretch = Stretch.UniformToFill }
            });
        if (role.Length > 0 && RoleIcons.Get(DbToLcu[role]) is { } roleIcon)
            face.Children.Add(new Border
            {
                // Тёмная подложка: светлая иконка роли иначе тонет в светлом портрете.
                Width = 16, Height = 16, CornerRadius = new CornerRadius(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x0E, 0x14, 0x1D)),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = new Image { Source = roleIcon, Width = 12, Height = 12 }
            });

        var inner = new StackPanel { Margin = new Thickness(4, 4, 4, 3) };
        inner.Children.Add(face);
        if (PoolEditorWindow.WrChip(r, n) is { } chip)
        {
            chip.Margin = new Thickness(0, 3, 0, 0);
            inner.Children.Add(chip);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = frame,
            // Подложка того же цвета, но почти прозрачная — слот слегка
            // «подсвечен» результатом, не перетягивая внимание с портрета.
            Background = WinrateColor.TintForSample(wrAll, gAll),
            Margin = new Thickness(0, 0, 8, 8),
            ToolTip = role.Length > 0 ? $"{DataDragon.Name(id)} · {Loc.T("roles." + role)}" : DataDragon.Name(id),
            Child = inner
        };
    }
}
