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

/// <summary>Панель тестового драфта: 5 своих (с ролями) + 5 врагов + фаза банов.</summary>
// Панель песочницы — каркас: поля, построение панели, мой слот и пересчёт драфта в окно.
// Профиль и этапы, слоты и друг, симуляции пиков и банов — в соседних TestPanel.*.cs.
sealed partial class TestPanel : Window
{
    private static readonly string[] LcuRoles  = ["top", "jungle", "middle", "bottom", "utility"];

    private static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];

    // Роли строк (изначально TOP/JGL/MID/BOT/SUP сверху вниз). Кнопка «🔀 Роли»
    // перемешивает их — так порядок пика (по номеру строки) перестаёт совпадать
    // с ролями, как в настоящем драфте, где первым пикает кто угодно.
    // У врагов СВОЙ независимый порядок: после перемешивания он гарантированно
    // не совпадает с моим — очередь пика врага-визави отличается от моей.
    private readonly string[] _rowRoles   = [.. LcuRoles];

    private readonly string[] _enemyRoles = [.. LcuRoles];

    private readonly ComboBox[] _roleCombos      = new ComboBox[5];  // роль строки вручную

    private readonly ComboBox[] _enemyRoleCombos = new ComboBox[5];

    private readonly OverlayWindow _overlay;

    private readonly RecommendationEngine _engine;

    private readonly List<int> _allChampIds;   // для галочки «часть чемпионов нет»

    private readonly CheckBox _missingChamps = new();

    private readonly CheckBox _emptyProfile  = new();   // «ещё ни одной игры»

    private readonly ComboBox _profile       = new();   // сколько игр сыграно

    private readonly Dictionary<string, int> _idByName;   // имя чемпиона → id

    private readonly List<string> _names;                 // "—" + имена по алфавиту

    private readonly ComboBox[]   _ally  = new ComboBox[5];

    private readonly ComboBox[]   _enemy = new ComboBox[5];

    private readonly RadioButton[] _meRadio = new RadioButton[5];

    // Строку «я» или её роль выбрали руками — «Баны» её не переставляют на
    // основную роль по журналу: так смотрят, как окно работает на чужой линии,
    // в том числе без единой игры на ней. Пока не выбирали — основная роль.
    private bool _meChosen;

    private enum TestStage { Draft, Bans, Ready }

    // По умолчанию — экран Ready (ник/ранг/пул). Драфт и баны — ТОЛЬКО по кнопке.
    private TestStage _stage = TestStage.Ready;

    private readonly Button _stageDraft = new();

    private readonly Button _stageBans  = new();

    private readonly Button _stageReady = new();

    // ── Поиск игры: экран ожидания в фазе поиска, потом баны и пики ─────────
    private readonly Button _searchBtn = new();

    private DispatcherTimer? _searchTimer;

    private DateTime? _searchEndAt;   // идёт поиск — до этого времени

    // Проверки укорачивают поиск, чтобы не ждать его целиком.
    internal static double SearchSeconds = 15;

    private const string SearchIdle = "🔎 Поиск игры";

    private bool _ready; // подавляет пересчёт во время построения UI

    // ── Авто-драфт: условные игроки пикают по очереди, 10 с на ход ──────────
    private readonly Button _simBtn;

    private DispatcherTimer? _simTimer;

    private int _simTurn = -1;              // индекс группы в _simGroups; -1 = не идёт

    private readonly Random _rng = new();

    // Настоящий порядок драфта LoL: первый пик — 1 чемпион, дальше команды
    // пикают ПО ДВА одновременно, замыкает один. Свои cellId 0..4, враги 5..9.
    // Кто первый — решает монетка при старте (50/50, как синяя/красная сторона):
    // мы первые:  B1 | R1+R2 | B2+B3 | R3+R4 | B4+B5 | R5
    // враг первый: R1 | B1+B2 | R2+R3 | B3+B4 | R4+R5 | B5
    private static readonly int[][] GroupsMyFirst =
        [[0], [5, 6], [1, 2], [7, 8], [3, 4], [9]];

    private static readonly int[][] GroupsEnemyFirst =
        [[5], [0, 1], [6, 7], [2, 3], [8, 9], [4]];

    private int[][] _simGroups = GroupsMyFirst;

    // Каждый бот пикает в СВОЁ время (3..10 с от начала хода) — даже в парном
    // ходе пики разнесены, и видно, как подбор реагирует на каждый по отдельности.
    private const int MaxTurnSeconds = 10;

    private readonly Dictionary<int, DateTime> _pickAt = new();   // cell → момент пика бота

    private readonly HashSet<int> _autoPicked = new();  // клетки, занятые авто-драфтом

    private bool _autoSetting;   // идёт программная установка чемпиона авто-драфтом

    private bool _settingRoles;  // идёт программная установка ролей (не рекурсировать)

    // Заранее выбранные ВРУЧНУЮ пики (клетка → чемпион): на старте авто-драфта их
    // прячем и резервируем от ботов, а на ХОДУ каждой клетки показываем — как будто
    // её владелец (я / союзник / враг) взял этого чемпиона в свою очередь. Для видео.
    private readonly Dictionary<int, int> _planned = new();

    // ── Фаза банов, как в рейтинге ──────────────────────────────────────────
    //
    // «Баны» запускают живую фазу на МОИХ данных (профиль «мой аккаунт», моя
    // основная роль по журналу). Сначала, как в клиенте, 15 секунд объявления:
    // окно показывает моих чемпионов на роли, клик — показать пик союзникам.
    // От двух до четырёх союзников наводят, кого возьмут, — один передумывает, — и подбор
    // банов защищает их пики. Боты банят каждый в свой момент из самых
    // банимых в патче. Сделал свой бан — окно показывает баны
    // команд с бан-рейтом; когда добанят все, вражеские баны раскрываются, а
    // через несколько секунд начинается авто-драфт с этими банами.
    private DispatcherTimer? _banTimer;

    private bool _banSim;                                     // фаза банов идёт

    // Конец объявления пиков (PLANNING). Пока не наступил — банить нельзя,
    // окно показывает моих чемпионов на роли. 15 секунд — как в драфте и
    // ранкеде на Ущелье (вики League: «Declaration phase: 15 seconds»).
    private DateTime? _planEndAt;

    // Проверки фазы банов укорачивают объявление, чтобы не ждать его целиком.
    internal static double PlanSeconds = 15;

    private bool Planning => _banSim && _planEndAt is { } end && DateTime.UtcNow < end;

    private bool _myBanDone;

    private readonly Dictionary<int, DateTime> _banAt = new();   // клетка бота → момент бана

    private readonly List<(int Cell, int Champ)> _bans = new();  // сделанные баны по порядку

    private readonly List<(int Cell, int Champ, DateTime At)> _hoverPlan = new();

    private readonly Dictionary<int, int> _intent = new();      // клетка союзника → наведённый

    private readonly Dictionary<int, HashSet<int>> _hoverHistory = new();

    // Все добанили — через секунду таймер банов кончается: клиент раскрывает
    // вражеские баны вместе с первым снимком пиков (так видит владелец).
    // Показ раскрытых банов держит уже само окно (HoldBanReveal).
    private DateTime? _banEndAt;

    private const double BanEndSeconds = 1;

    // Итог фазы банов — уходит в драфт: эти чемпионы недоступны ни подбору, ни ботам.
    private List<int> _ourBans = [];

    private List<int> _theirBans = [];

    // ── Друг: дуо-напарник в драфте ─────────────────────────────────────────
    //
    // Выбираешь друга — включается дуо-пул, настроенный на него, а союзник на
    // его роли становится им самим: у слота появляется его puuid. Программа
    // опознаёт напарника тем же путём, что в настоящей игре, а не запасным
    // правилом «кто взял чемпиона из половины друга». В авто-драфте друг
    // пикает из своей половины пула.
    private readonly ComboBox _mateCombo = new();

    private readonly ComboBox _mateRoleCombo = new();

    private readonly TextBlock _mateInfo = new();

    private readonly TextBlock[] _mateMark = new TextBlock[5];

    private readonly List<DuoPool?> _mateOptions = [];

    private DuoPool? _mate;          // выбранный друг (его дуо-пул); null — играю один

    private string _mateRole = "";   // его роль, LCU (top/jungle/middle/bottom/utility)

    private bool _fillingMates;      // идёт заполнение списка — выбор не обрабатывать

    private string _mateNote = "";   // строка о выбранном друге рядом со списком

    public TestPanel(OverlayWindow overlay, RecommendationEngine engine, List<int> allChampIds,
                     bool emptyProfile = false, bool firstGame = false, bool fiveGames = false)
    {
        _overlay = overlay;
        _engine  = engine;
        _allChampIds = allChampIds;

        _idByName = DataDragon.GetAllIconUrls().Keys
            // GroupBy, а не ToDictionary: если Data Dragon отдаст двух чемпионов с
            // одинаковым именем (бывает при выкатке патча), приложение не должно
            // падать — берём первого.
            .GroupBy(DataDragon.Name)
            .ToDictionary(g => g.Key, g => g.First());
        _names = ["—", .. _idByName.Keys.OrderBy(n => n, StringComparer.CurrentCulture)];

        Title  = "Counterplay — тестовый драфт";
        Width  = 620; Height = 572;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D));
        // В левый верхний угол рабочей области, с отступом от краёв (просьба
        // владельца; раньше — по центру экрана).
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Left + 8;
        Top  = SystemParameters.WorkArea.Top + 8;

        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Header("МОЯ КОМАНДА (точка = я)", 0, "#36D6E7"));
        root.Children.Add(Header("ВРАГИ (роль — списком слева)", 2, "#FF5A4D"));

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
            _meRadio[i].Checked += (_, _) => { if (_banSim) ReseatBans(); Recompute(); };
            _meRadio[i].Click += (_, _) => _meChosen = true;   // только клик мышью, не установка из кода
            DockPanel.SetDock(_meRadio[i], Dock.Left);
            row.Children.Add(_meRadio[i]);

            // Пометка «это друг» — видна только у его строки.
            _mateMark[i] = new TextBlock
            {
                Text = "друг", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x8A, 0xC8)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "Этот союзник — твой друг по дуо-пулу: программа опознаёт его по puuid, как в настоящей игре"
            };
            DockPanel.SetDock(_mateMark[i], Dock.Left);
            row.Children.Add(_mateMark[i]);

            _roleCombos[i] = MakeRoleCombo(_rowRoles, _roleCombos, i, "#8AA0B2");
            DockPanel.SetDock(_roleCombos[i], Dock.Left);
            row.Children.Add(_roleCombos[i]);
            row.Children.Add(_ally[i] = MakeCombo(i));

            Grid.SetRow(row, i + 1); Grid.SetColumn(row, 0);
            root.Children.Add(row);

            // Вражеский ряд: только чемпион
            var erow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            _enemyRoleCombos[i] = MakeRoleCombo(_enemyRoles, _enemyRoleCombos, i, "#C84040");
            DockPanel.SetDock(_enemyRoleCombos[i], Dock.Left);
            erow.Children.Add(_enemyRoleCombos[i]);
            erow.Children.Add(_enemy[i] = MakeCombo(5 + i));
            Grid.SetRow(erow, i + 1); Grid.SetColumn(erow, 2);
            root.Children.Add(erow);
        }

        // Нижний ряд: выбор ТЕСТОВОГО ЭТАПА (Драфт · Баны · Ready/пул) + сброс.
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        void InitStage(Button b, string text, TestStage s)
        {
            b.Content = text;
            b.Padding = new Thickness(11, 4, 11, 4);
            b.Margin = new Thickness(0, 0, 6, 0);
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Click += (_, _) => SetStage(s);
        }
        InitStage(_stageDraft, "Драфт", TestStage.Draft);
        InitStage(_stageBans,  "Баны",  TestStage.Bans);
        InitStage(_stageReady, "Ready / пул", TestStage.Ready);
        var stages = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        stages.Children.Add(_stageDraft);
        stages.Children.Add(_stageBans);
        stages.Children.Add(_stageReady);

        // Проверка плашки «нет чемпиона»: половина ростера считается некупленной.
        _missingChamps.Content = "Нет части чемпионов";
        _missingChamps.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));
        _missingChamps.VerticalAlignment = VerticalAlignment.Center;
        _missingChamps.Margin = new Thickness(12, 0, 0, 0);
        _missingChamps.ToolTip = "Отметить часть чемпионов как отсутствующих на аккаунте — видно предупреждение в подборе и в пуле";
        _missingChamps.Checked   += (_, _) => ApplyOwnership();
        _missingChamps.Unchecked += (_, _) => ApplyOwnership();

        // Проверка первого запуска: журнал пуст, но все элементы ready-экрана
        // на месте (пустые последние игры, пустой график, пустая полоса чемпионов).
        _emptyProfile.Content = "Профиль без игр";
        _emptyProfile.Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));
        _emptyProfile.VerticalAlignment = VerticalAlignment.Center;
        _emptyProfile.Margin = new Thickness(12, 0, 0, 0);
        _emptyProfile.ToolTip = "Показать ready-экран так, как его видит человек сразу после установки — до первой сыгранной игры";
        _emptyProfile.Checked   += (_, _) => ApplyProfileScenario();
        _emptyProfile.Unchecked += (_, _) => ApplyProfileScenario();

        // Сколько игр «уже сыграно» — от свежей установки до полной истории.
        _profile.Items.Add("Профиль: полный");
        _profile.Items.Add("Профиль: 1 игра");
        _profile.Items.Add("Профиль: 5 игр");
        _profile.Items.Add("Профиль: рывок серебро→платина");
        _profile.Items.Add("Профиль: мой аккаунт");   // MyAccountProfile
        _profile.Items.Add("Профиль: без ранга");     // UnrankedProfile
        _profile.Items.Add("Профиль: 3 игры");        // ThreeGamesProfile — порог ленты чемпионов
        _profile.Items.Add("Профиль: ARAM, 2 нормала"); // AramProfile — ленты чемпионов нет
        _profile.Items.Add("Профиль: алмаз II, 55%");   // DiamondProfile — для снимков на сайт
        // Аккаунты из журнала на этом компьютере — их экраны как есть (только
        // чтение): так видно настоящий аккаунт без ранговых, с нормалами и ARAM.
        _storedAccounts = SessionTracker.StoredAccounts();
        foreach (var (_, nick) in _storedAccounts)
            _profile.Items.Add($"Профиль: из журнала — {nick}");
        _profile.SelectedIndex = 0;
        _profile.VerticalAlignment = VerticalAlignment.Center;
        _profile.Margin = new Thickness(12, 0, 0, 0);
        _profile.Width = 150;
        _profile.ToolTip = "Что видно на ready-экране при разном количестве сыгранных игр: график появляется с пятой.\n"
                         + "«Мой аккаунт» — песочница на твоих данных: копия твоих пулов, твои игры, винрейты и связки. "
                         + "Твои настоящие пулы при этом не меняются.";
        _profile.SelectionChanged += (_, _) => ApplyProfileScenario();

        bottom.Children.Add(stages);

        var reset = new Button
        {
            Content = "Сброс", Width = 90, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(0, 3, 0, 3),
            ToolTip = "Остановить драфт и очистить слоты. Окно уходит с драфта на экран ожидания — "
                    + "настрой слоты, роль и друга и запусти заново: «Драфт», «Баны» или «Авто-драфт»",
        };
        reset.Click += (_, _) => ResetDraft();
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

        // Моментальный драфт: та же расстановка, что и у авто-драфта, но без
        // ожидания ходов. Нужен, когда драфт — не предмет проверки, а декорация:
        // снять видео, посмотреть связки, проверить вёрстку на полном составе.
        var instant = new Button
        {
            Content = "⚡ Моментально", Width = 118,
            Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Заполнить обе команды сразу. Уже выбранные чемпионы остаются на месте — "
                    + "добираются только пустые слоты"
        };
        instant.Click += (_, _) => InstantDraft();
        DockPanel.SetDock(instant, Dock.Right);
        bottom.Children.Insert(0, instant);

        // Перемешать роли строк: порядок пика перестаёт совпадать с ролями.
        var shuffle = new Button
        {
            Content = "🔀 Роли", Width = 74,
            Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(0, 0, 7, 0),
            ToolTip = "Перемешать роли по строкам — очередь пика у ролей будет разной"
        };
        shuffle.Click += (_, _) => ShuffleRoles();
        DockPanel.SetDock(shuffle, Dock.Right);
        bottom.Children.Insert(0, shuffle);

        Grid.SetRow(bottom, 6); Grid.SetColumn(bottom, 0); Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        // Настройки профиля — СВОЕЙ строкой: в ряду с кнопками этапов они не
        // помещались по ширине и просто уезжали за край окна.
        var profileRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        profileRow.Children.Add(new TextBlock
        {
            Text = "Сценарий:",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        profileRow.Children.Add(_profile);
        profileRow.Children.Add(_emptyProfile);
        profileRow.Children.Add(_missingChamps);

        // Боевой режим — ОТДЕЛЬНОЙ строкой: в общих рядах кнопка перекрывала
        // соседние («Драфт», «Баны») и путалась со сценариями песочницы.
        var live = new Button
        {
            Content = "⚔ Боевой режим", Width = 130,
            Padding = new Thickness(0, 3, 0, 3), Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Отключить песочницу и подключиться к своему клиенту LoL: "
                    + "окно свернётся в трей и поднимется, когда начнётся драфт"
        };
        live.Margin = new Thickness(0);
        live.Click += (_, _) =>
        {
            if (!ConfirmWindow.Ask(
                    "Перейти в боевой режим? Песочница закроется, программа подключится "
                    + "к твоему клиенту LoL и свернётся в трей до начала драфта.",
                    "Перейти", "Отмена", this)) return;
            StopSim();
            StopSearch();
            TestMode.RequestLiveMode();
        };
        var liveRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        liveRow.Children.Add(live);
        liveRow.Children.Add(new TextBlock
        {
            Text = "— песочница закроется, данные пойдут из твоего клиента",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });
        // Поиск игры — своей строкой под другом: сперва выбирают, с кем
        // играешь, потом встают в очередь. В бою подсказка режима пула живёт в
        // лобби и поиске, а «Баны» уводили окно в драфт сразу — рассмотреть её
        // было некогда.
        _searchBtn.Content = SearchIdle;
        _searchBtn.Width = 130;
        _searchBtn.Padding = new Thickness(0, 3, 0, 3);
        _searchBtn.VerticalAlignment = VerticalAlignment.Center;
        _searchBtn.ToolTip = $"Экран ожидания в фазе «идёт поиск игры» на {SearchSeconds:0} с — успеть посмотреть "
                           + "подсказку режима пула (отложенная «Не сейчас» снова в силе); затем объявление, баны и пики, как «Баны». "
                           + "Ещё раз — отсчёт заново, остановить — «Сброс»";
        _searchBtn.Click += (_, _) => StartSearch();
        var searchRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        searchRow.Children.Add(_searchBtn);
        searchRow.Children.Add(new TextBlock
        {
            Text = $"— {SearchSeconds:0} с поиска игры, затем баны и пики",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        });

        // Друг — своей строкой под кнопками драфта: с ним и запускают авто-драфт.
        var mateRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0)
        };
        mateRow.Children.Add(new TextBlock
        {
            Text = "Друг:",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _mateCombo.Width = 200;
        _mateCombo.VerticalAlignment = VerticalAlignment.Center;
        _mateCombo.ToolTip = "С кем играешь в дуо. Список — твои дуо-пулы: выбор включает пул, "
                           + "настроенный на этого друга. На профиле «мой аккаунт» это твои настоящие пулы";
        _mateCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingMates) return;
            var i = _mateCombo.SelectedIndex;
            SelectMate(i >= 0 && i < _mateOptions.Count ? _mateOptions[i] : null);
        };
        mateRow.Children.Add(_mateCombo);
        mateRow.Children.Add(new TextBlock
        {
            Text = "на",
            Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 6, 0)
        });
        _mateRoleCombo.ItemsSource = RoleNames;
        _mateRoleCombo.Width = 54;
        _mateRoleCombo.FontSize = 11;
        _mateRoleCombo.FontWeight = FontWeights.Bold;
        _mateRoleCombo.VerticalAlignment = VerticalAlignment.Center;
        _mateRoleCombo.IsEnabled = false;
        _mateRoleCombo.ToolTip = "Роль друга в этом драфте — его станет союзник на этой роли";
        _mateRoleCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingMates || _mate is null) return;
            var i = _mateRoleCombo.SelectedIndex;
            if (i < 0) return;
            _mateRole = LcuRoles[i];
            Recompute();
        };
        mateRow.Children.Add(_mateRoleCombo);
        _mateInfo.Foreground = new SolidColorBrush(Color.FromRgb(0x6A, 0x78, 0x86));
        _mateInfo.VerticalAlignment = VerticalAlignment.Center;
        _mateInfo.Margin = new Thickness(10, 0, 0, 0);
        _mateInfo.FontSize = 11;
        mateRow.Children.Add(_mateInfo);
        FillMates();

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(mateRow, 7); Grid.SetColumn(mateRow, 0); Grid.SetColumnSpan(mateRow, 3);
        root.Children.Add(mateRow);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(searchRow, 8); Grid.SetColumn(searchRow, 0); Grid.SetColumnSpan(searchRow, 3);
        root.Children.Add(searchRow);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(profileRow, 9); Grid.SetColumn(profileRow, 0); Grid.SetColumnSpan(profileRow, 3);
        root.Children.Add(profileRow);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(liveRow, 10); Grid.SetColumn(liveRow, 0); Grid.SetColumnSpan(liveRow, 3);
        root.Children.Add(liveRow);


        Content = root;
        _ready = true;
        UpdateStageButtons();
        // Запуск «dotnet run -- test empty»: сразу скелетон-вид ready-экрана.
        if (emptyProfile) _emptyProfile.IsChecked = true;
        if (firstGame) _profile.SelectedIndex = 1;
        if (fiveGames) _profile.SelectedIndex = 2;
        // «Мой аккаунт» запоминается: кто тестирует на своих данных, тот и
        // следующий запуск хочет начать с них.
        if (!firstGame && !fiveGames && File.Exists(MyAccountMark))
            _profile.SelectedIndex = MyAccountProfile;
        Recompute();
        // Язык меняют и посреди песочницы — в настройках, чтобы снять экран
        // на нужном языке. Подписи, собранные кодом («Готов»), иначе остались
        // бы на прежнем.
        Loc.LanguageChanged += Recompute;

        // Закрыл панель — выходим из приложения целиком.
        // Закрыли песочницу — вышли из программы. Кроме одного случая: переход в
        // боевой режим тоже закрывает панель, и раньше это убивало приложение
        // целиком (окно исчезало, клиента никто не ждал).
        Closed += (_, _) =>
        {
            Loc.LanguageChanged -= Recompute;
            if (!SwitchingToLive) System.Windows.Application.Current.Shutdown();
        };
    }

    /// Встать в строку с этой ролью.
    private void SetMyRole(string lcuRole)
    {
        var i = Array.IndexOf(_rowRoles, lcuRole);
        if (i >= 0) _meRadio[i].IsChecked = true;
    }

    private int MeCell()
    {
        int i = Array.FindIndex(_meRadio, r => r.IsChecked == true);
        return i < 0 ? 2 : i;
    }

    // Наведение и пик из оверлея: ставим чемпиона в мой слот панели.
    // SelectionChanged сам вызовет Recompute, а SimTick снимет паузу.
    /// Панель закрывают ради боевого режима, а не выхода из программы.
    public bool SwitchingToLive { get; set; }

    public void SetMy(int champId)
    {
        var name = DataDragon.Name(champId);
        if (_idByName.ContainsKey(name)) _ally[MeCell()].SelectedItem = name;
    }

    // Собирает DraftState из панели и обновляет оверлей.
    private void Recompute()
    {
        if (!_ready) return;

        // Этап Ready: экран с ником/рангом/графиком + кнопки пула (вместо подбора).
        // Идёт «поиск игры» — фаза поиска, как её присылает клиент.
        if (_stage == TestStage.Ready)
        {
            if (_searchEndAt is not null) _overlay.ShowReadyPhase("Matchmaking");
            else _overlay.ShowReady(ReadyText);
            return;
        }

        int meIdx = Array.FindIndex(_meRadio, r => r.IsChecked == true);
        if (meIdx < 0) meIdx = 2;

        // Друг — союзник на его роли, с его puuid: так его находит Party.
        var mateCell = MateCell();
        for (int i = 0; i < 5; i++)
            _mateMark[i].Visibility = i == mateCell ? Visibility.Visible : Visibility.Collapsed;
        _mateInfo.Text = _mate is not null && mateCell < 0
            ? "роль друга совпала с твоей — выбери ему другую"
            : _mateNote;

        bool banPhase = _stage == TestStage.Bans;
        var my = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
        {
            var champ = ChampOf(_ally[i]);
            var isMe  = i == meIdx;
            var puuid = i == mateCell ? MatePuuid(_mate!) : "";
            if (banPhase)
            {
                // В банах никто ещё не взят: у союзников — наведение, у меня —
                // выбранный руками или запланированный пик.
                var intent = champ != 0 ? champ
                    : isMe ? _planned.GetValueOrDefault(i) : _intent.GetValueOrDefault(i);
                my.Add(new DraftPlayer(i, 0, intent, _rowRoles[i], isMe, Puuid: puuid));
                if (intent != 0)
                {
                    if (!_hoverHistory.TryGetValue(i, out var seen)) _hoverHistory[i] = seen = [];
                    seen.Add(intent);
                }
                continue;
            }
            // Мой чемпион — как ховер (PickIntent): подбор продолжает показывать список.
            my.Add(new DraftPlayer(i, isMe ? 0 : champ, isMe ? champ : 0, _rowRoles[i], isMe,
                                   Puuid: puuid));
        }
        var their = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
            their.Add(new DraftPlayer(5 + i, ChampOf(_enemy[i]), 0, "", false)); // роли скрыты, как в Solo/Duo

        // Прямого оппонента НЕ подсказываем, как не подсказывает клиент в соло/дуо:
        // роли врагов скрыты, и оверлей раскладывает их сам — по доле игр на роли
        // или по ручной метке в карточке врага. Раньше песочница отдавала его
        // движку из своих списков ролей и считала по сведениям, которых в бою нет.
        // Роли врагов в панели нужны только авто-драфту — кого брать на линию.
        DraftPlayer? opp = null;

        // В тесте считаем, что сейчас мой ход пикать (кроме банфазы) — чтобы
        // работала кнопка выбора чемпиона через интерфейс. actionId условный.
        bool myPick = _stage != TestStage.Bans;

        // Чей ход: при авто-драфте — текущая группа _simGroups (в парных ходах
        // подсвечиваются сразу двое), иначе мой слот.
        List<int> active;
        int firstPick;
        bool myTurn;
        if (_simTurn >= 0 && _simTurn < _simGroups.Length)
        {
            active    = _simGroups[_simTurn].ToList();
            firstPick = _simGroups[0][0];
            myTurn    = _simGroups[_simTurn].Contains(meIdx);
        }
        else
        {
            active    = myPick ? [meIdx] : [];
            firstPick = 0;
            myTurn    = myPick;
        }

        // Баны: в фазе банов — наши, сделанные к этому моменту (вражеские клиент
        // прячет до конца таймера банов), на пиках — итог фазы.
        List<int> ourBans = banPhase
            ? [.. _bans.Where(b => b.Cell < 5 && b.Champ > 0).Select(b => b.Champ)] : _ourBans;
        List<int> theirBans = banPhase ? [] : _theirBans;
        if (banPhase) active = [];   // в банах пикает никто

        // Объявление: действия бана уже есть, но идёт не оно — как у клиента.
        bool planning = banPhase && Planning;
        // В объявлении пик показывают наведением — нужен номер действия пика.
        var pickAction = myPick || planning ? 1 : -1;
        var draft = new DraftState(
            my, their, ourBans, theirBans, my[meIdx], _rowRoles[meIdx],
            opp, false, banPhase, [], false,
            pickAction, myPick && myTurn, active, firstPick,
            banPhase && !_myBanDone ? 1 : -1, banPhase && !_myBanDone && !planning,
            MyBanDone: _myBanDone,    // как у клиента: сделанный бан остаётся сделанным и на пиках
            InPlanning: planning);

        if (banPhase)
        {
            _overlay.UpdateBans(_myBanDone || planning ? [] : _engine.RecommendBans(draft, _hoverHistory),
                                draft, _engine, _hoverHistory);
            _overlay.HideRunes();
        }
        else
        {
            // Длину списка берём из настроек — как в боевом режиме, иначе в
            // песочнице настройка «сколько рекомендаций» ничего не меняла.
            _overlay.UpdateRecommendations(
                _engine.Recommend(draft, AppSettings.Current.DraftCount), draft, _engine);
            _ = Program.UpdateRunesAsync(_overlay, draft, CancellationToken.None);
        }
    }
}
