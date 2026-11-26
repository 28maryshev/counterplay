using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace Counterplay;

/// <summary>
/// Бесконечные анимации, которые останавливаются по-настоящему.
///
/// Окно драфта прозрачное (layered window), и каждый кадр любой анимации —
/// перерисовка всего окна. Бесконечную анимацию нельзя снимать
/// <c>BeginAnimation(dp, null)</c>: это только отвязка от свойства, а её часы
/// остаются в TimeManager и тикают, пока их не соберёт сборщик мусора (когда —
/// не угадать), и всё это время WPF рисует кадры. Здесь часы свои и
/// останавливаются явно — <c>Controller.Stop</c> и <c>Remove</c>. Опыт и
/// замеры — журнал 2026-11-26-pick-alarm-gpu, проверка — tests/IdleFrames.
///
/// Разовые анимации (доиграли и встали) сюда не нужны: их часы кончаются сами.
/// </summary>
public static class Loops
{
    /// Кадров в секунду для мигания прозрачностью: глаз ловит его и на 24
    /// кадрах, а кадр — всё окно целиком. Не 20: WPF округляет частоту, и 20
    /// на деле дают 10 кадров — мигание рвётся; 24 дают 24–25.
    public const int BlinkFps = 24;

    // Часы по элементу и свойству; элемент ушёл — запись уходит вместе с ним.
    private static readonly ConditionalWeakTable<IAnimatable, Dictionary<DependencyProperty, AnimationClock>> Clocks = new();

    /// Пустить один проход <paramref name="anim"/> по кругу без конца; прежнюю
    /// анимацию этого свойства — остановить. fps 0 — сколько даст WPF (обычно 60).
    public static void Start(IAnimatable target, DependencyProperty dp, AnimationTimeline anim, int fps = 0)
    {
        Stop(target, dp);
        anim.RepeatBehavior = RepeatBehavior.Forever;
        if (fps > 0) Timeline.SetDesiredFrameRate(anim, fps);
        var clock = anim.CreateClock();
        Clocks.GetOrCreateValue(target)[dp] = clock;
        target.ApplyAnimationClock(dp, clock);
    }

    /// Остановить: часы — вон из TimeManager, свойству — его собственное значение.
    public static void Stop(IAnimatable target, DependencyProperty dp)
    {
        if (!Clocks.TryGetValue(target, out var byDp) || !byDp.Remove(dp, out var clock)) return;
        if (clock.Controller is { } c) { c.Stop(); c.Remove(); }
        target.ApplyAnimationClock(dp, null);
    }
}
