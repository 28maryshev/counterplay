using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Counterplay;

/// <summary>
/// Дымовая проверка СОБРАННОЙ программы: <c>Counterplay.exe --smoke [файл итога]</c>.
///
/// Проверки гоняют код, а путь запуска собранной программы раньше не проходил
/// никто — так 1.3.48 ушла к игрокам с падением при показе профиля. Здесь
/// программа поднимается тем же путём, что и при обычном запуске
/// (<see cref="Program.CreateApplication"/>, главное окно), и по очереди
/// показывает всё, что видит игрок: профиль, объявление, баны, пики,
/// наведение, руны, конец драфта, окна пулов и настроек.
///
/// Окна скрыты (прозрачность 0 до показа), данные — во временной папке; база
/// матчей игрока, если она есть, читается и не меняется. Итог — код выхода
/// (0 — всё прошло) и текстовый файл: какой шаг упал и с каким исключением.
/// </summary>
internal static class Smoke
{
    private static readonly List<string> _report = [];
    private static string _step = "запуск";
    private static int _fails;
    private static string _reportPath = "";

    /// Сколько ждём весь сценарий. Обычный прогон — секунды; зависание —
    /// тоже поломка, и сказать о ней надо, а не висеть.
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    public static int Run(string[] args)
    {
        var at = Array.IndexOf(args, "--smoke");
        _reportPath = at + 1 < args.Length && !args[at + 1].StartsWith("--")
            ? args[at + 1]
            : Path.Combine(Path.GetTempPath(), "counterplay-smoke.txt");

        // Настоящая папка игрока — только чтобы читать из неё базу и справочник;
        // берём её у AppPaths ДО подмены (мимо AppPaths пути не собираем —
        // tests/PlayerData). Дальше своя папка — до первого обращения к хранилищам.
        var player = AppPaths.Root;
        var root = Path.Combine(Path.GetTempPath(), "counterplay-smoke", Environment.ProcessId.ToString());
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        if (File.Exists(Path.Combine(player, "data.db"))) DataDb.DirOverride = player;   // только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        CopyAssets(player, root);
        Log.Mode = "дымовой";

        // Зависание: сценарий не дошёл до конца — пишем, на каком шаге стоял.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(Deadline);
            Fail($"завис дольше {Deadline.TotalSeconds:0} с");
            Finish();
            Environment.Exit(2);
        }) { IsBackground = true };
        watchdog.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Fail("необработанное исключение", e.ExceptionObject as Exception);
            Finish();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Fail("исключение в фоновой задаче", e.Exception);
            e.SetObserved();
        };

        try
        {
            Loc.Init();
            var app = Program.CreateApplication();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // То, что в обычном запуске роняет программу, здесь записывается с
            // именем шага и не обрывает сценарий: видно все поломки разом.
            app.DispatcherUnhandledException += (_, e) =>
            {
                Fail("исключение в окне", e.Exception);
                e.Handled = true;
            };
            // Методы окна откладывают работу через Dispatcher.InvokeAsync, а
            // исключение в ней до DispatcherUnhandledException не доходит: оно
            // остаётся в задаче и всплывает, только когда её подберёт сборщик
            // мусора, — на чужом шаге или никогда. Поэтому к задаче каждой
            // операции, поставленной в очередь, цепляем продолжение «упала».
            // (В момент OperationCompleted задача ещё не помечена упавшей.)
            app.Dispatcher.Hooks.OperationPosted += (_, e) =>
                e.Operation.Task.ContinueWith(t => Fail("исключение в окне", t.Exception),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await Scenario(); }
                catch (Exception ex) { Fail("сценарий оборвался", ex); }
                finally { app.Shutdown(); }
            }));
            app.Run();
        }
        catch (Exception ex) { Fail("приложение не поднялось", ex); }

        // Последний сбор: брошенные задачи с исключениями всплывают только так.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Finish();
        return _fails == 0 ? 0 : 1;
    }

    private static async Task Scenario()
    {
        var ct = CancellationToken.None;

        await Step("справочник чемпионов и иконки", async () =>
        {
            if (!DataDragon.LoadFromCache(Loc.DDragonLocale))
                await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
            await IconCache.PreloadAllAsync(null, ct);
        });

        OverlayWindow w = null!;
        await Step("главное окно", () => { w = new OverlayWindow(); ShowHidden(w); });
        await Step("экран ожидания", () => w.ShowReady(Loc.T("status.readyIdle")));
        await Step("профиль игрока", () => w.ShowSession(Session()));
        await Step("подсказка режима пула", async () =>
        {
            // Напарник по дуо-пулу в лобби, а включён обычный режим: поверх
            // кнопок пула — карточка «включить дуо-пул», и кнопка его включает.
            var a = PoolStore.Current();
            a.DuoPools.Add(new DuoPool { Id = "smoke", FriendName = "smoke", FriendPuuid = "SMOKE-mate" });
            Lobby("""[{"puuid":"SMOKE-me"},{"puuid":"SMOKE-mate","gameName":"Mate"}]""");
            w.ShowReadyPhase("Lobby");
            await Settle();
            if (((FrameworkElement)w.FindName("PoolHintCard")).Visibility != Visibility.Visible)
                throw new InvalidOperationException("напарник в лобби, а подсказки про дуо-пул нет");
            ((System.Windows.Controls.Button)w.FindName("PoolHintApplyBtn"))
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Settle();
            if (PoolStore.Current().ActiveKind != PoolKind.Duo)
                throw new InvalidOperationException("«Включить» в подсказке не включил дуо-пул");

            // Как было: драфт дальше проверяется в обычном режиме и без пати.
            PoolStore.SetActive(PoolKind.Normal, null);
            a.DuoPools.RemoveAll(d => d.Id == "smoke");
            Lobby("""[{"puuid":"SMOKE-me"}]""");
            w.ShowReady(Loc.T("status.readyIdle"));
        });

        var db = RecommendationEngine.FindDb();
        if (db is null) Note("базы матчей нет — драфт не проверялся");
        else
        {
            RecommendationEngine engine = null!;
            await Step("движок подбора", () =>
            {
                engine = RecommendationEngine.Create(db, "emerald");
                engine.Mastery = new Dictionary<int, long>();
                w.SetEngine(engine);
            });

            await Step("заглушка центра", async () =>
            {
                // Банфаза, советы ещё не посчитаны: центр — заглушка, не экран ожидания.
                w.UpdateBans([], Draft(ban: true), engine);
                await Settle();
                if (((FrameworkElement)w.FindName("CenterSkeleton")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("центру показать нечего, а заглушки нет");
            });
            await Step("объявление пиков", () =>
                w.UpdateBans([], Draft(ban: true, planning: true), engine));
            await Step("советы банов", () =>
            {
                var d = Draft(ban: true);
                w.UpdateBans(engine.RecommendBans(d), d, engine);
            });
            await Step("не советовать", async () =>
            {
                // 👎 в углу карточки: совет уходит; скрытый бан — в окошке шапки
                // тир-листа, скрытый пик — в полосе над пиками; клик по иконке
                // возвращает. Бан и пик — разные списки.
                await Dislike("BanFullList", "TierHiddenBox", () =>
                {
                    var d = Draft(ban: true);
                    w.UpdateBans(engine.RecommendBans(d), d, engine);
                });
                await Dislike("FullRecList", "PickHiddenStrip", () =>
                {
                    // Я ещё не выбрал: у моего пика кнопки нет, он стоял бы первым.
                    var full = Draft(ban: false);
                    var team = full.MyTeam.Select(p => p.IsLocalPlayer ? p with { ChampionId = 0 } : p).ToList();
                    var d = full with { MyTeam = team, Me = team.First(p => p.IsLocalPlayer) };
                    w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
                });

                async Task Dislike(string listName, string hiddenName, Action show)
                {
                    show();
                    await Settle();
                    var list = (ItemsControl)w.FindName(listName);
                    int Id(int i) => list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement f
                        && Card(f) is { Tag: int id } ? id : 0;
                    var first = Id(0);
                    var btn = Find<ContentControl>(list.ItemContainerGenerator.ContainerFromIndex(0),
                                                   c => c.GetType() == typeof(ContentControl) && c.Tag is int)
                              ?? throw new InvalidOperationException($"{listName}: кнопки «не советовать» нет");
                    Press(btn);
                    await Settle();
                    if (Enumerable.Range(0, list.Items.Count).Any(i => Id(i) == first))
                        throw new InvalidOperationException($"{listName}: совет после 👎 остался");
                    var hidden = (FrameworkElement)w.FindName(hiddenName);
                    var icon = Find<Grid>(hidden, g => g.Tag is HiddenChampVm)
                               ?? throw new InvalidOperationException($"{hiddenName}: скрытого не видно");
                    Press(icon);
                    await Settle();
                    if (Id(0) != first || hidden.Visibility == Visibility.Visible)
                        throw new InvalidOperationException($"{listName}: из {hiddenName} не вернулся");
                }
            });
            await Step("баны команд", () =>
                w.UpdateBans([], Draft(ban: true, banDone: true, theirBans: [157, 238, 555]), engine));
            await Step("подбор пиков", () =>
            {
                var d = Draft(ban: false);
                w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
            });
            await Step("драфт без роли", async () =>
            {
                // Вслепую подбора нет — в центре мой пул под союзников.
                var d = Draft(ban: false) with { MyPosition = "" };
                w.UpdateRecommendations([], d, engine);
                await Settle();
                if (((FrameworkElement)w.FindName("PlanPanel")).Visibility != Visibility.Visible)
                    throw new InvalidOperationException("роли нет, а панели моих чемпионов нет");
                var p = Draft(ban: false);
                w.UpdateRecommendations(engine.Recommend(p, AppSettings.Current.DraftCount), p, engine);
            });
            await Step("наведение на карточку", () =>
            {
                var list = (ItemsControl)w.FindName("FullRecList");
                if (list.Items.Count == 0) throw new InvalidOperationException("карточек пиков нет");
                var card = Card(list.ItemContainerGenerator.ContainerFromIndex(0))
                           ?? throw new InvalidOperationException("карточка не найдена");
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseEnterEvent });
                card.RaiseEvent(new System.Windows.Input.MouseEventArgs(Mouse.PrimaryDevice, 0) { RoutedEvent = Mouse.MouseLeaveEvent });
            });
            await Step("руны и сборка", async () =>
            {
                // Макет рун: дымовой проверке нужна отрисовка панели, а не сеть.
                RunesClient.UseMock = true;
                var stats = await RunesClient.GetAsync(201, "support", ct);
                w.ShowRunes(stats, 201, DataDragon.Name(201), "support", null);
            });
            await Step("конец драфта", () => w.DraftEnded());
        }

        await Step("окно пулов", async () =>
        {
            var p = new PoolSettingsWindow(() => { });
            ShowHidden(p);
            await Settle();
            p.Close();
        });
        await Step("окно настроек", async () =>
        {
            var s = new SettingsWindow();
            ShowHidden(s);
            await Settle();
            s.Close();
        });
        await Step("закрытие", () => w.Close());
    }

    // ── Шаги ─────────────────────────────────────────────────────────────────

    private static Task Step(string name, Action act) => Step(name, () => { act(); return Task.CompletedTask; });

    /// Шаг идёт, пока окно не доделает всё, что он поставил в очередь: методы
    /// окна откладывают работу (Dispatcher.InvokeAsync), и исключение в ней
    /// случается уже после возврата из вызова.
    private static async Task Step(string name, Func<Task> act)
    {
        _step = name;
        var before = _fails;
        try
        {
            await act();
            await Settle();
        }
        catch (Exception ex) { Fail("", ex); }
        if (_fails == before) _report.Add($"ок     {name}");
    }

    /// Лобби, как его присылает клиент: участники, первый из них — я.
    private static void Lobby(string members) =>
        Party.Update(System.Text.Json.JsonDocument.Parse(
            $$"""{"localMember":{"puuid":"SMOKE-me"},"members":{{members}}}""").RootElement);

    private static async Task Settle()
    {
        for (var i = 0; i < 3; i++)
        {
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            await Task.Delay(50);
        }
    }

    // Одно исключение может прийти дважды: из очереди окна и из сборщика мусора.
    private static readonly HashSet<Exception> _seen = [];

    private static void Fail(string what, Exception? ex = null)
    {
        lock (_report)
        {
            if (ex is AggregateException { InnerExceptions.Count: 1 } agg) ex = agg.InnerExceptions[0];
            if (ex is not null && !_seen.Add(ex)) return;
            _fails++;
            var head = what.Length > 0 ? $"{_step}: {what}" : _step;
            _report.Add($"ПЛОХО  {head}{(ex is null ? "" : $" — {ex.GetType().Name}: {ex.Message}")}");
            if (ex?.StackTrace is { } st)
                _report.AddRange(st.Split('\n').Take(8).Select(l => "         " + l.TrimEnd()));
        }
    }

    private static void Note(string text) => _report.Add($"—      {text}");

    private static void Finish()
    {
        lock (_report)
        {
            var lines = new List<string>
            {
                $"Counterplay {typeof(Smoke).Assembly.GetName().Version}, дымовая проверка",
            };
            lines.AddRange(_report);
            lines.Add(_fails == 0 ? "ИТОГ: собранная программа проходит путь игрока"
                                  : $"ИТОГ: провалено — {_fails}");
            try { File.WriteAllLines(_reportPath, lines); } catch { /* итог всё равно в коде выхода */ }
            Console.WriteLine(string.Join(Environment.NewLine, lines));
        }
    }

    // ── Подставные данные ───────────────────────────────────────────────────

    /// Профиль с рангом, последними играми и обоими графиками: показ профиля и
    /// уронил 1.3.48.
    private static SessionTracker.SessionData Session()
    {
        var now = DateTime.Now;
        var last5 = new List<SessionTracker.RecentGame>
        {
            new(201, true, 21), new(412, false, -18), new(201, true, 20),
            new(117, true, 22), new(412, false, -19),
        };
        var wr = Enumerable.Range(0, 20).Select(i => new SessionTracker.WrPoint(now.AddDays(-20 + i), 50 + i % 5)).ToList();
        var lp = Enumerable.Range(0, 20).Select(i => new SessionTracker.LpPoint(now.AddDays(-20 + i), 1900 + i * 9)).ToList();
        return new SessionTracker.SessionData("SmokeSummoner", "solo",
            new Dictionary<string, SessionTracker.QueueView>
            {
                ["solo"] = new SessionTracker.QueueView(
                    HasRank: true, Tier: "EMERALD", Division: "II", Lp: 73, ProgressPct: 73,
                    Wins: 63, Losses: 55, Winrate: 53.4, Last5: last5,
                    WinrateHistory: wr, RatingHistory: lp),
            });
    }

    /// Драфт: я — саппорт. До пиков союзники пусты, на пиках — полный состав.
    private static DraftState Draft(bool ban, bool planning = false, bool banDone = false,
                                    IReadOnlyList<int>? theirBans = null)
    {
        var my = new List<DraftPlayer>
        {
            new(0, ban ? 0 : 126, 0, "middle", false),
            new(1, ban ? 0 : 17,  0, "jungle", false),
            new(2, ban ? 0 : 222, 0, "bottom", false),
            new(3, ban ? 0 : 201, 0, "utility", true),
            new(4, ban ? 0 : 106, 0, "top", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, ban ? 0 : 238, 0, "", false), new(6, ban ? 0 : 157, 0, "", false),
            new(7, ban ? 0 : 893, 0, "", false), new(8, ban ? 0 : 203, 0, "", false),
            new(9, ban ? 0 : 555, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: banDone ? [86] : [], TheirTeamBans: theirBans ?? [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: ban && !banDone, Bench: [], IsAram: false,
            MyPickActionId: planning ? 1 : -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: ban && !banDone ? 0 : -1,
            MyBanInProgress: ban && !banDone && !planning,
            MyBanDone: banDone, InPlanning: planning);
    }

    // ── Окна и файлы ────────────────────────────────────────────────────────

    /// Окно показываем так, чтобы человек его не увидел: прозрачность 0 до
    /// показа (вынос за экран не работает — окно возвращает себя в экран).
    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    /// Первый элемент типа T под d, подходящий под условие.
    private static T? Find<T>(DependencyObject? d, Func<T, bool> ok) where T : DependencyObject
    {
        if (d is null) return null;
        if (d is T t && ok(t)) return t;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (Find(System.Windows.Media.VisualTreeHelper.GetChild(d, i), ok) is { } r) return r;
        return null;
    }

    /// Нажатие левой кнопкой, как от мыши: MouseDown всплывает, и WPF по дороге
    /// поднимает MouseLeftButtonDown у каждого предка.
    private static void Press(UIElement el) =>
        el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                      { RoutedEvent = Mouse.MouseDownEvent });

    private static System.Windows.Controls.Border? Card(DependencyObject? d)
    {
        if (d is null) return null;
        if (d is System.Windows.Controls.Border b && b.Tag is int) return b;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
            if (Card(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } r) return r;
        return null;
    }

    /// Справочник и иконки — копией из папки игрока, чтобы не качать их по сети.
    /// Нет папки — справочник скачается сам (шаг «справочник чемпионов»).
    private static void CopyAssets(string player, string root)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(player, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(root, Path.GetRelativePath(player, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
    }
}
