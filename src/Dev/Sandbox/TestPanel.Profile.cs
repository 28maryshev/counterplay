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

// Панель песочницы — профиль и этапы: свои данные или подставные, сценарий профиля, кнопки этапов, «Поиск игры».
sealed partial class TestPanel
{
    // Владение чемпионами в песочнице: всё куплено, либо половина «отсутствует»
    // (детерминированно по id) — чтобы проверить плашку «нет чемпиона».
    private void ApplyOwnership()
    {
        if (_missingChamps.IsChecked == true)
            _overlay.SetOwnedChampions(_allChampIds.Where(id => id % 2 == 0).ToList());
        else
            _overlay.SetOwnedChampions(_allChampIds);
    }

    // ── Профиль «мой аккаунт»: песочница на настоящих данных ────────────────
    //
    // Журнал игр и личный винрейт песочница и так читает с последнего аккаунта.
    // Не хватало пулов (у песочницы свой аккаунт «test-account») и связок с
    // напарником (подставные). Пулы приезжают КОПИЕЙ: правки в песочнице в
    // настоящие не попадают, а уход с профиля возвращает тестовые.
    private const int MyAccountProfile = 4;
    private const int UnrankedProfile = 5;
    private const int ThreeGamesProfile = 6;
    private const int AramProfile = 7;
    private const int DiamondProfile = 8;
    /// Пулов нет — зелёная кнопка «Пул» (13.11): тег «+ Пул» на тестовой истории
    /// и тег «+N% WR» на истории, где на своих выигрываешь чаще.
    private const int NoPoolsProfile = 9;
    private const int NoPoolsGainProfile = 10;
    /// Подпись экрана Ready: в песочнице с пометкой «TEST», кроме профиля для
    /// снимков на сайт — там экран как боевой.
    private string ReadyText => Loc.T("status.readyIdle")
        + (_profile.SelectedIndex == DiamondProfile ? "" : " · TEST");
    /// С этого места в списке — аккаунты из журнала (<see cref="_storedAccounts"/>).
    private const int FirstStoredProfile = 11;
    private IReadOnlyList<(string Key, string Nick)> _storedAccounts = [];

    // Выбор помнится отметкой в папке песочницы: settings.json — файл игрока,
    // песочница в него не пишет (см. Sandbox).
    private static string MyAccountMark => Path.Combine(Sandbox.Dir, "my-account");

    /// Чьи данные сейчас в песочнице: puuid из журнала; null — тестовые.
    private string? _dataAccount;

    /// <summary>
    /// Песочница на данных аккаунта из журнала (null — на тестовых). «Мой
    /// аккаунт» — последний аккаунт, и только его выбор помнится до следующего
    /// запуска; остальные аккаунты журнала — посмотреть, как выглядит чужой
    /// экран целиком: без пулов, без ранга, с одними нормалами.
    /// </summary>
    private void UseAccountData(string? account, bool remember)
    {
        if (account == _dataAccount) return;
        _dataAccount = account;
        try
        {
            Directory.CreateDirectory(Sandbox.Dir);
            if (remember) File.WriteAllText(MyAccountMark, "");
            else File.Delete(MyAccountMark);
        }
        catch { /* не запомнилось — выберут ещё раз */ }
        TestMode.ApplyAccountData(_engine, account);
        FillMates();   // пулы другие — и друзья другие
        _overlay.RefreshPoolMode();
        Recompute();
    }

    // Пустые пулы песочницы включены (профили «без пулов»).
    private bool _noPools;

    // История «на своих чаще» подменена (профиль «без пулов, на своих чаще»).
    private bool _mainsWin;

    // Показ выбранного сценария профиля: пустой / первая игра / полная история.
    private void ApplyProfileScenario()
    {
        var idx = _profile.SelectedIndex;
        var stored = idx - FirstStoredProfile;
        // Пустые пулы — у отдельного аккаунта песочницы. Снять их — ДО смены
        // данных (иначе «мой аккаунт» лёг бы копией в пустой аккаунт), включить —
        // ПОСЛЕ (иначе возврат к тестовым данным засеял бы его тестовыми пулами).
        var noPools = idx is NoPoolsProfile or NoPoolsGainProfile;
        if (_noPools && !noPools) { TestMode.UseNoPools(false); _noPools = false; }
        // «Алмаз II» — тоже на моих данных: пул, ник и винрейты чемпионов
        // настоящие, подставные только ранг и график (TestMode.DiamondSession).
        UseAccountData(
            idx is MyAccountProfile or DiamondProfile ? SessionTracker.LastAccountKey
            : stored >= 0 && stored < _storedAccounts.Count ? _storedAccounts[stored].Key
            : null,
            remember: idx == MyAccountProfile);
        if (noPools && !_noPools)
        {
            TestMode.UseNoPools(true);
            _noPools = true;
            // Предупреждение «как собрать пул» — снова как в первый раз: зайди
            // в настройки пулов и увидишь. Песочница настройки на диск не
            // пишет — у игрока отметка не меняется.
            AppSettings.Current.PoolFirstTipShown = false;
        }
        // История: «на своих чаще» — только у своего профиля, уход возвращает тестовую.
        if (idx == NoPoolsGainProfile && !_mainsWin)
        {
            SessionTracker.PreviewGames = TestMode.MainsWinHistory();
            _mainsWin = true;
        }
        else if (idx != NoPoolsGainProfile && _mainsWin)
        {
            if (_dataAccount is null) TestMode.UseTestHistory(_engine, true);
            _mainsWin = false;
        }
        // Другой профиль — другие пулы со своим включённым: на голой базе его
        // снова в обычный режим.
        TestMode.PausePools();
        _overlay.RefreshPoolMode();
        // Снимок для сайта — без тестовых кнопок («Сохранить для драфта»,
        // «Логи»); подпись «TEST» снимает ReadyText. Пересобираем экран сами:
        // с «моего аккаунта» сюда аккаунт тот же, и UseAccountData выходит сразу.
        _overlay.SetSandboxTools(idx != DiamondProfile);
        Recompute();
        if (_emptyProfile.IsChecked == true)
        {
            _overlay.SetEmptyProfilePreview(true);
            return;
        }
        _overlay.SetEmptyProfilePreview(false);
        switch (_profile.SelectedIndex)
        {
            case 1:
                _overlay.ShowSession(TestMode.FirstGameSession);
                _overlay.SetChampsPreview(TestMode.FirstGameChamps);
                break;
            case 2:
                _overlay.ShowSession(TestMode.FiveGamesSession);
                _overlay.SetChampsPreview(TestMode.FiveGamesChamps);
                break;
            case 3:
                _overlay.ShowSession(TestMode.ClimbSession);
                _overlay.SetChampsPreview(null);
                break;
            case UnrankedProfile:
                _overlay.ShowSession(TestMode.UnrankedSession);
                _overlay.SetChampsPreview(null);
                break;
            case ThreeGamesProfile:
                _overlay.ShowSession(TestMode.ThreeGamesSession);
                _overlay.SetChampsPreview(TestMode.ThreeGamesChamps);
                break;
            case AramProfile:
                _overlay.ShowSession(TestMode.AramSession);
                _overlay.SetChampsPreview(TestMode.AramChamps);
                break;
            case DiamondProfile:
                _overlay.ShowSession(TestMode.DiamondSession(SessionTracker.StoredView()));
                _overlay.SetChampsPreview(null);
                break;
            case >= FirstStoredProfile when _profile.SelectedIndex - FirstStoredProfile < _storedAccounts.Count:
                _overlay.ShowSession(SessionTracker.StoredView(
                    _storedAccounts[_profile.SelectedIndex - FirstStoredProfile].Key));
                _overlay.SetChampsPreview(null);
                break;
            case MyAccountProfile:
                _overlay.ShowSession(SessionTracker.StoredView() ?? TestMode.FullSession);
                _overlay.SetChampsPreview(null);
                break;
            default:
                _overlay.ShowSession(TestMode.FullSession);
                _overlay.SetChampsPreview(null);   // своя настоящая статистика
                break;
        }
    }

    /// <summary>
    /// «Мастерство и пулы» (владелец 22.11). Выключено — голая база: движок не
    /// видит истории и очков мастерства, пул в обычном режиме. Включено — как в
    /// бою, на истории и пулах выбранного профиля; выбранный друг включает свой
    /// дуо-пул.
    /// </summary>
    private void ApplyPersonal()
    {
        var on = _personal.IsChecked == true;
        TestMode.UsePersonal(on);
        UseMatePool();
        _overlay.RefreshPoolMode();
        Log.Write($"песочница: {(on ? "мастерство и пулы включены" : "голая база")}");
        Recompute();
    }

    /// «Показать окно»: окно драфта спрятали крестиком или «—» (в песочнице
    /// крестик программу не закрывает — она живёт, пока открыта панель) или
    /// Esc, который прячет мимо трея. Драфт и слоты при этом не трогаются.
    private void ShowOverlay()
    {
        // Окно и так поверх остальных (Topmost) — фокус у человека не отбираем.
        _overlay.RestoreFromTray(force: true);
        _overlay.Dispatcher.InvokeAsync(() => { if (!_overlay.IsVisible) _overlay.Show(); });
    }

    // ── Тестовые этапы: Драфт · Баны · Ready/пул ─────────────────────────────
    private void SetStage(TestStage s)
    {
        StopSearch();
        // «Баны» — это живая фаза банов, а не статичный кадр.
        if (s == TestStage.Bans) { _lastRun = SandboxRun.Bans; StartBanSim(); return; }
        if (_banSim) StopBanSim();
        _stage = s;
        UpdateStageButtons();
        // Оверлей могли закрыть крестиком — это помечает его «свёрнут вручную»,
        // и сам он больше не всплывёт. Переключение этапа — явное действие,
        // поэтому возвращаем принудительно, иначе окно драфта не появится.
        _overlay.RestoreFromTray(force: true);
        Recompute();
    }

    /// <summary>
    /// «Поиск игры»: экран ожидания в фазе «идёт поиск» на
    /// <see cref="SearchSeconds"/> секунд, затем объявление, баны и пики — как
    /// «Баны». Ещё одно нажатие — отсчёт заново; остановить — «Сброс».
    ///
    /// Владелец 15 октября: подсказку режима пула не успеть рассмотреть —
    /// «Баны» уводят окно в драфт сразу, а в бою она стоит всё время лобби и
    /// поиска. Слоты не чистятся: заранее выбранные пики уйдут в план, как при
    /// «Банах».
    ///
    /// Поиск — новое лобби: отложенная «Не сейчас» подсказка снова в силе. И
    /// повторное нажатие поиск не отменяет: первая версия отменяла, и владелец
    /// пять раз подряд гасил поиск через секунду после запуска, не заметив его.
    /// </summary>
    private void StartSearch()
    {
        var again = _searchEndAt is not null;
        StopSim();
        StopBanSim();
        _lastRun = SandboxRun.Search;
        _setup = ManualPicks();
        // Окно на драфте — уводим его на экран ожидания тем же путём, что
        // «Сброс», только слоты не трогаем.
        if (_stage != TestStage.Ready)
        {
            _overlay.UpdateRecommendations(null, null);
            _overlay.DraftEnded();
            _overlay.HideRunes();
            _stage = TestStage.Ready;
            _myLocked = false;
            UpdateStageButtons();
        }
        _overlay.RestoreFromTray(force: true);

        _searchEndAt = DateTime.UtcNow.AddSeconds(SearchSeconds);
        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.25) };
            _searchTimer.Tick += SearchTick;
        }
        _searchTimer.Start();
        SearchTick(null, EventArgs.Empty);
        _overlay.ForgetPoolHintLater();
        Log.Write($"песочница: поиск игры {SearchSeconds:0} с{(again ? " заново" : "")}, затем баны");
        Recompute();
    }

    private void SearchTick(object? sender, EventArgs e)
    {
        if (_searchEndAt is not { } end) { _searchTimer?.Stop(); return; }
        var left = end - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            StopSearch();
            StartBanSim();   // игра найдена — дальше как «Баны»
            return;
        }
        _searchBtn.Content = $"■ Поиск · {Math.Ceiling(left.TotalSeconds):0} с";
        UpdateStatus();
    }

    private void StopSearch()
    {
        _searchTimer?.Stop();
        _searchEndAt = null;
        _searchBtn.Content = SearchIdle;
    }

    /// Подсвечено то, что идёт сейчас: поиск, баны, авто-драфт, драфт
    /// вручную или лобби. Цвета — TestPanel.Layout.cs.
    private void UpdateStageButtons()
    {
        bool sim = _simTurn >= 0;
        Mark(_searchBtn,  _searchEndAt is not null, BtnKind.Launch);
        Mark(_stageBans,  _banSim, BtnKind.Launch);
        Mark(_simBtn,     sim, BtnKind.Launch);
        Mark(_stageDraft, _stage == TestStage.Draft && !sim, BtnKind.Plain);
        Mark(_stageReady, _stage == TestStage.Ready && _searchEndAt is null, BtnKind.Plain);
    }

    /// Сценарий профиля: список профилей и две галочки. Места им даёт
    /// карточка «Окно программы» (TestPanel.Layout.cs).
    private void InitScenario()
    {
        // Проверка плашки «нет чемпиона»: половина ростера считается некупленной.
        _missingChamps.Content = "Нет части чемпионов";
        _missingChamps.ToolTip = "Отметить часть чемпионов как отсутствующих на аккаунте — видно предупреждение в подборе и в пуле";
        _missingChamps.Checked   += (_, _) => ApplyOwnership();
        _missingChamps.Unchecked += (_, _) => ApplyOwnership();

        // Проверка первого запуска: журнал пуст, но все элементы ready-экрана
        // на месте (пустые последние игры, пустой график, пустая полоса чемпионов).
        _emptyProfile.Content = "Профиль без игр";
        _emptyProfile.ToolTip = "Показать ready-экран так, как его видит человек сразу после установки — до первой сыгранной игры";
        _emptyProfile.Checked   += (_, _) => ApplyProfileScenario();
        _emptyProfile.Unchecked += (_, _) => ApplyProfileScenario();

        // Подбор на голой базе (по умолчанию) или с личным — TestMode.UsePersonal.
        _personal.Content = "Мастерство и пулы";
        _personal.ToolTip = "Выключено (по умолчанию) — подбор на голой базе, как у человека без истории: "
                          + "ни наигранности и очков мастерства, ни своего винрейта и мейнов в банах, ни пулов.\n"
                          + "Включено — как в бою: история и пулы выбранного профиля. "
                          + "Экраны профиля, окно пулов и связки видны в обоих случаях";
        _personal.Checked   += (_, _) => ApplyPersonal();
        _personal.Unchecked += (_, _) => ApplyPersonal();

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
        _profile.Items.Add("Профиль: без пулов");       // NoPoolsProfile — зелёная кнопка «Пул», тег «+ Пул»
        _profile.Items.Add("Профиль: без пулов, на своих чаще"); // NoPoolsGainProfile — тег «+N% WR»
        // Аккаунты из журнала на этом компьютере — их экраны как есть (только
        // чтение): так видно настоящий аккаунт без ранговых, с нормалами и ARAM.
        _storedAccounts = SessionTracker.StoredAccounts();
        foreach (var (_, nick) in _storedAccounts)
            _profile.Items.Add($"Профиль: из журнала — {nick}");
        _profile.SelectedIndex = 0;
        _profile.ToolTip = "Что видно на ready-экране при разном количестве сыгранных игр: график появляется с пятой.\n"
                         + "«Мой аккаунт» — песочница на твоих данных: копия твоих пулов, твои игры, винрейты и связки. "
                         + "Твои настоящие пулы при этом не меняются. В подбор история и пулы идут "
                         + "только с галочкой «Мастерство и пулы».";
        _profile.SelectionChanged += (_, _) => ApplyProfileScenario();
    }

    private ComboBox MakeCombo(int cell)
    {
        var cb = new ComboBox
        {
            IsEditable = true, ItemsSource = _names, SelectedIndex = 0,
            IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 0, 0), Tag = cell
        };
        cb.SelectionChanged += (_, _) => OnChampChanged(cell);
        cb.LostKeyboardFocus += (_, _) => Recompute(); // подтверждение набранного текста
        return cb;
    }
}
