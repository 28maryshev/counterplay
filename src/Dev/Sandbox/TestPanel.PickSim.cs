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

        // Старт: чистим клетки прошлого авто-драфта. Мой заранее выбранный пик
        // ЗАПОМИНАЕМ и ПРЯЧЕМ — он появится сам на моём ходу (для видео «человек
        // взял этот пик»), а не светится с начала. Прочие ручные пики сохраняем.
        _ready = false;
        ClearBans();   // драфт с нуля — без банов прошлой фазы
        CapturePlanned();
        _ready = true;
        StartPickSim();
    }

    /// Захватываем РУЧНЫЕ пики (заполнены пользователем, не ботом) как «план»:
    /// прячем их и покажем каждый на ходу своей клетки. Бот-пики прошлого
    /// раунда — чистим (пересоберутся заново).
    private void CapturePlanned()
    {
        _planned.Clear();
        for (int c = 0; c < 10; c++)
            if (CellChamp(c) > 0 && !_autoPicked.Contains(c))
                _planned[c] = CellChamp(c);
        foreach (var cell in _autoPicked.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;
        _autoPicked.Clear();
        foreach (var cell in _planned.Keys.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;   // прячем до хода
    }

    /// Пики по очереди LoL — с нуля или сразу после фазы банов.
    private void StartPickSim()
    {
        _stage = TestStage.Draft; UpdateStageButtons();
        _overlay.RestoreFromTray(force: true);   // закрытый крестиком оверлей — вернуть

        // Монетка: кто пикает первым — мы или враги (50/50, синяя сторона).
        _simGroups = _rng.Next(2) == 0 ? GroupsMyFirst : GroupsEnemyFirst;

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

    private void StopSim()
    {
        _simTimer?.Stop();
        if (_simTurn < 0) return;
        _simTurn = -1;
        // Остановили до чьего-то хода — вернём непоказанные заранее выбранные пики
        // в их слоты, чтобы они не потерялись (на след. запуске снова спрячутся).
        foreach (var (cell, champ) in _planned)
            if (CellChamp(cell) == 0) RevealPlanned(cell, champ);
        _simBtn.Content = "▶ Авто-драфт";
        Recompute();
    }

    // Раздаёт ботам текущей группы персональные моменты пика (3..10 с). Мой слот
    // получает момент, только если есть заранее выбранный пик (тогда он появится
    // сам на моём ходу); иначе слот ждёт ручного пика.
    private void StartTurn()
    {
        _pickAt.Clear();
        int meCell = MeCell();
        foreach (var cell in _simGroups[_simTurn])
            // Момент пика даём боту ИЛИ клетке с заранее выбранным пиком. Мой
            // пустой слот без плана — ждёт ручного пика (момента не получает).
            if (cell != meCell || _planned.ContainsKey(cell))
                _pickAt[cell] = DateTime.UtcNow.AddSeconds(_rng.Next(3, MaxTurnSeconds + 1));
    }

    // Показывает заранее выбранный пик клетки в момент её хода в драфте.
    private void RevealPlanned(int cell, int champ)
    {
        var name = DataDragon.Name(champ);
        if (!_idByName.ContainsKey(name)) return;
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

        // Боты пикают каждый в свой момент — даже в парном ходе по отдельности.
        // Мой слот в свой момент показывает заранее выбранный пик (если он есть).
        foreach (var cell in group)
            if (CellChamp(cell) == 0 && _pickAt.TryGetValue(cell, out var t) && now >= t)
            {
                if (_planned.TryGetValue(cell, out var champ)) RevealPlanned(cell, champ);
                else if (cell != meCell) AutoPick(cell);
                // мой слот без плана — не трогаем, ждём ручного пика
            }

        // Ход завершён, когда запикалась вся группа. Мой слот БЕЗ плана — ждёт
        // ручного пика (боты уже отстрелялись, драфт ждёт меня).
        bool myManual = group.Contains(meCell) && CellChamp(meCell) == 0
                        && !_planned.ContainsKey(meCell);
        if (myManual && group.All(c => c == meCell || CellChamp(c) != 0))
        {
            _simBtn.Content = "⏸ Ваш пик…";
            return;
        }
        if (group.Any(c => CellChamp(c) == 0)) return;   // кто-то ещё «думает»

        _simTurn++;
        _simBtn.Content = "■ Стоп";
        if (_simTurn >= _simGroups.Length) { StopSim(); return; }
        StartTurn();
        Recompute();
    }

    /// Заполняет обе команды разом. Что человек выбрал руками — не трогаем: чаще
    /// всего половина состава уже набрана под конкретную сцену, и терять её ради
    /// «полного» драфта незачем.
    private void InstantDraft()
    {
        StopSim();          // тикающий авто-драфт тут только мешает
        _ready = false;     // без перерисовки на каждый из десяти слотов
        try
        {
            for (int cell = 0; cell < 10; cell++) AutoPick(cell);
        }
        finally
        {
            _ready = true;
        }
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
            ? MatePicks(_mate, dbRole).Where(id => !taken.Contains(id)).ToList()
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
        cb.SelectedItem = DataDragon.Name(pool[_rng.Next(pool.Count)]);
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
}
