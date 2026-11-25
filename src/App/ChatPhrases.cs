namespace Counterplay;

/// <summary>
/// Фразы для чата драфта: пять кнопок в окне драфта (OverlayWindow.Phrases).
///
/// У каждой кнопки стандартная фраза на выбранном языке
/// (<c>assets/i18n/*.json</c>, ключи <c>phrases.p1…p5</c>) или своя, если её
/// поменяли. Хранится только своя (<see cref="AppSettings.Phrases"/>): пустая
/// строка — «стандартная», и тогда фраза сама идёт за сменой языка фраз.
///
/// Язык фраз отдельный от языка программы: с русской программой играют на
/// западных серверах, и союзникам нужен английский.
/// </summary>
public static class ChatPhrases
{
    public const int Count = 5;

    /// Язык фраз: выбранный в окне фраз, а пока не выбирали — язык программы.
    public static string Lang(AppSettings s) =>
        Loc.Languages.Any(l => l.Code == s.PhraseLang) ? s.PhraseLang : Loc.Current;

    /// Стандартная фраза кнопки i (0…4) на языке lang.
    public static string Default(string lang, int i) => Loc.TIn(lang, $"phrases.p{i + 1}");

    /// Своя фраза кнопки i; пусто — стандартная.
    public static string Custom(AppSettings s, int i) =>
        i < s.Phrases.Length ? s.Phrases[i] ?? "" : "";

    /// Что отправит кнопка i: своя фраза, а без неё — стандартная на языке фраз.
    public static string Text(AppSettings s, int i) =>
        Custom(s, i) is { Length: > 0 } own ? own : Default(Lang(s), i);

    /// <summary>
    /// Записать текст кнопки i. Совпал со стандартной на языке фраз — хранится
    /// как пустая строка: так фраза пойдёт за языком, а не застынет на этом.
    /// Массив заменяется целиком, а не правится на месте: копия настроек в окне
    /// настроек (<see cref="AppSettings.Clone"/>) делит его с оригиналом.
    /// </summary>
    public static void Set(AppSettings s, int i, string text)
    {
        var clean = DraftChat.Clean(text);
        var arr = Normalize(s.Phrases);
        arr[i] = clean == Default(Lang(s), i) ? "" : clean;
        s.Phrases = arr;
    }

    /// Ровно пять строк, без null, каждая одной строкой и не длиннее предела.
    public static string[] Normalize(string?[]? src)
    {
        var arr = new string[Count];
        for (int i = 0; i < Count; i++)
            arr[i] = DraftChat.Clean(src is not null && i < src.Length ? src[i] : "");
        return arr;
    }
}
