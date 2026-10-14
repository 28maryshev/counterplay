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

// Окно драфта — полоса LP: прогресс в дивизионе, засечки и подсказки «что даст игра».
public partial class OverlayWindow
{
    // Заполнение полосы LP считаем от ФАКТИЧЕСКОЙ ширины дорожки: она тянется по
    // ширине панели, поэтому фиксированное число пикселей здесь не годится.
    private double _rankPct;

    // ── Что даст следующая игра ───────────────────────────────────────────
    //
    // Полоса показывает, где игрок сейчас. Но перед игрой важнее другое: хватит
    // ли одной победы и сколько игр осталось до перехода. Поэтому поверх полосы
    // идёт штриховка шириной в одну победу, а у краёв шкалы — засечки, которые
    // делят остаток на игры.
    //
    // Прибавку берём из ЕГО последних игр, а не «примерно двадцать»: она зависит
    // от рейтинга скрытого матчмейкинга, и чужое среднее здесь врёт.
    private int _lpWin;          // средняя прибавка за победу; 0 — не знаем

    private int _lpLoss;         // средняя потеря за поражение

    private bool _lpDivisioned;  // тир с дивизионами: у Мастера+ шкала другая

    private bool _lpLastLost;    // последняя игра проиграна

    // ── Как LP меняются после игры ────────────────────────────────────────
    //
    // Партия закончилась — и полоса доезжает до нового значения, а цифры
    // перещёлкиваются до него же. Прыжок в одно мгновение человек пропускает:
    // он смотрит на экран победы, возвращается — и не знает, было 73 или 93.
    // Движение это показывает.
    //
    // Анимируем не ширину полосы, а само ЗНАЧЕНИЕ: от него зависят ещё и
    // штриховка будущей игры, и засечки — иначе они стояли бы на месте, пока
    // полоса едет под ними.
    public static readonly DependencyProperty LpProgressProperty =
        DependencyProperty.Register(nameof(LpProgress), typeof(double), typeof(OverlayWindow),
            new PropertyMetadata(0.0, OnLpProgressChanged));

    /// Доля пройденного пути анимации, 0…1. Публичное — этого требует WPF.
    public double LpProgress
    {
        get => (double)GetValue(LpProgressProperty);
        set => SetValue(LpProgressProperty, value);
    }

    private bool _lpMoving;       // полоса сейчас едет к новому значению

    private DateTime _lpMoveStart; // когда поехала — предохранитель от зависшего флага

    private int _rankAbs;         // ранг в сквозной шкале: тир + дивизион + очки

    private int? _lpShown;        // сколько очков показано сейчас; null — ещё ничего

    private double _pctShown;     // и то же самое в процентах шкалы

    private string _rankKey = ""; // тир+дивизион: при их смене считать нечего

    private double _lpFrom, _lpTo, _pctFrom, _pctTo;

    private static void OnLpProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not OverlayWindow w) return;
        var k = (double)e.NewValue;
        w._rankPct = w._pctFrom + (w._pctTo - w._pctFrom) * k;
        var lp = w._lpFrom + (w._lpTo - w._lpFrom) * k;
        w.RankLpText.Text = $"{(int)Math.Round(lp)} LP";
        w.ApplyRankProgress();
    }

    /// Ранг одним числом: тир, дивизион и очки в общей шкале. Нужен, чтобы
    /// отличить повышение от понижения — по самим очкам этого не видно.
    private static int RankAbs(string tier, string div, int lp)
    {
        var idx = Array.FindIndex(TierBands,
            b => string.Equals(b.Name, tier, StringComparison.OrdinalIgnoreCase));
        var baseLp = idx >= 0 ? TierBands[idx].Base : 0;
        // Дивизион I — старший, IV — младший.
        var d = div.ToUpperInvariant() switch
        {
            "I" => 3, "II" => 2, "III" => 1, "IV" => 0, _ => 0,
        };
        return baseLp + d * 100 + lp;
    }

    /// Есть ли у показанной очереди LP: в нормалах и ARAM очков нет.
    private bool _lpBarShown = true;

    /// <summary>
    /// Полоса LP и засечки над ней — по настройкам и только там, где есть LP.
    /// В нормалах и ARAM под названием очереди висела пустая полоса, как будто
    /// ранг не загрузился. Одно место на обоих: настройки перерисовывают окно и
    /// вернули бы спрятанную при показе профиля полосу.
    /// </summary>
    private void ApplyLpBarVisibility()
    {
        var s = AppSettings.Current;
        var on = s.ReadyRank && !s.ReadyCompact && _lpBarShown;
        RankProgressTrack.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        // Засечки живут вместе с полосой: без неё они висели бы в пустоте.
        RankTicks.Visibility = RankProgressTrack.Visibility;
    }

    /// Прячет прогноз (штриховку и засечки) до конца перехода.
    private void HideLpHints()
    {
        foreach (UIElement el in new UIElement[] { RankProgressGhost, RankProgressGhost2 })
        {
            // Снимаем возможную анимацию прозрачности: пока она держит свойство,
            // прятать элемент бессмысленно — он вернётся на следующем кадре.
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Visibility = Visibility.Collapsed;
        }
        RankTicks.Children.Clear();
    }

    /// Проявляет новую штриховку и засечки после того, как полоса доехала.
    /// Появиться разом они не должны: это уже другой прогноз, и подмена на
    /// полном свету читалась бы как рывок.
    private void FadeInLpHints()
    {
        var hints = new List<UIElement> { RankProgressGhost, RankProgressGhost2 };
        hints.AddRange(RankTicks.Children.Cast<UIElement>());

        foreach (var el in hints)
        {
            if (el.Visibility != Visibility.Visible) continue;
            var to = el.Opacity;
            var fade = new DoubleAnimation(0, to, TimeSpan.FromSeconds(0.28));
            // Снимаем анимацию после показа: пока она держит свойство, обычное
            // присваивание Opacity не работает, и следующий пересчёт был бы
            // проигнорирован.
            fade.Completed += (_, _) =>
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = to;
            };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    /// Запускает перещёлкивание цифр и доезд полосы.
    private void AnimateLp(int fromLp, int toLp, double fromPct, double toPct)
    {
        // Предыдущая анимация, если она ещё идёт, обрывается новой — и её
        // Completed не сработает. Флаг поднимет заново эта же анимация ниже.
        _lpMoving = false;
        _lpFrom = fromLp; _lpTo = toLp;
        _pctFrom = fromPct; _pctTo = toPct;

        // Полсекунды: цифры успевают пробежать, но человек не ждёт их.
        var run = TimeSpan.FromSeconds(0.55);
        var move = new DoubleAnimation(0, 1, run)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        _lpMoving = true;
        _lpMoveStart = DateTime.UtcNow;
        move.Completed += (_, _) =>
        {
            _lpMoving = false;
            ApplyRankProgress();   // прогноз на следующую игру
            FadeInLpHints();       // и он не возникает рывком, а проявляется
        };
        BeginAnimation(LpProgressProperty, move);

        GrowLpText(run);
    }

    /// Пока цифры перещёлкиваются, надпись крупнее: взгляд сам идёт туда, где
    /// движение. К концу счёта возвращается к обычному размеру.
    private void GrowLpText(TimeSpan run)
    {
        var grow = new DoubleAnimationUsingKeyFrames();
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1.5, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.12))));
        grow.KeyFrames.Add(new LinearDoubleKeyFrame(1.5,
            KeyTime.FromTimeSpan(TimeSpan.FromSeconds(Math.Max(0.2, run.TotalSeconds - 0.13)))));
        grow.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(run),
            new KeySpline(0.2, 0.9, 0.3, 1.0)));
        RankLpScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RankLpScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow.Clone());
    }

    private static readonly Brush LpTickBrush =
        new SolidColorBrush(Color.FromArgb(0xCC, 0xBF, 0xF3, 0xF9));

    // Засечка самого перехода — ярче и выше остальных: это финиш, а не граница
    // между играми.
    private static readonly Brush LpEdgeTickBrush =
        new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    /// Средняя величина изменения LP по последним играм. Ноль — не из чего
    /// считать: пропущенные дельты бывают у первых игр после установки.
    private static int AvgLp(IEnumerable<SessionTracker.RecentGame> games, bool win)
    {
        var vals = games
            .Select(g => g.LpDelta ?? 0)
            .Where(d => win ? d > 0 : d < 0)
            .Select(Math.Abs)
            .ToList();
        return vals.Count == 0 ? 0 : (int)Math.Round(vals.Average());
    }

    private void AddLpTick(double x, bool edge = false)
    {
        var tick = new Border
        {
            Width = edge ? 2 : 1.5,
            Height = edge ? 7 : 5,
            CornerRadius = new CornerRadius(1),
            Background = edge ? LpEdgeTickBrush : LpTickBrush,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        Canvas.SetLeft(tick, x - (edge ? 1 : 0.75));
        Canvas.SetBottom(tick, 0);
        RankTicks.Children.Add(tick);
    }

    private void ApplyRankProgress()
    {
        var w = RankProgressTrack.ActualWidth;
        var pct = Math.Clamp(_rankPct, 0, 100);
        RankProgressFill.Width = w > 0 ? w * pct / 100.0 : 0;

        // Пока полоса едет, штриховку и засечки не трогаем: они показывали
        // ПРОШЛЫЙ прогноз, и полоса на глазах входит в него — это и есть
        // «предсказание сбылось». Новый прогноз появится, когда она доедет.
        //
        // Предохранитель по времени: самая долгая анимация — переход между лигами,
        // около двух секунд. Если флаг поднят дольше четырёх, что-то оборвало
        // анимацию, и ждать её окончания бессмысленно — рисуем прогноз.
        if (_lpMoving && DateTime.UtcNow - _lpMoveStart < TimeSpan.FromSeconds(4)) return;
        _lpMoving = false;

        RankTicks.Children.Clear();
        // Нечего показывать: нет ширины, нет своих дельт или тир без дивизионов
        // (у Мастера и выше сотня очков ничего не открывает).
        if (w <= 0 || !_lpDivisioned || _lpWin <= 0)
        {
            RankProgressGhost.Visibility = Visibility.Collapsed;
            RankProgressTrack.ToolTip = null;
            return;
        }

        // У краёв шкалы смотрим не на одну игру, а на две: на этом расстоянии
        // переход уже реален, и важно видеть, что до него ровно пара игр.
        // Переход близко — это не «очков больше семидесяти», а «осталось не
        // больше двух игр». У одного победа даёт 15 очков, у другого 28, и один
        // и тот же порог значит для них разное: первому от 70 идти две игры,
        // второму — одну. Поэтому считаем ИГРАМИ.
        var winsTo = _lpWin > 0 ? (int)Math.Ceiling((100 - pct) / _lpWin) : 0;
        var lossesTo = _lpLoss > 0 ? (int)Math.Ceiling(pct / _lpLoss) : 0;

        var canUp = winsTo is > 0 and <= 2;
        // Красным горит только после проигранной игры. Выиграл — значит идёшь
        // вверх, и пугать потерей нечестно: ту же полосу человек только что
        // отыграл.
        var canDown = _lpLastLost && lossesTo is > 0 and <= 2;

        // Если близко и то и другое, показываем то, до чего ближе: повышение
        // важнее, когда до него не дальше, чем до падения.
        var up = canUp && (!canDown || winsTo <= lossesTo);
        var down = !up && canDown;
        var step = down ? _lpLoss : _lpWin;
        // Ровно столько игр, сколько осталось: одна победа до повышения — одна
        // полоска, две — две. В середине шкалы показываем одну, ближайшую.
        var games = up ? winsTo : down ? lossesTo : 1;

        // Вверху штрихуем то, что МОЖНО НАБРАТЬ, — вправо от набранного. Внизу
        // то, что можно ПОТЕРЯТЬ, — влево, поверх уже набранного: там вопрос не
        // «сколько осталось добрать», а «сколько ещё можно отдать».
        //
        // Каждая игра — своя полоска. Ближняя в полную силу, вторая бледнее:
        // до неё ещё нужно дожить, и выглядеть одинаково уверенно они не должны.
        var room = down ? pct : 100 - pct;
        var first = Math.Min(step, room);
        var second = games > 1 ? Math.Min(step, room - first) : 0;
        // Больше двух игр не рисуем: полоса превратилась бы в расчёску, а
        // разговор идёт о ближайших играх, а не обо всём пути.
        // Вверху — штриховка: этих очков ещё нет. Внизу — сплошной красный:
        // очки уже набраны, и штриховать их было бы неправдой; меняется цвет,
        // а не плотность.
        var brush = (Brush)FindResource(down ? "LpRiskFill" : "LpGhostHatch");

        void Hatch(Border seg, double span, double before, bool outer, double alpha = 1.0)
        {
            seg.Opacity = alpha;
            // before — сколько уже занято ближней игрой; вторая начинается за ней.
            var from = down ? pct - before - span : pct + before;
            seg.Margin = new Thickness(w * from / 100.0, 0, 0, 0);
            seg.Width = Math.Max(0, w * span / 100.0);
            // Скругляем только внешний край — тот, что смотрит на край шкалы.
            seg.CornerRadius = !outer ? new CornerRadius(0)
                : down ? new CornerRadius(4, 0, 0, 4)
                : new CornerRadius(0, 4, 4, 0);
            seg.Background = brush;
            seg.Visibility = span > 0.01 && step > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        Hatch(RankProgressGhost, first, 0, outer: second <= 0);
        // Вторая игра — едва заметная. Сверху это штриховка на пустом месте, и
        // она должна именно намекать; снизу под ней залитая полоса, там слабее
        // четверти цвет просто потеряется.
        Hatch(RankProgressGhost2, second, first, outer: true, alpha: down ? 0.45 : 0.25);

        // Засечки — только когда переход в паре игр: они делят остаток на эти
        // игры. Дальше по шкале их нет — там считать нечего, и чёрточки
        // превратились бы в шум.
        var need = up ? winsTo : down ? lossesTo : 0;

        if (need > 0 && step > 0)
        {
            // Границы между играми.
            for (var k = 1; k < need; k++)
            {
                var at = up ? pct + k * step : pct - k * step;
                if (at is <= 0 or >= 100) break;
                AddLpTick(w * at / 100.0);
            }
            // И сам переход — конец шкалы. Без него не видно, где линия
            // кончается: штриховка сливается с дорожкой, и последний отрезок
            // выглядит уходящим в никуда.
            AddLpTick(up ? w : 0, edge: true);
        }

        // Подсказка словами: цифры на полосе не написать, а вопрос «сколько ещё
        // игр» — первое, что приходит в голову при взгляде на неё.
        var text = Loc.T("rank.winGives", _lpWin);
        if (need > 0)
            text += " · " + Loc.T(up ? "rank.toPromo" : "rank.toDemote", need);
        // Стиль не задаём: фирменный вид подсказок лежит в Theme.xaml БЕЗ ключа,
        // и подхватывается сам. Раньше здесь стоял FindResource("HintTip") —
        // ключ убрали вместе со стилем, и эта строка валила программу целиком
        // при каждом показе сессии: после игры, в начале драфта, на банах.
        RankProgressTrack.ToolTip = new System.Windows.Controls.ToolTip
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 240,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            },
        };
    }

    // Цвет тира для полос графика рейтинга: тот же язык, что и эмблемы рангов.
    // Цвета берём СВЕТЛЫЕ: полоса рисуется прозрачной поверх тёмного фона, и
    // «настоящий» золотой (#D8A63E) на 20% превращался в коричневый, а изумруд —
    // в болотный. Светлая база после смешивания с фоном даёт узнаваемый оттенок.
    private static readonly (int Base, string Name, Color Color)[] TierBands =
    [
        (0,    "Iron",     Color.FromRgb(0x9A, 0x93, 0x8B)),
        (400,  "Bronze",   Color.FromRgb(0xD2, 0x8B, 0x4C)),
        (800,  "Silver",   Color.FromRgb(0xCD, 0xDC, 0xEA)),
        (1200, "Gold",     Color.FromRgb(0xFF, 0xD4, 0x6A)),
        (1600, "Platinum", Color.FromRgb(0x74, 0xEC, 0xD9)),
        (2000, "Emerald",  Color.FromRgb(0x4A, 0xEE, 0x90)),
        (2400, "Diamond",  Color.FromRgb(0x96, 0xC8, 0xFF)),
        (2800, "Master",   Color.FromRgb(0xCE, 0x86, 0xFF)),
    ];

    private static (string Name, Color Color) BandAt(int absLp)
    {
        var b = TierBands[0];
        foreach (var t in TierBands) if (absLp >= t.Base) b = t;
        return (b.Name, b.Color);
    }
}
