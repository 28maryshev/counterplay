using System.Globalization;

namespace Counterplay;

/// <summary>
/// Кнопка полосы фраз. Kind — вид: просто фраза или фраза с выбором (роль, что
/// нужно команде). Std — стандартная кнопка (текст — ключ phrases.&lt;Std&gt; на
/// языке фраз), пусто — кнопку завёл человек. Text — свой текст; пусто у
/// стандартной — её текст на языке фраз. Неизменяемая: правка — новая запись.
/// </summary>
public sealed record ChatButton
{
    public string Kind { get; init; } = ChatPhrases.Plain;
    public string Std  { get; init; } = "";
    public string Text { get; init; } = "";
    /// Свои варианты выбора (ChatPhrases.SetOwnChoices); null — стандартные вида.
    public ChoiceItem[]? Choices { get; init; }

    // Для журнала настроек (AppSettings.Apply пишет, что поменялось).
    public override string ToString() =>
        $"{Kind}/{(Std.Length > 0 ? Std : "своя")}/{Text}"
        + (Choices is null ? "" : "/{" + string.Join(", ", Choices.Select(c => c.ToString())) + "}");
}

/// <summary>
/// Вариант в списке кнопки с выбором. Key — стандартный вариант (роль «mid»,
/// нужда «front»; название — ключ phrases.roles.* / phrases.needs.* на языке
/// фраз, у роли — ещё и иконка), пусто — вариант завёл человек. Text — своё
/// название («топчик»); пусто у стандартного — его название на языке фраз.
/// </summary>
public sealed record ChoiceItem
{
    public string Key  { get; init; } = "";
    public string Text { get; init; } = "";

    public override string ToString() => Key.Length == 0 ? Text : Text.Length > 0 ? $"{Key}={Text}" : Key;
}

/// <summary>
/// Фразы одного аккаунта: язык фраз (пусто — язык программы), стандартные или
/// свои (<see cref="Own"/> — галочка в окне фраз) и свои кнопки (null — копия
/// стандартного набора: идёт за языком и за новыми стандартными кнопками).
/// Свои кнопки хранятся и при стандартных фразах: вернулся к своим — они на месте.
/// </summary>
public sealed record PhraseSet
{
    public string Lang { get; init; } = "";
    public bool Own { get; init; }
    public ChatButton[]? Buttons { get; init; }

    public override string ToString() =>
        $"{(Lang.Length > 0 ? Lang : "язык программы")}, {(Own ? "свои" : "стандартные")}: "
        + (Buttons is null ? "как стандартные" : string.Join(" | ", Buttons.Select(b => b.ToString())));
}

/// <summary>
/// Кнопки фраз для чата драфта (полоса в OverlayWindow.Phrases).
///
/// Фразы — у каждого аккаунта свои (слово владельца 25.11: «должно
/// привязываться к аккаунту и синхронизироваться»): словарь
/// <see cref="AppSettings.PhraseSets"/>, ключ — puuid аккаунта в клиенте (тот
/// же, что у пулов), "" — пока аккаунт неизвестен. Лежит в ui.json, а его
/// синхронизация уже переносит между компьютерами. У аккаунта без своего
/// набора — общий (""), без общего — стандартный. У кнопки с выбором в тексте
/// есть место «…» (в поле редактора — блок): по нажатию открывается список
/// (роль или что нужно команде), и выбранное встаёт на это место — «Мид, кого
/// будешь брать?».
///
/// Язык фраз отдельный от языка программы: с русской программой играют на
/// западных серверах, и союзникам нужен английский.
/// </summary>
public static class ChatPhrases
{
    /// Виды кнопок (слово владельца 25.11: «Текст, Роль, Перечень»): просто
    /// фраза, с выбором роли, с выбором из перечня (по умолчанию в нём — что
    /// нужно команде: фронтлайн, пок, вейвклир…).
    public const string Plain = "text", Role = "role", List = "list";

    /// Больше кнопок полоса не вмещает: при восьми на кнопку ~90 px, две строки.
    public const int Max = 8;

    /// Место для выбора в тексте кнопки (в поле редактора — блок, SlotBox).
    public const string Slot = "…";

    // Стандартный набор по порядку. Слово владельца 25.11: кнопки с выбором —
    // первыми; «Можно мне …» и «Нам нужен …» встали на место «Не баньте моего
    // чемпиона» и «Можно я пикну последним?». 26.11: вместо «Удачи, хорошей
    // игры!» — приветствие, и оно первое. «swap» — просьба поменяться
    // очередью пика: сперва «Мид, давай свапнемся?» с выбором роли, с 26.11 —
    // «Можно я пикну …?» с выбором места в очереди (владелец: «wanna swap
    // замени на can I pick и перечисление позиций 1–5 словами»). Ключ прежний:
    // по нему кнопку узнают сохранённые наборы.
    private static readonly (string Std, string Kind)[] Standard =
    [
        ("hello", Plain),
        ("who", Role), ("canI", Role), ("need", List),
        (Swap, List), ("show", Plain),
    ];

    /// Стандартная «Можно я пикну …?» — у её перечня свои варианты: места в очереди пика.
    public const string Swap = "swap";

    /// Стандартная кнопка-приветствие — первая на полосе, даже перед кнопками с выбором.
    public const string Hello = "hello";

    /// Роли для выбора (ключи phrases.roles.*) и позиции клиента к ним — для иконок.
    public static readonly string[] Roles = ["top", "jungle", "mid", "adc", "support"];
    public static readonly string[] RolePositions = ["top", "jungle", "middle", "bottom", "utility"];

    /// Стандартный перечень — что может понадобиться команде (ключи phrases.needs.*).
    public static readonly string[] Needs = ["front", "poke", "waveclear", "cc", "engage", "ap", "ad", "disengage"];

    /// Места в очереди пика — перечень «Можно я пикну …?» (ключи phrases.order.*: «первым»…«пятым»).
    public static readonly string[] Order = ["first", "second", "third", "fourth", "fifth"];

    public static readonly string[] Kinds = [Plain, Role, List];

    public static ChatButton[] Defaults() =>
        Standard.Select(x => new ChatButton { Kind = x.Kind, Std = x.Std }).ToArray();

    // ── Набор аккаунта ────────────────────────────────────────────────────────

    /// Для проверок: чей набор, вместо аккаунта в клиенте.
    public static string? AccountOverride { get; set; }

    /// Чей набор читается и пишется: аккаунт в клиенте, без клиента — "".
    public static string Account => AccountOverride ?? PoolStore.AccountPuuid ?? "";

    /// Набор текущего аккаунта; своего нет — общий (""), нет и его — стандартный.
    public static PhraseSet Set(AppSettings s) =>
        s.PhraseSets is { } d && (d.TryGetValue(Account, out var mine) || d.TryGetValue("", out mine)) && mine is not null
            ? mine : new PhraseSet();

    /// <summary>
    /// Записать набор текущего аккаунта — новым словарём: копия настроек в окне
    /// настроек (AppSettings.Clone) делит его с оригиналом. Стандартный набор у
    /// аккаунта тоже пишется явно, если есть общий: иначе аккаунт после «Вернуть
    /// стандартные» увидел бы общий, а не стандартный.
    /// </summary>
    public static void PutSet(AppSettings s, PhraseSet set)
    {
        var d = s.PhraseSets is null ? new Dictionary<string, PhraseSet>() : new Dictionary<string, PhraseSet>(s.PhraseSets);
        var plain = set.Lang.Length == 0 && !set.Own && set.Buttons is null;
        if (plain && (Account.Length == 0 || !d.ContainsKey(""))) d.Remove(Account);
        else d[Account] = set;
        s.PhraseSets = d.Count == 0 ? null : d;
    }

    /// <summary>
    /// Перенести набор текущего аккаунта из правленой копии в другие настройки
    /// (окно фраз сохраняет в живые: остальное там могли поменять, пока оно
    /// было открыто). Пустые новые кнопки и варианты — прочь.
    /// </summary>
    public static void CopySet(AppSettings from, AppSettings to)
    {
        var set = Set(from);
        PutSet(to, set with { Buttons = Normalize(set.Buttons) });
    }

    /// Язык фраз: выбранный в окне фраз, а пока не выбирали — язык программы.
    public static string Lang(AppSettings s) =>
        Loc.Languages.Any(l => l.Code == Set(s).Lang) ? Set(s).Lang : Loc.Current;

    /// <summary>
    /// Сменить язык фраз аккаунта. Хранится как выбран — даже если совпал с
    /// языком программы: выбранный язык фраз от языка программы больше не
    /// зависит (владелец 26.11: «язык должен запоминаться и стоять в фразах
    /// такой, как выбрал пользователь, даже если язык остальной программы
    /// другой»). Раньше совпавший хранился пустым — и уезжал вслед за
    /// программой, если её язык потом меняли. Пусто — только пока язык фраз
    /// ни разу не выбирали: тогда фразы на языке программы.
    /// </summary>
    public static void SetLang(AppSettings s, string code)
    {
        if (Loc.Languages.All(l => l.Code != code)) return;
        PutSet(s, Set(s) with { Lang = code });
    }

    /// Свои ли фразы у аккаунта (галочка «Свои фразы»), иначе — стандартные.
    public static bool Own(AppSettings s) => Set(s).Own;

    /// <summary>
    /// Галочка «Стандартные фразы» / «Свои фразы». Свои начинаются копией
    /// стандартных (кнопки, ссылающиеся на стандартные); прежние свои кнопки,
    /// если были, возвращаются как были.
    /// </summary>
    public static void SetOwn(AppSettings s, bool own) => PutSet(s, Set(s) with { Own = own });

    /// Кнопки: при своих фразах — свои (без них — копия стандартных), при
    /// стандартных — стандартный набор на языке фраз.
    public static IReadOnlyList<ChatButton> Buttons(AppSettings s) =>
        Set(s) is { Own: true, Buttons: { } own } ? own : Defaults();

    /// <summary>
    /// Кнопки на полосе: приветствие (стандартная «Всем привет!», и
    /// переписанная — тоже), затем кнопки с выбором, затем остальные; внутри —
    /// как в редакторе. Пустые и с выбором без единого варианта — не видны.
    /// Приветствие говорят первым, поэтому оно и стоит первым (владелец 26.11);
    /// кнопки с выбором — сразу за ним (владелец 25.11).
    /// </summary>
    public static IReadOnlyList<ChatButton> Bar(AppSettings s) =>
        Buttons(s).Where(b => Text(s, b).Length > 0 && (!IsChoice(b) || Options(s, b).Count > 0))
                  .OrderBy(b => b.Std == Hello ? 0 : IsChoice(b) ? 1 : 2)   // OrderBy устойчив: порядок внутри сохраняется
                  .ToList();

    public static bool IsChoice(ChatButton b) => b.Kind != Plain;

    /// Стандартный текст кнопки std на языке lang.
    public static string StdText(string lang, string std) => Loc.TIn(lang, $"phrases.{std}");

    /// Текст кнопки (у кнопки с выбором — с местом «…»).
    public static string Text(AppSettings s, ChatButton b) =>
        b.Text.Length > 0 ? b.Text : b.Std.Length > 0 ? StdText(Lang(s), b.Std) : "";

    /// Подпись на полосе: у кнопки с выбором — знак списка.
    public static string Label(AppSettings s, ChatButton b) => IsChoice(b) ? Text(s, b) + " ▾" : Text(s, b);

    // Чей стандартный перечень у кнопки (раздел phrases.* с названиями): у
    // роли — роли; у перечня «Можно я пикну …?» — места в очереди, у прочих
    // перечней — что нужно команде.
    private static string Catalog(string kind, string std) =>
        kind == Role ? "roles" : kind != List ? "" : std == Swap ? "order" : "needs";

    /// Стандартные варианты кнопки вида kind (стандартной std, у своей — пусто), ключи.
    public static IReadOnlyList<string> StdChoices(string kind, string std = "") => Catalog(kind, std) switch
    {
        "roles" => Roles,
        "needs" => Needs,
        "order" => Order,
        _ => [],
    };

    /// Стандартное название варианта на языке фраз — так, как оно встаёт в середину фразы.
    public static string ChoiceName(string lang, string kind, string key, string std = "") =>
        Loc.TIn(lang, $"phrases.{Catalog(kind, std)}.{key}");

    // ── Варианты выбора — у каждой кнопки свои. null — стандартные варианты
    //    вида на языке фраз (идут за языком); список — «свои варианты»: при
    //    переключении туда он начинается копией стандартных (слово владельца
    //    25.11: «выбираются такие же стандартные, которые можно редактировать»).

    /// Больше вариантов список над кнопкой не вмещает без прокрутки.
    public const int MaxChoices = 12;

    /// <summary>
    /// Стандартные варианты кнопки. У роли — пять ролей. У перечня — нужды
    /// команды у стандартной «Нам нужен …», места в очереди пика — у «Можно я
    /// пикну …?»; новый перечень, заведённый
    /// человеком, пуст: его заполняют своими (владелец 26.11: «при добавлении
    /// нового перечня пусть не сохраняются варианты стандартного»).
    /// </summary>
    public static ChoiceItem[] DefaultChoices(ChatButton b) =>
        b.Kind == List && b.Std.Length == 0 ? [] : StdChoices(b.Kind, b.Std).Select(k => new ChoiceItem { Key = k }).ToArray();

    /// Свои ли варианты у кнопки (иначе — стандартные).
    public static bool OwnChoices(ChatButton b) => b.Choices is not null;

    /// Варианты кнопки по порядку редактора.
    public static IReadOnlyList<ChoiceItem> ChoiceItems(ChatButton b) => b.Choices ?? DefaultChoices(b);

    /// Название варианта: своё, а без него — стандартное на языке фраз.
    public static string Name(AppSettings s, ChatButton b, ChoiceItem c) =>
        c.Text.Length > 0 ? c.Text : c.Key.Length > 0 ? ChoiceName(Lang(s), b.Kind, c.Key, b.Std) : "";

    /// Что видно в списке кнопки: вариант и его название; пустые — нет.
    public static IReadOnlyList<(ChoiceItem Item, string Name)> Options(AppSettings s, ChatButton b) =>
        ChoiceItems(b).Select(c => (c, Name(s, b, c))).Where(x => x.Item2.Length > 0).ToList();

    /// <summary>
    /// Стандартные или свои варианты у кнопки i. Свои начинаются копией
    /// стандартных — с названиями на языке фраз, уже как своими: дальше они
    /// не идут за языком, их правят. Ключ остаётся — по нему у роли иконка.
    /// </summary>
    public static void SetOwnChoices(AppSettings s, int i, bool own)
    {
        var list = Buttons(s).ToList();
        if (i < 0 || i >= list.Count || !IsChoice(list[i]) || OwnChoices(list[i]) == own) return;
        var b = list[i];
        list[i] = b with
        {
            Choices = own
                ? DefaultChoices(b).Select(c => c with { Text = ChoiceName(Lang(s), b.Kind, c.Key, b.Std) }).ToArray()
                : null,
        };
        Store(s, list);
    }

    /// Название варианта j у кнопки i (только у своих вариантов).
    public static void SetChoiceText(AppSettings s, int i, int j, string text) =>
        EditChoices(s, i, c => { if (j < c.Count) c[j] = c[j] with { Text = DraftChat.Clean(text) }; });

    /// Новый пустой вариант в конец; полный список — ничего.
    public static void AddChoice(AppSettings s, int i) =>
        EditChoices(s, i, c => { if (c.Count < MaxChoices) c.Add(new ChoiceItem()); });

    public static void RemoveChoice(AppSettings s, int i, int j) =>
        EditChoices(s, i, c => { if (j < c.Count) c.RemoveAt(j); });

    // Свои варианты кнопки — копией, правкой и новым массивом: как и сам набор
    // кнопок, их делит копия настроек в окне настроек.
    private static void EditChoices(AppSettings s, int i, Action<List<ChoiceItem>> edit)
    {
        var list = Buttons(s).ToList();
        if (i < 0 || i >= list.Count || list[i].Choices is not { } own) return;
        var items = own.ToList();
        edit(items);
        list[i] = list[i] with { Choices = [.. items] };
        Store(s, list);
    }

    /// <summary>
    /// Привести свои варианты в порядок: стандартный ключ — только из перечня
    /// кнопки (вид kind, стандартная std), название чистое, не больше <see cref="MaxChoices"/>, пустые без
    /// ключа — прочь (кроме правки). В стандартные сами не превращаются: «свои»
    /// — выбор человека, даже если совпали. Пустой список — пустой: все
    /// варианты убраны, кнопка пропадёт с полосы.
    /// </summary>
    public static ChoiceItem[]? NormalizeChoices(string kind, string stdButton, ChoiceItem[]? src, bool keepEmpty = false)
    {
        if (src is null || kind == Plain) return null;
        var std = StdChoices(kind, stdButton);
        var list = new List<ChoiceItem>();
        foreach (var c in src)
        {
            if (c is null) continue;
            var key = std.Contains(c.Key) ? c.Key : "";
            var text = DraftChat.Clean(c.Text);
            if (key.Length == 0 && text.Length == 0 && !keepEmpty) continue;
            list.Add(new ChoiceItem { Key = key, Text = text });
            if (list.Count == MaxChoices) break;
        }
        return [.. list];
    }

    /// <summary>
    /// Фраза с выбранным: выбор — на место блока «…»; места нет — в конец
    /// через пробел. В начале фразы выбор пишется с заглавной («Мид, кого…»), в
    /// середине — как есть («Можно мне мид?»). Заглавная — по правилам языка
    /// фраз: у турецкого «i» → «İ».
    /// </summary>
    public static string Compose(string lang, string template, string choice)
    {
        var at = template.IndexOf(Slot, StringComparison.Ordinal);
        var len = Slot.Length;
        if (at < 0) return DraftChat.Clean(template.TrimEnd() + " " + choice);
        if (template[..at].Trim().Length == 0 && choice.Length > 0)
        {
            CultureInfo culture;
            try { culture = CultureInfo.GetCultureInfo(lang); } catch (CultureNotFoundException) { culture = CultureInfo.InvariantCulture; }
            choice = char.ToUpper(choice[0], culture) + choice[1..];
        }
        return DraftChat.Clean(template[..at] + choice + template[(at + len)..]);
    }

    // ── Правка из редактора (PhraseEditor). Список каждый раз заменяется
    //    целиком: копия настроек в окне настроек (AppSettings.Clone) делит его
    //    с оригиналом, и правка на месте ушла бы в живые настройки.

    /// <summary>
    /// Текст кнопки i. У стандартной совпал со стандартным — хранится пустым:
    /// так кнопка пойдёт за языком, а не застынет на этом.
    /// </summary>
    public static void SetText(AppSettings s, int i, string text)
    {
        var list = Buttons(s).ToList();
        if (i < 0 || i >= list.Count) return;
        var clean = DraftChat.Clean(text);
        var b = list[i];
        list[i] = b with { Text = b.Std.Length > 0 && clean == StdText(Lang(s), b.Std) ? "" : clean };
        Store(s, list);
    }

    /// <summary>
    /// Вид кнопки i. Стандартная другого вида — уже своя: текст переносится
    /// как есть, связь со стандартной рвётся (иначе смена языка вернула бы ей
    /// стандартный текст под чужой вид).
    /// </summary>
    public static void SetKind(AppSettings s, int i, string kind)
    {
        var list = Buttons(s).ToList();
        if (i < 0 || i >= list.Count || !Kinds.Contains(kind) || list[i].Kind == kind) return;
        var b = list[i];
        list[i] = b.Std.Length > 0 && StdKind(b.Std) == kind
            ? b with { Kind = kind, Choices = null }   // варианты другого вида сюда не годятся
            : new ChatButton { Kind = kind, Text = kind == Plain ? WithoutSlot(Text(s, b)) : WithSlot(Text(s, b)) };
        Store(s, list);
    }

    /// Текст с блоком: нет его — в конец (пустая — один блок, дальше дописывают вокруг).
    public static string WithSlot(string text) =>
        text.Contains(Slot) ? text : (text.TrimEnd() + " " + Slot).TrimStart();

    /// Текст без блока: простой фразе вставлять некуда. Знак, оставшийся от
    /// блока в начале («…, кого будешь брать?»), уходит вместе с ним.
    public static string WithoutSlot(string text) =>
        DraftChat.Clean(text.Replace(Slot, " ").Replace("  ", " ")).TrimStart(',', ';', ':', '，', '、', ' ');

    /// Новая пустая кнопка в конец; полный набор — ничего.
    public static void Add(AppSettings s)
    {
        var list = Buttons(s).ToList();
        if (list.Count >= Max) return;
        list.Add(new ChatButton());
        Store(s, list);
    }

    public static void Remove(AppSettings s, int i)
    {
        var list = Buttons(s).ToList();
        if (i < 0 || i >= list.Count) return;
        list.RemoveAt(i);
        Store(s, list);
    }

    /// Стандартный набор (и дальше — за ним).
    public static void Reset(AppSettings s) => PutSet(s, Set(s) with { Buttons = null });

    // Пустые кнопки в редакторе живут, пока их не дописали: строка с полем на
    // экране и запись в списке должны совпадать по номеру.
    private static void Store(AppSettings s, List<ChatButton> list) =>
        PutSet(s, Set(s) with { Own = true, Buttons = Normalize([.. list], keepEmpty: true) });

    /// <summary>
    /// Привести в порядок все наборы (чтение ui.json): язык — из известных,
    /// кнопки — <see cref="Normalize"/>; пустой словарь — null.
    /// </summary>
    public static Dictionary<string, PhraseSet>? NormalizeSets(Dictionary<string, PhraseSet>? src)
    {
        if (src is null) return null;
        var d = new Dictionary<string, PhraseSet>();
        foreach (var (key, set) in src)
        {
            if (key is null || set is null) continue;
            d[key] = new PhraseSet
            {
                Lang = Loc.Languages.Any(l => l.Code == set.Lang) ? set.Lang : "",
                Own = set.Own,
                Buttons = Normalize(set.Buttons),
            };
        }
        return d.Count == 0 ? null : d;
    }

    private static string StdKind(string std) => Standard.FirstOrDefault(x => x.Std == std).Kind ?? Plain;

    /// <summary>
    /// Привести набор в порядок: вид — из известных, стандартная — только
    /// известная (и своего вида), текст чистый, не больше <see cref="Max"/>.
    /// Пустые свои кнопки выбрасываются (кроме правки в редакторе). Совпал со
    /// стандартным набором — null: дальше набор идёт за стандартным.
    /// </summary>
    public static ChatButton[]? Normalize(ChatButton[]? src, bool keepEmpty = false)
    {
        if (src is null) return null;
        var list = new List<ChatButton>();
        foreach (var b in src)
        {
            if (b is null) continue;
            // «need» — так «перечень» звался в сборках 25.11.
            var kind = b.Kind == "need" ? List : Kinds.Contains(b.Kind) ? b.Kind : Plain;
            var std = Standard.Any(x => x.Std == b.Std) ? b.Std : "";
            var text = DraftChat.Clean(b.Text);
            var choices = b.Choices;
            if (std.Length > 0 && StdKind(std) != kind)
            {
                if (text.Length > 0) std = "";      // свой текст под другим видом — своя кнопка
                else
                {
                    // Текста нет — остаётся стандартной, а её вид сменился (у «swap»
                    // 26.11 роли стали очередью пика): свои варианты прежнего вида
                    // сюда не годятся — снова стандартные.
                    kind = StdKind(std);
                    choices = null;
                }
            }
            if (std.Length == 0 && text.Length == 0 && !keepEmpty) continue;
            list.Add(new ChatButton
            {
                Kind = kind, Std = std, Text = text,
                Choices = NormalizeChoices(kind, std, choices, keepEmpty),
            });
            if (list.Count == Max) break;
        }
        return list.SequenceEqual(Defaults()) ? null : [.. list];
    }
}
