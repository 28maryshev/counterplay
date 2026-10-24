using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

/// <summary>
/// Трекер сессии для экрана ожидания: ник, ранги, последние игры и винрейт —
/// РАЗДЕЛЬНО по четырём очередям (Solo/Duo, Flex, Normal, ARAM): кто-то играет
/// только нормалы или только ARAM. LP за игру и историю винрейта LCU не отдаёт —
/// трекер ведёт собственный журнал игр между запусками
/// (%APPDATA%\Counterplay\session.json). Ключ Riot не нужен — только LCU.
/// </summary>
// Журнал игр — каркас: виды для экрана, очереди, файл session.json, аккаунты и выбор очереди.
// Модель файла, роли, связки, ранг и обновление из клиента — в соседних SessionTracker.*.cs.
public static partial class SessionTracker
{
    public sealed record RecentGame(int ChampionId, bool Win, int? LpDelta);

    public sealed record WrPoint(DateTime Date, double Winrate);

    /// Точка графика рейтинга: абсолютный LP (тир*400 + дивизион*100 + очки) —
    /// в такой шкале дивизионы ложатся ровными полосами по 100.
    public sealed record LpPoint(DateTime Date, int AbsLp);

    /// Статистика одной очереди для панели трекера.
    public sealed record QueueView(
        bool HasRank, string Tier, string Division, int Lp, int ProgressPct,
        int Wins, int Losses, double Winrate,
        IReadOnlyList<RecentGame> Last5,
        IReadOnlyList<WrPoint> WinrateHistory,
        IReadOnlyList<LpPoint> RatingHistory);

    public sealed record SessionData(string Nick, string SelectedQueue, IReadOnlyDictionary<string, QueueView> Queues);

    /// Ключи очередей в порядке выпадающего списка.
    public static readonly string[] QueueKeys = ["solo", "flex", "normal", "aram"];

    /// Больше этого за одну игру LP не меняется (даже с промо/демоушеном).
    /// Всё, что выше — признак смены аккаунта или сброса сезона, а не результата.
    private const int MaxLpPerGame = 100;

    /// Сколько игр нужно, чтобы показывать винрейт и график. По одной-двум играм
    /// «100%» и «0%» — не статистика, а шум, который вдобавок портит масштаб.
    public const int MinGamesForChart = 5;

    /// Окно графика: три месяца. Прошлогодняя форма ничего не говорит о
    /// сегодняшней, а журнал не растёт без предела.
    /// Сколько истории рейтинга отдаём наружу. Окно показа (30 или 90 дней)
    /// выбирается в настройках, поэтому здесь держим максимум — иначе переключатель
    /// «90 дней» не смог бы показать больше, чем ему дали.
    private const int RatingDays = 90;

    public const int ChartDays = 90;

    /// Сколько «виртуальных» игр по 50% подмешивать в винрейт для графика.
    /// Это не подгонка цифры, а признание того, что по трём играм винрейт
    /// неизвестен: точка стартует у середины и расходится по мере реальных игр.
    private const int PriorGames = 6;

    /// Винрейт со сглаживанием к 50%.
    private static double SmoothWr(int wins, int games) =>
        100.0 * (wins + PriorGames * 0.5) / (games + PriorGames);

    /// Swiftplay: чемпион выбирается в лобби до поиска. Игры пишутся как
    /// нормалы, а подбор в драфте там не показывается (решение владельца, 24.10).
    public const int Swiftplay = 480;

    /// Показывать ли подбор в драфте этой очереди.
    public static bool DraftHelp(int queueId) => queueId != Swiftplay;

    // queueId LCU → наш ключ очереди. Нормалы — 400 драфт и 480 Swiftplay (430
    // блайнд и 490 квикплей из клиента ушли — Quickplay сменил Swiftplay, — но
    // остаются в старых историях). ARAM: Mayhem (2400) во всём как ARAM
    // (решение владельца, 24.10). Номера сверены с клиентом 24.10.2026.
    public static string? QueueOf(int queueId) => queueId switch
    {
        420                       => "solo",
        440                       => "flex",
        400 or 430 or 480 or 490  => "normal",
        450 or 2400               => "aram",
        _                         => null,
    };

    private static string StorePath =>
        Path.Combine(AppPaths.Root, "session.json");

    // Один Refresh за раз: конец матча порождает шквал gameflow-событий, и
    // параллельные вызовы гонялись на файле журнала (затирали его пустым).
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// Аккаунт, под которым сейчас сидят. null — клиент ещё не отдал игрока.
    private static Account? CurrentAccount()
    {
        if (_account is null) return null;
        var store = Load();
        return store.Accounts.GetValueOrDefault(_account);
    }

    /// Правит журнал после ошибки в привязке LP: победа отдавала свои очки
    /// предыдущему поражению, и в истории оставалась пара «поражение +23» и
    /// «победа 0». Раз известно, чьи это очки, возвращаем их победе; если
    /// подходящей игры рядом нет — просто стираем, прочерк честнее чужого числа.
    private static void FixImpossibleLp(Store store)
    {
        foreach (var acc in store.Accounts.Values)
            foreach (var q in acc.Queues.Values)
            {
                var games = q.Games;
                for (int i = 0; i < games.Count; i++)
                {
                    if (games[i].Lp is not int lp || lp == 0 || games[i].Win == lp > 0) continue;

                    var next = i + 1 < games.Count ? games[i + 1] : null;
                    if (next is not null && next.Lp is null or 0 && next.Win == lp > 0)
                        next.Lp = lp;      // очки нашли своего владельца
                    games[i].Lp = null;
                }
            }
    }

    private static Store Load()
    {
        // Основной файл, затем резервная копия — журнал не теряется из-за
        // одного битого чтения.
        foreach (var path in new[] { StorePath, StorePath + ".bak" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? new Store();
                FixImpossibleLp(store);
                return store;
            }
            catch { /* пробуем следующий */ }
        }
        return new Store();
    }

    /// <summary>
    /// Аккаунт по ключу; создаёт при первом появлении. Заодно разово переносит
    /// данные старого (одноаккаунтного) формата — но ТОЛЬКО если ник совпадает:
    /// иначе история эмеральд-аккаунта прилипла бы к свежему золотому.
    /// </summary>
    private static Account GetAccount(Store s, string key, string? nick)
    {
        if (s.Accounts.TryGetValue(key, out var acc)) return acc;

        acc = new Account { Nick = nick };

        var hasLegacy = s.Queues is { Count: > 0 } || s.Games is { Count: > 0 };

        // Наследие принадлежит тому, чей ник в нём записан. Если ник совпал —
        // переносим, даже если аккаунтов уже несколько (человек мог сначала
        // зайти на смурф, а потом вернуться на основной — история его ждёт).
        // Если ника в наследии нет вовсе (совсем старый файл) — отдаём первому.
        var namedOwner = !string.IsNullOrEmpty(s.Nick) && !string.IsNullOrEmpty(nick) &&
                         string.Equals(s.Nick, nick, StringComparison.OrdinalIgnoreCase);
        var anonymousLegacy = string.IsNullOrEmpty(s.Nick) && s.Accounts.Count == 0;

        if (hasLegacy && (namedOwner || anonymousLegacy))
        {
            acc.Queues       = s.Queues ?? new();
            acc.Ranked       = s.Ranked ?? new();
            acc.LastGameId   = s.LastGameId;
            acc.SelectedQueue = s.SelectedQueue ?? "solo";
            acc.QueueChosen  = s.QueueChosen || (s.SelectedQueue is not null and not "solo");

            // Совсем старый формат: один журнал без очередей → solo.
            if (s.Games is { Count: > 0 })
            {
                var q = GetQueue(acc, "solo");
                if (q.Games.Count == 0)
                {
                    q.Games.AddRange(s.Games.Select(g => new GameLog
                    { Ts = g.Ts, ChampionId = g.ChampionId, Win = g.Win, Lp = g.LpDelta, Wr = g.Winrate }));
                    q.LastAbsLp = s.LastAbsLp;
                }
            }
            // Наследие перенесено — чистим, чтобы не прилипло ко второму аккаунту.
            s.Queues = null; s.Ranked = null; s.Games = null;
            s.Nick = null; s.LastGameId = 0; s.LastAbsLp = int.MinValue;
        }

        // Лечим записи, испорченные до раздельного учёта аккаунтов: дельта в
        // сотни LP — это разница рангов двух аккаунтов, а не результат игры.
        foreach (var q in acc.Queues.Values)
            foreach (var g in q.Games)
                if (g.Lp is { } lp && Math.Abs(lp) > MaxLpPerGame)
                    g.Lp = null;

        s.Accounts[key] = acc;
        return acc;
    }

    private static QueueLog GetQueue(Account a, string key)
    {
        if (!a.Queues.TryGetValue(key, out var q)) a.Queues[key] = q = new QueueLog();
        return q;
    }

    /// Порядок игр очереди — по времени записи: по нему считаются «последние
    /// пять» и график. Порядок равных сохраняется.
    private static void SortByTs(List<GameLog> games)
    {
        var sorted = games.OrderBy(g => g.Ts).ToList();
        games.Clear();
        games.AddRange(sorted);
    }

    private static void Save(Store s)
    {
        // Песочница журнал не пишет: рядом работает боевая программа, и две
        // записи одного файла теряли бы игры. Выбор очереди в песочнице так и
        // остаётся в её окне, а не переключает график в боевом сайдбаре.
        if (Sandbox.Active) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            // Атомарная запись: tmp + подмена; прошлая версия остаётся как .bak.
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s));
            if (File.Exists(StorePath))
                File.Copy(StorePath, StorePath + ".bak", overwrite: true);
            File.Move(tmp, StorePath, overwrite: true);
            // Журнал переписан — статистика из кэша устарела. Без этого игры,
            // добитые из истории при первом запуске, 20 секунд не видела лента
            // «мои чемпионы»: экран успевал закэшировать пустой журнал.
            DropCache();
        }
        catch { /* не критично */ }
    }

    // ── Выбранная очередь (переключатель в панели) ──────────────────────────
    // Настройка своя у каждого аккаунта: на смурфе можно смотреть нормалы, на
    // основном — соло/дуо.

    private static string? _account;   // puuid текущего аккаунта (ставится при refresh)

    public static string GetSelectedQueue()
    {
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            var q = key != null && s.Accounts.TryGetValue(key, out var a)
                ? a.SelectedQueue : "solo";
            return QueueKeys.Contains(q) ? q : "solo";
        }
        finally { Gate.Release(); }
    }

    /// <summary>
    /// Запомнить, с кем стоим в очереди соло/дуо. Зовётся из <see cref="Party"/>
    /// при каждом обновлении лобби — поэтому пишем на диск, только если состав
    /// правда изменился: лобби присылает себя часто.
    /// </summary>
    public static void NoteQueued(IEnumerable<(string Puuid, string Name)> mates)
    {
        var list = mates.Where(m => m.Puuid.Length > 0).ToList();
        if (list.Count == 0) return;
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            if (key is null) return;
            var a = GetAccount(s, key, null);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var changed = false;
            foreach (var (puuid, name) in list)
            {
                if (!a.Queued.TryGetValue(puuid, out var rec))
                {
                    a.Queued[puuid] = rec = new QueuedMate();
                    changed = true;
                    Log.Write($"очередь соло/дуо: впервые встаём с «{(name.Length > 0 ? name : puuid[..8])}»");
                }
                if (name.Length > 0 && rec.Name != name) { rec.Name = name; changed = true; }
                // Отметку времени двигаем не чаще раза в час: иначе каждое
                // обновление лобби — запись файла.
                if (now - rec.Seen > 3600) { rec.Seen = now; changed = true; }
            }
            // Список живёт долго, но не бесконечно: держим последних QueuedKeep.
            const int QueuedKeep = 50;
            if (a.Queued.Count > QueuedKeep)
            {
                foreach (var old in a.Queued.OrderByDescending(x => x.Value.Seen)
                                            .Skip(QueuedKeep).Select(x => x.Key).ToList())
                    a.Queued.Remove(old);
                changed = true;
            }
            if (changed) Save(s);
        }
        finally { Gate.Release(); }
    }

    /// С кем стояли в очереди соло/дуо — от недавних к давним.
    public static IReadOnlyList<(string Puuid, string Name, long Seen)> QueuedMates()
    {
        Gate.Wait();
        try
        {
            var a = CurrentAccount();
            if (a is null) return [];
            return a.Queued.OrderByDescending(x => x.Value.Seen)
                           .Select(x => (x.Key, x.Value.Name, x.Value.Seen)).ToList();
        }
        finally { Gate.Release(); }
    }

    public static void SetSelectedQueue(string queue)
    {
        if (!QueueKeys.Contains(queue)) return;
        Gate.Wait();
        try
        {
            var s = Load();
            var key = _account ?? s.LastAccount;
            if (key is null) return;
            var a = GetAccount(s, key, null);
            a.SelectedQueue = queue;
            a.QueueChosen = true;
            Save(s);
        }
        finally { Gate.Release(); }
    }
}
