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

    private readonly CheckBox _personal      = new();   // «Мастерство и пулы»; нет — голая база

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
    private readonly Button _simBtn = new();

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

    private readonly Dictionary<int, DateTime> _pickAt = new();   // cell (не я) → момент пика: наведённый берётся, пустой слот заполняет бот

    // Мой ход длиннее хода бота: успеть нажать «Выбрать» или сменить наведённого.
    // Конец хода — момент, когда клиент сам берёт наведённого.
    private const int MyTurnSeconds = MaxTurnSeconds + 5;
    private DateTime? _myTurnEnd;   // конец моего хода; null — ход не мой

    private readonly HashSet<int> _autoPicked = new();  // клетки, занятые авто-драфтом

    private bool _autoSetting;   // идёт программная установка чемпиона авто-драфтом

    private bool _settingRoles;  // идёт программная установка ролей (не рекурсировать)

    // Заранее выбранные ВРУЧНУЮ пики (клетка → чемпион): на старте авто-драфта их
    // прячем и резервируем от ботов (в банах их наводят по расписанию), а с
    // началом пиков возвращаем в слоты НАВЕДЕНИЕМ — владелец клетки (я /
    // союзник / враг) берёт его в свою очередь.
    private readonly Dictionary<int, int> _planned = new();

    // Клетки (не моя: у меня — _myLocked), где чемпион в авто-драфте только
    // наведён: заранее выбранный или выбранный до хода клетки. В свой ход она
    // его берёт (владелец, 22.11: «показывается как hover, пока не придёт его
    // очередь»). Наведения союзника окно видит, врага — нет, как у клиента.
    private readonly HashSet<int> _hoverCells = new();

    // Мой пик подтверждён кнопкой «Выбрать» в окне. До этого мой чемпион уходит
    // в драфт наведением, после — взятым, как у клиента. Руны и сборка ждут
    // именно взятого (с 16.09), и пока песочница отдавала только наведение,
    // панели рун в ней не было вовсе.
    private bool _myLocked;

    // Идёт установка чемпиона из LockMy: смена в моём слоте не снимает пик.
    private bool _locking;

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

        Title  = "Counterplay — песочница";
        Width  = 640; Height = 820;   // внизу — разбор подбора рун, он и забирает лишнюю высоту
        MinWidth = 600; MinHeight = 720;   // меньше — разбору рун не остаётся места
        Background = WindowBg;
        // В левый верхний угол рабочей области, с отступом от краёв (просьба
        // владельца; раньше — по центру экрана). Не выше рабочей области: низ
        // окна за панелью задач прятал бы боевой режим.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.WorkArea.Left + 8;
        Top  = SystemParameters.WorkArea.Top + 8;
        Height = Math.Max(MinHeight, Math.Min(Height, SystemParameters.WorkArea.Height - 16));

        Content = BuildLayout();   // TestPanel.Layout.cs
        FillMates();
        // Подбор — на голой базе, пока не включат «Мастерство и пулы».
        TestMode.UsePersonal(false);
        _overlay.RefreshPoolMode();

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

    /// Пик подтверждён в окне: чемпион в моём слоте становится взятым. Он мог
    /// уже стоять там от наведения — тогда SelectionChanged не придёт, поэтому
    /// пересчёт зовём сами.
    public void LockMy(int champId)
    {
        _locking = true;
        try { SetMy(champId); }
        finally { _locking = false; }
        _myLocked = CellChamp(MeCell()) == champId;
        Recompute();
    }

    // Собирает DraftState из панели и обновляет оверлей.
    private void Recompute()
    {
        if (!_ready) return;
        UpdateStatus();

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
            // Мой чемпион — наведение (PickIntent), пока пик не подтверждён в
            // окне; после «Выбрать» — взятый, и тогда приходят руны и сборка.
            // Союзник наводит, пока не придёт его ход: в авто-драфте — по его
            // ходам, руками — если его строка ниже моей (ManualHover).
            var hovered = isMe ? !_myLocked : _hoverCells.Contains(i) || ManualHover(i, meIdx);
            my.Add(new DraftPlayer(i, hovered ? 0 : champ, hovered ? champ : 0, _rowRoles[i], isMe,
                                   Puuid: puuid));
        }
        var their = new List<DraftPlayer>();
        // Роли скрыты, как в Solo/Duo; наведений врага клиент не присылает —
        // до своего хода наводящий враг пуст.
        for (int i = 0; i < 5; i++)
            their.Add(new DraftPlayer(5 + i, _hoverCells.Contains(5 + i) ? 0 : ChampOf(_enemy[i]), 0, "", false));

        // Прямого оппонента НЕ подсказываем, как не подсказывает клиент в соло/дуо:
        // роли врагов скрыты, и оверлей раскладывает их сам — по доле игр на роли
        // или по ручной метке в карточке врага. Раньше песочница отдавала его
        // движку из своих списков ролей и считала по сведениям, которых в бою нет.
        // Роли врагов в панели нужны только авто-драфту — кого брать на линию.
        DraftPlayer? opp = null;

        // В тесте считаем, что сейчас мой ход пикать (кроме банфазы) — чтобы
        // работала кнопка выбора чемпиона через интерфейс. actionId условный.
        // Пик подтверждён — ход сделан: клиент больше не присылает действие
        // моего пика, и кнопки «Выбрать» нет.
        bool myPick = _stage != TestStage.Bans && !_myLocked;

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
            opp, Exposed(meIdx, their), banPhase, [], false,
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
