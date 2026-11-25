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

    // Для журнала настроек (AppSettings.Apply пишет, что поменялось).
    public override string ToString() => $"{Kind}/{(Std.Length > 0 ? Std : "своя")}/{Text}";
}

/// <summary>
/// Кнопки фраз для чата драфта (полоса в OverlayWindow.Phrases).
///
/// Набор кнопок хранится в настройках (<see cref="AppSettings.ChatButtons"/>);
/// null — стандартный набор, и тогда он идёт за сменой языка и за новыми
/// стандартными кнопками программы. У кнопки с выбором в тексте есть место
/// «…»: по нажатию открывается список (роль или что нужно команде), и выбранное
/// встаёт на это место — «Мид, кого будешь брать?».
///
/// Язык фраз отдельный от языка программы: с русской программой играют на
/// западных серверах, и союзникам нужен английский.
/// </summary>
public static class ChatPhrases
{
    public const string Plain = "text", Role = "role", Need = "need";

    /// Больше кнопок полоса не вмещает: при восьми на кнопку ~90 px, две строки.
    public const int Max = 8;

    /// Место для выбора в тексте кнопки; три точки подряд — тоже оно.
    public const string Slot = "…";

    // Стандартный набор по порядку. Слово владельца 25.11: кнопки с выбором —
    // первыми; «Можно мне …» и «Нам нужен …» встали на место «Не баньте моего
    // чемпиона» и «Можно я пикну последним?».
    private static readonly (string Std, string Kind)[] Standard =
    [
        ("who", Role), ("canI", Role), ("need", Need),
        ("swap", Plain), ("show", Plain), ("glhf", Plain),
    ];

    /// Роли для выбора (ключи phrases.roles.*) и позиции клиента к ним — для иконок.
    public static readonly string[] Roles = ["top", "jungle", "mid", "adc", "support"];
    public static readonly string[] RolePositions = ["top", "jungle", "middle", "bottom", "utility"];

    /// Что может понадобиться команде (ключи phrases.needs.*).
    public static readonly string[] Needs = ["front", "poke", "waveclear", "cc", "engage", "ap", "ad", "disengage"];

    public static readonly string[] Kinds = [Plain, Role, Need];

    public static ChatButton[] Defaults() =>
        Standard.Select(x => new ChatButton { Kind = x.Kind, Std = x.Std }).ToArray();

    /// Язык фраз: выбранный в окне фраз, а пока не выбирали — язык программы.
    public static string Lang(AppSettings s) =>
        Loc.Languages.Any(l => l.Code == s.PhraseLang) ? s.PhraseLang : Loc.Current;

    /// Кнопки по порядку редактора: свои, а без них — стандартный набор.
    public static IReadOnlyList<ChatButton> Buttons(AppSettings s) => s.ChatButtons ?? Defaults();

    /// Кнопки на полосе: с выбором первыми, внутри — как в редакторе; пустые — нет.
    public static IReadOnlyList<ChatButton> Bar(AppSettings s) =>
        Buttons(s).Where(b => Text(s, b).Length > 0)
                  .OrderBy(b => IsChoice(b) ? 0 : 1)   // OrderBy устойчив: порядок внутри сохраняется
                  .ToList();

    public static bool IsChoice(ChatButton b) => b.Kind != Plain;

    /// Стандартный текст кнопки std на языке lang.
    public static string StdText(string lang, string std) => Loc.TIn(lang, $"phrases.{std}");

    /// Текст кнопки (у кнопки с выбором — с местом «…»).
    public static string Text(AppSettings s, ChatButton b) =>
        b.Text.Length > 0 ? b.Text : b.Std.Length > 0 ? StdText(Lang(s), b.Std) : "";

    /// Подпись на полосе: у кнопки с выбором — знак списка.
    public static string Label(AppSettings s, ChatButton b) => IsChoice(b) ? Text(s, b) + " ▾" : Text(s, b);

    /// Варианты выбора вида kind (ключи).
    public static IReadOnlyList<string> Choices(string kind) =>
        kind == Role ? Roles : kind == Need ? Needs : [];

    /// Название варианта на языке фраз — так, как оно встаёт в середину фразы.
    public static string ChoiceName(string lang, string kind, string key) =>
        Loc.TIn(lang, $"phrases.{(kind == Role ? "roles" : "needs")}.{key}");

    /// <summary>
    /// Фраза с выбранным: выбор — на место «…» (или «...»); места нет — в конец
    /// через пробел. В начале фразы выбор пишется с заглавной («Мид, кого…»), в
    /// середине — как есть («Можно мне мид?»). Заглавная — по правилам языка
    /// фраз: у турецкого «i» → «İ».
    /// </summary>
    public static string Compose(string lang, string template, string choice)
    {
        var at = template.IndexOf(Slot, StringComparison.Ordinal);
        var len = Slot.Length;
        if (at < 0) { at = template.IndexOf("...", StringComparison.Ordinal); len = 3; }
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
            ? b with { Kind = kind }
            : new ChatButton { Kind = kind, Text = Text(s, b) };
        Store(s, list);
    }

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
    public static void Reset(AppSettings s) => s.ChatButtons = null;

    // Пустые кнопки в редакторе живут, пока их не дописали: строка с полем на
    // экране и запись в списке должны совпадать по номеру.
    private static void Store(AppSettings s, List<ChatButton> list) =>
        s.ChatButtons = Normalize([.. list], keepEmpty: true);

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
            var kind = Kinds.Contains(b.Kind) ? b.Kind : Plain;
            var std = Standard.Any(x => x.Std == b.Std) ? b.Std : "";
            var text = DraftChat.Clean(b.Text);
            if (std.Length > 0 && StdKind(std) != kind)
            {
                if (text.Length > 0) std = "";      // свой текст под другим видом — своя кнопка
                else kind = StdKind(std);           // текста нет — остаётся стандартной
            }
            if (std.Length == 0 && text.Length == 0 && !keepEmpty) continue;
            list.Add(new ChatButton { Kind = kind, Std = std, Text = text });
            if (list.Count == Max) break;
        }
        return list.SequenceEqual(Defaults()) ? null : [.. list];
    }
}
