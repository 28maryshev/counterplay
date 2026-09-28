using System.Windows;
using System.Windows.Media;

namespace Counterplay;

/// <summary>
/// Куда класть и откуда брать геометрию окна. Пара действий, чтобы
/// <see cref="WindowScale"/> не знал, в какой именно настройке она живёт.
/// </summary>
public sealed record WindowPlace(
    Func<(double L, double T, double W, double H)> Read,
    Action<double, double, double, double> Write);

/// <summary>
/// Масштаб отдельных окон — тот же, что у оверлея: выбор человека
/// («Мелкий / Обычный / Крупный») × подгонка под разрешение клиента LoL.
///
/// Нужен потому, что окна пулов живут своей жизнью: оверлей масштабирует себя
/// сам, а настройки пулов, редактор и выбор чемпионов открывались всегда в одном
/// размере. На клиенте 1024×576 они выглядели громадными, на 1920×1080 —
/// мелкими, хотя человек один раз сказал, какой размер ему нужен.
///
/// Растёт и содержимое, и само окно: масштабировать одно без другого — значит
/// получить либо поля по краям, либо полосы прокрутки на ровном месте.
/// </summary>
static class WindowScale
{
    /// <summary>
    /// Привязать окно к масштабу. <paramref name="baseW"/>/<paramref name="baseH"/>
    /// и минимумы задаются в «единичном» размере — тут их умножат.
    ///
    /// <paramref name="remember"/> — необязательная память места и размера: окно
    /// открывается там, где его оставили в прошлый раз.
    /// </summary>
    public static void Apply(Window w, FrameworkElement root,
                             double baseW, double baseH, double minW, double minH,
                             WindowPlace? remember = null)
    {
        // SourceInitialized, а не Loaded: окно уже знает свой DPI, но ещё не
        // показано — человек не увидит, как оно прыгает в нужный размер.
        w.SourceInitialized += (_, _) =>
        {
            var k = AppSettings.Current.FontScale * OverlayWindow.ClientScaleFor(w);
            root.LayoutTransform = Math.Abs(k - 1.0) < 0.01 ? Transform.Identity : new ScaleTransform(k, k);

            // Больше рабочего стола окно не делаем: при «крупном» на маленьком
            // экране оно иначе вылезает за край вместе с кнопками.
            var area = SystemParameters.WorkArea;
            var maxW = Math.Max(320, area.Width  - 40);
            var maxH = Math.Max(240, area.Height - 40);
            w.MinWidth  = Math.Min(minW * k, maxW);
            w.MinHeight = Math.Min(minH * k, maxH);
            w.Width     = Math.Min(baseW * k, maxW);
            w.Height    = Math.Min(baseH * k, maxH);

            if (remember is null) return;
            var (l, t, ww, hh) = remember.Read();
            if (ww <= 0 || hh <= 0) return;          // ещё ни разу не двигали

            // Не отбрасываем, а поджимаем под нынешние границы. Масштаб зависит
            // от разрешения клиента LoL: человек поменял его — и запомненный
            // размер может оказаться меньше нового минимума. Отбрасывать из-за
            // этого и место, и размер значило бы забыть всё из-за пары пикселей.
            w.Width  = Math.Clamp(ww, w.MinWidth, maxW);
            w.Height = Math.Clamp(hh, w.MinHeight, maxH);

            // Место принимаем, только если оно ещё на экране: монитор могли
            // отключить, и окно уехало бы туда, откуда его не достать мышью.
            if (OnScreen(l, t))
            {
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = l;
                w.Top  = t;
            }
        };

        // Сохраняем при закрытии: ловить каждое движение незачем, а закрытие
        // случается ровно тогда, когда человек уже поставил окно как хотел.
        if (remember is not null)
            w.Closing += (_, _) =>
            {
                // У свёрнутого и развёрнутого окна Left/Top врут (у свёрнутого
                // это −32000). Спрашиваем размер, с которым оно вернётся.
                var r = w.WindowState == WindowState.Normal
                    ? new Rect(w.Left, w.Top, w.Width, w.Height)
                    : w.RestoreBounds;
                if (double.IsNaN(r.Width) || r.Width <= 0 || r.Height <= 0) return;
                if (!OnScreen(r.X, r.Y)) return;
                remember.Write(r.X, r.Y, r.Width, r.Height);
            };
    }

    /// <summary>
    /// Виден ли левый верхний угол настолько, чтобы окно можно было схватить.
    ///
    /// Меряем по ВСЕМ мониторам, а не по рабочей области основного: у второго
    /// монитора слева координаты отрицательные, и проверка по основному экрану
    /// отказывалась бы запоминать совершенно нормальное место.
    /// </summary>
    private static bool OnScreen(double l, double t)
    {
        var left   = SystemParameters.VirtualScreenLeft;
        var top    = SystemParameters.VirtualScreenTop;
        var right  = left + SystemParameters.VirtualScreenWidth;
        var bottom = top  + SystemParameters.VirtualScreenHeight;
        return l > left - 50 && l < right - 100 &&
               t > top  - 50 && t < bottom - 80;
    }
}
