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
    // Сессии для тестовых сценариев: полная история и «первая игра».
    public const int FirstGameChampion = 86;   // Гарен — узнаваемая иконка
    internal static SessionTracker.SessionData? FullSession;
    internal static SessionTracker.SessionData? FirstGameSession;
    internal static SessionTracker.SessionData? FiveGamesSession;
    /// Профиль «рывок»: серебро → платина за две недели. Нужен, чтобы видеть, как
    /// график рейтинга ведёт себя на широком диапазоне — подписи дивизионов там
    /// перестают помещаться и должны прореживаться.
    internal static SessionTracker.SessionData? ClimbSession;

    /// Нажали «Боевой режим»: песочница завершается, и Program подключается к
    /// настоящему клиенту. Нужно, чтобы проверять свежую сборку на своих данных
    /// LCU, не перезапуская программу с другими аргументами.
    private static readonly TaskCompletionSource LiveRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static bool SwitchToLive { get; private set; }

    internal static void RequestLiveMode()
    {
        if (SwitchToLive) return;
        SwitchToLive = true;
        LiveRequested.TrySetResult();
    }

    // Чемпионы в полосе винрейта для каждого сценария: (id, винрейт, игр).
    internal static readonly (int, double, int)[] FirstGameChamps = [(86, 100, 1)];
    internal static readonly (int, double, int)[] FiveGamesChamps =
        [(86, 100, 2), (103, 0, 2), (99, 100, 1)];

    public static async Task RunAsync(OverlayWindow overlay, bool emptyProfile, bool firstGame,
                                      bool fiveGames, CancellationToken ct)
    {
        // Песочница не привязывается к окну клиента: даже если лига запущена,
        // она к тесту отношения не имеет, а окно из-за неё прыгало и пряталось.
        overlay.SandboxMode = true;
        Party.Sandbox = true;   // пати нет — напарника ищем по чемпиону из половины друга

        // Та же подготовка, что в боевом режиме: статика, иконки, база.
        overlay.ShowStatus(Loc.T("status.loadingChamps"));
        await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
        overlay.ShowStatus(Loc.T("status.loadingIcons"));
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        // Иконки ролей — своим ходом, как в бою: при медленном Community Dragon
        // песочница стояла на «загрузке иконок» по минуте с лишним.
        Program.StartRoleIcons(overlay, ct);
        await ItemIcons.PreloadAsync(ct);
        // Бакет берём ТОТ ЖЕ, что и боевой режим (сохранённый ранг). Иначе теги
        // версий не совпадают ("all:…" против "gold:…") и база перекачивается
        // целиком при каждом переключении тест ↔ обычный режим.
        // Тот же бакет, что и в боевом режиме, ВКЛЮЧАЯ фолбэк «emerald». Без
        // фолбэка на свежей машине (ранг из LCU ещё не сохранён) песочница
        // просила общую базу всех эло, а боевой режим — эмеральдовую: теги версий
        // не совпадали, и база в 530 МБ качалась заново при каждой смене режима.
        var dbBucket = Settings.GetString("dataBucket") ?? "emerald";
        await DataDb.EnsureAsync(dbBucket, (msg, frac) => overlay.ShowProgress(msg, frac), ct);

        // Руны и сборки — НАСТОЯЩИЕ, те же, что в бою. Раньше песочница рисовала
        // правдоподобные выдумки: данных на сервере ещё не было. Теперь они есть,
        // а выдуманные предметы только вводили в заблуждение — по ним нельзя
        // судить ни о сборке, ни о подборе под состав врагов.
        RunesClient.UseMock = false;
        await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
        await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, ct);
        // Свойства предметов (броня, магзащита, срез лечения, пробивание) — на
        // них держится подбор сборки под состав врагов.
        await ItemFacts.LoadAsync(ct);
        await RunesClient.LoadManifestAsync(ct);
        // Сервер не ответил (нет сети) — панель осталась бы пустой, и обкатать её
        // было бы нельзя. Тогда, и только тогда, возвращаемся к макету.
        if (!RunesClient.ManifestLoaded)
        {
            RunesClient.UseMock = true;
            Log.Write("песочница: руны с сервера недоступны, панель работает на макете");
        }

        // Импорт в клиент из теста не делаем (клиента может не быть) —
        // кнопки отвечают «как будто получилось», чтобы проверить сценарий.
        TestPanel? panel = null;
        overlay.ApplyRunesHandler  = async (_, _, _) => { await Task.Delay(400, ct); return true; };
        overlay.ApplySpellsHandler = async _ => { await Task.Delay(200, ct); return true; };
        overlay.ExportBuildHandler = async (_, _, _, _, _, _) => { await Task.Delay(400, ct); return true; };
        // Наведение из оверлея ставит чемпиона в мой слот, как это делает клиент:
        // он отвечает на ховер полем championPickIntent, и сайдбар показывает
        // уже нового. Раньше хендлер отвечал «200» и выбрасывал чемпиона — в
        // песочнице клик по карточке менял кнопку, а слот оставался с прежним,
        // и весь путь «ткнул в кандидата → смотрю его» проверить было нельзя.
        overlay.HoverHandler = async id => { await Task.Delay(150, ct); panel?.SetMy(id); return 200; };
        // Лок из оверлея — туда же: в песочнице мой слот всегда ховер
        // (PickIntentId), отдельного «залочено» она не изображает.
        overlay.LockHandler  = async id => { await Task.Delay(300, ct); panel?.SetMy(id); return 200; };
        // Баны: без этих хендлеров кнопка бана в песочнице не появлялась вовсе
        // (клик по тир-листу/карточке молча выходил, а UpdateBanBar их проверяет).
        overlay.BanHoverHandler = async _ => { await Task.Delay(150, ct); return 200; };
        overlay.BanLockHandler  = async _ => { await Task.Delay(300, ct); return 200; };

        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is null)
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        // Тир-бакет для скоринга — тоже свой: в побакетной базе данных чужого
        // бакета попросту нет, и все показатели вышли бы пустыми.
        var engine = RecommendationEngine.Create(dbPath, dbBucket);
        overlay.SetEngine(engine);   // окну настроек пула — считать WR/дельту связок

        var allIds = DataDragon.GetAllIconUrls().Keys.ToList();

        // ТЕСТ пулов: обычный + дуо-пул, дуо активен — увидеть синий слот «пик из
        // пула» и иконку чемпиона дуо-друга. В бою пулы задаёт игрок в настройках.
        //
        // Пулы песочницы лежат в СВОЕЙ папке. Раньше она писала аккаунт
        // «test-account» в настоящий pools.json — тот же файл, что держит в
        // памяти запущенная рядом программа, и тот же, что синхронизация
        // переписывает целиком. Запись песочницы могла вернуть настоящие пулы к
        // виду на момент её запуска. Свои пулы игрока песочница теперь только
        // читает (PoolStore.ReadLive).
        PoolStore.DirOverride = Sandbox.Dir;
        PoolStore.Reload();
        PoolStore.SetAccount("test-account", "TEST");
        SeedTestPools();

        // ТЕСТ владения: по умолчанию доступны ВСЕ чемпионы (в песочнице «нет
        // чемпиона» обычно мешает). Проверить плашку можно галочкой в панели —
        // тогда часть чемпионов считается отсутствующими.
        overlay.SetOwnedChampions(allIds);

        // ТЕСТ связок: без сыгранных игр раздел «Винрейт связок» всегда пуст, и
        // посмотреть, как он выглядит с данными, было нельзя. Подменная история
        // по играм (см. MakeGamePreview): у частого напарника пар больше и
        // выборка крупнее, у редкого — одна-две игры; есть игры старше месяца и
        // мимо пула.
        UseTestHistory(engine, true);

        // ТЕСТ сессии: фейковый ник/ранг/W-L/график, чтобы был виден экран ready
        // с кнопками режимов пула (в бою данные приходят из клиента).
        // Список идёт от свежей игры к старой. Первой стоит поражение — на этом
        // сценарии видно, как низ шкалы горит красным.
        var last5 = new List<SessionTracker.RecentGame>
        {
            new(103, false, -21), new(86, true, 22), new(103, false, -18),
            new(238, true, 20), new(51, true, 19),
        };
        // Три месяца истории: иначе переключатель окна графика (30/90 дней)
        // нечем проверить — на 14 точках оба варианта выглядят одинаково.
        var hist = new List<SessionTracker.WrPoint>();
        var baseDate = DateTime.Now.AddDays(-95);
        double wr = 48; var rndWr = new Random(7);
        for (int i = 0; i < 95; i++) { wr = Math.Clamp(wr + rndWr.Next(-3, 4) * 0.6, 42, 62); hist.Add(new(baseDate.AddDays(i), wr)); }

        // История рейтинга для второго режима графика. Специально ведём её через
        // границу тира (эмеральд IV → платина I, 2000 LP), чтобы на тесте было
        // видно и деление на дивизионы, и смену цвета фона.
        var rating = new List<SessionTracker.LpPoint>();
        var lpAbs = 1780; var rndLp = new Random(11);
        for (int i = 0; i < 92; i++)
        {
            lpAbs += rndLp.Next(0, 10) < 6 ? rndLp.Next(12, 24) : -rndLp.Next(10, 22);
            rating.Add(new(DateTime.Now.AddDays(-92 + i), lpAbs));
        }
        rating.Add(new(DateTime.Now, 2073));   // эмеральд II, 73 LP — как в карточке
        var fakeSession = new SessionTracker.SessionData(
            "TestSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    // 73 LP при проигранной последней игре: верх шкалы красным
                    // не горит — там речь о том, что можно набрать, а не потерять.
                    HasRank: true, Tier: "EMERALD", Division: "II", Lp: 73, ProgressPct: 73,
                    Wins: 63, Losses: 55, Winrate: 53.4, Last5: last5, WinrateHistory: hist, RatingHistory: rating),
            });

        // Сценарий «поставил программу и сыграл одну игру»: ранг из клиента уже
        // есть, а СВОЯ история только началась — одна игра из пяти, одна точка
        // на графике, один чемпион в полосе. Остальное — скелетон.
        FirstGameSession = new SessionTracker.SessionData(
            "TestSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    // Лига ВЫШЕ основного профиля: переключение сюда показывает
                    // переход в новую лигу — сборку знака из двух половин.
                    HasRank: true, Tier: "DIAMOND", Division: "IV", Lp: 12, ProgressPct: 12,
                    // W/L и винрейт ранговой очереди приходят из клиента, а не
                    // копятся программой: человек ставит её посреди сезона и
                    // сразу видит свои 64–56.
                    Wins: 64, Losses: 56, Winrate: 53.3,
                    Last5: [new(FirstGameChampion, true, 22)],
                    // Две точки уже после первой игры: опора — сезонный винрейт
                    // на момент установки, вторая — после сыгранного матча.
                    WinrateHistory: [
                        new(DateTime.Now.AddHours(-2), 53.3),
                        new(DateTime.Now, 53.7)],
                    // Одна игра — и на графике рейтинга ровно одна точка.
                    RatingHistory: [new(DateTime.Now, 2412)]),
            });
        FullSession = fakeSession;

        // Тот самый «рывок»: 800 → 1660 LP (Silver IV → Platinum I) за 14 дней.
        var climb = new List<SessionTracker.LpPoint>();
        var climbLp = 812; var rndC = new Random(3);
        for (int i = 0; i < 60; i++)
        {
            climbLp += rndC.Next(0, 10) < 8 ? rndC.Next(16, 30) : -rndC.Next(10, 20);
            climb.Add(new(DateTime.Now.AddDays(-14).AddHours(i * 5.6), climbLp));
        }
        // Доводим до 73 LP: на этой отметке полоса показывает не только штриховку
        // будущей победы, но и засечку — границу между двумя играми до повышения.
        climb.Add(new(DateTime.Now, 1673));
        ClimbSession = new SessionTracker.SessionData(
            "TestSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    HasRank: true, Tier: "PLATINUM", Division: "I", Lp: 73, ProgressPct: 73,
                    Wins: 92, Losses: 61, Winrate: 60.1, Last5: last5,
                    WinrateHistory: hist, RatingHistory: climb),
            });

        // Пятая игра — момент, когда график только появляется: точек ровно
        // столько, сколько сыграно, а винрейт ещё скачет широкими шагами.
        var five = new List<SessionTracker.RecentGame>
        {
            new(86, true, 22), new(103, false, -19), new(86, true, 21),
            new(99, true, 20), new(103, false, -18),
        };
        // Значения — как их посчитает трекер: винрейт тянется к 50%, поэтому
        // первая победа даёт ~57%, а не 100%.
        var fiveHist = new List<SessionTracker.WrPoint>();
        var start = DateTime.Now.AddDays(-4);
        foreach (var (day, pct) in new[] {(0, 57.1), (1, 50.0), (2, 55.6), (3, 60.0), (4, 54.5)})
            fiveHist.Add(new(start.AddDays(day), pct));
        FiveGamesSession = new SessionTracker.SessionData(
            "TestSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    // Лига НИЖЕ основного профиля: переключение сюда показывает
                    // падение в прошлую лигу — знак просто выезжает снизу.
                    // 22 LP заодно держат нижний край шкалы с засечками.
                    HasRank: true, Tier: "PLATINUM", Division: "IV", Lp: 22, ProgressPct: 22,
                    Wins: 3, Losses: 2, Winrate: 60, Last5: five, WinrateHistory: fiveHist, RatingHistory: rating),
            });

        overlay.Dispatcher.Invoke(() =>
        {
            overlay.ShowReady(Loc.T("status.readyIdle") + " · TEST");
            overlay.EnableSaveForDraft();   // кнопка режима-для-драфта — только в тесте
            overlay.ShowSession(fakeSession);   // фейковый ник/ранг/график
            panel = new TestPanel(overlay, engine, allIds, emptyProfile, firstGame, fiveGames);
            panel.Show();
        });

        // Живём до закрытия окна/Ctrl+C — или до нажатия «Боевой режим»: тогда
        // возвращаем управление, и Program поднимает обычный цикл LCU.
        // «test golive» — тот же переход без клика: нужен, чтобы проверять
        // боевой режим автоматически.
        if (Environment.GetCommandLineArgs().Contains("golive")) RequestLiveMode();

        await Task.WhenAny(LiveRequested.Task, Task.Delay(Timeout.Infinite, ct));
        if (!SwitchToLive) return;

        // Дальше программа боевая: данные игрока снова пишутся, пулы — обратно
        // к его настоящему файлу.
        Sandbox.Active = false;
        PoolStore.DirOverride = null;
        PoolStore.Reload();
        SessionTracker.UseStoredAccount(false);   // аккаунт выставит клиент

        overlay.Dispatcher.Invoke(() =>
        {
            if (panel is not null) { panel.SwitchingToLive = true; panel.Close(); }
            // Сбрасываем всё тестовое: фейковый профиль, превью чемпионов,
            // рекомендации и мок-хендлеры — дальше данные придут из клиента.
            // Привязку к окну клиента возвращаем: в боевом режиме она нужна.
            overlay.SandboxMode = false;
            overlay.SetEmptyProfilePreview(false);
            overlay.SetChampsPreview(null);
            SessionTracker.Preview = null;   // связки — только настоящие
            SessionTracker.PreviewGames = null;
            DuoShare.Preview = null;
            Party.Sandbox = false;           // в бою напарник только по пати
            Party.SandboxMate(null);         // друг из панели — не пати; её пришлёт лобби
            overlay.ShowSession(null);
            overlay.UpdateRecommendations(null, null);
            overlay.ApplyRunesHandler = null;
            overlay.ApplySpellsHandler = null;
            overlay.ExportBuildHandler = null;
            overlay.HoverHandler = null;
            overlay.LockHandler = null;
            overlay.BanHoverHandler = null;
            overlay.BanLockHandler = null;
        });
        RunesClient.UseMock = false;   // руны — настоящие, из базы
    }

    // Почти все мидеры и саппорты — по доле игр на роли в базе (emerald, 16.18–
    // 16.19): мид от четверти игр, саппорт от половины. Мидеры уходят другу,
    // саппорты — мне: так пул «Друг» покрывает связку «я саппорт, он мид».
    internal static readonly int[] MidAll =
    [
        103, 711, 61, 38, 268, 127, 4, 55, 90, 7, 166, 105, 805, 1, 134, 893, 13, 3, 112, 142,
        8, 238, 84, 910, 69, 136, 34, 45, 157, 163, 800, 131, 101, 777, 245, 517, 39, 99, 246, 115,
    ];
    internal static readonly int[] SupportAll =
    [
        267, 902, 350, 201, 89, 412, 432, 117, 53, 888, 37, 526, 40, 555, 497, 16, 12, 111, 44, 43,
        235, 147, 26, 25, 518, 161, 143, 57, 63, 50, 99, 78, 223, 80,
    ];

    /// Тестовые пулы песочницы — если у её аккаунта пулов нет совсем.
    internal static void SeedTestPools()
    {
        var pools = PoolStore.Current();
        if (pools.Pools.Count > 0 || pools.DuoPools.Count > 0) { WidenTestDuo(pools); return; }
        // Мид и саппорт — широкие, как их собрал владелец в песочнице: на них
        // и держится подменная история (см. MakeGamePreview).
        var mine = new Dictionary<string, List<int>>
        {
            ["top"] = [86, 122, 54], ["jungle"] = [64, 19, 32],
            ["mid"] = [103, 238, 99, 34, 101, 131, 45, 268, 166, 142, 3, 893, 8, 38, 805, 55, 69, 127, 711],
            ["adc"] = [222, 22, 51],
            ["support"] = [.. SupportAll],
        };
        var friend = new Dictionary<string, List<int>>
        {
            ["top"] = [24, 92], ["jungle"] = [11, 60], ["mid"] = [.. MidAll],
            ["adc"] = [67, 236], ["support"] = [117, 40],
        };
        pools.Pools.Add(new ChampPool { Name = "Тест", ByRole = mine });
        // Дуо-пул сразу настроен на постоянного напарника подменной истории:
        // иначе раздел «винрейт связок» ждал бы, пока его опознают в драфте.
        pools.DuoPools.Add(new DuoPool
        {
            FriendName = "Друг", FriendPuuid = MateOften, FriendNick = MateOftenNick,
            Mine = mine, Friend = friend,
        });
        PoolStore.Persist();
        PoolStore.SetActive(PoolKind.Duo, pools.DuoPools[0].Id);
    }

    /// <summary>
    /// Разово дописать в уже собранный тестовый дуо-пул всех мидеров другу и всех
    /// саппортов мне — ПОВЕРХ того, что там собрано руками. Отметка в папке
    /// песочницы: второй раз не дописываем, иначе убранный чемпион возвращался бы
    /// при каждом запуске.
    /// </summary>
    private static void WidenTestDuo(AccountPools pools)
    {
        var mark = Path.Combine(Sandbox.Dir, "duo-widen-1");
        if (File.Exists(mark)) return;
        var d = pools.DuoPools.FirstOrDefault(x => x.FriendPuuid == MateOften);
        if (d is null) return;
        d.Friend["mid"]   = [.. d.FriendForRole("mid").Union(MidAll)];
        d.Mine["support"] = [.. d.MineForRole("support").Union(SupportAll)];
        PoolStore.Persist();
        try { File.WriteAllText(mark, ""); } catch { /* допишем в следующий раз — Union повторов не даст */ }
    }

    /// <summary>
    /// Тестовая история в дело (true) или прочь — профиль «мой аккаунт» (false).
    ///
    /// Подбор должен считать в песочнице так же, как в бою, поэтому из истории
    /// берётся всё, что в бою приходит из клиента и от напарника: журнал игр
    /// (личный винрейт, наигранность), очки мастерства и наигранность напарника
    /// по обмену.
    /// </summary>
    internal static void UseTestHistory(RecommendationEngine engine, bool on)
    {
        var games = on ? MakeGamePreview() : null;
        SessionTracker.PreviewGames = games;
        engine.Mastery = games is null ? new Dictionary<int, long>() : MakeMastery(games);
        DuoShare.Preview = games is null ? null : MakeMateComfort(games);
    }

    // Очков мастерства за игру. В бою они приходят из клиента; клиента в
    // песочнице нет, и без них наигранность держалась бы на одних играх за
    // месяц — не так, как в бою. Тысяча — порядок того, что Riot даёт за игру.
    private const long PointsPerGame = 1000;

    internal static Dictionary<int, long> MakeMastery(IEnumerable<SessionTracker.PreviewGame> games) =>
        games.Where(g => g.MyChampionId != 0)
             .GroupBy(g => g.MyChampionId)
             .ToDictionary(x => x.Key, x => x.Count() * PointsPerGame);

    /// <summary>
    /// Наигранность напарников — тем же расчётом, что DuoShare.Snapshot у
    /// настоящего напарника, только из его половины наших общих игр: его
    /// чемпион становится «моим», и дальше всё как у меня.
    /// </summary>
    internal static Dictionary<string, IReadOnlyDictionary<int, MateComfort>> MakeMateComfort(
        IReadOnlyList<SessionTracker.PreviewGame> games)
    {
        string[] queues = [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal];
        var res = new Dictionary<string, IReadOnlyDictionary<int, MateComfort>>();
        foreach (var mate in games.GroupBy(g => g.AllyPuuid))
        {
            var his = mate.Select(g => g with { MyChampionId = g.AllyChampionId, AllyChampionId = g.MyChampionId })
                          .ToList();
            var hist = SessionTracker.PreviewHistory(his, RecommendationEngine.FreshDays, queues);
            var pts  = MakeMastery(his);
            var map  = new Dictionary<int, MateComfort>();
            foreach (var id in his.Select(g => g.MyChampionId).Where(x => x != 0).Distinct())
            {
                var (g, w) = hist.Recent(id);
                map[id] = new MateComfort(g, w, pts.GetValueOrDefault(id),
                                          hist.DaysSinceNth(id, RecommendationEngine.RegularGames));
            }
            res[mate.Key] = map;
        }
        return res;
    }

    // Напарники подменной истории. Постоянный — тот, на кого настроен тестовый
    // дуо-пул «Друг» (см. SeedTestPools).
    internal const string MateOften = "TEST-mate-often-0000000000000000000000000000000000000000000000000";
    internal const string MateRare  = "TEST-mate-rare-00000000000000000000000000000000000000000000000000";
    internal const string MateOnce  = "TEST-mate-once-00000000000000000000000000000000000000000000000000";
    internal const string MateOftenNick = "Harribon";

    /// <summary>
    /// Подменная история совместных игр для песочницы — по игре на запись, с
    /// датой, ролями и исходом, как копит настоящий журнал.
    ///
    /// Раньше были готовые итоги по парам без дат, и почти все мимо тестового
    /// пула: в разделе «винрейт связок» оставалась одна плитка, а переключатель
    /// «за 30 дней / за всё время» ничего не менял.
    ///
    /// Постоянный напарник (Harribon): пары из обеих половин тестового пула «Друг»
    /// — от уверенно выигрышных до провальных, — плюс игры старше месяца (их видно
    /// только «за всё время») и пары мимо пула (их окно связок не показывает).
    /// Редкий и разовый напарники — для списка людей и чтобы чужие связки не
    /// лезли в пул.
    /// </summary>
    internal static List<SessionTracker.PreviewGame> MakeGamePreview()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();

        // Игры одной пары: results — по букве на игру (W/L) от свежей к старой,
        // разложены ровно между fromDays и toDays дней назад.
        void Pair(string puuid, string nick, string queue, int mine, string myRole,
                  int his, string hisRole, string results, double fromDays, double toDays)
        {
            for (var i = 0; i < results.Length; i++)
            {
                var days = results.Length == 1 ? fromDays
                    : fromDays + (toDays - fromDays) * i / (results.Length - 1);
                games.Add(new(puuid, nick, queue, mine, his, myRole, hisRole,
                              now - (long)(days * 86400) - 1800L * i, results[i] == 'W'));
            }
        }
        const string H = MateOftenNick;

        // ── Harribon, последний месяц: я саппорт, он стрелок ──
        Pair(MateOften, H, "solo",    89, "support", 236, "adc",     "WWLWLWWLW", 1, 28);  // Леона + Люциан
        Pair(MateOften, H, "solo",   412, "support",  67, "adc",     "WWWLWLW",   1, 26);  // Треш + Вейн
        Pair(MateOften, H, "flex",   412, "support", 236, "adc",     "LWLWW",     3, 24);  // Треш + Люциан
        Pair(MateOften, H, "flex",    16, "support", 236, "adc",     "LLW",       5, 18);  // Сорака + Люциан
        Pair(MateOften, H, "solo",   235, "support",  67, "adc",     "WLWW",      2, 21);  // Сенна + Вейн
        Pair(MateOften, H, "flex",   201, "support", 236, "adc",     "LWL",       6, 19);  // Браум + Люциан

        // ── Harribon, последний месяц: я саппорт, он мид ──
        Pair(MateOften, H, "solo",   412, "support",   4, "mid",     "WLWWW",     1, 25);  // Треш + Твистед Фэйт
        Pair(MateOften, H, "solo",    89, "support",  45, "mid",     "WWL",       4, 17);  // Леона + Вейгар
        Pair(MateOften, H, "flex",   267, "support",   4, "mid",     "WWLW",      2, 23);  // Нами + Твистед Фэйт
        Pair(MateOften, H, "flex",   235, "support",  45, "mid",     "LWW",       7, 20);  // Сенна + Вейгар
        Pair(MateOften, H, "solo",    43, "support",   4, "mid",     "WLW",       5, 27);  // Карма + Твистед Фэйт
        Pair(MateOften, H, "normal", 201, "support",  45, "mid",     "LL",        9, 11);  // Браум + Вейгар
        Pair(MateOften, H, "solo",   432, "support",   4, "mid",     "W",         3,  3);  // Бард + Твистед Фэйт
        Pair(MateOften, H, "flex",   161, "support",  45, "mid",     "WLLW",      8, 29);  // Вел'Коз + Вейгар

        // ── Harribon: я саппорт, он мид — широко, по всему пулу ──
        // Пар много, и вручную их не расписать: генерируем, но с постоянным
        // зерном — история одна и та же от запуска к запуску. Большинство пар на
        // одну-три игры, часть до восьми; винрейт пары от 30 до 75%. У каждой
        // пятой есть игры старше месяца.
        var rng = new Random(20261007);
        var made = new HashSet<(int, int)>();
        for (var n = 0; n < 90; n++)
        {
            var sup = SupportAll[rng.Next(SupportAll.Length)];
            var mid = MidAll[rng.Next(MidAll.Length)];
            if (sup == mid || !made.Add((sup, mid))) continue;
            string Results(int count, double wr) =>
                new([.. Enumerable.Range(0, count).Select(_ => rng.NextDouble() < wr ? 'W' : 'L')]);
            var wr    = 0.30 + rng.NextDouble() * 0.45;
            var count = 1 + rng.Next(rng.Next(3) == 0 ? 8 : 3);
            var from  = 1 + rng.NextDouble() * 12;
            var to    = Math.Min(29.5, from + rng.NextDouble() * 17);
            var queue = rng.Next(10) switch { < 5 => "solo", < 8 => "flex", _ => "normal" };
            Pair(MateOften, H, queue, sup, "support", mid, "mid", Results(count, wr), from, to);
            if (rng.Next(5) == 0)
                Pair(MateOften, H, queue, sup, "support", mid, "mid",
                     Results(1 + rng.Next(3), wr), 32 + rng.NextDouble() * 5, 40 + rng.NextDouble() * 18);
        }

        // ── Harribon, последний месяц: я мид, он лес ──
        Pair(MateOften, H, "solo",   103, "mid",      60, "jungle",  "WLWWLWW",   2, 28);  // Ари + Элиза
        Pair(MateOften, H, "solo",   238, "mid",      11, "jungle",  "WWLLW",     3, 25);  // Зед + Мастер Йи
        Pair(MateOften, H, "normal",  99, "mid",      11, "jungle",  "LLWL",      9, 21);  // Люкс + Мастер Йи
        Pair(MateOften, H, "solo",    34, "mid",      60, "jungle",  "WWWLWW",    1, 27);  // Анивия + Элиза
        Pair(MateOften, H, "flex",   101, "mid",      60, "jungle",  "LWLW",      5, 20);  // Зерат + Элиза
        Pair(MateOften, H, "solo",   131, "mid",      11, "jungle",  "WLW",       6, 15);  // Диана + Мастер Йи
        Pair(MateOften, H, "solo",    45, "mid",      60, "jungle",  "WWLWWLW",   2, 29);  // Вейгар + Элиза
        Pair(MateOften, H, "flex",   268, "mid",      11, "jungle",  "LLLW",      4, 24);  // Азир + Мастер Йи
        Pair(MateOften, H, "normal", 166, "mid",      60, "jungle",  "WL",        8, 12);  // Акшан + Элиза
        Pair(MateOften, H, "solo",   142, "mid",      60, "jungle",  "WWWW",      3, 18);  // Зои + Элиза
        Pair(MateOften, H, "flex",     3, "mid",      11, "jungle",  "WLWLW",     2, 26);  // Галио + Мастер Йи
        Pair(MateOften, H, "solo",   893, "mid",      60, "jungle",  "LW",        7,  9);  // Аврора + Элиза
        Pair(MateOften, H, "solo",     8, "mid",      11, "jungle",  "LWLLW",     5, 28);  // Владимир + Мастер Йи
        Pair(MateOften, H, "flex",    38, "mid",      11, "jungle",  "WWLW",      6, 23);  // Кассадин + Мастер Йи
        Pair(MateOften, H, "solo",   805, "mid",      60, "jungle",  "W",         4,  4);  // Локк + Элиза
        Pair(MateOften, H, "solo",    55, "mid",      11, "jungle",  "LLWL",      9, 21);  // Катарина + Мастер Йи
        Pair(MateOften, H, "flex",    69, "mid",      60, "jungle",  "WLW",      11, 19);  // Кассиопея + Элиза
        Pair(MateOften, H, "solo",   127, "mid",      60, "jungle",  "WWLWW",     2, 24);  // Лиссандра + Элиза
        Pair(MateOften, H, "normal", 711, "mid",      11, "jungle",  "LW",       13, 17);  // Векс + Мастер Йи

        // ── Harribon, последний месяц: остальное из пула ──
        Pair(MateOften, H, "flex",   222, "adc",     117, "support", "WWLW",      6, 27);  // Джинкс + Лулу
        Pair(MateOften, H, "solo",    51, "adc",      40, "support", "WL",       10, 16);  // Кейтлин + Жанна
        Pair(MateOften, H, "solo",    86, "top",      60, "jungle",  "W",         8,  8);  // Гарен + Элиза

        // ── Harribon, старше месяца: видно только «за всё время» ──
        Pair(MateOften, H, "solo",    89, "support", 236, "adc",     "LLW",      35, 55);
        Pair(MateOften, H, "flex",   412, "support",  67, "adc",     "WW",       40, 48);
        Pair(MateOften, H, "flex",    89, "support",  67, "adc",     "WLW",      33, 45);  // Леона + Вейн
        Pair(MateOften, H, "solo",   412, "support",   4, "mid",     "WWL",      36, 58);
        Pair(MateOften, H, "solo",   103, "mid",      60, "jungle",  "L",        50, 50);
        Pair(MateOften, H, "solo",    34, "mid",      60, "jungle",  "WL",       35, 45);
        Pair(MateOften, H, "flex",     3, "mid",      11, "jungle",  "W",        38, 38);
        Pair(MateOften, H, "solo",    45, "mid",      60, "jungle",  "LW",       33, 52);
        Pair(MateOften, H, "solo",   238, "mid",      11, "jungle",  "L",        40, 40);

        // ── Harribon мимо пула: в окне связок их нет, в сайдбаре есть ──
        Pair(MateOften, H, "normal", 555, "support",  22, "adc",     "LLL",       7, 19);  // Пайк + Эш
        Pair(MateOften, H, "flex",   412, "support",  21, "adc",     "WL",       11, 14);  // Треш + Мисс Фортуна

        // ── Другие люди ──
        Pair(MateRare, "Ozzy",  "normal", 103, "mid", 64,  "jungle", "WWLWLW", 10, 40);  // Ари + Ли Син
        Pair(MateRare, "Ozzy",  "normal", 157, "mid", 254, "jungle", "WL",     20, 25);  // Ясуо + Вай
        Pair(MateOnce, "Sanya", "solo",    86, "top", 122, "jungle", "W",      15, 15);  // Гарен + Дариус

        return games;
    }
}

/// <summary>Панель тестового драфта: 5 своих (с ролями) + 5 врагов + фаза банов.</summary>
sealed class TestPanel : Window
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
    private enum TestStage { Draft, Bans, Ready }
    // По умолчанию — экран Ready (ник/ранг/пул). Драфт и баны — ТОЛЬКО по кнопке.
    private TestStage _stage = TestStage.Ready;
    private readonly Button _stageDraft = new();
    private readonly Button _stageBans  = new();
    private readonly Button _stageReady = new();
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
        Width  = 620; Height = 540;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

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
            _meRadio[i].Checked += (_, _) => Recompute();
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
            Padding = new Thickness(0, 3, 0, 3)
        };
        reset.Click += (_, _) =>
        {
            StopSim();
            _ready = false;
            foreach (var cb in _ally.Concat(_enemy)) cb.SelectedIndex = 0;
            _autoPicked.Clear();
            _planned.Clear();
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
        Grid.SetRow(profileRow, 8); Grid.SetColumn(profileRow, 0); Grid.SetColumnSpan(profileRow, 3);
        root.Children.Add(profileRow);

        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(liveRow, 9); Grid.SetColumn(liveRow, 0); Grid.SetColumnSpan(liveRow, 3);
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

        // Закрыл панель — выходим из приложения целиком.
        // Закрыли песочницу — вышли из программы. Кроме одного случая: переход в
        // боевой режим тоже закрывает панель, и раньше это убивало приложение
        // целиком (окно исчезало, клиента никто не ждал).
        Closed += (_, _) =>
        {
            if (!SwitchingToLive) System.Windows.Application.Current.Shutdown();
        };
    }

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
    // Выбор помнится отметкой в папке песочницы: settings.json — файл игрока,
    // песочница в него не пишет (см. Sandbox).
    private static string MyAccountMark => Path.Combine(Sandbox.Dir, "my-account");
    private bool _myData;

    private void UseMyData(bool on)
    {
        if (on == _myData) return;
        _myData = on;
        try
        {
            Directory.CreateDirectory(Sandbox.Dir);
            if (on) File.WriteAllText(MyAccountMark, "");
            else File.Delete(MyAccountMark);
        }
        catch { /* не запомнилось — выберут ещё раз */ }
        if (on)
        {
            PoolStore.ReplaceCurrent(PoolStore.ReadLive(SessionTracker.LastAccountKey));
            TestMode.UseTestHistory(_engine, false);
            SessionTracker.UseStoredAccount(true);
        }
        else
        {
            PoolStore.ReplaceCurrent(null);
            TestMode.SeedTestPools();
            SessionTracker.UseStoredAccount(false);
            TestMode.UseTestHistory(_engine, true);
        }
        FillMates();   // пулы другие — и друзья другие
        _overlay.RefreshPoolMode();
        Recompute();
    }

    // Показ выбранного сценария профиля: пустой / первая игра / полная история.
    private void ApplyProfileScenario()
    {
        UseMyData(_profile.SelectedIndex == MyAccountProfile);
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
        _stage = s;
        UpdateStageButtons();
        // Оверлей могли закрыть крестиком — это помечает его «свёрнут вручную»,
        // и сам он больше не всплывёт. Переключение этапа — явное действие,
        // поэтому возвращаем принудительно, иначе окно драфта не появится.
        _overlay.RestoreFromTray(force: true);
        Recompute();
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

    // Пользователь сам поставил чемпиона → снимаем метку «авто» (авто-драфт его
    // больше не сотрёт). Программные установки авто-драфта идут с _autoSetting.
    private void OnChampChanged(int cell)
    {
        if (!_autoSetting) _autoPicked.Remove(cell);
        Recompute();
    }

    // Выпадающий список роли строки. Роли уникальны: выбрал занятую другой строкой
    // роль — строки меняются местами (перестановка ролей, как реальная смена линий).
    private ComboBox MakeRoleCombo(string[] roles, ComboBox[] combos, int i, string color)
    {
        var cb = new ComboBox
        {
            ItemsSource = RoleNames, Width = 54, Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, FontSize = 11, FontWeight = FontWeights.Bold,
            SelectedIndex = Array.IndexOf(LcuRoles, roles[i]),
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            ToolTip = "Роль строки — выбери вручную (роли уникальны: при повторе строки меняются местами)"
        };
        cb.SelectionChanged += (_, _) => OnRoleChanged(roles, combos, i);
        return cb;
    }

    private void OnRoleChanged(string[] roles, ComboBox[] combos, int i)
    {
        if (_settingRoles) return;
        int sel = combos[i].SelectedIndex;
        if (sel < 0) return;
        var newRole = LcuRoles[sel];
        if (roles[i] == newRole) return;
        int j = Array.IndexOf(roles, newRole);   // строка, у которой сейчас эта роль
        if (j >= 0) (roles[i], roles[j]) = (roles[j], roles[i]);   // меняем местами
        else roles[i] = newRole;
        SyncRoleCombos(roles, combos);
        Recompute();
    }

    private void SyncRoleCombos(string[] roles, ComboBox[] combos)
    {
        _settingRoles = true;
        for (int k = 0; k < 5; k++) combos[k].SelectedIndex = Array.IndexOf(LcuRoles, roles[k]);
        _settingRoles = false;
    }

    // Перемешивает роли строк ОБЕИХ команд (Фишер-Йетс) — независимо, и следит,
    // чтобы порядок врагов не совпал с моим: очередь пика у ролей всегда разная.
    private void ShuffleRoles()
    {
        static void Shuffle(string[] a, Random rng)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        Shuffle(_rowRoles, _rng);
        do Shuffle(_enemyRoles, _rng);
        while (_enemyRoles.SequenceEqual(_rowRoles));

        SyncRoleCombos(_rowRoles, _roleCombos);
        SyncRoleCombos(_enemyRoles, _enemyRoleCombos);
        Recompute();
    }

    // ── Друг ─────────────────────────────────────────────────────────────────

    /// Список друзей — дуо-пулы песочницы. Пулы сменились (другой профиль) —
    /// список заполняется заново, и друг сбрасывается: прежний пул исчез.
    private void FillMates()
    {
        _fillingMates = true;
        _mateOptions.Clear();
        var items = new List<string> { "— играю один" };
        _mateOptions.Add(null);
        foreach (var d in PoolStore.Current().DuoPools)
        {
            var nick = DuoNaming.PartnerNick(d, SessionTracker.TopPairs(d.FriendPuuid, 1));
            var tile = d.FriendName.Length > 0 ? d.FriendName : "дуо-пул";
            items.Add(nick.Length > 0 && nick != tile ? $"{nick} · {tile}" : tile);
            _mateOptions.Add(d);
        }
        _mateCombo.ItemsSource = items;
        _mateCombo.SelectedIndex = 0;
        _fillingMates = false;
        SelectMate(null);
    }

    private void SelectMate(DuoPool? duo)
    {
        _mate = duo;
        if (duo is null)
        {
            Party.SandboxMate(null);
            _mateRole = "";
            _fillingMates = true;
            _mateRoleCombo.SelectedIndex = -1;
            _fillingMates = false;
            _mateRoleCombo.IsEnabled = false;
            _mateNote = PoolStore.Current().DuoPools.Count == 0
                ? "дуо-пулов нет — настрой в окне пулов"
                : "";
            _mateInfo.Text = _mateNote;
            Recompute();
            return;
        }

        // Пул, настроенный на этого друга, — в дело, как кнопкой «Дуо».
        PoolStore.SetActive(PoolKind.Duo, duo.Id);
        _overlay.RefreshPoolMode();

        var nick = DuoNaming.PartnerNick(duo, SessionTracker.TopPairs(duo.FriendPuuid, 1));
        Party.SandboxMate(MatePuuid(duo), nick);

        _mateRole = DefaultMateRole(duo);
        _fillingMates = true;
        _mateRoleCombo.SelectedIndex = Array.IndexOf(LcuRoles, _mateRole);
        _fillingMates = false;
        _mateRoleCombo.IsEnabled = true;

        var together = duo.FriendPuuid.Length > 0
            ? SessionTracker.MateStats(duo.FriendPuuid).Games : 0;
        _mateNote = duo.FriendPuuid.Length > 0
            ? $"пул включён · вместе {together} игр"
            : "пул включён · напарник в пуле не записан, считаю его условным";
        _mateInfo.Text = _mateNote;
        Recompute();
    }

    /// puuid друга для его слота. У пула, собранного руками и ни разу не
    /// игранного вместе, хозяина нет — даём условный, и программа опознает
    /// его как человека из пати (запомнит в копии пула песочницы).
    private static string MatePuuid(DuoPool d) =>
        d.FriendPuuid.Length > 0 ? d.FriendPuuid : "TEST-friend-" + d.Id;

    /// Его роль по пулу: где в его половине больше всего чемпионов (у
    /// фиксированных связок — роль из первой пары). Мою роль пропускаем —
    /// на одной линии вдвоём не стоят.
    private string DefaultMateRole(DuoPool d)
    {
        var mine = _rowRoles[MeCell()];
        var byPool = d.Manual
            ? d.ManualPairs.Select(p => p.FriendRole).Where(r => r.Length > 0)
            : d.Friend.Where(kv => kv.Value.Any(x => x != 0))
                      .OrderByDescending(kv => kv.Value.Count(x => x != 0))
                      .Select(kv => kv.Key);
        foreach (var db in byPool)
        {
            var lcu = RecommendationEngine.DbToLcuRole(db);
            if (LcuRoles.Contains(lcu) && lcu != mine) return lcu;
        }
        return LcuRoles.First(r => r != mine);
    }

    /// Строка друга: союзник на его роли. -1 — друга нет или его роль совпала
    /// с моей.
    private int MateCell()
    {
        if (_mate is null || _mateRole.Length == 0) return -1;
        var i = Array.IndexOf(_rowRoles, _mateRole);
        return i == MeCell() ? -1 : i;
    }

    /// Чемпионы его половины на эту роль — из них он и пикает в авто-драфте.
    private static List<int> MatePicks(DuoPool d, string dbRole) =>
        d.Manual
            ? [.. d.ManualPairs.Where(p => p.FriendRole.Length == 0 || p.FriendRole == dbRole)
                               .Select(p => p.Friend).Where(x => x != 0).Distinct()]
            : [.. d.FriendForRole(dbRole).Where(x => x != 0)];

    // ── Авто-драфт ───────────────────────────────────────────────────────────

    private void ToggleSim()
    {
        if (_simTurn >= 0) { StopSim(); return; }

        // Старт: чистим клетки прошлого авто-драфта. Мой заранее выбранный пик
        // ЗАПОМИНАЕМ и ПРЯЧЕМ — он появится сам на моём ходу (для видео «человек
        // взял этот пик»), а не светится с начала. Прочие ручные пики сохраняем.
        _ready = false;
        // Захватываем РУЧНЫЕ пики (заполнены пользователем, не ботом) как «план»:
        // прячем их и покажем каждый на ходу своей клетки. Бот-пики прошлого
        // раунда — чистим (пересоберутся заново).
        _planned.Clear();
        for (int c = 0; c < 10; c++)
            if (CellChamp(c) > 0 && !_autoPicked.Contains(c))
                _planned[c] = CellChamp(c);
        foreach (var cell in _autoPicked.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;
        _autoPicked.Clear();
        foreach (var cell in _planned.Keys.ToList())
            (cell < 5 ? _ally[cell] : _enemy[cell - 5]).SelectedIndex = 0;   // прячем до хода
        _stage = TestStage.Draft; UpdateStageButtons();
        _overlay.RestoreFromTray(force: true);   // закрытый крестиком оверлей — вернуть
        _ready = true;

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

    // Собирает DraftState из панели и обновляет оверлей.
    private void Recompute()
    {
        if (!_ready) return;

        // Этап Ready: экран с ником/рангом/графиком + кнопки пула (вместо подбора).
        if (_stage == TestStage.Ready)
        {
            _overlay.ShowReady(Loc.T("status.readyIdle") + " · TEST");
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

        var my = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
        {
            var champ = ChampOf(_ally[i]);
            var isMe  = i == meIdx;
            var puuid = i == mateCell ? MatePuuid(_mate!) : "";
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

        bool banPhase = _stage == TestStage.Bans;
        var draft = new DraftState(
            my, their, [], [], my[meIdx], _rowRoles[meIdx],
            opp, false, banPhase, [], false,
            myPick ? 1 : -1, myPick && myTurn, active, firstPick,
            banPhase ? 1 : -1, banPhase);   // в тесте банфазы считаем, что мой ход банить

        if (banPhase)
        {
            _overlay.UpdateBans(_engine.RecommendBans(draft), draft, _engine);
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
