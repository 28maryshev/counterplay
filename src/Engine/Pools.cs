using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Пулы чемпионов пользователя: «ТВОЙ ПУЛ ПРОТИВ ВРАГОВ». Пул — несколько
/// чемпионов на каждую роль; движок всегда предлагает ЛУЧШЕГО из активного пула
/// (см. RecommendationEngine.TopFromPool), даже если он не попал в общий топ.
/// Хранится по аккаунту (ключ — puuid), с возможностью импорта пулов другого
/// аккаунта, открытого на этом же ПК. Файл: %APPDATA%\Counterplay\pools.json.
/// </summary>
public enum PoolKind { Normal, Pool, Duo }

/// Пул: championId по каждой роли (top/jungle/mid/adc/support).
public sealed class ChampPool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public Dictionary<string, List<int>> ByRole { get; set; } = new();
    public List<int> ForRole(string role) => ByRole.TryGetValue(role, out var l) ? l : [];
}

/// Дуо-пул: мой набор по ролям + набор друга (подсказать, кого пикнуть другу).
/// На чемпионов дуо-пула движок повышает вес синергии — на них играют в паре.
public sealed class DuoPool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FriendName { get; set; } = "";

    /// <summary>
    /// puuid напарника — узнаётся сам, когда с этим дуо-пулом заходят в драфт
    /// вместе с человеком из пати. Нужен для винрейта связки: имя в пуле пишет
    /// игрок как хочет, а считать надо по тому же ключу, что и игры.
    /// Пусто — пока не заходили вместе; тогда связки показываем по всем, с кем играли.
    /// </summary>
    public string FriendPuuid { get; set; } = "";

    /// <summary>
    /// Ник напарника — ЧЕЛОВЕКА, а не набора чемпионов.
    ///
    /// <see cref="FriendName"/> — подпись плитки, и она складывается из
    /// названий двух половин («supports + top»): по ней видно, из чего пара
    /// собрана. Но там, где речь о человеке («связки с …»), такая склейка
    /// читается бессмыслицей. Сюда кладём настоящий ник — он приезжает в файле
    /// вместе с puuid хозяина.
    /// </summary>
    public string FriendNick { get; set; } = "";
    public Dictionary<string, List<int>> Mine { get; set; } = new();
    public Dictionary<string, List<int>> Friend { get; set; } = new();
    public List<int> MineForRole(string role)   => Mine.TryGetValue(role, out var l) ? l : [];
    public List<int> FriendForRole(string role) => Friend.TryGetValue(role, out var l) ? l : [];

    // Manual = фиксированные связки: я всегда играю Mine, друг — Friend. Можно
    // задать НЕСКОЛЬКО пар. Иначе (авто) подсказка ждёт, пока напарник возьмёт
    // чемпиона из своей половины, и подбирает под него мой пик.
    public bool Manual { get; set; }
    public List<ManualDuoPair> ManualPairs { get; set; } = [];

    /// <summary>
    /// Напарник взял чемпиона ИЗ СВОЕЙ половины?
    ///
    /// Два условия у дуо-пула разные и складываются: ЧЕЛОВЕКА опознаём по пати
    /// и puuid (см. <see cref="Party"/>), а срабатывает связка только от
    /// ЧЕМПИОНА — пара задумана под конкретных, и подставлять её под случайный
    /// пик друга нечестно. Взял мимо половины — идёт обычный подбор, в котором
    /// моя половина и так учтена наигранностью.
    ///
    /// У фиксированных связок половин нет: чемпионы друга живут в парах, и
    /// сторона в паре может быть любой — роли разбираются уже при показе.
    /// </summary>
    public bool FriendHas(int championId) =>
        championId != 0 && (Manual
            ? ManualPairs.Any(p => p.Friend == championId || p.Mine == championId)
            : Friend.Values.Any(l => l.Contains(championId)));
}

/// Одна фиксированная дуо-связка: мой чемпион+роль и чемпион+роль друга.
/// Роли (db-ключ) нужны движку для роль-специфичных данных синергии/базы.
public sealed class ManualDuoPair
{
    public int    Mine       { get; set; }
    public string MineRole   { get; set; } = "";
    public int    Friend     { get; set; }
    public string FriendRole { get; set; } = "";
}

/// Все пулы одного аккаунта.
public sealed class AccountPools
{
    public List<ChampPool> Pools     { get; set; } = [];
    public List<DuoPool>   DuoPools  { get; set; } = [];
    public PoolKind        ActiveKind { get; set; } = PoolKind.Normal;
    public string?         ActiveId   { get; set; }   // id активного (дуо-)пула
    public string?         AccountName { get; set; }  // ник (для выбора при импорте)

    // Избранное — ОТДЕЛЬНО в каждой половине: звезда может гореть и на личном
    // пуле, и на дуо одновременно. Какой из них сейчас в деле, решает кнопка в
    // сайдбаре (ActiveKind выше), а звезда лишь говорит «этот из своей половины».
    //
    // Раньше звезда и переключатель были одним и тем же: выбрал дуо — личный
    // выбор терялся, и вернуться к нему можно было только заново отметив пул.
    public string? FavPoolId { get; set; }
    public string? FavDuoId  { get; set; }

    // Выбор режима ЗАПОМИНАЕТСЯ ПО ОЧЕРЕДИ (solo/flex/normal/aram): дуо-пул из
    // соло-очереди не должен утекать во флекс. ActiveKind/ActiveId выше — это
    // «текущий» выбор для очереди, в которой мы сейчас (см. PoolStore.SetQueue).
    public Dictionary<string, QueueActive> ByQueue { get; set; } = new();
}

/// Запомненный выбор режима для одной очереди.
public sealed class QueueActive
{
    public PoolKind Kind { get; set; } = PoolKind.Normal;
    public string?  Id   { get; set; }
}

public static class PoolStore
{
    /// <summary>
    /// Папка с файлом пулов. Подменяется ТОЛЬКО проверками.
    ///
    /// Проверки работают с настоящим `%APPDATA%` игрока, и это однажды стоило
    /// ему всех пулов: в его файле оказались тестовые аккаунты, а свои —
    /// пустыми. Писать в живые данные человека, чтобы что-то проверить,
    /// нельзя; у базы такой шов (`DataDb.DirOverride`) уже есть.
    /// </summary>
    public static string? DirOverride { get; set; }

    private static string Path_ => System.IO.Path.Combine(
        DirOverride ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay"),
        "pools.json");

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Dictionary<string, AccountPools> _all = new();
    private static string? _account;   // puuid текущего аккаунта
    private static bool _loaded;

    /// <summary>
    /// Файл не прочитался — запись запрещена до конца работы программы.
    ///
    /// Раньше любая ошибка чтения просто давала пустой набор, а первая же
    /// запись затирала файл пустотой — и синхронизация выгружала её на сервер,
    /// добивая копию там. Обрыв записи при падении программы, сбой диска,
    /// недочитанный файл — и пулы, собиравшиеся месяцами, исчезали молча.
    ///
    /// Теперь так: испорченный файл откладываем рядом, работаем с пустыми
    /// пулами в памяти (иначе программу не запустить), но НА ДИСК НЕ ПИШЕМ.
    /// Пусть лучше человек увидит пустой экран и перезапустит, чем потеряет всё.
    /// </summary>
    private static bool _readFailed;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(Path_))
                _all = JsonSerializer.Deserialize<Dictionary<string, AccountPools>>(File.ReadAllText(Path_))
                       ?? new();
        }
        catch (Exception ex)
        {
            _all = new();
            _readFailed = true;
            Log.Write($"пулы не прочитались ({ex.Message}) — записывать не буду, чтобы не затереть");
            try
            {
                var keep = Path_ + ".unreadable";
                if (File.Exists(Path_) && !File.Exists(keep)) File.Copy(Path_, keep);
            }
            catch { /* не вышло отложить — но писать всё равно не станем */ }
        }

        // Переход со старого формата, где звезда и переключатель были одним и
        // тем же. Что было активным — то и становится избранным в своей
        // половине, иначе после обновления звёзды погасли бы на всех пулах.
        foreach (var a in _all.Values)
        {
            if (a.FavPoolId is null && a.ActiveKind == PoolKind.Pool) a.FavPoolId = a.ActiveId;
            if (a.FavDuoId  is null && a.ActiveKind == PoolKind.Duo)  a.FavDuoId  = a.ActiveId;
            // Плюс то, что помнится по очередям: человек мог играть и с личным
            // пулом в соло, и с дуо во флексе — обе звезды должны загореться.
            foreach (var q in a.ByQueue.Values)
            {
                if (a.FavPoolId is null && q.Kind == PoolKind.Pool) a.FavPoolId = q.Id;
                if (a.FavDuoId  is null && q.Kind == PoolKind.Duo)  a.FavDuoId  = q.Id;
            }
        }
        _loaded = true;
    }

    /// <summary>
    /// Сколько раз пулы записывались на диск за жизнь процесса.
    ///
    /// Нужно проверкам: «мы подставили пулы в память и файл игрока не тронули»
    /// раньше сверялось по отметке времени файла, а с тех пор появилась
    /// автосинхронизация — и запущенная рядом программа переписывает тот же
    /// файл сама. Отметка стала говорить о чужой работе, а не о нашей.
    /// Счётчик говорит ровно о своём процессе и гонки не знает.
    /// </summary>
    public static int SaveCount { get; private set; }

    private static void Save()
    {
        // Файл не прочитался — мы не знаем, что в нём было. Писать поверх
        // нельзя: это и есть та самая потеря.
        if (_readFailed) return;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);

            // Пустым поверх непустого — никогда. Пулы собирают месяцами, а
            // обнулить их может что угодно: сбой чтения, чужой процесс,
            // ошибка в коде. Ни один такой случай не стоит файла игрока.
            if (_all.Values.All(a => a.Pools.Count == 0 && a.DuoPools.Count == 0)
                && File.Exists(Path_) && new FileInfo(Path_).Length > MinMeaningful)
            {
                Log.Write("пулы пусты, а на диске не пусто — запись отменена");
                return;
            }

            File.WriteAllText(Path_, JsonSerializer.Serialize(_all, JsonOpts));
            SaveCount++;
        }
        catch { /* не критично */ }
    }

    /// Пустой набор на диске весит сотню байт; всё, что больше, уже чьи-то пулы.
    private const int MinMeaningful = 200;

    private static string Key => _account ?? "_local";

    /// <summary>
    /// Перечитать пулы с диска, забыв то, что в памяти.
    ///
    /// Нужно после синхронизации: файл на диске заменили снаружи, а в памяти
    /// остались прежние пулы — и первое же сохранение затёрло бы принесённое.
    /// </summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _loaded = false;
            _all = new();
            EnsureLoaded();
        }
    }

    /// puuid того, кто сейчас в клиенте. null — клиент ещё не отвечал.
    /// Уезжает в выгружаемый пул, чтобы получатель знал, чей это набор.
    public static string? AccountPuuid => _account;

    /// Текущий аккаунт (puuid + ник) — вызывается при подключении к LCU.
    public static void SetAccount(string? puuid, string? name)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(puuid)) return;
            _account = puuid;
            var a = CurrentLocked();
            if (!string.IsNullOrEmpty(name) && a.AccountName != name) { a.AccountName = name; Save(); }
        }
    }

    private static AccountPools CurrentLocked()
    {
        if (!_all.TryGetValue(Key, out var a)) { a = new(); _all[Key] = a; }
        return a;
    }

    /// Пулы текущего аккаунта (создаются при первом обращении).
    public static AccountPools Current()
    {
        lock (Gate) { EnsureLoaded(); return CurrentLocked(); }
    }

    public static void Persist() { lock (Gate) { EnsureLoaded(); Save(); } }

    private static string _queue = "solo";   // очередь текущего лобби

    /// Смена очереди в лобби: подставляем ЗАПОМНЕННЫЙ для неё режим. Очередь,
    /// в которой пул ещё не выбирали, начинается с Normal — поэтому дуо-пул из
    /// соло не «переезжает» во флекс. Возвращает true, если выбор изменился.
    public static bool SetQueue(string queue)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(queue)) return false;
            _queue = queue;
            var a = CurrentLocked();

            // Переход со старого формата (выбор был один на аккаунт): первую
            // встреченную очередь наследуем текущим выбором, дальше — по очередям.
            if (a.ByQueue.Count == 0 && a.ActiveKind != PoolKind.Normal)
                a.ByQueue[queue] = new QueueActive { Kind = a.ActiveKind, Id = a.ActiveId };

            var sel = a.ByQueue.TryGetValue(queue, out var s) ? s : new QueueActive();

            // Пул мог быть удалён — тогда откатываемся на обычный режим.
            var ok = sel.Kind == PoolKind.Normal || sel.Id is null
                     || (sel.Kind == PoolKind.Pool ? a.Pools.Any(p => p.Id == sel.Id)
                                                   : a.DuoPools.Any(d => d.Id == sel.Id));
            if (!ok) sel = new QueueActive();

            if (a.ActiveKind == sel.Kind && a.ActiveId == sel.Id) return false;
            a.ActiveKind = sel.Kind;
            a.ActiveId   = sel.Id;
            Save();
            return true;
        }
    }

    /// Активный режим: Normal (пул выключен) / Pool / Duo. Запоминается за той
    /// очередью, в которой мы сейчас.
    public static void SetActive(PoolKind kind, string? id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            a.ActiveKind = kind;
            a.ActiveId   = id;
            a.ByQueue[_queue] = new QueueActive { Kind = kind, Id = id };
            // Включили пул кнопкой — он же и становится избранным в своей
            // половине: иначе звезда показывала бы одно, а работало другое.
            if (kind == PoolKind.Pool && id is not null) a.FavPoolId = id;
            if (kind == PoolKind.Duo  && id is not null) a.FavDuoId  = id;
            Save();
        }
    }

    /// <summary>
    /// Отметить избранный пул в СВОЕЙ половине (звезда). Вторую половину не
    /// трогает. Повторный клик по уже избранному — снять отметку.
    ///
    /// Если эта половина сейчас в деле, переключатель идёт следом: иначе на
    /// экране горела бы одна звезда, а подбор шёл по другому пулу.
    /// </summary>
    public static void SetFavourite(PoolKind kind, string? id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            if (kind == PoolKind.Pool) a.FavPoolId = a.FavPoolId == id ? null : id;
            else if (kind == PoolKind.Duo) a.FavDuoId = a.FavDuoId == id ? null : id;
            else return;

            var fav = kind == PoolKind.Pool ? a.FavPoolId : a.FavDuoId;
            if (a.ActiveKind == kind)
            {
                if (fav is null) { a.ActiveKind = PoolKind.Normal; a.ActiveId = null; }
                else a.ActiveId = fav;
                a.ByQueue[_queue] = new QueueActive { Kind = a.ActiveKind, Id = a.ActiveId };
            }
            Save();
        }
    }

    /// Избранный пул своей половины (null — звезда не горит).
    public static string? Favourite(PoolKind kind)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            return kind == PoolKind.Pool ? a.FavPoolId
                 : kind == PoolKind.Duo  ? a.FavDuoId : null;
        }
    }

    /// Активные кандидаты для роли: (мои, набор друга|null, это дуо?).
    /// Normal → пусто (пул не активен).
    public static (List<int> Mine, List<int>? Friend, bool IsDuo) ActiveForRole(string role)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            if (a.ActiveKind == PoolKind.Pool && a.ActiveId is not null)
            {
                var p = a.Pools.FirstOrDefault(x => x.Id == a.ActiveId);
                if (p != null) return (p.ForRole(role), null, false);
            }
            if (a.ActiveKind == PoolKind.Duo && a.ActiveId is not null)
            {
                var d = a.DuoPools.FirstOrDefault(x => x.Id == a.ActiveId);
                if (d != null) return (d.MineForRole(role), d.FriendForRole(role), true);
            }
            return ([], null, false);
        }
    }

    /// Активный дуо-пул целиком (для авто/ручного режима пары), или null.
    public static DuoPool? ActiveDuo()
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            if (a.ActiveKind == PoolKind.Duo && a.ActiveId is not null)
                return a.DuoPools.FirstOrDefault(x => x.Id == a.ActiveId);
            return null;
        }
    }

    /// Другие аккаунты на этом ПК (для импорта): puuid → ник.
    public static IReadOnlyList<(string Puuid, string Name)> OtherAccounts()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _all.Where(kv => kv.Key != Key && kv.Key != "_local")
                       .Select(kv => (kv.Key, kv.Value.AccountName ?? kv.Key[..Math.Min(8, kv.Key.Length)]))
                       .ToList();
        }
    }

    /// Импорт пулов другого аккаунта в текущий (копиями).
    public static void ImportFrom(string puuid)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!_all.TryGetValue(puuid, out var src)) return;
            var cur = CurrentLocked();
            foreach (var p in src.Pools)
                cur.Pools.Add(new ChampPool { Name = p.Name, ByRole = Clone(p.ByRole) });
            foreach (var d in src.DuoPools)
                cur.DuoPools.Add(new DuoPool { FriendName = d.FriendName, Mine = Clone(d.Mine), Friend = Clone(d.Friend),
                                               Manual = d.Manual,
                                               ManualPairs = d.ManualPairs.Select(p => new ManualDuoPair {
                                                   Mine = p.Mine, MineRole = p.MineRole, Friend = p.Friend, FriendRole = p.FriendRole }).ToList() });
            Save();
        }
    }

    private static Dictionary<string, List<int>> Clone(Dictionary<string, List<int>> src) =>
        src.ToDictionary(kv => kv.Key, kv => new List<int>(kv.Value));
}

/// <summary>
/// Как назвать напарника там, где речь о ЧЕЛОВЕКЕ, а не о наборе чемпионов.
///
/// <see cref="DuoPool.FriendName"/> для этого не годится: это подпись плитки,
/// и она нарочно склеена из названий обеих половин («supports + top»). На месте
/// имени такая склейка читается бессмыслицей — «связки с supports + top».
/// </summary>
public static class DuoNaming
{
    /// <summary>
    /// С кем игрок правда играл вдвоём: puuid → ник и число совместных игр,
    /// от частых к редким. Один источник на плитку и на редактор — иначе списки
    /// разъедутся, а человек там один и тот же.
    /// </summary>
    public static List<(string Puuid, string Name, int Games)> Mates()
    {
        // Игры вместе считаем ТОЛЬКО по соло/дуо. Связки копятся на всех
        // союзников подряд, и по всем очередям в списке оказывались все, с кем
        // вообще доводилось играть, — у владельца это 182 человека.
        var games = new Dictionary<string, (string Name, int Games)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SessionTracker.TopPairs(take: 10_000, queues: new[] { "solo" }))
        {
            if (p.AllyPuuid.Length == 0) continue;
            var had = games.GetValueOrDefault(p.AllyPuuid);
            games[p.AllyPuuid] = (p.AllyName.Length > 0 ? p.AllyName : had.Name,
                                  had.Games + p.Games);
        }

        var res  = new List<(string Puuid, string Name, int Games)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Главный источник — лобби соло/дуо: это ровно те, с кем ВСТАВАЛИ В
        // ОЧЕРЕДЬ. Человек попадает сюда, даже если совместных игр ещё нет.
        foreach (var (puuid, name, _) in SessionTracker.QueuedMates())
        {
            var g = games.GetValueOrDefault(puuid);
            res.Add((puuid, name.Length > 0 ? name : g.Name, g.Games));
            seen.Add(puuid);
        }

        // Запас для тех, у кого лобби ещё не записалось: список лобби копится
        // только вперёд, а пул с другом у многих настроен давно. В соло/дуо
        // случайный союзник попадается один раз, напарник — помногу, поэтому
        // двух совместных игр уже достаточно, чтобы это была не случайность.
        foreach (var (puuid, v) in games)
            if (!seen.Contains(puuid) && v.Games >= 2)
                res.Add((puuid, v.Name, v.Games));

        return res.OrderByDescending(x => x.Games).ToList();
    }

    /// <summary>
    /// Ник напарника или пусто, если назвать некого.
    ///
    /// По порядку: ник из файла, которым обменялись (приезжает рядом с puuid);
    /// ник из самих связок, где он лежит рядом с играми; иначе — пусто.
    ///
    /// Пусто возвращается и тогда, когда напарник вообще не опознан: в этом
    /// случае показаны связки СО ВСЕМИ, и называть кого-то одного нельзя.
    /// </summary>
    public static string PartnerNick(DuoPool? duo, IEnumerable<SessionTracker.PairStat> pairs)
    {
        if (duo is null || duo.FriendPuuid.Length == 0) return "";
        if (duo.FriendNick.Length > 0) return duo.FriendNick;
        return pairs.FirstOrDefault(p => p.AllyName.Length > 0)?.AllyName ?? "";
    }

    /// <summary>
    /// Имя для ПЛИТКИ пула. Правило мягче, чем у заголовка.
    ///
    /// Заголовок подписывает список связок, и назвать там человека можно только
    /// когда список по нему и отфильтрован, — иначе подпись обещает одного, а
    /// показаны связки со всеми. На плитке вопрос другой: «с кем этот пул». Ник
    /// из файла отвечает на него сам по себе, даже если вместе ещё не играли и
    /// puuid не подтверждён.
    /// </summary>
    public static string TileNick(DuoPool? duo, IEnumerable<SessionTracker.PairStat> pairs)
    {
        if (duo is null) return "";
        if (duo.FriendNick.Length > 0) return duo.FriendNick;
        return duo.FriendPuuid.Length > 0
            ? pairs.FirstOrDefault(p => p.AllyName.Length > 0)?.AllyName ?? ""
            : "";
    }
}
