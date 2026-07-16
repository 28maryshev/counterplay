using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Counterplay;

/// <summary>
/// Тестовый режим (запуск: dotnet run test) — песочница без клиента LoL.
/// Открывает панель, где вручную расставляются союзники/враги и роль игрока;
/// оверлей показывает живые рекомендации, как в настоящем драфте. Роли врагов
/// не задаются — их можно назначать кликом в карточке врага (как в бою).
/// </summary>
static class TestMode
{
    public static async Task RunAsync(OverlayWindow overlay, CancellationToken ct)
    {
        // Та же подготовка, что в боевом режиме: статика, иконки, база.
        overlay.ShowStatus(Loc.T("status.loadingChamps"));
        await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
        overlay.ShowStatus(Loc.T("status.loadingIcons"));
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        await RoleIcons.PreloadAsync(ct);
        await ItemIcons.PreloadAsync(ct);
        await DataDb.EnsureAsync((msg, frac) => overlay.ShowProgress(msg, frac), ct);

        // Руны: реальных данных в базе ещё нет — в тестовом режиме панель
        // наполняется правдоподобными моками, чтобы обкатать вид и кнопки.
        RunesClient.UseMock = true;
        await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
        await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, ct);
        await RunesClient.LoadManifestAsync(ct);

        // Импорт в клиент из теста не делаем (клиента может не быть) —
        // кнопки отвечают «как будто получилось», чтобы проверить сценарий.
        TestPanel? panel = null;
        overlay.ApplyRunesHandler  = async (_, _) => { await Task.Delay(400, ct); return true; };
        overlay.ApplySpellsHandler = async _ => { await Task.Delay(200, ct); return true; };
        overlay.ExportBuildHandler = async (_, _, _, _, _, _) => { await Task.Delay(400, ct); return true; };
        overlay.HoverHandler = async _ => { await Task.Delay(150, ct); return 200; };
        // Лок из оверлея ставит чемпиона в мой слот тестовой панели — пик через
        // интерфейс работает в песочнице как в бою (и снимает паузу авто-драфта).
        overlay.LockHandler  = async id => { await Task.Delay(300, ct); panel?.LockMy(id); return 200; };

        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is null)
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        var engine = RecommendationEngine.Create(dbPath, "emerald");

        overlay.Dispatcher.Invoke(() =>
        {
            overlay.ShowReady(Loc.T("status.readyIdle") + " · TEST");
            panel = new TestPanel(overlay, engine);
            panel.Show();
        });

        await Task.Delay(Timeout.Infinite, ct); // живём до закрытия окна/Ctrl+C
    }
}

/// <summary>Панель тестового драфта: 5 своих (с ролями) + 5 врагов + фаза банов.</summary>
sealed class TestPanel : Window
{
    private static readonly string[] LcuRoles  = ["top", "jungle", "middle", "bottom", "utility"];
    private static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];

    private readonly OverlayWindow _overlay;
    private readonly RecommendationEngine _engine;
    private readonly Dictionary<string, int> _idByName;   // имя чемпиона → id
    private readonly List<string> _names;                 // "—" + имена по алфавиту

    private readonly ComboBox[]   _ally  = new ComboBox[5];
    private readonly ComboBox[]   _enemy = new ComboBox[5];
    private readonly RadioButton[] _meRadio = new RadioButton[5];
    private readonly CheckBox     _banPhase;
    private bool _ready; // подавляет пересчёт во время построения UI

    // ── Авто-драфт: условные игроки пикают по очереди, 10 с на ход ──────────
    private readonly Button _simBtn;
    private DispatcherTimer? _simTimer;
    private int _simTurn = -1;              // индекс группы в SimGroups; -1 = не идёт
    private DateTime _turnStart;
    private readonly Random _rng = new();
    // Настоящий порядок драфта LoL: первый пик — 1 чемпион, дальше команды
    // пикают ПО ДВА одновременно, замыкает один. Свои cellId 0..4, враги 5..9.
    // B1 | R1+R2 | B2+B3 | R3+R4 | B4+B5 | R5 — в парных ходах подсвечиваются
    // и пикают сразу два игрока (как в реальном champ select).
    private static readonly int[][] SimGroups =
        [[0], [5, 6], [1, 2], [7, 8], [3, 4], [9]];
    private const int TurnSeconds = 10;

    public TestPanel(OverlayWindow overlay, RecommendationEngine engine)
    {
        _overlay = overlay;
        _engine  = engine;

        _idByName = DataDragon.GetAllIconUrls().Keys
            .ToDictionary(id => DataDragon.Name(id), id => id);
        _names = ["—", .. _idByName.Keys.OrderBy(n => n, StringComparer.CurrentCulture)];

        Title  = "Counterplay — тестовый драфт";
        Width  = 560; Height = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Header("МОЯ КОМАНДА (точка = я)", 0, "#36D6E7"));
        root.Children.Add(Header("ВРАГИ (роли — кликом в оверлее)", 2, "#FF5A4D"));

        for (int i = 0; i < 5; i++)
        {
            // Свой ряд: радио «это я» + роль + чемпион
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            _meRadio[i] = new RadioButton
            {
                GroupName = "me", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0), IsChecked = i == 2, // по умолчанию я — мид
                ToolTip = "Мой слот"
            };
            _meRadio[i].Checked += (_, _) => Recompute();
            DockPanel.SetDock(_meRadio[i], Dock.Left);
            row.Children.Add(_meRadio[i]);

            var roleLbl = new TextBlock
            {
                Text = RoleNames[i], Width = 34, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
                FontWeight = FontWeights.Bold, FontSize = 11
            };
            DockPanel.SetDock(roleLbl, Dock.Left);
            row.Children.Add(roleLbl);
            row.Children.Add(_ally[i] = MakeCombo());

            Grid.SetRow(row, i + 1); Grid.SetColumn(row, 0);
            root.Children.Add(row);

            // Вражеский ряд: только чемпион
            var erow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var eLbl = new TextBlock
            {
                Text = $"E{i + 1}", Width = 26, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x40, 0x40)),
                FontWeight = FontWeights.Bold, FontSize = 11
            };
            DockPanel.SetDock(eLbl, Dock.Left);
            erow.Children.Add(eLbl);
            erow.Children.Add(_enemy[i] = MakeCombo());
            Grid.SetRow(erow, i + 1); Grid.SetColumn(erow, 2);
            root.Children.Add(erow);
        }

        // Нижний ряд: фаза банов + сброс
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _banPhase = new CheckBox
        {
            Content = "Фаза банов (советы по банам)",
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
        };
        _banPhase.Checked   += (_, _) => Recompute();
        _banPhase.Unchecked += (_, _) => Recompute();
        bottom.Children.Add(_banPhase);

        var reset = new Button
        {
            Content = "Сброс", Width = 90, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(0, 3, 0, 3)
        };
        reset.Click += (_, _) =>
        {
            StopSim();
            _ready = false;
            foreach (var cb in _ally.Concat(_enemy)) cb.SelectedIndex = 0;
            _ready = true;
            Recompute();
        };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Insert(0, reset);

        // Авто-драфт: условные игроки пикают по очереди LoL, 10 секунд на ход.
        _simBtn = new Button
        {
            Content = "▶ Авто-драфт", Width = 110,
            Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 7, 0)
        };
        _simBtn.Click += (_, _) => ToggleSim();
        DockPanel.SetDock(_simBtn, Dock.Right);
        bottom.Children.Insert(0, _simBtn);

        Grid.SetRow(bottom, 6); Grid.SetColumn(bottom, 0); Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        Content = root;
        _ready = true;
        Recompute();

        // Закрыл панель — выходим из приложения целиком.
        Closed += (_, _) => System.Windows.Application.Current.Shutdown();
    }

    private static TextBlock Header(string text, int col, string color)
    {
        var tb = new TextBlock
        {
            Text = text, FontWeight = FontWeights.Bold, FontSize = 12,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(tb, 0); Grid.SetColumn(tb, col);
        return tb;
    }

    private ComboBox MakeCombo()
    {
        var cb = new ComboBox
        {
            IsEditable = true, ItemsSource = _names, SelectedIndex = 0,
            IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 0, 0)
        };
        cb.SelectionChanged += (_, _) => Recompute();
        cb.LostKeyboardFocus += (_, _) => Recompute(); // подтверждение набранного текста
        return cb;
    }

    // ── Авто-драфт ───────────────────────────────────────────────────────────

    private void ToggleSim()
    {
        if (_simTurn >= 0) { StopSim(); return; }

        // Старт: чистим слоты и запускаем очередь пиков с первого игрока.
        _ready = false;
        foreach (var cb in _ally.Concat(_enemy)) cb.SelectedIndex = 0;
        _banPhase.IsChecked = false;
        _ready = true;

        _simTurn   = 0;
        _turnStart = DateTime.UtcNow;
        _simBtn.Content = "■ Стоп";
        if (_simTimer is null)
        {
            _simTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
            _simTimer.Tick += SimTick;
        }
        _simTimer.Start();
        Recompute();
    }

    private void StopSim()
    {
        _simTimer?.Stop();
        if (_simTurn < 0) return;
        _simTurn = -1;
        _simBtn.Content = "▶ Авто-драфт";
        Recompute();
    }

    private void SimTick(object? sender, EventArgs e)
    {
        if (_simTurn < 0 || _simTurn >= SimGroups.Length) { StopSim(); return; }
        if ((DateTime.UtcNow - _turnStart).TotalSeconds < TurnSeconds) return;

        var group  = SimGroups[_simTurn];
        int meCell = MeCell();

        // Время хода вышло: боты лочат пики (в парных ходах — оба), а МОЙ слот
        // сам не пикается — жду ручного выбора (комбобокс панели или кнопка
        // «Выбрать» в оверлее). Пока я не пикнул, драфт стоит на паузе.
        foreach (var cell in group)
            if (cell != meCell) AutoPick(cell);

        if (group.Contains(meCell) && ChampOf(_ally[meCell]) == 0)
        {
            _simBtn.Content = "⏸ Ваш пик…";
            return;                          // пауза до моего пика
        }

        _simTurn++;
        _turnStart = DateTime.UtcNow;
        _simBtn.Content = "■ Стоп";
        if (_simTurn >= SimGroups.Length) StopSim();
        else Recompute();
    }

    private int MeCell()
    {
        int i = Array.FindIndex(_meRadio, r => r.IsChecked == true);
        return i < 0 ? 2 : i;
    }

    // Пик из оверлея (кнопка «Выбрать»): ставим чемпиона в мой слот панели.
    // SelectionChanged сам вызовет Recompute, а SimTick снимет паузу.
    public void LockMy(int champId)
    {
        var name = DataDragon.Name(champId);
        if (_idByName.ContainsKey(name)) _ally[MeCell()].SelectedItem = name;
    }

    // Случайный ещё не занятый чемпион в слот cell (0..4 свои, 5..9 враги),
    // ПОДХОДЯЩИЙ ПО РОЛИ слота: строки идут TOP/JGL/MID/BOT/SUP у обеих команд,
    // берём тех, кто реально играет эту роль (≥20% своих игр на ней по базе).
    private void AutoPick(int cell)
    {
        var cb = cell < 5 ? _ally[cell] : _enemy[cell - 5];
        if (ChampOf(cb) != 0) return;   // уже выбран (например, я успел сам)

        var taken  = _ally.Concat(_enemy).Select(ChampOf).Where(id => id != 0).ToHashSet();
        var dbRole = RecommendationEngine.LcuToDbRole(LcuRoles[cell % 5]);
        var pool   = _idByName.Values
            .Where(id => !taken.Contains(id) && _engine.RoleShare(id, dbRole) >= 0.20)
            .ToList();
        if (pool.Count == 0)   // нет данных по ролям — фолбэк на любых свободных
            pool = _idByName.Values.Where(id => !taken.Contains(id)).ToList();
        if (pool.Count == 0) return;
        // SelectionChanged → Recompute
        cb.SelectedItem = DataDragon.Name(pool[_rng.Next(pool.Count)]);
    }

    private int ChampOf(ComboBox cb)
    {
        var text = (cb.SelectedItem as string ?? cb.Text ?? "").Trim();
        if (text.Length == 0 || text == "—") return 0;
        if (_idByName.TryGetValue(text, out var id)) return id;
        // Частичное совпадение по началу имени (пользователь мог не дописать)
        var hit = _idByName.Keys.FirstOrDefault(n => n.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        return hit != null ? _idByName[hit] : 0;
    }

    // Собирает DraftState из панели и обновляет оверлей.
    private void Recompute()
    {
        if (!_ready) return;

        int meIdx = Array.FindIndex(_meRadio, r => r.IsChecked == true);
        if (meIdx < 0) meIdx = 2;

        var my = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
        {
            var champ = ChampOf(_ally[i]);
            var isMe  = i == meIdx;
            // Мой чемпион — как ховер (PickIntent): подбор продолжает показывать список.
            my.Add(new DraftPlayer(i, isMe ? 0 : champ, isMe ? champ : 0, LcuRoles[i], isMe));
        }
        var their = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
            their.Add(new DraftPlayer(5 + i, ChampOf(_enemy[i]), 0, "", false)); // роли скрыты, как в Solo/Duo

        // Прямой оппонент: враг в строке моей роли (в бою роли врагов вычисляются
        // эвристикой/вручную; тут — по позиции в списке, этого хватает для стенда).
        var opp = their[meIdx].EffectiveChampionId > 0 ? their[meIdx] : null;

        // В тесте считаем, что сейчас мой ход пикать (кроме банфазы) — чтобы
        // работала кнопка выбора чемпиона через интерфейс. actionId условный.
        bool myPick = _banPhase.IsChecked != true;

        // Чей ход: при авто-драфте — текущая группа SimGroups (в парных ходах
        // подсвечиваются сразу двое), иначе мой слот.
        List<int> active;
        int firstPick;
        bool myTurn;
        if (_simTurn >= 0 && _simTurn < SimGroups.Length)
        {
            active    = SimGroups[_simTurn].ToList();
            firstPick = SimGroups[0][0];
            myTurn    = SimGroups[_simTurn].Contains(meIdx);
        }
        else
        {
            active    = myPick ? [meIdx] : [];
            firstPick = 0;
            myTurn    = myPick;
        }

        var draft = new DraftState(
            my, their, [], [], my[meIdx], LcuRoles[meIdx],
            opp, false, _banPhase.IsChecked == true, [], false,
            myPick ? 1 : -1, myPick && myTurn, active, firstPick);

        if (_banPhase.IsChecked == true)
        {
            _overlay.UpdateBans(_engine.RecommendBans(draft), draft);
            _overlay.HideRunes();
        }
        else
        {
            _overlay.UpdateRecommendations(_engine.Recommend(draft), draft, _engine);
            _ = Program.UpdateRunesAsync(_overlay, draft, CancellationToken.None);
        }
    }
}
