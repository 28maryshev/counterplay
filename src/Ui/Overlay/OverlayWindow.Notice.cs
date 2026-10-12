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

// Окно драфта — сообщения с сайта на плашке и разовое уведомление об автозапуске.
public partial class OverlayWindow
{
    // ── Автозапуск: разовое уведомление после включения ──────────────────────

    /// Показать плашку «программа теперь запускается с Windows» (один раз).
    public void ShowAutostartNotice() => Dispatcher.InvokeAsync(() =>
    {
        AutostartNoticeText.Text = Loc.T("autostart.notice");
        AutostartOffBtn.Content  = Loc.T("autostart.disable");
        AutostartOkBtn.Content   = Loc.T("autostart.ok");
        AutostartNotice.Visibility = Visibility.Visible;
    });

    private void AutostartOff_Click(object sender, RoutedEventArgs e)
    {
        Autostart.Set(false);
        Settings.Set("autostart", false);
        AutostartNotice.Visibility = Visibility.Collapsed;
        // Синхронизируем галочку в трее (без пересоздания иконки).
        if (_autostartItem != null) _autostartItem.Checked = false;
    }

    private void AutostartOk_Click(object sender, RoutedEventArgs e) =>
        AutostartNotice.Visibility = Visibility.Collapsed;

    // Клик по чипу языка — выпадающий список в стиле выбора очереди.

    // Язык сменился: обновляем UI сразу, имена чемпионов — после дозагрузки Data Dragon.
    // ── Сообщение с сайта на плашке «BETA» ────────────────────────────────
    //
    // Плашка — единственное место, где мы можем сказать игроку что-то прямо в
    // программе. Текст лежит на сайте: если встал сбор или вышел патч, человек
    // узнает об этом, не обновляя программу.
    private DispatcherTimer? _noticeTimer;

    private static readonly SolidColorBrush BetaGoldBrush  = new(Color.FromRgb(0xFF, 0xE0, 0x66));

    private static readonly SolidColorBrush BetaAlertBrush = new(Color.FromRgb(0xFF, 0x6A, 0x5D));

    private static readonly SolidColorBrush BetaCardGold   = new(Color.FromArgb(0x33, 0xC8, 0x9B, 0x3C));

    private static readonly SolidColorBrush BetaCardAlert  = new(Color.FromArgb(0x66, 0xFF, 0x5A, 0x4D));

    private static readonly SolidColorBrush BetaFillAlert  = new(Color.FromArgb(0x18, 0xFF, 0x5A, 0x4D));

    private static readonly SolidColorBrush BetaFillNormal = new(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

    private DispatcherTimer? _noticeRotate;

    private int _noticeIndex;

    private void StartNoticeWatch()
    {
        Notice.LoadCached();   // показать сохранённое до того, как ответит сеть
        ApplyNotice();
        _ = RefreshNoticeAsync();

        // Полчаса: сообщение — не срочный сигнал, а запросов иначе набежит много.
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _noticeTimer.Tick += async (_, _) => await RefreshNoticeAsync();
        _noticeTimer.Start();
    }

    private async Task RefreshNoticeAsync()
    {
        if (!await Notice.RefreshAsync()) return;
        await Dispatcher.InvokeAsync(() =>
        {
            _noticeIndex = 0;   // набор сменился — показываем с начала
            ApplyNotice();
        });
    }

    // Сообщений может быть несколько: показываем по одному, сменяя по таймеру.
    // Одно сообщение — таймер не нужен, пусть висит.
    private int _noticeCount = -1;

    private void RestartNoticeRotation(int count)
    {
        // Перерисовка идёт и по тику таймера — заводить его заново каждый раз
        // незачем, иначе отсчёт сбрасывается сам о себя.
        if (count == _noticeCount) return;
        _noticeCount = count;

        _noticeRotate?.Stop();
        if (count < 2) { _noticeRotate = null; return; }

        _noticeRotate = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Notice.RotateSeconds)
        };
        _noticeRotate.Tick += (_, _) =>
        {
            _noticeIndex++;
            ApplyNotice();
        };
        _noticeRotate.Start();
    }

    /// Набор, под который уже посчитана высота плашки. Меняется вместе с
    /// сообщениями и с языком — пересчитываем только тогда, а не каждый тик.
    private string _noticeSig = "";

    private void ApplyNotice()
    {
        var list = Notice.Active();
        if (list.Count == 0) _noticeIndex = 0;
        var n = list.Count == 0 ? null : list[_noticeIndex % list.Count];
        RestartNoticeRotation(list.Count);

        RenderNotice(n);
        FitNoticeCard(list);
    }

    /// <summary>
    /// Плашка держит высоту самого длинного из АКТИВНЫХ сообщений.
    ///
    /// Сообщения сменяются по кругу сами, и высота панели прыгала вместе с
    /// ними: короткое — сайдбар поджался, длинное — вырос. Человек в этот
    /// момент ничего не делает, а окно под ним дёргается.
    ///
    /// Меряем ТЕКСТ, а не плашку целиком. Живую плашку измерить не вышло: WPF
    /// отдаёт размер, посчитанный для прежнего текста, сколько ни сбрасывай
    /// мерку вручную — длинный абзац выходил в 76 точек вместо 145. Поэтому
    /// текст меряется отдельным блоком с той же типографикой, а плашке задаётся
    /// минимум по САМОМУ тексту: всё остальное в ней от сообщения не зависит.
    ///
    /// Кроме строки ссылки — она есть не у всех. Если ссылка есть хоть у
    /// одного, у остальных прячем её местом, а не выбрасываем: иначе высота
    /// прыгала бы на эту строку.
    /// </summary>
    private void FitNoticeCard(IReadOnlyList<Notice.Item> list)
    {
        if (list.Count < 2)
        {
            BetaText.MinHeight = 0;
            _noticeSig = "";
            _noticeKeepLink = false;
            return;
        }

        var sig = string.Join("", list.Select(i => i.Text + "|" + i.Head + "|" + i.Link));
        if (sig == _noticeSig) return;
        _noticeSig = sig;

        // Ширина, которая достаётся тексту: плашка минус её поля и рамка.
        var inner = BetaCard.Width
                  - BetaCard.Padding.Left - BetaCard.Padding.Right
                  - BetaCard.BorderThickness.Left - BetaCard.BorderThickness.Right;

        var ruler = new TextBlock
        {
            FontFamily    = BetaText.FontFamily,
            FontSize      = BetaText.FontSize,
            FontWeight    = BetaText.FontWeight,
            LineHeight    = BetaText.LineHeight,
            LineStackingStrategy = BetaText.LineStackingStrategy,
            TextWrapping  = TextWrapping.Wrap,
            TextAlignment = BetaText.TextAlignment,
            Width         = inner,
        };

        var tallest = 0.0;
        foreach (var item in list)
        {
            ruler.Text = item.Text;
            ruler.Measure(new Size(inner, double.PositiveInfinity));
            tallest = Math.Max(tallest, ruler.DesiredSize.Height);
        }
        BetaText.MinHeight = tallest;

        // Ссылка есть хоть у одного — место под неё держим у всех.
        _noticeKeepLink = list.Any(i => !string.IsNullOrWhiteSpace(i.Link));
        RenderNotice(list[_noticeIndex % list.Count]);
    }

    /// Держать ли место под строку ссылки у сообщений без неё.
    private bool _noticeKeepLink;

    /// Выставить содержимое плашки под одно сообщение. null — плашка про бету.
    private void RenderNotice(Notice.Item? n)
    {
        var alert = n?.Kind == "alert";

        // Нет сообщения — обычный текст «пишите в поддержку».
        BetaText.Text = n?.Text ?? Loc.T("ready.beta");
        // Одного восклицательного знака мало — предупреждение должно бросаться в
        // глаза: слово целиком и по треугольнику с каждой стороны.
        // Заголовок плашки: у неполадки всегда «ВНИМАНИЕ», у обычного сообщения —
        // тот, что выбрали при отправке (новое, важно, совет, обновление). Без
        // сообщения остаётся «BETA», как было.
        BetaBadge.Text = alert
            ? Loc.T("ready.alert")
            : n is null ? "BETA" : Loc.T("notice.head." + n.Head);
        // «BETA» — короткая метка и остаётся крупной. Слово-заголовок сообщения
        // длиннее («Обновление»), и в том же размере оно давит текст под собой.
        BetaBadge.FontSize = alert ? 17 : n is null ? 21 : 15;
        SetNoticeMark(alert ? "alert" : n?.Head ?? "");
        BetaBadge.Foreground = alert ? BetaAlertBrush : BetaGoldBrush;
        BetaGlow.Color = alert ? Color.FromRgb(0xFF, 0x5A, 0x4D) : Color.FromRgb(0xFF, 0xD7, 0x5A);
        BetaCard.BorderBrush = alert ? BetaCardAlert : BetaCardGold;
        BetaCard.Background  = alert ? BetaFillAlert : BetaFillNormal;

        // Ссылка — только та, что задана у сообщения. Раньше вместо пустой
        // подставлялась поддержка: новость про Discord заканчивалась строкой
        // «counterplays.com/support», и человек шёл не туда, куда зовут.
        //
        // Без сообщения плашка своя, про бету, и просьба писать в поддержку в
        // ней и живёт — там ссылка остаётся.
        var link = n is null ? "https://counterplays.com/support" : n.Link;
        if (string.IsNullOrWhiteSpace(link))
        {
            // Hidden, а не Collapsed, когда ссылка есть у соседнего сообщения:
            // строка исчезает, а место под неё остаётся, и высота не прыгает.
            BetaLinkLine.Visibility = _noticeKeepLink ? Visibility.Hidden : Visibility.Collapsed;
        }
        else
        {
            BetaLinkLine.Visibility = Visibility.Visible;
            BetaLink.NavigateUri = new Uri(link);
            BetaLinkText.Text = link.Replace("https://", "").TrimEnd('/');
        }
    }

    /// Знак по бокам заголовка плашки — свой у каждого вида сообщения. Слово
    /// между двумя значками читается как метка, а не как случайная строка: так
    /// уже сделано у неполадки, и остальным заголовкам это нужно не меньше.
    ///
    /// Геометрии нарисованы в коробке 21×19 — той же, что у треугольника
    /// неполадки, — и векторами, а не символами шрифта: нужного знака в
    /// системном шрифте может не оказаться, и вместо метки игрок увидел бы
    /// пустой квадрат. «BETA» в списке нет намеренно: это не сообщение, а
    /// состояние программы, и знаков ему не нужно.
    private static readonly Dictionary<string,
        (string Data, string Fill, string Stroke, string Glyph, double GlyphTop)>
        NoticeMarks = new()
        {
            // Неполадка — красный треугольник с восклицательным знаком.
            ["alert"] = ("M10.5,0.8 L20.4,18 L0.6,18 Z", "#FF4438", "#FFE8E6", "!", 4),
            // Важное — тот же знак, но синий: важное не равно поломке, и цвет
            // тревоги на нём обесценивал бы саму тревогу.
            ["important"] = ("M10.5,0.8 L20.4,18 L0.6,18 Z", "#3A7BD5", "#CFE4FF", "!", 4),
            // Новинка — искра.
            ["new"] = ("M10.5,0.5 L12.7,7.3 L19.5,9.5 L12.7,11.7 L10.5,18.5 "
                       + "L8.3,11.7 L1.5,9.5 L8.3,7.3 Z", "#FFE066", "#FFF4C0", "", 0),
            // Совет — лампочка: колба и цоколь двумя фигурами.
            ["tip"] = ("M10.5,1.2 C6.8,1.2 3.9,4.1 3.9,7.6 C3.9,10.1 5.4,11.8 6.5,13.2 "
                       + "L6.5,15 L14.5,15 L14.5,13.2 C15.6,11.8 17.1,10.1 17.1,7.6 "
                       + "C17.1,4.1 14.2,1.2 10.5,1.2 Z M7.3,16.3 L13.7,16.3 L13.7,18 L7.3,18 Z",
                       "#36D6E7", "#BFF3F9", "", 0),
            // Вопрос — круг со знаком вопроса: спрашиваем игроков, а не сообщаем им.
            ["question"] = ("M2.1,9.5 A8.4,8.4 0 1 0 18.9,9.5 A8.4,8.4 0 1 0 2.1,9.5 Z",
                            "#36D6E7", "#BFF3F9", "?", 0),
            // Обновление — стрелка вверх: версия растёт, а не скачивается.
            ["update"] = ("M10.5,1.2 L16.8,9.2 L12.3,9.2 L12.3,17.6 L8.7,17.6 L8.7,9.2 L4.2,9.2 Z",
                          "#36D6E7", "#BFF3F9", "", 0),
        };

    private static readonly Dictionary<string, Brush> MarkBrushes = new();

    private static readonly Dictionary<string, Geometry> MarkShapes = new();

    private static Brush MarkBrush(string hex)
    {
        if (MarkBrushes.TryGetValue(hex, out var b)) return b;
        var brush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return MarkBrushes[hex] = brush;
    }

    private static Geometry MarkShape(string data)
    {
        if (MarkShapes.TryGetValue(data, out var g)) return g;
        var geo = Geometry.Parse(data);
        geo.Freeze();
        return MarkShapes[data] = geo;
    }

    private void SetNoticeMark(string key)
    {
        if (!NoticeMarks.TryGetValue(key, out var m))
        {
            BetaWarnLeft.Visibility = BetaWarnRight.Visibility = Visibility.Collapsed;
            return;
        }

        BetaMarkLeft.Data = BetaMarkRight.Data = MarkShape(m.Data);
        BetaMarkLeft.Fill = BetaMarkRight.Fill = MarkBrush(m.Fill);
        BetaMarkLeft.Stroke = BetaMarkRight.Stroke = MarkBrush(m.Stroke);
        // Знак внутри фигуры: восклицательный у треугольника, вопросительный у
        // круга. У искры, лампы и стрелки его нет — фигура говорит сама.
        BetaBangLeft.Text = BetaBangRight.Text = m.Glyph;
        BetaBangLeft.Margin = BetaBangRight.Margin = new Thickness(0, m.GlyphTop, 0, 0);
        BetaBangLeft.Visibility = BetaBangRight.Visibility =
            m.Glyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BetaWarnLeft.Visibility = BetaWarnRight.Visibility = Visibility.Visible;
    }
}
