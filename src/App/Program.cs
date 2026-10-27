using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

// Program — вход: служебные режимы, одна копия, создание приложения и окна.
// Цикл клиента, сессия, фоновые задачи и обновления — в соседних Program.*.cs.
partial class Program
{
    // Подключаем WPF-процесс к консоли родителя — иначе Ctrl+C не работает при dotnet run
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

    [STAThread]
    static void Main(string[] args)
    {
        // Velopack: обязательно ПЕРВОЙ строкой — обрабатывает хуки установки/обновления.
        VelopackApp.Build().Run();

        AttachConsole(-1); // -1 = ATTACH_PARENT_PROCESS

        // Диагностический прогон движка рекомендаций (настройка весов), затем выход.
        if (args.Contains("--drafttest")) { DraftTest.Run().GetAwaiter().GetResult(); return; }

        // Дымовая проверка собранной программы (release.ps1, tests/Smoke): окна
        // скрыты, данные во временной папке, итог — кодом выхода. До замка
        // единственного экземпляра: рядом может работать настоящая программа.
        if (args.Contains("--smoke")) { Environment.Exit(Smoke.Run(args)); return; }

        // Диагностика автозапуска (поддержка, отладка): показать состояние и выйти.
        if (args.Contains("--autostart-info"))
        {
            Console.WriteLine($"supported: {Autostart.Supported}");
            Console.WriteLine($"stub:      {Autostart.StubPath ?? "(none)"}");
            Console.WriteLine($"enabled:   {Autostart.IsEnabled}");
            Console.WriteLine($"setting:   {Settings.GetBool("autostart")?.ToString() ?? "(unset)"}");
            return;
        }

        // Один экземпляр на пользователя. С автозапуском программа уже висит в
        // трее — второй запуск (клик по ярлыку) плодил бы второй оверлей и мешал
        // обновлению (Velopack держит блокировку папки). Вместо этого будим тот,
        // что уже работает, и выходим.
        // У песочницы свой замок: иначе она молча выходила, когда открыта боевая
        // программа, и вместо неё разворачивалось уже запущенное окно. Смотреть
        // на изменения рядом с настоящим окном — обычное дело при разработке.
        var sandbox = args.Contains("test") || args.Contains("--test");
        Sandbox.Active = sandbox;   // данные игрока — только чтением, см. Sandbox
        using var single = new Mutex(
            initiallyOwned: true,
            sandbox ? "Counterplay.SingleInstance.Test" : "Counterplay.SingleInstance",
            out var isFirst);
        // Двойной клик по файлу пула: путь приходит аргументом.
        var poolFile = args.FirstOrDefault(
            a => a.EndsWith(FileAssoc.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        if (!isFirst)
        {
            try
            {
                // Сигнал — голое событие, данных в нём нет. Путь передаём файлом:
                // кладём ДО сигнала, иначе первый экземпляр проснётся и ничего
                // не найдёт.
                if (poolFile is not null) OpenRequest.Put(poolFile);
                using var show = EventWaitHandle.OpenExisting(ShowSignal);
                show.Set(); // существующий экземпляр развернётся из трея
            }
            catch { /* сигнал не дошёл — просто выходим */ }
            return;
        }

        Loc.Init(); // язык интерфейса: сохранённый выбор → язык Windows → English

        // Шапку файла журнала пишет первая же запись, а первая запись случается
        // уже при создании окна — значит версию и режим надо знать до него.
        // Иначе в присланном журнале не видно, о какой сборке речь.
        Log.Mode = sandbox ? "тестовый" : "боевой";
        // Песочница живёт на той же базе, что и боевой экземпляр, и своей не
        // качает: подменить файл она всё равно не может, пока основной запущен.
        DataDb.ReuseLocal = sandbox;
        Log.Version = CurrentVersion();
        // Падение — в log.txt со стеком: иначе от него остаётся только запись
        // в журнале событий Windows, а у игрока её не попросишь.
        Log.CatchCrashes();

        // Автозапуск с Windows: включаем при первом старте новой версии и один раз
        // сообщаем об этом в панели (молча прописываться в автозагрузку — дурной тон).
        // Песочница систему не трогает: реестр прописал бы автозапуск и файлы
        // пулов на тот exe, из которого её запустили.
        var autostartNotice = !sandbox && Autostart.ApplyOnStartup();

        // Связка .cpool с программой: двойной клик по присланному файлу должен
        // открывать импорт, а не блокнот. Проверяем на каждом старте — путь
        // меняется при переустановке.
        if (!sandbox) FileAssoc.EnsureRegistered();

        // Анонимный пинг для статистики активных пользователей. Запуск
        // песочницы — не пользователь.
        if (!sandbox) _ = Telemetry.PingAsync();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var app     = CreateApplication();
        var overlay = new OverlayWindow();

        // Автозапуск с Windows: стартуем свёрнутыми в трей. Показывать окно
        // «LCU is starting…» при входе в систему — навязчиво; оверлей появится
        // сам, когда запустится клиент.
        if (args.Contains("--autostart")) overlay.StartInTray();
        else                              overlay.Show();

        if (autostartNotice) overlay.ShowAutostartNotice();

        // Повторный запуск (клик по ярлыку, пока мы в трее) — разворачиваем окно.
        StartShowSignalListener(overlay, cts.Token);

        // Программу запустили двойным кликом по файлу пула — предложим загрузить
        // его, как только окно готово принимать диалоги.
        if (poolFile is not null)
            overlay.Dispatcher.BeginInvoke(new Action(() => overlay.OpenPoolFile(poolFile)),
                                           System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        var lcuTask = Task.Run(async () =>
        {
            try
            {
                // «test empty» — сразу скелетон-вид (профиль без единой игры).
                if (sandbox)   // песочница-драфт без клиента LoL
                {
                    await TestMode.RunAsync(overlay, args.Contains("empty"), args.Contains("firstgame"),
                                            args.Contains("fivegames"), cts.Token);
                    // Песочница вернула управление — значит, в ней нажали
                    // «Боевой режим»: дальше работаем на настоящем клиенте.
                    //
                    // Аргументы песочницы обязательно вычищаем: путь к lockfile
                    // берётся как ПЕРВЫЙ не-флаговый аргумент, и слово «test»
                    // уходило туда как путь. Клиент по нему не находился, цикл
                    // сразу заканчивался — и программа закрывалась целиком.
                    if (TestMode.SwitchToLive)
                    {
                        var liveArgs = args
                            .Where(a => a is not ("test" or "--test" or "empty"
                                                  or "firstgame" or "fivegames" or "golive"))
                            .ToArray();
                        Console.WriteLine("[live] стартую боевой цикл, аргументы: "
                                          + (liveArgs.Length == 0 ? "(нет)" : string.Join(" ", liveArgs)));
                        await RunLcuAsync(overlay, liveArgs, cts.Token);
                    }
                }
                else await RunLcuAsync(overlay, args, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!overlay.Dispatcher.HasShutdownStarted)
            {
                try
                {
                    overlay.Dispatcher.Invoke(() =>
                        System.Windows.MessageBox.Show(ex.Message, "Counterplay", MessageBoxButton.OK, MessageBoxImage.Error));
                }
                catch (TaskCanceledException) { /* окно закрыли, пока показывали */ }
            }
            finally
            {
                // Выход из программы: окно закрыли, его очередь уже остановлена,
                // и синхронный Invoke бросал TaskCanceledException. Он уходил в
                // Main, и программа «падала» на каждом выходе — Windows писала
                // аварию (так было у 1.3.59 10.10 в 22:09). Очередь уже
                // останавливается — значит, и так закрываемся; иначе просим
                // закрыться, не дожидаясь.
                if (!overlay.Dispatcher.HasShutdownStarted)
                    overlay.Dispatcher.BeginInvoke(new Action(() => app.Shutdown()));
            }
        });

        overlay.Closed += (_, _) => cts.Cancel();
        app.Run();

        cts.Cancel();
        lcuTask.GetAwaiter().GetResult();
    }

    /// Приложение WPF с общим видом для всех окон: подсказки в фирменной
    /// плашке, а не системной. Окна пулов, настроек и подтверждения собраны
    /// кодом, своей разметки у них нет, и стиль до них доходит только отсюда.
    /// Дымовая проверка поднимает программу этим же путём.
    internal static System.Windows.Application CreateApplication()
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
        {
            // Путь с именем сборки, а не просто "/src/...": короткий вид
            // разрешается относительно точки входа, и вне приложения (в
            // проверке) словарь по нему не находится.
            Source = new Uri("/Counterplay;component/src/Ui/Theme.xaml", UriKind.Relative),
        });
        return app;
    }

    // Имя события «покажи окно» — им второй экземпляр будит первый.
    const string ShowSignal = "Counterplay.ShowWindow";

    // Слушаем сигнал от повторных запусков и разворачиваем оверлей из трея.
    static void StartShowSignalListener(OverlayWindow overlay, CancellationToken ct)
    {
        var handle = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignal);
        var thread = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!handle.WaitOne(500)) continue;
                overlay.RestoreFromTray(force: true);
                // Второй запуск мог принести файл пула — забираем и открываем.
                if (OpenRequest.Take() is { } file)
                    overlay.Dispatcher.BeginInvoke(new Action(() => overlay.OpenPoolFile(file)));
            }
        })
        { IsBackground = true };
        thread.Start();
    }
}
