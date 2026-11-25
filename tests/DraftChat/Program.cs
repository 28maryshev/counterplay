using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Фразы в чат драфта (кнопки внизу центра окна драфта).
///
/// 1. Беседа драфта — та, у которой тип championSelect, а не первая в списке:
///    у клиента там же лежат личные чаты и чат лобби, и фраза ушла бы другу.
/// 2. Тело сообщения — как у клиента; перевод строки и длина режутся.
/// 3. Кнопки: стандартный набор (с выбором — первыми), своя и стандартная
///    фраза, смена языка, вид кнопки, добавить и убрать; копия настроек (окно
///    настроек) не правит оригинал через общий массив. Фраза с выбором — на
///    место «…», в начале с заглавной. Варианты: стандартные или свои (копия
///    стандартных), у каждой кнопки свои; переименовать, добавить, убрать.
/// 4. На всех 15 языках есть стандартные фразы и подписи окна и настроек.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        // Настройки и язык — в своей папке, не в папке игрока.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "DraftChat");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Loc.Init();
        Loc.SetLanguage("ru");

        Conversation();
        MessageBody();
        OwnPhrases();
        Compose();
        Choices();
        Translations();
        Window(playerDir, Environment.GetEnvironmentVariable("CP_SNAP"));

        Console.WriteLine(_fails == 0 ? "\nВСЁ В ПОРЯДКЕ" : $"\nНЕ ПРОШЛО: {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what, string got = "")
    {
        Console.WriteLine((ok ? "  ок   " : "  FAIL ") + what + (ok || got.Length == 0 ? "" : $" — {got}"));
        if (!ok) _fails++;
    }

    private static void Conversation()
    {
        Console.WriteLine("беседа драфта:");
        const string list = """
            [
              {"id":"friend@eu1.pvp.net","type":"chat"},
              {"id":"lobby-123@lol-lobby.eu1.pvp.net","type":"customGame"},
              {"id":"a1b2c3@champ-select.eu1.pvp.net","type":"championSelect"}
            ]
            """;
        var id = DraftChat.FindConversation(list);
        Check(id == "a1b2c3@champ-select.eu1.pvp.net", "из трёх бесед взята драфтовая, а не первая", id ?? "null");
        Check(DraftChat.FindConversation("""[{"id":"friend@eu1.pvp.net","type":"chat"}]""") is null,
              "драфта нет — беседы нет (не личный чат)");
        Check(DraftChat.FindConversation("[]") is null, "пустой список — беседы нет");
        Check(DraftChat.FindConversation("""{"errorCode":"RPC_ERROR"}""") is null, "ошибка клиента вместо списка — беседы нет");
        Check(DraftChat.FindConversation("не json") is null, "битый ответ — беседы нет, без исключения");
        Check(DraftChat.FindConversation("""[{"type":"championSelect"},{"id":"","type":"championSelect"}]""") is null,
              "у беседы драфта нет адреса — беседы нет");
    }

    private static void MessageBody()
    {
        Console.WriteLine("тело сообщения:");
        using var doc = JsonDocument.Parse(DraftChat.Message("Давай свапнемся?"));
        var r = doc.RootElement;
        Check(r.GetProperty("body").GetString() == "Давай свапнемся?", "текст фразы в body как есть");
        Check(r.GetProperty("type").GetString() == "chat", "type = chat, как у поля ввода клиента");
        Check(DraftChat.Clean("раз\r\nдва\nтри") == "раз  два три", "перевод строки — пробел (иначе ушло бы двумя сообщениями)",
              DraftChat.Clean("раз\r\nдва\nтри"));
        Check(DraftChat.Clean(new string('я', 400)).Length == DraftChat.MaxLength, $"длина режется до {DraftChat.MaxLength}");
        Check(DraftChat.Clean("   ") == "" && DraftChat.Clean(null) == "", "пробелы и null — пусто");
    }

    private static void OwnPhrases()
    {
        Console.WriteLine("кнопки и свои фразы:");
        var s = AppSettings.Defaults();
        var std = ChatPhrases.Buttons(s);
        Check(s.ChatButtons is null && std.Count == 6, "по умолчанию стандартный набор из шести", $"{std.Count}");
        Check(std.Take(3).All(ChatPhrases.IsChoice) && !std.Skip(3).Any(ChatPhrases.IsChoice),
              "стандартные с выбором — первыми (кого берёшь, можно мне, нам нужен)",
              string.Join(" ", std.Select(b => b.Kind)));
        Check(std[0].Kind == ChatPhrases.Role && std[1].Kind == ChatPhrases.Role && std[2].Kind == ChatPhrases.Need,
              "две кнопки с выбором роли и одна — что нужно команде");

        s.PhraseLang = "en";
        var enSwap = ChatPhrases.StdText("en", "swap");
        Check(ChatPhrases.Text(s, std[3]) == enSwap, "без своей — стандартная на языке фраз", ChatPhrases.Text(s, std[3]));

        // Копия настроек (окно настроек) правит кнопку — оригинал не меняется.
        var copy = s.Clone();
        ChatPhrases.SetText(copy, 4, "mid or feed");
        Check(s.ChatButtons is null && ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[4]) == "mid or feed",
              "правка копии не задела оригинал (массив не общий)",
              $"оригинал «{ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4])}»");
        // Вторая правка копии — по уже своему массиву: оригинал всё так же цел.
        var own = s.Clone();
        ChatPhrases.SetText(own, 4, "раз");
        var keep = own.Clone();
        ChatPhrases.SetText(own, 4, "два");
        Check(ChatPhrases.Text(keep, ChatPhrases.Buttons(keep)[4]) == "раз",
              "правка копии своего набора не задела оригинал", ChatPhrases.Text(keep, ChatPhrases.Buttons(keep)[4]));

        // Вписали ровно стандартную — хранится стандартной и пойдёт за языком.
        ChatPhrases.SetText(copy, 3, "  " + enSwap + " ");
        Check(ChatPhrases.Buttons(copy)[3].Text == "", "стандартная, вписанная руками, хранится как стандартная",
              ChatPhrases.Buttons(copy)[3].Text);
        ChatPhrases.SetText(copy, 4, ChatPhrases.StdText("en", "show"));
        Check(copy.ChatButtons is null, "всё снова как в стандартном наборе — набор опять стандартный (идёт за программой)");

        ChatPhrases.SetText(copy, 4, "mid or feed");
        copy.PhraseLang = "ru";
        var b3 = ChatPhrases.Buttons(copy)[3];
        Check(ChatPhrases.Text(copy, b3) == ChatPhrases.StdText("ru", "swap"), "смена языка: стандартная встала на новом языке",
              ChatPhrases.Text(copy, b3));
        Check(ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[4]) == "mid or feed", "смена языка: своя фраза осталась");

        // Вид кнопки: стандартная другого вида становится своей, текст — как был.
        var who = ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[0]);
        ChatPhrases.SetKind(copy, 0, ChatPhrases.Plain);
        var b0 = ChatPhrases.Buttons(copy)[0];
        Check(b0.Kind == ChatPhrases.Plain && b0.Std == "" && b0.Text == who, "стандартная с выбором → простая: своя, текст тот же",
              b0.ToString());
        ChatPhrases.SetKind(copy, 5, ChatPhrases.Need);
        Check(ChatPhrases.Buttons(copy)[5].Kind == ChatPhrases.Need, "простую можно сделать кнопкой с выбором");
        var bar = ChatPhrases.Bar(copy);
        Check(bar.Take(3).All(ChatPhrases.IsChoice) && bar[2].Kind == ChatPhrases.Need && !bar.Skip(3).Any(ChatPhrases.IsChoice),
              "на полосе кнопки с выбором — первыми, в своём порядке", string.Join(" ", bar.Select(b => b.Kind)));

        // Добавить и убрать; не больше восьми; пустая новая — не на полосе и не сохраняется.
        ChatPhrases.Add(copy);
        Check(ChatPhrases.Buttons(copy).Count == 7 && ChatPhrases.Bar(copy).Count == 6,
              "новая кнопка пустая — в редакторе есть, на полосе нет");
        Check(ChatPhrases.Normalize(copy.ChatButtons)!.Length == 6, "пустая новая не сохраняется");
        ChatPhrases.SetText(copy, 6, "Кто в лес?");
        ChatPhrases.Add(copy); ChatPhrases.Add(copy); ChatPhrases.Add(copy);
        Check(ChatPhrases.Buttons(copy).Count == ChatPhrases.Max, $"кнопок не больше {ChatPhrases.Max}", $"{ChatPhrases.Buttons(copy).Count}");
        ChatPhrases.Remove(copy, 1);
        Check(ChatPhrases.Buttons(copy).Count == ChatPhrases.Max - 1 && ChatPhrases.Buttons(copy).All(b => b.Std != "canI"),
              "убрать — убирает именно эту кнопку");
        ChatPhrases.Reset(copy);
        Check(copy.ChatButtons is null, "«Вернуть стандартные» — стандартный набор");

        // Битое из файла: неизвестный вид, чужая стандартная, больше восьми.
        var junk = Enumerable.Range(0, 12).Select(i => new ChatButton { Kind = "xx", Std = "nope", Text = $"фраза {i}" }).ToArray();
        var fixedUp = ChatPhrases.Normalize(junk)!;
        Check(fixedUp.Length == ChatPhrases.Max && fixedUp.All(b => b.Kind == ChatPhrases.Plain && b.Std == ""),
              "битый набор из файла приведён в порядок");

        // Пустой язык — язык программы.
        copy.PhraseLang = "";
        Check(ChatPhrases.Lang(copy) == Loc.Current, "язык фраз не выбран — язык программы", ChatPhrases.Lang(copy));
        copy.PhraseLang = "xx";
        Check(ChatPhrases.Lang(copy) == Loc.Current, "неизвестный язык — язык программы", ChatPhrases.Lang(copy));
    }

    private static void Choices()
    {
        Console.WriteLine("варианты выбора:");
        var s = AppSettings.Defaults();
        s.PhraseLang = "ru";
        var who = ChatPhrases.Buttons(s)[0];
        var names = ChatPhrases.Options(s, who).Select(o => o.Name).ToList();
        Check(!ChatPhrases.OwnChoices(who) && names.SequenceEqual(["топ", "лес", "мид", "АДК", "саппорт"]),
              "по умолчанию — стандартные роли на языке фраз", string.Join(", ", names));
        s.PhraseLang = "en";
        Check(ChatPhrases.Options(s, who)[0].Name == "top", "стандартные варианты идут за языком фраз",
              ChatPhrases.Options(s, who)[0].Name);
        s.PhraseLang = "ru";

        // Свои — копия стандартных, уже своими словами; оригинал настроек цел.
        var copy = s.Clone();
        ChatPhrases.SetOwnChoices(copy, 0, true);
        var own = ChatPhrases.Buttons(copy)[0];
        Check(ChatPhrases.OwnChoices(own) && own.Choices!.Select(c => c.Text).SequenceEqual(names)
              && own.Choices!.Select(c => c.Key).SequenceEqual(ChatPhrases.Roles),
              "«свои варианты» начинаются копией стандартных, с ключами ролей (для иконок)",
              string.Join(", ", own.Choices!.Select(c => c.ToString())));
        Check(!ChatPhrases.OwnChoices(ChatPhrases.Buttons(s)[0]), "включили свои в копии — оригинал не задет");

        // Топчик — только у этой кнопки, у соседней роли стандартные.
        ChatPhrases.SetChoiceText(copy, 0, 0, "топчик");
        var first = ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[0])[0];
        Check(first.Name == "топчик" && first.Item.Key == "top", "переименовали топ в «топчик» — ключ роли остался",
              first.Item.ToString());
        var sent = ChatPhrases.Compose("ru", ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[0]), first.Name);
        Check(sent == "Топчик, кого будешь брать?", "фраза с ним: «Топчик, кого будешь брать?»", sent);
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[0].Name == "топ",
              "у каждой кнопки свои варианты — у «Можно мне …?» по-прежнему «топ»");
        // Копия настроек со своими вариантами правит их — оригинал цел.
        var before = copy.Clone();
        ChatPhrases.SetChoiceText(copy, 0, 1, "лесник");
        Check(ChatPhrases.Options(before, ChatPhrases.Buttons(before)[0])[1].Name == "лес",
              "правка своих вариантов в копии не задела оригинал",
              ChatPhrases.Options(before, ChatPhrases.Buttons(before)[0])[1].Name);
        ChatPhrases.SetChoiceText(copy, 0, 1, "лес");

        // Смена языка: свои остаются своими, стандартные соседа — на новом языке.
        copy.PhraseLang = "en";
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[0])[1].Name == "лес"
              && ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[0].Name == "top",
              "смена языка: свои варианты как есть, стандартные — на новом языке");
        copy.PhraseLang = "ru";

        // Добавить свой, убрать, предел, пустой не виден.
        ChatPhrases.AddChoice(copy, 0);
        Check(ChatPhrases.ChoiceItems(ChatPhrases.Buttons(copy)[0]).Count == 6
              && ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[0]).Count == 5,
              "новый вариант пустой — в редакторе есть, в списке нет");
        ChatPhrases.SetChoiceText(copy, 0, 5, "филл");
        var last = ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[0])[^1];
        Check(last.Name == "филл" && last.Item.Key == "", "свой вариант «филл» — без ключа", last.Item.ToString());
        ChatPhrases.RemoveChoice(copy, 0, 0);
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[0])[0].Name == "лес", "убрать — убирает именно его");
        for (int n = 0; n < 20; n++) ChatPhrases.AddChoice(copy, 0);
        Check(ChatPhrases.ChoiceItems(ChatPhrases.Buttons(copy)[0]).Count == ChatPhrases.MaxChoices,
              $"вариантов не больше {ChatPhrases.MaxChoices}");
        var saved = ChatPhrases.Normalize(copy.ChatButtons)!;
        Check(saved[0].Choices!.Length == 5, "пустые новые варианты не сохраняются", $"{saved[0].Choices!.Length}");

        // Все варианты убраны — кнопке нечего предложить, на полосе её нет.
        var bare = s.Clone();
        ChatPhrases.SetOwnChoices(bare, 2, true);
        for (int n = 0; n < ChatPhrases.Needs.Length; n++) ChatPhrases.RemoveChoice(bare, 2, 0);
        Check(ChatPhrases.Bar(bare).All(b => b.Std != "need") && ChatPhrases.Buttons(bare)[2].Choices is { Length: 0 },
              "все варианты убраны — кнопки нет на полосе (а не стандартные назад)");

        // Обратно к стандартным; смена вида — стандартные нового вида.
        ChatPhrases.SetOwnChoices(copy, 0, false);
        Check(!ChatPhrases.OwnChoices(ChatPhrases.Buttons(copy)[0]), "«Стандартные варианты» — снова стандартные");
        ChatPhrases.SetOwnChoices(copy, 1, true);
        ChatPhrases.SetKind(copy, 1, ChatPhrases.Need);
        var b1 = ChatPhrases.Buttons(copy)[1];
        Check(!ChatPhrases.OwnChoices(b1) && ChatPhrases.Options(copy, b1)[0].Name == "фронтлайн",
              "сменили вид — варианты нового вида, стандартные");
        Check(ChatPhrases.NormalizeChoices(ChatPhrases.Plain, [new ChoiceItem { Text = "x" }]) is null,
              "у простой фразы вариантов нет");
        var junk = ChatPhrases.NormalizeChoices(ChatPhrases.Need, [new ChoiceItem { Key = "mid", Text = "мид" }, new ChoiceItem()]);
        Check(junk is [{ Key: "", Text: "мид" }], "чужой ключ (роль у «что нужно») отброшен, пустой — прочь",
              string.Join(", ", junk?.Select(c => c.ToString()) ?? []));
    }

    private static void Compose()
    {
        Console.WriteLine("фраза с выбором:");
        string C(string lang, string std, string kind, string key) =>
            ChatPhrases.Compose(lang, ChatPhrases.StdText(lang, std), ChatPhrases.ChoiceName(lang, kind, key));
        var r = C("ru", "who", ChatPhrases.Role, "mid");
        Check(r == "Мид, кого будешь брать?", "в начале фразы — с заглавной", r);
        r = C("ru", "canI", ChatPhrases.Role, "jungle");
        Check(r == "Можно мне лес?", "в середине — как есть", r);
        r = C("ru", "need", ChatPhrases.Need, "front");
        Check(r == "Нам нужен фронтлайн", "нам нужен фронтлайн", r);
        r = C("en", "who", ChatPhrases.Role, "adc");
        Check(r == "ADC, who are you picking?", "английский: ADC, who are you picking?", r);
        r = ChatPhrases.Compose("tr", "… lazım", "istek");
        Check(r == "İstek lazım", "заглавная по правилам языка (турецкое i → İ)", r);
        r = ChatPhrases.Compose("ru", "Кто на ...?", "мид");
        Check(r == "Кто на мид?", "три точки — тоже место для выбора", r);
        r = ChatPhrases.Compose("ru", "Кто идёт", "мид");
        Check(r == "Кто идёт мид", "места нет — выбор в конец через пробел", r);
        var bad = Loc.Languages.SelectMany(l => new[] { ("who", ChatPhrases.Role), ("canI", ChatPhrases.Role), ("need", ChatPhrases.Need) }
                .SelectMany(x => ChatPhrases.StdChoices(x.Item2).Select(k => (l.Code, Text: C(l.Code, x.Item1, x.Item2, k)))))
            .Where(x => x.Text.Contains(ChatPhrases.Slot) || x.Text.Contains("phrases.")).ToList();
        Check(bad.Count == 0, "на всех языках выбор встаёт на место, ключей в тексте нет",
              string.Join(" | ", bad.Take(5).Select(x => $"{x.Code}: {x.Text}")));
    }

    private static void Translations()
    {
        Console.WriteLine("переводы:");
        var repo = FindRepo();
        var dir = Path.Combine(repo, "assets", "i18n");
        var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "en.json"))).RootElement;
        // Все строки раздела phrases, со вложенными (роли, что нужно), и три строки настроек.
        static IEnumerable<string> Keys(JsonElement e, string prefix) => e.EnumerateObject().SelectMany(p =>
            p.Value.ValueKind == JsonValueKind.Object ? Keys(p.Value, prefix + p.Name + ".") : [prefix + p.Name]);
        var keys = Keys(en.GetProperty("phrases"), "phrases.")
            .Concat(["settings.groupPhrases", "settings.draftPhrases", "settings.draftPhrasesHint"]).ToList();
        var missing = new List<string>();
        foreach (var l in Loc.Languages)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{l.Code}.json"))).RootElement;
            foreach (var k in keys)
            {
                var cur = doc;
                var ok = true;
                foreach (var part in k.Split('.'))
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(part, out cur)) { ok = false; break; }
                if (!ok || cur.ValueKind != JsonValueKind.String || cur.GetString() is not { Length: > 0 })
                    missing.Add($"{l.Code}:{k}");
            }
            // Стандартные фразы переведены, а не скопированы с английского.
            if (l.Code != "en")
                foreach (var std in new[] { "who", "canI", "need", "swap", "show", "glhf" })
                    if (ChatPhrases.StdText(l.Code, std) == ChatPhrases.StdText("en", std))
                        missing.Add($"{l.Code}:{std} по-английски");
            // У шаблонов с выбором есть место для него.
            foreach (var std in new[] { "who", "canI", "need" })
                if (!ChatPhrases.StdText(l.Code, std).Contains(ChatPhrases.Slot))
                    missing.Add($"{l.Code}:{std} без «…»");
        }
        Check(missing.Count == 0, $"{keys.Count} строк на {Loc.Languages.Count} языках",
              string.Join(", ", missing.Take(10)));
    }

    /// <summary>
    /// Полоса в окне драфта: видна в свой ход пика, кнопка «Выбрать» стоит
    /// НАД ней (раньше обе плавали внизу верхней строки центра — легли бы одна
    /// на другую), последнюю карточку можно докрутить над полосой; выключена в
    /// настройках или нет связи с клиентом — полосы нет. С CP_SNAP — снимок.
    /// </summary>
    private static void Window(string playerDir, string? snap)
    {
        Console.WriteLine("окно драфта:");
        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("  базы нет — окно проверять нечем"); return; }
        Assets(playerDir);
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var s = AppSettings.Current;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;
        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        var sent = new List<string>();
        w.HoverHandler = _ => Task.FromResult(200);
        w.LockHandler = _ => Task.FromResult(200);
        w.ChatHandler = t => { sent.Add(t); return Task.FromResult(200); };
        var d = Draft();
        w.UpdateRecommendations(engine.Recommend(d, s.DraftCount), d, engine);
        Pump();

        var bar = (FrameworkElement)w.FindName("PhraseBar");
        var pick = (FrameworkElement)w.FindName("PickBar");
        Check(bar.Visibility == Visibility.Visible, "в свой ход пика полоса фраз видна");
        Check(pick.Visibility == Visibility.Visible, "кнопка «Выбрать» тоже видна");
        var barTop = bar.TranslatePoint(new System.Windows.Point(0, 0), w).Y;
        var pickBottom = pick.TranslatePoint(new System.Windows.Point(0, pick.ActualHeight), w).Y;
        Check(pickBottom <= barTop + 0.5, "«Выбрать» стоит над полосой, а не на ней",
              $"низ кнопки {pickBottom:0}, верх полосы {barTop:0}");
        var list = (ItemsControl)w.FindName("PhraseList");
        Check(list.Items.Count == 6, "шесть кнопок фраз", $"{list.Items.Count}");
        Check(((FrameworkElement)w.FindName("RecTail")).ActualHeight > 0, "в конце списка пиков есть место под полосу");
        var labels = list.Items.OfType<PhraseChip>().Select(c => c.Text).ToList();
        Check(labels.Count == 6 && labels[0] == "…, кого будешь брать? ▾" && labels[2] == "Нам нужен … ▾"
              && labels[3] == "Давай свапнемся?",
              "на кнопках стандартные фразы языка программы, у кнопок с выбором — ▾", string.Join(" | ", labels));
        if (snap is not null) Snap(w, snap, "draft-phrases.png");

        // Кнопка с выбором: список ролей (не открываем — окно было бы на экране),
        // пункт «мид» — фраза с выбранным.
        var menu = w.PhraseMenu(0);
        var items = menu.Items.OfType<MenuItem>().ToList();
        // Иконка — только если картинки ролей грузятся: ресурсы лежат в сборке
        // программы, а pack://application ищет их у запущенного exe — у
        // проверки их нет, у программы есть.
        var iconsLoad = RoleIcons.Get("middle") is not null;
        Check(items.Count == 5 && items.All(i => i.Header is StackPanel { Children.Count: var n } && n == (iconsLoad ? 2 : 1)),
              $"у «кого будешь брать» пять ролей{(iconsLoad ? " с иконками" : "")}", $"{items.Count}");
        items.First(i => (string)i.Tag == "mid").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(sent is ["Мид, кого будешь брать?"], "выбрал «мид» — в чат «Мид, кого будешь брать?»", string.Join(" | ", sent));
        var flashed = list.Items.OfType<PhraseChip>().FirstOrDefault();
        Check(flashed is { State: "sent" } && flashed.Text == "✓ Мид, кого будешь брать?",
              "кнопка вспыхнула отправленной фразой", flashed?.Text ?? "нет");
        var needs = w.PhraseMenu(2).Items.Count;
        Check(needs == ChatPhrases.Needs.Length, $"у «нам нужен» {ChatPhrases.Needs.Length} вариантов", $"{needs}");
        // Второй выбор сразу — не уходит (не чаще раза в 1,5 с).
        ((MenuItem)w.PhraseMenu(1).Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(sent.Count == 1, "вторая фраза сразу за первой не уходит", $"{sent.Count}");

        // Своя кнопка в окне фраз → на полосе после «Сохранить»; простая
        // кнопка, ставшая кнопкой с выбором, встаёт к кнопкам с выбором.
        var edit = w.OpenPhrases();
        ShowHidden(edit);
        Pump();
        var fields = edit.Fields();
        Check(fields.Count == 6, "в окне фраз шесть полей", $"{fields.Count}");
        if (snap is not null) Snap(edit, snap, "phrases-window.png");
        fields[3].Text = "Не баньте Сону, я на ней";
        var kinds = Find<System.Windows.Controls.ComboBox>(edit).Where(c => c.Items.Count == ChatPhrases.Kinds.Length).ToList();
        Check(kinds.Count == 6, "у каждой кнопки выбор вида", $"{kinds.Count}");
        kinds[5].SelectedIndex = Array.IndexOf(ChatPhrases.Kinds, ChatPhrases.Role);   // «Удачи…» → с ролью
        Pump();
        edit.Save();
        Pump();
        labels = list.Items.OfType<PhraseChip>().Select(c => c.Text).ToList();
        Check(labels.Contains("Не баньте Сону, я на ней"), "своя фраза — на кнопке после «Сохранить»", string.Join(" | ", labels));
        Check(labels.Count == 6 && labels[3] == "Удачи, хорошей игры! ▾",
              "ставшая кнопкой с выбором — четвёртой, после стандартных с выбором", string.Join(" | ", labels));

        // Свои варианты в окне: «Свои варианты» у первой кнопки — копия ролей,
        // «топ» → «топчик», «Сохранить» — в списке над кнопкой «топчик».
        var edit2 = w.OpenPhrases();
        ShowHidden(edit2);
        Pump();
        var mode = PhrasesWindow.Find<System.Windows.Controls.ComboBox>(edit2).First(c => (string?)c.Tag == "choiceMode");
        mode.SelectedIndex = 1;   // «Свои варианты»
        Pump();
        var choiceFields = edit2.ChoiceFields();
        Check(choiceFields.Count == 5 && choiceFields[0].Text == "топ", "«Свои варианты» — раскрыты, пять ролей, первая «топ»",
              string.Join(", ", choiceFields.Select(f => f.Text)));
        if (snap is not null) Snap(edit2, snap, "phrases-own-choices.png");
        choiceFields[0].Text = "топчик";
        edit2.Save();
        Pump();
        var own = w.PhraseMenu(0).Items.OfType<MenuItem>().ToList();
        Check(own.Count == 5 && (string?)own[0].ToolTip == "Топчик, кого будешь брать?",
              "в списке над кнопкой — «Топчик, кого будешь брать?»", own.FirstOrDefault()?.ToolTip?.ToString() ?? "нет");
        var neighbour = w.PhraseMenu(1).Items.OfType<MenuItem>().FirstOrDefault();
        Check((string?)neighbour?.ToolTip == "Можно мне топ?", "у соседней кнопки «Можно мне топ?» — свои варианты только у первой",
              neighbour?.ToolTip?.ToString() ?? "нет");

        // Выключили в настройках — полосы нет, места под неё тоже.
        var off = AppSettings.Current.Clone();
        off.DraftPhrases = false;
        AppSettings.Apply(off);
        Pump();
        Check(bar.Visibility != Visibility.Visible && ((FrameworkElement)w.FindName("RecTail")).ActualHeight == 0,
              "выключено в настройках — полосы и места под неё нет");
        var on = AppSettings.Current.Clone();
        on.DraftPhrases = true;
        AppSettings.Apply(on);

        // Нет связи с клиентом (обработчика нет) — писать некуда, полосы нет.
        w.ChatHandler = null;
        w.UpdateRecommendations(engine.Recommend(d, s.DraftCount), d, engine);
        Pump();
        Check(bar.Visibility != Visibility.Visible, "без связи с клиентом полосы нет");

        w.Close();
        app.Shutdown();
    }

    // Элементы окна по логическому дереву (окно собрано кодом).
    private static IEnumerable<T> Find<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var deeper in Find<T>(child)) yield return deeper;
        }
    }

    private static DraftState Draft()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 126, 0, "middle", false), new(1, 17, 0, "jungle", false), new(2, 222, 0, "bottom", false),
            new(3, 0, 201, "utility", true), new(4, 106, 0, "top", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, 238, 0, "", false), new(6, 157, 0, "", false), new(7, 893, 0, "", false),
            new(8, 203, 0, "", false), new(9, 0, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[3], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: true,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: true, ActiveCells: [3],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false,
            MyBanDone: true, InPlanning: false);
    }

    /// Имена и иконки чемпионов: кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Окно показываем так, чтобы человек его не увидел.
    private static void ShowHidden(System.Windows.Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    /// Снимок содержимого окна — посмотреть глазами; окно невидимо, дерево рисуется.
    private static void Snap(System.Windows.Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    private static string FindRepo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) return d.FullName;
        throw new InvalidOperationException("не нашёл Counterplay.csproj");
    }
}
