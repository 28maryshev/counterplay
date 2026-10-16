using System.IO;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// «Не советовать»: чемпионы, которых игрок сам убрал из советов кнопкой 👎 в
/// углу карточки пика или бана.
///
/// Зачем. Подбор не знает, что игрок умеет. Владелец: Блицкранк первым баном
/// против Соны, а он, увидев Блица, возьмёт Браума и выиграет; Тарик в пиках
/// «контрит почти всё», но играть на нём владелец не умеет. Такой совет
/// занимает место, которое мог бы занять полезный.
///
/// Как работает. Первые <see cref="HideGames"/> игр после дизлайка чемпиона
/// нет в советах вовсе. Потом он возвращается постепенно — каждую игру на
/// место выше: сначала на <see cref="ReturnGames"/> мест ниже своего, через
/// <see cref="ReturnGames"/> игр — на своём (владелец: «пусть постепенно
/// поднимается, чтобы если что потом вернулся на место»). Игры — сыгранные
/// с драфтом (соло, флекс, нормалы) по журналу игр. Дизлайк держится по роли:
/// Блицкранк против саппорта ничего не говорит о банах на миде.
///
/// Скрытого можно вернуть сразу: окно «Скрытые» (кнопка в шапке тир-листа,
/// слева баны, справа пики) и полоса «Скрыты» над советами пиков.
/// </summary>
public static class Dislikes
{
    public enum Kind { Pick, Ban }

    /// Сколько игр после дизлайка чемпиона не видно совсем.
    public const int HideGames = 10;

    /// Сколько игр он потом возвращается: каждая — на место выше.
    public const int ReturnGames = 5;

    public sealed class Entry
    {
        public Kind   Kind  { get; set; }
        public int    Champ { get; set; }
        public string Role  { get; set; } = "";   // роль базы: top, jungle, mid, adc, support
        public long   At    { get; set; }         // когда отмечен, unix-секунды
    }

    /// <summary>
    /// Где чемпион сейчас. Played — сколько игр сыграно с дизлайка; пока их
    /// меньше HideGames, он скрыт, потом опущен на Demote мест.
    /// </summary>
    public readonly record struct State(int Champ, int Played)
    {
        public bool Hidden    => Played < HideGames;
        public int  GamesLeft => Math.Max(0, HideGames - Played);
        public int  Demote    => Hidden ? int.MaxValue : HideGames + ReturnGames - Played;
    }

    /// Папка файла вместо папки игрока: песочница пишет в свою.
    public static string? DirOverride { get; set; }

    /// Что-то скрыли или вернули — окно скрытых перерисовывает списки.
    public static event Action? Changed;

    /// Сколько игр сыграно после отметки времени. Подменяют проверки: журнал
    /// копится только настоящими играми.
    public static Func<long, int>? GamesSinceOverride { get; set; }

    private static string FilePath => Path.Combine(DirOverride ?? AppPaths.Root, "dislikes.json");

    private static readonly object Gate = new();
    private static List<Entry>? _list;
    private static string? _loadedFrom;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static int GamesSince(long at) => GamesSinceOverride?.Invoke(at) ?? SessionTracker.GamesSince(at);

    private static bool Expired(Entry e) => GamesSince(e.At) >= HideGames + ReturnGames;

    /// Записи из файла; файл сменился (песочница, проверка) — перечитываем.
    private static List<Entry> All()
    {
        var path = FilePath;
        if (_list is not null && _loadedFrom == path) return _list;
        try
        {
            _list = File.Exists(path)
                ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch { _list = []; }   // битый файл — дизлайки сами истекают, терять нечего
        _loadedFrom = path;
        return _list;
    }

    private static void Save(List<Entry> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Write($"не советовать: файл не записан — {ex.Message}"); }
    }

    /// Не советовать чемпиона на роли. Повторный дизлайк начинает счёт заново.
    public static void Add(Kind kind, int champ, string role)
    {
        if (champ <= 0 || role.Length == 0) return;
        lock (Gate)
        {
            var list = All();
            // Истёкшие записи убираем заодно: читать файл они не мешают, но копиться незачем.
            list.RemoveAll(e => Same(e, kind, champ, role) || Expired(e));
            list.Add(new Entry { Kind = kind, Champ = champ, Role = role,
                                 At = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            Save(list);
        }
        Log.Write($"не советовать: {DataDragon.Name(champ)} ({(kind == Kind.Ban ? "бан" : "пик")}, {role}) — на {HideGames} игр");
        Changed?.Invoke();
    }

    /// Вернуть в советы сразу.
    public static void Remove(Kind kind, int champ, string role)
    {
        lock (Gate)
        {
            var list = All();
            if (list.RemoveAll(e => Same(e, kind, champ, role)) == 0) return;
            Save(list);
        }
        Log.Write($"не советовать: {DataDragon.Name(champ)} ({(kind == Kind.Ban ? "бан" : "пик")}, {role}) — вернул");
        Changed?.Invoke();
    }

    private static bool Same(Entry e, Kind kind, int champ, string role) =>
        e.Kind == kind && e.Champ == champ && e.Role == role;

    /// Действующие дизлайки на роли: скрытые и возвращающиеся.
    public static IReadOnlyList<State> Active(Kind kind, string role)
    {
        if (role.Length == 0) return [];
        lock (Gate)
            return [.. All().Where(e => e.Kind == kind && e.Role == role)
                            .Select(e => new State(e.Champ, GamesSince(e.At)))
                            .Where(s => s.Played < HideGames + ReturnGames)];
    }

    /// Действующие дизлайки вида на всех ролях — для окна скрытых.
    public static IReadOnlyList<(string Role, State State)> ActiveAll(Kind kind)
    {
        lock (Gate)
            return [.. All().Where(e => e.Kind == kind)
                            .Select(e => (e.Role, State: new State(e.Champ, GamesSince(e.At))))
                            .Where(x => x.State.Played < HideGames + ReturnGames)];
    }

    /// <summary>
    /// Список советов по порядку → тот же список с дизлайками: скрытых нет,
    /// возвращающиеся опущены на своё число мест, остальные сохраняют порядок.
    /// keep — кого не трогать: мой собственный пик или наведение, его я вижу
    /// всегда.
    /// </summary>
    public static List<T> Apply<T>(IEnumerable<T> ordered, Func<T, int> id, Kind kind, string role, int keep = 0)
    {
        var src = ordered.ToList();
        var act = Active(kind, role).Where(s => s.Champ != keep).ToDictionary(s => s.Champ);
        if (act.Count == 0) return src;
        // Опущенный на n мест встаёт сразу за тем, кто стоял на n мест ниже него
        // (+0,5); OrderBy устойчивый — остальные не меняются местами.
        return [.. src.Where(x => !(act.TryGetValue(id(x), out var s) && s.Hidden))
                      .Select((x, i) => (x, key: act.TryGetValue(id(x), out var s) ? i + s.Demote + 0.5 : i))
                      .OrderBy(t => t.key)
                      .Select(t => t.x)];
    }
}
