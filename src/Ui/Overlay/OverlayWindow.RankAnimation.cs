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

// Окно драфта — анимации смены ранга: переход лиги, сварка эмблемы, вспышка.
public partial class OverlayWindow
{
    /// Переход в соседний дивизион — в два хода.
    ///
    /// Сначала полоса добирается до края с ПРЕЖНИМ рангом: повышение — это
    /// момент, когда шкала закончилась, и он должен быть виден. На переломе
    /// эмблема вспыхивает и меняется, после чего полоса набирается с чистого
    /// края до новых очков.
    ///
    /// Разом подменить подпись нельзя: тогда выходит, что новый ранг был всегда,
    /// а достижение — самое важное, что происходит с игроком за вечер, —
    /// проскакивает незамеченным.
    private void AnimateRankCross(bool up, bool newLeague, ImageSource? emblem, string rankLine,
                                  Brush rankBrush, int toLp, double toPct)
    {
        _lpMoving = false;
        var edge = up ? 100.0 : 0.0;
        var start = up ? 0.0 : 100.0;

        // Прогноз был про ПРЕЖНИЙ дивизион — на переходе он перестаёт что-либо
        // значить. Убираем его сразу, иначе штриховка висела бы над едущей
        // полосой, обещая игру, которой в этой шкале уже не будет.
        HideLpHints();

        _lpFrom = _lpShown ?? 0; _lpTo = (int)edge;
        _pctFrom = _pctShown; _pctTo = edge;

        var first = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        _lpMoving = true;
        _lpMoveStart = DateTime.UtcNow;
        first.Completed += (_, _) =>
        {
            // Перелом: новый ранг на месте, старая шкала закончилась.
            RankText.Text = rankLine;
            RankText.Foreground = rankBrush;

            if (newLeague && up)
            {
                // Новая лига — знака этого у игрока ещё не было: собираем его.
                WeldEmblem(emblem, up);
            }
            else if (newLeague)
            {
                SlideEmblemBack(emblem);
            }
            else
            {
                // Соседний дивизион — событие поменьше: смена знака и вспышка.
                RankEmblem.Source = emblem;
                RankEmblem.Visibility = emblem != null ? Visibility.Visible : Visibility.Collapsed;
                FlashRank(up);
            }

            _lpFrom = (int)start; _lpTo = toLp;
            _pctFrom = start; _pctTo = toPct;
            var second = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.5))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            second.Completed += (_, _) =>
            {
                _lpMoving = false;
                ApplyRankProgress();
                FadeInLpHints();
            };
            BeginAnimation(LpProgressProperty, second);
        };
        BeginAnimation(LpProgressProperty, first);

        GrowLpText(TimeSpan.FromSeconds(0.85));
    }

    /// Переход в НОВУЮ ЛИГУ: старый знак уезжает вниз, новый собирается из двух
    /// половин.
    ///
    /// Повышение тира — редкое событие: между ними десятки игр. Обычной вспышки
    /// для него мало, поэтому знак именно СОБИРАЕТСЯ: половины приезжают сверху,
    /// сходятся и свариваются по шву — луч, вспышка, скачок размера. Двух секунд
    /// на это не жалко, такое видишь раз в неделю.
    private void WeldEmblem(ImageSource? emblem, bool up)
    {
        if (emblem is null)
        {
            RankEmblem.Source = null;
            RankEmblem.Visibility = Visibility.Collapsed;
            return;
        }

        // Ширина эмблемы известна только после раскладки — от неё зависит и линия
        // разреза, и место шва.
        var h = RankEmblem.ActualHeight > 0 ? RankEmblem.ActualHeight : 74;
        var w = RankEmblem.ActualWidth > 0 ? RankEmblem.ActualWidth : h;

        RankHalfLeft.Source = RankHalfRight.Source = emblem;
        RankHalfLeft.Clip = new RectangleGeometry(new Rect(0, 0, w / 2, h));
        RankHalfRight.Clip = new RectangleGeometry(new Rect(w / 2, 0, w / 2, h));
        RankWeld.Width = w;
        RankWeldBeam.Height = h * 0.9;
        RankWeldBeam.Margin = new Thickness(w / 2 - RankWeldBeam.Width / 2, 0, 0, 0);
        RankWeld.Visibility = Visibility.Visible;
        RankWeldScale.ScaleX = RankWeldScale.ScaleY = 1;

        // 1. Старый знак уезжает вниз и гаснет.
        var drop = new DoubleAnimation(0, 46, TimeSpan.FromSeconds(0.42))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var dim = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.42));
        dim.Completed += (_, _) =>
        {
            RankEmblem.Visibility = Visibility.Collapsed;
            // Анимацию снимаем ЯВНО: пока она держит свойство, присваивание
            // значения ничего не меняет — знак так и остался бы уехавшим вниз.
            RankEmblem.BeginAnimation(UIElement.OpacityProperty, null);
            RankEmblem.Opacity = 1;
            RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, null);
            RankEmblemMove.Y = 0;
        };
        RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, drop);
        RankEmblem.BeginAnimation(UIElement.OpacityProperty, dim);

        // 2. Половины спускаются сверху — врозь и не спеша.
        var gap = w * 0.22;
        var fall = TimeSpan.FromSeconds(0.75);
        var joinAt = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.95));

        var downL = new DoubleAnimationUsingKeyFrames();
        downL.KeyFrames.Add(new LinearDoubleKeyFrame(-h, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        downL.KeyFrames.Add(new SplineDoubleKeyFrame(0, KeyTime.FromTimeSpan(fall),
            new KeySpline(0.1, 0.7, 0.2, 1.0)));

        var sideL = new DoubleAnimationUsingKeyFrames();
        sideL.KeyFrames.Add(new LinearDoubleKeyFrame(-gap, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sideL.KeyFrames.Add(new LinearDoubleKeyFrame(-gap, KeyTime.FromTimeSpan(fall)));
        // 3. Сходятся — быстро, в одно движение: это и есть момент стыка.
        sideL.KeyFrames.Add(new SplineDoubleKeyFrame(0, joinAt, new KeySpline(0.8, 0.0, 0.9, 1.0)));
        sideL.Completed += (_, _) => FinishWeld(emblem, up);

        RankHalfLeftMove.BeginAnimation(TranslateTransform.YProperty, downL);
        RankHalfLeftMove.BeginAnimation(TranslateTransform.XProperty, sideL);
        RankHalfRightMove.BeginAnimation(TranslateTransform.YProperty, downL.Clone());
        var sideR = new DoubleAnimationUsingKeyFrames();
        sideR.KeyFrames.Add(new LinearDoubleKeyFrame(gap, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sideR.KeyFrames.Add(new LinearDoubleKeyFrame(gap, KeyTime.FromTimeSpan(fall)));
        sideR.KeyFrames.Add(new SplineDoubleKeyFrame(0, joinAt, new KeySpline(0.8, 0.0, 0.9, 1.0)));
        RankHalfRightMove.BeginAnimation(TranslateTransform.XProperty, sideR);

        // Луч шва загорается ровно там, где половины встречаются.
        var beamOn = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.95));
        var beamOff = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.55));

        var spark = new DoubleAnimationUsingKeyFrames();
        spark.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
        spark.KeyFrames.Add(new LinearDoubleKeyFrame(1, beamOn));
        spark.KeyFrames.Add(new SplineDoubleKeyFrame(0, beamOff, new KeySpline(0.0, 0.7, 0.2, 1.0)));
        RankWeldBeam.BeginAnimation(UIElement.OpacityProperty, spark);

        var stretch = new DoubleAnimationUsingKeyFrames();
        stretch.KeyFrames.Add(new LinearDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
        stretch.KeyFrames.Add(new LinearDoubleKeyFrame(1.15, beamOn));
        stretch.KeyFrames.Add(new SplineDoubleKeyFrame(0.9, beamOff, Ease()));
        RankWeldBeamScale.BeginAnimation(ScaleTransform.ScaleYProperty, stretch);

        var heat = new DoubleAnimationUsingKeyFrames();
        heat.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
        heat.KeyFrames.Add(new LinearDoubleKeyFrame(48, beamOn));
        heat.KeyFrames.Add(new SplineDoubleKeyFrame(0, beamOff, Ease()));
        var heatLight = new DoubleAnimationUsingKeyFrames();
        heatLight.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
        heatLight.KeyFrames.Add(new LinearDoubleKeyFrame(1, beamOn));
        heatLight.KeyFrames.Add(new SplineDoubleKeyFrame(0, beamOff, Ease()));
        RankWeldGlow.Color = up ? Color.FromRgb(0xFF, 0xF3, 0xC4) : Color.FromRgb(0xFF, 0xC7, 0xBE);
        RankWeldGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, heat);
        RankWeldGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, heatLight);

        // 4. Сварилось — знак вырастает на треть и оседает обратно.
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.9))));
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(1.3, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.02))));
        // Оседает ровно к единице, без пружины. Пружинная кривая проскакивает
        // цель: знак уходил ниже нормального размера и возвращался обратно, и
        // последним движением был рост — будто он в конце становится больше.
        pop.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.5)),
            new KeySpline(0.2, 0.9, 0.3, 1.0)));
        RankWeldScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RankWeldScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());

        static KeySpline Ease() => new(0.0, 0.7, 0.2, 1.0);
    }

    /// Возвращает обычную эмблему на место собранной: дальше она живёт как всегда.
    private void FinishWeld(ImageSource emblem, bool up)
    {
        var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.75) };
        hold.Tick += (_, _) =>
        {
            hold.Stop();

            // Возвращаем знак на место — и с чистого листа: все анимации,
            // которые держат сдвиг, масштаб и прозрачность, снимаем. Иначе
            // следующий переход начнётся из положения, оставшегося от этого.
            RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, null);
            RankEmblemMove.Y = 0;
            RankEmblem.BeginAnimation(UIElement.OpacityProperty, null);
            RankEmblem.Opacity = 1;
            RankEmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RankEmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RankEmblemScale.ScaleX = RankEmblemScale.ScaleY = 1;

            RankEmblem.Source = emblem;
            RankEmblem.Visibility = Visibility.Visible;

            // То же самое у собранного знака: он ещё пригодится следующей лиге.
            RankWeldScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            RankWeldScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RankWeldScale.ScaleX = RankWeldScale.ScaleY = 1;
            RankHalfLeftMove.BeginAnimation(TranslateTransform.XProperty, null);
            RankHalfLeftMove.BeginAnimation(TranslateTransform.YProperty, null);
            RankHalfRightMove.BeginAnimation(TranslateTransform.XProperty, null);
            RankHalfRightMove.BeginAnimation(TranslateTransform.YProperty, null);
            RankWeldBeam.BeginAnimation(UIElement.OpacityProperty, null);
            RankWeldBeam.Opacity = 0;
            RankWeldBeamScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            RankWeld.Visibility = Visibility.Collapsed;
        };
        hold.Start();
        _ = up;
    }

    /// Падение в прошлую лигу: без торжества. Старый знак уходит вверх, прежний
    /// выезжает снизу — он у игрока уже был, собирать его заново не из чего.
    private void SlideEmblemBack(ImageSource? emblem)
    {
        var up = new DoubleAnimation(0, -44, TimeSpan.FromSeconds(0.3))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var out_ = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.3));
        out_.Completed += (_, _) =>
        {
            RankEmblem.Source = emblem;
            RankEmblem.Visibility = emblem != null ? Visibility.Visible : Visibility.Collapsed;

            // Новый (прежний) знак заходит снизу.
            var back = new DoubleAnimation(44, 0, TimeSpan.FromSeconds(0.45))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            var show = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35));
            show.Completed += (_, _) =>
            {
                RankEmblem.BeginAnimation(UIElement.OpacityProperty, null);
                RankEmblem.Opacity = 1;
                RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, null);
                RankEmblemMove.Y = 0;
            };
            RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, back);
            RankEmblem.BeginAnimation(UIElement.OpacityProperty, show);
        };
        RankEmblemMove.BeginAnimation(TranslateTransform.YProperty, up);
        RankEmblem.BeginAnimation(UIElement.OpacityProperty, out_);
    }

    /// Вспышка на эмблеме в момент перехода: свет и короткий подскок размера.
    /// Вверх — золотом, вниз — красным: цвет говорит, что случилось, раньше,
    /// чем человек прочитает подпись.
    private void FlashRank(bool up)
    {
        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = up ? Color.FromRgb(0xFF, 0xD7, 0x5A) : Color.FromRgb(0xE8, 0x50, 0x3F),
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0,
        };
        RankEmblem.Effect = glow;

        var flash = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.1));
        var end = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.3));
        static KeySpline Ease() => new(0.0, 0.7, 0.2, 1.0);

        var blur = new DoubleAnimationUsingKeyFrames();
        blur.KeyFrames.Add(new LinearDoubleKeyFrame(55, flash));
        blur.KeyFrames.Add(new SplineDoubleKeyFrame(0, end, Ease()));

        var light = new DoubleAnimationUsingKeyFrames();
        light.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, flash));
        light.KeyFrames.Add(new SplineDoubleKeyFrame(0, end, Ease()));
        // Эффект снимаем: висящий DropShadow дорог при перерисовке.
        light.Completed += (_, _) => RankEmblem.Effect = null;

        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, blur);
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, light);

        // Подскок эмблемы: вверх — заметный, вниз — сдержанный. Понижение и так
        // неприятно, праздновать его прыжком не стоит.
        var pop = new DoubleAnimationUsingKeyFrames();
        pop.KeyFrames.Add(new LinearDoubleKeyFrame(up ? 1.18 : 0.94, flash));
        pop.KeyFrames.Add(new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.65)),
            new KeySpline(0.2, 0.9, 0.3, 1.0)));
        RankEmblemScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RankEmblemScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop.Clone());
    }
}
