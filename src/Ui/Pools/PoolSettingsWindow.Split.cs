using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

// Окно пулов — средняя полоса между пулами и винрейтами: строка «?» и пароля
// синхронизации, которую тянут вверх-вниз, и ручка по центру.
public sealed partial class PoolSettingsWindow
{
    // Ручка в покое и под мышью: бледная, чтобы не спорить с пулами, и синяя,
    // когда полосу можно взять.
    private static readonly Color GripIdle = Color.FromArgb(0x90, 0x8A, 0xA0, 0xB2);

    private static readonly Color GripHot  = Color.FromRgb(0x5A, 0x8A, 0xC8);

    /// <summary>
    /// Средняя полоса в строке <paramref name="row"/> сетки <paramref name="layout"/>:
    /// черта, «? Как это работает» слева и пароль справа, снова черта.
    ///
    /// Полоса тянется за ЛЮБОЕ пустое место. Раньше тянулась только черта в
    /// 9 точек над строкой, а сама строка лежала в нижней части — человек
    /// хватался за середину, и ничего не происходило (владелец, 19.11).
    /// Устроено так: GridSplitter растянут на всю строку и лежит ПОД
    /// содержимым. У панелей строки фона нет, поэтому мышь на пустом месте
    /// проваливается к нему, а «?» и кнопки пароля ловят её сами. Свой
    /// обработчик перетаскивания не нужен, и курсор над кнопками свой.
    /// </summary>
    private void AddMiddleBand(Grid layout, int row)
    {
        var splitter = new GridSplitter
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Оба выравнивания Stretch — направление само не угадывается.
            ResizeDirection = GridResizeDirection.Rows,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeNS,
            ToolTip = Loc.T("pool.splitHint"),
            Focusable = false,
        };
        Grid.SetRow(splitter, row);
        layout.Children.Add(splitter);

        var band = new DockPanel { Margin = new Thickness(16, 0, 16, 0) };

        var topLine = new Border
        {
            Height = 1, Margin = new Thickness(0, 9, 0, 9),
            Background = new SolidColorBrush(Line), IsHitTestVisible = false,
        };
        DockPanel.SetDock(topLine, Dock.Top);
        band.Children.Add(topLine);

        var bottomLine = new Border
        {
            Height = 1, Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Line), IsHitTestVisible = false,
        };
        DockPanel.SetDock(bottomLine, Dock.Bottom);
        band.Children.Add(bottomLine);

        // «?» слева, пароль синхронизации справа — одной строкой: оба про то,
        // «как этим пользоваться», а не про сами пулы.
        var helpRow = new DockPanel();
        _syncBox = SyncBadge.Row(() => AskSyncPassword(this));
        DockPanel.SetDock(_syncBox, Dock.Right);
        helpRow.Children.Add(_syncBox);
        helpRow.Children.Add(HelpBadge());
        band.Children.Add(helpRow);

        Grid.SetRow(band, row);
        layout.Children.Add(band);

        // Ручка — на верхней черте по центру: знак, что полосу можно тянуть.
        var grip = Grip();
        Grid.SetRow(grip, row);
        layout.Children.Add(grip);

        splitter.MouseEnter += (_, _) => Paint(grip, GripHot);
        splitter.MouseLeave += (_, _) => { if (!splitter.IsDragging) Paint(grip, GripIdle); };
        splitter.DragCompleted += (_, _) => { if (!splitter.IsMouseOver) Paint(grip, GripIdle); };

        // Где человек оставил границу — сразу, как отпустил полосу, и ещё раз
        // при закрытии. Только при закрытии мало: программа кончается и при
        // открытом окне, и граница уезжала обратно (владелец, 19.11).
        splitter.DragCompleted += (_, _) => SaveSplit(layout, row);
        Closing += (_, _) => SaveSplit(layout, row);
    }

    // Доля верхней части от двух подвижных — соседей полосы сверху и снизу.
    // Берём звёздные веса, а не ActualHeight: полоса ставит их сразу, а
    // высоты пересчитываются только на следующем проходе раскладки — в миг
    // отпускания они ещё прежние, и записалась бы старая граница.
    private static void SaveSplit(Grid layout, int row)
    {
        var a = layout.RowDefinitions[row - 1];
        var b = layout.RowDefinitions[row + 1];
        var (top, all) = a.Height.IsStar && b.Height.IsStar
            ? (a.Height.Value, a.Height.Value + b.Height.Value)
            : (a.ActualHeight, a.ActualHeight + b.ActualHeight);
        if (all <= 0) return;
        AppSettings.Current.PoolSplit = Math.Clamp(top / all, 0.25, 0.85);
        AppSettings.SaveQuiet();
    }

    // Плашка со стрелками вверх и вниз: «тянуть сюда или туда». Мышь не
    // ловит — её ловит полоса под ней.
    private static Border Grip()
    {
        var arrows = new System.Windows.Shapes.Path
        {
            // Две галочки: ^ сверху, v снизу.
            Data = Geometry.Parse("M 0,4 L 4,0 L 8,4 M 0,8 L 4,12 L 8,8"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        var grip = new Border
        {
            Width = 34, Height = 18, CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Bg),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0.5, 0, 0),   // центр плашки — на верхней черте
            IsHitTestVisible = false,
            Child = arrows,
        };
        Paint(grip, GripIdle);
        return grip;
    }

    private static void Paint(Border grip, Color c)
    {
        var brush = new SolidColorBrush(c);
        grip.BorderBrush = brush;
        if (grip.Child is System.Windows.Shapes.Path p) p.Stroke = brush;
    }
}
