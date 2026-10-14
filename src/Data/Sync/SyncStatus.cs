using System.IO;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Чем кончилась последняя синхронизация — для строки под кнопкой
/// «синхронизировать» (<see cref="SyncBadge"/>).
///
/// Синхронизация ходит сама и молча: при запуске, при входе в аккаунт, после
/// каждой игры. Узнать, свежая ли копия на сервере и доехало ли с другого
/// компьютера, можно было только из log.txt. Владелец: «как мне понять, что на
/// сервере свежая инфа по моим данным».
///
/// Лежит в файле <c>sync.last</c>, а не только в памяти: открыл окно после
/// запуска — сразу видно, когда ходили последний раз.
/// </summary>
public static class SyncStatus
{
    /// Unix-секунды; 0 — такого ещё не было.
    public sealed record State
    {
        /// Последний удачный проход: всё своё на сервере, всё чужое здесь.
        public long OkAt { get; init; }
        /// Последняя неудача и её причина — ключ строки, а не готовый текст:
        /// язык окна мог смениться с тех пор.
        public long FailAt { get; init; }
        public string? FailKey { get; init; }
        /// Когда последний раз ПРИШЛО что-то с другого компьютера и что именно.
        /// Отдельно от последнего прохода: после него ходили ещё десять раз
        /// впустую, а знать, что игры с ноутбука доехали, всё равно нужно.
        public long GotAt { get; init; }
        public int GotGames { get; init; }
        public bool GotPools { get; init; }
        public bool GotSettings { get; init; }
        /// В последнем удачном проходе на сервере не было ничего. Так выглядит
        /// первый компьютер с этим паролем — и второй, если пароль на нём
        /// набран иначе: тогда данные с первого сюда не придут никогда.
        public bool WasEmpty { get; init; }

        /// Последняя попытка не удалась. Хранится, а не выводится из времён:
        /// неудача в ту же секунду, что и удачный проход, иначе не считалась бы.
        public bool Failed { get; init; }
    }

    private static string Path_ => Path.Combine(AppPaths.Root, "sync.last");

    private static readonly object Lock = new();
    private static State? _state;
    private static int _running;

    /// Что-то поменялось: проход начался или кончился, сменился пароль. Зовётся
    /// из фонового потока.
    public static event Action? Changed;

    /// Проход идёт прямо сейчас.
    public static bool Running => Volatile.Read(ref _running) > 0;

    public static State Current
    {
        get
        {
            lock (Lock)
            {
                if (_state is not null) return _state;
                try
                {
                    if (File.Exists(Path_))
                        _state = JsonSerializer.Deserialize<State>(File.ReadAllText(Path_));
                }
                catch { /* битый файл — как будто не ходили */ }
                return _state ??= new State();
            }
        }
    }

    internal static void Started()
    {
        Interlocked.Increment(ref _running);
        Changed?.Invoke();
    }

    /// <summary>
    /// Проход кончился. <paramref name="r"/> null — его отменили (программа
    /// закрывается): записывать нечего.
    /// </summary>
    internal static void Finished(SyncClient.Result? r)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (r is not null)
            lock (Lock)
            {
                var s = Current;
                if (r.Ok)
                {
                    s = s with { OkAt = now, Failed = false, WasEmpty = r.ServerEmpty };
                    if (r.Got is { Any: true } got)
                        s = s with
                        {
                            GotAt = now, GotGames = got.Games,
                            GotPools = got.Pools, GotSettings = got.Settings
                        };
                }
                else s = s with { FailAt = now, Failed = true, FailKey = r.ErrorKey ?? "sync.failedNet" };
                _state = s;
                Save(s);
            }
        Interlocked.Decrement(ref _running);
        Changed?.Invoke();
    }

    /// <summary>
    /// Пароль сменили или убрали: прежнее состояние — про другую строку на
    /// сервере, и «синхронизировано вчера» под новым паролем было бы неправдой.
    /// </summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _state = new State();
            try { File.Delete(Path_); } catch { }
        }
        Changed?.Invoke();
    }

    private static void Save(State s)
    {
        try { File.WriteAllText(Path_, JsonSerializer.Serialize(s)); }
        catch { /* не записалось — строка просто покажет прошлое после перезапуска */ }
    }
}
