using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

public partial class OverlayWindow : Window
{
    private bool   _isFullMode = true;
    private const double FullW    = 1320;
    private const double FullH    = 680;
    private const double CompactW = 320;
    private const double IdleW    = 340;   // компактное окно режима ожидания
    private const double MinW     = 1240;
    private const double MinH     = 200;

    // Запоминаем размер полного режима при переключении в компактный
    private double _savedFullW = FullW;
    private double _savedFullH = FullH;

    private IReadOnlyList<Recommendation>? _lastRecs;
    private IReadOnlyList<BanRec>?         _lastBans;
    private DraftState?                    _lastDraft;    // с применёнными ручными ролями
    private DraftState?                    _lastRawDraft; // как пришёл из LCU
    private RecommendationEngine?          _engine;

    // Ручное назначение ролей врагам (cellId → LCU-позиция): игрок знает, куда
    // пойдёт флекс-пик (Ирелия мид и т.п.) — клик по роли в карточке врага.
    private readonly Dictionary<int, string> _enemyRoleOverrides = new();
    private static readonly string[] RoleCycle = ["", "top", "jungle", "middle", "bottom", "utility"];

    // Привязка к окну клиента LoL: ставим один раз при появлении, дальше
    // окно свободно перетаскивается. Как только пользователь сдвинул вручную —
    // больше не привязываем (до нажатия кнопки-пина).
    private bool _userMoved;

    // Сворачивание в системный трей на время игры.
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _autostartItem; // галочка «запускать с Windows»
    private bool _inTray;
    // true = свёрнуто пользователем (крестик) — автоматический возврат не разворачивает.
    private bool _userHidden;
    // true = идёт игра / вход в игру: оверлей специально скрыт, авто-показ подавлен.
    // Управляется из геймфлоу (Program). В меню/лобби/чемп-селекте — false.
    private bool _gameActive;

    /// Геймфлоу сообщает, идёт ли игра (или вход в неё). В это время авто-возврат
    /// оверлея из трея по слежению за окном подавлен — иначе окно всплыло бы поверх игры.
    public void SetGameActive(bool active) => _gameActive = active;

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
    }

    // Привязка только при появлении и пока пользователь не двигал окно сам.
    private void AnchorIfNotMoved()
    {
        if (!_userMoved) AnchorToClient();
    }

    // ── Слежение за окном клиента LoL ─────────────────────────────────────
    // Оверлей повторяет поведение окна клиента: свернули клиент → оверлей в трей,
    // развернули → вернулся и приклеился сбоку, передвинули клиент → переклеился.
    // Ручное перетаскивание оверлея (_userMoved) снимает только переклейку,
    // но сворачивание/возврат в трей по клиенту продолжает работать.

    private DispatcherTimer? _followTimer;
    private RECT _lastClientRect;
    private bool _followHidden; // в трей убрал именно фолловер (клиент свёрнут/закрыт)
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
            RestoreFromTray();   // не развернёт, если свёрнуто вручную крестиком
            AnchorIfNotMoved();  // приклеит сбоку, если оверлей не двигали руками
            return;
        }

        // Фолбэк надёжности: клиент открыт и активен, а оверлей завис в трее (например,
        // после игры проскочило событие геймфлоу, или трей-показ обогнала гонка) —
        // возвращаем окно. Не трогаем, если идёт игра (спец. скрытие) или пользователь
        // свернул вручную крестиком.
        if (_inTray && !_userHidden && !_gameActive)
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
                AnchorToClient();
            }
        }
    }

    // Клиент лиги в фокусе → оверлей уходит ПОД окно клиента (на маленьких экранах он
    // иначе перекрывает весь клиент). Клик по оверлею возвращает его поверх. Прочие
    // окна в фокусе — оверлей остаётся поверх, как и раньше.
    private void UpdateZOrder(IntPtr clientHwnd)
    {
        if (GetForegroundWindow() == clientHwnd)
        {
            if (Topmost) Topmost = false;           // снимаем «всегда сверху»…
            SetWindowPos(Hwnd, clientHwnd, 0, 0, 0, 0, // …и опускаем ровно под окно клиента
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        else if (!Topmost) Topmost = true;          // оверлей/иное окно активно → снова поверх
    }

    // ── Системный трей (скрытие на время игры) ────────────────────────────

    private void EnsureTray()
    {
        if (_tray != null) return;
        System.Drawing.Icon trayIcon;
        // Грузим иконку трея в малом системном размере (чётче 16×16, берёт нужный кадр ICO).
        try
        {
            trayIcon = File.Exists(LogoPath)
                ? new System.Drawing.Icon(LogoPath, System.Windows.Forms.SystemInformation.SmallIconSize)
                : System.Drawing.SystemIcons.Application;
        }
        catch { trayIcon = System.Drawing.SystemIcons.Application; }
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon    = trayIcon,
            Text    = "Counterplay",
            Visible = false,
        };
        // Восстановление по двойному клику / меню — принудительное (с ручного сворачивания).
        _tray.DoubleClick += (_, _) => RestoreFromTray(force: true);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray(force: true));

        // Автозапуск с Windows — переключатель (только в установленной версии).
        if (Autostart.Supported)
        {
            _autostartItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray.autostart"))
            {
                CheckOnClick = true,
                Checked      = Autostart.IsEnabled,
            };
            _autostartItem.Click += (_, _) =>
            {
                var on = Autostart.Set(_autostartItem.Checked);
                Settings.Set("autostart", on);
                _autostartItem.Checked = on; // фактическое состояние (реестр мог не поддаться)
            };
            menu.Items.Add(_autostartItem);
        }

        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => System.Windows.Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;

        Closed += (_, _) => { _tray?.Dispose(); _tray = null; };
    }

    /// Старт сразу в трее (автозапуск с Windows): окно не показываем вовсе —
    /// иначе при входе в систему выскакивает «LCU is starting…». Оверлей сам
    /// поднимется, когда появится клиент (RestoreFromTray из фолловера/геймфлоу).
    /// HWND создаём явно: позиционирование и слежение за окном клиента опираются
    /// на него, а без Show() он бы не появился.
    public void StartInTray()
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        EnsureTray();
        _inTray = true;
        _tray!.Visible = true;
    }

    /// Свернуть оверлей в трей. userInitiated=true (крестик) — не разворачивать
    /// автоматически (только по клику в трее). Вызывается из фонового потока тоже.
    public void HideToTray(bool userInitiated = false) => Dispatcher.InvokeAsync(() =>
    {
        EnsureTray();
        if (userInitiated) _userHidden = true;
        if (_inTray) return;
        _inTray = true;
        _tray!.Visible = true;
        Hide(); // тихо, без всплывающего уведомления
    });

    /// Вернуть оверлей из трея. force=true — по явному действию пользователя
    /// (клик в трее). Автовозврат (force=false) не сработает, если пользователь
    /// свернул окно сам.
    public void RestoreFromTray(bool force = false) => Dispatcher.InvokeAsync(() =>
    {
        if (_userHidden && !force) return; // свёрнуто вручную — ждём действия пользователя
        _userHidden = false;
        if (!_inTray) return;
        _inTray = false;
        if (_tray != null) _tray.Visible = false;
        Show();
    });

    private IntPtr Hwnd => new System.Windows.Interop.WindowInteropHelper(this).Handle;

    private enum ResDir { Left=1, Right=2, Top=3, TopLeft=4, TopRight=5, Bottom=6, BottomLeft=7, BottomRight=8 }

    private void StartResize(ResDir dir, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        _userMoved = true; // ручной ресайз — фиксируем положение пользователя
        SendMessage(Hwnd, 0x0112 /*WM_SYSCOMMAND*/, new IntPtr(0xF000 /*SC_SIZE*/ + (int)dir), IntPtr.Zero);
        e.Handled = true;
    }

    // 8 обработчиков, по одному на каждое направление
    private void ResN (object s, MouseButtonEventArgs e) => StartResize(ResDir.Top,         e);
    private void ResS (object s, MouseButtonEventArgs e) => StartResize(ResDir.Bottom,      e);
    private void ResW (object s, MouseButtonEventArgs e) => StartResize(ResDir.Left,        e);
    private void ResE (object s, MouseButtonEventArgs e) => StartResize(ResDir.Right,       e);
    private void ResNW(object s, MouseButtonEventArgs e) => StartResize(ResDir.TopLeft,     e);
    private void ResNE(object s, MouseButtonEventArgs e) => StartResize(ResDir.TopRight,    e);
    private void ResSW(object s, MouseButtonEventArgs e) => StartResize(ResDir.BottomLeft,  e);
    private void ResSE(object s, MouseButtonEventArgs e) => StartResize(ResDir.BottomRight, e);

    // Путь к логотипу рядом с приложением (если положен).
    private static string LogoPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "assets", "logo.ico");

    public OverlayWindow()
    {
        InitializeComponent();

        // Логотип окна (если есть assets\logo.ico) — иначе стандартный.
        try { if (File.Exists(LogoPath)) Icon = new BitmapImage(new Uri(LogoPath)); }
        catch { /* битый ico — пропускаем */ }

        // Стартуем в полном режиме с явными размерами
        SizeToContent = SizeToContent.Manual;
        Width  = FullW;
        Height = FullH;
        MinWidth  = MinW;
        MinHeight = MinH;

        Left = SystemParameters.PrimaryScreenWidth - FullW - 24;
        Top  = 60;

        // Слежение за окном клиента (свернуть/развернуть/переместить).
        StartWindowFollow();

        // Чип языка: показываем текущий, меню — по клику.
        LangText.Text = Loc.CurrentLang.Native + " ▾";
        Loc.LanguageChanged += OnLanguageChanged;
    }

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
    private void LangChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        foreach (var lang in Loc.Languages)
        {
            var item = new MenuItem
            {
                Header    = lang.Native,
                IsChecked = lang.Code == Loc.Current,
                Style     = itemStyle,
            };
            var code = lang.Code;
            item.Click += (_, _) => Loc.SetLanguage(code);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = LangChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Язык сменился: обновляем UI сразу, имена чемпионов — после дозагрузки Data Dragon.
    private void OnLanguageChanged()
    {
        LangText.Text = Loc.CurrentLang.Native + " ▾";
        RenderCurrentState();         // мгновенно перерисовываем интерфейс

        // Тексты, выставляемые из кода: строка «Готов · фаза» и панель трекера
        // (ранг/W-L/подсказка) — перелокализуем из сохранённого сырья.
        ApplyReadyText();
        ShowSession(_session);

        _ = ReloadNamesAsync();       // имена чемпионов под новую локаль
    }

    private async Task ReloadNamesAsync()
    {
        try
        {
            await DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
            await Dispatcher.InvokeAsync(RenderCurrentState);
        }
        catch { /* офлайн — имена обновятся при следующей загрузке */ }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        _userMoved = true; // дальше окно стоит там, куда его поставил пользователь
        DragMove();
    }

    // Кнопка-пин: вернуть оверлей к правому краю окна клиента
    private void OnPin(object sender, RoutedEventArgs e)
    {
        _userMoved = false;
        AnchorToClient();
    }
    // Крестик сворачивает окно в трей (значок остаётся). Окно не вернётся само
    // при событиях LCU — только по двойному клику в трее. Полный выход — пункт
    // «Выход» в меню значка.
    private void OnClose(object sender, RoutedEventArgs e) => HideToTray(userInitiated: true);
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Hide();
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_isFullMode)
        {
            // Сохраняем текущие размеры перед сворачиванием
            _savedFullW = Width;
            _savedFullH = Height;

            _isFullMode = false;
            SizeToContent  = SizeToContent.Height; // компактный — высота по контенту
            Width          = CompactW;
            ToggleBtn.Content = "⊞";
            ToggleBtn.ToolTip = Loc.T("tip.expand");
        }
        else
        {
            _isFullMode = true;
            SizeToContent  = SizeToContent.Manual;
            Width          = _savedFullW;
            Height         = _savedFullH;
            MinWidth       = MinW;
            MinHeight      = MinH;
            ToggleBtn.Content = "⊟";
            ToggleBtn.ToolTip = Loc.T("tip.minimize");
        }

        RenderCurrentState();
    }

    // ── Вызывается из фонового потока (цикл событий LCU) ──────────────────
    // ВАЖНО: только InvokeAsync (неблокирующий). Блокирующий Invoke остановил бы
    // поток чтения WebSocket; при лавине событий (переход в игру) сокет LCU
    // перестал бы вычитываться, буфер клиента переполнялся бы и клиент зависал
    // («reconnect», игра не стартует). Никогда не делать здесь блокирующий вызов.

    public void ShowStatus(string msg) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = msg;
            DlBar.Visibility = Visibility.Collapsed;
            PulseAnim(false);
            SetLoadingMode();
            ShowIdle();
        });

    // Состояние строки «Готов»: сырая фаза LCU и/или произвольный текст — храним
    // сырьё, чтобы при смене языка перелокализовать на лету.
    private string? _readyPhaseRaw;  // фаза геймфлоу; null — просто «Готов»
    private string? _readyCustom;    // произвольная строка (тестовый режим)

    // Готов и простаивает: вместо «Готов» — сноска о программе + карусель советов.
    // status == null → локализованное «Готов» (перерисуется при смене языка).
    public void ShowReady(string? status = null) =>
        Dispatcher.InvokeAsync(() =>
        {
            _readyCustom = status;
            _readyPhaseRaw = null;
            ShowReadyCore();
        });

    // Готов с фазой геймфлоу (сырое имя из LCU — локализуется при рендере).
    public void ShowReadyPhase(string rawPhase) =>
        Dispatcher.InvokeAsync(() =>
        {
            _readyCustom = null;
            _readyPhaseRaw = rawPhase;
            ShowReadyCore();
        });

    private void ShowReadyCore()
    {
        ApplyReadyText();
        DlBar.Visibility = Visibility.Collapsed;
        PulseAnim(false);
        LoadingInfo.Visibility = Visibility.Collapsed;
        ReadyInfo.Visibility   = Visibility.Visible;
        ShowIdle();
    }

    private void ApplyReadyText() =>
        ReadyStatusText.Text = _readyCustom
            ?? (_readyPhaseRaw is null
                ? Loc.T("status.readyIdle")
                : Loc.T("status.readyPhase", PhaseDisplay(_readyPhaseRaw)));

    // Локализованное имя фазы геймфлоу (phase.* в i18n; неизвестная — как есть).
    private static string PhaseDisplay(string phase)
    {
        var key = "phase." + phase.ToLowerInvariant();
        var t = Loc.T(key);
        return t == key ? phase : t;
    }

    // ── Трекер сессии на экране ожидания ─────────────────────────────────────
    private SessionTracker.SessionData? _session;
    private SessionTracker.QueueView?   _sessionView; // выбранная очередь
    private string _selectedQueue = "";
    private bool _queueUserSet;   // выбрал руками в этой сессии — авто не перебивает
    private bool _wrChartHooked;

    private static readonly Brush WinBrush  = new SolidColorBrush(Color.FromRgb(0x57, 0xC9, 0x8A));
    private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B));
    private static readonly Brush MuteBrush = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));

    // Заполняет панель ранга/последних игр/винрейта. Вызывать из Program после RefreshAsync.
    public void ShowSession(SessionTracker.SessionData? d) =>
        Dispatcher.InvokeAsync(() =>
        {
            _session = d;
            // Автоопределённая очередь (где больше игр) — пока пользователь
            // не выбрал вручную в этой сессии.
            if (!_queueUserSet && d is not null) _selectedQueue = d.SelectedQueue;
            if (!_wrChartHooked)
            {
                WrChart.SizeChanged += (_, _) => DrawWrChart();
                _wrChartHooked = true;
            }
            RenderSessionView();
        });

    // Рендер панели для выбранной очереди (Solo/Flex/Normal/ARAM).
    private void RenderSessionView()
    {
        if (_selectedQueue.Length == 0)
            _selectedQueue = SessionTracker.GetSelectedQueue();

        var d = _session;
        var v = d?.Queues.GetValueOrDefault(_selectedQueue);
        _sessionView = v;

        NickText.Text  = d?.Nick ?? "";
        QueueText.Text = Loc.T("session.queue." + _selectedQueue) + " ▾";

        // По бокам ника — половинки ранговой эмблемы выбранной очереди (крылья).
        // Чем выше ранг — тем больше размах крыльев.
        var halves = v?.HasRank == true ? WingHalves(v.Tier) : null;
        WingLeft.Source  = halves?.Left;
        WingRight.Source = halves?.Right;
        WingLeft.Visibility  = halves != null ? Visibility.Visible : Visibility.Collapsed;
        WingRight.Visibility = halves != null ? Visibility.Visible : Visibility.Collapsed;
        if (halves != null && v != null)
            WingLeft.Height = WingRight.Height = WingHeight(v.Tier);

        if (v is null)
        {
            RankEmblem.Visibility = Visibility.Collapsed;
            RankText.Text = "—";
            RankText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
            RankLpText.Text = "";
            RankProgressFill.Width = 0;
            SessionHint.Text = Loc.T("session.needGames");
            SessionHint.Visibility = Visibility.Visible;
            Last5Panel.Children.Clear();
            SeasonWlText.Text = "";
            WinrateBig.Text = "—";
            WinrateBig.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
            WrChart.Children.Clear();
            return;
        }

        // Ранг есть только у Solo/Flex; для Normal/ARAM — имя очереди вместо тира.
        if (v.HasRank)
        {
            var emblem = RankEmblemSource(v.Tier);
            RankEmblem.Source = emblem;
            RankEmblem.Visibility = emblem != null ? Visibility.Visible : Visibility.Collapsed;
            var tierLoc = LocalizedTier(v.Tier);
            RankText.Text = string.IsNullOrEmpty(v.Division) ? tierLoc : $"{tierLoc} {v.Division}";
            RankText.Foreground = TierBrush(v.Tier);
            RankLpText.Text = $"{v.Lp} LP";
            RankProgressFill.Width = 258.0 * Math.Clamp(v.ProgressPct, 0, 100) / 100.0;
        }
        else
        {
            RankEmblem.Visibility = Visibility.Collapsed;
            RankText.Text = Loc.T("session.queue." + _selectedQueue);
            RankText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
            RankLpText.Text = "";
            RankProgressFill.Width = 0;
        }

        // Свежая установка/очередь: журнал этой очереди пуст — статистика
        // появится после сыгранных при запущенной программе игр.
        SessionHint.Text = Loc.T("session.needGames");
        SessionHint.Visibility = v.WinrateHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Последние 5 игр — 5 равных колонок во всю ширину бара
        Last5Panel.Children.Clear();
        Last5Panel.ColumnDefinitions.Clear();
        for (int i = 0; i < 5; i++)
            Last5Panel.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < v.Last5.Count && i < 5; i++)
        {
            var cell = BuildGameCell(v.Last5[i]);
            Grid.SetColumn(cell, i);
            Last5Panel.Children.Add(cell);
        }

        // W/L (мелко, локализовано) + винрейт (крупно, цвет по правилам)
        var played = v.Wins + v.Losses;
        SeasonWlText.Text = Loc.T("session.wl", v.Wins, v.Losses);
        WinrateBig.Text = played > 0 ? $"{v.Winrate:0}%" : "—";
        WinrateBig.Foreground = played > 0
            ? WinrateBrush(v.Winrate)
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));

        DrawWrChart();
    }

    // Клик по чипу очереди — выпадающий список Solo/Flex/Normal/ARAM.
    private void QueueChip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var menu = new ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        foreach (var key in SessionTracker.QueueKeys)
        {
            var item = new MenuItem
            {
                Header    = Loc.T("session.queue." + key),
                IsChecked = key == _selectedQueue,
                Style     = itemStyle,
            };
            var chosen = key;
            item.Click += (_, _) =>
            {
                _selectedQueue = chosen;
                _queueUserSet  = true;
                _ = Task.Run(() => SessionTracker.SetSelectedQueue(chosen)); // персист вне UI-потока
                RenderSessionView();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = QueueChip;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Цвет цифры винрейта по заданным порогам.
    private static Brush WinrateBrush(double wr)
    {
        Color c;
        if (wr > 65)      c = Color.FromRgb(0xF0, 0x8A, 0x3C); // оранжевый
        else if (wr > 55) c = Color.FromRgb(0xE2, 0x4C, 0x4C); // красный
        else if (wr >= 50) c = Color.FromRgb(0xE6, 0xED, 0xF3); // нейтральный (белый)
        else if (wr >= 45) c = Color.FromRgb(0xF0, 0xC9, 0xC4); // светло-красный, ближе к белому
        else              c = Color.FromRgb(0xE2, 0x4C, 0x4C); // красный
        return new SolidColorBrush(c);
    }

    // Локализованное название ранга (ranks.* в i18n; нет перевода — как есть).
    private static string LocalizedTier(string tier)
    {
        var key = "ranks." + tier.ToLowerInvariant();
        var t = Loc.T(key);
        return t == key ? tier : t;
    }

    // Цвет названия ранга по тиру.
    private static Brush TierBrush(string tier) => (tier?.ToUpperInvariant() ?? "") switch
    {
        "IRON"        => new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
        "BRONZE"      => new SolidColorBrush(Color.FromRgb(0xB0, 0x7A, 0x53)),
        "SILVER"      => new SolidColorBrush(Color.FromRgb(0xB6, 0xC2, 0xCC)),
        "GOLD"        => new SolidColorBrush(Color.FromRgb(0xE8, 0xC1, 0x5A)),
        "PLATINUM"    => new SolidColorBrush(Color.FromRgb(0x4F, 0xC7, 0xC7)),
        "EMERALD"     => new SolidColorBrush(Color.FromRgb(0x4F, 0xC7, 0x8A)),
        "DIAMOND"     => new SolidColorBrush(Color.FromRgb(0x6E, 0x9B, 0xE7)),
        "MASTER"      => new SolidColorBrush(Color.FromRgb(0xC0, 0x6E, 0xE0)),
        "GRANDMASTER" => new SolidColorBrush(Color.FromRgb(0xE0, 0x5B, 0x5B)),
        "CHALLENGER"  => new SolidColorBrush(Color.FromRgb(0x54, 0xC7, 0xF0)),
        _             => new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
    };

    // Эмблема ранга из встроенных ресурсов (assets/ranks/{tier}.png).
    private static readonly Dictionary<string, ImageSource> _emblemCache = new();
    private static ImageSource? RankEmblemSource(string tier)
    {
        var key = (tier ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return null;
        if (_emblemCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/assets/ranks/{key}.png");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            _emblemCache[key] = bmp;
            return bmp;
        }
        catch { return null; }
    }

    // Размах крыльев растёт с рангом: Железо — скромные, Претендент — максимальные.
    private static double WingHeight(string tier) => tier.ToUpperInvariant() switch
    {
        "IRON"        => 14,
        "BRONZE"      => 16,
        "SILVER"      => 18,
        "GOLD"        => 20,
        "PLATINUM"    => 22.5,
        "EMERALD"     => 25,
        "DIAMOND"     => 27.5,
        "MASTER"      => 30,
        "GRANDMASTER" => 32,
        "CHALLENGER"  => 34,
        _             => 20,
    };

    // Половинки эмблемы ранга (левая/правая) — «крылья» вокруг ника, с кэшем.
    private static readonly Dictionary<string, (ImageSource Left, ImageSource Right)> _wingCache = new();
    private static (ImageSource Left, ImageSource Right)? WingHalves(string tier)
    {
        var key = (tier ?? "").ToLowerInvariant();
        if (key.Length == 0) return null;
        if (_wingCache.TryGetValue(key, out var cached)) return cached;
        if (RankEmblemSource(tier!) is not BitmapSource bmp) return null;
        try
        {
            int half = bmp.PixelWidth / 2;
            var left  = new CroppedBitmap(bmp, new Int32Rect(0, 0, half, bmp.PixelHeight));
            var right = new CroppedBitmap(bmp, new Int32Rect(half, 0, bmp.PixelWidth - half, bmp.PixelHeight));
            left.Freeze(); right.Freeze();
            var pair = ((ImageSource)left, (ImageSource)right);
            _wingCache[key] = pair;
            return pair;
        }
        catch { return null; }
    }

    // Ячейка одной игры: иконка чемпиона (рамка по W/L) + LP за игру под ней.
    private FrameworkElement BuildGameCell(SessionTracker.RecentGame g)
    {
        var col = new StackPanel
        {
            Margin = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var border = new Border
        {
            Width = 47, Height = 47, CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(2),
            BorderBrush = g.Win ? WinBrush : LossBrush,
        };
        var img = IconCache.Get(g.ChampionId);
        if (img != null)
            // Рисунок скругляем собственным клипом — иначе его квадратные углы
            // вылезают за скруглённую рамку и она выглядит разорванной.
            border.Child = new Image
            {
                Source = img, Stretch = Stretch.UniformToFill,
                Width = 43, Height = 43,
                Clip = new RectangleGeometry(new Rect(0, 0, 43, 43), 7, 7),
            };
        col.Children.Add(border);

        var lp = new TextBlock
        {
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 11, FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0)
        };
        if (g.LpDelta is int lpd)
        {
            lp.Text = lpd >= 0 ? $"+{lpd}" : lpd.ToString();
            lp.Foreground = lpd >= 0 ? WinBrush : LossBrush;
        }
        else { lp.Text = "·"; lp.Foreground = MuteBrush; }
        col.Children.Add(lp);
        return col;
    }

    // Линейный график динамики винрейта (синий) по датам.
    private void DrawWrChart()
    {
        WrChart.Children.Clear();
        var v = _sessionView;
        if (v is null) return;
        IReadOnlyList<SessionTracker.WrPoint> pts = v.WinrateHistory;
        double w = WrChart.ActualWidth > 4 ? WrChart.ActualWidth : 150;
        double h = WrChart.ActualHeight > 4 ? WrChart.ActualHeight : 58;
        if (pts.Count == 0) return;

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
        double min = pts.Min(p => p.Winrate), max = pts.Max(p => p.Winrate);
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

        var lineBrush = new SolidColorBrush(Color.FromRgb(0x36, 0xD6, 0xE7));
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        // Вертикальная ось + горизонтальная сетка со шкалой винрейта (низ/середина/верх)
        WrChart.Children.Add(new System.Windows.Shapes.Line
        { X1 = plotL, X2 = plotL, Y1 = padY, Y2 = baseY, Stroke = gridBrush, StrokeThickness = 1 });
        foreach (var wr in new[] { max, (min + max) / 2, min })
        {
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

        // Точка последнего значения
        int last = pts.Count - 1;
        var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = lineBrush };
        Canvas.SetLeft(dot, X(last) - 3);
        Canvas.SetTop(dot, Y(pts[last].Winrate) - 3);
        WrChart.Children.Add(dot);

        // Подписи крайних дат под областью графика
        AddDateLabel(pts[0].Date, plotL, h, right: false);
        if (pts.Count > 1) AddDateLabel(pts[last].Date, plotR, h, right: true);
    }

    private void AddDateLabel(DateTime date, double x, double h, bool right = false)
    {
        var tb = new TextBlock
        {
            Text = date.ToString("d.MM"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 9, Foreground = MuteBrush, Opacity = 0.7
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(tb, right ? Math.Max(0, x - tb.DesiredSize.Width) : x);
        Canvas.SetTop(tb, h - 11);
        WrChart.Children.Add(tb);
    }

    // Прогресс загрузки со строкой состояния и полосой (обновление/база).
    public void ShowProgress(string text, double fraction) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = text;
            DlBar.Visibility    = Visibility.Visible;
            DlBarFill.Width     = 260.0 * Math.Clamp(fraction, 0.0, 1.0);
            PulseAnim(true);   // скользящий блик — бар «живой» даже на плато %
            SetLoadingMode();
            ShowIdle();
        });

    // Неопределённый прогресс: фоновая работа без процентов (распаковка/проверка).
    // Полоса полная, но блик продолжает бежать — видно, что процесс идёт.
    public void ShowProgressBusy(string text) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = text;
            DlBar.Visibility    = Visibility.Visible;
            DlBarFill.Width     = 260.0;
            PulseAnim(true);
            SetLoadingMode();
            ShowIdle();
        });

    // Управление бегущим бликом на полосе прогресса.
    private bool _pulseOn;
    private void PulseAnim(bool on)
    {
        if (on == _pulseOn) return;
        _pulseOn = on;
        if (on)
        {
            DlBarPulse.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(-70, 260,
                new Duration(TimeSpan.FromSeconds(1.1)))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
            };
            DlBarPulseT.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, anim);
        }
        else
        {
            DlBarPulseT.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            DlBarPulse.Visibility = Visibility.Collapsed;
        }
    }

    // ── Карусель советов по пику (поле фиксированного размера, смена раз в 40 с) ──
    // Переключение на стадию загрузки/ожидания — прячем готовность.
    private void SetLoadingMode()
    {
        LoadingInfo.Visibility = Visibility.Visible;
        ReadyInfo.Visibility   = Visibility.Collapsed;
        // _tipTimer?.Stop(); // карусель советов (см. блок ниже)
    }

    /* ── Карусель советов: временно скрыта — на её месте BETA-плашка. ─────────
       Вернуть: раскомментировать этот блок и Border с TipText в XAML,
       StartTips() в ShowReadyCore, _tipTimer?.Stop() в SetLoadingMode,
       а в OnLanguageChanged — сброс _tipIdx = -1 и StartTips() при видимом ReadyInfo.

    // Тексты — из локализации (assets/i18n/{lang}.json, ключ "tips").
    private static string[] Tips => Loc.TArray("tips");

    private int _tipIdx = -1;
    private System.Windows.Threading.DispatcherTimer? _tipTimer;

    private void StartTips()
    {
        if (_tipTimer == null)
        {
            _tipTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(40) };
            _tipTimer.Tick += (_, _) => NextTip();
        }
        if (_tipIdx < 0 && Tips.Length > 0)
        {
            // случайный стартовый совет — чтобы не всегда первый
            _tipIdx = new Random().Next(Tips.Length) - 1;
            NextTip();
        }
        _tipTimer.Start();
    }

    private void NextTip()
    {
        var tips = Tips;
        if (tips.Length == 0) return;
        _tipIdx = (_tipIdx + 1) % tips.Length;
        TipText.Text = tips[_tipIdx];
        // мягкое проявление при смене
        TipText.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                new Duration(TimeSpan.FromMilliseconds(350))));
    }
    ────────────────────────────────────────────────────────────────────────── */

    // Экран ожидания: маленькое окно, спиннер и крупная подпись стадии.
    private void ShowIdle()
    {
        // Запоминаем размер полного режима перед сворачиванием в ожидание.
        if (IdlePanel.Visibility != Visibility.Visible && _isFullMode)
        {
            _savedFullW = Width;
            _savedFullH = Height;
        }

        IdlePanel.Visibility     = Visibility.Visible;
        StatusText.Visibility    = Visibility.Collapsed;
        CompactScroll.Visibility = Visibility.Collapsed;
        FullView.Visibility      = Visibility.Collapsed;

        SizeToContent = SizeToContent.Height; // высота по контенту
        MinWidth  = 0;
        MinHeight = 0;
        Width     = IdleW;

        if (_inTray) return; // во время игры окно скрыто в трее
        Show();
        AnchorIfNotMoved();
    }

    // Возврат к размеру активного режима (полный/компактный) при появлении пиков.
    private void RestoreModeSize()
    {
        StatusText.Visibility = Visibility.Visible;
        IdlePanel.Visibility  = Visibility.Collapsed;

        if (_isFullMode)
        {
            SizeToContent = SizeToContent.Manual;
            MinWidth  = MinW;
            MinHeight = MinH;
            Width     = _savedFullW;
            Height    = _savedFullH;
        }
        else
        {
            SizeToContent = SizeToContent.Height;
            MinWidth  = 0;
            MinHeight = 0;
            Width     = CompactW;
        }
    }

    public void UpdateRecommendations(
        IReadOnlyList<Recommendation>? recs, DraftState? draft,
        RecommendationEngine? engine = null)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (engine != null) _engine = engine;
            if (draft is null) _enemyRoleOverrides.Clear(); // конец драфта — сброс меток
            _lastRawDraft = draft;
            var eff = draft != null ? ApplyEnemyRoleOverrides(draft) : null;
            _lastDraft = eff;
            // Авто-раскладка ролей врагов + ручные метки меняют оппонента и
            // кросс-пары — пересчитываем подбор по эффективному драфту.
            if (recs != null && eff != null && _engine != null && !ReferenceEquals(eff, draft))
                recs = eff.IsAram ? _engine.RecommendAram(eff) : _engine.Recommend(eff);
            _lastRecs  = recs;
            _lastBans  = null;
            RenderCurrentState();
        });
    }

    public void UpdateBans(IReadOnlyList<BanRec>? bans, DraftState? draft) =>
        Dispatcher.InvokeAsync(() =>
        {
            _lastBans  = bans;
            _lastRecs  = null;
            _lastRawDraft = draft;
            _lastDraft = draft != null ? ApplyEnemyRoleOverrides(draft) : null;
            RenderCurrentState();
        });

    // ── Роли врагов: авто-раскладка + ручные метки ────────────────────────

    // Раскладывает роли по вражеской команде: ручные метки фиксированы, остальным
    // чемпионам роли назначаются жадно по доле их игр на роли (каждая роль — один
    // раз). Итог: у Ирелии «топ», но стоит вручную поставить Чо'Гата на топ — она
    // автоматически съезжает на свою следующую лучшую свободную роль (мид).
    private DraftState ApplyEnemyRoleOverrides(DraftState d)
    {
        if (d.IsAram) return d; // в ARAM ролей нет
        var their = d.TheirTeam.ToArray();
        var usedRoles = new HashSet<string>();

        // 1. Ручные метки — фиксированы.
        for (int i = 0; i < their.Length; i++)
            if (_enemyRoleOverrides.TryGetValue(their[i].CellId, out var pos) && pos.Length > 0)
            {
                their[i] = their[i] with { Position = pos };
                usedRoles.Add(pos);
            }

        // 2. Остальные с чемпионами — жадное назначение по доле роли.
        if (_engine != null)
        {
            var freeIdx = Enumerable.Range(0, their.Length)
                .Where(i => !_enemyRoleOverrides.ContainsKey(their[i].CellId)
                            && their[i].EffectiveChampionId != 0)
                .ToList();
            var pairs = new List<(int Idx, string Role, double Share)>();
            foreach (var i in freeIdx)
                foreach (var lcuRole in RoleCycle.Skip(1)) // без пустой "авто"
                {
                    if (usedRoles.Contains(lcuRole)) continue;
                    var share = _engine.RoleShare(their[i].EffectiveChampionId,
                                                  RecommendationEngine.LcuToDbRole(lcuRole));
                    pairs.Add((i, lcuRole, share));
                }
            var assigned = new HashSet<int>();
            foreach (var (idx, role, share) in pairs.OrderByDescending(x => x.Share))
            {
                if (share <= 0 || assigned.Contains(idx) || usedRoles.Contains(role)) continue;
                their[idx] = their[idx] with { Position = role };
                assigned.Add(idx);
                usedRoles.Add(role);
            }
        }

        // 3. Прямой оппонент — враг, вставший на мою позицию.
        var list = their.ToList();
        var direct = !string.IsNullOrEmpty(d.MyPosition)
            ? list.FirstOrDefault(p => p.EffectiveChampionId != 0 && p.Position == d.MyPosition)
            : null;
        return d with { TheirTeam = list, DirectOpponent = direct ?? d.DirectOpponent };
    }

    // Клик по роли в карточке врага — выпадающий список: Авто / ТОП / ЛЕС / МИД / БОТ / САПП.
    private void EnemyRole_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not int cellId || cellId < 0) return;
        e.Handled = true;

        var menu = new ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        var current = _enemyRoleOverrides.GetValueOrDefault(cellId, "");
        foreach (var lcuRole in RoleCycle) // "" = авто
        {
            var item = new MenuItem
            {
                Header      = lcuRole.Length == 0 ? Loc.T("slot.roleAuto") : RoleName(lcuRole),
                IsChecked   = current == lcuRole,
                IsCheckable = false,
                Style       = itemStyle,
            };
            var chosen = lcuRole;
            item.Click += (_, _) => SetEnemyRole(cellId, chosen);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = el;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Назначает роль врагу вручную. Если роль уже занята другой ручной меткой —
    // та снимается (вернётся в авто и переедет на следующую лучшую роль).
    private void SetEnemyRole(int cellId, string lcuRole)
    {
        if (lcuRole.Length == 0) _enemyRoleOverrides.Remove(cellId);
        else
        {
            foreach (var other in _enemyRoleOverrides
                         .Where(kv => kv.Key != cellId && kv.Value == lcuRole)
                         .Select(kv => kv.Key).ToList())
                _enemyRoleOverrides.Remove(other);
            _enemyRoleOverrides[cellId] = lcuRole;
        }

        if (_lastRawDraft is null) { RenderCurrentState(); return; }
        var eff = ApplyEnemyRoleOverrides(_lastRawDraft);
        _lastDraft = eff;
        if (_engine != null && _lastRecs != null)
            _lastRecs = eff.IsAram ? _engine.RecommendAram(eff) : _engine.Recommend(eff);
        RenderCurrentState();
    }

    // ── Рендер ────────────────────────────────────────────────────────────

    private void RenderCurrentState()
    {
        var recs  = _lastRecs;
        var draft = _lastDraft;

        // Фаза банов: показываем рекомендуемые баны (в том же разделе, что и пики).
        if (draft?.InBanPhase == true)
        {
            if (_lastBans is null || _lastBans.Count == 0)
            {
                IdleStatusText.Text = Loc.T("status.banPhase");
                ShowIdle();
                return;
            }
            RestoreModeSize();
            if (_isFullMode)
            {
                RenderBansFull(_lastBans, draft);
                CompactScroll.Visibility = Visibility.Collapsed;
                FullView.Visibility      = Visibility.Visible;
            }
            else
            {
                RenderBansCompact(_lastBans, draft);
                FullView.Visibility      = Visibility.Collapsed;
                CompactScroll.Visibility = Visibility.Visible;
            }
            if (_inTray) return;
            Show();
            AnchorIfNotMoved();
            return;
        }

        if (recs == null || recs.Count == 0)
        {
            IdleStatusText.Text = Loc.T("status.waitDraft");
            ShowIdle();
            return;
        }

        RestoreModeSize();

        if (draft?.IsAram == true)
        {
            // ARAM: врагов не видно, подбираем лучший пик со скамейки под команду.
            StatusText.Text = Loc.T("draft.aram");
            PickHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            var dbRole       = draft?.MyPosition is { Length: > 0 } pos
                ? RecommendationEngine.LcuToDbRole(pos) : "";
            var role         = RoleNameDb(dbRole);
            var knownEnemies = draft?.TheirTeam.Count(p => p.EffectiveChampionId != 0) ?? 0;
            StatusText.Text  = knownEnemies == 0
                ? Loc.T("draft.roleByTeam", role)
                : Loc.T("draft.roleCounter", role);

            // Подсказка по порядку пика: пикаешь раньше врагов → риск контрпика.
            if (draft?.ExposedToCounter == true)
            {
                PickHint.Text = Loc.T("draft.pickHint");
                PickHint.Visibility = Visibility.Visible;
            }
            else
            {
                PickHint.Visibility = Visibility.Collapsed;
            }
        }

        if (_isFullMode)
        {
            RenderFull(recs, draft);
            CompactScroll.Visibility = Visibility.Collapsed;
            FullView.Visibility      = Visibility.Visible;
        }
        else
        {
            RenderCompact(recs);
            FullView.Visibility      = Visibility.Collapsed;
            CompactScroll.Visibility = Visibility.Visible;
        }

        if (_inTray) return; // во время игры окно скрыто в трее
        Show();
        AnchorIfNotMoved();
    }

    // ── Фаза банов ─────────────────────────────────────────────────────────

    private void RenderBansCompact(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        PickHint.Visibility = Visibility.Collapsed;
        StatusText.Text = Loc.T("draft.roleBans", RoleNameDb(
            draft.MyPosition is { Length: > 0 } pos ? RecommendationEngine.LcuToDbRole(pos) : ""));

        RecList.ItemsSource = bans.Take(6).Select((b, i) => new RecCard
        {
            Rank       = $"{i + 1}.",
            Name       = DataDragon.Name(b.ChampionId),
            Score      = Loc.T("badge.ban"),
            ScoreColor = "#E0584F",
            Reason     = b.Reasons.FirstOrDefault() ?? "",
            Icon       = IconCache.Get(b.ChampionId),
        }).ToList();
    }

    // ── Компактный вид ────────────────────────────────────────────────────

    private void RenderCompact(IReadOnlyList<Recommendation> recs)
    {
        RecList.ItemsSource = recs.Take(4).Select(r => new RecCard
        {
            Rank       = $"{r.Rank}.",
            Name       = (r.IsMyPick ? "★ " : "") + DataDragon.Name(r.ChampionId),
            Score      = Signed(r.Score),
            ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
            Reason     = r.Reasons.FirstOrDefault() ?? "",
            Icon       = IconCache.Get(r.ChampionId),
        }).ToList();
    }

    // ── Полный вид ────────────────────────────────────────────────────────

    private void RenderFull(IReadOnlyList<Recommendation> recs, DraftState? draft)
    {
        // Связки союзников и их цвета — нужны и панели, и дашикам рекомендаций.
        var allyIds  = draft?.MyTeam.Where(p => p.EffectiveChampionId != 0)
                                    .Select(p => p.EffectiveChampionId).ToList() ?? [];
        var myCombos = draft != null ? DetectCombos(draft.MyTeam, ally: true) : [];
        var comboColorByName = new Dictionary<string, string>();
        for (int i = 0; i < myCombos.Count; i++)
            comboColorByName[myCombos[i].Name] = ComboColors[i % ComboColors.Length];

        // Сильнейшая метрика среди показанных пиков → золотое свечение всей строки (если > 0).
        double maxBase = recs.Count > 0 ? recs.Max(r => r.BaseDelta)    : 0;
        double maxDir  = recs.Count > 0 ? recs.Max(r => r.DirectDelta)  : 0;
        double maxSty  = recs.Count > 0 ? recs.Max(r => r.StyleDelta)   : 0;
        double maxSyn  = recs.Count > 0 ? recs.Max(r => r.SynergyDelta) : 0;
        static bool IsMax(double v, double max) => max > 0.05 && v >= max - 0.05;

        // Имена чемпионов драфта → цвет их архетипа (подсветка имён в обоснованиях).
        var nameColor = new Dictionary<string, string>();
        if (draft != null)
            foreach (var p in draft.MyTeam.Concat(draft.TheirTeam))
            {
                var id = p.EffectiveChampionId;
                if (id == 0) continue;
                var (_, archCol, _) = ArchBadge(id);
                var nm = DataDragon.Name(id);
                if (!string.IsNullOrEmpty(nm) && archCol != "#888888") nameColor[nm] = archCol;
            }

        // Союзники без меня — для расчёта контр-предметов состава.
        var allyNoMe = draft?.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
                                    .Select(p => p.EffectiveChampionId).ToList() ?? [];

        FullRecList.ItemsSource = recs.Select(r =>
        {
            var (ag, ac, at) = ArchBadge(r.ChampionId);
            return new FullRecCard
            {
                Rank       = $"{r.Rank}.",
                IsMyPick   = r.IsMyPick,
                MineLabel  = Loc.T("rec.yourPick"),
                Name       = DataDragon.Name(r.ChampionId),
                Score      = Signed(r.Score),
                ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
                WinRate    = $"WR ~{50.0 + r.BaseDelta:F1}%",
                Icon       = IconCache.Get(r.ChampionId),
                // Маркеры «•» + имена чемпионов цветом их архетипа (см. ReasonSegments).
                ReasonSegs = ReasonSegments(r.Reasons, nameColor),
                BaseBar    = ToBaseBar(r.BaseDelta),
                DirectBar  = ToBar(r.DirectDelta),
                OtherBar   = ToBar(r.StyleDelta),   // строка «Против их стиля»
                SynBar     = ToBar(r.SynergyDelta),
                BaseText   = Signed(r.BaseDelta),
                DirectText = Signed(r.DirectDelta),
                OtherText  = Signed(r.StyleDelta),
                SynText    = Signed(r.SynergyDelta),
                BaseStrong   = IsMax(r.BaseDelta,    maxBase),
                DirectStrong = IsMax(r.DirectDelta,  maxDir),
                OtherStrong  = IsMax(r.StyleDelta,   maxSty),
                SynStrong    = IsMax(r.SynergyDelta, maxSyn),
                ArchGlyph  = ag,
                ArchColor  = ac,
                ArchTip    = at,
                SynDashes  = SynDashesFor(r.ChampionId, allyIds, comboColorByName),
                CounterItems = ItemValue.CounterItems(r.ChampionId, allyNoMe)
                    .Select(ItemIcons.Get).Where(x => x != null).Cast<ImageSource>().ToList(),
            };
        }).ToList();

        RecScroll.Visibility = Visibility.Visible;   // показываем пики
        BanScroll.Visibility = Visibility.Collapsed;
        if (draft != null) RenderTeams(draft);
    }

    // Разбивает причины на сегменты: маркер «•», перевод строк между причинами и
    // имена чемпионов, покрашенные в цвет их архетипа (nameColor).
    private static List<ReasonSeg> ReasonSegments(string[] reasons, Dictionary<string, string> nameColor)
    {
        var segs = new List<ReasonSeg>();
        System.Text.RegularExpressions.Regex? rx = null;
        if (nameColor.Count > 0)
        {
            var alt = string.Join("|", nameColor.Keys.OrderByDescending(n => n.Length)
                .Select(System.Text.RegularExpressions.Regex.Escape));
            rx = new System.Text.RegularExpressions.Regex("(" + alt + ")");
        }
        for (int i = 0; i < reasons.Length; i++)
        {
            if (i > 0) segs.Add(new ReasonSeg { Break = true });
            var line = "•  " + reasons[i];
            if (rx is null) { segs.Add(new ReasonSeg { Text = line }); continue; }
            foreach (var part in rx.Split(line))
            {
                if (part.Length == 0) continue;
                segs.Add(nameColor.TryGetValue(part, out var c)
                    ? new ReasonSeg { Text = part, Color = c }
                    : new ReasonSeg { Text = part });
            }
        }
        return segs;
    }

    // Полный вид для фазы банов: тот же раздел (команды + центр), но в центре баны.
    private void RenderBansFull(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        StatusText.Text     = Loc.T("draft.roleBans", RoleNameDb(
            draft.MyPosition is { Length: > 0 } pos ? RecommendationEngine.LcuToDbRole(pos) : ""));
        PickHint.Visibility = Visibility.Collapsed;

        BanFullList.ItemsSource = bans.Take(6).Select((b, i) => new RecCard
        {
            Rank   = $"{i + 1}.",
            Name   = DataDragon.Name(b.ChampionId),
            Reason = b.Reasons.FirstOrDefault() ?? "",
            Icon   = IconCache.Get(b.ChampionId),
        }).ToList();

        RecScroll.Visibility = Visibility.Collapsed; // показываем баны
        BanScroll.Visibility = Visibility.Visible;
        RenderTeams(draft);
    }

    // Команды по бокам (слоты, стиль, связки, линии) — общее для пиков и банов.
    private void RenderTeams(DraftState draft)
    {
        var myRole = RecommendationEngine.LcuToDbRole(draft.MyPosition);
        MyTeamList.ItemsSource    = BuildSlots(draft.MyTeam,    ally: true,  _engine, myRole);
        EnemyTeamList.ItemsSource = BuildSlots(draft.TheirTeam, ally: false, _engine, myRole, _enemyRoleOverrides);

        var allyIds  = draft.MyTeam.Where(p => p.EffectiveChampionId != 0)
                                   .Select(p => p.EffectiveChampionId).ToList();
        var enemyIds = draft.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                      .Select(p => p.EffectiveChampionId).ToList();
        var myStyle    = ChampionTraits.StyleLabel(allyIds);
        var enemyStyle = ChampionTraits.StyleLabel(enemyIds);
        MyTeamStyle.Text    = myStyle.Length    > 0 ? Loc.T("draft.style", myStyle)    : "";
        EnemyTeamStyle.Text = enemyStyle.Length > 0 ? Loc.T("draft.style", enemyStyle) : "";

        var myCombos    = DetectCombos(draft.MyTeam,    ally: true);
        var enemyCombos = DetectCombos(draft.TheirTeam, ally: false);
        MyTeamCombos.ItemsSource     = ToCards(myCombos,    ally: true);
        EnemyTeamCombos.ItemsSource  = ToCards(enemyCombos, ally: false);
        MyCombosHeader.Visibility    = myCombos.Count    > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyCombosHeader.Visibility = enemyCombos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var myTeam    = draft.MyTeam;
        var enemyTeam = draft.TheirTeam;
        Dispatcher.InvokeAsync(() =>
        {
            DrawTeamLines(MyTeamLines,    MyTeamList,    myTeam,    myCombos);
            DrawTeamLines(EnemyTeamLines, EnemyTeamList, enemyTeam, enemyCombos);
        }, DispatcherPriority.Loaded);
    }

    // Палитра коннектор-линий (фиолетовый / бирюзовый / золотой), как в референсе.
    private static readonly string[] ComboColors = ["#8B7CF6", "#2FB7A0", "#E8B84B"];

    private static List<TeamCombo> DetectCombos(IReadOnlyList<DraftPlayer> players, bool ally)
    {
        var team = players
            .Where(p => p.EffectiveChampionId != 0)
            .Select(p => (Id: p.EffectiveChampionId, Role: RecommendationEngine.LcuToDbRole(p.Position)))
            .ToList();
        return TeamSynergies.Detect(team, ally).Take(3).ToList();
    }

    private static List<ComboCard> ToCards(List<TeamCombo> combos, bool ally) =>
        combos.Select((co, i) => new ComboCard
        {
            Title       = co.Name,
            Icons       = co.ChampionIds.Select(IconCache.Get).Where(x => x != null).Cast<ImageSource>().ToList(),
            Description = co.Description,
            Tip         = co.Tip,
            TipLabel    = ally ? Loc.T("combo.howToPlay") : Loc.T("combo.danger"),
            AccentColor = ally ? "#C89B3C" : "#C84040",
            LineColor   = ComboColors[i % ComboColors.Length],
        }).ToList();

    // Цветные «черточки» под иконкой рекомендации: кандидат входит в связку с
    // союзниками — уже сложившуюся (цвет её линии) или ту, которую он СОЗДАСТ
    // своим пиком (следующий свободный цвет). Раньше дашик появлялся только у
    // готовых связок — т.е. лишь после того, как чемпиона реально взяли.
    private static List<string> SynDashesFor(
        int candidateId, List<int> allyIds, Dictionary<string, string> comboColorByName)
    {
        var result = new List<string>();
        if (candidateId == 0 || allyIds.Count == 0) return result;

        // Кандидат может уже быть в команде (это мой собственный пик) — тогда не
        // дублируем его, но чёрточки его связок всё равно показываем.
        var ids = allyIds.Contains(candidateId) ? allyIds : allyIds.Append(candidateId).ToList();
        var team = ids.Select(id => (Id: id, Role: "")).ToList();
        int nextColor = comboColorByName.Count; // будущая связка получит следующий цвет
        foreach (var combo in TeamSynergies.Detect(team, forAlly: true))
        {
            if (!combo.ChampionIds.Contains(candidateId)) continue;          // кандидат — участник
            if (!comboColorByName.TryGetValue(combo.Name, out var color))    // связки ещё нет —
                color = ComboColors[nextColor++ % ComboColors.Length];       // пик её создаст
            if (!result.Contains(color)) result.Add(color);
        }
        return result;
    }

    // Рисует скобки-коннекторы, связывающие портреты участников каждой связки.
    private void DrawTeamLines(
        Canvas canvas, ItemsControl list,
        IReadOnlyList<DraftPlayer> players, List<TeamCombo> combos)
    {
        canvas.Children.Clear();
        if (combos.Count == 0) return;

        // championId → индекс строки (0..4)
        var rowOf = new Dictionary<int, int>();
        for (int i = 0; i < players.Count && i < 5; i++)
        {
            var id = players[i].EffectiveChampionId;
            if (id != 0 && !rowOf.ContainsKey(id)) rowOf[id] = i;
        }

        double? RowY(int i)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) return null;
            var top = c.TransformToVisual(canvas).Transform(new System.Windows.Point(0, 0)).Y;
            // центр портрета: отступы Border(1+3) + Grid(2) + половина эллипса 36
            return top + 6 + 36;
        }

        const double portraitRight = 72;
        for (int ci = 0; ci < combos.Count; ci++)
        {
            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    ComboColors[ci % ComboColors.Length]));
            brush.Freeze();
            double vx = 80 + ci * 9; // вертикаль скобки со сдвигом на каждую связку

            var ys = combos[ci].ChampionIds
                .Where(rowOf.ContainsKey).Select(id => rowOf[id]).Distinct()
                .Select(RowY).Where(y => y.HasValue).Select(y => y!.Value)
                .OrderBy(y => y).ToList();
            if (ys.Count < 2) continue;

            // вертикальный ствол скобки
            canvas.Children.Add(new Line
            {
                X1 = vx, Y1 = ys.First(), X2 = vx, Y2 = ys.Last(),
                Stroke = brush, StrokeThickness = 2.5,
            });
            // горизонтальные отводы к каждому портрету
            foreach (var y in ys)
                canvas.Children.Add(new Line
                {
                    X1 = portraitRight, Y1 = y, X2 = vx, Y2 = y,
                    Stroke = brush, StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
        }
    }

    private static List<ChampSlotCard> BuildSlots(
        IReadOnlyList<DraftPlayer> players, bool ally,
        RecommendationEngine? engine, string myRole,
        IReadOnlyDictionary<int, string>? roleOverrides = null)
    {
        return Enumerable.Range(0, 5).Select(i =>
        {
            if (i >= players.Count) return EmptySlot(i + 1, ally);

            var p        = players[i];
            var champId  = p.ChampionId > 0 ? p.ChampionId : p.PickIntentId;
            var isLocked = p.ChampionId > 0;
            var hasChamp = champId > 0;
            var isMe     = p.IsLocalPlayer;

            List<ImageSource> sideIcons = [];
            string sideLabel = "";
            if (hasChamp && engine != null)
            {
                if (!ally)
                {
                    var enemyRole = RecommendationEngine.LcuToDbRole(p.Position);
                    var ids = engine.TopCounters(champId, string.IsNullOrEmpty(enemyRole) ? null : enemyRole);
                    sideIcons = ids.Select(id => IconCache.Get(id)).Where(x => x != null).Cast<ImageSource>().ToList();
                    if (sideIcons.Count > 0) sideLabel = Loc.T("slot.counters");
                }
                else if (!p.IsLocalPlayer)
                {
                    var ids = engine.TopSynergies(champId, myRole);
                    sideIcons = ids.Select(id => IconCache.Get(id)).Where(x => x != null).Cast<ImageSource>().ToList();
                    if (sideIcons.Count > 0) sideLabel = Loc.T("slot.synergy");
                }
            }

            // Значок архетипа (камень/ножницы/бумага), определяется по чемпиону.
            var (archGlyph, archColor, archTip) = hasChamp ? ArchBadge(champId) : ("", "#888888", "");

            return new ChampSlotCard
            {
                Name        = hasChamp
                    ? (ally ? DataDragon.Name(champId).ToUpperInvariant() : $"ENEMY {i + 1}")
                    : "—",
                // У врага с чемпионом роль кликабельна («▾»): игрок может задать её
                // вручную, если знает, куда пойдёт флекс-пик.
                Role        = hasChamp
                    ? (ally ? RoleName(p.Position) : RoleName(p.Position) + " ▾")
                    : (isMe ? Loc.T("slot.youPick") : ""),
                RoleCellId  = (!ally && hasChamp) ? p.CellId : -1,
                RoleColor   = (!ally && roleOverrides != null && roleOverrides.ContainsKey(p.CellId))
                    ? "#F5D77A" : "#8AA0B2",
                RoleTip     = (!ally && hasChamp) ? Loc.T("slot.roleTip") : null,
                Icon        = hasChamp ? IconCache.Get(champId) : null,
                // В пустом моём слоте показываем иконку роли
                RoleIcon    = (isMe && !hasChamp) ? RoleIcons.Get(p.Position) : null,
                Opacity     = isLocked ? 1.0 : (isMe ? 1.0 : 0.55),
                // Мой слот подсвечиваем ярким золотом и толстой рамкой
                BorderColor = isMe ? "#F5D77A" : (ally ? "#C89B3C" : "#C84040"),
                BorderThickness = isMe ? 3.5 : 2.0,
                IsMe        = isMe,
                NameColor   = hasChamp ? (ally ? "White" : "#E07070") : (isMe ? "#F5D77A" : "#3A5060"),
                IsEmpty     = !hasChamp,
                SideLabel   = sideLabel,
                SideIcons   = sideIcons,
                ArchGlyph   = archGlyph,
                ArchColor   = archColor,
                ArchTip     = archTip,
            };
        }).ToList();
    }

    private static ChampSlotCard EmptySlot(int n, bool ally) => new()
    {
        Name        = "—",
        Role        = "",
        BorderColor = ally ? "#C89B3C" : "#C84040",
        NameColor   = "#3A5060",
        IsEmpty     = true,
        Opacity     = 0.3,
    };

    // Имя роли по позиции LCU (top/jungle/middle/bottom/utility) — локализовано.
    private static string RoleName(string pos) => Loc.T(pos.ToLowerInvariant() switch
    {
        "top"     => "roles.top",
        "jungle"  => "roles.jungle",
        "middle"  => "roles.mid",
        "bottom"  => "roles.adc",
        "utility" => "roles.support",
        _         => "roles.unknown",
    });

    // Имя роли по ключу БД (top/jungle/mid/adc/support) — локализовано.
    private static string RoleNameDb(string dbRole) =>
        string.IsNullOrEmpty(dbRole) || dbRole == "—"
            ? Loc.T("roles.unknown")
            : Loc.T($"roles.{dbRole}");

    // Ширина полоски показателя (трек ~226px). Дельта масштабируется так, что
    // сильные значения (≈+17пп синергии) почти заполняют полоску.
    private static double ToBar(double delta) => Math.Max(0, Math.Min(214, delta * 13));

    // Базовый WR после темпера упирается в ~+3пп, поэтому у него своя шкала:
    // +3 = полная полоса (значит +2.5 ≈ 83%), иначе строка выглядела бы пустой.
    private static double ToBaseBar(double delta) => Math.Max(0, Math.Min(214, delta * 71));
    private static string Signed(double v)    => (v >= 0 ? "+" : "") + v.ToString("F1");

    // Значок архетипа чемпиона: эмодзи камень-ножницы-бумага + цвет фона.
    // Фронт(✊,синий) бьёт Дайв(✌,красный), Дайв бьёт Пик(✋,зелёный), Пик бьёт Фронт.
    private static (string Glyph, string Color, string Tip) ArchBadge(int champId) =>
        ChampionTraits.ChampArch(champId) switch
        {
            ChampionTraits.Arch.FrontToBack => ("✊", "#3D7EC4", Loc.T("arch.frontToBack")),
            ChampionTraits.Arch.Dive        => ("✌", "#D85050", Loc.T("arch.dive")),
            ChampionTraits.Arch.PickPoke    => ("✋", "#4CAE6A", Loc.T("arch.pickPoke")),
            _                               => ("", "#888888", ""),
        };
}

// ── View-models ──────────────────────────────────────────────────────────────

public sealed class RecCard
{
    public string       Rank       { get; init; } = "";
    public string       Name       { get; init; } = "";
    public string       Score      { get; init; } = "";
    public string       ScoreColor { get; init; } = "#C89B3C";
    public string       Reason     { get; init; } = "";
    public ImageSource? Icon       { get; init; }
}

public sealed class FullRecCard
{
    public string       Rank       { get; init; } = "";
    public string       Name       { get; init; } = "";
    public string       Score      { get; init; } = "";
    public string       ScoreColor { get; init; } = "#C89B3C";
    public string       WinRate    { get; init; } = "";
    public ImageSource? Icon       { get; init; }
    public string       ReasonText { get; init; } = "";
    public IEnumerable<ReasonSeg> ReasonSegs { get; init; } = [];
    public double       BaseBar    { get; init; }
    public double       DirectBar  { get; init; }
    public double       OtherBar   { get; init; }
    public double       SynBar     { get; init; }
    public string       BaseText   { get; init; } = "";
    public string       DirectText { get; init; } = "";
    public string       OtherText  { get; init; } = "";
    public string       SynText    { get; init; } = "";
    // Базовый цвет значения метрики (свой у каждой строки).
    public string       BaseColor   { get; init; } = "#5C9BDC";
    public string       DirectColor { get; init; } = "#E06464";
    public string       OtherColor  { get; init; } = "#E0944C";
    public string       SynColor    { get; init; } = "#5BC487";
    // Сильнейшая среди пула метрика → золотое свечение всей строки (лейбл + число).
    public bool         BaseStrong   { get; init; }
    public bool         DirectStrong { get; init; }
    public bool         OtherStrong  { get; init; }
    public bool         SynStrong    { get; init; }
    public string       ArchGlyph  { get; init; } = "";
    public string       ArchColor  { get; init; } = "#888888";
    public string       ArchTip    { get; init; } = "";
    public Visibility   ArchVisibility => ArchGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<string> SynDashes  { get; init; } = []; // цвета связок с союзниками

    // Иконки предметов, которыми враг контрит состав (см. ItemValue.CounterItems).
    public List<ImageSource> CounterItems { get; init; } = [];
    public Visibility CounterItemsVisibility =>
        CounterItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Мой уже выбранный чемпион — выделяем карточку и показываем бейдж.
    public bool         IsMyPick   { get; init; }
    public string       MineLabel  { get; init; } = "";
    public Visibility   MineVisibility => IsMyPick ? Visibility.Visible : Visibility.Collapsed;
    public string       CardBg     => IsMyPick ? "#1E36D6E7" : "#1EC89B3C";
    public string       CardBorder => IsMyPick ? "#36D6E7" : "#00000000";
}

public sealed class ChampSlotCard
{
    public string            Name        { get; init; } = "";
    public string            Role        { get; init; } = "";
    // Кликабельная роль врага (ручное назначение): cellId слота, -1 = не кликается.
    public int               RoleCellId  { get; init; } = -1;
    public string            RoleColor   { get; init; } = "#8AA0B2";
    public string?           RoleTip     { get; init; }   // null — без тултипа (союзники)
    public ImageSource?      Icon        { get; init; }
    public double            Opacity     { get; init; } = 1.0;
    public string            BorderColor { get; init; } = "#C89B3C";
    public string            NameColor   { get; init; } = "White";
    public bool              IsEmpty     { get; init; }
    public string            SideLabel   { get; init; } = "";
    public List<ImageSource> SideIcons   { get; init; } = [];
    public Visibility        SideVisibility =>
        SideIcons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Значок архетипа чемпиона (камень/ножницы/бумага).
    public string      ArchGlyph      { get; init; } = "";
    public string      ArchColor      { get; init; } = "#888888";
    public string      ArchTip        { get; init; } = "";
    public Visibility  ArchVisibility => ArchGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // Выделение слота локального игрока (мой пик)
    public bool         IsMe            { get; init; }
    public double       BorderThickness { get; init; } = 2.0;
    public ImageSource? RoleIcon        { get; init; }  // иконка роли в пустом моём слоте
    public Visibility   RoleIconVisibility =>
        RoleIcon != null ? Visibility.Visible : Visibility.Collapsed;
}

public sealed class ComboCard
{
    public string            Title       { get; init; } = "";
    public List<ImageSource> Icons       { get; init; } = [];
    public string            Description { get; init; } = "";
    public string            Tip         { get; init; } = "";
    public string            TipLabel    { get; init; } = "";
    public string            AccentColor { get; init; } = "#C89B3C";
    public string            LineColor   { get; init; } = "#8B7CF6"; // цвет коннектор-линии
}
