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
// Песочница — запуск: подготовка как в бою, подставной профиль, свои данные и выход в боевой режим.
// Подставные пулы, мастерство и история — в TestMode.Data.cs; панель — TestPanel*.cs.
static partial class TestMode
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
    /// Аккаунт без ранга: отборочные не сыграны, ранговых игр нет. На месте
    /// эмблемы — её скелетон; нормалы и ARAM — как в полном профиле.
    internal static SessionTracker.SessionData? UnrankedSession;
    /// Порог ленты «мои чемпионы»: три игры с драфтом — лента есть, два
    /// чемпиона и четыре пустых слота без блика.
    internal static SessionTracker.SessionData? ThreeGamesSession;
    /// Играет ARAM: среди последних двадцати игр две нормалки — ленты нет,
    /// под кнопками пула пусто; профиль открыт на ARAM, график по винрейту.
    internal static SessionTracker.SessionData? AramSession;

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

    internal static readonly (int, double, int)[] ThreeGamesChamps = [(86, 100, 2), (103, 0, 1)];

    internal static readonly (int, double, int)[] AramChamps = [(86, 100, 1), (103, 0, 1)];

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
        // Бан — в фазу банов панели: мой бан сделан, окно переходит к банам команд.
        overlay.BanLockHandler  = async id => { await Task.Delay(300, ct); panel?.LockMyBan(id); return 200; };

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
        EnterSandboxData(engine);

        // ТЕСТ владения: по умолчанию доступны ВСЕ чемпионы (в песочнице «нет
        // чемпиона» обычно мешает). Проверить плашку можно галочкой в панели —
        // тогда часть чемпионов считается отсутствующими.
        overlay.SetOwnedChampions(allIds);

        // ТЕСТ связок: без сыгранных игр раздел «Винрейт связок» всегда пуст, и
        // посмотреть, как он выглядит с данными, было нельзя. Подменная история
        // по играм (см. MakeGamePreview): у частого напарника пар больше и
        // выборка крупнее, у редкого — одна-две игры; есть игры старше месяца и
        // мимо пула.
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
                // Очереди без ранга — выбираются в списке очередей профиля: на
                // месте эмблемы силуэт карты, под играми W/L, график по винрейту.
                ["normal"] = Unranked("WLLWWLWWLWWLWW", [222, 222, 222, 39, 17]),
                ["aram"] = Unranked("WWLWLWWLLWWWLWLWWLWLW", [86, 103, 238, 99, 222]),
            });

        // Очередь без ранга: W/L и винрейт — по своим играм, точки графика так
        // же, как их считает журнал (со сглаживанием к 50%), игра в день.
        static SessionTracker.QueueView Unranked(string results, int[] champs)
        {
            var points = new List<SessionTracker.WrPoint>();
            int w = 0;
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] == 'W') w++;
                points.Add(new(DateTime.Now.AddDays(-results.Length + i + 1), 100.0 * (w + 3) / (i + 1 + 6)));
            }
            // Последние пять — от свежей к старой, без LP: в этих очередях его нет.
            var recent = Enumerable.Range(0, 5)
                .Select(k => new SessionTracker.RecentGame(champs[k], results[results.Length - 1 - k] == 'W', null))
                .ToList();
            int l = results.Length - w;
            return new SessionTracker.QueueView(false, "", "", 0, 0, w, l, 100.0 * w / results.Length,
                                                recent, points, []);
        }

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

        var noRank = new SessionTracker.QueueView(false, "", "", 0, 0, 0, 0, 0, [], [], []);
        UnrankedSession = new SessionTracker.SessionData(
            "TestSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = noRank,
                ["flex"] = noRank,
                ["normal"] = fakeSession.Queues["normal"],
                ["aram"] = fakeSession.Queues["aram"],
            });

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

        // Первые три игры из тех же пяти: Гарен дважды, Дариус один раз.
        ThreeGamesSession = FiveGamesSession with
        {
            Queues = new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = FiveGamesSession.Queues["solo"] with
                {
                    Wins = 2, Losses = 1, Winrate = 66.7,
                    Last5 = five.Take(3).ToList(), WinrateHistory = fiveHist.Take(3).ToList(),
                },
            },
        };
        AramSession = fakeSession with { SelectedQueue = "aram" };

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

        LeaveSandboxData();

        overlay.Dispatcher.Invoke(() =>
        {
            if (panel is not null) { panel.SwitchingToLive = true; panel.Close(); }
            // Сбрасываем всё тестовое: фейковый профиль, превью чемпионов,
            // рекомендации и мок-хендлеры — дальше данные придут из клиента.
            // Привязку к окну клиента возвращаем: в боевом режиме она нужна.
            overlay.SandboxMode = false;
            overlay.SetEmptyProfilePreview(false);
            overlay.SetChampsPreview(null);
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

    /// <summary>
    /// Профиль «алмаз II, 55%» — для снимков сайдбара на сайт. Ник, чемпионы
    /// последних игр и прочие очереди — настоящие, из журнала игрока (<paramref name="mine"/>,
    /// как в «моём аккаунте»; пул и винрейты чемпионов панель подключает той же
    /// подменой). Выдуманы только ранг, счёт сезона и график: рейтинг за месяц
    /// растёт с изумруда I до алмаза II постепенно — с откатом к границе тира и
    /// полками, как у живого игрока, а не ровной лестницей. Живёт только в
    /// памяти песочницы: журнал, настройки и синхронизация в ней не пишутся.
    /// </summary>
    internal static SessionTracker.SessionData DiamondSession(SessionTracker.SessionData? mine)
    {
        // 90 игр за месяц, от старой к свежей, побед 57%. Ряд подобран руками:
        // серий длиннее четырёх побед нет — иначе на графике видны рывки.
        const string results =
            "WWLWLLWWLLLWWLWWWWLWWWLWWLWLWLLLLWLLLWWLLLWWWLWLWWLWWLLLWWWLWLWWWLWLLWWWWLLWLWWWWLLWLWWWLW";
        const int n = 90;
        // Очки гуляют, как в клиенте: +18…+23 за победу, −16…−20 за поражение.
        static int Delta(int i, bool win) => win ? 18 + i * 7 % 6 : -(16 + i * 3 % 5);
        // Шаг ~7,8 ч: все 91 точка помещаются в окно графика в 30 дней.
        static DateTime At(int k) => DateTime.Now.AddHours(-(n - k) * 7.8 - 0.5);

        var win = results.Select(c => c == 'W').ToArray();
        var delta = Enumerable.Range(0, n).Select(i => Delta(i, win[i])).ToArray();

        // Последние пять (от свежей к старой): чемпионы — мои настоящие, а
        // исходы и очки — из хвоста ряда. Настоящие исходы владелец не просил:
        // свежие поражения роняли бы конец графика, а он должен расти.
        // Журнала нет — чемпионы свои.
        var mineLast = mine?.Queues.GetValueOrDefault("solo")?.Last5 ?? [];
        int[] champs = mineLast.Count >= 5
            ? [.. mineLast.Take(5).Select(g => g.ChampionId)]
            : [201, 412, 201, 117, 111];   // Браум, Треш, Браум, Лулу, Наутилус
        var last5 = Enumerable.Range(0, 5)
            .Select(k => new SessionTracker.RecentGame(champs[k], win[n - 1 - k], delta[n - 1 - k]))
            .ToList();

        // Идём от нынешнего ранга, как журнал (SessionTracker.RatingPoints), —
        // последняя точка совпадает с карточкой: алмаз II, 64 LP = 2664.
        var lp = 2400 + 200 + 64 - delta.Sum();
        var rating = new List<SessionTracker.LpPoint>(n + 1) { new(At(0), lp) };
        for (int i = 0; i < n; i++)
        {
            lp += delta[i];
            rating.Add(new(At(i + 1), lp));
        }

        // Сезонный счёт клиента 246–201 — это 55%. Винрейт после каждой игры —
        // назад от него, тем же расчётом, что в журнале (SeasonWrBefore).
        const int wins = 246, losses = 201;
        var wrHist = new List<SessionTracker.WrPoint>(n);
        for (int i = n - 1, w = wins, l = losses; i >= 0; i--)
        {
            wrHist.Add(new(At(i + 1), 100.0 * w / (w + l)));
            if (win[i]) w--; else l--;
        }
        wrHist.Reverse();

        var queues = new Dictionary<string, SessionTracker.QueueView>(
            mine?.Queues ?? new Dictionary<string, SessionTracker.QueueView>())
        {
            ["solo"] = new SessionTracker.QueueView(
                HasRank: true, Tier: "DIAMOND", Division: "II", Lp: 64, ProgressPct: 64,
                Wins: wins, Losses: losses, Winrate: 100.0 * wins / (wins + losses),
                Last5: last5, WinrateHistory: wrHist, RatingHistory: rating),
        };
        return new SessionTracker.SessionData(mine?.Nick ?? "TestSummoner", "solo", queues);
    }

    // ── Данные песочницы: вход, профиль, выход ─────────────────────────────
    //
    // Всё, что песочница делает с данными, — в трёх методах ниже. Их зовут и
    // сама песочница, и проверка tests/PlayerData: она прогоняет те же шаги на
    // подставных «данных игрока» и следит, что ни один его файл не изменился,
    // а пулы и статистика после выхода видны, как были.

    /// Вход: пулы — в своей папке, тестовые пулы и подменная история.
    internal static void EnterSandboxData(RecommendationEngine engine)
    {
        PoolStore.DirOverride = Sandbox.Dir;
        PoolStore.Reload();
        PoolStore.SetAccount("test-account", "TEST");
        // «Не советовать» — тоже своё и с чистого листа: подменная история не
        // растёт, и скрытый в прошлой песочнице так и висел бы скрытым.
        Dislikes.DirOverride = Path.Combine(Sandbox.Dir, "dislikes");
        try { Directory.Delete(Dislikes.DirOverride, recursive: true); } catch { /* не было */ }
        SeedTestPools();
        UseTestHistory(engine, true);
    }

    /// Профиль «мой аккаунт» (true) — копия пулов игрока и его настоящая
    /// статистика, только чтением; false — снова тестовые пулы и история.
    internal static void ApplyMyData(RecommendationEngine engine, bool on) =>
        ApplyAccountData(engine, on ? SessionTracker.LastAccountKey : null);

    /// <summary>
    /// Песочница на данных любого аккаунта из журнала: его пулы (копией; нет —
    /// пусто, как у него и есть), его игры, винрейты и связки. null — снова
    /// тестовые пулы и история.
    /// </summary>
    internal static void ApplyAccountData(RecommendationEngine engine, string? account)
    {
        if (account is not null)
        {
            PoolStore.ReplaceCurrent(PoolStore.ReadLive(account));
            UseTestHistory(engine, false);
            SessionTracker.UseStoredAccount(true, account);
        }
        else
        {
            PoolStore.ReplaceCurrent(null);
            SeedTestPools();
            SessionTracker.UseStoredAccount(false);
            UseTestHistory(engine, true);
        }
    }

    /// Выход в боевой режим: данные игрока снова пишутся, пулы — обратно к
    /// его настоящему файлу, все подмены сняты.
    internal static void LeaveSandboxData()
    {
        Sandbox.Active = false;
        PoolStore.DirOverride = null;
        PoolStore.Reload();
        Dislikes.DirOverride = null;
        SessionTracker.UseStoredAccount(false);   // аккаунт выставит клиент
        SessionTracker.Preview = null;            // связки — только настоящие
        SessionTracker.PreviewGames = null;
        DuoShare.Preview = null;
        Party.Sandbox = false;                    // в бою напарник только по пати
        Party.SandboxMate(null);                  // друг из панели — не пати; её пришлёт лобби
    }
}
