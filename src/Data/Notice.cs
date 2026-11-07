using System.Net.Http;
using System.Text.Json;

namespace Counterplay;

/// Сообщения от нас игроку — то, что видно на плашке «BETA» в окне ожидания.
///
/// Живут на сайте, а не в сборке: если что-то случилось (встал сбор, вышел
/// патч, нужна помощь с багом), текст меняется в админке и доезжает до людей
/// сам — без обновления программы. Спрашиваем при запуске и дальше раз в
/// полчаса; последний ответ храним на диске, чтобы плашка не пустовала, пока
/// сеть недоступна.
///
/// Сообщений может быть несколько — тогда окно показывает их по очереди.
/// Неполадки важнее новостей: если есть хоть одна, показываем только их, иначе
/// сообщение о сбое потеряется в очереди с объявлениями.
///
/// Сообщение может быть опросом: вопрос — его текст, варианты и голоса по ним —
/// в Poll и Counts. Голос и память о нём — <see cref="NoticePoll"/>.
public static class Notice
{
    // /v2 — «умею опросы». По старому адресу сайт опросы не отдаёт: старые
    // версии показали бы вопрос, на который нечем ответить.
    private const string Url = "https://counterplays.com/api/notice/v2";

    // Старый адрес — если /v2 на сайте нет (сайт откатили или выложили позже
    // программы): без него у новой версии пропали бы все сообщения разом.
    private const string UrlV1 = "https://counterplays.com/api/notice";

    private static string CachePath => Path.Combine(AppPaths.Root, "notice.json");

    /// Готовое к показу сообщение: номер на сайте, текст на языке интерфейса,
    /// ссылка (если есть) и вид — "info" (обычная плашка) или "alert" (красная,
    /// о неполадке). У опроса ещё варианты ответа и голоса по ним, как их
    /// прислал сайт (свежее счёт может оказаться у NoticePoll — после голоса).
    public sealed record Item(string Id, string Text, string? Link, string Kind, string Head)
    {
        public IReadOnlyList<PollOption>? Poll { get; init; }
        public IReadOnlyDictionary<string, int> Counts { get; init; } =
            new Dictionary<string, int>();
    }

    /// Вариант ответа: номер (не меняется при правке, к нему привязаны голоса)
    /// и текст на языке интерфейса.
    public sealed record PollOption(string Id, string Text);

    /// Что изменилось после запроса к сайту. Голоса меняются постоянно, а
    /// набор сообщений — редко: на новый набор плашка начинает очередь с
    /// начала, а на новые голоса только перерисовывается.
    public enum Change { None, Counts, Content }

    private static JsonDocument? _doc;

    /// Через сколько секунд сменить сообщение, если их несколько.
    public static int RotateSeconds
    {
        get
        {
            var root = _doc?.RootElement;
            if (root is { ValueKind: JsonValueKind.Object }
                && root.Value.TryGetProperty("rotateSec", out var v)
                && v.TryGetInt32(out var sec) && sec >= 5) return sec;
            return 45;
        }
    }

    /// Что показывать сейчас, в порядке очереди. Пусто — обычный текст плашки.
    public static IReadOnlyList<Item> Active()
    {
        var root = _doc?.RootElement;
        if (root is not { ValueKind: JsonValueKind.Object }) return [];

        // Новый ответ — список; старый (одно сообщение) читаем как список из одного.
        var raw = root.Value.TryGetProperty("items", out var items)
                  && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : [root.Value];

        var list = raw.Select(Read).OfType<Item>().ToList();

        // Неполадки вытесняют новости.
        var alerts = list.Where(i => i.Kind == "alert").ToList();
        return alerts.Count > 0 ? alerts : list;
    }

    private static Item? Read(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        // Срок мог истечь, пока программа была открыта, — проверяем при показе.
        if (e.TryGetProperty("until", out var until)
            && until.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(until.GetString(), out var till)
            && till < DateTimeOffset.UtcNow) return null;

        if (!e.TryGetProperty("text", out var texts) || texts.ValueKind != JsonValueKind.Object)
            return null;

        var text = Pick(texts);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var link = e.TryGetProperty("link", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString() : null;
        var kind = e.TryGetProperty("kind", out var k) && k.GetString() == "alert" ? "alert" : "info";
        // Заголовок приходит ключом — слово подставляем на языке интерфейса.
        // Незнакомый ключ переводить нечем, поэтому проверяем по списку: иначе
        // на плашке оказалось бы служебное «notice.head.что-то».
        var head = e.TryGetProperty("head", out var h) ? h.GetString() ?? "beta" : "beta";
        if (head is not ("beta" or "new" or "important" or "tip" or "update" or "question"))
            head = "beta";

        var id = e.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String
            ? idv.GetString() ?? "" : "";
        var item = new Item(id, text!, string.IsNullOrWhiteSpace(link) ? null : link, kind, head);

        // Опрос — только у обычной плашки и только с номером: голос без номера
        // отправить некуда, а неполадку вопрос заслонять не должен.
        if (kind == "info" && id.Length > 0
            && e.TryGetProperty("poll", out var poll) && poll.ValueKind == JsonValueKind.Object)
        {
            var options = ReadOptions(poll);
            if (options.Count >= 2) item = item with { Poll = options, Counts = ReadCounts(poll) };
        }
        return item;
    }

    /// Текст на языке интерфейса. Сообщения переводятся на все языки
    /// программы; нет перевода (старое сообщение или перевод не сделали) —
    /// английский, затем русский. Пустое поле пропускаем: лучше текст на
    /// другом языке, чем пустая плашка.
    private static string? Pick(JsonElement texts)
    {
        var order = new[] { Loc.Current, "en", "ru" }.Distinct();
        return order
            .Select(k => texts.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
    }

    /// Варианты опроса. Вариант без номера или без текста пропускаем — его не
    /// выбрать и не показать.
    private static List<PollOption> ReadOptions(JsonElement poll)
    {
        var options = new List<PollOption>();
        if (!poll.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
            return options;

        foreach (var o in opts.EnumerateArray())
        {
            if (o.ValueKind != JsonValueKind.Object
                || !o.TryGetProperty("id", out var oid) || oid.ValueKind != JsonValueKind.String
                || !o.TryGetProperty("text", out var ot) || ot.ValueKind != JsonValueKind.Object)
                continue;
            var t = Pick(ot);
            if (!string.IsNullOrWhiteSpace(t) && !string.IsNullOrEmpty(oid.GetString()))
                options.Add(new PollOption(oid.GetString()!, t!));
        }
        return options;
    }

    /// Голоса {вариант: число} из ответа сайта — и в выдаче сообщений, и в
    /// ответе на голос.
    internal static Dictionary<string, int> ReadCounts(JsonElement parent)
    {
        var counts = new Dictionary<string, int>();
        if (parent.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object)
            foreach (var p in c.EnumerateObject())
                if (p.Value.TryGetInt32(out var n) && n >= 0) counts[p.Name] = n;
        return counts;
    }

    /// Читает сохранённый ответ — чтобы плашка была заполнена ещё до сети.
    public static void LoadCached()
    {
        try
        {
            if (File.Exists(CachePath)) Parse(File.ReadAllText(CachePath));
        }
        catch (Exception ex) { Log.Write($"сообщение: кэш не прочитался — {ex.Message}"); }
    }

    /// <summary>
    /// Не ходить на сайт. Нужно ПРОВЕРКАМ: они подставляют свои сообщения, а
    /// окно при открытии идёт за настоящими и затирает подставленные.
    /// В работе всегда false.
    /// </summary>
    public static bool Frozen { get; set; }

    /// Спрашивает сайт и говорит, что изменилось: набор сообщений, только
    /// голоса в опросах или ничего.
    public static async Task<Change> RefreshAsync()
    {
        if (Frozen) return Change.None;
        try
        {
            var before = Signature();
            var votesBefore = CountsSignature();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            string json;
            try { json = await http.GetStringAsync(Url); }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                json = await http.GetStringAsync(UrlV1);
            }
            // Пока шёл запрос, проверка могла подставить свои сообщения: ответ
            // сайта затёр бы их посреди шага.
            if (Frozen) return Change.None;
            Parse(json);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, json);
            }
            catch { /* не записалось — не беда, в следующий раз спросим снова */ }

            var after = Signature();
            if (before != after)
            {
                var now = Active();
                Log.Write(now.Count == 0
                    ? "сообщений с сайта нет"
                    : $"сообщений с сайта: {now.Count} ({now[0].Kind}) — {Cut(now[0].Text)}");
            }
            return before != after ? Change.Content
                 : votesBefore != CountsSignature() ? Change.Counts
                 : Change.None;
        }
        catch (Exception ex)
        {
            Log.Write($"сообщение с сайта не получено: {ex.Message}");
            return Change.None;
        }
    }

    // Чем отличается один набор сообщений от другого — чтобы зря не перерисовывать.
    // Голоса сюда не входят: они меняются сами, а очередь из-за них начинать
    // сначала незачем.
    private static string Signature() =>
        string.Join("¦", Active().Select(i =>
            $"{i.Id}|{i.Kind}|{i.Head}|{i.Text}|{i.Link}|"
            + string.Join("/", i.Poll?.Select(o => o.Id + "=" + o.Text) ?? [])));

    private static string CountsSignature() =>
        string.Join("¦", Active().Where(i => i.Poll is not null)
            .Select(i => i.Id + ":" + string.Join(",", i.Counts.OrderBy(c => c.Key)
                .Select(c => $"{c.Key}={c.Value}"))));

    private static void Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        _doc?.Dispose();
        _doc = doc;
    }

    private static string Cut(string s) => s.Length <= 60 ? s : s[..57] + "…";
}
