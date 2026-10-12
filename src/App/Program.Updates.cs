using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

// Program — обновления программы: сторож, тихая установка, перезапуск.
partial class Program
{
    /// Скачанное обновление, которое ждёт тихой минуты. Пусто — ждать нечего.
    private static Velopack.VelopackAsset? _updateStaged;

    /// <summary>
    /// Можно ли сейчас перезапуститься.
    ///
    /// Нельзя в драфте и в игре: перезапуск отнимает секунду, но именно эта
    /// секунда — то, ради чего программу открывают. В меню, в лобби и при
    /// закрытом клиенте — можно.
    /// </summary>
    private static volatile bool _restartSafe = true;

    /// <summary>
    /// Поставить скачанное обновление и перезапуститься, если сейчас можно.
    ///
    /// Не время — ничего не делаем: позовут снова в конце драфта или на смене
    /// фазы. Совсем не позовут (человек закрыл программу) — обновление никуда
    /// не денется, оно уже на диске, и встанет при следующем запуске.
    ///
    /// Пробуем ОДИН раз за сеанс: если установка не удалась, повторять её по
    /// кругу — верный способ получить программу, которая только и делает, что
    /// перезапускается.
    /// </summary>
    static void ApplyUpdateIfIdle(OverlayWindow overlay)
    {
        var staged = _updateStaged;
        if (staged is null) return;
        if (!_restartSafe || overlay.GameActive) return;
        if (AlreadyFailed(staged.Version?.ToString())) { _updateStaged = null; return; }

        _updateStaged = null;
        try
        {
            Log.Write($"ставлю версию {staged.Version} и перезапускаюсь");
            overlay.ShowProgressBusy(Loc.T("status.applyingUpdate"));
            ApplySilently(overlay, new UpdateManager(UpdateSource), staged);
        }
        catch (Exception ex)
        {
            // Не встало — работаем на прежней версии, попробуем при запуске.
            Log.Write($"обновление не поставилось: {ex.GetType().Name} — {ex.Message}");
        }
    }

    /// <summary>
    /// С чем перезапускаться после обновления.
    ///
    /// Переносим свои ключи (в том числе --autostart: с ним программа
    /// поднимается сразу в трей) и путь к lockfile. Открытый двойным кликом
    /// файл пула НЕ переносим — это разовое действие, повторять его незачем.
    /// Был значок в трее — уходим в трей, даже если запускались иначе: человек
    /// убрал окно сам, и возвращать его без спроса невежливо.
    /// </summary>
    static string[] RestartArgs(OverlayWindow overlay)
    {
        var args = Environment.GetCommandLineArgs().Skip(1)
            .Where(a => !a.EndsWith(FileAssoc.Extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overlay.InTray && !args.Contains("--autostart")) args.Add("--autostart");
        return [.. args];
    }

    /// <summary>
    /// Источник обновлений — СТАТИЧЕСКИЙ фид, а не GitHub API.
    ///
    /// Почему не GithubSource: он ходит в api.github.com, а тот без токена даёт
    /// 60 запросов в час НА IP-АДРЕС. За общим IP (общежитие, интернет-кафе,
    /// мобильный оператор с NAT) эти 60 делят сотни чужих людей — и обновления
    /// у пользователя просто перестают приходить, молча. Мы дважды напоролись на
    /// это сами.
    ///
    /// Прямые ссылки на файлы релизов (CDN GitHub) лимитом НЕ ограничены.
    /// Тег `latest` — катящийся: release.ps1 перезаливает в него фид и пакеты,
    /// поэтому адрес всегда один и тот же.
    /// </summary>
    static Velopack.Sources.IUpdateSource UpdateSource =>
        new Velopack.Sources.SimpleWebSource(
            "https://github.com/28maryshev/counterplay/releases/download/latest/");

    // Фоновая проверка обновлений: первый заход через 10 минут, дальше раз в час.
    // С автозапуском программа висит в
    // трее сутками — без этого она узнала бы о новой версии только при следующем
    // запуске Windows. Обновление НЕ перезапускает приложение на ходу (человек
    // может быть в драфте): скачиваем и применяем при выходе.
    // Версия сборки: у установленной берём ту, что знает Velopack, у dev-сборки —
    // из атрибутов самой сборки.
    static string CurrentVersion()
    {
        try
        {
            var mgr = new UpdateManager(UpdateSource);
            if (mgr.IsInstalled && mgr.CurrentVersion is { } v) return v.ToString();
        }
        catch { /* не установлено через Velopack */ }
        var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return asm is null ? "dev" : $"{asm.Major}.{asm.Minor}.{asm.Build}";
    }

    static void StartUpdateWatcher(OverlayWindow overlay, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            // Первый заход — почти сразу: проверка ушла со старта сюда, и тянуть
            // с ней десять минут значило бы, что человек эти десять минут сидит
            // на старой версии. Пять секунд — чтобы не спорить за сеть с
            // разогревом и не мешать окну появиться.
            var delay = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
                delay = TimeSpan.FromHours(1);

                try
                {
                    var mgr = new UpdateManager(UpdateSource);
                    if (!mgr.IsInstalled) return;

                    // Признак «скачано и ждёт перезапуска» спрашиваем У VELOPACK, а не
                    // помним по факту своей же загрузки. Раньше уведомление показывалось
                    // ровно один раз — сразу после скачивания; если тот заход падал
                    // (сеть, лимит GitHub) или обновление скачал прошлый запуск, человек
                    // не узнавал о новой версии никогда.
                    if (mgr.UpdatePendingRestart is { } staged)
                    {
                        var ready = staged.Version?.ToString() ?? "";

                        // Уже на ней: пакет мог остаться лежать после УДАЧНОЙ
                        // установки. Без этой ветки отметка о попытке приняла бы
                        // удачу за неудачу и навсегда запретила бы эту версию.
                        if (ready == mgr.CurrentVersion?.ToString()) { ForgetUpdateAttempt(); continue; }

                        overlay.ShowUpdateReady(ready);
                        if (AlreadyFailed(ready))
                        {
                            Log.Write($"версия {ready} однажды не поставилась — сама больше не пробую");
                            continue;
                        }
                        // Качать нечего — но и ждать следующего запуска незачем:
                        // ставим в первую тихую минуту, как и всё остальное.
                        _updateStaged = staged;
                        ApplyUpdateIfIdle(overlay);
                        continue;
                    }

                    // Ставить нечего — значит, прошлая попытка удалась. Отметку
                    // снимаем, иначе она запретила бы и честное обновление до
                    // той же версии.
                    ForgetUpdateAttempt();

                    var info = await mgr.CheckForUpdatesAsync();
                    if (info == null) { Log.Write("обновлений нет (фоновая проверка)"); continue; }
                    var v = info.TargetFullRelease?.Version.ToString() ?? "?";
                    Log.Write($"фоновая проверка: есть версия {v}, качаю");
                    // Полосой внизу сайдбара, у версии — там же, где идёт
                    // загрузка базы. Раньше обновление качалось совсем молча:
                    // программа что-то тянула, а по ней этого было не видно.
                    await mgr.DownloadUpdatesAsync(info, pct =>
                        overlay.ShowSideProgress(
                            Loc.T("status.downloadingUpdate", pct, ""), pct / 100.0, "app"));
                    overlay.HideSideProgress("app");
                    Log.Write($"версия {v} скачана");
                    overlay.ShowUpdateReady(v);

                    // Ставим при первой возможности. Раньше скачанное ждало
                    // СЛЕДУЮЩЕГО ЗАПУСКА, а программа с автозапуском живёт
                    // неделями — человек всё это время сидел на старой версии,
                    // хотя новая лежала у него на диске.
                    _updateStaged = info.TargetFullRelease;
                    ApplyUpdateIfIdle(overlay);
                }
                catch (Exception ex)
                {
                    // Офлайн, лимит GitHub, битый фид — на следующем круге
                    // попробуем снова. Но молчать нельзя: раньше сюда уходила
                    // ЛЮБАЯ причина, по которой человек не получал обновление,
                    // и по жалобе «не качается» смотреть было не на что.
                    Log.Write($"фоновая проверка обновлений не удалась: {ex.Message}");
                }
            }
        }, ct);
    }

    /// <summary>
    /// Применить обновление и перезапуститься — без чужого окна поверх клиента.
    ///
    /// ApplyUpdatesAndRestart поднимает собственное окно Velopack: «Установка
    /// обновления, пожалуйста, подождите». Программа с автозапуском стартует
    /// вместе с клиентом LoL, и это окно вылезало поверх игры — притом что своё
    /// состояние мы и так показываем в сайдбаре.
    ///
    /// WaitExitThenApplyUpdates делает то же самое молча, но ЖДЁТ нашего
    /// выхода — и ждёт всего 60 секунд. Поэтому сразу за ним гасим программу:
    /// закрываем окно (на этом снимается значок из трея) и выходим. Мгновенный
    /// выход тут не грубость: прежний метод делал ровно то же, только со своим
    /// окном впридачу.
    /// </summary>
    static void ApplySilently(OverlayWindow overlay, UpdateManager mgr, VelopackAsset? asset)
    {
        // Помечаем попытку. Если установка не удастся, программа поднимется на
        // ПРЕЖНЕЙ версии, снова увидит готовое обновление и пойдёт ставить его
        // опять — бесконечный круг «ставлю — перезапуск — не встало», без
        // единого сообщения человеку. По этой отметке следующий запуск поймёт,
        // что эту версию уже пробовал.
        Settings.Set("updateTried", asset?.Version?.ToString() ?? "");
        mgr.WaitExitThenApplyUpdates(asset, silent: true, restart: true,
                                     restartArgs: RestartArgs(overlay));
        try { overlay.Dispatcher.Invoke(overlay.Close); }
        catch { /* окно уже закрыто — не повод не выходить */ }
        Environment.Exit(0);
    }

    /// <summary>
    /// Пробовали ли уже поставить эту версию — и остались на прежней.
    ///
    /// Значит, установка не прошла: сама программа второй раз за неё не
    /// берётся, иначе получится круг из перезапусков. Человеку версия
    /// покажется готовой — поставит вручную или переустановит.
    /// </summary>
    static bool AlreadyFailed(string? version) =>
        !string.IsNullOrEmpty(version) && Settings.GetString("updateTried") == version;

    /// Снять отметку о неудачной попытке. Пишем, только если есть что снимать:
    /// иначе файл настроек трогался бы на каждом запуске без всякой нужды.
    static void ForgetUpdateAttempt()
    {
        if (!string.IsNullOrEmpty(Settings.GetString("updateTried")))
            Settings.Set("updateTried", "");
    }
}
