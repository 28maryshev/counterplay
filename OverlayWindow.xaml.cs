using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

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
    private DraftState?                    _lastDraft;
    private RecommendationEngine?          _engine;

    // Привязка к окну клиента LoL: ставим один раз при появлении, дальше
    // окно свободно перетаскивается. Как только пользователь сдвинул вручную —
    // больше не привязываем (до нажатия кнопки-пина).
    private bool _userMoved;

    // Сворачивание в системный трей на время игры.
    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _inTray;
    // true = свёрнуто пользователем (крестик) — автоматический возврат не разворачивает.
    private bool _userHidden;

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
    private bool _followHidden; // в трей убрал именно фолловер (клиент свёрнут)

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
        if (!TryFindClientWindow(out var h)) return; // клиент закрыт — не трогаем

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
        menu.Items.Add("Показать", null, (_, _) => RestoreFromTray(force: true));
        menu.Items.Add("Выход",    null, (_, _) => System.Windows.Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;

        Closed += (_, _) => { _tray?.Dispose(); _tray = null; };
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
            ToggleBtn.ToolTip = "Развернуть";
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
            ToggleBtn.ToolTip = "Свернуть";
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

    // Готов и простаивает: вместо «Готов» — сноска о программе + карусель советов.
    public void ShowReady(string status) =>
        Dispatcher.InvokeAsync(() =>
        {
            ReadyStatusText.Text = status;
            DlBar.Visibility = Visibility.Collapsed;
            PulseAnim(false);
            LoadingInfo.Visibility = Visibility.Collapsed;
            ReadyInfo.Visibility   = Visibility.Visible;
            StartTips();
            ShowIdle();
        });

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

    // ── Карусель советов по пику (поле фиксированного размера, смена раз в 15 с) ──

    private static readonly string[] _tips =
    {
        "Контрпик на линии против прямого оппонента важнее общего винрейта чемпиона.",
        "Не стакай один тип урона: миксуй физический и магический, иначе один предмет врага гасит пол-команды.",
        "Команде нужен фронтлайн — кто-то должен начинать драки и принимать урон на себя.",
        "Пикай в комфорт: знакомый чемпион обычно сильнее непривычного контрпика.",
        "Против поука нужны заход и мобильность; против дайва — контроль и пил для своего кэрри.",
        "Последний пик — преимущество: подбирай под уже открытый состав врага.",
        "Бан убирает либо самых сильных в патче, либо то, против чего тебе тяжелее всего.",
        "Связки решают тимфайты: контроль+бёрст ловят одиночку, заход+пил держат кэрри живым.",
    };

    private int _tipIdx = -1;
    private System.Windows.Threading.DispatcherTimer? _tipTimer;

    // Переключение на стадию загрузки/ожидания — прячем готовность и стопаем карусель.
    private void SetLoadingMode()
    {
        LoadingInfo.Visibility = Visibility.Visible;
        ReadyInfo.Visibility   = Visibility.Collapsed;
        _tipTimer?.Stop();
    }

    private void StartTips()
    {
        if (_tipTimer == null)
        {
            _tipTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(40) };
            _tipTimer.Tick += (_, _) => NextTip();
        }
        if (_tipIdx < 0)
        {
            // случайный стартовый совет — чтобы не всегда первый
            _tipIdx = new Random().Next(_tips.Length) - 1;
            NextTip();
        }
        _tipTimer.Start();
    }

    private void NextTip()
    {
        _tipIdx = (_tipIdx + 1) % _tips.Length;
        TipText.Text = _tips[_tipIdx];
        // мягкое проявление при смене
        TipText.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                new Duration(TimeSpan.FromMilliseconds(350))));
    }

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
            _lastRecs  = recs;
            _lastBans  = null;
            _lastDraft = draft;
            if (engine != null) _engine = engine;
            RenderCurrentState();
        });
    }

    public void UpdateBans(IReadOnlyList<BanRec>? bans, DraftState? draft) =>
        Dispatcher.InvokeAsync(() =>
        {
            _lastBans  = bans;
            _lastRecs  = null;
            _lastDraft = draft;
            RenderCurrentState();
        });

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
                IdleStatusText.Text = "Фаза банов…";
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
            IdleStatusText.Text = "Жду чемп-выбор…";
            ShowIdle();
            return;
        }

        RestoreModeSize();

        var roleLabel    = draft?.MyPosition is { Length: > 0 } pos
            ? RecommendationEngine.LcuToDbRole(pos) : "—";
        var knownEnemies = draft?.TheirTeam.Count(p => p.EffectiveChampionId != 0) ?? 0;
        StatusText.Text  = knownEnemies == 0
            ? $"Роль: {roleLabel} — по составу команды:"
            : $"Роль: {roleLabel} — контрпик + синергия:";

        // Подсказка по порядку пика: пикаешь раньше врагов → риск контрпика.
        if (draft?.ExposedToCounter == true)
        {
            PickHint.Text = "⚠ Пикаешь раньше врагов — возможен контрпик. Возьми нейтральный пик " +
                            "или своп с тем, чей оппонент уже залочен.";
            PickHint.Visibility = Visibility.Visible;
        }
        else
        {
            PickHint.Visibility = Visibility.Collapsed;
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
        var roleLabel = draft.MyPosition is { Length: > 0 } pos
            ? RecommendationEngine.LcuToDbRole(pos) : "—";
        StatusText.Text = $"Роль: {roleLabel} — кого банить:";

        RecList.ItemsSource = bans.Take(6).Select((b, i) => new RecCard
        {
            Rank       = $"{i + 1}.",
            Name       = DataDragon.Name(b.ChampionId),
            Score      = "БАН",
            ScoreColor = "#E0584F",
            Reason     = b.Reasons.FirstOrDefault() ?? "",
            Icon       = IconCache.Get(b.ChampionId),
        }).ToList();
    }

    // ── Компактный вид ────────────────────────────────────────────────────

    private void RenderCompact(IReadOnlyList<Recommendation> recs)
    {
        RecList.ItemsSource = recs.Take(4).Select((r, i) => new RecCard
        {
            Rank       = $"{i + 1}.",
            Name       = DataDragon.Name(r.ChampionId),
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

        FullRecList.ItemsSource = recs.Take(6).Select((r, i) =>
        {
            var (ag, ac, at) = ArchBadge(r.ChampionId);
            return new FullRecCard
            {
                Rank       = $"{i + 1}.",
                Name       = DataDragon.Name(r.ChampionId),
                Score      = Signed(r.Score),
                ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
                WinRate    = $"WR ~{50.0 + r.BaseDelta:F1}%",
                Icon       = IconCache.Get(r.ChampionId),
                ReasonText = string.Join("\n", r.Reasons),
                BaseBar    = ToBaseBar(r.BaseDelta),
                DirectBar  = ToBar(r.DirectDelta),
                OtherBar   = ToBar(r.StyleDelta),   // строка «Против их стиля»
                SynBar     = ToBar(r.SynergyDelta),
                BaseText   = Signed(r.BaseDelta),
                DirectText = Signed(r.DirectDelta),
                OtherText  = Signed(r.StyleDelta),
                SynText    = Signed(r.SynergyDelta),
                ArchGlyph  = ag,
                ArchColor  = ac,
                ArchTip    = at,
                SynDashes  = SynDashesFor(r.ChampionId, allyIds, comboColorByName),
            };
        }).ToList();

        RecScroll.Visibility = Visibility.Visible;   // показываем пики
        BanScroll.Visibility = Visibility.Collapsed;
        if (draft != null) RenderTeams(draft);
    }

    // Полный вид для фазы банов: тот же раздел (команды + центр), но в центре баны.
    private void RenderBansFull(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        var roleLabel = draft.MyPosition is { Length: > 0 } pos
            ? RecommendationEngine.LcuToDbRole(pos) : "—";
        StatusText.Text     = $"Роль: {roleLabel} — кого банить:";
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
        EnemyTeamList.ItemsSource = BuildSlots(draft.TheirTeam, ally: false, _engine, myRole);

        var allyIds  = draft.MyTeam.Where(p => p.EffectiveChampionId != 0)
                                   .Select(p => p.EffectiveChampionId).ToList();
        var enemyIds = draft.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                      .Select(p => p.EffectiveChampionId).ToList();
        var myStyle    = ChampionTraits.StyleLabel(allyIds);
        var enemyStyle = ChampionTraits.StyleLabel(enemyIds);
        MyTeamStyle.Text    = myStyle.Length    > 0 ? $"Стиль: {myStyle}"    : "";
        EnemyTeamStyle.Text = enemyStyle.Length > 0 ? $"Стиль: {enemyStyle}" : "";

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
            TipLabel    = ally ? "КАК ИГРАТЬ" : "ОПАСНОСТЬ",
            AccentColor = ally ? "#C89B3C" : "#C84040",
            LineColor   = ComboColors[i % ComboColors.Length],
        }).ToList();

    // Цветные «черточки» под иконкой рекомендации: если кандидат входит в одну
    // из связок, уже сложившихся у союзников, — дашик цвета этой связки.
    private static List<string> SynDashesFor(
        int candidateId, List<int> allyIds, Dictionary<string, string> comboColorByName)
    {
        var result = new List<string>();
        if (candidateId == 0 || allyIds.Count == 0 || comboColorByName.Count == 0) return result;
        if (allyIds.Contains(candidateId)) return result;

        var team = allyIds.Append(candidateId).Select(id => (Id: id, Role: "")).ToList();
        foreach (var combo in TeamSynergies.Detect(team, forAlly: true))
        {
            if (!combo.ChampionIds.Contains(candidateId)) continue;          // кандидат — участник
            if (comboColorByName.TryGetValue(combo.Name, out var color)      // и связка уже есть у союзников
                && !result.Contains(color))
                result.Add(color);
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
        RecommendationEngine? engine, string myRole)
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
                    if (sideIcons.Count > 0) sideLabel = "КОНТРЫ";
                }
                else if (!p.IsLocalPlayer)
                {
                    var ids = engine.TopSynergies(champId, myRole);
                    sideIcons = ids.Select(id => IconCache.Get(id)).Where(x => x != null).Cast<ImageSource>().ToList();
                    if (sideIcons.Count > 0) sideLabel = "СИНЕРГИЯ";
                }
            }

            // Значок архетипа (камень/ножницы/бумага), определяется по чемпиону.
            var (archGlyph, archColor, archTip) = hasChamp ? ArchBadge(champId) : ("", "#888888", "");

            return new ChampSlotCard
            {
                Name        = hasChamp
                    ? (ally ? DataDragon.Name(champId).ToUpperInvariant() : $"ENEMY {i + 1}")
                    : "—",
                Role        = hasChamp ? RoleRu(p.Position) : (isMe ? "ТЫ ВЫБИРАЕШЬ" : ""),
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

    private static string RoleRu(string pos) => pos.ToLowerInvariant() switch
    {
        "top"     => "ТОП",
        "jungle"  => "ДЖУНГЛИ",
        "middle"  => "МИД",
        "bottom"  => "БОТ",
        "utility" => "ПОДДЕРЖКА",
        _         => pos.ToUpperInvariant(),
    };

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
            ChampionTraits.Arch.FrontToBack => ("✊", "#3D7EC4", "Фронт-ту-бэк"),
            ChampionTraits.Arch.Dive        => ("✌", "#D85050", "Дайв"),
            ChampionTraits.Arch.PickPoke    => ("✋", "#4CAE6A", "Пик/пок"),
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
    public double       BaseBar    { get; init; }
    public double       DirectBar  { get; init; }
    public double       OtherBar   { get; init; }
    public double       SynBar     { get; init; }
    public string       BaseText   { get; init; } = "";
    public string       DirectText { get; init; } = "";
    public string       OtherText  { get; init; } = "";
    public string       SynText    { get; init; } = "";
    public string       ArchGlyph  { get; init; } = "";
    public string       ArchColor  { get; init; } = "#888888";
    public string       ArchTip    { get; init; } = "";
    public Visibility   ArchVisibility => ArchGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public List<string> SynDashes  { get; init; } = []; // цвета связок с союзниками
}

public sealed class ChampSlotCard
{
    public string            Name        { get; init; } = "";
    public string            Role        { get; init; } = "";
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
