using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Обмен пулами через файл: один выгружает свой пул, присылает файл второму,
/// тот загружает его себе. Нужно прежде всего для дуо — чтобы у обоих перед
/// глазами был один и тот же набор, а не два похожих.
///
/// Формат простой и человекочитаемый: JSON с пометкой, что это наш файл, и
/// номером версии. Идентификаторы чемпионов одинаковы у всех, поэтому файл
/// переносится между людьми, аккаунтами и компьютерами как есть.
///
/// Свой Id при загрузке НЕ переносится — пулу выдаётся новый. Иначе повторная
/// загрузка того же файла затирала бы уже имеющийся пул, а не добавляла второй.
/// </summary>
public static class PoolFile
{
    public const string Extension = ".cpool";

    private const string Marker = "counterplay-pool";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // имена друзей кириллицей
    };

    private sealed class Envelope
    {
        // Без значений по умолчанию: пометки должны стоять В ФАЙЛЕ. Иначе любой
        // посторонний JSON («{}») сойдёт за наш файл и загрузится пустым пулом.
        public string? Format { get; set; }
        public int Version { get; set; }
        public string Kind { get; set; } = "pool";      // pool | duo
        public string Name { get; set; } = "";
        public Dictionary<string, List<int>>? ByRole { get; set; }
        public Dictionary<string, List<int>>? Mine { get; set; }
        public Dictionary<string, List<int>>? Friend { get; set; }
        public bool Manual { get; set; }
        public List<ManualDuoPair>? ManualPairs { get; set; }
    }

    public static string Export(ChampPool p) => JsonSerializer.Serialize(new Envelope
    {
        Format = Marker,
        Version = FormatVersion,
        Kind = "pool",
        Name = p.Name,
        ByRole = p.ByRole,
    }, Opts);

    public static string Export(DuoPool d) => JsonSerializer.Serialize(new Envelope
    {
        Format = Marker,
        Version = FormatVersion,
        Kind = "duo",
        Name = d.FriendName,
        Mine = d.Mine,
        Friend = d.Friend,
        Manual = d.Manual,
        ManualPairs = d.ManualPairs,
    }, Opts);

    /// <summary>
    /// Разобрать файл. Возвращает то, что в нём лежит; обе ссылки null — файл
    /// не наш или испорчен, и звать об этом должен вызывающий: молча ничего не
    /// добавить хуже, чем сказать, что файл не подошёл.
    /// </summary>
    public static (ChampPool? Pool, DuoPool? Duo) Parse(string json)
    {
        try
        {
            var e = JsonSerializer.Deserialize<Envelope>(json);
            if (e is null || e.Format != Marker || e.Version < 1 || e.Version > FormatVersion)
                return (null, null);

            if (e.Kind == "duo")
            {
                var duo = new DuoPool
                {
                    FriendName = Clean(e.Name),
                    Mine = Roles(e.Mine),
                    Friend = Roles(e.Friend),
                    Manual = e.Manual,
                    ManualPairs = (e.ManualPairs ?? []).Where(p => p.Mine != 0 || p.Friend != 0).ToList(),
                };
                // Пустой пул грузить не во что: пометка нашей и осталась, а внутри
                // ничего нет — честнее сказать, что файл не подошёл.
                if (duo.Mine.Count == 0 && duo.Friend.Count == 0 && duo.ManualPairs.Count == 0)
                    return (null, null);
                return (null, duo);
            }

            var pool = new ChampPool { Name = Clean(e.Name), ByRole = Roles(e.ByRole) };
            return pool.ByRole.Count == 0 ? (null, null) : (pool, null);
        }
        catch { return (null, null); }
    }

    /// Имя из чужого файла показывается в нашем окне — длину и переносы режем.
    private static string Clean(string? s)
    {
        var t = (s ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return t.Length > 40 ? t[..40] : t;
    }

    /// Роли и чемпионы из чужого файла: чужие ключи ролей и нули отбрасываем,
    /// повторы схлопываем. Файл мог прийти от версии, где ролей было больше.
    private static Dictionary<string, List<int>> Roles(Dictionary<string, List<int>>? src)
    {
        var known = new[] { "top", "jungle", "mid", "adc", "support" };
        var outp = new Dictionary<string, List<int>>();
        if (src is null) return outp;
        foreach (var (role, list) in src)
        {
            if (!known.Contains(role) || list is null) continue;
            var ids = list.Where(id => id > 0).Distinct().Take(60).ToList();
            if (ids.Count > 0) outp[role] = ids;
        }
        return outp;
    }

    /// Имя файла из названия пула: «supports» → «supports.cpool». Пустое или
    /// состоящее из запрещённых символов — заменяем, иначе диалог сохранения
    /// откажется открываться.
    public static string SuggestName(string poolName)
    {
        var bad = Path.GetInvalidFileNameChars();
        var safe = new string((poolName ?? "").Where(c => !bad.Contains(c)).ToArray()).Trim();
        if (safe.Length == 0) safe = "pool";
        return safe + Extension;
    }
}
