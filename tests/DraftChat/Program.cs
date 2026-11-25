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
/// 3. Свои фразы: совпавшая со стандартной хранится пустой и идёт за языком
///    фраз, своя — остаётся при смене языка; копия настроек (окно настроек)
///    не правит оригинал через общий массив.
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
        Console.WriteLine("свои фразы:");
        var s = AppSettings.Defaults();
        Check(s.Phrases.Length == ChatPhrases.Count && s.Phrases.All(p => p == ""), "по умолчанию пять стандартных");
        Check(ChatPhrases.Normalize(["одна"]).Length == ChatPhrases.Count, "короткий массив из файла дополняется до пяти");
        Check(ChatPhrases.Normalize(null).Length == ChatPhrases.Count, "нет поля в файле — пять стандартных");

        s.PhraseLang = "en";
        var enSwap = ChatPhrases.Default("en", 0);
        Check(ChatPhrases.Text(s, 0) == enSwap, "без своей — стандартная на языке фраз", ChatPhrases.Text(s, 0));

        // Копия настроек (окно настроек) правит свою фразу — оригинал не меняется.
        var copy = s.Clone();
        ChatPhrases.Set(copy, 1, "mid or feed");
        Check(ChatPhrases.Custom(s, 1) == "" && ChatPhrases.Custom(copy, 1) == "mid or feed",
              "правка копии не задела оригинал (массив не общий)", $"оригинал «{ChatPhrases.Custom(s, 1)}»");

        // Вписали ровно стандартную — хранится пустой и пойдёт за языком.
        ChatPhrases.Set(copy, 0, "  " + enSwap + " ");
        Check(ChatPhrases.Custom(copy, 0) == "", "стандартная, вписанная руками, хранится как стандартная",
              ChatPhrases.Custom(copy, 0));

        copy.PhraseLang = "ru";
        Check(ChatPhrases.Text(copy, 0) == ChatPhrases.Default("ru", 0) && ChatPhrases.Text(copy, 0) != enSwap,
              "смена языка: стандартная встала на новом языке", ChatPhrases.Text(copy, 0));
        Check(ChatPhrases.Text(copy, 1) == "mid or feed", "смена языка: своя фраза осталась", ChatPhrases.Text(copy, 1));

        // Пустой язык — язык программы.
        copy.PhraseLang = "";
        Check(ChatPhrases.Lang(copy) == Loc.Current, "язык фраз не выбран — язык программы", ChatPhrases.Lang(copy));
        copy.PhraseLang = "xx";
        Check(ChatPhrases.Lang(copy) == Loc.Current, "неизвестный язык — язык программы", ChatPhrases.Lang(copy));
    }

    private static void Translations()
    {
        Console.WriteLine("переводы:");
        var repo = FindRepo();
        var dir = Path.Combine(repo, "assets", "i18n");
        var en = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "en.json"))).RootElement;
        var keys = en.GetProperty("phrases").EnumerateObject().Select(p => "phrases." + p.Name)
            .Concat(["settings.groupPhrases", "settings.draftPhrases", "settings.draftPhrasesHint"]).ToList();
        var missing = new List<string>();
        foreach (var l in Loc.Languages)
        {
            var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, $"{l.Code}.json"))).RootElement;
            foreach (var k in keys)
            {
                var parts = k.Split('.');
                if (!doc.TryGetProperty(parts[0], out var sec) || !sec.TryGetProperty(parts[1], out var v)
                    || v.GetString() is not { Length: > 0 })
                    missing.Add($"{l.Code}:{k}");
            }
            // Стандартные фразы переведены, а не скопированы с английского.
            if (l.Code != "en")
                for (int i = 0; i < ChatPhrases.Count; i++)
                    if (ChatPhrases.Default(l.Code, i) == ChatPhrases.Default("en", i))
                        missing.Add($"{l.Code}:p{i + 1} по-английски");
        }
        Check(missing.Count == 0, $"{keys.Count} строк на {Loc.Languages.Count} языках",
              string.Join(", ", missing.Take(10)));
        Check(Enumerable.Range(0, ChatPhrases.Count).All(i => ChatPhrases.Default("ru", i) != $"phrases.p{i + 1}"),
              "стандартные фразы читаются из сборки, а не ключом");
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
        Check(list.Items.Count == ChatPhrases.Count, "пять кнопок фраз", $"{list.Items.Count}");
        Check(((FrameworkElement)w.FindName("RecTail")).ActualHeight > 0, "в конце списка пиков есть место под полосу");
        var labels = list.Items.OfType<PhraseChip>().Select(c => c.Text).ToList();
        Check(labels.SequenceEqual(Enumerable.Range(0, ChatPhrases.Count).Select(i => ChatPhrases.Default("ru", i))),
              "на кнопках стандартные фразы языка программы", string.Join(" | ", labels));
        if (snap is not null) Snap(w, snap, "draft-phrases.png");

        // Своя фраза в окне фраз → на кнопке после «Сохранить».
        var edit = w.OpenPhrases();
        ShowHidden(edit);
        Pump();
        var fields = edit.Fields();
        Check(fields.Count == ChatPhrases.Count, "в окне фраз пять полей", $"{fields.Count}");
        if (snap is not null) Snap(edit, snap, "phrases-window.png");
        fields[2].Text = "Не баньте Сону, я на ней";
        edit.Save();
        Pump();
        var third = list.Items.OfType<PhraseChip>().ElementAtOrDefault(2)?.Text;
        Check(third == "Не баньте Сону, я на ней", "своя фраза — на кнопке после «Сохранить»", third ?? "нет");

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
