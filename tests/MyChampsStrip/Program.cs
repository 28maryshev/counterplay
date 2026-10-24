using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Лента «мои чемпионы за 30 дней» под кнопками пула.
///
/// Владелец 24 октября: человек без пулов видит под кнопками своих чемпионов
/// или вечный скелетон? Оказалось — при первом запуске скелетон до первой
/// смены фазы: экран рисовался раньше, чем журнал добивался историей клиента,
/// пришедшая история ленту не перестраивала, а статистика чемпионов ещё
/// 20 секунд отдавала пустую карту из кэша. У того, кто играет ARAM, шесть
/// переливающихся слотов висели навсегда.
///
/// Теперь: лента перестраивается, когда пришла история; меньше трёх игр в
/// нормалах и ранкеде среди последних двадцати — ленты нет, остаются кнопки
/// пула; пустые слоты рядом с настоящими чемпионами стоят без блика.
///
/// Своя папка (<c>AppPaths.RootOverride</c>), подставной клиент, окно — невидимое.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Me = "TEST-mychamps-0000000000000000000000000000000000000000000000000000";

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "MyChampsStrip");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            FirstLaunch(w);
            Rules(w);
        }
        finally
        {
            w.Close();
            try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: лента появляется с историей, без игр с драфтом её нет, пустые слоты не мерцают"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Первый запуск ──────────────────────────────────────────────────

    /// <summary>
    /// Как в бою: экран готовности при пустом журнале, затем журнал из истории
    /// клиента (шесть нормалов на трёх чемпионах и четырнадцать ARAM) и показ
    /// профиля. Лента должна наполниться сразу, без смены фазы.
    /// </summary>
    private static void FirstLaunch(OverlayWindow w)
    {
        Console.WriteLine("первый запуск:");
        var now = DateTimeOffset.UtcNow;
        var games = new List<(int, int, bool, DateTimeOffset)>();
        int[] normals = [86, 86, 86, 103, 103, 99];
        for (var i = 0; i < normals.Length; i++) games.Add((400, normals[i], i % 2 == 0, now.AddHours(-2 - i)));
        for (var i = 0; i < 14; i++) games.Add((450, 22, i % 2 == 0, now.AddHours(-10 - i)));

        using var lcu = new FakeLcu(Me, "проверка", games);
        using var http = new LcuHttpClient(lcu.Credentials);

        w.ShowReady(Loc.T("status.readyIdle"));
        Pump();
        Check("до истории журнал пуст — ленты нет, только кнопки пула", !Visible(w), Describe(w));

        var d = Task.Run(() => SessionTracker.RefreshAsync(http, CancellationToken.None)).GetAwaiter().GetResult();
        Check("история клиента пришла", d is not null, d is null ? "профиль не собрался" : "");
        w.ShowSession(d);
        Pump();
        var (champs, empty) = Cards(w);
        Check("пришла история — лента сама наполнилась чемпионами из нормалов",
              Visible(w) && champs == 3 && empty == 3, Describe(w));
    }

    // ── 2. Когда ленты нет ────────────────────────────────────────────────

    private static void Rules(OverlayWindow w)
    {
        Console.WriteLine("\nсколько игр с драфтом:");
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Двадцать последних: две нормалки и восемнадцать ARAM.
        Journal(("normal", Games(now, 3600, 86, 103)), ("aram", Games(now - 7200 * 3, 3600, Many(18))));
        Ready(w);
        Check("2 нормала из последних 20 — ленты нет", !Visible(w), Describe(w));

        // Порог: три нормала на двух чемпионах и семнадцать ARAM.
        Journal(("normal", Games(now, 3600, 86, 86, 103)), ("aram", Games(now - 7200 * 3, 3600, Many(17))));
        Ready(w);
        var (champs, empty) = Cards(w);
        Check("3 нормала из последних 20 — лента есть: два чемпиона и четыре пустых слота",
              Visible(w) && champs == 2 && empty == 4, Describe(w));
        Check("пустые слоты рядом с чемпионами — ровная заливка, без блика", EmptyStill(w), Fills(w));

        // Ранкед считается наравне с нормалами.
        Journal(("solo", Games(now, 3600, 86, 103)), ("flex", Games(now - 3 * 3600, 3600, 99)),
                ("aram", Games(now - 7200 * 3, 3600, Many(17))));
        Ready(w);
        (champs, _) = Cards(w);
        Check("соло и флекс считаются: 3 из последних 20 — лента есть", Visible(w) && champs == 3, Describe(w));

        // Три нормала за месяц есть, но после них двадцать ARAM — в счёт не идут.
        Journal(("normal", Games(now - 10 * 86400, 3600, 86, 86, 103)), ("aram", Games(now, 3600, Many(20))));
        Ready(w);
        Check("нормалы старше последних 20 игр — ленты нет", !Visible(w), Describe(w));
    }

    private static int[] Many(int n) => Enumerable.Repeat(22, n).ToArray();

    /// Игры очереди от <paramref name="newest"/> назад с шагом <paramref name="step"/> секунд.
    private static JsonArray Games(long newest, long step, params int[] champs) =>
        new(champs.Select((c, i) => (JsonNode)new JsonObject
        {
            ["Ts"] = newest - (i + 1) * step, ["ChampionId"] = c, ["Win"] = i % 2 == 0,
        }).ToArray());

    private static void Journal(params (string Queue, JsonArray Games)[] queues)
    {
        var q = new JsonObject();
        foreach (var (name, games) in queues) q[name] = new JsonObject { ["Games"] = games };
        File.WriteAllText(Path.Combine(AppPaths.Root, "session.json"), new JsonObject
        {
            ["LastAccount"] = Me,
            ["Accounts"] = new JsonObject { [Me] = new JsonObject { ["Nick"] = "проверка", ["Queues"] = q } },
        }.ToJsonString());
        // Файл подменён мимо журнала — кэш статистики об этом не знает.
        SessionTracker.DropCache();
    }

    private static void Ready(OverlayWindow w)
    {
        w.ShowReady(Loc.T("status.readyIdle"));
        Pump();
    }

    // ── Что на экране ─────────────────────────────────────────────────────

    private static bool Visible(OverlayWindow w) =>
        ((FrameworkElement)w.FindName("MyChampsBox")).Visibility == Visibility.Visible;

    private static List<MyChampCard> Items(OverlayWindow w) =>
        (((ItemsControl)w.FindName("MyChampsStrip")).ItemsSource as IEnumerable<MyChampCard>)?.ToList() ?? [];

    /// Чемпионы — с цифрой винрейта, пустые слоты — со скелетоном вместо неё.
    private static (int Champs, int Empty) Cards(OverlayWindow w)
    {
        var items = Items(w);
        return (items.Count(c => c.WrSkeleton != Visibility.Visible),
                items.Count(c => c.WrSkeleton == Visibility.Visible));
    }

    private static bool EmptyStill(OverlayWindow w)
    {
        var empty = Items(w).Where(c => c.WrSkeleton == Visibility.Visible).ToList();
        return empty.Count > 0 && empty.All(c => c.Fill is SolidColorBrush { HasAnimatedProperties: false });
    }

    private static string Describe(OverlayWindow w)
    {
        var (champs, empty) = Cards(w);
        return $"лента {(Visible(w) ? "видна" : "скрыта")}, чемпионов {champs}, пустых {empty}";
    }

    private static string Fills(OverlayWindow w) =>
        string.Join(", ", Items(w).Where(c => c.WrSkeleton == Visibility.Visible)
                                  .Select(c => c.Fill?.GetType().Name ?? "null").Distinct());

    private static void Pump()
    {
        for (var i = 0; i < 10; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

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
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
