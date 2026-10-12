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

// Панель песочницы — фаза банов: объявление, наведения союзников, баны ботов, раскрытие.
sealed partial class TestPanel
{
    // ── Фаза банов ───────────────────────────────────────────────────────────

    private void StartBanSim()
    {
        StopSim();
        StopBanSim();
        _ready = false;
        try
        {
            // Мои данные: подбор банов смотрит на то, чем я играю на самом деле.
            if (_profile.SelectedIndex != MyAccountProfile) _profile.SelectedIndex = MyAccountProfile;
            if (!_meChosen) SetMyRole(MyMainRole());

            // Драфт только начинается: слоты пусты, ручные пики — в план на пики.
            ClearBans();
            CapturePlanned();

            // Объявление: союзники наводят уже в нём, баны — после.
            var now = DateTime.UtcNow;
            _planEndAt = now.AddSeconds(PlanSeconds);
            PlanHovers(now);
            int me = MeCell();
            for (int cell = 0; cell < 10; cell++)
                if (cell != me) _banAt[cell] = _planEndAt.Value.AddSeconds(4 + _rng.NextDouble() * 10);
            // Один враг банит моего мейна — чтобы его отметку на панели банов было
            // видно в первом же прогоне: сами по себе мейны банятся редко (у Соны
            // бан-рейт 0,1%).
            _mainBanCell = RecommendationEngine.MyMains().Count > 0 ? 5 + _rng.Next(5) : -1;

            _stage = TestStage.Bans; UpdateStageButtons();
            _overlay.RestoreFromTray(force: true);
            _banSim = true;
        }
        finally { _ready = true; }

        _simBtn.Content = "■ Стоп";
        if (_banTimer is null)
        {
            _banTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
            _banTimer.Tick += BanTick;
        }
        _banTimer.Start();
        Log.Write($"песочница: объявление пиков {PlanSeconds:0} с, затем баны; роль {_rowRoles[MeCell()]}" +
                  $"{(_meChosen ? " (выбрана руками)" : " (основная по журналу)")}, " +
                  $"наводят: {string.Join(", ", _hoverPlan.Select(h => $"{h.Cell}:{DataDragon.Name(h.Champ)}"))}");
        Recompute();
    }

    /// <summary>
    /// «Я» пересел на другую строку посреди фазы — так в песочнице видно, что
    /// окно объявления следует ВЫДАННОЙ роли (выкинуло в лес — саппортов нет).
    /// Боты банят по расписанию на свои клетки: новое моё место из него
    /// убираем, прежнее отдаём боту, иначе фаза ждала бы бана от пустого места.
    /// </summary>
    private void ReseatBans()
    {
        int me = MeCell();
        _banAt.Remove(me);
        var from = _planEndAt is { } end && end > DateTime.UtcNow ? end : DateTime.UtcNow;
        for (int cell = 0; cell < 10; cell++)
            if (cell != me && !_banAt.ContainsKey(cell) && _bans.All(b => b.Cell != cell))
                _banAt[cell] = from.AddSeconds(2 + _rng.NextDouble() * 6);
    }

    /// Остановить фазу банов. Сделанные баны не нужны: драфт не дошёл до пиков.
    private void StopBanSim()
    {
        _banTimer?.Stop();
        if (!_banSim) return;
        _banSim = false;
        ClearBans();
        _simBtn.Content = "▶ Авто-драфт";
    }

    private void ClearBans()
    {
        _bans.Clear(); _banAt.Clear(); _hoverPlan.Clear(); _hoversShown.Clear();
        _intent.Clear(); _hoverHistory.Clear();
        _myBanDone = false; _banEndAt = null; _planEndAt = null;
        _ourBans = []; _theirBans = [];
    }

    /// Кнопка «Забанить» в окне: мой бан сделан.
    public void LockMyBan(int champId)
    {
        if (!_banSim || _myBanDone || champId <= 0) return;
        if (Planning) return;                              // банить ещё рано — клиент не дал бы
        if (_bans.Any(b => b.Champ == champId)) return;   // уже забанен — клиент не дал бы
        _bans.Add((MeCell(), champId));
        _myBanDone = true;
        Recompute();
    }

    private bool IsBanned(int champ) => _bans.Any(b => b.Champ == champ);

    private void BanTick(object? sender, EventArgs e)
    {
        if (!_banSim) { _banTimer?.Stop(); return; }
        var now = DateTime.UtcNow;
        bool changed = false;

        // Объявление кончилось — начинаются баны: окно меняет моих чемпионов
        // на советы банов.
        if (_planEndAt is { } planEnd && now >= planEnd)
        {
            _planEndAt = null;
            changed = true;
            Log.Write("песочница: объявление кончилось — баны");
        }

        // Наведения союзников — по порядку времени: позднее перебивает раннее.
        for (int i = 0; i < _hoverPlan.Count; i++)
            if (now >= _hoverPlan[i].At && _hoversShown.Add(i))
            {
                _intent[_hoverPlan[i].Cell] = _hoverPlan[i].Champ;
                changed = true;
            }

        foreach (var (cell, at) in _banAt)
            if (now >= at && _bans.All(b => b.Cell != cell))
            {
                var champ = BotBan(cell);
                if (champ > 0) { _bans.Add((cell, champ)); changed = true; }
                else _bans.Add((cell, 0));   // некого — бан пропущен, как по таймеру
            }

        bool botsDone = _banAt.Keys.All(c => _bans.Any(b => b.Cell == c));
        if (botsDone && _myBanDone && _banEndAt is null) _banEndAt = now.AddSeconds(BanEndSeconds);
        if (_banEndAt is { } end && now >= end)
        {
            EndBanPhase();
            return;
        }

        _simBtn.Content = Planning ? $"■ Стоп · {(_planEndAt!.Value - now).TotalSeconds:0} с"
                        : !_myBanDone ? (botsDone ? "⏸ Ваш бан…" : "■ Стоп") : "■ Стоп";
        if (changed) Recompute();
    }

    private readonly HashSet<int> _hoversShown = new();   // показанные пункты _hoverPlan

    /// Фаза банов кончилась: баны уходят в драфт, союзники берут то, что
    /// наводили (если это не забанили), и начинаются пики.
    private void EndBanPhase()
    {
        _banTimer?.Stop();
        _banSim = false;
        _ourBans   = [.. _bans.Where(b => b.Cell < 5  && b.Champ > 0).Select(b => b.Champ)];
        _theirBans = [.. _bans.Where(b => b.Cell >= 5 && b.Champ > 0).Select(b => b.Champ)];
        foreach (var (cell, champ) in _intent)
            if (!_planned.ContainsKey(cell)) _planned[cell] = champ;
        foreach (var cell in _planned.Where(p => IsBanned(p.Value)).Select(p => p.Key).ToList())
            _planned.Remove(cell);   // его пик забанили — возьмёт кого-то другого
        // Мой показанный пик забанили — клиент снимает показ, выбирать заново.
        if (IsBanned(CellChamp(MeCell())))
        {
            _autoSetting = true;
            _ally[MeCell()].SelectedIndex = 0;
            _autoSetting = false;
        }
        Log.Write($"песочница: баны {string.Join(", ", _ourBans.Select(DataDragon.Name))} | " +
                  $"{string.Join(", ", _theirBans.Select(DataDragon.Name))} — начинаются пики");
        _intent.Clear();
        StartPickSim();
    }

    /// От двух до четырёх союзников (не я, сколько — случайно) показывают, кого
    /// возьмут: популярных на своей роли чаще, как в жизни. Первый через
    /// несколько секунд передумывает — история наведений тоже участвует в
    /// подборе банов. Заранее выбранный руками пик союзника и есть его наведение.
    /// Все первые наведения — в первые пять секунд объявления.
    private void PlanHovers(DateTime now)
    {
        int me = MeCell();
        var allies = Enumerable.Range(0, 5).Where(c => c != me).OrderBy(_ => _rng.Next())
                               .Take(_rng.Next(2, 5)).ToList();
        var used = new HashSet<int>(_planned.Values);
        for (int k = 0; k < allies.Count; k++)
        {
            int cell = allies[k];
            var first = _planned.TryGetValue(cell, out var plan) ? plan : PopularOn(cell, used);
            if (first <= 0) continue;
            used.Add(first);
            _hoverPlan.Add((cell, first, now.AddSeconds(1 + 0.8 * k + _rng.NextDouble() * 1.5)));
            if (k == 0 && !_planned.ContainsKey(cell))
            {
                var second = PopularOn(cell, used);
                if (second > 0)
                {
                    used.Add(second);
                    _hoverPlan.Add((cell, second, now.AddSeconds(6 + _rng.NextDouble() * 2)));
                }
            }
        }
    }

    /// Случайный чемпион роли клетки, с весом по пикрейту за последние патчи.
    private int PopularOn(int cell, HashSet<int> exclude)
    {
        var dbRole = RecommendationEngine.LcuToDbRole(_rowRoles[cell]);
        var pool = _engine.PickRates(dbRole)
            .Where(kv => kv.Value >= 1.5 && !exclude.Contains(kv.Key) && _idByName.ContainsKey(DataDragon.Name(kv.Key)))
            .ToList();
        return Weighted(pool);
    }

    /// Бан бота: из самых банимых в патче, с весом по бан-рейту. Союзник не
    /// банит то, что навели или запланировали свои, враг — свои планы.
    private int _mainBanCell = -1;   // враг, который банит моего мейна (-1 — никто)

    private int BotBan(int cell)
    {
        bool ally = cell < 5;
        var keep = new HashSet<int>(_bans.Select(b => b.Champ));
        foreach (var (c, champ) in _planned)
            if ((c < 5) == ally) keep.Add(champ);
        if (cell == _mainBanCell)
        {
            var mains = RecommendationEngine.MyMains().Keys
                .Where(id => !keep.Contains(id) && _idByName.ContainsKey(DataDragon.Name(id))).ToList();
            if (mains.Count > 0) return mains[_rng.Next(mains.Count)];
        }
        if (ally) keep.UnionWith(_intent.Values);
        if (ally && CellChamp(MeCell()) is var mine and > 0) keep.Add(mine);
        var pool = _engine.BanRates()
            .Where(kv => !keep.Contains(kv.Key) && _idByName.ContainsKey(DataDragon.Name(kv.Key)))
            .OrderByDescending(kv => kv.Value)
            .Take(20)
            .ToList();
        return Weighted(pool);
    }

    private int Weighted(List<KeyValuePair<int, double>> pool)
    {
        var total = pool.Sum(kv => kv.Value);
        if (total <= 0) return 0;
        var x = _rng.NextDouble() * total;
        foreach (var kv in pool)
            if ((x -= kv.Value) <= 0) return kv.Key;
        return pool[^1].Key;
    }

    /// Моя основная роль по журналу за месяц: где больше всего игр — по ролям
    /// самих игр, как их видит окно объявления (у владельца Эш — саппорт, хотя
    /// база числит её стрелком). Журнала нет — роль строки «я».
    private string MyMainRole()
    {
        var best = new[] { "top", "jungle", "mid", "adc", "support" }
            .Select(r => (Role: r, Games: SessionTracker.LaneChamps(r, _engine.RoleShare).Sum(c => c.Games)))
            .Where(x => x.Games > 0)
            .OrderByDescending(x => x.Games)
            .Select(x => x.Role)
            .FirstOrDefault();
        // Ролей у игр нет вовсе (проверки подставляют историю без них) — по
        // главной роли каждого сыгранного чемпиона, с весом по играм.
        best ??= SessionTracker.History(RecommendationEngine.FreshDays,
                [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal]).Played
            .Where(p => p.Games > 0)
            .GroupBy(p => _engine.PrimaryRole(p.Id))
            .Where(g => g.Key.Length > 0)
            .OrderByDescending(g => g.Sum(p => p.Games))
            .Select(g => g.Key)
            .FirstOrDefault();
        return best is null ? _rowRoles[MeCell()] : RecommendationEngine.DbToLcuRole(best);
    }
}
