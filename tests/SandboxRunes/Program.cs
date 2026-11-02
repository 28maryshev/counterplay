using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Песочница показывает руны и сборку после «Выбрать» (владелец, 02.11: «в
/// тестовой среде не отображаются руны»).
///
/// С 16.09 руны и сборка ждут подтверждённого пика (`Me.ChampionId`), а
/// песочница отдавала моего чемпиона только наведением (`PickIntentId`) — и
/// после «Выбрать» тоже. Панели рун в песочнице не было полтора месяца.
///
/// 1. Навёл (как клик по карточке) — чемпион наведён, рун нет, «Выбрать» есть.
/// 2. «Выбрать» (обработчик лока песочницы) — чемпион взят, мой ход сделан
///    (как у клиента: действия пика больше нет), руны и сборка на экране.
/// 3. Враги выбирают — шкала подбора растёт; пятый — во второй строке предметы.
/// 4. Сменил чемпиона руками — снова наведение, руны ушли.
/// 5. Обработчик лока, который ставит песочница, подтверждает пик (а не наводит).
///
/// Панель песочницы и окно — невидимые; руны — макет; база матчей настоящая,
/// только чтение; данные песочницы — в своей папке.
/// </summary>
internal static class Program
{
    private static int _fails;
    private const int Shen = 98, Braum = 201;
    private static readonly int[] Enemies = [54, 64, 103, 222, 412];

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "SandboxRunes");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        try
        {
            Source();

            var db = RecommendationEngine.FindDb();
            if (db is null) { Console.WriteLine("базы нет — песочницу проверять нечем"); return Done(); }
            using var engine = RecommendationEngine.Create(db, "emerald");
            engine.Mastery = new Dictionary<int, long>();

            Assets(playerDir);
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Sandbox(engine);
            app.Shutdown();
        }
        finally
        {
            RunesClient.UseMock = false;
            AppPaths.RootOverride = null;
        }
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: в песочнице после «Выбрать» есть руны и сборка"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 5. Обработчик лока песочницы ─────────────────────────────────────────

    /// Обработчик ставит TestMode вместе с сетью и справочниками — целиком его
    /// здесь не поднять. Поэтому смотрим исходник всей папки песочницы:
    /// присваивание LockHandler должно подтверждать пик.
    private static void Source()
    {
        Console.WriteLine("обработчик лока песочницы:");
        var dir = Path.Combine(Repo(), "src", "Dev", "Sandbox");
        var lines = Directory.GetFiles(dir, "*.cs")
            .SelectMany(f => File.ReadAllLines(f).Select(l => (File: Path.GetFileName(f), Line: l)))
            .Where(x => x.Line.Contains("LockHandler") && x.Line.Contains("=>") && !x.Line.Contains("Ban"))
            .ToList();
        Check("«Выбрать» в песочнице подтверждает пик (LockMy), а не наводит (SetMy)",
              lines.Count > 0 && lines.All(x => x.Line.Contains("LockMy")),
              string.Join(" | ", lines.Select(x => $"{x.File}: {x.Line.Trim()}")));
    }

    // ── 1–4. Песочница ───────────────────────────────────────────────────────

    private static void Sandbox(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("песочница:");
        var s = AppSettings.Current;
        s.DraftRunes = true;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var asm = typeof(OverlayWindow).Assembly;
        Counterplay.Sandbox.Active = true;
        var mode = asm.GetType("Counterplay.TestMode")!;
        mode.GetMethod("EnterSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [engine]);
        RunesClient.UseMock = true;

        var overlay = new OverlayWindow();
        ShowHidden(overlay);
        overlay.SetEngine(engine);
        var panelType = asm.GetType("Counterplay.TestPanel")!;
        var panel = (Window)Activator.CreateInstance(panelType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [overlay, engine, DataDragon.GetAllIconUrls().Keys.ToList(), false, false, false], null)!;
        Pump();

        DraftState? Last() => Get<DraftState>(overlay, "_lastDraft");
        var rows = (string[])panelType.GetField("_rowRoles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(panel)!;
        var radios = Get<RadioButton[]>(panel, "_meRadio")!;
        radios[Array.IndexOf(rows, "utility")].IsChecked = true;
        Click(Get<Button>(panel, "_stageDraft")!);
        Pump();

        // 1. Наведение.
        Call(panel, "SetMy", Shen);
        Wait(0.5);
        var d = Last();
        Check("навёл — чемпион наведён, мой ход, рун нет",
              d?.Me is { ChampionId: 0, PickIntentId: Shen } && d.MyPickActionId >= 0 && !Vis(overlay, "RunesBar"),
              $"взят {d?.Me?.ChampionId}, наведён {d?.Me?.PickIntentId}, действие {d?.MyPickActionId}, руны {Vis(overlay, "RunesBar")}");

        // 2. «Выбрать».
        Call(panel, "LockMy", Shen);
        var shown = WaitFor(() => Vis(overlay, "RunesBar") && Vis(overlay, "BuildList"), 5);
        d = Last();
        Check("«Выбрать» — чемпион взят, ход сделан, руны и сборка на экране",
              shown && d?.Me is { ChampionId: Shen, PickIntentId: 0 } && d.MyPickActionId == -1 && !d.MyPickInProgress,
              $"взят {d?.Me?.ChampionId}, действие {d?.MyPickActionId}, руны {Vis(overlay, "RunesBar")}, сборка {Vis(overlay, "BuildList")}");

        // 3. Враги выбирают.
        var enemy = Get<ComboBox[]>(panel, "_enemy")!;
        for (var n = 1; n <= 5; n++)
        {
            enemy[n - 1].SelectedItem = DataDragon.Name(Enemies[n - 1]);
            Wait(0.7);
            var row = Row(overlay, 1);
            if (n < 5)
            {
                if (n == 2)
                    Check("враги выбирают — шкала подбора растёт (2 из 5), пик остаётся взятым",
                          row is { Index: -1, FillVis: Visibility.Visible } && Last()?.Me?.ChampionId == Shen,
                          $"строка {row?.Index}, шкала {row?.FillVis}, взят {Last()?.Me?.ChampionId}");
                continue;
            }
            Check("пятый враг — во второй строке предметы",
                  row is { Index: 1, FillVis: Visibility.Collapsed } && row.Slots.Count(x => x.HasTip) == 6,
                  $"строка {row?.Index}, предметов {row?.Slots.Count(x => x.HasTip)}");
        }

        // 4. Сменил руками.
        Call(panel, "SetMy", Braum);
        var gone = WaitFor(() => !Vis(overlay, "RunesBar"), 3);
        d = Last();
        Check("сменил чемпиона — снова наведение, руны ушли",
              gone && d?.Me is { ChampionId: 0, PickIntentId: Braum } && d.MyPickActionId >= 0,
              $"взят {d?.Me?.ChampionId}, наведён {d?.Me?.PickIntentId}, руны {Vis(overlay, "RunesBar")}");

        Call(panel, "ResetDraft");
        panel.Hide();
        overlay.Close();
        Pump();
        mode.GetMethod("LeaveSandboxData", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
    }

    private static OverlayWindow.BuildRowVm? Row(Window w, int i) =>
        (((ItemsControl)w.FindName("BuildList")).ItemsSource as IEnumerable<OverlayWindow.BuildRowVm>)?
        .ElementAtOrDefault(i);

    // ── Общее ────────────────────────────────────────────────────────────────

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    /// Имена, иконки и предметы. Кэш игрока копируем к себе — его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons", "items" })
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
        RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        ItemFacts.LoadAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static T? Get<T>(object o, string field) where T : class =>
        o.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(o) as T;

    private static void Call(object o, string method, params object[] args) =>
        o.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
         .Invoke(o, args);

    private static void Click(Button b) =>
        b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static bool Vis(Window w, string name) =>
        w.FindName(name) is FrameworkElement fe && fe.Visibility == Visibility.Visible;

    private static bool WaitFor(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            Wait(0.25);
        }
        return done();
    }

    private static void Wait(double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) => { t.Stop(); frame.Continue = false; };
        t.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Pump();
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

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
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
