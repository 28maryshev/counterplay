using System.Windows.Controls;

namespace Counterplay;

// Панель песочницы — запуск драфта: что запускали последним, состав до
// драфта, «Заново», «Сброс» и строка состояния в карточке «Драфт».
sealed partial class TestPanel
{
    // Чем запускали драфт: «Заново» повторяет именно это.
    private enum SandboxRun { Auto, Bans, Search, Instant }

    private SandboxRun _lastRun = SandboxRun.Auto;

    // Состав до драфта (клетка → чемпион): что выбрали руками к запуску. Его
    // возвращает «Заново». null — после «Сброса» драфт не запускали, и всё в
    // слотах выбрано руками.
    private Dictionary<int, int>? _setup;

    /// Чемпионы, выбранные руками: всё в слотах, кроме поставленного ботами.
    private Dictionary<int, int> ManualPicks() =>
        Enumerable.Range(0, 10)
            .Where(c => CellChamp(c) > 0 && !_autoPicked.Contains(c))
            .ToDictionary(c => c, CellChamp);

    private static string RunName(SandboxRun run) => run switch
    {
        SandboxRun.Bans    => "баны и пики",
        SandboxRun.Search  => "поиск игры, баны и пики",
        SandboxRun.Instant => "моментальный драфт",
        _                  => "авто-драфт",
    };

    /// <summary>
    /// «Заново» (владелец, 22.11): как «Сброс», только чемпионы, выбранные до
    /// драфта, возвращаются в слоты, и тот же запуск идёт снова — авто-драфт,
    /// баны, поиск или моментальный драфт. Боты пикают заново, монетка «кто
    /// пикает первым» бросается заново (StartPickSim). Выбранное по ходу
    /// драфта — и ботами, и руками — не сохраняется: это уже сам драфт. Роли,
    /// «я», друг и профиль остаются, как и при «Сбросе».
    /// </summary>
    private void RestartDraft()
    {
        var setup = _setup ?? ManualPicks();
        var run = _lastRun;
        ResetDraft();
        _ready = false;   // одна перерисовка — уже запуском
        try
        {
            foreach (var (cell, champ) in setup) RevealPlanned(cell, champ);
        }
        finally { _ready = true; }
        Log.Write($"песочница: заново — {RunName(run)}; до драфта: " +
                  (setup.Count == 0 ? "пусто"
                      : string.Join(", ", setup.Select(p => $"{p.Key}:{DataDragon.Name(p.Value)}"))));
        switch (run)
        {
            case SandboxRun.Bans:    SetStage(TestStage.Bans); break;
            case SandboxRun.Search:  StartSearch(); break;
            case SandboxRun.Instant: InstantDraft(); break;
            default:                 ToggleSim(); break;
        }
    }

    /// <summary>
    /// «Сброс»: драфт кончился, как додж в клиенте. Симуляции стоп, слоты
    /// пусты, окно уходит с драфта на экран ожидания тем же путём, что в бою
    /// (конец драфта: запомнить положение, сбросить метки ролей и выбранного).
    /// Этап — «Ready»: пока настраиваешь слоты, роль и друга, драфт в окно не
    /// идёт (раньше каждая правка тут же открывала его снова). Запуск — любой
    /// кнопкой карточки «Драфт»; роль, друг и профиль остаются.
    /// </summary>
    private void ResetDraft()
    {
        StopSearch();
        StopSim();
        StopBanSim();
        ClearBans();
        _ready = false;
        foreach (var cb in _ally.Concat(_enemy)) cb.SelectedIndex = 0;
        _autoPicked.Clear();
        _planned.Clear();
        _setup = null;
        _myLocked = false;
        _ready = true;
        _overlay.UpdateRecommendations(null, null);
        _overlay.DraftEnded();
        _overlay.HideRunes();
        _stage = TestStage.Ready;
        UpdateStageButtons();
        Recompute();
        Log.Write("песочница: сброс — драфт закрыт, этап Ready");
    }

    /// Строка состояния под кнопками запуска: что идёт, чей ход, кто пикает
    /// первым. И подпись «Заново» — что именно оно повторит.
    private void UpdateStatus()
    {
        string text;
        if (_searchEndAt is { } end)
            text = $"Поиск игры · {Math.Max(0, Math.Ceiling((end - DateTime.UtcNow).TotalSeconds)):0} с, затем баны и пики";
        else if (_banSim)
            text = Planning ? "Объявление пиков: союзники наводят, банить ещё нельзя"
                 : _myBanDone ? "Баны: твой сделан, ждём остальных"
                 : "Баны: твой бан";
        else if (_simTurn >= 0 && _simTurn < _simGroups.Length)
        {
            var first = _simGroups[0][0] < 5 ? "мы" : "враги";
            text = $"Пики · ход {_simTurn + 1} из {_simGroups.Length} · первыми пикают {first}";
            if (_simGroups[_simTurn].Contains(MeCell()) && !_myLocked) text += " · твой ход";
        }
        else
            text = _stage switch
            {
                TestStage.Draft => "Драфт стоит: окно считает по слотам панели",
                _ => "Лобби: собери составы и запусти драфт",
            };
        _status.Text = text;
        _restartBtn.ToolTip =
            $"Начать заново: {RunName(_lastRun)} — с чемпионами, выбранными до драфта. Что выбрали по ходу " +
            "драфта боты и ты, уходит; кто пикает первым — снова монетка. Роли, друг и профиль остаются";
        UpdateStageButtons();
    }
}
