using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Counterplay;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;

namespace Counterplay.Tests;

/// <summary>
/// Кнопка «Пул», пока личных пулов нет.
///
/// Владелец (13.11): «повысим интерес добавить пул: если не добавлено ни
/// одного пула — выбран “Обычный”, а вместо названия зелёная надпись тегом
/// +3% win rate (если можно посчитать; если расчёт сложный — не надо); кнопка
/// пула с небольшой зелёной подсветкой по периметру, по нажатию — окно
/// настроек пула; когда пул уже есть — переключает на пул, как раньше».
///
/// Меряет:
/// - цифра (PoolGain) на подставных числах: винрейт пяти самых игранных против
///   остальных; мало игр, разница не в плюс или в пределах ошибки — цифры нет;
///   чемпионы без игр в «свои» не идут;
/// - окно: без пулов — выбран «Обычный», вместо «Пул» зелёный тег «+N% WR» по
///   журналу, зелёная подсветка, в подсказке оба винрейта; клик открывает
///   настройки пулов;
/// - игр мало — тег «+ Пул» и подсказка без цифры;
/// - пул появился — обычная кнопка без подсветки, клик включает пул, окна нет;
/// - смена языка переводит тег и подсказку;
/// - строки на 15 языках с теми же подстановками.
/// Своя папка, пулы — во временной, окна — невидимые.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static readonly string[] NewKeys = ["pool.ctaGain", "pool.ctaTipGain", "pool.ctaTip"];

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PoolCta");
        try { Directory.Delete(root, true); } catch { }
        Directory.CreateDirectory(root);
        AppPaths.RootOverride = root;
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Log.FileDisabled = true;
        Loc.Init();               // английский — запасной, грузится только здесь
        Loc.SetLanguage("ru");

        Strings();
        Math();

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { Window(); }
        finally
        {
            SessionTracker.PreviewGames = null;
            app.Shutdown();
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: без пулов кнопка «Пул» зовёт собрать пул, с пулом — как раньше"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Цифра на подставных числах ────────────────────────────────────────

    private static void Math()
    {
        Console.WriteLine("цифра «насколько чаще на своих»:");
        // Пятеро своих: 60 игр, 36 побед (60%); остальные четверо: 40 игр, 18 побед (45%).
        var (order, stats) = Stats([(1, 12, 7), (2, 12, 7), (3, 12, 7), (4, 12, 7), (5, 12, 8),
                                    (6, 10, 4), (7, 10, 4), (8, 10, 5), (9, 10, 5)]);
        var r = PoolGain.Compute(order, stats);
        Check("60% на своих против 45% — тег +15", r is { Gain: 15, Top: 5, TopGames: 60, RestGames: 40 }
              && System.Math.Abs(r.TopWr - 60) < 1e-9 && System.Math.Abs(r.RestWr - 45) < 1e-9,
              r is null ? "нет цифры" : $"+{r.Gain}: {r.TopWr:F1}% ({r.TopGames}) против {r.RestWr:F1}% ({r.RestGames})");

        var (o2, s2) = Stats([(1, 3, 3), (2, 3, 3), (3, 3, 3), (4, 3, 3), (5, 3, 3), (6, 40, 18)]);
        Check($"своих игр меньше {PoolGain.MinGames} — цифры нет", PoolGain.Compute(o2, s2) is null, "");

        var (o3, s3) = Stats([(1, 12, 6), (2, 12, 6), (3, 12, 6), (4, 12, 6), (5, 12, 6), (6, 40, 24)]);
        Check("на своих выигрывает реже — цифры нет", PoolGain.Compute(o3, s3) is null, "");

        // 56% на 25 играх против 48% на 25: разница 8, ошибка разницы ~14 — шум.
        var (o4, s4) = Stats([(1, 5, 3), (2, 5, 3), (3, 5, 3), (4, 5, 3), (5, 5, 2), (6, 25, 12)]);
        Check("разница в пределах ошибки выборки — цифры нет", PoolGain.Compute(o4, s4) is null, "");

        // В порядке стоит чемпион без игр в окне — в «свои» он не идёт, берётся шестой.
        var (o5, s5) = Stats([(1, 12, 7), (2, 12, 7), (3, 12, 7), (4, 12, 7), (5, 12, 8),
                              (6, 10, 4), (7, 10, 4), (8, 10, 5), (9, 10, 5)]);
        var withGhost = new List<int> { 1, 42, 2, 3, 4, 5, 6, 7, 8, 9 };
        var r5 = PoolGain.Compute(withGhost, s5);
        Check("чемпион без игр в «свои» не идёт", r5 is { Top: 5, TopGames: 60 }, r5 is null ? "нет цифры" : $"{r5.Top} своих, {r5.TopGames} игр");
    }

    private static (List<int> Order, Dictionary<int, (int Games, int Wins)> Stats) Stats(
        (int Id, int Games, int Wins)[] rows) =>
        ([.. rows.OrderByDescending(x => x.Games).Select(x => x.Id)],
         rows.ToDictionary(x => x.Id, x => (x.Games, x.Wins)));

    // ── 2. Окно ──────────────────────────────────────────────────────────────

    private static void Window()
    {
        Console.WriteLine();
        Console.WriteLine("кнопка «Пул» на экране готовности:");
        var s = AppSettings.Current;
        s.ReadyPool = true; s.DraftEnabled = true;
        SessionTracker.PreviewGames = Month(topWins: 36, restWins: 18);   // 60% против 45%

        var w = new OverlayWindow();
        ShowHidden(w);
        PoolSettingsWindow? opened = null;
        w.ShowPoolSettings = win => { opened = (PoolSettingsWindow)win; ShowHidden(win); };
        w.ShowReady(Loc.T("status.readyIdle"));
        Pump();

        var btn   = (Button)w.FindName("PoolPoolBtn");
        var label = (TextBlock)w.FindName("PoolPoolLabel");
        var cta   = (TextBlock)w.FindName("PoolPoolCta");
        var row   = (FrameworkElement)w.FindName("PoolButtonsRow");
        Check("кнопки пула на экране (игры с драфтом есть)", row.Visibility == Visibility.Visible, "");
        Check("пулов нет — выбран «Обычный»", PoolStore.Current().ActiveKind == PoolKind.Normal, $"{PoolStore.Current().ActiveKind}");
        Check("вместо «Пул» — зелёный тег «+15% WR»",
              label.Visibility == Visibility.Collapsed && cta.Visibility == Visibility.Visible
              && cta.Text == Loc.T("pool.ctaGain", 15) && IsGreen(cta.Foreground),
              $"«{cta.Text}»");
        Check("по периметру — зелёная подсветка", btn.Effect is DropShadowEffect { } fx && fx.Color == Color.FromRgb(0x57, 0xC9, 0x8A)
              && IsGreen(btn.BorderBrush), btn.Effect?.GetType().Name ?? "нет");
        var tip = btn.ToolTip as string ?? "";
        Check("в подсказке — оба винрейта и число игр", tip.Contains("60%") && tip.Contains("45%")
              && tip.Contains("(60)") && tip.Contains("(40)"), tip);

        Click(btn);
        Pump();
        Check("клик без пулов — окно настроек пулов", opened is not null, "");
        opened?.Close();
        opened = null;

        // Игр мало — тег без цифры.
        SessionTracker.PreviewGames = Month(topWins: 36, restWins: 18, restGames: 4);
        w.RefreshPoolMode();
        Pump();
        Check("игр мало — тег «+ Пул» без цифры", cta.Visibility == Visibility.Visible
              && cta.Text == "+ " + Loc.T("pool.pool") && (btn.ToolTip as string) == Loc.T("pool.ctaTip"),
              $"«{cta.Text}» / {btn.ToolTip}");

        // Смена языка — тег и подсказка переводятся (тег и подсказка ставятся
        // из кода, привязка их сама не переведёт).
        var ruTip = btn.ToolTip as string;
        Loc.SetLanguage("en");
        Pump();
        Check("смена языка: тег и подсказка по-английски", (btn.ToolTip as string) == Loc.T("pool.ctaTip")
              && (btn.ToolTip as string) != ruTip && cta.Text == "+ Pool",
              $"«{cta.Text}» / {btn.ToolTip}");
        Loc.SetLanguage("ru");
        Pump();

        // Пул появился — кнопка обычная, клик включает пул, окна нет.
        var pools = PoolStore.Current();
        pools.Pools.Add(new ChampPool { Name = "Тест", ByRole = new() { ["support"] = [37, 117] } });
        PoolStore.Persist();
        w.RefreshPoolMode();
        Pump();
        Check("пул есть — снова «Пул», без тега и подсветки",
              label.Visibility == Visibility.Visible && cta.Visibility == Visibility.Collapsed
              && btn.Effect is null && btn.ToolTip is null, $"эффект {btn.Effect?.GetType().Name ?? "нет"}");
        Click(btn);
        Pump();
        Check("клик с пулом — включён пул, окна настроек нет",
              opened is null && PoolStore.Current().ActiveKind == PoolKind.Pool, $"{PoolStore.Current().ActiveKind}");

        w.Close();
    }

    /// Месяц игр: пятеро своих по 12 игр (60 всего) и четверо прочих.
    private static List<SessionTracker.PreviewGame> Month(int topWins, int restWins, int restGames = 40)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();
        void Add(int champ, int n, ref int wins)
        {
            for (var i = 0; i < n; i++)
            {
                games.Add(new("", "", "solo", champ, 0, "support", "", now - 3600 * (games.Count + 1), wins > 0));
                wins--;
            }
        }
        var tw = topWins;
        foreach (var c in new[] { 37, 117, 267, 350, 201 }) Add(c, 12, ref tw);
        var rw = restWins;
        var per = restGames / 4;
        foreach (var c in new[] { 22, 161, 412, 89 }) Add(c, per, ref rw);
        return games;
    }

    private static bool IsGreen(Brush? b) =>
        b is SolidColorBrush { Color: var c } && c.G > c.R + 40 && c.G > c.B + 20;

    private static void Click(Button b) =>
        b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));

    // ── 3. Строки ────────────────────────────────────────────────────────────

    private static void Strings()
    {
        Console.WriteLine("строки на всех языках:");
        var dir = Path.Combine(AppContext.BaseDirectory, "assets", "i18n");
        if (!Directory.Exists(dir)) dir = Path.Combine(Repo(), "assets", "i18n");
        var docs = Directory.GetFiles(dir, "*.json")
            .ToDictionary(Path.GetFileNameWithoutExtension, f => JsonDocument.Parse(File.ReadAllText(f)).RootElement);
        Check("языков 15", docs.Count == 15, $"{docs.Count}");
        foreach (var key in NewKeys)
        {
            var holes = Holes(Get(docs["ru"], key));
            var bad = docs.Where(kv => Get(kv.Value, key) is not { } v || !Holes(v).SequenceEqual(holes))
                          .Select(kv => kv.Key).ToList();
            var english = docs.Where(kv => kv.Key != "en" && Get(kv.Value, key) is { Length: > 12 } v
                                           && v == Get(docs["en"], key)).Select(kv => kv.Key).ToList();
            Check($"  {key}", bad.Count == 0 && english.Count == 0,
                  bad.Count > 0 ? string.Join(",", bad) : english.Count > 0 ? "по-английски: " + string.Join(",", english)
                  : $"{holes.Count} подстановок");
        }
    }

    private static List<string> Holes(string? s) =>
        [.. Regex.Matches(s ?? "", @"\{\d\}").Select(m => m.Value).OrderBy(x => x)];

    private static string? Get(JsonElement root, string key)
    {
        var e = root;
        foreach (var part in key.Split('.'))
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(part, out e)) return null;
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }

    private static string Repo()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? ".";
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(40);
        }
    }

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
    private static void ShowHidden(System.Windows.Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        if (!w.AllowsTransparency)
        {
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
        }
        w.Opacity = 0;
        w.Show();
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
