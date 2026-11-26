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
        Accounts();
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
        Check(ChatPhrases.Set(s).Buttons is null && std.Count == 6, "по умолчанию стандартный набор из шести", $"{std.Count}");
        Check(std[0].Std == ChatPhrases.Hello && !ChatPhrases.IsChoice(std[0]) && std.Skip(1).Take(4).All(ChatPhrases.IsChoice) && !std.Skip(5).Any(ChatPhrases.IsChoice),
              "первое — приветствие, за ним стандартные с выбором (кого берёшь, можно мне, нам нужен, можно я пикну)",
              string.Join(" ", std.Select(b => b.Kind)));
        Check(std[1].Kind == ChatPhrases.Role && std[2].Kind == ChatPhrases.Role && std[3].Kind == ChatPhrases.List && std[4].Kind == ChatPhrases.List,
              "две кнопки с выбором роли, два перечня (что нужно команде, место в очереди пика)");

        ChatPhrases.SetLang(s, "en");
        var enSwap = ChatPhrases.StdText("en", "swap");
        Check(ChatPhrases.Text(s, std[4]) == enSwap, "без своей — стандартная на языке фраз", ChatPhrases.Text(s, std[4]));

        // Копия настроек (окно настроек) правит кнопку — оригинал не меняется.
        var copy = s.Clone();
        ChatPhrases.SetText(copy, 5, "mid or feed");
        Check(ChatPhrases.Set(s).Buttons is null && ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[5]) == "mid or feed",
              "правка копии не задела оригинал (массив не общий)",
              $"оригинал «{ChatPhrases.Text(s, ChatPhrases.Buttons(s)[5])}»");
        // Вторая правка копии — по уже своему массиву: оригинал всё так же цел.
        var own = s.Clone();
        ChatPhrases.SetText(own, 5, "раз");
        var keep = own.Clone();
        ChatPhrases.SetText(own, 5, "два");
        Check(ChatPhrases.Text(keep, ChatPhrases.Buttons(keep)[5]) == "раз",
              "правка копии своего набора не задела оригинал", ChatPhrases.Text(keep, ChatPhrases.Buttons(keep)[5]));

        // Вписали ровно стандартную — хранится стандартной и пойдёт за языком.
        ChatPhrases.SetText(copy, 4, "  " + enSwap + " ");
        Check(ChatPhrases.Buttons(copy)[4].Text == "", "стандартная, вписанная руками, хранится как стандартная",
              ChatPhrases.Buttons(copy)[4].Text);
        ChatPhrases.SetText(copy, 5, ChatPhrases.StdText("en", "show"));
        Check(ChatPhrases.Set(copy).Buttons is null, "всё снова как в стандартном наборе — набор опять стандартный (идёт за программой)");

        ChatPhrases.SetText(copy, 5, "mid or feed");
        ChatPhrases.SetLang(copy, "ru");
        var b3 = ChatPhrases.Buttons(copy)[4];
        Check(ChatPhrases.Text(copy, b3) == ChatPhrases.StdText("ru", "swap"), "смена языка: стандартная встала на новом языке",
              ChatPhrases.Text(copy, b3));
        Check(ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[5]) == "mid or feed", "смена языка: своя фраза осталась");

        // Вид кнопки: стандартная другого вида становится своей, текст — как был.
        var who = ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[1]);
        ChatPhrases.SetKind(copy, 1, ChatPhrases.Plain);
        var b0 = ChatPhrases.Buttons(copy)[1];
        Check(b0.Kind == ChatPhrases.Plain && b0.Std == "" && b0.Text == "кого будешь брать?",
              "стандартная с выбором → простая: своя, блок и знак перед ним ушли из текста", b0.ToString());
        ChatPhrases.SetKind(copy, 5, ChatPhrases.List);
        Check(ChatPhrases.Buttons(copy)[5].Kind == ChatPhrases.List && ChatPhrases.Buttons(copy)[5].Text == "mid or feed …",
              "простую можно сделать кнопкой с выбором — блок встаёт в конец", ChatPhrases.Buttons(copy)[5].Text);
        // Новый перечень — пустой, а не стандартные нужды (владелец 26.11); пока
        // пуст, на полосе его нет. У стандартной «Нам нужен» — нужды.
        var newList = ChatPhrases.Buttons(copy)[5];
        Check(ChatPhrases.ChoiceItems(newList).Count == 0 && !ChatPhrases.Bar(copy).Contains(newList),
              "новый перечень — без вариантов, пока пуст — его нет на полосе",
              string.Join(", ", ChatPhrases.ChoiceItems(newList).Select(c => c.ToString())));
        Check(ChatPhrases.ChoiceItems(ChatPhrases.Buttons(copy)[3]).Count == ChatPhrases.Needs.Length,
              "у стандартной «Нам нужен …» — стандартный перечень");
        ChatPhrases.SetOwnChoices(copy, 5, true);
        ChatPhrases.AddChoice(copy, 5);
        ChatPhrases.SetChoiceText(copy, 5, 0, "хил");
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[5]) is [(_, "хил")], "в новый перечень вписан свой вариант «хил»");
        var bar = ChatPhrases.Bar(copy);
        Check(bar[0].Std == ChatPhrases.Hello && bar.Skip(1).Take(4).All(ChatPhrases.IsChoice) && bar[2].Kind == ChatPhrases.List && bar[4].Kind == ChatPhrases.List && !bar.Skip(5).Any(ChatPhrases.IsChoice),
              "на полосе приветствие первым, за ним кнопки с выбором в своём порядке", string.Join(" ", bar.Select(b => b.Kind)));

        // Добавить и убрать; не больше восьми; пустая новая — не на полосе и не сохраняется.
        ChatPhrases.Add(copy);
        Check(ChatPhrases.Buttons(copy).Count == 7 && ChatPhrases.Bar(copy).Count == 6,
              "новая кнопка пустая — в редакторе есть, на полосе нет");
        Check(ChatPhrases.Normalize(ChatPhrases.Set(copy).Buttons)!.Length == 6, "пустая новая не сохраняется");
        ChatPhrases.SetText(copy, 6, "Кто в лес?");
        ChatPhrases.Add(copy); ChatPhrases.Add(copy); ChatPhrases.Add(copy);
        Check(ChatPhrases.Buttons(copy).Count == ChatPhrases.Max, $"кнопок не больше {ChatPhrases.Max}", $"{ChatPhrases.Buttons(copy).Count}");
        ChatPhrases.Remove(copy, 2);
        Check(ChatPhrases.Buttons(copy).Count == ChatPhrases.Max - 1 && ChatPhrases.Buttons(copy).All(b => b.Std != "canI"),
              "убрать — убирает именно эту кнопку");
        ChatPhrases.Reset(copy);
        Check(ChatPhrases.Set(copy).Buttons is null, "«Вернуть стандартные» — стандартный набор");

        // Битое из файла: неизвестный вид, чужая стандартная, больше восьми.
        var junk = Enumerable.Range(0, 12).Select(i => new ChatButton { Kind = "xx", Std = "nope", Text = $"фраза {i}" }).ToArray();
        var fixedUp = ChatPhrases.Normalize(junk)!;
        Check(fixedUp.Length == ChatPhrases.Max && fixedUp.All(b => b.Kind == ChatPhrases.Plain && b.Std == ""),
              "битый набор из файла приведён в порядок");

        // Пустой язык — язык программы.
        ChatPhrases.PutSet(copy, ChatPhrases.Set(copy) with { Lang = "" });
        Check(ChatPhrases.Lang(copy) == Loc.Current, "язык фраз не выбран — язык программы", ChatPhrases.Lang(copy));
        ChatPhrases.PutSet(copy, ChatPhrases.Set(copy) with { Lang = "xx" });
        Check(ChatPhrases.Lang(copy) == Loc.Current, "неизвестный язык — язык программы", ChatPhrases.Lang(copy));

        // Выбранный язык фраз держится, даже если он совпал с языком программы,
        // а программу потом переключили (владелец 26.11: «язык должен
        // запоминаться и стоять в фразах такой, как выбрал пользователь»).
        var chosen = AppSettings.Defaults();
        ChatPhrases.SetLang(chosen, "ru");             // программа сейчас по-русски
        var json = System.Text.Json.JsonSerializer.Serialize(chosen);
        Loc.SetLanguage("en");                          // переключили язык программы
        var reread = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        var whoText = ChatPhrases.Text(reread, ChatPhrases.Buttons(reread)[1]);
        Loc.SetLanguage("ru");
        Check(ChatPhrases.Lang(reread) == "ru" && whoText == "…, кого будешь брать?",
              "выбрали русские фразы, программу переключили на английский — фразы русские (и после записи на диск)",
              $"{ChatPhrases.Lang(reread)}: {whoText}");
    }

    private static void Choices()
    {
        Console.WriteLine("варианты выбора:");
        var s = AppSettings.Defaults();
        ChatPhrases.SetLang(s, "ru");
        var who = ChatPhrases.Buttons(s)[1];
        var names = ChatPhrases.Options(s, who).Select(o => o.Name).ToList();
        Check(!ChatPhrases.OwnChoices(who) && names.SequenceEqual(["топ", "лес", "мид", "АДК", "саппорт"]),
              "по умолчанию — стандартные роли на языке фраз", string.Join(", ", names));
        ChatPhrases.SetLang(s, "en");
        Check(ChatPhrases.Options(s, who)[0].Name == "top", "стандартные варианты идут за языком фраз",
              ChatPhrases.Options(s, who)[0].Name);
        ChatPhrases.SetLang(s, "ru");

        // Свои — копия стандартных, уже своими словами; оригинал настроек цел.
        var copy = s.Clone();
        ChatPhrases.SetOwnChoices(copy, 1, true);
        var own = ChatPhrases.Buttons(copy)[1];
        Check(ChatPhrases.OwnChoices(own) && own.Choices!.Select(c => c.Text).SequenceEqual(names)
              && own.Choices!.Select(c => c.Key).SequenceEqual(ChatPhrases.Roles),
              "«свои варианты» начинаются копией стандартных, с ключами ролей (для иконок)",
              string.Join(", ", own.Choices!.Select(c => c.ToString())));
        Check(!ChatPhrases.OwnChoices(ChatPhrases.Buttons(s)[1]), "включили свои в копии — оригинал не задет");

        // Топчик — только у этой кнопки, у соседней роли стандартные.
        ChatPhrases.SetChoiceText(copy, 1, 0, "топчик");
        var first = ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[0];
        Check(first.Name == "топчик" && first.Item.Key == "top", "переименовали топ в «топчик» — ключ роли остался",
              first.Item.ToString());
        var sent = ChatPhrases.Compose("ru", ChatPhrases.Text(copy, ChatPhrases.Buttons(copy)[1]), first.Name);
        Check(sent == "Топчик, кого будешь брать?", "фраза с ним: «Топчик, кого будешь брать?»", sent);
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[2])[0].Name == "топ",
              "у каждой кнопки свои варианты — у «Можно мне …?» по-прежнему «топ»");
        // Копия настроек со своими вариантами правит их — оригинал цел.
        var before = copy.Clone();
        ChatPhrases.SetChoiceText(copy, 1, 1, "лесник");
        Check(ChatPhrases.Options(before, ChatPhrases.Buttons(before)[1])[1].Name == "лес",
              "правка своих вариантов в копии не задела оригинал",
              ChatPhrases.Options(before, ChatPhrases.Buttons(before)[1])[1].Name);
        ChatPhrases.SetChoiceText(copy, 1, 1, "лес");

        // Смена языка: свои остаются своими, стандартные соседа — на новом языке.
        ChatPhrases.SetLang(copy, "en");
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[1].Name == "лес"
              && ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[2])[0].Name == "top",
              "смена языка: свои варианты как есть, стандартные — на новом языке");
        ChatPhrases.SetLang(copy, "ru");

        // Добавить свой, убрать, предел, пустой не виден.
        ChatPhrases.AddChoice(copy, 1);
        Check(ChatPhrases.ChoiceItems(ChatPhrases.Buttons(copy)[1]).Count == 6
              && ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1]).Count == 5,
              "новый вариант пустой — в редакторе есть, в списке нет");
        ChatPhrases.SetChoiceText(copy, 1, 5, "филл");
        var last = ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[^1];
        Check(last.Name == "филл" && last.Item.Key == "", "свой вариант «филл» — без ключа", last.Item.ToString());
        ChatPhrases.RemoveChoice(copy, 1, 0);
        Check(ChatPhrases.Options(copy, ChatPhrases.Buttons(copy)[1])[0].Name == "лес", "убрать — убирает именно его");
        for (int n = 0; n < 20; n++) ChatPhrases.AddChoice(copy, 1);
        Check(ChatPhrases.ChoiceItems(ChatPhrases.Buttons(copy)[1]).Count == ChatPhrases.MaxChoices,
              $"вариантов не больше {ChatPhrases.MaxChoices}");
        var saved = ChatPhrases.Normalize(ChatPhrases.Set(copy).Buttons)!;
        Check(saved[1].Choices!.Length == 5, "пустые новые варианты не сохраняются", $"{saved[1].Choices!.Length}");

        // Все варианты убраны — кнопке нечего предложить, на полосе её нет.
        var bare = s.Clone();
        ChatPhrases.SetOwnChoices(bare, 3, true);
        for (int n = 0; n < ChatPhrases.Needs.Length; n++) ChatPhrases.RemoveChoice(bare, 3, 0);
        Check(ChatPhrases.Bar(bare).All(b => b.Std != "need") && ChatPhrases.Buttons(bare)[3].Choices is { Length: 0 },
              "все варианты убраны — кнопки нет на полосе (а не стандартные назад)");

        // Обратно к стандартным; смена вида — стандартные нового вида.
        ChatPhrases.SetOwnChoices(copy, 1, false);
        Check(!ChatPhrases.OwnChoices(ChatPhrases.Buttons(copy)[1]), "«Стандартные варианты» — снова стандартные");
        ChatPhrases.SetOwnChoices(copy, 2, true);
        ChatPhrases.SetKind(copy, 2, ChatPhrases.List);
        var b1 = ChatPhrases.Buttons(copy)[2];
        Check(!ChatPhrases.OwnChoices(b1) && ChatPhrases.Options(copy, b1).Count == 0,
              "роль сменили на перечень — новый перечень пуст (стандартные нужды — только у «Нам нужен»)");
        Check(ChatPhrases.NormalizeChoices(ChatPhrases.Plain, "", [new ChoiceItem { Text = "x" }]) is null,
              "у простой фразы вариантов нет");
        var junk = ChatPhrases.NormalizeChoices(ChatPhrases.List, "need", [new ChoiceItem { Key = "mid", Text = "мид" }, new ChoiceItem()]);
        Check(junk is [{ Key: "", Text: "мид" }], "чужой ключ (роль у «что нужно») отброшен, пустой — прочь",
              string.Join(", ", junk?.Select(c => c.ToString()) ?? []));
        var order = ChatPhrases.NormalizeChoices(ChatPhrases.List, ChatPhrases.Swap, [new ChoiceItem { Key = "front" }, new ChoiceItem { Key = "second" }]);
        Check(order is [{ Key: "second" }], "у «Можно я пикну» ключи — только места в очереди",
              string.Join(", ", order?.Select(c => c.ToString()) ?? []));

        // «Можно я пикну …?» (владелец 26.11: вместо «wanna swap» — «can I pick»
        // и места в очереди словами): стандартный перечень — первым…пятым.
        var swap = ChatPhrases.Buttons(s)[4];
        var places = ChatPhrases.Options(s, swap).Select(o => o.Name).ToList();
        Check(swap.Std == ChatPhrases.Swap && swap.Kind == ChatPhrases.List
              && places.SequenceEqual(["первым", "вторым", "третьим", "четвёртым", "пятым"]),
              "у «Можно я пикну …?» — места в очереди пика словами", string.Join(", ", places));
        ChatPhrases.SetLang(s, "en");
        places = ChatPhrases.Options(s, swap).Select(o => o.Name).ToList();
        Check(places.SequenceEqual(["first", "second", "third", "fourth", "fifth"]),
              "по-английски — first…fifth", string.Join(", ", places));
        ChatPhrases.SetLang(s, "ru");
        var ownOrder = s.Clone();
        ChatPhrases.SetOwnChoices(ownOrder, 4, true);
        Check(ChatPhrases.Buttons(ownOrder)[4].Choices!.Select(c => c.Key).SequenceEqual(ChatPhrases.Order)
              && ChatPhrases.Buttons(ownOrder)[4].Choices![0].Text == "первым",
              "свои варианты «Можно я пикну» начинаются копией мест в очереди",
              string.Join(", ", ChatPhrases.Buttons(ownOrder)[4].Choices!.Select(c => c.ToString())));

        // Наборы, сохранённые, пока «swap» была с выбором роли («Мид, давай
        // свапнемся?»): без своего текста — снова стандартная, с местами в
        // очереди (свои роли туда не годятся); со своим текстом — своя кнопка с ролью.
        var roleChoices = new[] { new ChoiceItem { Key = "mid", Text = "мидер" } };
        var old = ChatPhrases.Normalize(
        [
            new ChatButton { Kind = ChatPhrases.Role, Std = ChatPhrases.Swap, Choices = roleChoices },
            new ChatButton { Kind = ChatPhrases.Role, Std = ChatPhrases.Swap, Text = "…, меняемся?", Choices = roleChoices },
        ])!;
        Check(old[0] is { Kind: ChatPhrases.List, Std: ChatPhrases.Swap, Choices: null },
              "старая «swap» без своего текста — стандартная «Можно я пикну» со стандартными местами", old[0].ToString());
        Check(old[1] is { Kind: ChatPhrases.Role, Std: "", Text: "…, меняемся?", Choices: [{ Key: "mid", Text: "мидер" }] },
              "старая «swap» со своим текстом — своя кнопка с ролью, варианты целы", old[1].ToString());
    }

    /// <summary>
    /// Фразы у каждого аккаунта свои (слово владельца 25.11): правки аккаунта A
    /// не видны B; без своего набора — общий (""), без него — стандартный; копия
    /// настроек не правит оригинал через общий словарь; набор переживает запись
    /// ui.json и чтение обратно. Плюс язык стандартных вариантов у кнопки.
    /// </summary>
    private static void Accounts()
    {
        Console.WriteLine("по аккаунтам:");
        var s = AppSettings.Defaults();
        ChatPhrases.AccountOverride = "";
        ChatPhrases.SetText(s, 4, "общая фраза");
        ChatPhrases.AccountOverride = "A";
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == "общая фраза", "у аккаунта без своего набора — общий");
        ChatPhrases.SetText(s, 4, "фраза A");
        ChatPhrases.AccountOverride = "B";
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == "общая фраза", "правка A не видна B");
        ChatPhrases.Reset(s);
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == ChatPhrases.StdText("ru", "swap"),
              "«Вернуть стандартные» у B — стандартные, а не общий набор", ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]));
        ChatPhrases.AccountOverride = "A";
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == "фраза A", "у A — его правка");

        // Копия настроек: правка набора A в копии не задевает оригинал.
        var copy = s.Clone();
        ChatPhrases.SetText(copy, 4, "фраза A2");
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == "фраза A", "правка копии не задела оригинал (словарь не общий)",
              ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]));

        // Галочки окна фраз: при «Стандартных» — стандартный набор, свои
        // кнопки хранятся; снова «Свои» — они на месте.
        Check(ChatPhrases.Own(s), "правка кнопки — это свои фразы (галочка «Свои фразы»)");
        ChatPhrases.SetOwn(s, false);
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == ChatPhrases.StdText("ru", "swap"),
              "галочка «Стандартные фразы» — стандартный набор", ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]));
        ChatPhrases.SetOwn(s, true);
        Check(ChatPhrases.Text(s, ChatPhrases.Buttons(s)[4]) == "фраза A", "снова «Свои фразы» — свои кнопки на месте");
        ChatPhrases.SetOwnChoices(s, 2, true);
        Check(ChatPhrases.Buttons(s)[2].Choices![2].Text == "мид", "свои варианты начинаются копией стандартных на языке фраз",
              ChatPhrases.Buttons(s)[2].Choices![2].Text);

        // Запись ui.json и чтение обратно.
        var json = System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        Check(ChatPhrases.Text(back, ChatPhrases.Buttons(back)[4]) == "фраза A"
              && ChatPhrases.Buttons(back)[2].Choices![2].Text == "мид" && ChatPhrases.Own(back),
              "наборы по аккаунтам переживают запись ui.json и чтение", json.Length.ToString());
        ChatPhrases.AccountOverride = "B";
        Check(ChatPhrases.Text(back, ChatPhrases.Buttons(back)[4]) == ChatPhrases.StdText("ru", "swap"),
              "из файла у B — стандартные");
        ChatPhrases.AccountOverride = null;
    }

    private static void Compose()
    {
        Console.WriteLine("фраза с выбором:");
        string C(string lang, string std, string kind, string key) =>
            ChatPhrases.Compose(lang, ChatPhrases.StdText(lang, std), ChatPhrases.ChoiceName(lang, kind, key, std));
        var r = C("ru", "who", ChatPhrases.Role, "mid");
        Check(r == "Мид, кого будешь брать?", "в начале фразы — с заглавной", r);
        r = C("ru", "canI", ChatPhrases.Role, "jungle");
        Check(r == "Можно мне лес?", "в середине — как есть", r);
        r = C("ru", "need", ChatPhrases.List, "front");
        Check(r == "Нам нужен фронтлайн", "нам нужен фронтлайн", r);
        r = C("en", "who", ChatPhrases.Role, "adc");
        Check(r == "ADC, who are you picking?", "английский: ADC, who are you picking?", r);
        r = C("en", "swap", ChatPhrases.List, "first");
        Check(r == "Can I pick first?", "английский: Can I pick first?", r);
        r = C("ru", "swap", ChatPhrases.List, "second");
        Check(r == "Можно я пикну вторым?", "можно я пикну вторым", r);
        r = C("tr", "swap", ChatPhrases.List, "first");
        Check(r == "Birinci sırada pick atabilir miyim?", "в начале фразы место в очереди — с заглавной (турецкий)", r);
        r = ChatPhrases.Compose("tr", "… lazım", "istek");
        Check(r == "İstek lazım", "заглавная по правилам языка (турецкое i → İ)", r);
        r = ChatPhrases.Compose("ru", "Ну... нам нужен …", "танк");
        Check(r == "Ну... нам нужен танк", "три точки — просто текст: место для выбора только у блока", r);
        r = ChatPhrases.Compose("ru", "Кто идёт", "мид");
        Check(r == "Кто идёт мид", "места нет — выбор в конец через пробел", r);
        var bad = Loc.Languages.SelectMany(l => new[] { ("who", ChatPhrases.Role), ("canI", ChatPhrases.Role), ("need", ChatPhrases.List), ("swap", ChatPhrases.List) }
                .SelectMany(x => ChatPhrases.StdChoices(x.Item2, x.Item1).Select(k => (l.Code, Text: C(l.Code, x.Item1, x.Item2, k)))))
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
                foreach (var std in new[] { "who", "canI", "need", "swap", "show", "hello" })
                    if (ChatPhrases.StdText(l.Code, std) == ChatPhrases.StdText("en", std))
                        missing.Add($"{l.Code}:{std} по-английски");
            // У шаблонов с выбором есть место для него.
            foreach (var std in new[] { "who", "canI", "need", "swap" })
                if (!ChatPhrases.StdText(l.Code, std).Contains(ChatPhrases.Slot))
                    missing.Add($"{l.Code}:{std} без «…»");
        }
        Check(missing.Count == 0, $"{keys.Count} строк на {Loc.Languages.Count} языках",
              string.Join(", ", missing.Take(10)));

        // Каждая строка фраз, которую зовёт код (Loc.T / TIn / loc:T в разметке),
        // есть в файле — иначе на экране вместо надписи встал бы ключ.
        var src = Directory.GetFiles(Path.Combine(repo, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml")).Select(File.ReadAllText);
        var called = src.SelectMany(text => System.Text.RegularExpressions.Regex.Matches(text, @"""phrases\.([A-Za-z]+)""|loc:T phrases\.([A-Za-z]+)")
                .Select(m => "phrases." + (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)))
            .Distinct().ToList();
        var absent = called.Where(k => !keys.Contains(k)).ToList();
        Check(called.Count > 20 && absent.Count == 0, $"все {called.Count} строк фраз, которые зовёт код, есть в файлах",
              string.Join(", ", absent));
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
        // Настоящий клиент LoL проверке не указ: свёрнут — окно ушло бы в трей,
        // и кадры вышли бы пустыми (так падала SettingPreviews 26.11).
        var w = new OverlayWindow { FollowClient = false };
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
        Check(labels.Count == 6 && labels[0] == "Всем привет!" && labels[1] == "…, кого будешь брать? ▾" && labels[3] == "Нам нужен … ▾"
              && labels[4] == "Можно я пикну …? ▾",
              "на кнопках стандартные фразы языка программы, у кнопок с выбором — ▾", string.Join(" | ", labels));
        if (snap is not null) Snap(w, snap, "draft-phrases.png");

        // Кнопка с выбором: список ролей (не открываем — окно было бы на экране),
        // пункт «мид» — фраза с выбранным.
        var menu = w.PhraseMenu(1);
        var items = menu.Items.OfType<MenuItem>().ToList();
        // Иконка — только если картинки ролей грузятся: ресурсы лежат в сборке
        // программы, а pack://application ищет их у запущенного exe — у
        // проверки их нет, у программы есть.
        var iconsLoad = RoleIcons.Get("middle") is not null;
        Check(items.Count == 5 && items.All(i => i.Header is StackPanel { Children.Count: var n } && n == (iconsLoad ? 2 : 1)),
              $"у «кого будешь брать» пять ролей{(iconsLoad ? " с иконками" : "")}", $"{items.Count}");
        items.First(i => (string)i.Tag == "mid").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        // Вспышку смотреть сразу, до Pump: она ставится в самом нажатии, а гаснет
        // через 1,2 с — медленная отрисовка в Pump (со снимками) успевала её
        // погасить, и пункт падал через раз.
        var flashed = list.Items.OfType<PhraseChip>().ElementAtOrDefault(1);
        Pump();
        Check(sent is ["Мид, кого будешь брать?"], "выбрал «мид» — в чат «Мид, кого будешь брать?»", string.Join(" | ", sent));
        Check(flashed is { State: "sent" } && flashed.Text == "✓ Мид, кого будешь брать?",
              "кнопка вспыхнула отправленной фразой", flashed?.Text ?? "нет");
        var needs = w.PhraseMenu(3).Items.Count;
        Check(needs == ChatPhrases.Needs.Length, $"у «нам нужен» {ChatPhrases.Needs.Length} вариантов", $"{needs}");
        var picks = w.PhraseMenu(4).Items.OfType<MenuItem>().Select(i => (string)i.Tag).ToList();
        Check(picks.SequenceEqual(ChatPhrases.Order), "у «Можно я пикну» в списке пять мест в очереди", string.Join(", ", picks));
        // Второй выбор сразу — не уходит (не чаще раза в 1,5 с).
        ((MenuItem)w.PhraseMenu(2).Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        Check(sent.Count == 1, "вторая фраза сразу за первой не уходит", $"{sent.Count}");

        // Своя кнопка в окне фраз → на полосе после «Сохранить»; простая
        // кнопка, ставшая кнопкой с выбором, встаёт к кнопкам с выбором.
        var edit = w.OpenPhrases();
        ShowHidden(edit);
        Pump();
        var stdFields = edit.Fields();
        Check(stdFields.Count == 6 && stdFields.All(f => !f.IsEnabled) && !ChatPhrases.Own(AppSettings.Current),
              "окно фраз: стандартные фразы, поля своих видны, но не активны", $"{stdFields.Count}, активных {stdFields.Count(f => f.IsEnabled)}");
        if (snap is not null) Snap(edit, snap, "phrases-std.png");
        // Щелчок по приглушённым своим фразам включает их.
        PhrasesWindow.Find<System.Windows.Controls.Border>(edit).First(b => (string?)b.Tag == "ownPreview")
            .RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        Pump();
        Check(ChatPhrases.Own(edit.Draft) && edit.Fields().All(f => f.IsEnabled),
              "щелчок по приглушённым своим фразам — галочка «Свои фразы», поля активны");
        var fields = edit.Fields();
        Check(fields.Count == 6, "галочка «Свои фразы» — шесть полей, копия стандартных", $"{fields.Count}");
        if (snap is not null) Snap(edit, snap, "phrases-window.png");
        ((System.Windows.Controls.TextBox)fields[0]).Text = "Не баньте Сону, я на ней";   // приветствие — простое поле
        var kinds = Find<System.Windows.Controls.ComboBox>(edit).Where(c => c.Items.Count == ChatPhrases.Kinds.Length).ToList();
        Check(kinds.Count == 6, "у каждой кнопки выбор вида", $"{kinds.Count}");
        kinds[5].SelectedIndex = Array.IndexOf(ChatPhrases.Kinds, ChatPhrases.Role);   // «Покажите своих чемпионов» → роль
        Pump();
        edit.Save();
        Pump();
        labels = list.Items.OfType<PhraseChip>().Select(c => c.Text).ToList();
        Check(labels.Count > 0 && labels[0] == "Не баньте Сону, я на ней", "своя фраза — на кнопке после «Сохранить» (переписанное приветствие — всё ещё первое)",
              string.Join(" | ", labels));
        Check(labels.Count == 6 && labels[5] == "Покажите своих чемпионов … ▾",
              "ставшая кнопкой с выбором — шестой: после приветствия и стандартных с выбором", string.Join(" | ", labels));

        // Свои варианты в окне: «Свои варианты» у первой кнопки — копия ролей,
        // «топ» → «топчик», «Сохранить» — в списке над кнопкой «топчик».
        var edit2 = w.OpenPhrases();
        ShowHidden(edit2);
        Pump();
        // Окно вариантов (с «✎ Редактировать»): в полях стандартные роли,
        // первая правка делает список своим; «топ» → «топчик», «Сохранить».
        var links = PhrasesWindow.Find<TextBlock>(edit2).Count(x => (string?)x.Tag == "editChoices");
        var choiceButtons = ChatPhrases.Buttons(AppSettings.Current).Count(ChatPhrases.IsChoice);
        Check(links == choiceButtons, "«✎ Редактировать» — у каждой кнопки с выбором", $"{links} из {choiceButtons}");
        var cw = edit2.ChoicesFor(1);
        ShowHidden(cw);
        Pump();
        var resetLabel = Loc.T("phrases.reset");
        Check(cw.Title == Loc.T("phrases.choicesTitle", "[Роль], кого будешь брать?")
              && PhrasesWindow.Find<TextBlock>(cw).Any(x => x.Text == Loc.T("phrases.choicesHint"))
              && PhrasesWindow.Find<System.Windows.Controls.Button>(cw).First(x => (string)x.Content == resetLabel).Visibility == Visibility.Visible,
              "окно вариантов роли: заголовок с блоком «[Роль]», подсказка про стандартные, «Вернуть стандартные» есть", cw.Title);
        var choiceFields = cw.Fields();
        Check(choiceFields.Count == 5 && choiceFields[0].Text == "топ" && !ChatPhrases.OwnChoices(ChatPhrases.Buttons(AppSettings.Current)[1]),
              "окно вариантов: пять стандартных ролей в полях, первая «топ»", string.Join(", ", choiceFields.Select(f => f.Text)));
        if (snap is not null) Snap(cw, snap, "choices.png");
        choiceFields[0].Text = "топчик";
        cw.Close();
        // Поле с блоком: у кнопки с выбором вписать текст до блока.
        var slotField = (System.Windows.Controls.RichTextBox)edit2.Fields()[3];
        Check(SlotBox.Read(slotField.Document) == "Нам нужен …", "в поле «Нам нужен» — текст и блок на месте «…»",
              SlotBox.Read(slotField.Document));
        var para = (System.Windows.Documents.Paragraph)slotField.Document.Blocks.FirstBlock;
        para.ContentEnd.InsertTextInRun(" срочно");
        Pump();
        Check(SlotBox.Read(slotField.Document) == "Нам нужен … срочно", "текст после блока дописывается",
              SlotBox.Read(slotField.Document));
        if (snap is not null) Snap(edit2, snap, "phrases-slot.png");
        if (snap is not null) SnapZoom((FrameworkElement)slotField.Parent, snap, "slot-zoom.png");
        if (snap is not null) SnapZoom(PhrasesWindow.Find<System.Windows.Controls.Grid>(edit2).First(g => g.Tag is 3), snap, "row-zoom.png", 2);
        edit2.Save();
        Pump();
        var needMenu = w.PhraseMenu(3).Items.OfType<MenuItem>().FirstOrDefault();
        Check((string?)needMenu?.ToolTip == "Нам нужен фронтлайн срочно", "в драфте выбор встаёт на место блока, текст вокруг — свой",
              needMenu?.ToolTip?.ToString() ?? "нет");
        // Запомнится ли до следующего запуска: «Сохранить» пишет ui.json; читаем
        // его заново, как программа при старте (владелец 26.11: «программа
        // запоминает выбор и текст, если сохранить?»).
        var uiPath = Path.Combine(AppPaths.Root, "ui.json");
        AppSettings.Reload();
        Pump();
        var reread = AppSettings.Current;
        var first = ChatPhrases.Buttons(reread)[1];
        Check(File.Exists(uiPath) && ChatPhrases.Own(reread)
              && ChatPhrases.Text(reread, ChatPhrases.Buttons(reread)[3]) == "Нам нужен … срочно"
              && ChatPhrases.Name(reread, first, ChatPhrases.ChoiceItems(first)[0]) == "топчик",
              "после «Сохранить» и чтения ui.json с диска (как при запуске) — галочка, свой текст и свои варианты на месте",
              $"{File.Exists(uiPath)} {ChatPhrases.Own(reread)} «{ChatPhrases.Text(reread, ChatPhrases.Buttons(reread)[3])}»");
        var own = w.PhraseMenu(1).Items.OfType<MenuItem>().ToList();
        Check(own.Count == 5 && (string?)own[0].ToolTip == "Топчик, кого будешь брать?",
              "в списке над кнопкой — «Топчик, кого будешь брать?»", own.FirstOrDefault()?.ToolTip?.ToString() ?? "нет");
        var neighbour = w.PhraseMenu(2).Items.OfType<MenuItem>().FirstOrDefault();
        Check((string?)neighbour?.ToolTip == "Можно мне топ?", "у соседней кнопки «Можно мне топ?» — свои варианты только у первой",
              neighbour?.ToolTip?.ToString() ?? "нет");

        // Новый перечень в окне вариантов: заголовок «[Перечень]», подсказка
        // «перечень пуст — добавьте», «Вернуть стандартные» нет — нечего.
        var edit4 = w.OpenPhrases();
        ShowHidden(edit4);
        Pump();
        ChatPhrases.Add(edit4.Draft);
        var last = ChatPhrases.Buttons(edit4.Draft).Count - 1;
        ChatPhrases.SetKind(edit4.Draft, last, ChatPhrases.List);
        var cwNew = edit4.ChoicesFor(last);
        ShowHidden(cwNew);
        Pump();
        Check(cwNew.Title == Loc.T("phrases.choicesTitle", "[Перечень]")
              && PhrasesWindow.Find<TextBlock>(cwNew).Any(x => x.Text == Loc.T("phrases.choicesHintNew"))
              && PhrasesWindow.Find<System.Windows.Controls.Button>(cwNew).First(x => (string)x.Content == resetLabel).Visibility != Visibility.Visible,
              "окно нового перечня: «Варианты: [Перечень]», подсказка «перечень пуст», без «Вернуть стандартные»", cwNew.Title);
        cwNew.Close();
        edit4.Close();

        // Язык фраз английский — вид кнопки и подпись блока тоже по-английски
        // (владелец 26.11: «переключил язык, а выпадающий список остался»).
        var enSettings = AppSettings.Current.Clone();
        ChatPhrases.SetLang(enSettings, "en");
        AppSettings.Apply(enSettings);
        var edit3 = w.OpenPhrases();
        ShowHidden(edit3);
        Pump();
        var kind0 = PhrasesWindow.Find<System.Windows.Controls.ComboBox>(edit3).Where(c => c.Items.Count == ChatPhrases.Kinds.Length).ElementAt(1);   // 0 — приветствие
        var kindLabel = (kind0.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content as string;
        var chipLabel = ChipText((System.Windows.Controls.RichTextBox)edit3.Fields()[1]);
        Check(kindLabel == "Role" && chipLabel == "Role", "язык фраз английский — вид кнопки и подпись блока «Role»",
              $"{kindLabel} / {chipLabel}");
        edit3.Close();
        var ruSettings = AppSettings.Current.Clone();
        ChatPhrases.SetLang(ruSettings, "ru");
        AppSettings.Apply(ruSettings);
        Pump();

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

    /// Подпись первого блока в поле с блоком.
    private static string? ChipText(System.Windows.Controls.RichTextBox box) =>
        box.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().SelectMany(p => p.Inlines)
            .OfType<System.Windows.Documents.InlineUIContainer>()
            .Select(c => (c.Child as System.Windows.Controls.Border)?.Child).OfType<TextBlock>().FirstOrDefault()?.Text;

    /// Снимок одного элемента крупно (во столько-то раз) — разглядеть мелкое:
    /// ровно ли стоит надпись в плашке блока.
    private static void SnapZoom(FrameworkElement el, string dir, string file, double k = 3)
    {
        Directory.CreateDirectory(dir);
        var bmp = new RenderTargetBitmap((int)(el.ActualWidth * k), (int)(el.ActualHeight * k), 96 * k, 96 * k, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
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
