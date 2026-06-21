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

    // ── Системный трей (скрытие на время игры) ────────────────────────────

    private void EnsureTray()
    {
        if (_tray != null) return;
        System.Drawing.Icon trayIcon;
        try { trayIcon = File.Exists(LogoPath) ? new System.Drawing.Icon(LogoPath) : System.Drawing.SystemIcons.Application; }
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
            ShowIdle();
        });

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
            _lastDraft = draft;
            if (engine != null) _engine = engine;
            RenderCurrentState();
        });
    }

    // ── Рендер ────────────────────────────────────────────────────────────

    private void RenderCurrentState()
    {
        var recs  = _lastRecs;
        var draft = _lastDraft;

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
        FullRecList.ItemsSource = recs.Take(6).Select((r, i) => new FullRecCard
        {
            Rank       = $"{i + 1}.",
            Name       = DataDragon.Name(r.ChampionId),
            Score      = Signed(r.Score),
            ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
            WinRate    = $"WR ~{50.0 + r.BaseDelta:F1}%",
            Icon       = IconCache.Get(r.ChampionId),
            ReasonText = string.Join("\n", r.Reasons),
            BaseBar    = ToBar(r.BaseDelta),
            DirectBar  = ToBar(r.DirectDelta),
            OtherBar   = ToBar(r.OtherDelta),
            SynBar     = ToBar(r.SynergyDelta),
            BaseText   = Signed(r.BaseDelta),
            DirectText = Signed(r.DirectDelta),
            OtherText  = Signed(r.OtherDelta),
            SynText    = Signed(r.SynergyDelta),
        }).ToList();

        if (draft != null)
        {
            var myRole = RecommendationEngine.LcuToDbRole(draft.MyPosition);
            MyTeamList.ItemsSource    = BuildSlots(draft.MyTeam,    ally: true,  _engine, myRole);
            EnemyTeamList.ItemsSource = BuildSlots(draft.TheirTeam, ally: false, _engine, myRole);

            var myCombos    = DetectCombos(draft.MyTeam,    ally: true);
            var enemyCombos = DetectCombos(draft.TheirTeam, ally: false);
            MyTeamCombos.ItemsSource     = ToCards(myCombos,    ally: true);
            EnemyTeamCombos.ItemsSource  = ToCards(enemyCombos, ally: false);
            MyCombosHeader.Visibility    = myCombos.Count    > 0 ? Visibility.Visible : Visibility.Collapsed;
            EnemyCombosHeader.Visibility = enemyCombos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // Линии-коннекторы рисуем после раскладки (контейнеры строк ещё не готовы).
            var myTeam    = draft.MyTeam;
            var enemyTeam = draft.TheirTeam;
            Dispatcher.InvokeAsync(() =>
            {
                DrawTeamLines(MyTeamLines,    MyTeamList,    myTeam,    myCombos);
                DrawTeamLines(EnemyTeamLines, EnemyTeamList, enemyTeam, enemyCombos);
            }, DispatcherPriority.Loaded);
        }
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
    private static string Signed(double v)    => (v >= 0 ? "+" : "") + v.ToString("F1");
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
