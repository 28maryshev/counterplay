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

// Окно драфта — слежение за окном клиента LoL (Win32): найти его, встать рядом,
// держать порядок окон.
public partial class OverlayWindow
{
    // Win32 для resize без оконного хрома
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    // Win32 для поиска окна клиента LoL и его расположения
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr h); // окно свёрнуто в панель задач

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow(); // активное окно — для z-порядка

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10;

    private struct RECT { public int Left, Top, Right, Bottom; }

    // Находит окно клиента LoL (champ select) — заголовок "League of Legends",
    // класс RCLIENT/RiotWindowClass. Возвращает его прямоугольник в пикселях.
    private static bool TryGetClientRect(out RECT rect)
    {
        RECT found = default;
        var ok = false;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(h, title, 256);
            if (!title.ToString().StartsWith("League of Legends", StringComparison.Ordinal)) return true;
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            var c = cls.ToString();
            if (c is "RCLIENT" or "RiotWindowClass" && GetWindowRect(h, out var r) && r.Right - r.Left > 400)
            {
                found = r; ok = true; return false; // нашли — останавливаем перебор
            }
            return true;
        }, IntPtr.Zero);
        rect = found;
        return ok;
    }

    // Ставит ЛЕВЫЙ верхний угол оверлея к ПРАВОМУ верхнему углу клиента
    // (оверлей появляется справа от окна клиента, не перекрывая его).
    private void AnchorToClient()
    {
        if (SandboxMode) return;
        if (!TryGetClientRect(out var r)) return;

        // Win32 отдаёт пиксели устройства; WPF Left/Top — в DIP. Учитываем DPI.
        double dpiX = 1, dpiY = 1;
        var src = System.Windows.PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is { } ct)
        {
            dpiX = ct.TransformToDevice.M11;
            dpiY = ct.TransformToDevice.M22;
        }
        Left = r.Right / dpiX;
        Top  = r.Top  / dpiY;
        LimitHeightToClient();
        ClampToScreen();   // клиент у правого края — окно не должно уехать за экран
    }

    // ── Слежение за окном клиента LoL ─────────────────────────────────────
    // Оверлей повторяет поведение окна клиента: свернули клиент → оверлей в трей,
    // развернули → вернулся и приклеился сбоку, передвинули клиент → переклеился.
    // Ручное перетаскивание оверлея (_userMoved) снимает только переклейку,
    // но сворачивание/возврат в трей по клиенту продолжает работать.

    private DispatcherTimer? _followTimer;

    private RECT _lastClientRect;

    private bool _followHidden; // в трей убрал именно фолловер (клиент свёрнут/закрыт)

    // Идёт драфт, а подбор выключен настройкой: окно должно остаться в трее.
    // Без этого фолловер поднимал бы его обратно на следующем же тике.
    private bool _draftSuppressed;

    private bool _clientSeen;   // клиент хоть раз был найден в этой сессии

    // Находит окно клиента (RCLIENT) даже свёрнутым — без фильтра по размеру.
    private static bool TryFindClientWindow(out IntPtr hwnd)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var title = new System.Text.StringBuilder(256);
            GetWindowText(h, title, 256);
            if (!title.ToString().StartsWith("League of Legends", StringComparison.Ordinal)) return true;
            var cls = new System.Text.StringBuilder(256);
            GetClassName(h, cls, 256);
            if (cls.ToString() is "RCLIENT" or "RiotWindowClass")
            {
                found = h; return false;
            }
            return true;
        }, IntPtr.Zero);
        hwnd = found;
        return found != IntPtr.Zero;
    }

    private void StartWindowFollow()
    {
        if (_followTimer != null) return;
        _followTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _followTimer.Tick += (_, _) => FollowClientWindow();
        _followTimer.Start();
    }

    private void FollowClientWindow()
    {
        if (SandboxMode) return;   // песочнице клиент не указ
        if (!TryFindClientWindow(out var h))
        {
            // Клиент закрыт. Если он уже был открыт в этой сессии (человек наигрался
            // и закрыл лигу) — сворачиваем оверлей в трей. До первого появления
            // клиента (старт приложения) — не трогаем, показываем экран ожидания.
            if (_clientSeen && !_inTray && !_userHidden)
            {
                _followHidden = true;
                HideToTray();
            }
            return;
        }
        _clientSeen = true;

        if (IsIconic(h))
        {
            // Клиент свёрнут → прячем оверлей в трей (если не убран игрой/вручную).
            if (!_inTray && !_userHidden)
            {
                _followHidden = true;
                HideToTray();
            }
            return;
        }

        // Клиент развёрнут.
        if (_followHidden)
        {
            // Возвращаем именно то, что прятал фолловер, и фиксируем сбоку.
            _followHidden = false;
            if (_draftSuppressed) return;   // подбор выключен и идёт драфт — остаёмся в трее
            RestoreFromTray();   // не развернёт, если свёрнуто вручную крестиком
            AnchorIfNotMoved();  // приклеит сбоку, если оверлей не двигали руками
            return;
        }

        // Фолбэк надёжности: клиент открыт и активен, а оверлей завис в трее (например,
        // после игры проскочило событие геймфлоу, или трей-показ обогнала гонка) —
        // возвращаем окно. Условие _lcuReady КРИТИЧНО: без него при автозапуске окно
        // вылазило бы из трея с экраном «ожидание клиента», едва появится окно клиента,
        // хотя LCU ещё не подключён. Не трогаем во время игры и при ручном скрытии.
        if (_inTray && !_userHidden && !_gameActive && !_draftSuppressed && _lcuReady)
        {
            RestoreFromTray();
            AnchorIfNotMoved();
            return;
        }

        // Z-порядок: когда активно окно клиента — уводим оверлей на второй план, чтобы
        // на маленьких экранах он не перекрывал весь клиент. Клик по оверлею вернёт его.
        if (!_inTray) UpdateZOrder(h);

        // Клиент передвинули/изменили размер → переклеиваемся к его краю.
        if (!_inTray && !_userMoved && GetWindowRect(h, out var r))
        {
            if (r.Left != _lastClientRect.Left || r.Top != _lastClientRect.Top ||
                r.Right != _lastClientRect.Right || r.Bottom != _lastClientRect.Bottom)
            {
                _lastClientRect = r;
                // Клиент реально переехал или сменил размер. Раскладки «поверх»
                // считаются от его прямоугольника — пересчитываем их. «Где
                // оставили» не трогаем: это абсолютные координаты, к клиенту они
                // не привязаны.
                if (_lastDraft is not null
                    && AppSettings.Current.DraftPlacement is not ("right" or "remember"))
                {
                    _placementApplied = false;
                    ApplyDraftPlacement();
                }
                else AnchorIfNotMoved();
            }
        }
    }

    // Оверлей «всегда сверху» только когда активен ОН САМ. Во всех прочих случаях
    // (в фокусе клиент ИЛИ постороннее приложение) снимаем Topmost и ставим окно
    // вплотную под клиент в z-порядке — тогда оверлей следует за клиентом: другое
    // окно перекрыло клиент → оно перекрывает и оверлей (раньше плашка ожидания
    // висела поверх всего). Клик по оверлею делает его активным и возвращает поверх.
    private void UpdateZOrder(IntPtr clientHwnd)
    {
        // «Всегда поверх» из настроек: не опускаем окно под клиент вообще.
        if (AppSettings.Current.AlwaysOnTop)
        {
            if (!Topmost) Topmost = true;
            return;
        }
        if (GetForegroundWindow() == Hwnd)
        {
            if (!Topmost) Topmost = true;           // наш оверлей активен → поверх
            return;
        }
        // Открыты настройки — оверлей остаётся на виду: человек меняет тумблеры,
        // чтобы смотреть на результат, а не на окно клиента.
        if (_settingsWin is { IsVisible: true })
        {
            if (!Topmost) Topmost = true;
            return;
        }
        // Идёт драфт — держимся поверх клиента: подсказки нужны именно сейчас, а
        // уходя под окно клиента, оверлей прятал бы их ровно в тот момент, когда
        // человек кликает по чемпионам.
        if (_lastDraft is not null)
        {
            if (!Topmost) Topmost = true;
            return;
        }
        if (Topmost) Topmost = false;               // снимаем «всегда сверху»…
        SetWindowPos(Hwnd, clientHwnd, 0, 0, 0, 0,  // …и опускаем ровно под окно клиента
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }
}
