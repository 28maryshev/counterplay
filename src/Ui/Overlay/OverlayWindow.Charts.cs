using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — графики профиля: рейтинг и винрейт по дням.
public partial class OverlayWindow
{
    /// График движения ранга за месяц. Ось Y — абсолютный LP, поэтому дивизионы
    /// ложатся ровными полосами по 100 очков, а тир задаёт цвет фона: видно не
    /// только «вверх/вниз», но и где именно ты идёшь и близко ли граница.
    private void DrawRatingChart()
    {
        WrChart.Children.Clear();
        IReadOnlyList<SessionTracker.LpPoint> pts = _sessionView?.RatingHistory ?? [];
        // Окно показа из настроек — то же самое, что и у графика винрейта.
        var since = DateTime.Now.AddDays(-Math.Max(1, AppSettings.Current.ChartDays));
        if (pts.Count > 0 && pts.Any(p => p.Date >= since))
            pts = pts.Where(p => p.Date >= since).ToList();

        double w = WrChart.ActualWidth > 4 ? WrChart.ActualWidth : 150;
        double h = WrChart.ActualHeight > 4 ? WrChart.ActualHeight : 58;

        if (pts.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = Loc.T("chart.noRating"), FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 10, Foreground = MuteBrush, TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(60, w - 26)
            };
            Canvas.SetLeft(hint, 24);
            Canvas.SetTop(hint, h / 2 - 14);
            WrChart.Children.Add(hint);
            return;
        }

        // Границы — по данным, но выровненные на дивизион (100 LP), чтобы полосы
        // не обрезались посередине.
        int lo = pts.Min(p => p.AbsLp), hi = pts.Max(p => p.AbsLp);
        if (hi - lo < 60) { int m = (lo + hi) / 2; lo = m - 30; hi = m + 30; }
        int min = (int)(Math.Floor((lo - 12) / 100.0) * 100);
        int max = (int)(Math.Ceiling((hi + 12) / 100.0) * 100);
        if (max <= min) max = min + 100;

        const double axisW = 26;
        double padY = 4, bottomPad = 12;
        double plotL = axisW, plotR = w;
        double chartH = h - padY - bottomPad;
        double baseY = padY + chartH;
        double Y(double lp) => padY + (1 - (lp - min) / (double)(max - min)) * chartH;
        double X(int i) => pts.Count == 1 ? (plotL + plotR) / 2
            : plotL + (double)i / (pts.Count - 1) * (plotR - plotL - 2) + 1;

        // Полосы тиров: если за месяц пересёк границу тира, фон делится на два
        // цвета — сразу видно, где закончилось золото и началась платина.
        for (int band = min; band < max; band += 100)
        {
            var (_, color) = BandAt(band);
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, plotR - plotL),
                Height = Math.Max(1, Y(band) - Y(band + 100)),
                Fill = new SolidColorBrush(color) { Opacity = 0.20 }
            };
            Canvas.SetLeft(rect, plotL);
            Canvas.SetTop(rect, Y(band + 100));
            WrChart.Children.Add(rect);
        }

        // Разделители: дивизионы — пунктиром, граница тира — сплошной и ярче.
        for (int line = min; line <= max; line += 100)
        {
            var tierEdge = line % 400 == 0;
            var sep = new System.Windows.Shapes.Line
            {
                X1 = plotL, X2 = plotR, Y1 = Y(line), Y2 = Y(line),
                // Пунктир дивизионов — почти незаметный: он размечает шкалу, а не
                // спорит с линией графика. Граница тира заметно ярче.
                Stroke = new SolidColorBrush(Color.FromArgb(tierEdge ? (byte)0x66 : (byte)0x16,
                                                            0xFF, 0xFF, 0xFF)),
                StrokeThickness = tierEdge ? 1.4 : 1
            };
            if (!tierEdge) sep.StrokeDashArray = [3, 3];
            WrChart.Children.Add(sep);
        }

        // Подписи слева. Сколько их влезет — столько и рисуем: за две недели можно
        // проехать из серебра в платину, и тогда дивизионов в кадре два десятка —
        // подписать каждый нельзя, они сольются в кашу. Поэтому шаг подписи
        // выбираем по высоте полосы: помещается дивизион — подписываем дивизионы,
        // не помещается — только тиры, а если и им тесно — через один.
        double divH = chartH / Math.Max(1, (max - min) / 100.0);

        // Подписи ставим ПО ГРАНИЦАМ САМИХ ТИРОВ, а не от нижнего края графика.
        // Иначе при шаге в тир (400 LP) буквы отсчитывались от произвольного
        // «min», кратного сотне, и уезжали относительно цветных полос: на широком
        // диапазоне «G» оказывалась над серебряной полосой.
        var marks = new List<(double Lp, string Text, Color Color)>();
        if (divH >= 12)
        {
            // Помещается дивизион — подписываем каждый: «EIV», «EIII», …
            for (int line = min; line < max; line += 100)
            {
                var t = BandAt(line);
                marks.Add((line + 50,
                    line >= 2800 ? "M" : $"{t.Name[0]}{new[] { "IV", "III", "II", "I" }[(line % 400) / 100]}",
                    t.Color));
            }
        }
        else
        {
            // Тесно — только тиры, буквой по центру ВИДИМОЙ части своей полосы.
            for (int tier = min / 400 * 400; tier < max; tier += 400)
            {
                double from = Math.Max(min, tier), to = Math.Min(max, tier + 400);
                if (to - from < 60) continue;        // от полосы видна одна щель
                var t = BandAt(tier);
                marks.Add(((from + to) / 2, tier >= 2800 ? "M" : t.Name[0].ToString(), t.Color));
            }
        }

        double lastLabelY = double.MaxValue;
        foreach (var (lp, text, color) in marks)
        {
            var y = Y(lp);
            if (lastLabelY - y < 11) continue;       // соседняя подпись слишком близко
            var lab = new TextBlock
            {
                Text = text, FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 8, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(color), Opacity = 0.95
            };
            lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lab, Math.Max(0, axisW - 4 - lab.DesiredSize.Width));
            Canvas.SetTop(lab, y - lab.DesiredSize.Height / 2);
            WrChart.Children.Add(lab);
            lastLabelY = y;
        }

        var lineColor = BandAt(pts[^1].AbsLp).Color;
        var stroke = new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xF8));

        if (pts.Count >= 2)
        {
            var poly = new System.Windows.Shapes.Polyline
            { Stroke = stroke, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < pts.Count; i++) poly.Points.Add(new Point(X(i), Y(pts[i].AbsLp)));
            WrChart.Children.Add(poly);
        }

        int last = pts.Count - 1;
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7, Height = 7, Fill = new SolidColorBrush(lineColor),
            Stroke = stroke, StrokeThickness = 1.5
        };
        Canvas.SetLeft(dot, X(last) - 3.5);
        Canvas.SetTop(dot, Y(pts[last].AbsLp) - 3.5);
        WrChart.Children.Add(dot);

        var occupied = new List<(double L, double R)>();
        AddDateLabel(pts[0].Date, X(0), h, Align.Left, occupied);
        if (pts.Count > 1) AddDateLabel(pts[last].Date, X(last), h, Align.Right, occupied);
    }

    private void DrawWrChart()
    {
        // Режим графика — из настроек: винрейт, движение ранга или ничего.
        // Движение ранга есть только у ранговых: в нормалах и ARAM очков нет,
        // и при режиме «рейтинг» там вечно стояла подсказка про рейтинговые
        // игры. Там график всегда по винрейту.
        var ranked = _selectedQueue is "solo" or "flex";
        if (AppSettings.Current.ChartMode == "rating" && ranked) { DrawRatingChart(); return; }
        if (AppSettings.Current.ChartMode == "off") { WrChart.Children.Clear(); return; }

        WrChart.Children.Clear();
        var v = _sessionView;
        IReadOnlyList<SessionTracker.WrPoint> pts = v?.WinrateHistory ?? [];
        double w = WrChart.ActualWidth > 4 ? WrChart.ActualWidth : 150;
        double h = WrChart.ActualHeight > 4 ? WrChart.ActualHeight : 58;

        // Истории ещё нет — вместо графика скелетон: крупная плашка под область
        // линии и три коротких под подписи дат.
        if (pts.Count == 0)
        {
            var area = Skeleton.Block(Math.Max(20, w - 22), Math.Max(12, h - 18), 6);
            Canvas.SetLeft(area, 22);
            Canvas.SetTop(area, 2);
            WrChart.Children.Add(area);
            for (int i = 0; i < 3; i++)
            {
                var lab = Skeleton.Block(26, 7, 3);
                Canvas.SetLeft(lab, 22 + i * Math.Max(30, (w - 48) / 2.4));
                Canvas.SetTop(lab, h - 11);
                WrChart.Children.Add(lab);
            }
            return;
        }

        // Окно графика из настроек: 30 дней — текущая форма, 90 — тренд за сезон.
        var since = DateTime.Now.AddDays(-Math.Max(1, AppSettings.Current.ChartDays));
        if (pts.Count > 0 && pts.Any(p => p.Date >= since))
            pts = pts.Where(p => p.Date >= since).ToList();

        // Журнал сезонный (сотни игр) — для рисования прореживаем до ~120 точек
        // равномерной выборкой, первая и последняя игры сохраняются всегда.
        const int maxDots = 120;
        if (pts.Count > maxDots)
        {
            var sampled = new List<SessionTracker.WrPoint>(maxDots);
            for (int i = 0; i < maxDots; i++)
                sampled.Add(pts[(int)Math.Round((double)i / (maxDots - 1) * (pts.Count - 1))]);
            pts = sampled;
        }

        // Диапазон Y — вокруг данных, чтобы динамика была видна.
        // Истории ещё нет — берём нейтральные 45–55%: оси и сетка на месте,
        // просто без линии (график не пропадает до первой игры).
        double min = pts.Count > 0 ? pts.Min(p => p.Winrate) : 45;
        double max = pts.Count > 0 ? pts.Max(p => p.Winrate) : 55;
        if (max - min < 6) { double m = (min + max) / 2; min = m - 3; max = m + 3; }
        min = Math.Max(0, min - 1); max = Math.Min(100, max + 1);
        if (max <= min) max = min + 1;

        const double axisW = 22;            // место под вертикальную шкалу винрейта
        double padY = 4, bottomPad = 12;    // низ — под подписи дат
        double plotL = axisW, plotR = w;
        double chartH = h - padY - bottomPad;
        double baseY = padY + chartH;
        double Y(double wr) => padY + (1 - (wr - min) / (max - min)) * chartH;
        double X(int i) => pts.Count == 1 ? (plotL + plotR) / 2
            : plotL + (double)i / (pts.Count - 1) * (plotR - plotL - 2) + 1;

        // Линия графика — того же бирюзового тона, что и заливка под ней, только
        // светлее и без прозрачности: тренд выделяется, но график остаётся
        // одноцветным (разные цвета линии по винрейту сбивали с толку).
        var lineBrush = new SolidColorBrush(Color.FromRgb(0x5C, 0xE2, 0xF0));
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        // Вертикальная ось + горизонтальная сетка со шкалой винрейта.
        // 5 уровней (верх / ¾ / середина / ¼ / низ) — с промежуточными значениями.
        WrChart.Children.Add(new System.Windows.Shapes.Line
        { X1 = plotL, X2 = plotL, Y1 = padY, Y2 = baseY, Stroke = gridBrush, StrokeThickness = 1 });
        foreach (var frac in new[] { 1.0, 0.75, 0.5, 0.25, 0.0 })
        {
            double wr = min + (max - min) * frac;
            double yy = Y(wr);
            WrChart.Children.Add(new System.Windows.Shapes.Line
            { X1 = plotL, X2 = plotR, Y1 = yy, Y2 = yy, Stroke = gridBrush, StrokeThickness = 1 });
            var lab = new TextBlock
            {
                Text = $"{wr:0}", FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 8, Foreground = MuteBrush, Opacity = 0.75
            };
            lab.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(lab, Math.Max(0, axisW - 4 - lab.DesiredSize.Width));
            Canvas.SetTop(lab, Math.Min(baseY - lab.DesiredSize.Height, Math.Max(-1, yy - lab.DesiredSize.Height / 2)));
            WrChart.Children.Add(lab);
        }

        if (pts.Count >= 2)
        {
            // Заливка области под линией — полупрозрачный синий
            var area = new System.Windows.Shapes.Polygon
            { Fill = new SolidColorBrush(Color.FromArgb(0x3A, 0x36, 0xD6, 0xE7)) };
            area.Points.Add(new Point(X(0), baseY));
            for (int i = 0; i < pts.Count; i++) area.Points.Add(new Point(X(i), Y(pts[i].Winrate)));
            area.Points.Add(new Point(X(pts.Count - 1), baseY));
            WrChart.Children.Add(area);

            // Линия поверх заливки
            var poly = new System.Windows.Shapes.Polyline
            { Stroke = lineBrush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
            for (int i = 0; i < pts.Count; i++) poly.Points.Add(new Point(X(i), Y(pts[i].Winrate)));
            WrChart.Children.Add(poly);
        }

        if (pts.Count == 0) return;   // истории нет — оставляем пустую сетку

        // Точка последнего значения
        int last = pts.Count - 1;
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = lineBrush };
        Canvas.SetLeft(dot, X(last) - 3);
        Canvas.SetTop(dot, Y(pts[last].Winrate) - 3);
        WrChart.Children.Add(dot);

        // Подписи дат под графиком. Крайние — всегда; промежуточные ставим не
        // равномерно, а в точках смены направления линии (локальные пики/впадины).
        // Диапазон делим на 4 части и в каждой берём самую заметную точку разворота
        // — если в этой части график вообще менял направление.
        var occupied = new List<(double L, double R)>();
        AddDateLabel(pts[0].Date, X(0), h, Align.Left, occupied);
        if (pts.Count > 1)
        {
            AddDateLabel(pts[last].Date, X(last), h, Align.Right, occupied);

            // Точки разворота: где знак наклона меняется. Prom — «заметность»
            // (насколько резкий пик/впадина), по ней выбираем главный разворот.
            static int Sgn(double d) => d > 0.05 ? 1 : d < -0.05 ? -1 : 0;
            var turns = new List<(int Idx, double Prom)>();
            for (int i = 1; i < last; i++)
            {
                int s1 = Sgn(pts[i].Winrate - pts[i - 1].Winrate);
                int s2 = Sgn(pts[i + 1].Winrate - pts[i].Winrate);
                if (s1 != 0 && s2 != 0 && s1 != s2)
                    turns.Add((i, Math.Abs(pts[i].Winrate - pts[i - 1].Winrate)
                                 + Math.Abs(pts[i + 1].Winrate - pts[i].Winrate)));
            }

            // По одной точке-развороту на каждую из 4 частей (где разворот был).
            var picks = new List<(int Idx, double Prom)>();
            for (int b = 0; b < 4; b++)
            {
                double lo = (double)b / 4 * last, hi = (double)(b + 1) / 4 * last;
                (int Idx, double Prom) best = (-1, 0);
                foreach (var t in turns)
                    if (t.Idx >= lo && t.Idx < hi && t.Prom > best.Prom) best = t;
                if (best.Idx > 0) picks.Add(best);
            }

            // Ставим по убыванию заметности; AddDateLabel пропустит наезжающие.
            foreach (var p in picks.OrderByDescending(p => p.Prom))
                AddDateLabel(pts[p.Idx].Date, X(p.Idx), h, Align.Center, occupied);
        }
    }

    private enum Align { Left, Center, Right }

    // Рисует подпись даты, если она не наезжает на уже стоящие (occupied —
    // занятые по горизонтали интервалы). Крайние подписи ставятся всегда первыми.
    private void AddDateLabel(DateTime date, double x, double h, Align align,
                             List<(double L, double R)> occupied)
    {
        var tb = new TextBlock
        {
            Text = date.ToString("d.MM"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 9, Foreground = MuteBrush, Opacity = 0.7
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = align switch
        {
            Align.Right  => x - tb.DesiredSize.Width,
            Align.Center => x - tb.DesiredSize.Width / 2,
            _            => x,
        };
        left = Math.Max(0, left);
        double right = left + tb.DesiredSize.Width;
        foreach (var (l, r) in occupied)
            if (left < r + 3 && l < right + 3) return;   // пересекается — пропускаем
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, h - 11);
        WrChart.Children.Add(tb);
        occupied.Add((left, right));
    }
}
