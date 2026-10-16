using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using Counterplay;
using Velopack;
using Velopack.Sources;

// Program — сессия клиента: события чемп-селекта и игры, драфт в окно.
partial class Program
{
    /// Обновить профиль игрока (ранг, LP, график) текущей сессии клиента;
    /// null — клиента нет. Нужно синхронизации: приехали игры с другого
    /// компьютера — профиль перерисовывается сразу, а не через минуту.
    private static Func<Task>? _refreshSession;

    // Бакеты, за которыми уже ходили в этом запуске: одна попытка на бакет.
    private static readonly HashSet<string> _bucketFetched = [];

    // ── Одна сессия клиента ────────────────────────────────────────────────────

    static async Task RunSessionAsync(OverlayWindow overlay, LcuCredentials creds, CancellationToken ct)
    {
        using var http = new LcuHttpClient(creds);

        // Язык клиента LoL — лучшая подсказка, чем язык Windows: человек с
        // английской системой, играющий в русский клиент, ждёт русский интерфейс
        // (у него и имена чемпионов русские). Ручной выбор это не трогает.
        _ = Task.Run(async () =>
        {
            try
            {
                var (code, body) = await http.GetAsync("/riotclient/region-locale", ct);
                if (code is < 200 or >= 300) return;
                using var doc = JsonDocument.Parse(body);
                var locale = doc.RootElement.TryGetProperty("locale", out var l) ? l.GetString() : null;
                if (Loc.FromClientLocale(locale) is { } lang)
                    overlay.Dispatcher.Invoke(() => Loc.SetLanguageAuto(lang));
            }
            catch { /* старый клиент или нет такого эндпоинта — остаёмся на языке Windows */ }
        }, ct);

        // Ждём пока LCU реально поднимется (lockfile появляется раньше первых ответов).
        // Ждём НЕ вечно: если клиент закрылся или lockfile протух (порт/пароль от
        // прошлой сессии), выходим — внешний цикл перечитает свежие креды.
        overlay.ShowStatus(Loc.T("status.lcuStarting"));
        var waitStart = DateTime.UtcNow;
        while (true)
        {
            try
            {
                var (s, _) = await http.GetAsync("/lol-gameflow/v1/availability", ct);
                if (s != 0) break;
            }
            catch (HttpRequestException)
            {
                if (!LcuFinder.IsClientRunning() || DateTime.UtcNow - waitStart > TimeSpan.FromSeconds(30))
                    return; // клиента нет или креды мертвы — начинаем заново
                await Task.Delay(2000, ct);
            }
        }

        // LCU ответил — соединение живо. Теперь слежению за окном разрешён
        // авто-возврат оверлея из трея (до этого он подавлен, чтобы на старте не
        // показывать «ожидание клиента»).
        overlay.SetLcuReady(true);

        // Ранг мог ещё не прочитаться — тогда берём запомненный с прошлого раза и
        // базу не трогаем. Раньше на его месте молча оказывался «изумруд», и
        // программа качала чужую базу, а следом свою.
        var tierBucket = await PlayerInfo.GetTierBucketAsync(http, ct)
                         ?? Settings.GetString("dataBucket");
        // Дуо считается по ВЫСШЕМУ бакету из двух: иначе у двоих в одном драфте
        // разные базовые винрейты, и подсказки расходятся при полностью совпавших
        // остальных данных. Поднимаем ЗДЕСЬ, до всего остального, — этим же
        // значением дальше качается база и собирается движок, а смена бакета сама
        // триггерит подкачку нужной базы.
        tierBucket = DuoShare.RaiseBucket(tierBucket) ?? tierBucket;
        // Запоминаем ранг для скачивания нужной базы на следующем запуске. Если
        // ранг сменился (поднялся), а на диске база ДРУГОГО одиночного бакета —
        // она не содержит его данных, поэтому подкачаем нужную сразу. Общая
        // база ("all") содержит все бакеты — до-качка не нужна.
        if (tierBucket is not null) Settings.Set("dataBucket", tierBucket);
        var loaded = DataDb.CurrentBucket;
        if (tierBucket is not null && loaded is not null && loaded != "all" && loaded != tierBucket
            && _bucketFetched.Add(tierBucket))
            // Add() — не только проверка ранга, но и защита от петли: если
            // закачка не довела до конца (оборвалась сеть, не записался тег),
            // переподключение к клиенту не должно тянуть базу снова и снова.
            //
            // ФОНОМ, как и на запуске. Раньше тут стоял EnsureAsync с экраном
            // загрузки — и это был последний путь, по которому программа могла
            // пропасть посреди сеанса: ранг читается уже после подключения к
            // клиенту, то есть человек к тому моменту сидит в лобби.
            //
            // Заодно снимается двойная закачка. Видно в журнале живого запуска:
            // запуск начал тянуть базу золота, следом ранг прочитался — и та же
            // база поехала ВТОРОЙ раз, 64 МБ вместо 32. Замок внутри
            // UpdateInBackgroundAsync пропустит только одну; до эло игрока
            // движок поработает на соседнем бакете, что несравнимо лучше
            // экрана загрузки.
            StartDbUpdate(overlay, tierBucket, ct);

        var mastery = await PlayerInfo.GetMasteryAsync(http, ct); // пул игрока (комфорт)
        // Чемпионы аккаунта: которых нет — помечаем в рекомендациях «нет чемпиона».
        overlay.SetOwnedChampions(await PlayerInfo.GetOwnedChampionsAsync(http, ct));
        // Аккаунт (puuid) — ключ пулов чемпионов: подгружаем набор этого игрока.
        var (poolPuuid, poolName) = await PlayerInfo.GetAccountAsync(http, ct);
        PoolStore.SetAccount(poolPuuid, poolName);

        // Обмен наигранностью с напарником по дуо-пулу: своё выкладываем, его
        // забираем в кэш. С пути запуска это снято намеренно — нынешний драфт
        // считается на ПРОШЛОМ снимке (его берёт оверлей из кэша), а забранное
        // пригодится следующему. Сеть на старте мы однажды уже убирали целиком,
        // возвращать её сюда незачем.
        StartDuoShareWatcher(() => mastery, ct);

        // Клиент назвал аккаунт — раньше этого синхронизироваться не с чем:
        // адрес строки на сервере считается из пароля И профиля.
        _ = SyncClient.AutoAsync(poolPuuid, "запуск", ct);

        // Импорт рун и билда прямо в клиент — по кнопкам в панели.
        overlay.ApplyRunesHandler  = (page, id, name) => RunesImporter.ApplyRunesAsync(http, page, id, name, ct);
        overlay.ApplySpellsHandler = spells => RunesImporter.ApplySpellsAsync(http, spells, ct);
        overlay.ExportBuildHandler = (core, full, alt, role, id, name) =>
            RunesImporter.ExportItemSetAsync(http, core, full, alt, role, id, name, ct);

        // Id моих текущих действий пика/бана — обновляются на каждом снимке сессии.
        // Кнопки в оверлее (навести/залочить/забанить) используют именно их.
        // -1 = действия нет (id 0 — валидный: в кастомках нумерация с нуля).
        int myPickActionId = -1;
        int myBanActionId  = -1;
        overlay.HoverHandler    = champId => RunesImporter.HoverChampionAsync(http, myPickActionId, champId, ct);
        overlay.LockHandler     = champId => RunesImporter.LockChampionAsync(http, myPickActionId, champId, ct);
        overlay.BanHoverHandler = champId => RunesImporter.HoverChampionAsync(http, myBanActionId, champId, ct);
        overlay.BanLockHandler  = champId => RunesImporter.LockChampionAsync(http, myBanActionId, champId, ct);

        RecommendationEngine? engine = null;
        // Скачанное фоном могло дождаться этого момента — сейчас движка ещё нет,
        // базу никто не держит.
        DataDb.ApplySwap();
        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is not null)
        {
            // Ранг ещё неизвестен — собираем движок на середине шкалы: без него
            // не будет ни тир-листа, ни подбора, а прочитается ранг событием
            // ниже, и движок пересоберётся уже под него.
            engine = RecommendationEngine.Create(dbPath, tierBucket ?? "emerald");
            engine.Mastery = mastery;
            overlay.SetEngine(engine);   // окну настроек пула — считать WR/дельту связок
            overlay.ShowReady();
        }
        else
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
        }

        /// <summary>
        /// Применить скачанную фоном базу и пересобрать движок.
        ///
        /// Зовётся только между драфтами. Подмена требует закрыть соединение с
        /// базой, а посреди драфта это отняло бы рекомендации на те полсекунды,
        /// ради которых программу и открывают.
        /// </summary>
        void ApplyNewDbIfReady()
        {
            if (!DataDb.SwapReady) return;
            try
            {
                var old = engine;
                engine = null;          // наружу битый движок не отдаём
                old?.Dispose();         // отпускаем файл базы
                if (!DataDb.ApplySwap()) return;   // занята — подождём ещё

                dbPath = RecommendationEngine.FindDb();
                if (dbPath is null) return;
                engine = RecommendationEngine.Create(dbPath, tierBucket ?? "emerald");
                engine.Mastery = mastery;
                overlay.SetEngine(engine);
                Log.Write("движок пересобран на свежей базе");
            }
            catch (Exception ex)
            {
                Log.Write($"пересборка движка на свежей базе не удалась: {ex.Message}");
            }
        }

        // Синк фазы геймфлоу при (ПЕРЕ)подключении. Подписка на события отдаёт
        // только БУДУЩИЕ события, а не текущее состояние: если сокет умер во время
        // игры и ожил после (клиент перезапустил UX / сеть моргнула), событие
        // «конец игры» прошло мимо — без этого синка _gameActive застрял бы в true
        // и оверлей навсегда остался бы в трее (ровно симптом «висит в процессах,
        // но окно не возвращается»). Читаем фазу явно и приводим видимость в норму.
        try
        {
            var (gfCode, gfBody) = await http.GetAsync("/lol-gameflow/v1/session", ct);
            if (gfCode == 200)
            {
                using var gdoc = JsonDocument.Parse(gfBody);
                var phase = PhaseOf(gdoc.RootElement);
                _restartSafe = phase != "ChampSelect";
                if (phase is "GameStart" or "InProgress" or "Reconnect")
                {
                    _restartSafe = false;
                    overlay.SetGameActive(true);
                    // По умолчанию прячемся: прозрачное topmost-окно мешает входу
                    // в игру. Настройкой это можно отключить.
                    if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                }
                else if (phase != "ChampSelect")
                {
                    // Только снимаем игровой флаг + задаём текст. Сам возврат из
                    // трея делает слежение за окном (уважая ручное скрытие и
                    // видимость клиента) — не форсим, иначе окно всплывало бы не к
                    // месту (в т.ч. на старте с автозапуском).
                    overlay.SetGameActive(false);
                    overlay.ShowReadyPhase(phase);
                }
            }
        }
        catch { /* геймфлоу временно недоступен — не критично */ }

        // Уже в чемп-выборе? — сразу покажем рекомендации
        var (initCode, initBody) = await http.GetAsync("/lol-champ-select/v1/session", ct);
        if (initCode == 200)
        {
            using var doc = JsonDocument.Parse(initBody);
            var draft = ChampSelectParser.Parse(doc.RootElement);
            myPickActionId = draft.MyPickActionId;
            myBanActionId  = draft.MyBanActionId;
            overlay.UpdateRecommendations(
                draft.IsAram ? engine?.RecommendAram(draft, AppSettings.Current.DraftCount)
                             : engine?.Recommend(draft, AppSettings.Current.DraftCount),
                draft, engine);
        }

        // Сменили аккаунт в клиенте — перечитываем ВСЁ, что к нему привязано.
        // Без этого панель ждала бы следующего тика фонового обновления (до
        // минуты) и показывала бы ранг с пулом прошлого игрока.
        async Task ReloadAccountAsync()
        {
            try
            {
                var (puuid, name) = await PlayerInfo.GetAccountAsync(http, ct);
                // Пусто — клиент между аккаунтами (вышли, ещё не вошли): ничего
                // не трогаем, дождёмся события о новом игроке.
                if (string.IsNullOrEmpty(puuid) || puuid == poolPuuid) return;
                poolPuuid = puuid;
                PoolStore.SetAccount(puuid, name);

                // Синхронизация — и здесь, а не только при старте. Программа
                // часто стартует раньше, чем игрок вошёл в клиент (автозапуск с
                // Windows): тогда аккаунта при старте нет, синхронизация при
                // старте пропускается, а пулы и игры с другого компьютера не
                // приезжали до конца первой игры. Владелец так и увидел «пропали
                // дуо-пул, новый пул и свежая история» — на диске всё было.
                _ = SyncClient.AutoAsync(puuid, "вход в аккаунт", ct);

                // Эло у нового аккаунта своё: и пул чемпионов, и владение
                // чемпионами, и рекомендации считаются по нему.
                // Ранг ещё не прочитался — оставляем прежний бакет. Иначе на
                // каждом старте качали бы базу «по умолчанию», а следом свою.
                var bucket = await PlayerInfo.GetTierBucketAsync(http, ct)
                             ?? Settings.GetString("dataBucket");
                if (bucket is null)
                {
                    Log.Write("ранг ещё не читается, база подождёт до следующего события");
                    return;
                }
                Settings.Set("dataBucket", bucket);
                mastery = await PlayerInfo.GetMasteryAsync(http, ct);
                overlay.SetOwnedChampions(await PlayerInfo.GetOwnedChampionsAsync(http, ct));

                if (engine is not null)
                {
                    // Бакет базы зашит в движок при создании — под новое эло его
                    // пересобираем (данные уже на диске, это дёшево).
                    // И тут по высшему: иначе пересборка под новое эло вернула
                    // бы движок на свой бакет, а напарников остался бы в прошлом.
                    bucket = DuoShare.RaiseBucket(bucket) ?? bucket;
                    if (bucket != tierBucket && dbPath is not null)
                    {
                        tierBucket = bucket;
                        // Прежний закрываем: он держал базу открытой, а SQLite в
                        // режиме WAL не отпускает файл даже на чтении. Брошенное
                        // соединение означало бы, что скачанную фоном базу уже
                        // никогда не подменить.
                        var old = engine;
                        engine = RecommendationEngine.Create(dbPath, bucket);
                        overlay.SetEngine(engine);
                        old.Dispose();
                    }
                    engine.Mastery = mastery;
                }

                overlay.RefreshPoolMode();        // пул нового игрока
                await RefreshSessionAsync();      // ник, ранг, LP, последние игры
            }
            catch { /* клиент в переходном состоянии — попробуем на следующем событии */ }
        }

        // Трекер сессии для экрана ожидания (ранг/LP/последние игры/винрейт).
        async Task RefreshSessionAsync()
        {
            // Доля роли — из движка: ею достраиваются роли в прошлых играх.
            // Клиент их отдаёт, но верны они только на бот-линии, остальное он
            // путает (замер: полная раскладка сходится в 16% команд).
            Func<int, string, double>? shares = engine is null ? null : engine.RoleShare;
            try { overlay.ShowSession(await SessionTracker.RefreshAsync(http, ct, shares)); }
            catch { /* LCU временно недоступен — пропускаем обновление */ }
        }
        await RefreshSessionAsync();
        _refreshSession = RefreshSessionAsync;

        // Периодическое обновление трекера: история матчей LCU появляется с
        // задержкой после конца игры — событие EndOfGame может прийти раньше её.
        // Раз в минуту тихо догоняем (2 лёгких GET к локальному LCU).
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); }
                catch (OperationCanceledException) { return; }
                await RefreshSessionAsync();
            }
        }, ct);

        // Состав пати на момент запуска: событие о лобби придёт только когда оно
        // изменится, а программу вполне могли включить с уже собранным лобби.
        async Task RefreshPartyAsync()
        {
            try
            {
                var (code, body) = await http.GetAsync("/lol-lobby/v2/lobby", ct);
                if (code == 200)
                    Party.Update(System.Text.Json.JsonDocument.Parse(body).RootElement);
            }
            catch { /* лобби нет или клиент занят — узнаем из события */ }
        }
        await RefreshPartyAsync();

        await using var socket = new LcuEventSocket(creds);
        await socket.ConnectAsync(ct);

        var lastHash = "";
        bool draftUnhidden = false; // сбрасывали ли ручное скрытие на этом драфте
        CancellationTokenSource? hideCts = null; // запланированное скрытие в трей
        // История ховеров своей команды за текущий драфт (cellId → чемпионы):
        // союзники в фазе банов перебирают несколько чемпионов — баны считаются
        // по всему показанному пулу, а не только по текущему наведению.
        var hoverHistory = new Dictionary<int, HashSet<int>>();
        // Вражеские баны уже показывались в этом драфте. Нужно одной строке
        // журнала: когда клиент их раскрывает — ещё в фазе банов или уже с
        // первым пиком (от этого зависит, увидит ли их панель банов).
        bool theirBansSeen = false;

        await foreach (var ev in socket.ReadEventsAsync(ct))
        {
            switch (ev.Uri)
            {
                case "/lol-summoner/v1/current-summoner":
                    await ReloadAccountAsync();
                    break;

                // Состав пати: кто со мной в лобби. Нужен, чтобы в драфте узнать
                // напарника по человеку. Delete (лобби закрылось) пропускаем —
                // оно исчезает при переходе в чемп-селект, то есть ровно тогда,
                // когда напарник и нужен.
                case "/lol-lobby/v2/lobby":
                    if (ev.EventType != "Delete") Party.Update(ev.Data);
                    break;

                case "/lol-gameflow/v1/session":
                    var phase = PhaseOf(ev.Data);
                    // Программу могли запустить, когда лобби уже собрано: события
                    // о нём тогда не будет, а начальный опрос пришёлся на момент
                    // без лобби. Пока состав неизвестен — переспрашиваем на каждой
                    // смене фазы, это один лёгкий запрос к локальному клиенту.
                    if (!Party.Known && phase is "Lobby" or "Matchmaking" or "ReadyCheck" or "ChampSelect")
                        await RefreshPartyAsync();
                    // Очередь лобби сменилась → подставляем режим пула, запомненный
                    // для НЕЁ (соло-дуо не переезжает во флекс, и наоборот).
                    if (QueueKeyOf(ev.Data) is { } qk && PoolStore.SetQueue(qk))
                        overlay.RefreshPoolMode();
                    // Фаза геймфлоу — единственный источник правды для трея.
                    if (phase is "GameStart" or "InProgress" or "Reconnect")
                    {
                        _restartSafe = false;      // не перезапускаемся посреди игры
                        // Игра идёт — оверлей скрыт в трее (не разворачиваем ни при каких
                        // событиях, иначе прозрачное topmost-окно блокирует вход в игру).
                        overlay.SetGameActive(true);
                        if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                        lastHash = "";
                    }
                    else if (phase != "ChampSelect")
                    {
                        // Драфта нет — самое время подменить скачанную фоном базу
                        // и поставить скачанное обновление.
                        _restartSafe = true;
                        ApplyNewDbIfReady();
                        ApplyUpdateIfIdle(overlay);
                        // Меню/лобби/конец игры — возвращаем оверлей из трея.
                        overlay.SetGameActive(false);
                        overlay.RestoreFromTray();
                        overlay.UpdateRecommendations(null, null);
                        // Меню/лобби/конец игры — программа простаивает: показываем
                        // сноску о программе и карусель советов вместо «Готов».
                        overlay.ShowReadyPhase(phase);
                        // После игры (EndOfGame) ранг/LP обновились — перечитываем трекер.
                        await RefreshSessionAsync();
                        // И отдаём свежую историю на сервер. Главный момент из
                        // всех: именно здесь появляются новые игры и связки.
                        _ = SyncClient.AutoAsync(PoolStore.AccountPuuid, "после игры", ct);
                        lastHash = "";
                        draftUnhidden = false; // новый драфт снова снимет ручное скрытие
                        hoverHistory.Clear();
                        theirBansSeen = false;
                    }
                    // ChampSelect: НЕ восстанавливаем здесь — показ управляется
                    // обработчиком champ-select и флагом _inTray. Иначе разворачивали бы
                    // окно обратно во время FINALIZATION (за 5с до игры).
                    break;

                case "/lol-champ-select/v1/session":
                    if (ev.EventType == "Delete")
                    {
                        // Конец драфта: в игру или дродж. НЕ восстанавливаем из трея —
                        // видимостью управляет фаза геймфлоу (Lobby/EndOfGame вернёт окно,
                        // GameStart оставит скрытым). Иначе при входе в игру окно всплывёт.
                        hideCts?.Cancel(); hideCts = null;
                        RunesDraftEnded();
                        overlay.UpdateRecommendations(null, null);
                        // Не просто статус: окно должно вернуться на экран готовности
                        // той фазы, в которой клиент уже находится (см. DraftEnded).
                        overlay.DraftEnded(); // подавится, если в трее
                        _restartSafe = true;  // драфт кончился — можно и то, и другое
                        ApplyNewDbIfReady();
                        ApplyUpdateIfIdle(overlay);
                        lastHash = "";
                        draftUnhidden = false; // новый драфт снова снимет ручное скрытие
                        hoverHistory.Clear();
                        theirBansSeen = false;
                    }
                    else
                    {
                        _restartSafe = false;   // идёт драфт — не до перезапуска
                        var draft = ChampSelectParser.Parse(ev.Data);
                        myPickActionId = draft.MyPickActionId;
                        myBanActionId  = draft.MyBanActionId;
                        // Выданная роль — в журнал игр: по ней следующая игра
                        // ляжет на свою линию (фаза планирования их и показывает).
                        if (!draft.IsAram && draft.Me is { ChampionId: > 0 } meNow)
                            SessionTracker.NoteDraftRole(meNow.ChampionId,
                                RecommendationEngine.LcuToDbRole(draft.MyPosition));

                        // За 5 секунд до конца финализации прячем оверлей в трей.
                        var (timerPhase, timeLeftMs) = ChampSelectTimer(ev.Data);
                        if (timerPhase == "FINALIZATION" && hideCts == null)
                        {
                            hideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var delay = Math.Max(0, timeLeftMs - 5000);
                            var token = hideCts.Token;
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                                    overlay.SetGameActive(true); // вход в игру — подавляем авто-показ
                                    if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                                }
                                catch (OperationCanceledException) { }
                            }, token);
                        }
                        else if (timerPhase != "FINALIZATION")
                        {
                            // Активный драфт (не финализация) — авто-показ оверлея разрешён,
                            // чтобы окно надёжно всплыло на новом чемп-селекте после игры.
                            overlay.SetGameActive(false);
                            // Один раз за драфт снимаем ручное скрытие: закрыл окно
                            // раньше — на новом чемп-селекте оно всё равно вернётся
                            // (но закрыть его В ТЕЧЕНИЕ драфта по-прежнему можно).
                            if (!draftUnhidden) { overlay.AllowAutoShow(); draftUnhidden = true; }
                        }

                        var hash = DraftHash(draft);
                        if (hash == lastHash) break;
                        lastHash = hash;

                        if (!theirBansSeen && draft.TheirTeamBans.Count > 0)
                        {
                            theirBansSeen = true;
                            Log.Write($"баны врагов открылись: {draft.TheirTeamBans.Count}, " +
                                      $"фаза банов {(draft.InBanPhase ? "ещё идёт" : "уже кончилась")}");
                        }

                        // Копим показанных командой чемпионов (пул на этот драфт).
                        foreach (var p in draft.MyTeam)
                        {
                            var idc = p.EffectiveChampionId;
                            if (idc == 0) continue;
                            if (!hoverHistory.TryGetValue(p.CellId, out var setc))
                                hoverHistory[p.CellId] = setc = [];
                            setc.Add(idc);
                        }

                        if (draft.IsAram)
                        {
                            overlay.UpdateRecommendations(
                                engine?.RecommendAram(draft, AppSettings.Current.DraftCount), draft, engine);
                            overlay.HideRunes();               // руны не в этом режиме
                        }
                        else if (draft.InBanPhase)
                        {
                            // Свой бан сделан — советовать нечего: окно показывает баны
                            // обеих команд, а подбор банов не считаем зря. В
                            // планировании окно показывает моих чемпионов на роли —
                            // советы банов посчитаются со стартом таймера банов.
                            // Наведения — копией: окно пересчитывает по ним советы после
                            // «не советовать» в своём потоке, а этот их дописывает.
                            overlay.UpdateBans(draft.MyBanDone || draft.InPlanning
                                                   ? [] : engine?.RecommendBans(draft, hoverHistory),
                                               draft, engine,
                                               hoverHistory.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value)));
                            overlay.HideRunes();               // руны — на этапе пика, не банов
                            _runesShownFor = "";               // сбросить, чтобы после банов показать заново
                        }
                        else
                        {
                            var recs = engine?.Recommend(draft, AppSettings.Current.DraftCount);
                            overlay.UpdateRecommendations(recs, draft, engine);

                            // Руны: панель для МОЕГО чемпиона (залоченного или наведённого).
                            // Плюс предзагрузка топ-кандидатов — чтобы к моменту пика
                            // данные уже лежали в памяти и панель появилась мгновенно.
                            if (recs is { Count: > 0 })
                                RunesClient.Prefetch(recs.Select(r => r.ChampionId),
                                                     RecommendationEngine.LcuToDbRole(draft.MyPosition), ct);

                            // И сборки ВРАГОВ: по ним видно, кто из них правда
                            // купит предмет, которым нас «накажут». Без этого
                            // строка контр-предметов судит по одному классу и
                            // предлагает Посох серафима тем, кто его не берёт.
                            RunesClient.PrefetchRoles(
                                draft.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                     .Select(p => (p.EffectiveChampionId,
                                                   RecommendationEngine.LcuToDbRole(p.Position))), ct);
                            _ = UpdateRunesAsync(overlay, draft, ct);
                        }
                    }
                    break;
            }
        }

        hideCts?.Cancel();
        RunesDraftEnded();
        engine?.Dispose();
        _refreshSession = null;
    }

    // Достаёт фазу таймера чемп-селекта и остаток времени (мс).
    static (string phase, double leftMs) ChampSelectTimer(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("timer", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            var ph = t.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? "" : "";
            double left = 0;
            if (t.TryGetProperty("adjustedTimeLeftInPhase", out var l) && l.ValueKind == JsonValueKind.Number)
                left = l.GetDouble();
            else if (t.TryGetProperty("timeLeftInPhase", out var l2) && l2.ValueKind == JsonValueKind.Number)
                left = l2.GetDouble();
            return (ph, left);
        }
        return ("", 0);
    }

    // Очередь текущего лобби из события геймфлоу: gameData.queue.id → наш ключ
    // (solo/flex/normal/aram). Нужна, чтобы режим пула помнился отдельно по очередям.
    static string? QueueKeyOf(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        if (!data.TryGetProperty("gameData", out var gd) || gd.ValueKind != JsonValueKind.Object) return null;
        if (!gd.TryGetProperty("queue", out var q) || q.ValueKind != JsonValueKind.Object) return null;
        if (!q.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) return null;
        return SessionTracker.QueueOf(id.GetInt32());
    }

    static string PhaseOf(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object &&
        data.TryGetProperty("phase", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";

    // В хэш входит EffectiveChampionId (залок ИЛИ ховер) — иначе наведение
    // чемпиона не меняло бы хэш и рекомендации не пересчитывались бы до лока.
    // MyPickActionId/MyPickInProgress тоже обязаны быть в хэше: свап позицией/
    // очередью и наступление моего хода не меняют составы, но без перерисовки
    // оверлей держит устаревший драфт — кнопка лока не появляется или лочит
    // по старому action id.
    static string DraftHash(DraftState s) =>
        string.Join(",", s.MyTeam.Select(p  => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.TheirTeam.Select(p => $"{p.EffectiveChampionId}:{p.Position}")) + "|" +
        string.Join(",", s.MyTeamBans) + "|" +
        string.Join(",", s.TheirTeamBans) + "|" +
        string.Join(",", s.Bench) + "|" +
        // Планирование → баны: окно меняет моих чемпионов на советы банов.
        (s.InPlanning ? "plan" : s.InBanPhase ? "ban" : "pick") + "|" +
        $"{s.MyPickActionId}:{s.MyPickInProgress}|" +
        // Пустой бан (время вышло) не меняет списков банов, а окно обязано
        // перейти от советов к банам команд.
        $"{s.MyBanActionId}:{s.MyBanInProgress}:{s.MyBanDone}|" +
        string.Join(",", s.ActiveCells) + $":{s.FirstPickCell}";
}
