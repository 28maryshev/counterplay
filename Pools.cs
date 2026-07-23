using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Пулы чемпионов пользователя: «ТВОЙ ПУЛ ПРОТИВ ВРАГОВ». Пул — несколько
/// чемпионов на каждую роль; движок всегда предлагает ЛУЧШЕГО из активного пула
/// (см. RecommendationEngine.BestFromPool), даже если он не попал в общий топ.
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
    public Dictionary<string, List<int>> Mine { get; set; } = new();
    public Dictionary<string, List<int>> Friend { get; set; } = new();
    public List<int> MineForRole(string role)   => Mine.TryGetValue(role, out var l) ? l : [];
    public List<int> FriendForRole(string role) => Friend.TryGetValue(role, out var l) ? l : [];

    // Manual = фиксированные связки (без автоподбора): я всегда играю Mine, друг —
    // Friend. Можно задать НЕСКОЛЬКО пар. Иначе пара подбирается движком (BestDuoPairs).
    public bool Manual { get; set; }
    public List<ManualDuoPair> ManualPairs { get; set; } = [];
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
}

public static class PoolStore
{
    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "pools.json");

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Dictionary<string, AccountPools> _all = new();
    private static string? _account;   // puuid текущего аккаунта
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        try
        {
            if (File.Exists(Path_))
                _all = JsonSerializer.Deserialize<Dictionary<string, AccountPools>>(File.ReadAllText(Path_))
                       ?? new();
        }
        catch { _all = new(); }
        _loaded = true;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(_all, JsonOpts));
        }
        catch { /* не критично */ }
    }

    private static string Key => _account ?? "_local";

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

    /// Активный режим: Normal (пул выключен) / Pool / Duo.
    public static void SetActive(PoolKind kind, string? id)
    {
        lock (Gate)
        {
            EnsureLoaded();
            var a = CurrentLocked();
            a.ActiveKind = kind;
            a.ActiveId   = id;
            Save();
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
