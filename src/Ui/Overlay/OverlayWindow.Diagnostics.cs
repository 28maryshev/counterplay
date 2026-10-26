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

// Окно драфта — «Сохранить для драфта», копия журнала и снимок состояния для него,
// строка в журнал при смене мониторов.
public partial class OverlayWindow
{
    // ── «Сохранить для драфта» (только тестовый режим) ───────────────────────
    private DispatcherTimer? _saveHintTimer;

    /// Показывает на экране Ready кнопку «Сохранить для драфта». В боевом режиме
    /// не вызывается — кнопка остаётся скрытой.
    public void EnableSaveForDraft() => Dispatcher.InvokeAsync(() =>
    {
        SaveForDraftBtn.Content    = Loc.T("pool.saveForDraft");
        SaveForDraftBtn.Visibility = Visibility.Visible;
        CopyLogBtn.Visibility      = Visibility.Visible;   // журнал — тоже только в тесте
    });

    /// Копирует хвост журнала в буфер: человеку остаётся вставить его в переписку,
    /// а мне — увидеть, что он менял и как программа на это отреагировала.
    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(Log.Tail(100));
            CopyLogBtn.Content = $"✓ {Log.Count} строк";
        }
        catch (Exception ex)
        {
            CopyLogBtn.Content = "буфер занят";
            Log.Write($"копирование журнала не удалось: {ex.Message}");
        }
        // Через пару секунд возвращаем обычную подпись.
        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        back.Tick += (_, _) => { back.Stop(); CopyLogBtn.Content = "📋 Логи"; };
        back.Start();
    }

    // Запоминает выбранный режим/пул (Обычный/Пул/Дуо) для драфта и применяет его
    // к текущему подбору. Кратко подтверждает и возвращает подпись.
    private void SaveForDraft_Click(object sender, RoutedEventArgs e)
    {
        PoolStore.Persist();
        RefreshPoolSlot();
        SaveForDraftBtn.Content = Loc.T("pool.saved");
        _saveHintTimer?.Stop();
        _saveHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _saveHintTimer.Tick += (_, _) =>
        {
            _saveHintTimer!.Stop();
            SaveForDraftBtn.Content = Loc.T("pool.saveForDraft");
        };
        _saveHintTimer.Start();
    }

    private static string Fmt(object? v) => v switch
    {
        bool b     => b ? "да" : "нет",
        double d   => d.ToString("0.##"),
        null       => "—",
        _          => v.ToString() ?? "—",
    };

    /// Всё, что нужно для разбора жалобы, без единого встречного вопроса:
    /// сборка, режим, язык, база, экран и DPI, окно клиента и выбранный по нему
    /// масштаб, положение нашего окна — и ВЕСЬ список настроек (изменённые от
    /// умолчаний помечены звёздочкой). Дальше идут события — но они без этой
    /// шапки почти бесполезны: одно и то же поведение при разных настройках
    /// разбирается по-разному.
    private string BuildSnapshot()
    {
        var sb = new StringBuilder();
        var s  = AppSettings.Current;

        sb.AppendLine($"── Counterplay v{Log.Version} · {DateTime.Now:dd.MM HH:mm:ss} ──");
        sb.AppendLine($"режим {Log.Mode} · язык {Loc.Current} · база {DataDb.CurrentBucket ?? "—"}");

        var (dpiX, dpiY) = Dpi();
        sb.AppendLine($"экран: мониторы {Monitors.Describe(WorkAreas())}, " +
                      $"весь стол {SystemParameters.VirtualScreenWidth:0}×{SystemParameters.VirtualScreenHeight:0}, " +
                      $"DPI ×{dpiX:0.00}");

        if (TryGetClientRect(out var r))
            sb.AppendLine($"клиент LoL: {(r.Right - r.Left) / dpiX:0}×{(r.Bottom - r.Top) / dpiY:0} " +
                          $"в ({r.Left / dpiX:0};{r.Top / dpiY:0}) → масштаб ×{ClientScale():0.00} " +
                          $"(настройка «{s.ClientSize}»)");
        else
            sb.AppendLine($"клиент LoL: окно не найдено → масштаб ×{ClientScale():0.00} " +
                          $"(настройка «{s.ClientSize}»)");

        var view = _inTray ? "трей"
                 : FullView.Visibility     == Visibility.Visible ? "драфт"
                 : CompactScroll.Visibility == Visibility.Visible ? "компактный" : "панель";
        sb.AppendLine($"окно: {ActualWidth:0}×{ActualHeight:0} в ({Left:0};{Top:0}), {MonitorNote()}, вид {view}, " +
                      $"масштаб ×{_loggedScale:0.00}, поверх всех {(Topmost ? "да" : "нет")}, " +
                      $"двигали руками {(_userMoved ? "да" : "нет")}");

        // Настройки целиком, а не только изменённые: если человек уверен, что
        // что-то выключил, а оно включено, это видно сразу.
        var def = new AppSettings();
        var parts = typeof(AppSettings).GetProperties()
            .Where(pi => pi.CanRead && pi.CanWrite)
            .Select(pi =>
            {
                var v = pi.GetValue(s);
                return (Equals(v, pi.GetValue(def)) ? "" : "*") + pi.Name + "=" + Fmt(v);
            }).ToList();

        sb.AppendLine("настройки (* — изменена):");
        for (int i = 0; i < parts.Count; i += 5)
            sb.AppendLine("  " + string.Join("  ", parts.Skip(i).Take(5)));

        return sb.ToString();
    }

    /// Подключили или сняли монитор, сменили разрешение — строка в журнал: с
    /// какими мониторами дальше живёт окно и где оно стоит. Без неё жалобу
    /// «окно прыгает между мониторами» по журналу не разобрать: шапка снимка
    /// показывает только стол на момент копирования.
    private void WatchMonitors()
    {
        void OnChanged(object? s, EventArgs e) => Dispatcher.InvokeAsync(() =>
            Log.Write($"мониторы изменились: {Monitors.Describe(WorkAreas())}; " +
                      $"окно в ({Left:0};{Top:0}), {MonitorNote()}"));
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnChanged;
    }
}
