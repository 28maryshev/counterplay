using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Опрос на плашке сообщений.
///
/// Что держит:
/// - до ответа — варианты кнопками и подсказка, итога не видно;
/// - нажал — на месте кнопок доли голосов («3 · 60%»), свой вариант отмечен,
///   под вариантами «Голосов: N»; плашка при этом не меняет высоту;
/// - второй раз не проголосовать; голос помнится после перезапуска;
/// - счёт берётся свежий: прислал сайт больше — его, отстал (выдача из кэша
///   до голоса) — свой, чтобы собственный голос не пропал;
/// - в очереди с короткой новостью плашка держит высоту опроса;
/// - у обычного сообщения вариантов нет.
///
/// Голос не уходит на сайт: Notice.Frozen, и NoticePoll считает его на месте.
/// Данные — в своей временной папке (AppPaths.RootOverride).
/// </summary>
internal static class Program
{
    private static int _fails;

    private static string _root = "";

    private const string Question = "Смотрите ли вы руны в драфте?";

    [STAThread]
    private static int Main()
    {
        // Своя папка — и чистая: память о голосах с прошлого прогона сделала бы
        // опрос «уже отвеченным» с первого шага.
        _root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "NoticePoll");
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        Directory.CreateDirectory(_root);
        AppPaths.RootOverride = _root;
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Notice.Frozen = true;   // ни сообщений с сайта, ни настоящего голоса
        try { Run(); }
        finally { Notice.Frozen = false; }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: опрос на плашке работает"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // ── До ответа ──────────────────────────────────────────────────────
        Console.WriteLine("до ответа");
        File.WriteAllText(NoticePath, Items(Poll("7", """{"o1":3,"o2":1}""")));
        var w = Open();
        var poll = (ItemsControl)w.FindName("BetaPoll")!;
        var foot = (TextBlock)w.FindName("BetaPollFoot")!;
        var rows = Rows(poll);
        Check("три варианта на плашке", poll.Visibility == Visibility.Visible && rows.Count == 3,
              $"{poll.Visibility}, {rows.Count}");
        Check("до ответа итога не видно", rows.All(r => r.Score == "" && r.CanVote && !r.Voted),
              Scores(rows));
        Check("под вариантами — подсказка", foot.Text == Loc.T("notice.poll.hint"), foot.Text);
        var before = Card(w).ActualHeight;

        // ── Голос ──────────────────────────────────────────────────────────
        Console.WriteLine("голос за третий вариант");
        Click(poll, 2);
        Pump();
        rows = Rows(poll);
        Check("голос запомнился", NoticePoll.Mine(Item("7")) == "o3", NoticePoll.Mine(Item("7")) ?? "нет");
        Check("итог по вариантам", Scores(rows) == "3 · 60%|1 · 20%|1 · 20%", Scores(rows));
        Check("свой вариант отмечен", rows.Count(r => r.Mine) == 1 && rows.Single(r => r.Mine).OptionId == "o3",
              string.Join(",", rows.Where(r => r.Mine).Select(r => r.OptionId)));
        Check("варианты больше не нажимаются", rows.All(r => !r.CanVote), "");
        Check("под вариантами — сколько голосов", foot.Text == Loc.T("notice.poll.votes", 5), foot.Text);
        Check("на голосе плашка не прыгнула", Math.Abs(Card(w).ActualHeight - before) < 1,
              $"{Card(w).ActualHeight:0} против {before:0}");

        Click(poll, 0);
        Pump();
        Check("второй раз не проголосовать",
              NoticePoll.Mine(Item("7")) == "o3" && Scores(Rows(poll)) == "3 · 60%|1 · 20%|1 · 20%",
              Scores(Rows(poll)));
        Close(w);

        // ── Перезапуск ─────────────────────────────────────────────────────
        Console.WriteLine("новый запуск");
        Check("память о голосе — в папке программы",
              File.Exists(Path.Combine(_root, "notice-votes.json")), "notice-votes.json");
        NoticePoll_Reload();
        w = Open();
        poll = (ItemsControl)w.FindName("BetaPoll")!;
        Check("после перезапуска — итог, а не кнопки",
              Scores(Rows(poll)) == "3 · 60%|1 · 20%|1 · 20%" && Rows(poll).All(r => !r.CanVote),
              Scores(Rows(poll)));
        Close(w);

        // ── Чей счёт свежее ────────────────────────────────────────────────
        Console.WriteLine("счёт с сайта");
        File.WriteAllText(NoticePath, Items(Poll("7", """{"o1":10,"o2":4,"o3":6}""")));
        w = Open();
        poll = (ItemsControl)w.FindName("BetaPoll")!;
        foot = (TextBlock)w.FindName("BetaPollFoot")!;
        Check("сайт прислал больше голосов — его счёт", Scores(Rows(poll)) == "10 · 50%|4 · 20%|6 · 30%",
              Scores(Rows(poll)));
        Check("и всего голосов по нему", foot.Text == Loc.T("notice.poll.votes", 20), foot.Text);
        Close(w);

        // Тот же опрос, а сайт отдал старый ответ из кэша — без моего голоса.
        File.WriteAllText(NoticePath, Items(Poll("7", """{"o1":3,"o2":1}""")));
        w = Open();
        poll = (ItemsControl)w.FindName("BetaPoll")!;
        // Помним счёт на момент голоса (5), в выдаче — 4: свежее свой.
        Check("сайт отстал — счёт на момент голоса", Scores(Rows(poll)) == "3 · 60%|1 · 20%|1 · 20%",
              Scores(Rows(poll)));
        Check("и свой вариант не с нулём", !Rows(poll).Single(r => r.Mine).Score.StartsWith("0 "),
              Rows(poll).Single(r => r.Mine).Score);
        Close(w);

        // ── Новый опрос ────────────────────────────────────────────────────
        File.WriteAllText(NoticePath, Items(Poll("8", "{}")));
        w = Open();
        poll = (ItemsControl)w.FindName("BetaPoll")!;
        Check("другой опрос — снова кнопки", Rows(poll).All(r => r.CanVote && r.Score == ""),
              Scores(Rows(poll)));
        Close(w);

        // ── Очередь: опрос и короткая новость ──────────────────────────────
        // Плашка держит высоту самого высокого из активных — и опрос выше
        // новости на свои варианты. Без них в мерке короткая новость
        // сжимала бы плашку, а опрос растягивал обратно.
        Console.WriteLine("очередь из опроса и новости");
        File.WriteAllText(NoticePath, Items(Plain("Коротко.")));
        var alone = OpenAndMeasure();
        File.WriteAllText(NoticePath, Items(Poll("9", "{}"), Plain("Коротко.")));
        var pollFirst = OpenAndMeasure();
        File.WriteAllText(NoticePath, Items(Plain("Коротко."), Poll("9", "{}")));
        var newsFirst = OpenAndMeasure();
        Check("опрос заметно выше короткой новости", pollFirst > alone + 30,
              $"{pollFirst:0} против {alone:0}");
        Check("на новости плашка держит высоту опроса", Math.Abs(newsFirst - pollFirst) < 1,
              $"{newsFirst:0} против {pollFirst:0}");

        // ── Обычное сообщение ──────────────────────────────────────────────
        File.WriteAllText(NoticePath, Items(Plain("Просто новость.")));
        w = Open();
        Check("у обычного сообщения вариантов нет",
              ((ItemsControl)w.FindName("BetaPoll")!).Visibility == Visibility.Collapsed
              && ((TextBlock)w.FindName("BetaPollFoot")!).Visibility == Visibility.Collapsed, "");
        Close(w);

        app.Shutdown();
    }

    // ── Сообщения ──────────────────────────────────────────────────────────

    private static string NoticePath => Path.Combine(_root, "notice.json");

    private static string Items(params string[] items) =>
        $$$"""{"rotateSec":45,"items":[{{{string.Join(",", items)}}}]}""";

    private static string Poll(string id, string counts) =>
        $$$"""
        {"id":"{{{id}}}","kind":"info","head":"question","text":{"ru":"{{{Question}}}"},
         "poll":{"options":[{"id":"o1","text":{"ru":"Да, каждую игру"}},
                            {"id":"o2","text":{"ru":"Иногда"}},
                            {"id":"o3","text":{"ru":"Нет, ставлю свои"}}],
                 "counts":{{{counts}}} }}
        """;

    private static string Plain(string text) =>
        $$$"""{"id":"p{{{text.Length}}}","kind":"info","head":"new","text":{"ru":"{{{text}}}"} }""";

    private static Notice.Item Item(string id) => Notice.Active().First(i => i.Id == id);

    /// Перезапуск программы — память о голосах читается с диска заново.
    /// Отдельного «перечитать» у NoticePoll нет: путь тот же, и в работе
    /// файл никто, кроме неё, не пишет. Поэтому переставляем папку туда и
    /// обратно — это и есть «новый процесс» для её кэша.
    private static void NoticePoll_Reload()
    {
        AppPaths.RootOverride = _root + "-elsewhere";
        NoticePoll.Mine(new Notice.Item("x", "x", null, "info", "beta"));
        AppPaths.RootOverride = _root;
    }

    // ── Окно ───────────────────────────────────────────────────────────────

    private static OverlayWindow Open()
    {
        var w = new OverlayWindow();
        ShowHidden(w);
        w.ShowReadyPhase("Lobby");   // плашка живёт на экране готовности
        Pump();
        return w;
    }

    private static double OpenAndMeasure()
    {
        var w = Open();
        var h = Card(w).ActualHeight;
        Close(w);
        return h;
    }

    private static void Close(Window w)
    {
        w.Close();
        Pump();
    }

    private static Border Card(OverlayWindow w) => (Border)w.FindName("BetaCard")!;

    private static List<OverlayWindow.PollOptionVm> Rows(ItemsControl poll) =>
        poll.Items.Cast<OverlayWindow.PollOptionVm>().ToList();

    private static string Scores(List<OverlayWindow.PollOptionVm> rows) =>
        string.Join("|", rows.Select(r => r.Score));

    /// Нажать вариант — той же кнопкой, что нажимает человек.
    private static void Click(ItemsControl poll, int index)
    {
        var holder = (DependencyObject)poll.ItemContainerGenerator.ContainerFromIndex(index)!;
        var button = Find<System.Windows.Controls.Button>(holder)
                     ?? throw new InvalidOperationException($"у варианта {index} нет кнопки");
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } deeper) return deeper;
        }
        return null;
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Показать окно так, чтобы человек его не увидел: прозрачность 0 до
    /// показа (у оверлея попиксельная прозрачность и так включена) и старт
    /// за краем экрана. Раскладка и обход дерева работают как обычно.
    private static void ShowHidden(Window w)
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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
