using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

// Окно настроек — картинки к настройкам: навёл на строку — рядом рамка с
// кадрами «как с ней» и «как без неё», кадры сменяются плавно (владелец 19.11:
// «наводишь на "Компактная карточка ранга" — и видно, что на сайдбаре меняется»).
sealed partial class SettingsWindow
{
    /// Сколько держать мышь на строке до показа: проводишь через список — не мелькает.
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(250);

    /// Сколько стоит кадр и сколько длится переход.
    private static readonly TimeSpan FrameTime = TimeSpan.FromMilliseconds(1500);
    private static readonly Duration FadeTime = new(TimeSpan.FromMilliseconds(320));

    /// Язык картинок: русский — для русского, английский — для прочих. Набор
    /// на каждый из 15 языков весил бы в семь раз больше, а на кадре важно,
    /// ЧТО появилось, а не подпись (tests/SettingPreviews).
    private static string PreviewLang => Loc.Current == "ru" ? "ru" : "en";

    private static readonly Dictionary<string, ImageSource?> FrameCache = [];

    /// <summary>
    /// Кадры настройки по значениям: assets/previews/{язык}/{ключ}.{значение}.jpg,
    /// встроены в программу. Хоть одного нет — картинки у настройки нет (null):
    /// строка просто без рамки. Дымовая проверяет, что картинки доехали в сборку.
    /// </summary>
    internal static List<ImageSource>? LoadPreview(string key, IEnumerable<string> values)
    {
        var frames = new List<ImageSource>();
        foreach (var v in values)
        {
            // Сборка названа явно: «/assets/…» искал бы в запускающей сборке, а
            // проверки запускают свою.
            var uri = $"pack://application:,,,/{typeof(SettingsWindow).Assembly.GetName().Name};component/assets/previews/{PreviewLang}/{key}.{v}.jpg";
            if (!FrameCache.TryGetValue(uri, out var img))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(uri);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
                catch { img = null; }   // ресурса нет — у настройки нет картинки
                FrameCache[uri] = img;
            }
            if (img is null) return null;
            frames.Add(img);
        }
        return frames;
    }

    /// Подписи кадров тумблера.
    private static (string Value, string Label)[] OnOffFrames =>
        [("on", Loc.T("settings.previewOn")), ("off", Loc.T("settings.previewOff"))];

    /// <summary>
    /// Повесить картинку на строку настройки: <paramref name="key"/> — имя поля
    /// AppSettings, <paramref name="frames"/> — значения с подписями (нет —
    /// вкл/выкл). Картинки нет — строка как была.
    /// </summary>
    private UIElement Previewed(UIElement row, string key, (string Value, string Label)[]? frames = null)
    {
        frames ??= OnOffFrames;
        if (row is not FrameworkElement fe || LoadPreview(key, frames.Select(f => f.Value)) is null) return row;
        // Ловить мышь всей строкой, а не только текстом и тумблером.
        if (fe is Panel { Background: null } panel) panel.Background = System.Windows.Media.Brushes.Transparent;
        fe.MouseEnter += (_, _) => SchedulePreview(fe, key, frames);
        fe.MouseLeave += (_, _) => HidePreview();
        return row;
    }

    private Popup? _preview;
    private DispatcherTimer? _previewWait, _previewTick;

    private void SchedulePreview(FrameworkElement row, string key, (string Value, string Label)[] frames)
    {
        _previewWait?.Stop();
        _previewWait = new DispatcherTimer { Interval = PreviewDelay };
        _previewWait.Tick += (_, _) => { _previewWait?.Stop(); ShowPreview(row, key, frames); };
        _previewWait.Start();
    }

    private void HidePreview()
    {
        _previewWait?.Stop();
        _previewTick?.Stop();
        if (_preview is not null) _preview.IsOpen = false;
    }

    /// Рамка справа от окна, верхом вровень со строкой (у края экрана WPF
    /// сам сдвигает её внутрь). Мышь не ловит: под ней может оказаться
    /// соседняя строка, и рамка не должна её закрывать от наведения.
    private void ShowPreview(FrameworkElement row, string key, (string Value, string Label)[] frames)
    {
        var images = LoadPreview(key, frames.Select(f => f.Value));
        if (images is null || !row.IsMouseOver) return;
        var (box, layers, caption) = PreviewBox(images, frames);

        _preview ??= new Popup
        {
            AllowsTransparency = true, Placement = PlacementMode.Right, IsHitTestVisible = false,
            PopupAnimation = PopupAnimation.Fade, StaysOpen = true,
        };
        _preview.IsOpen = false;
        _preview.PlacementTarget = row;
        // От правого края строки до края окна — поле страницы (20) и рамка.
        _preview.HorizontalOffset = 30;
        _preview.VerticalOffset = -10;
        _preview.Child = box;
        _preview.IsOpen = true;

        // Смена кадров — только пока рамка открыта: окно в покое кадров не рисует.
        _previewTick?.Stop();
        if (layers.Count < 2) return;
        var cur = 0;
        _previewTick = new DispatcherTimer { Interval = FrameTime };
        _previewTick.Tick += (_, _) =>
        {
            var next = (cur + 1) % layers.Count;
            layers[cur].BeginAnimation(OpacityProperty, new DoubleAnimation(0, FadeTime));
            layers[next].BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeTime));
            caption(next);
            cur = next;
        };
        _previewTick.Start();
    }

    /// <summary>
    /// Содержимое рамки: подпись кадра с точкой и кадры стопкой (виден
    /// первый). Отдельно от показа — так рамку можно нарисовать в картинку, не
    /// открывая всплывающее окно (оно встало бы у края экрана видимым).
    /// </summary>
    internal static (Border Box, List<Image> Layers, Action<int> Caption) PreviewBox(
        List<ImageSource> images, (string Value, string Label)[] frames)
    {
        var stack = new Grid();
        var layers = images.Select((src, i) => new Image
        {
            Source = src, Stretch = Stretch.Uniform, Opacity = i == 0 ? 1 : 0,
            MaxWidth = 480, MaxHeight = 640,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        }).ToList();
        foreach (var l in layers) stack.Children.Add(l);
        RenderOptions.SetBitmapScalingMode(stack, BitmapScalingMode.HighQuality);

        var dot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 7, 0),
                                                       VerticalAlignment = VerticalAlignment.Center };
        var caption = new TextBlock
        {
            FontFamily = Font("UiFont"), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Text), VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(dot);
        head.Children.Add(caption);
        void Caption(int i)
        {
            caption.Text = frames[i].Label;
            // Вкл и выбранные значения — бирюзой, как тумблер; выкл — серой.
            dot.Fill = new SolidColorBrush(frames[i].Value == "off" ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
                                                                     : Color.FromRgb(0x36, 0xD6, 0xE7));
        }
        Caption(0);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(stack);
        var box = new Border
        {
            Background = new SolidColorBrush(Bg), Padding = new Thickness(10),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0xC8, 0x9B, 0x3C)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Child = body,
        };
        return (box, layers, Caption);
    }
}
