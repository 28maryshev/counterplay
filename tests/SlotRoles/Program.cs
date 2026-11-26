using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Роль в слоте моей команды (владелец, 26.11.2026: «нужно чтобы было
/// подписано в моей команде какая роль у каждого»). До этого роль писалась
/// только у тех, кто уже навёл или взял чемпиона; у остальных союзников — «—»
/// без подписи, хотя роль клиент раздаёт с начала драфта.
///
/// Меряет:
/// - у союзника без чемпиона роль подписана (модель слота и текст на экране);
/// - у взявших — как раньше; мой пустой слот — «ТЫ ВЫБИРАЕШЬ» (роль там значком);
/// - вслепую (роль не раскрыта) у пустого союзника подписи нет — не «—»;
/// - у врага без чемпиона подписи нет: его роль неизвестна.
/// Модель слота проверяется всегда, текст на экране — если есть база.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SlotRoles");
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Console.WriteLine("модель слота:");
        Model();

        var db = RecommendationEngine.FindDb();
        if (db is null) Console.WriteLine("базы нет — окно не проверялось");
        else
        {
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Console.WriteLine("окно:");
            Window(engine);
            app.Shutdown();
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: роль подписана у каждого союзника, и до пика тоже"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── Модель слота ───────────────────────────────────────────────────────

    private static void Model()
    {
        var build = typeof(OverlayWindow).GetMethod("BuildSlots", BindingFlags.NonPublic | BindingFlags.Static)
                    ?? throw new InvalidOperationException("BuildSlots не найден");
        List<ChampSlotCard> Build(IReadOnlyList<DraftPlayer> players, bool ally) =>
            (List<ChampSlotCard>)build.Invoke(null,
                [players, ally, null, "support", null, null, -1, null, null])!;

        // Никто ещё не навёл: я — саппорт, остальные с ролями от клиента.
        var allies = Build(
        [
            new(0, 0, 0, "top", false), new(1, 0, 0, "jungle", false),
            new(2, 0, 0, "middle", false), new(3, 0, 0, "bottom", false),
            new(4, 0, 0, "utility", true),
        ], ally: true);
        Check("пустые союзники: ТОП, ДЖУНГЛИ, МИД, БОТ",
              allies.Take(4).Select(s => s.Role).SequenceEqual(["ТОП", "ДЖУНГЛИ", "МИД", "БОТ"]),
              string.Join(", ", allies.Select(s => $"«{s.Role}»")));
        Check("мой пустой слот — «ТЫ ВЫБИРАЕШЬ»", allies[4].Role == Loc.T("slot.youPick"), $"«{allies[4].Role}»");

        var blind = Build([new(0, 0, 0, "", false), new(1, 0, 0, "", true)], ally: true);
        Check("вслепую у пустого союзника подписи нет", blind[0].Role == "", $"«{blind[0].Role}»");

        var enemies = Build([new(5, 0, 0, "", false), new(6, 0, 0, "jungle", false)], ally: false);
        Check("у пустых врагов подписи нет", enemies.All(s => s.Role == ""),
              string.Join(", ", enemies.Select(s => $"«{s.Role}»")));
    }

    // ── Окно ───────────────────────────────────────────────────────────────

    private static void Window(RecommendationEngine engine)
    {
        var w = new OverlayWindow { FollowClient = false };   // настоящий клиент LoL проверке не указ
        ShowHidden(w);
        w.SetEngine(engine);
        var d = Scene();
        w.UpdateRecommendations(engine.Recommend(d, AppSettings.Current.DraftCount), d, engine);
        Pump();

        var list = (ItemsControl)w.FindName("MyTeamList");
        Check("взявшие: роль на экране", ShownText(list, 0).Contains("БОТ") && ShownText(list, 1).Contains("ДЖУНГЛИ"),
              $"{string.Join(" ", ShownText(list, 0))} | {string.Join(" ", ShownText(list, 1))}");
        Check("ещё не выбрали: роль на экране", ShownText(list, 3).Contains("ТОП") && ShownText(list, 4).Contains("ПОДДЕРЖКА"),
              $"{string.Join(" ", ShownText(list, 3))} | {string.Join(" ", ShownText(list, 4))}");
        Check("мой слот: «ТЫ ВЫБИРАЕШЬ»", ShownText(list, 2).Contains(Loc.T("slot.youPick")),
              string.Join(" ", ShownText(list, 2)));
        w.Close();
    }

    // Соло/дуо, как на снимке владельца: бот и лес взяли, я — мид и мой ход,
    // топ и саппорт ещё никого не навели.
    private static DraftState Scene()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 222, 0, "bottom", false),
            new(1, 33,  0, "jungle", false),
            new(2, 0,   0, "middle", true),
            new(3, 0,   0, "top", false),
            new(4, 0,   0, "utility", false),
        };
        var their = new List<DraftPlayer>
        {
            new(5, 238, 0, "", false), new(6, 0, 0, "", false),
            new(7, 0, 0, "", false), new(8, 0, 0, "", false), new(9, 0, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[2], MyPosition: "middle", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: 7, MyPickInProgress: true, ActiveCells: [2],
            FirstPickCell: 0, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
    }

    // Тексты, которые сейчас нарисованы в строке слота.
    private static List<string> ShownText(ItemsControl list, int i)
    {
        var found = new List<string>();
        void Walk(DependencyObject d)
        {
            if (d is UIElement { IsVisible: false }) return;
            if (d is TextBlock tb && tb.Text.Length > 0) found.Add(tb.Text);
            for (var k = 0; k < VisualTreeHelper.GetChildrenCount(d); k++) Walk(VisualTreeHelper.GetChild(d, k));
        }
        if (list.ItemContainerGenerator.ContainerFromIndex(i) is { } row) Walk(row);
        return found;
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
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
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? " — " + detail : "")}");
        if (!ok) _fails++;
    }
}
