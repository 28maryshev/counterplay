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

// Панель песочницы — профиль и этапы: свои данные или подставные, «Сброс», кнопки этапов.
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
    /// С этого места в списке — аккаунты из журнала (<see cref="_storedAccounts"/>).
    private const int FirstStoredProfile = 6;
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

    // Показ выбранного сценария профиля: пустой / первая игра / полная история.
    private void ApplyProfileScenario()
    {
        var idx = _profile.SelectedIndex;
        var stored = idx - FirstStoredProfile;
        UseAccountData(
            idx == MyAccountProfile ? SessionTracker.LastAccountKey
            : stored >= 0 && stored < _storedAccounts.Count ? _storedAccounts[stored].Key
            : null,
            remember: idx == MyAccountProfile);
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

    // ── Тестовые этапы: Драфт · Баны · Ready/пул ─────────────────────────────
    private void SetStage(TestStage s)
    {
        StopSearch();
        // «Баны» — это живая фаза банов, а не статичный кадр.
        if (s == TestStage.Bans) { StartBanSim(); return; }
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
    /// «Сброс»: драфт кончился, как дродж в клиенте. Симуляции стоп, слоты
    /// пусты, окно уходит с драфта на экран ожидания тем же путём, что в бою
    /// (конец драфта: запомнить положение, сбросить метки ролей и выбранного).
    /// Этап — «Ready»: пока настраиваешь слоты, роль и друга, драфт в окно не
    /// идёт (раньше каждая правка тут же открывала его снова). Запуск —
    /// «Драфт», «Баны» или «Авто-драфт»; роль, друг и профиль остаются.
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
        _ready = true;
        _overlay.UpdateRecommendations(null, null);
        _overlay.DraftEnded();
        _overlay.HideRunes();
        _stage = TestStage.Ready;
        UpdateStageButtons();
        Recompute();
        Log.Write("песочница: сброс — драфт закрыт, этап Ready");
    }

    /// <summary>
    /// «Поиск игры»: экран ожидания в фазе «идёт поиск» на
    /// <see cref="SearchSeconds"/> секунд, затем объявление, баны и пики — как
    /// «Баны». Ещё одно нажатие — отмена.
    ///
    /// Владелец 15 октября: подсказку режима пула не успеть рассмотреть —
    /// «Баны» уводят окно в драфт сразу, а в бою она стоит всё время лобби и
    /// поиска. Слоты не чистятся: заранее выбранные пики уйдут в план, как при
    /// «Банах».
    /// </summary>
    private void ToggleSearch()
    {
        if (_searchEndAt is not null)
        {
            StopSearch();
            Log.Write("песочница: поиск игры отменён");
            Recompute();
            return;
        }
        StopSim();
        StopBanSim();
        // Окно на драфте — уводим его на экран ожидания тем же путём, что
        // «Сброс», только слоты не трогаем.
        if (_stage != TestStage.Ready)
        {
            _overlay.UpdateRecommendations(null, null);
            _overlay.DraftEnded();
            _overlay.HideRunes();
            _stage = TestStage.Ready;
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
        Log.Write($"песочница: поиск игры {SearchSeconds:0} с, затем баны");
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
    }

    private void StopSearch()
    {
        _searchTimer?.Stop();
        _searchEndAt = null;
        _searchBtn.Content = SearchIdle;
    }

    private void UpdateStageButtons()
    {
        void Set(Button b, bool on)
        {
            b.Background  = on ? new SolidColorBrush(Color.FromRgb(0x22, 0x36, 0x55)) : System.Windows.Media.Brushes.Transparent;
            b.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x5A, 0x8A, 0xC8) : Color.FromRgb(0x40, 0x50, 0x60));
            b.BorderThickness = new Thickness(1);
            b.Foreground  = on ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));
        }
        Set(_stageDraft, _stage == TestStage.Draft);
        Set(_stageBans,  _stage == TestStage.Bans);
        Set(_stageReady, _stage == TestStage.Ready);
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
