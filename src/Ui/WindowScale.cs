using System.Windows;
using System.Windows.Media;

namespace Counterplay;

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
    /// </summary>
    public static void Apply(Window w, FrameworkElement root,
                             double baseW, double baseH, double minW, double minH)
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
        };
    }
}
