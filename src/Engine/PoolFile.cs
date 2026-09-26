using System.IO;
using System.IO.Compression;
using System.Text;
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
///
/// Тот же пул умеет ездить СТРОКОЙ (<see cref="ToCode"/>): файл удобно передать
/// не везде, а строку можно просто кинуть в мессенджер. Строка — это тот же JSON,
/// сжатый и переведённый в base64, с пометкой в начале.
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

        /// <summary>
        /// Кто отдал этот пул: puuid и ник на момент выгрузки.
        ///
        /// Ради этого поля файл и возит с собой хозяина: загрузив личный пул
        /// друга в свою половину «Дуо», мы сразу знаем, ЧЕЙ это набор, и
        /// связку потом считаем по нему, а не по совпадению имени. В обычной
        /// игре впятером среди союзников находится именно он.
        ///
        /// Пусто — файл из старой версии или выгружен без клиента: тогда всё
        /// работает как раньше, по пати.
        /// </summary>
        public string? OwnerPuuid { get; set; }
        public string? OwnerName { get; set; }

        /// Для дуо-файла — хозяин ВТОРОЙ половины (тот, с кем эта связка).
        public string? FriendPuuid { get; set; }
    }

    public static string Export(ChampPool p) => JsonSerializer.Serialize(new Envelope
    {
        Format = Marker,
        Version = FormatVersion,
        Kind = "pool",
        Name = p.Name,
        ByRole = p.ByRole,
        OwnerPuuid = PoolStore.AccountPuuid,
        OwnerName  = PoolStore.Current().AccountName,
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
        OwnerPuuid  = PoolStore.AccountPuuid,
        OwnerName   = PoolStore.Current().AccountName,
        FriendPuuid = d.FriendPuuid,
    }, Opts);

    /// <summary>
    /// Разобрать файл. Возвращает то, что в нём лежит; обе ссылки null — файл
    /// не наш или испорчен, и звать об этом должен вызывающий: молча ничего не
    /// добавить хуже, чем сказать, что файл не подошёл.
    /// </summary>
    public static (ChampPool? Pool, DuoPool? Duo) Parse(string json)
    {
        var (p, d, _) = ParseWithOwner(json);
        return (p, d);
    }

    /// <summary>
    /// То же, но ещё и puuid хозяина файла.
    ///
    /// Нужен при загрузке ЛИЧНОГО пула друга в половину «Дуо»: сам пул о паре
    /// ничего не знает, а хозяином второй половины становится как раз тот, кто
    /// его отдал. Пусто — файл из старой версии.
    /// </summary>
    public static (ChampPool? Pool, DuoPool? Duo, string OwnerPuuid) ParseWithOwner(string json)
    {
        try
        {
            var e = JsonSerializer.Deserialize<Envelope>(json);
            if (e is null || e.Format != Marker || e.Version < 1 || e.Version > FormatVersion)
                return (null, null, "");

            if (e.Kind == "duo")
            {
                var duo = new DuoPool
                {
                    FriendName = Clean(e.Name),
                    Mine = Roles(e.Mine),
                    Friend = Roles(e.Friend),
                    Manual = e.Manual,
                    ManualPairs = (e.ManualPairs ?? []).Where(p => p.Mine != 0 || p.Friend != 0).ToList(),
                    // Кто на второй половине.
                    //
                    // В файле записана пара «хозяин + его напарник». Если этот
                    // напарник — Я, то для меня второй половиной становится сам
                    // хозяин: связка та же, стороны зеркальны. Иначе берём то,
                    // что записано, а без него — хозяина файла.
                    FriendPuuid = OtherHalf(e),
                };
                // Пустой пул грузить не во что: пометка нашей и осталась, а внутри
                // ничего нет — честнее сказать, что файл не подошёл.
                if (duo.Mine.Count == 0 && duo.Friend.Count == 0 && duo.ManualPairs.Count == 0)
                    return (null, null, "");
                return (null, duo, CleanPuuid(e.OwnerPuuid));
            }

            var pool = new ChampPool { Name = Clean(e.Name), ByRole = Roles(e.ByRole) };
            return pool.ByRole.Count == 0
                ? (null, null, "")
                : (pool, null, CleanPuuid(e.OwnerPuuid));
        }
        catch { return (null, null, ""); }
    }

    /// Имя из чужого файла показывается в нашем окне — длину и переносы режем.
    /// <summary>
    /// Кто для НАС хозяин второй половины дуо-файла.
    ///
    /// Записан напарник, и это я сам → второй половиной становится хозяин файла
    /// (он прислал мне нашу же связку со своей стороны). Иначе — записанный
    /// напарник, а если его нет, хозяин файла.
    /// </summary>
    private static string OtherHalf(Envelope e)
    {
        var owner  = CleanPuuid(e.OwnerPuuid);
        var friend = CleanPuuid(e.FriendPuuid);
        var me = PoolStore.AccountPuuid ?? "";
        if (friend.Length > 0 && friend.Equals(me, StringComparison.OrdinalIgnoreCase))
            return owner;
        return friend.Length > 0 ? friend : owner;
    }

    /// <summary>
    /// puuid из чужого файла. Проверяем форму, а не режем длину: puuid у Riot —
    /// 78 знаков из букв, цифр, дефиса и подчёркивания, и обычная обрезка имён
    /// (40 знаков) превратила бы его в мусор, по которому никто не нашёлся бы.
    /// Всё, что на puuid не похоже, отбрасываем целиком.
    /// </summary>
    private static string CleanPuuid(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.Length is < 20 or > 128) return "";
        foreach (var c in t)
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return "";
        return t;
    }

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

    // ── Код передачи: тот же пул одной строкой ──────────────────────────────

    /// Пометка в начале кода: по ней отличаем свою строку от случайного текста
    /// и оставляем себе путь к следующей версии формата.
    private const string CodePrefix = "CP1-";

    /// <summary>
    /// Строка для передачи в мессенджере. JSON сжимаем: в нём много повторов
    /// («top», «jungle», скобки), и без сжатия код выходил втрое длиннее — такой
    /// в сообщение уже не хочется класть.
    /// </summary>
    public static string ToCode(string json)
    {
        var raw = Encoding.UTF8.GetBytes(json);
        using var packed = new MemoryStream();
        using (var gz = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return CodePrefix + Convert.ToBase64String(packed.ToArray());
    }

    /// <summary>
    /// Обратно в JSON. null — строка не наша или испорчена по дороге.
    ///
    /// Пробелы и переводы строк выкидываем: мессенджеры ломают длинные строки
    /// переносами, а человек копирует «с запасом», захватывая соседние пробелы.
    /// </summary>
    public static string? FromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var clean = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());

        // Код могли прислать с приписками вокруг — берём от пометки и до конца.
        var at = clean.IndexOf(CodePrefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        clean = clean[(at + CodePrefix.Length)..];

        try
        {
            var packed = Convert.FromBase64String(clean);
            using var input = new MemoryStream(packed);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var outp = new MemoryStream();
            gz.CopyTo(outp);
            // Мусор, распаковавшийся во что попало, отсеет уже Parse.
            return Encoding.UTF8.GetString(outp.ToArray());
        }
        catch { return null; }
    }

    /// Похоже ли это вообще на код пула (для подсветки поля ввода).
    public static bool LooksLikeCode(string? text) =>
        text is not null && text.Contains(CodePrefix, StringComparison.OrdinalIgnoreCase);

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
