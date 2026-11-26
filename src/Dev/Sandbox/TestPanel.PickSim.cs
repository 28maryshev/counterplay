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

// Панель песочницы — авто-драфт: ходы ботов, мой запланированный пик, мгновенный драфт.
sealed partial class TestPanel
{
    // ── Авто-драфт ───────────────────────────────────────────────────────────

    private void ToggleSim()
    {
        if (_banSim) { StopBanSim(); Recompute(); return; }
        if (_simTurn >= 0) { StopSim(); return; }

        // Старт: чистим клетки прошлого авто-драфта. Ручные пики запоминаем —
        // с началом пиков они вернутся в слоты наведением и будут взяты в ход
        // своей клетки.
        _lastRun = SandboxRun.Auto;
        _ready = false;
        ClearBans();   // драфт с нуля — без банов прошлой фазы
        CapturePlanned();
        _ready = true;
        StartPickSim();
    }

    /// Захватываем РУЧНЫЕ пики (заполнены пользователем, не ботом) как «план»:
    /// прячем их (в банах союзники наводят по своему расписанию), с началом
    /// пиков каждый вернётся наведением (`StartPickSim`). Бот-пики прошлого
    /// раунда — чистим (пересоберутся заново). План — это и состав до
    /// драфта, который вернёт «Заново».
    private void CapturePlanned()
    {
        _planned.Clear();
        foreach (var (cell, champ) in ManualPicks()) _planned[cell] = champ;
        _setup = new(_planned);
        foreach (var cell in _autoPicked.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;
        _autoPicked.Clear();
        foreach (var cell in _planned.Keys.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;   // прячем до хода
    }

    /// Пики по очереди LoL — с нуля или сразу после фазы банов.
    private void StartPickSim()
    {
        StopSearch();
        _stage = TestStage.Draft; UpdateStageButtons();
        _overlay.RestoreFromTray(force: true);   // закрытый крестиком оверлей — вернуть

        // Монетка: кто пикает первым — мы или враги (50/50, синяя сторона).
        _simGroups = _rng.Next(2) == 0 ? GroupsMyFirst : GroupsEnemyFirst;

        // Заранее выбранные пики — в слоты наведением: каждый наводит своего с
        // начала пиков и берёт его в свой ход (владелец, 22.11). Сюда же —
        // выбранные руками за фазу банов. Мой слот — наведение по _myLocked.
        int meCell = MeCell();
        _hoverCells.Clear();
        _ready = false;   // одна перерисовка в конце, а не на каждый слот
        try
        {
            foreach (var (cell, champ) in _planned)
                if (CellChamp(cell) == 0) RevealPlanned(cell, champ);
            for (int cell = 0; cell < 10; cell++)
                if (cell != meCell && CellChamp(cell) != 0) _hoverCells.Add(cell);
        }
        finally { _ready = true; }
        _myLocked = false;   // пики только начались — никто ничего не взял

        _simTurn = 0;
        StartTurn();
        _simBtn.Content = "■ Стоп";
        if (_simTimer is null)
        {
            _simTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
            _simTimer.Tick += SimTick;
        }
        _simTimer.Start();
        Recompute();
    }

    // Остановили — дальше драфт руками: союзники ниже моей строки наводят,
    // выше — взяты (ManualHover); мой пик подтверждён — взяты все.
    private void StopSim()
    {
        _simTimer?.Stop();
        _hoverCells.Clear();
        if (_simTurn < 0) return;
        _simTurn = -1;
        _simBtn.Content = "▶ Авто-драфт";
        Recompute();
    }

    // Раздаёт клеткам текущей группы персональные моменты пика (3..25 с): кто
    // наводил — берёт наведённого, пустой слот заполняет бот. Ход — 30 секунд
    // у всех, как в ранкеде (PickTurnSeconds); кончается раньше, когда взяли
    // все. Мой ход кончается сроком: наведённого к тому времени клиент берёт
    // сам, пустой слот ждёт ручного пика (в клиенте был бы додж).
    private void StartTurn()
    {
        _pickAt.Clear();
        var now = DateTime.UtcNow;
        _turnSeconds = PickTurnSeconds;
        _turnEnd = now.AddSeconds(PickTurnSeconds);
        _myTurnEnd = null;
        int meCell = MeCell();
        foreach (var cell in _simGroups[_simTurn])
        {
            if (cell == meCell) _myTurnEnd = _turnEnd;
            else _pickAt[cell] = now.AddSeconds(_rng.Next(3, BotPickMaxSeconds + 1));
        }
    }

    // Клетка своё в этом ходе уже сделала — как у клиента, где завершённое
    // действие пропадает из «чей ход» (ActiveCells): бот взял (не наводит),
    // мой пик подтверждён.
    private bool TurnDone(int cell, int meCell) =>
        cell == meCell ? _myLocked : CellChamp(cell) != 0 && !_hoverCells.Contains(cell);

    // Ход клетки ещё впереди: авто-драфт идёт, а её группа после текущей.
    private bool TurnAhead(int cell) =>
        _simTurn >= 0 && Array.FindIndex(_simGroups, g => g.Contains(cell)) > _simTurn;

    /// <summary>
    /// Драфт руками, без авто-драфта: строки панели сверху вниз — порядок
    /// пиков, как клетки в клиенте. Союзник в строке ниже моей ещё не пикал и
    /// только наводит — в клиенте взять до своего хода нельзя; выше — уже
    /// взял. До 22.11 руками все союзники были взятыми, и тег «С показанным» в
    /// ручной сцене не появлялся никогда (владелец: «лес + мид, топ + лес, когда
    /// я беру первым в этих связках, тегов нет»; стрелок с саппортом у него шёл
    /// авто-драфтом). Парный ход клиента (лес с мидом, стрелок с саппортом)
    /// здесь не разыгрываем: «первый в паре» — тот, кто выше, — и саппорт
    /// последним по-прежнему видит стрелка взятым. Мой пик подтверждён — драфт
    /// застыл как есть, все взяты; после «Моментально» мой пик взят, так что и
    /// там все взяты.
    /// </summary>
    private bool ManualHover(int cell, int meCell) =>
        _simTurn < 0 && !_myLocked && cell > meCell && cell < 5;

    // Ставит заранее выбранный пик в слот клетки (с началом пиков — наведением).
    private void RevealPlanned(int cell, int champ)
    {
        if (!_nameById.TryGetValue(champ, out var name)) return;
        _autoSetting = true;
        (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedItem = name;
        _autoSetting = false;
    }

    // Чемпион клетки (0..4 свои, 5..9 враги); 0 — ещё не выбран.
    private int CellChamp(int cell) =>
        ChampOf(cell < 5 ? _ally[cell] : _enemy[cell - 5]);

    private void SimTick(object? sender, EventArgs e)
    {
        if (_simTurn < 0 || _simTurn >= _simGroups.Length) { StopSim(); return; }

        var group  = _simGroups[_simTurn];
        int meCell = MeCell();
        var now    = DateTime.UtcNow;

        // Каждый пикает в свой момент — даже в парном ходе по отдельности:
        // наводил — берёт наведённого (владелец, 22.11), пусто — берёт бот.
        bool took = false;
        foreach (var cell in group)
        {
            if (cell == meCell || !_pickAt.TryGetValue(cell, out var t) || now < t) continue;
            if (CellChamp(cell) == 0) AutoPick(cell);
            else took |= _hoverCells.Remove(cell);
        }
        if (took) Recompute();   // слот не менялся — SelectionChanged не придёт

        // Конец моего хода: наведённого клиент берёт сам. Это пик, а не
        // наведение — до 21.11 песочница оставляла его наведением, и руны (они
        // ждут взятого) не приходили, пока не нажмёшь «Выбрать». Пустой слот
        // ждёт ручного пика.
        if (group.Contains(meCell) && !_myLocked && _myTurnEnd is { } end && now >= end
            && CellChamp(meCell) is var mine and not 0)
            LockMy(mine);

        // Ход завершён, когда запикалась вся группа, а мой пик — взят: наведение
        // ход не заканчивает (до 21.11 заканчивало, и драфт шёл дальше с
        // наведённым, без рун). Боты отстрелялись, а я нет — драфт ждёт меня.
        if (group.Any(c => c != meCell && (CellChamp(c) == 0 || _hoverCells.Contains(c))))
            return;   // кто-то ещё «думает»
        if (group.Contains(meCell) && !(_myLocked && CellChamp(meCell) != 0))
        {
            // Наведённого возьмёт конец хода, пустой слот ждёт меня.
            if (CellChamp(meCell) == 0) _simBtn.Content = "⏸ Ваш пик…";
            return;
        }

        _simTurn++;
        _simBtn.Content = "■ Стоп";
        if (_simTurn >= _simGroups.Length) { StopSim(); return; }
        StartTurn();
        Recompute();
    }

    /// Заполняет обе команды разом. Что человек выбрал руками — не трогаем: чаще
    /// всего половина состава уже набрана под конкретную сцену, и терять её ради
    /// «полного» драфта незачем. Окно — на драфт: из лобби кнопка раньше
    /// заполняла слоты, а окно оставалось на экране ожидания.
    private void InstantDraft()
    {
        StopSearch();       // идущие поиск, баны и авто-драфт тут только мешают
        StopSim();
        StopBanSim();
        _lastRun = SandboxRun.Instant;
        _setup = ManualPicks();
        if (_stage != TestStage.Draft)
        {
            _stage = TestStage.Draft;
            _overlay.RestoreFromTray(force: true);
        }
        _ready = false;     // без перерисовки на каждый из десяти слотов
        try
        {
            for (int cell = 0; cell < 10; cell++) AutoPick(cell);
        }
        finally
        {
            _ready = true;
        }
        // Драфт закончен — мой чемпион взят, а не наведён: иначе руны и сборка
        // (они ждут взятого) не приходили до «Выбрать».
        _myLocked = CellChamp(MeCell()) != 0;
        Recompute();        // одна перерисовка на весь состав
    }

    // Случайный ещё не занятый чемпион в слот cell (0..4 свои, 5..9 враги),
    // ПОДХОДЯЩИЙ ПО РОЛИ слота: строки идут TOP/JGL/MID/BOT/SUP у обеих команд,
    // берём тех, кто реально играет эту роль (≥20% своих игр на ней по базе).
    private void AutoPick(int cell)
    {
        var cb = cell < 5 ? _ally[cell] : _enemy[cell - 5];
        if (ChampOf(cb) != 0) return;   // уже выбран (например, я успел сам)

        var taken  = _ally.Concat(_enemy).Select(ChampOf).Where(id => id != 0).ToHashSet();
        taken.UnionWith(_planned.Values);   // все заранее выбранные пики — ботам недоступны
        taken.UnionWith(_ourBans);          // и забаненные в фазе банов
        taken.UnionWith(_theirBans);
        var dbRole = RecommendationEngine.LcuToDbRole(
            cell < 5 ? _rowRoles[cell] : _enemyRoles[cell - 5]);
        // Друг пикает из своей половины пула, как в жизни. Вся половина на эту
        // роль занята или пуста — берёт кого-то с линии, как любой союзник.
        var pool = _mate is not null && cell == MateCell()
            ? MatePicks(_mate, dbRole).Where(id => !taken.Contains(id) && _nameById.ContainsKey(id)).ToList()
            : [];
        if (pool.Count == 0)
            pool = _idByName.Values
                .Where(id => !taken.Contains(id) && _engine.RoleShare(id, dbRole) >= 0.20)
                .ToList();
        if (pool.Count == 0)   // нет данных по ролям — фолбэк на любых свободных
            pool = _idByName.Values.Where(id => !taken.Contains(id)).ToList();
        if (pool.Count == 0) return;
        // SelectionChanged → Recompute. Помечаем клетку как авто-занятую, чтобы
        // следующий запуск авто-драфта её пересобрал (а ручные пики — сохранил).
        _autoSetting = true;
        cb.SelectedItem = _nameById[pool[_rng.Next(pool.Count)]];
        _autoSetting = false;
        _autoPicked.Add(cell);
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

    /// <summary>
    /// «Кто-то из врагов пикает после меня» — как ExposedToCounter у клиента:
    /// подбор считает пик вслепую только тогда (последнему пику оппонент к его
    /// ходу уже известен). С авто-драфтом — по его порядку (_simGroups): после
    /// моей группы есть враг без чемпиона. Без авто-драфта порядка нет —
    /// считаем как первый пик: пока у врагов пустой слот, ответить могут.
    /// </summary>
    private bool Exposed(int meIdx, IReadOnlyList<DraftPlayer> their)
    {
        if (_myLocked) return false;
        bool Empty(int cell) => cell >= 5 && cell - 5 < their.Count && their[cell - 5].ChampionId == 0;
        if (_simTurn < 0) return their.Any(p => p.ChampionId == 0);
        var mine = Array.FindIndex(_simGroups, g => g.Contains(meIdx));
        return _simGroups.Skip(mine + 1).Any(g => g.Any(Empty));
    }

    // ── Срок хода пика ───────────────────────────────────────────────────────

    // Ход авто-драфта: срок для отсчёта в окне (StartTurn).
    private DateTime? _turnEnd;
    private int _turnSeconds;

    // Ход пика в драфте руками — как в ранкеде (проверка укорачивает).
    internal static double ManualTurnSeconds = PickTurnSeconds;
    private DateTime? _manualTurnEnd;          // конец моего хода в драфте руками; null — ход не идёт
    private DispatcherTimer? _manualTurnTimer; // начинает ход заново, когда время вышло

    /// <summary>
    /// Срок нынешнего хода — окну, как его дал бы клиент: отсчёт в слотах
    /// всех, чей ход (<c>ActiveCells</c>), и рамка последних 5 секунд, если
    /// ход мой. Авто-драфт — ход по очереди (<c>_turnEnd</c>, 30 с у всех, как в
    /// ранкеде), у союзников и врагов тоже (владелец, 25.11: «таймер должен
    /// быть у всех»). Драфт руками — только мой ход, тоже 30 секунд; вышло —
    /// ход начинается заново: в драфте руками песочница сама
    /// ничего не берёт (сцену собирают дольше 30 секунд), а отсчёт виден, пока
    /// ход мой. null — драфта нет (лобби).
    /// </summary>
    private void PushPickTimer(DraftState? draft)
    {
        var mine = draft?.MyPickInProgress == true;
        if (!mine || _simTurn >= 0) StopManualTurn();
        if (draft is null || draft.ActiveCells.Count == 0) _overlay.SetPickTimer(null, 0, false);
        else if (_simTurn >= 0) _overlay.SetPickTimer(_turnEnd, _turnSeconds * 1000, mine);
        else if (mine)
        {
            if (_manualTurnEnd is null) StartManualTurn();
            _overlay.SetPickTimer(_manualTurnEnd, ManualTurnSeconds * 1000, true);
        }
        else _overlay.SetPickTimer(null, 0, false);
    }

    private void StartManualTurn()
    {
        _manualTurnEnd = DateTime.UtcNow.AddSeconds(ManualTurnSeconds);
        if (_manualTurnTimer is null)
        {
            _manualTurnTimer = new DispatcherTimer();
            _manualTurnTimer.Tick += (_, _) =>
            {
                // Пока ждали, драфт руками мог кончиться: лобби, баны,
                // авто-драфт, мой пик взят.
                if (_stage != TestStage.Draft || _simTurn >= 0 || _myLocked) { StopManualTurn(); return; }
                StartManualTurn();
                _overlay.SetPickTimer(_manualTurnEnd, ManualTurnSeconds * 1000, true);
            };
        }
        _manualTurnTimer.Stop();
        _manualTurnTimer.Interval = TimeSpan.FromSeconds(ManualTurnSeconds);
        _manualTurnTimer.Start();
    }

    private void StopManualTurn()
    {
        _manualTurnTimer?.Stop();
        _manualTurnEnd = null;
    }
}
