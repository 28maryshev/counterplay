using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — пулы: окно пулов, файл пула, полоса моих чемпионов, режим подбора
// (обычный · пул · дуо).
public partial class OverlayWindow
{
    // ── Пул чемпионов против врагов: режимы (Настройки · Обычный · Пул · Дуо) ──
    private PoolSettingsWindow? _poolSettings;

    private void PoolSettings_Click(object sender, RoutedEventArgs e)
    {
        // Открываем окно настроек пулов (одно на приложение). После изменений —
        // обновляем кнопки и слот пула.
        if (_poolSettings is { IsVisible: true }) { _poolSettings.Activate(); return; }
        _poolSettings = new PoolSettingsWindow(() => { UpdatePoolButtons(); RefreshPoolSlot(); }, _engine);
        // Закрыли окно — отдаём правки на сервер. Пулы правят здесь и только
        // здесь, а ждать до конца следующей игры значит рисковать ими зря.
        _poolSettings.Closed += (_, _) =>
            _ = SyncClient.AutoAsync(PoolStore.AccountPuuid, "правка пулов");
        _poolSettings.Show();
    }

    /// <summary>
    /// Файл пула открыли двойным кликом в проводнике. Предлагаем загрузить его и
    /// спрашиваем, куда — в личные пулы или половиной дуо.
    ///
    /// Владельцем диалогов берём открытые настройки, если они на экране: иначе
    /// вопрос уехал бы ЗА них, и человек увидел бы застывшее окно.
    /// </summary>
    public void OpenPoolFile(string path)
    {
        try
        {
            // Диалогу нужен ПОКАЗАННЫЙ владелец: WPF не даёт назначить Owner окну,
            // которое ещё ни разу не показывали. При запуске двойным кликом по файлу
            // наш вызов вполне может обогнать первый показ оверлея.
            if (!IsVisible) { RestoreFromTray(force: true); Show(); }
            var owner = _poolSettings is { IsVisible: true } ? (Window)_poolSettings : this;
            owner.Activate();
            if (!PoolImport.Load(owner, path, null)) return;
            UpdatePoolButtons();
            RefreshPoolSlot();
            _poolSettings?.ReloadPools();
        }
        catch (Exception ex) { Log.Write($"файл пула не открылся: {ex.Message}"); }
    }

    /// Мои чемпионы за 30 дней — одна строка под кнопками пула на ready-экране.
    /// Показываем ровно столько, сколько влезает в ширину панели: строка не
    /// переносится и не растягивает окно (оно не должно быть выше клиента LoL).
    private const int MyChampsMax = 6;

    /// Лента — у того, кто играет с драфтом: не меньше MyChampsMinGames игр в
    /// ранкеде и нормалах среди последних MyChampsLastGames (решение владельца).
    private const int MyChampsLastGames = 20;
    private const int MyChampsMinGames = 3;

    // Предпросмотр «профиль без единой игры» — только тестовый режим: рисуем
    // ready-экран так, как его увидит человек сразу после установки.
    private bool _emptyProfilePreview;

    // Предпросмотр «сыграна первая игра»: в полосе чемпионов одна карточка,
    // остальные слоты — скелетон. Так видно переходное состояние, в котором
    // человек оказывается сразу после первого матча.
    // Подменные чемпионы для тестовых сценариев: (id, винрейт, число игр).
    // null — берём настоящую статистику из журнала.
    private IReadOnlyList<(int Id, double Wr, int Games)>? _champsPreview;

    public void SetChampsPreview(IReadOnlyList<(int Id, double Wr, int Games)>? champs) =>
        Dispatcher.Invoke(() =>
        {
            _champsPreview = champs;
            _emptyProfilePreview = false;
            UpdateMyChampsStrip();
            RenderSessionView();
        });

    public void SetEmptyProfilePreview(bool on) => Dispatcher.Invoke(() =>
    {
        if (on) _champsPreview = null;
        _emptyProfilePreview = on;
        UpdateMyChampsStrip();
        RenderSessionView();
    });

    /// <summary>
    /// Лента винрейтов в сайдбаре.
    ///
    /// Включён дуо-пул — показываем винрейт СВЯЗОК: с кем и на какой паре
    /// сколько выиграно. В обычном режиме и с личным пулом — свой винрейт по
    /// чемпионам, как было. Логика простая: лента отвечает на вопрос того
    /// режима, в котором игрок сейчас находится.
    /// </summary>
    private void UpdateMyChampsStrip()
    {
        if (PoolStore.Current().ActiveKind == PoolKind.Duo && TryDuoStrip()) return;

        var ranked = SessionTracker.ChampStatsMap(SessionTracker.QueuesRanked);
        var normal = SessionTracker.ChampStatsMap(SessionTracker.QueuesNormal);
        var items = ranked.Keys.Concat(normal.Keys).Distinct()
            .Select(id =>
            {
                var (rg, rw) = ranked.GetValueOrDefault(id);
                var (ng, nw) = normal.GetValueOrDefault(id);
                var g = rg + ng;
                return (Id: id, Games: g, Wr: g > 0 ? 100.0 * (rw + nw) / g : 0.0);
            })
            .Where(x => x.Games > 0)
            .OrderByDescending(x => x.Games)
            .Take(MyChampsMax)
            .Select(x => MyChampCard.FromChampion(
                IconCache.Get(x.Id),
                $"{x.Wr:F0}%",
                WinrateColor.BrushForSample(x.Wr, x.Games),
                WinrateColor.TintForSample(x.Wr, x.Games),
                $"{DataDragon.Name(x.Id)} — {x.Wr:F0}% / {x.Games}"))
            .ToList();

        if (_emptyProfilePreview) items.Clear();

        // Тестовый сценарий: показываем заданный набор вместо своей статистики.
        if (_champsPreview is { } preview)
        {
            items.Clear();
            foreach (var (id, wr, games) in preview.Take(MyChampsMax))
                items.Add(MyChampCard.FromChampion(
                    IconCache.Get(id),
                    $"{wr:F0}%",
                    WinrateColor.BrushForSample(wr, games),
                    WinrateColor.TintForSample(wr, games),
                    $"{DataDragon.Name(id)} — {wr:F0}% / {games}"));
        }

        // Игр с драфтом мало — ленты нет, остаются кнопки пула: у того, кто
        // играет ARAM, шесть пустых слотов висели бы вечно. Решают последние
        // игры, а не месяц: две нормалки среди ARAM о пуле не говорят ничего.
        var drafted = _champsPreview is { } shown
            ? shown.Sum(c => c.Games)
            : SessionTracker.RecentGamesIn(MyChampsLastGames,
                [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal]);
        if (drafted < MyChampsMinGames || items.Count == 0)
        {
            MyChampsStrip.ItemsSource = null;
            MyChampsBox.Visibility = Visibility.Collapsed;
            return;
        }

        // Чемпионов меньше, чем мест, — остаток стоит пустыми слотами без блика:
        // место видно, а с новым чемпионом слот просто наполнится.
        while (items.Count < MyChampsMax)
            items.Add(MyChampCard.Placeholder(Loc.T("session.needGames")));

        MyChampsStrip.ItemsSource = items;
        MyChampsBox.Visibility    = AppSettings.Current.ReadyChamps
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Лента связок для дуо-режима. false — связок ещё нет, и тогда лента
    /// остаётся обычной: пустые слоты «сыграйте вдвоём» ничего не объясняют,
    /// а свой винрейт по чемпионам полезен всегда.
    /// </summary>
    private bool TryDuoStrip()
    {
        if (_champsPreview is not null || _emptyProfilePreview) return false;

        // Напарник опознан (заходили с этим пулом в драфт вместе) — считаем по
        // нему. Ещё не опознан — берём связки со всеми, с кем играли.
        var a = PoolStore.Current();
        var duo = a.DuoPools.FirstOrDefault(d => d.Id == (a.ActiveId ?? a.FavDuoId));
        // Только с напарником по пулу: без опознанного человека лента
        // показывала связки со всеми, кого заносило в команду.
        if (duo is null || duo.FriendPuuid.Length == 0) return false;

        var pairs = SessionTracker.TopPairs(duo.FriendPuuid, MyChampsMax * 3);
        if (pairs.Count == 0) return false;

        // Связки, задуманные в пуле, — вперёд и с пометкой. В ленте они шли
        // вперемешку со случайными союзниками, хотя в окне пулов уже разделены.
        var inPool = InDuoPool(duo);
        pairs = pairs.OrderByDescending(p => inPool(p.MyChampionId, p.AllyChampionId))
                     .ThenByDescending(p => p.Games)
                     .Take(MyChampsMax).ToList();

        MyChampsStrip.ItemsSource = pairs.Select(p => MyChampCard.FromPair(
            IconCache.Get(p.MyChampionId),
            IconCache.Get(p.AllyChampionId),
            $"{p.WinRate:F0}%",
            WinrateColor.BrushForSample(p.WinRate, p.Games),
            WinrateColor.TintForSample(p.WinRate, p.Games),
            // В подсказке — вся связка целиком: на карточке помещается только
            // мой чемпион, и без неё две разные пары выглядели бы одинаково.
            $"{DataDragon.Name(p.MyChampionId)} + {DataDragon.Name(p.AllyChampionId)}"
            + (p.AllyName.Length > 0 ? $" ({p.AllyName})" : "")
            + $" — {p.WinRate:F0}% / {p.Games}"
            + (inPool(p.MyChampionId, p.AllyChampionId) ? "\n" + Loc.T("pool.inPool") : ""))).ToList();

        MyChampsBox.Visibility = AppSettings.Current.ReadyChamps
            ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    /// «Эта пара задумана в пуле?» — обе половины должны быть из него.
    private static Func<int, int, bool> InDuoPool(DuoPool? d)
    {
        if (d is null) return (_, _) => false;
        if (d.Manual)
        {
            var set = d.ManualPairs.Select(p => (p.Mine, p.Friend)).ToHashSet();
            return (m, f) => set.Contains((m, f));
        }
        var mine   = d.Mine.Values.SelectMany(l => l).Where(x => x != 0).ToHashSet();
        var friend = d.Friend.Values.SelectMany(l => l).Where(x => x != 0).ToHashSet();
        return (m, f) => mine.Contains(m) && friend.Contains(f);
    }

    /// Синхронизация переписала пулы или журнал игр — перечитать открытое окно
    /// пулов: иначе оно показывало бы прежнее, а правка в нём сохранила бы
    /// прежнее поверх пришедшего.
    public void ReloadPoolWindow() => Dispatcher.InvokeAsync(() =>
    {
        if (_poolSettings is { IsVisible: true } w) w.ReloadPools();
    });

    /// Сменилась очередь лобби — подтянуть запомненный для неё режим пула.
    public void RefreshPoolMode() => Dispatcher.InvokeAsync(() =>
    {
        UpdatePoolButtons();
        RefreshPoolSlot();
    });

    // Обновить слот пула, только если сейчас НЕ экран ready (т.е. показан подбор).
    // На ready-экране остаёмся: пул настраивается именно там, перерисовка в драфт
    // была бы перекидыванием (ReadyInfo — надёжный признак ready-экрана).
    private void RefreshPoolSlot()
    {
        if (ReadyScroll.Visibility == Visibility.Visible) return;
        RenderCurrentState();
    }

    private void PoolNormal_Click(object sender, RoutedEventArgs e)
    {
        PoolStore.SetActive(PoolKind.Normal, null);
        UpdatePoolButtons();
        RefreshPoolSlot();
    }

    private void PoolPool_Click(object sender, RoutedEventArgs e) => ShowPoolMenu(PoolKind.Pool, PoolPoolBtn);

    private void PoolDuo_Click(object sender, RoutedEventArgs e)  => ShowPoolMenu(PoolKind.Duo,  PoolDuoBtn);

    // Пункт меню: имя пула + приглушённая пометка режима («· Ручной» / «· Авто»).
    private static object MenuHeader(string name, string mode)
    {
        if (mode.Length == 0) return name;
        var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(new TextBlock
        {
            Text = "· " + mode, FontSize = 11, FontWeight = FontWeights.Normal,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
            Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
        });
        return sp;
    }

    // Меню выбора конкретного пула/дуо-пула; при одном — активируем сразу.
    private void ShowPoolMenu(PoolKind kind, System.Windows.Controls.Button anchor)
    {
        var a = PoolStore.Current();
        // У дуо-пула показываем ещё и его режим подбора пары (Ручной / Авто).
        var items = kind == PoolKind.Pool
            ? a.Pools.Select(p => (p.Id, Name: p.Name.Length > 0 ? p.Name : Loc.T("pool.pool"), Mode: "")).ToList()
            : a.DuoPools.Select(d => (d.Id, Name: d.FriendName.Length > 0 ? d.FriendName : Loc.T("pool.duo"),
                                      Mode: Loc.T(d.Manual ? "pool.duoManual" : "pool.duoAuto"))).ToList();
        if (items.Count == 0) return;   // нет пулов — создать в настройках (след. этап)

        // Отмечен звездой — включаем сразу его, без меню: звезда для того и
        // ставится, чтобы кнопка включала «тот самый» пул этой половины.
        var fav = PoolStore.Favourite(kind);
        if (items.Count == 1 || (fav is not null && items.Any(i => i.Id == fav)))
        {
            PoolStore.SetActive(kind, fav ?? items[0].Id);
            UpdatePoolButtons();
            RenderCurrentState();
            return;
        }
        var menu = new System.Windows.Controls.ContextMenu { Style = (Style)FindResource("PoolMenuStyle") };
        var itemStyle = (Style)FindResource("PoolMenuItemStyle");
        foreach (var (id, name, mode) in items)
        {
            var mi = new System.Windows.Controls.MenuItem { Header = MenuHeader(name, mode), Style = itemStyle };
            mi.Click += (_, _) => { PoolStore.SetActive(kind, id); UpdatePoolButtons(); RenderCurrentState(); };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = anchor;
        menu.IsOpen = true;
    }

    // Подсветка активного режима.
    private void UpdatePoolButtons()
    {
        UpdateMyChampsStrip();
        var a    = PoolStore.Current();
        var kind = a.ActiveKind;
        void Set(System.Windows.Controls.Button b, bool on)
        {
            b.Background  = on ? new SolidColorBrush(Color.FromRgb(0x22, 0x36, 0x55)) : System.Windows.Media.Brushes.Transparent;
            b.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x5A, 0x8A, 0xC8) : Color.FromRgb(0x40, 0x50, 0x60));
            b.Foreground  = on ? System.Windows.Media.Brushes.White : new SolidColorBrush(Color.FromRgb(0x9F, 0xB3, 0xC8));
        }
        Set(PoolNormalBtn, kind == PoolKind.Normal);
        Set(PoolPoolBtn,   kind == PoolKind.Pool);
        Set(PoolDuoBtn,    kind == PoolKind.Duo);

        // Имя выбранного пула / связки — в конце ряда.
        var name = kind switch
        {
            PoolKind.Pool => a.Pools.FirstOrDefault(p => p.Id == a.ActiveId)?.Name ?? "",
            PoolKind.Duo  => a.DuoPools.FirstOrDefault(d => d.Id == a.ActiveId)?.FriendName ?? "",
            _             => "",
        };
        PoolActiveName.Text       = name.Length > 0 ? "· " + name : "";
        PoolActiveName.ToolTip    = name.Length > 0 ? name : null;
        PoolActiveName.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Режим сменился — подсказка о нём могла стать лишней (или нужной).
        UpdatePoolHint();
    }
}
