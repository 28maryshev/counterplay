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

public partial class OverlayWindow : Window
{
    private bool   _isFullMode = true;
    private const double FullW    = 1320;
    // Высота по умолчанию: чтобы в боковой колонке целиком помещалась первая
    // карточка TEAM COMBOS (заголовок + описание + how-to-play).
    private const double FullH    = 760;
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
    private HashSet<int>                    _ownedChamps = new(); // чемпионы аккаунта (пусто = данных нет)

    // Список чемпионов аккаунта из LCU: которых нет — помечаем «нет чемпиона».
    public void SetOwnedChampions(IEnumerable<int> ids) => Dispatcher.InvokeAsync(() =>
    {
        _ownedChamps = ids.ToHashSet();
        RenderCurrentState();   // перерисовать, если рекомендации уже на экране
    });

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
    // true = LCU реально подключён. Пока НЕ подключён, авто-возврат из трея по
    // слежению за окном подавлен — иначе при автозапуске оверлей вылазил бы с
    // экраном «ожидание клиента», как только окно клиента появится (но LCU ещё
    // грузится). Ставится в Program при подключении, снимается при обрыве/ожидании.
    private bool _lcuReady;

    /// Геймфлоу сообщает, идёт ли игра (или вход в неё). В это время авто-возврат
    /// оверлея из трея по слежению за окном подавлен — иначе окно всплыло бы поверх игры.
    public void SetGameActive(bool active) => _gameActive = active;

    /// LCU подключён/отключён — гейтит авто-возврат из трея (см. _lcuReady).
    public void SetLcuReady(bool ready) => _lcuReady = ready;

    /// Разрешить авто-показ снова (сбросить ручное скрытие крестиком). Зовём при
    /// входе в чемп-селект: закрыл окно раньше — на драфте оно всё равно вернётся.
    public void AllowAutoShow() => Dispatcher.InvokeAsync(() => _userHidden = false);

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
        // возвращаем окно. Условие _lcuReady КРИТИЧНО: без него при автозапуске окно
        // вылазило бы из трея с экраном «ожидание клиента», едва появится окно клиента,
        // хотя LCU ещё не подключён. Не трогаем во время игры и при ручном скрытии.
        if (_inTray && !_userHidden && !_gameActive && _lcuReady)
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

    // Оверлей «всегда сверху» только когда активен ОН САМ. Во всех прочих случаях
    // (в фокусе клиент ИЛИ постороннее приложение) снимаем Topmost и ставим окно
    // вплотную под клиент в z-порядке — тогда оверлей следует за клиентом: другое
    // окно перекрыло клиент → оно перекрывает и оверлей (раньше плашка ожидания
    // висела поверх всего). Клик по оверлею делает его активным и возвращает поверх.
    private void UpdateZOrder(IntPtr clientHwnd)
    {
        if (GetForegroundWindow() == Hwnd)
        {
            if (!Topmost) Topmost = true;           // наш оверлей активен → поверх
            return;
        }
        if (Topmost) Topmost = false;               // снимаем «всегда сверху»…
        SetWindowPos(Hwnd, clientHwnd, 0, 0, 0, 0,  // …и опускаем ровно под окно клиента
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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

        // Запоминаем ручной ресайз, чтобы перерисовки его не сбрасывали.
        SizeChanged += OnWindowSizeChanged;

        // Слежение за окном клиента (свернуть/развернуть/переместить).
        StartWindowFollow();

        // Чип языка: показываем текущий, меню — по клику.
        LangText.Text = Loc.CurrentLang.Native + " ▾";
        Loc.LanguageChanged += OnLanguageChanged;
    }

    // ── Руны и билд ──────────────────────────────────────────────────────────

    private ChampStats? _runeStats;
    private IReadOnlyList<RuneChoice> _runeChoices = [];
    private int _runeSelected;
    private int _runeChampId;
    private string _runeChampName = "";

    /// Функция импорта в клиент. Ставится из Program (в тестовом режиме — null).
    public Func<RunePage, string, Task<bool>>? ApplyRunesHandler { get; set; }
    /// (core, полный билд, ситуативные, role, championId, имя) → успех.
    public Func<IReadOnlyList<int>, IReadOnlyList<int>, IReadOnlyList<int>, string, int, string, Task<bool>>? ExportBuildHandler { get; set; }
    /// Выставить саммонер-спеллы (пара id) — вызывается вместе с рунами.
    public Func<IReadOnlyList<int>, Task<bool>>? ApplySpellsHandler { get; set; }
    /// Навести чемпиона в клиенте (hover). championId → HTTP-код (0 — исключение).
    public Func<int, Task<int>>? HoverHandler { get; set; }
    /// Залочить наведённого чемпиона (необратимо). championId → HTTP-код.
    public Func<int, Task<int>>? LockHandler { get; set; }

    /// Руна в подсказке/на кнопке: иконка, название и короткое описание.
    public sealed record RuneVm(BitmapImage? Icon, string Name, string Desc);

    /// Элемент кнопки варианта рун (привязка в XAML).
    public sealed record RuneOptionVm(
        int Index, string Label, string WinrateText, string GamesText, string Tip,
        BitmapImage? KeystoneIcon, Brush WinrateBrush, Brush Background, Brush BorderBrush,
        // Мелкие иконки на кнопке: первая строка — основное дерево (без кейстоуна),
        // вторая — вторичное. Видно, чем варианты отличаются, без наведения.
        IReadOnlyList<RuneVm> PrimaryMini,
        IReadOnlyList<RuneVm> SecondaryMini,
        // Полное дерево в подсказке.
        string TreeTitle, string SubTreeTitle,
        IReadOnlyList<RuneVm> PrimaryRunes,
        IReadOnlyList<RuneVm> SecondaryRunes,
        IReadOnlyList<RuneVm> ShardRunes);

    private string _runeRole = "";

    /// Показать панель рун/билда. stats=null — панели нет (данных мало/нет сети).
    public void ShowRunes(ChampStats? stats, int championId, string championName, string role, int? opponentId) =>
        Dispatcher.InvokeAsync(() =>
        {
            _runeStats = stats;
            _runeChampId = championId;
            _runeChampName = championName;
            _runeRole = role;
            _lastOpponentId = opponentId;

            if (stats is null || stats.Keystones.Count == 0)
            {
                RunesBar.Visibility = Visibility.Collapsed;
                TierListBar.Visibility = Visibility.Collapsed;   // тир-лист — только под банами
                return;
            }

            _runeChoices = RunesClient.Choices(stats, opponentId);
            _runeSelected = 0;
            _buildSelected = -1;   // новый чемпион — прошлый выбор сборки не в счёт
            RenderRuneOptions(opponentId);

            ApplyRunesText.Text = Loc.T("runes.apply");
            RenderBuilds(stats);

            RunesStatus.Visibility = Visibility.Collapsed;
            RunesBar.Visibility = Visibility.Visible;
            TierListBar.Visibility = Visibility.Collapsed;   // руны заняли Row 1 — тир-лист прячем
            PulseApplyButton();   // сразу подсвечиваем: вариант выбран по умолчанию
        });

    // ── Сборки ───────────────────────────────────────────────────────────────

    /// Слот предмета: CORE выделен золотой рамкой, ситуативные — тусклой.
    public sealed record SlotVm(ImageSource? Icon, string Tip, Brush Stroke, Thickness Thickness, double Dim);

    /// Строка сборки: винрейт/игры слева, 6 слотов, кнопка экспорта. Выбранная — золотом.
    public sealed record BuildRowVm(
        int Index, IReadOnlyList<SlotVm> Slots, string ExportText, string Tip,
        string WrText, Brush WrBrush, string GamesText,
        Brush RowBg, Brush RowStroke);

    private static readonly Brush CoreStroke = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C));
    private static readonly Brush AltStroke  = new SolidColorBrush(Color.FromArgb(0x55, 0x55, 0x70, 0x89));

    // Подсветка выбранной сборки — золото (в тон кнопке экспорта).
    private static readonly Brush RowOn     = new SolidColorBrush(Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C));
    private static readonly Brush RowOff    = new SolidColorBrush(Colors.Transparent);
    private static readonly Brush RowOnEdge = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C));
    private static readonly Brush RowOffEdge = new SolidColorBrush(Colors.Transparent);

    private int _buildSelected = -1;   // -1 = ничего не выбрано

    private void RenderBuilds(ChampStats stats)
    {
        if (stats.Builds.Count == 0)
        {
            BuildList.Visibility = Visibility.Collapsed;
            return;
        }

        var rows = new List<BuildRowVm>();
        for (int i = 0; i < stats.Builds.Count; i++)
        {
            var b = stats.Builds[i];
            var slots = new List<SlotVm>();

            // CORE — набор, который реально играли вместе (у него и винрейт).
            // Остальные слоты — ходовые докупки, которыми сборка добита до шести.
            var core = b.Core.Count > 0 ? b.Core.ToHashSet() : b.Items.Take(3).ToHashSet();
            var ordered = b.Items.Where(core.Contains).Concat(b.Items.Where(x => !core.Contains(x)));
            foreach (var id in ordered.Take(6))
            {
                var isCore = core.Contains(id);
                slots.Add(new SlotVm(
                    ItemIcons.GetOrLoad(id),
                    ItemIcons.NameOf(id) + (isCore ? $" · {Loc.T("runes.core")}" : ""),
                    isCore ? CoreStroke : AltStroke,
                    new Thickness(isCore ? 1.5 : 1),
                    isCore ? 1.0 : 0.85));
            }
            // Добиваем до 6 слотов пустыми рамками — сетка ровная.
            while (slots.Count < 6)
                slots.Add(new SlotVm(null, "", AltStroke, new Thickness(1), 1.0));

            var selected = i == _buildSelected;
            rows.Add(new BuildRowVm(
                Index: i,
                Slots: slots,
                ExportText: Loc.T("runes.export"),
                Tip: Loc.T("runes.buildTip", b.Winrate.ToString("0.0"), FormatGames(b.Games)),
                WrText: b.Winrate.ToString("0.0") + "%",
                WrBrush: WinrateBrush(b.Winrate),
                GamesText: FormatGames(b.Games),
                RowBg: selected ? RowOn : RowOff,
                RowStroke: selected ? RowOnEdge : RowOffEdge));
        }

        BuildList.ItemsSource = rows;
        BuildList.Visibility = Visibility.Visible;
    }

    /// Клик по строке сборки — выбор (подсветка золотом), без экспорта.
    private void BuildRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (_runeStats is null) return;
        _buildSelected = idx;
        RenderBuilds(_runeStats);
        e.Handled = true;
    }

    /// <summary>
    /// Свечение вокруг кнопки «Применить руны»: яркая вспышка при выборе варианта,
    /// затем непрерывная пульсация — кнопка «зовёт», пока руны не применены.
    /// Каждый новый выбор перезапускает анимацию с нуля.
    /// </summary>
    private void PulseApplyButton(bool on = true)
    {
        if (!on)
        {
            // Применили — свечение гасим (действие больше не требуется).
            ApplyGlowRing.BeginAnimation(OpacityProperty, null);
            ApplyGlowRing.Opacity = 0;
            if (ApplyGlowRing.Effect is DropShadowEffect g)
                g.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            return;
        }

        // ОДНА вспышка при выборе (не мигает постоянно): резко разгорается и
        // оседает на ровном свечении — кнопка остаётся подсвеченной, но не
        // отвлекает морганием.
        var op = new DoubleAnimationUsingKeyFrames();
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)),
                                                  new SineEase { EasingMode = EasingMode.EaseOut }));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
                                                  new SineEase { EasingMode = EasingMode.EaseInOut }));
        op.FillBehavior = FillBehavior.HoldEnd;
        ApplyGlowRing.BeginAnimation(OpacityProperty, op);

        // Ореол: широкая вспышка → устойчивое сильное свечение.
        if (ApplyGlowRing.Effect is DropShadowEffect glow)
        {
            var blur = new DoubleAnimationUsingKeyFrames();
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(8,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(34, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)),
                                                        new SineEase { EasingMode = EasingMode.EaseOut }));
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
                                                        new SineEase { EasingMode = EasingMode.EaseInOut }));
            blur.FillBehavior = FillBehavior.HoldEnd;
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);
        }
    }

    private void RenderRuneOptions(int? opponentId)
    {
        var opp = opponentId is { } o ? DataDragon.Name(o) : null;
        RunesTitle.Text = opp != null
            ? Loc.T("runes.titleVs", _runeChampName, opp)
            : Loc.T("runes.title", _runeChampName);

        var vms = new List<RuneOptionVm>();
        for (int i = 0; i < _runeChoices.Count; i++)
        {
            var c = _runeChoices[i];
            var total = c.Winrate + c.VsDelta;
            var selected = i == _runeSelected;

            // Подсказка: честно показываем размер выборки и вклад матчапа —
            // чтобы было видно, где цифра надёжна, а где это лишь намёк.
            var tip = new StringBuilder();
            tip.AppendLine(Loc.T("runes.tipBase", c.Winrate.ToString("0.0"), c.Games.ToString("N0")));
            if (c.VsGames > 0 && Math.Abs(c.VsDelta) >= 0.1)
                tip.AppendLine(Loc.T("runes.tipVs", (c.VsDelta >= 0 ? "+" : "") + c.VsDelta.ToString("0.0"), c.VsGames.ToString("N0")));
            tip.Append(Loc.T("runes.tipPick", c.PickRate.ToString("0")));

            RuneVm Vm(int id) => new(RuneIcons.Icon(id), RuneIcons.NameOf(id), RuneIcons.DescOf(id));

            // Неизвестные руны не показываем: Riot убирает их между сезонами
            // (например, Eyeball Collection), и в старой статистике они ещё есть —
            // рисовать «#8138» вместо названия нельзя.
            List<RuneVm> Vms(IEnumerable<int> ids) =>
                ids.Where(RuneIcons.Known).Select(Vm).ToList();

            // Основное дерево: 4 руны (первая — кейстоун), вторичное: 2, плюс осколки.
            var primary   = Vms(c.Page.Perks);
            var secondary = Vms(c.Page.Secondary);
            var shards    = Vms(c.Page.Shards);

            // На кнопке — две строки иконок: сверху остаток основного дерева,
            // снизу вторичное. Так видно структуру страницы, а кнопка остаётся узкой.
            var primaryMini   = Vms(c.Page.Perks.Skip(1));
            var secondaryMini = secondary;

            vms.Add(new RuneOptionVm(
                Index: i,
                Label: RuneIcons.NameOf(c.Keystone),
                WinrateText: total.ToString("0.0") + "%",
                GamesText: FormatGames(c.Games),
                Tip: tip.ToString(),
                KeystoneIcon: RuneIcons.Icon(c.Keystone),
                WinrateBrush: WinrateBrush(total),
                Background: new SolidColorBrush(selected
                    ? Color.FromArgb(0x28, 0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush: new SolidColorBrush(selected
                    ? Color.FromRgb(0x36, 0xD6, 0xE7)
                    : Color.FromRgb(0x2A, 0x3A, 0x4F)),
                PrimaryMini: primaryMini,
                SecondaryMini: secondaryMini,
                TreeTitle: $"{RuneIcons.NameOf(c.Keystone)} · {RuneIcons.StyleName(c.Page.Primary)}",
                SubTreeTitle: RuneIcons.StyleName(c.Page.Sub),
                PrimaryRunes: primary,
                SecondaryRunes: secondary,
                ShardRunes: shards));
        }
        // Адаптивная раскладка по числу вариантов:
        //   1 → [руна][кнопка] в один ряд, по центру;
        //   2 → две руны в верхнем ряду, кнопка под левой;
        //   3 → 2 сверху + 1 снизу, кнопка в свободной ячейке (1,1).
        foreach (var child in RuneGrid.Children.OfType<ContentPresenter>().ToList())
            RuneGrid.Children.Remove(child);

        var template = (DataTemplate)RunesBar.FindResource("RuneOptionTemplate");
        int n = Math.Min(vms.Count, 3);

        (int row, int col) Cell(int i) => n switch
        {
            1 => (0, 0),                 // одна руна слева, кнопка справа (см. ниже)
            2 => (0, i),                 // обе в верхнем ряду
            _ => (i / 2, i % 2),         // 2 сверху, 1 снизу
        };

        for (int i = 0; i < n; i++)
        {
            var cp = new ContentPresenter { Content = vms[i], ContentTemplate = template };
            var (row, col) = Cell(i);
            Grid.SetRow(cp, row);
            Grid.SetColumn(cp, col);
            RuneGrid.Children.Add(cp);
        }

        // Кнопка «Применить»: 1 → справа от руны (ряд 0, кол 1);
        //                     2 → под левой руной (ряд 1, кол 0);
        //                     3 → свободная ячейка (ряд 1, кол 1).
        var (btnRow, btnCol) = n switch
        {
            1 => (0, 1),
            2 => (1, 0),
            _ => (1, 1),
        };
        Grid.SetRow(ApplyCell, btnRow);
        Grid.SetColumn(ApplyCell, btnCol);
    }

    private static string FormatGames(int g) =>
        g >= 1000 ? $"{g / 1000.0:0.#}k" : g.ToString();

    private void RuneOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (idx < 0 || idx >= _runeChoices.Count) return;
        _runeSelected = idx;
        RenderRuneOptions(_lastOpponentId);
        PulseApplyButton();      // выбор сменился — подсветка вспыхивает заново
        RunesStatus.Visibility = Visibility.Collapsed;  // прошлый «применено» уже неактуален
        e.Handled = true;
    }

    private int? _lastOpponentId;

    private async void ApplyRunes_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ApplyRunesHandler is null || _runeChoices.Count == 0) return;
        var page = _runeChoices[_runeSelected].Page;

        RunesStatus.Text = Loc.T("runes.applying");
        RunesStatus.Foreground = MuteBrush;
        RunesStatus.Visibility = Visibility.Visible;

        var ok = await ApplyRunesHandler(page, _runeChampName);

        // Вместе с рунами выставляем и саммонер-спеллы (пара из статистики;
        // привычный слот Флеша сохраняет импортёр). Спеллы берём из выбранной
        // сборки, иначе — из первой.
        var spellsOk = false;
        if (ok && ApplySpellsHandler != null && _runeStats is { Builds.Count: > 0 })
        {
            var bIdx = _buildSelected >= 0 && _buildSelected < _runeStats.Builds.Count
                ? _buildSelected : 0;
            var spells = _runeStats.Builds[bIdx].Spells;
            if (spells.Count >= 2) spellsOk = await ApplySpellsHandler(spells);
        }

        RunesStatus.Text = Loc.T(!ok ? "runes.failed"
                                 : spellsOk ? "runes.appliedSpells" : "runes.applied");
        RunesStatus.Foreground = ok ? WinBrush : LossBrush;
        if (ok) PulseApplyButton(on: false);   // применено — кнопка больше не «зовёт»
    }

    private async void ExportBuild_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ExportBuildHandler is null || _runeStats is null) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (idx < 0 || idx >= _runeStats.Builds.Count) return;

        // Экспортируемая сборка становится выбранной — видно, что именно уехало.
        _buildSelected = idx;
        RenderBuilds(_runeStats);

        RunesStatus.Text = Loc.T("runes.exporting");
        RunesStatus.Foreground = MuteBrush;
        RunesStatus.Visibility = Visibility.Visible;

        // Три части набора: core (ключевые предметы этой сборки), полный билд из
        // 6 слотов и ситуативные — всё, что ещё часто берут на чемпионе.
        var b = _runeStats.Builds[idx];
        var core = b.Core.Count > 0 ? b.Core : b.Items.Take(2).ToList();
        var full = b.Items;
        var situational = _runeStats.Builds
            .SelectMany(x => x.Items)
            .Distinct()
            .Where(i => !full.Contains(i))
            .ToList();

        var ok = await ExportBuildHandler(core, full, situational, _runeRole, _runeChampId, _runeChampName);
        RunesStatus.Text = Loc.T(ok ? "runes.exportedToSets" : "runes.failed");
        RunesStatus.Foreground = ok ? WinBrush : LossBrush;
    }

    // ── Выбор чемпиона через интерфейс (hover кликом + кнопка лока) ───────────
    private int _pickHoverId;     // наведённый кликом чемпион (0 = нет)
    private bool _pickBusy;       // идёт запрос к клиенту
    private DateTime _pickMsgUntil; // до этого времени не затирать текст ошибки
                                    // (сессия перерисовывается каждый тик таймера)

    // Клик по карточке рекомендации: наводим чемпиона в клиенте (обратимо).
    private async void RecCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        if (HoverHandler is null) return;   // не боевой режим/нет actionId
        e.Handled = true;

        _pickHoverId = champId;
        RenderCurrentState();          // перерисуем карточки — рамка выбранного золотится
        UpdatePickBar();               // и подсветим кнопку выбранным чемпионом
        var code = await HoverHandler(champId);   // навести в клиенте
        // Если клиент не принял ховер — сразу сообщаем на кнопке (с кодом).
        if (code is < 200 or >= 300)
        {
            PickText.Text = $"{Loc.T("pick.failed")} ({code})";
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(4);
        }
    }

    // Кнопка «Выбрать»: лочим наведённого чемпиона (необратимо, только в свой ход).
    private async void PickConfirm_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (LockHandler is null || _pickHoverId <= 0 || _pickBusy) return;
        if (_lastDraft is null || !_lastDraft.MyPickInProgress)
        {
            // Не мой ход — говорим об этом, а не молчим.
            PickText.Text = Loc.T("pick.failed");
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(3);
            return;
        }

        _pickBusy = true;
        PickText.Text = Loc.T("pick.locking");
        _pickMsgUntil = DateTime.UtcNow.AddSeconds(2);
        var code = await LockHandler(_pickHoverId);
        _pickBusy = false;
        if (code is < 200 or >= 300)
        {
            // Ошибка «липнет» на 4 секунды — иначе её мгновенно затрёт
            // перерисовка от очередного события сессии.
            PickText.Text = $"{Loc.T("pick.failed")} ({code})";
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(4);
            return;
        }
        // Успех: клиент сам обновит сессию, карточки исчезнут — прячем плашку.
        PickBar.Visibility = Visibility.Collapsed;
    }

    // Показ/скрытие плашки выбора: видна только когда мой ход пикать и есть
    // наведённый кликом чемпион. Иконку/имя берём из выбранного.
    private void UpdatePickBar()
    {
        var draft = _lastDraft;
        bool canPick = HoverHandler != null && LockHandler != null
                       && draft is { MyPickInProgress: true } && _pickHoverId > 0;
        if (!canPick) { PickBar.Visibility = Visibility.Collapsed; return; }

        PickIcon.Source = IconCache.Get(_pickHoverId);
        // Пока показывается ошибка/статус — не затираем текст обычной подписью.
        if (DateTime.UtcNow >= _pickMsgUntil)
            PickText.Text = Loc.T("pick.confirm", DataDragon.Name(_pickHoverId));
        PickBar.Visibility = Visibility.Visible;
    }

    // ── Бан через интерфейс (клик по карточке бана + кнопка «Забанить») ────────
    /// Навести бан в клиенте (обратимо). championId → HTTP-код.
    public Func<int, Task<int>>? BanHoverHandler { get; set; }
    /// Завершить бан (необратимо). championId → HTTP-код.
    public Func<int, Task<int>>? BanLockHandler  { get; set; }
    private int _banHoverId;
    private bool _banBusy;
    private DateTime _banMsgUntil;

    private async void BanCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        if (BanHoverHandler is null) return;
        e.Handled = true;
        _banHoverId = champId;
        RenderCurrentState();          // перерисуем баны — рамка выбранного краснеет
        UpdateBanBar();
        var code = await BanHoverHandler(champId);
        if (code is < 200 or >= 300)
        {
            BanText.Text = $"{Loc.T("ban.failed")} ({code})";
            _banMsgUntil = DateTime.UtcNow.AddSeconds(4);
        }
    }

    private async void BanConfirm_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (BanLockHandler is null || _banHoverId <= 0 || _banBusy) return;
        if (_lastDraft is null || !_lastDraft.MyBanInProgress)
        {
            BanText.Text = Loc.T("ban.failed");
            _banMsgUntil = DateTime.UtcNow.AddSeconds(3);
            return;
        }
        _banBusy = true;
        BanText.Text = Loc.T("ban.banning");
        _banMsgUntil = DateTime.UtcNow.AddSeconds(2);
        var code = await BanLockHandler(_banHoverId);
        _banBusy = false;
        if (code is < 200 or >= 300)
        {
            BanText.Text = $"{Loc.T("ban.failed")} ({code})";
            _banMsgUntil = DateTime.UtcNow.AddSeconds(4);
            return;
        }
        BanBar.Visibility = Visibility.Collapsed;
    }

    // Ещё можно навести этого чемпиона? Забанённые и уже залоченные кем-то —
    // нельзя. Ховер сбрасываем ИМЕННО по этому признаку, а не по «нет в списке
    // рекомендаций»: выбор из тир-листа почти никогда не совпадает с топ-6
    // советов, и такой сброс гасил кнопку бана сразу после клика.
    private static bool StillAvailable(DraftState d, int champId) =>
        champId > 0
        && !d.MyTeamBans.Contains(champId)
        && !d.TheirTeamBans.Contains(champId)
        && d.MyTeam.Concat(d.TheirTeam).All(p => p.ChampionId != champId);

    private void UpdateBanBar()
    {
        var draft = _lastDraft;
        bool canBan = BanHoverHandler != null && BanLockHandler != null
                      && draft is { MyBanInProgress: true } && _banHoverId > 0;
        if (!canBan) { BanBar.Visibility = Visibility.Collapsed; return; }

        BanIcon.Source = IconCache.Get(_banHoverId);
        if (DateTime.UtcNow >= _banMsgUntil)
            BanText.Text = Loc.T("ban.confirm", DataDragon.Name(_banHoverId));
        BanBar.Visibility = Visibility.Visible;
    }

    // Клик по чемпиону в тир-листе: в банфазе — наводим бан, иначе — пик.
    // Переиспользуем те же обработчики, что и карточки рекомендаций.
    private void TierCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        e.Handled = true;
        if (_lastDraft?.InBanPhase == true)
        {
            if (BanHoverHandler is null) return;
            _banHoverId = champId;
            RenderCurrentState();
            UpdateBanBar();
            _ = BanHoverHandler(champId);
        }
        else
        {
            if (HoverHandler is null) return;
            _pickHoverId = champId;
            RenderCurrentState();
            UpdatePickBar();
            _ = HoverHandler(champId);
        }
    }

    // ── Тир-лист патча (лучшие по WR на роль) — под банами ────────────────────
    private IReadOnlyList<TierRoleCol>? _tierCols;   // кэш: статичен в пределах патча

    // Цвет эмблемы по грейду — как ранги в LoL, все оттенки различимы.
    private static string GradeColor(char g) => g switch
    {
        'S' => "#FFD23C",   // золото (яркое)
        'A' => "#5CE0E6",   // диамант (бирюза)
        'B' => "#CD7F32",   // бронза
        'C' => "#B7C2CC",   // серебро
        _   => "#6E5140",   // тёмно-коричневый (D) — как ранг Iron
    };

    private static string WrBrush(double wr) =>
        wr >= 52 ? "#4CD08A" : wr >= 50 ? "#C9D2DC" : "#E0806A";

    private TierCell TierCellOf(RecommendationEngine.TierEntry t, bool showGrade) => new()
    {
        ChampionId = t.ChampionId,
        Icon    = IconCache.Get(t.ChampionId),
        WrText  = $"{t.Winrate:F1}%",
        WrBrush = WrBrush(t.Winrate),
        Grade   = t.Grade.ToString(),
        GradeColor = GradeColor(t.Grade),
        ShowGrade  = showGrade,
        Tip     = $"{DataDragon.Name(t.ChampionId)}"
                + (showGrade ? $" · {t.Grade}" : "") + "\n"
                + $"WR {t.Winrate:F1}%  ·  {Loc.T("tier.pick")} {t.PickRate:F1}%"
                + (t.BanRate > 0 ? $"  ·  {Loc.T("tier.ban")} {t.BanRate:F1}%" : "")
                + $"  ·  {t.Games} " + Loc.T("tier.games"),
    };

    private void RenderTierList()
    {
        if (_engine is null) { TierListBar.Visibility = Visibility.Collapsed; return; }
        if (_tierCols is null)
        {
            // Две разбивки: по чистому винрейту (без грейдов) и по meta-тиру.
            var byWr   = _engine.TierList(15, byWinrate: true).GroupBy(t => t.Role)
                                 .ToDictionary(g => g.Key, g => g.ToList());
            var byTier = _engine.TierList(15, byWinrate: false).GroupBy(t => t.Role)
                                 .ToDictionary(g => g.Key, g => g.ToList());

            var cols = new List<TierRoleCol>();
            foreach (var role in new[] { "top", "jungle", "mid", "adc", "support" })
            {
                if (!byTier.TryGetValue(role, out var tierList)) continue;
                cols.Add(new TierRoleCol
                {
                    RoleLabel = RoleNameDb(role),
                    RoleIcon  = RoleIcons.Get(DbToLcuRole(role)),
                    WrCells   = byWr.GetValueOrDefault(role, new()).Select(t => TierCellOf(t, showGrade: false)).ToList(),
                    TierCells = tierList.Select(t => TierCellOf(t, showGrade: true)).ToList(),
                });
            }
            _tierCols = cols;
            // Заголовок с именем бакета игрока: «Тир-лист · Изумруд».
            TierTitle.Text = Loc.T("tier.title", Loc.T(_engine.TierBucketLocKey));
        }
        // Источник ставим ОДИН раз (список статичен в пределах патча) — иначе
        // переприсвоение на каждом событии драфта зря пересобирало бы 150 эмблем.
        if (!ReferenceEquals(TierList.ItemsSource, _tierCols))
            TierList.ItemsSource = _tierCols;
        TierListBar.Visibility = _tierCols.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // db-роль (mid/adc/support) → lcu-позиция (middle/bottom/utility) для иконок.
    private static string DbToLcuRole(string db) => db switch
    {
        "mid"     => "middle",
        "adc"     => "bottom",
        "support" => "utility",
        _         => db,
    };

    // Клик по ссылке в плашке беты — открываем в системном браузере.
    private void OpenLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch { /* нет браузера/URL битый — молча игнорируем */ }
        e.Handled = true;
    }

    public void HideRunes() => Dispatcher.InvokeAsync(() => RunesBar.Visibility = Visibility.Collapsed);

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
        _tierCols = null;             // роли/тултипы тир-листа под новую локаль
        // Обоснования рекомендаций — ГОТОВЫЕ строки, собранные движком через Loc.T
        // на прежнем языке. Перерисовка их не переводит — перегенерируем движком из
        // сохранённого драфта, иначе центральная колонка остаётся на старом языке.
        if (_engine is not null && _lastDraft is not null && _lastRecs is not null)
            _lastRecs = _lastDraft.IsAram ? _engine.RecommendAram(_lastDraft) : _engine.Recommend(_lastDraft);
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
            // Имена чемпионов + названия/описания рун + названия предметов — всё
            // под новую локаль (руны/предметы приходят из Data Dragon переведёнными).
            await DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
            await RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
            await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None);
            await Dispatcher.InvokeAsync(() =>
            {
                RenderCurrentState();
                // Панель рун открыта — перерисовать с переведёнными названиями.
                if (_runeStats is not null && RunesBar.Visibility == Visibility.Visible)
                    ShowRunes(_runeStats, _runeChampId, _runeChampName, _runeRole, _lastOpponentId);
            });
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
            var (w, h) = (_savedFullW, _savedFullH);
            _isFullMode = true;
            _settingSize = true;
            SizeToContent  = SizeToContent.Manual;
            Width          = w;
            Height         = h;
            MinWidth       = MinW;
            MinHeight      = MinH;
            _settingSize = false;
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
    private string _lastNick = "";

    public void ShowSession(SessionTracker.SessionData? d) =>
        Dispatcher.InvokeAsync(() =>
        {
            // Сменился аккаунт — ручной выбор очереди с прошлого не переносим:
            // у нового аккаунта своя настройка (и своя статистика).
            if (d is not null && d.Nick != _lastNick)
            {
                _lastNick = d.Nick;
                _queueUserSet = false;
            }

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

        // Вертикальная ось + горизонтальная сетка со шкалой винрейта.
        // 5 уровней (верх / ¾ / середина / ¼ / низ) — с промежуточными значениями.
        WrChart.Children.Add(new System.Windows.Shapes.Line
        { X1 = plotL, X2 = plotL, Y1 = padY, Y2 = baseY, Stroke = gridBrush, StrokeThickness = 1 });
        foreach (var frac in new[] { 1.0, 0.75, 0.5, 0.25, 0.0 })
        {
            double wr = min + (max - min) * frac;
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

        // Подписи дат под графиком. Крайние — всегда; промежуточные ставим не
        // равномерно, а в точках смены направления линии (локальные пики/впадины).
        // Диапазон делим на 4 части и в каждой берём самую заметную точку разворота
        // — если в этой части график вообще менял направление.
        var occupied = new List<(double L, double R)>();
        AddDateLabel(pts[0].Date, X(0), h, Align.Left, occupied);
        if (pts.Count > 1)
        {
            AddDateLabel(pts[last].Date, X(last), h, Align.Right, occupied);

            // Точки разворота: где знак наклона меняется. Prom — «заметность»
            // (насколько резкий пик/впадина), по ней выбираем главный разворот.
            static int Sgn(double d) => d > 0.05 ? 1 : d < -0.05 ? -1 : 0;
            var turns = new List<(int Idx, double Prom)>();
            for (int i = 1; i < last; i++)
            {
                int s1 = Sgn(pts[i].Winrate - pts[i - 1].Winrate);
                int s2 = Sgn(pts[i + 1].Winrate - pts[i].Winrate);
                if (s1 != 0 && s2 != 0 && s1 != s2)
                    turns.Add((i, Math.Abs(pts[i].Winrate - pts[i - 1].Winrate)
                                 + Math.Abs(pts[i + 1].Winrate - pts[i].Winrate)));
            }

            // По одной точке-развороту на каждую из 4 частей (где разворот был).
            var picks = new List<(int Idx, double Prom)>();
            for (int b = 0; b < 4; b++)
            {
                double lo = (double)b / 4 * last, hi = (double)(b + 1) / 4 * last;
                (int Idx, double Prom) best = (-1, 0);
                foreach (var t in turns)
                    if (t.Idx >= lo && t.Idx < hi && t.Prom > best.Prom) best = t;
                if (best.Idx > 0) picks.Add(best);
            }

            // Ставим по убыванию заметности; AddDateLabel пропустит наезжающие.
            foreach (var p in picks.OrderByDescending(p => p.Prom))
                AddDateLabel(pts[p.Idx].Date, X(p.Idx), h, Align.Center, occupied);
        }
    }

    private enum Align { Left, Center, Right }

    // Рисует подпись даты, если она не наезжает на уже стоящие (occupied —
    // занятые по горизонтали интервалы). Крайние подписи ставятся всегда первыми.
    private void AddDateLabel(DateTime date, double x, double h, Align align,
                             List<(double L, double R)> occupied)
    {
        var tb = new TextBlock
        {
            Text = date.ToString("d.MM"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 9, Foreground = MuteBrush, Opacity = 0.7
        };
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double left = align switch
        {
            Align.Right  => x - tb.DesiredSize.Width,
            Align.Center => x - tb.DesiredSize.Width / 2,
            _            => x,
        };
        left = Math.Max(0, left);
        double right = left + tb.DesiredSize.Width;
        foreach (var (l, r) in occupied)
            if (left < r + 3 && l < right + 3) return;   // пересекается — пропускаем
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, h - 11);
        WrChart.Children.Add(tb);
        occupied.Add((left, right));
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

    // Идёт программная установка размера (RestoreModeSize/OnToggle): такие
    // SizeChanged НЕ запоминаем. Иначе клобер: RestoreModeSize ставит Width,
    // SizeChanged срабатывает с ЕЩЁ СТАРОЙ маленькой высотой (после айдл-экрана)
    // и затирает _savedFullH — следующая строка Height=_savedFullH читает уже
    // испорченное значение, и окно навсегда сжимается по высоте.
    private bool _settingSize;

    // Пользователь вручную растянул окно в активном полном режиме — запоминаем
    // новый размер. Иначе следующая же перерисовка (пик/бан/смена фазы) вызовет
    // RestoreModeSize и вернёт старый _savedFullW/_savedFullH, обнулив ресайз.
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_settingSize
            && _isFullMode
            && SizeToContent == SizeToContent.Manual         // не idle/компакт (там авто-высота)
            && IdlePanel.Visibility != Visibility.Visible)   // не экран ожидания
        {
            _savedFullW = e.NewSize.Width;
            _savedFullH = e.NewSize.Height;
        }
    }

    // Возврат к размеру активного режима (полный/компактный) при появлении пиков.
    private void RestoreModeSize()
    {
        StatusText.Visibility = Visibility.Visible;
        IdlePanel.Visibility  = Visibility.Collapsed;

        if (_isFullMode)
        {
            // Локальные копии + флаг: установка Width дергает SizeChanged до
            // установки Height, и без защиты сохранённая высота затиралась.
            var (w, h) = (_savedFullW, _savedFullH);
            _settingSize = true;
            SizeToContent = SizeToContent.Manual;
            MinWidth  = MinW;
            MinHeight = MinH;
            Width     = w;
            Height    = h;
            _settingSize = false;
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

    public void UpdateBans(IReadOnlyList<BanRec>? bans, DraftState? draft,
                           RecommendationEngine? engine = null) =>
        Dispatcher.InvokeAsync(() =>
        {
            if (engine != null) _engine = engine;   // нужен тир-листу (банфаза идёт до пиков)
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
                ChampionId = r.ChampionId,
                Rank       = $"{r.Rank}.",
                IsMyPick   = r.IsMyPick,
                IsSelected = r.ChampionId == _pickHoverId,
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
                // Чемпиона нет на аккаунте (только если владение вообще известно).
                NotOwned      = _ownedChamps.Count > 0 && !_ownedChamps.Contains(r.ChampionId),
                NotOwnedLabel = Loc.T("rec.notOwned"),
            };
        }).ToList();

        RecScroll.Visibility = Visibility.Visible;   // показываем пики
        BanScroll.Visibility = Visibility.Collapsed;
        BanBar.Visibility      = Visibility.Collapsed;  // бан-плашка — только в банфазе
        TierListBar.Visibility = Visibility.Collapsed;  // тир-лист — только под банами
        // Пики заполняют список (звёздная строка), руны — по контенту (Auto).
        CenterGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        CenterGrid.RowDefinitions[1].Height = GridLength.Auto;
        // Сбрасываем наведённого, только если его уже забанили/взяли — а не
        // просто «выпал из рекомендаций» (иначе гасили бы свой же выбор).
        if (_pickHoverId > 0 && draft != null && !StillAvailable(draft, _pickHoverId))
            _pickHoverId = 0;
        UpdatePickBar();
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
            ChampionId = b.ChampionId,
            IsSelected = b.ChampionId == _banHoverId,
            Rank   = $"{i + 1}.",
            Name   = DataDragon.Name(b.ChampionId),
            Reason = b.Reasons.FirstOrDefault() ?? "",
            Icon   = IconCache.Get(b.ChampionId),
        }).ToList();

        RecScroll.Visibility = Visibility.Collapsed; // показываем баны
        BanScroll.Visibility = Visibility.Visible;
        PickBar.Visibility   = Visibility.Collapsed;  // пик в банфазе не нужен
        // Баны — 100% приоритет: их строка занимает всю нужную высоту (Auto),
        // тир-лист довольствуется остатком (звёздная строка со скроллом внутри).
        CenterGrid.RowDefinitions[0].Height = GridLength.Auto;
        CenterGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        // Сбрасываем наведённого, только если его уже забанили/взяли (выбор из
        // тир-листа при этом сохраняется — он вне списка рекомендаций).
        if (_banHoverId > 0 && !StillAvailable(draft, _banHoverId)) _banHoverId = 0;
        UpdateBanBar();
        RenderTierList();
        RenderTeams(draft);
    }

    // Команды по бокам (слоты, стиль, связки, линии) — общее для пиков и банов.
    private void RenderTeams(DraftState draft)
    {
        var myRole = RecommendationEngine.LcuToDbRole(draft.MyPosition);
        MyTeamList.ItemsSource    = BuildSlots(draft.MyTeam,    ally: true,  _engine, myRole,
                                               null, draft.ActiveCells, draft.FirstPickCell);
        EnemyTeamList.ItemsSource = BuildSlots(draft.TheirTeam, ally: false, _engine, myRole,
                                               _enemyRoleOverrides, draft.ActiveCells, draft.FirstPickCell);

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
        IReadOnlyDictionary<int, string>? roleOverrides = null,
        IReadOnlyList<int>? activeCells = null, int firstPickCell = -1)
    {
        return Enumerable.Range(0, 5).Select(i =>
        {
            if (i >= players.Count) return EmptySlot(i + 1, ally);

            var p        = players[i];
            var champId  = p.ChampionId > 0 ? p.ChampionId : p.PickIntentId;
            var isLocked = p.ChampionId > 0;
            var hasChamp = champId > 0;
            var isMe     = p.IsLocalPlayer;
            // Мигание: ход этого игрока пикать прямо сейчас (и ещё не залочен).
            var isPicking = !isLocked && activeCells?.Contains(p.CellId) == true;

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
                IsPicking   = isPicking,
                // Союзник пикает — синяя волна слева, враг — красная справа.
                PickGradStrong  = ally ? "#5C36D6E7" : "#5CFF5A4D",
                PickGradWeak    = ally ? "#1A36D6E7" : "#1AFF5A4D",
                PickAccentColor = ally ? "#36D6E7"   : "#FF5A4D",
                PickMirror      = ally ? 1.0 : -1.0,
                IsFirstPick = p.CellId == firstPickCell,
                FirstPickLabel = Loc.T("slot.firstPick"),
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
    public int          ChampionId { get; init; }   // для бана из оверлея
    public bool         IsSelected { get; init; }   // наведён кликом (рамка)
    public string       CardBg     => IsSelected ? "#2AC84040" : "#1EC84040";
    public string       CardBorder => IsSelected ? "#F0684F" : "#00000000";
    public string       Rank       { get; init; } = "";
    public string       Name       { get; init; } = "";
    public string       Score      { get; init; } = "";
    public string       ScoreColor { get; init; } = "#C89B3C";
    public string       Reason     { get; init; } = "";
    public ImageSource? Icon       { get; init; }
}

/// <summary>Ячейка тир-листа (лучшие по WR на роль) под банами.</summary>
public sealed class TierCell
{
    public int          ChampionId { get; init; }
    public ImageSource? Icon       { get; init; }
    public string       WrText     { get; init; } = "";
    public string       WrBrush    { get; init; } = "#8AA0B2";
    public string       Grade      { get; init; } = "";   // S/A/B/C/D
    public string       GradeColor { get; init; } = "#C89B3C"; // цвет эмблемы и буквы
    public string       Tip        { get; init; } = "";
    // У WR-столбца грейдов нет: скрываем бейдж, рамку красим нейтрально.
    public bool         ShowGrade  { get; init; } = true;
    public Visibility   GradeVisibility => ShowGrade ? Visibility.Visible : Visibility.Collapsed;
    public string       FrameColor => ShowGrade ? GradeColor : "#3A4B5F";
}

/// <summary>Колонка роли в тир-листе: два вертикальных списка — по WR и по тиру.</summary>
public sealed class TierRoleCol
{
    public string           RoleLabel { get; init; } = "";
    public ImageSource?     RoleIcon  { get; init; }
    public List<TierCell>   WrCells   { get; init; } = [];   // по винрейту, без грейдов
    public List<TierCell>   TierCells { get; init; } = [];   // по meta-score, с грейдами
}

public sealed class FullRecCard
{
    public int          ChampionId { get; init; }   // для hover/lock в клиенте
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

    // Наведён кликом через интерфейс — вся рамка светится золотом.
    // Толщина рамки у ВСЕХ карточек одинаковая (1.5): разная толщина сдвигала
    // содержимое выбранной карточки на 1px — ряды «плыли» относительно соседних.
    public bool         IsSelected { get; init; }
    public string       CardBg     => IsSelected ? "#2AC89B3C" : IsMyPick ? "#1E36D6E7" : "#1EC89B3C";
    public string       CardBorder => IsSelected ? "#F0C24B" : IsMyPick ? "#36D6E7" : "#00000000";

    // Чемпиона нет на аккаунте — красная рамка + надпись «нет чемпиона».
    public bool         NotOwned      { get; init; }
    public string       NotOwnedLabel { get; init; } = "";
    public Visibility   NotOwnedVisibility => NotOwned ? Visibility.Visible : Visibility.Collapsed;
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

    // Ход этого игрока пикать прямо сейчас → подсветка строки цветом команды:
    // вспышка → волна-градиент от края (левый у союзников, правый у врагов).
    public bool         IsPicking       { get; init; }
    public Visibility   PickVisibility  => IsPicking ? Visibility.Visible : Visibility.Collapsed;
    public string       PickGradStrong  { get; init; } = "#00000000"; // край волны (ярко)
    public string       PickGradWeak    { get; init; } = "#00000000"; // центр (не до нуля)
    public string       PickAccentColor { get; init; } = "#00000000"; // полоска + вспышка
    public double       PickMirror      { get; init; } = 1.0;         // -1 = волна справа (враг)
    // Стартовые значения трансформов. Через свойства (Binding), чтобы WPF не
    // заморозил трансформы в шаблоне — замороженные анимировать нельзя.
    public double       PickScaleX      => 0;    // волна начинается с нулевой ширины
    public double       SweepStartX     => -90;  // блик стартует за левым краем
    // Бейдж «1st pick» у игрока, пикающего первым в очереди драфта.
    public bool         IsFirstPick     { get; init; }
    public string       FirstPickLabel  { get; init; } = "";
    public Visibility   FirstPickVisibility =>
        IsFirstPick ? Visibility.Visible : Visibility.Collapsed;
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
