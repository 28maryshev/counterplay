using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Counterplay;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTip = System.Windows.Controls.ToolTip;

namespace Counterplay.Tests;

/// <summary>
/// Карточка чемпиона при наведении на слот состава.
///
/// Владелец: «при наведении на персонажа из союзников и врагов показывать
/// небольшое окно со статистикой: винрейт и банрейт, позиция в тир-листе,
/// контрпики с винрейтами против него и синергии с винрейтом».
///
/// Что держит проверка:
///  1. место в карточке — то же, что в тир-листе (номер, грейд, сколько в роли),
///     у каждого чемпиона тир-листа;
///  2. контрпики — те же, что иконки под портретом врага (три иконки входят в
///     пять строк карточки), и ни одного проигрышного;
///  3. напарники — с других ролей, без повторов, и та же выборка, что у иконок
///     под портретом союзника;
///  4. в окне: подсказка есть у каждого слота с чемпионом и нет у пустого,
///     союзникам встаёт справа, врагам слева (к центру окна), выключатель в
///     настройках работает, повторное наведение берёт карточку из кэша;
///  5. на всех 15 языках подписи переведены и не вылезают за свои плитки.
///
/// Подсказку не открываем — это отдельное окно на экране. Шаблон карточки
/// раскладываем сами, как его разложила бы подсказка. Нет базы — проверять
/// нечего.
/// </summary>
internal static class Program
{
    private static int _fails;

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к хранилищам.
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "ChampTip");
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
        Loc.Init();              // без него нет английского — запасного для всех языков
        Loc.SetLanguage("ru");

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — проверять нечем"); return 0; }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        Assets(playerDir);
        Console.WriteLine($"база: {db}; патч {engine.Patch}");

        EngineNumbers(engine);

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        InWindow(engine);
        app.Shutdown();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: карточка чемпиона в порядке"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1–3. Числа карточки против тир-листа и иконок под портретами ────────
    private static void EngineNumbers(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("── числа движка ──");
        var tier = engine.TierList(perRole: 1000);

        var badSpot = new List<string>();
        foreach (var g in tier.GroupBy(t => t.Role))
        {
            var list = g.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var s = engine.TierSpot(list[i].ChampionId, g.Key);
                if (s is not { } v || v.Place != i + 1 || v.Of != list.Count || v.Grade != list[i].Grade)
                    badSpot.Add($"{DataDragon.Name(list[i].ChampionId)} ({g.Key}): тир-лист " +
                                $"{list[i].Grade} {i + 1}/{list.Count}, карточка {s}");
            }
        }
        Check($"место в тир-листе как в самом тир-листе — у всех {tier.Count}",
              tier.Count > 0 && badSpot.Count == 0, Few(badSpot));

        var notIcons = new List<string>();
        var losing   = new List<string>();
        var sameRole = new List<string>();
        var dups     = new List<string>();
        var aliens   = new List<string>();
        var counts   = new List<(int C, int P)>();
        foreach (var t in tier)
        {
            var name = $"{DataDragon.Name(t.ChampionId)} ({t.Role})";
            var c = engine.CountersWithWr(t.ChampionId, t.Role, 5);
            var icons = engine.TopCounters(t.ChampionId, t.Role, 3, fill: true);
            if (!icons.All(id => c.Any(x => x.Id == id)))
                notIcons.Add($"{name}: иконки {Names(icons)}, карточка {Names(c.Select(x => x.Id))}");
            losing.AddRange(c.Where(x => x.Wr < 0.49 || x.Wr >= 1 || x.Role != t.Role)
                             .Select(x => $"{DataDragon.Name(x.Id)} против {name}: {100 * x.Wr:F1}%"));

            var p = engine.PartnersWithWr(t.ChampionId, t.Role, 5);
            sameRole.AddRange(p.Where(x => x.Role == t.Role).Select(x => $"{DataDragon.Name(x.Id)} у {name}"));
            if (p.GroupBy(x => (x.Id, x.Role)).Any(x => x.Count() > 1)) dups.Add(name);
            // Иконки под союзником — его напарники на МОЕЙ роли. Здесь «моя» —
            // роль напарника: та же выборка, только по всем ролям сразу.
            aliens.AddRange(p.Where(x => !engine.TopSynergies(t.ChampionId, x.Role, 5, t.Role).Contains(x.Id))
                             .Select(x => $"{DataDragon.Name(x.Id)} ({x.Role}) у {name}"));
            counts.Add((c.Count, p.Count));
        }
        Check("три иконки контр входят в пять строк карточки", notIcons.Count == 0, Few(notIcons));
        Check("контрпики не проигрывают ему и с его линии", losing.Count == 0, Few(losing));
        Check("напарники — с других ролей", sameRole.Count == 0, Few(sameRole));
        Check("напарники без повторов", dups.Count == 0, Few(dups));
        Check("напарники — та же выборка, что у иконок союзника", aliens.Count == 0, Few(aliens));
        Console.WriteLine($"  контрпиков по пять у {counts.Count(x => x.C == 5)} из {counts.Count}, " +
                          $"напарников по пять у {counts.Count(x => x.P == 5)}");
        Check("у тир-листа почти всегда полные списки (90%)",
              counts.Count(x => x.C == 5 && x.P == 5) >= 0.9 * counts.Count,
              $"{counts.Count(x => x.C == 5 && x.P == 5)} из {counts.Count}");
    }

    // ── 4–5. В окне ─────────────────────────────────────────────────────────
    private static void InWindow(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("── в окне ──");
        var s = AppSettings.Current;
        s.DraftChampTip = true;
        s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        Pump();
        var draft = Scene();
        w.UpdateRecommendations(engine.Recommend(draft, s.DraftCount), draft, engine);
        Pump();

        var allies  = Hosts(w, "MyTeamList");
        var enemies = Hosts(w, "EnemyTeamList");
        Check("у каждого слота есть подсказка", allies.Count == 5 && enemies.Count == 5,
              $"союзников {allies.Count}, врагов {enemies.Count}");

        var slow = 0L;
        foreach (var (host, ally) in allies.Select(h => (h, true)).Concat(enemies.Select(h => (h, false))))
        {
            var card = (ChampSlotCard)host.DataContext;
            var sw = Stopwatch.StartNew();
            var (shown, tip) = Open(host);
            slow = Math.Max(slow, sw.ElapsedMilliseconds);
            var name = card.ChampionId > 0 ? DataDragon.Name(card.ChampionId) : "пустой";
            if (card.ChampionId == 0)
            {
                Check($"{name} слот — без подсказки", !shown, "");
                continue;
            }
            var vm = tip.Content as ChampTipVm;
            Console.WriteLine($"  {name,-12} {card.StatsRole,-8} {vm?.WrText,6} {vm?.DeltaText,5}  бан {vm?.BanText,5}  " +
                              $"{vm?.Grade,2} {vm?.PlaceText,-12} контр {vm?.Counters.Count}, напарников {vm?.Synergies.Count}");
            Check($"{name}: карточка есть", shown && vm is not null, "");
            if (vm is null) continue;
            Check($"{name}: встаёт к центру ({(ally ? "справа" : "слева")})",
                  tip.Placement == (ally ? PlacementMode.Right : PlacementMode.Left) && tip.PlacementTarget == host,
                  $"{tip.Placement}");
            var role = card.StatsRole;
            Check($"{name}: роль слота — {role}", vm.RoleText == Loc.T($"roles.{role}"), vm.RoleText);
            var patch = engine.PatchWr(card.ChampionId, role);
            Check($"{name}: винрейт как в патче", vm.WrText == (patch is { } p ? $"{p.Wr:F1}%" : "—"), vm.WrText);
            Check($"{name}: бан-рейт", vm.BanText == $"{engine.BanRate(card.ChampionId):F1}%", vm.BanText);
            Check($"{name}: строки по винрейту, сверху вниз",
                  Sorted(vm.Counters) && Sorted(vm.Synergies), "");
            var (again, tip2) = Open(host);
            Check($"{name}: второе наведение — из кэша", again && ReferenceEquals(tip2.Content, vm), "");
        }
        Console.WriteLine($"  самая долгая карточка: {slow} мс");
        Check("карточка собирается быстрее секунды", slow < 1000, $"{slow} мс");

        // Враги в соло/дуо приходят без ролей — слот берёт угаданную.
        Check("у врагов роль угадана", enemies.All(h => ((ChampSlotCard)h.DataContext).StatsRole.Length > 0),
              string.Join(", ", enemies.Select(h => ((ChampSlotCard)h.DataContext).StatsRole)));

        s.DraftChampTip = false;
        Check("выключено в настройках — подсказки нет", !Open(allies[0]).Shown, "");
        s.DraftChampTip = true;

        // Языки: карточку собираем заново (кэш сбрасывается со сменой языка) и
        // раскладываем шаблон, как его разложила бы подсказка.
        var ruFooter = ((ChampTipVm)Open(allies[0]).Tip.Content).Footer;
        foreach (var lang in Loc.Languages.Select(l => l.Code))
        {
            Loc.SetLanguage(lang);
            Pump();
            var hosts = Hosts(w, "MyTeamList").Concat(Hosts(w, "EnemyTeamList"))
                .Where(h => ((ChampSlotCard)h.DataContext).ChampionId > 0).ToList();
            var bad = new List<string>();
            foreach (var h in hosts)
            {
                var (_, tip) = Open(h);
                if (tip.Content is not ChampTipVm vm) { bad.Add("нет карточки"); continue; }
                bad.AddRange(Overflow(tip.ContentTemplate, vm));
            }
            Check($"{lang}: переведено и влезает", bad.Count == 0, Few(bad.Distinct().ToList()));
        }
        Loc.SetLanguage("ru");
        Pump();
        Loc.SetLanguage("en");
        Pump();
        var enFooter = ((ChampTipVm)Open(Hosts(w, "MyTeamList")[0]).Tip.Content).Footer;
        Check("смена языка собирает карточку заново", enFooter != ruFooter, $"«{ruFooter}» → «{enFooter}»");
        Loc.SetLanguage("ru");
        w.Close();
    }

    /// Раскладывает шаблон карточки и ищет непереведённые ключи и подписи,
    /// которые не влезли: за край своей плитки или сжатые соседом.
    private static List<string> Overflow(DataTemplate template, ChampTipVm vm)
    {
        var cp = new ContentPresenter { Content = vm, ContentTemplate = template };
        cp.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        cp.Arrange(new Rect(cp.DesiredSize));
        cp.UpdateLayout();

        var bad = new List<string>();
        var texts = Descendants<TextBlock>(cp).ToList();
        foreach (var tb in texts)
        {
            if (tb.Text.Contains("champTip.")) bad.Add($"нет перевода: {tb.Text}");
            if (tb.TextTrimming != TextTrimming.None || tb.TextWrapping != TextWrapping.NoWrap
                || tb.Text.Length == 0) continue;
            // Плитка или вся карточка — что ближе.
            FrameworkElement box = cp;
            for (var d = VisualTreeHelper.GetParent(tb); d is not null; d = VisualTreeHelper.GetParent(d))
                if (d is Border { Padding.Right: > 0 } b && b.Background is not null) { box = b; break; }
            var right = tb.TranslatePoint(new Point(tb.ActualWidth, 0), box).X;
            var limit = box.ActualWidth - (box is Border bb ? bb.Padding.Right : 0) + 0.5;
            if (right > limit) bad.Add($"«{tb.Text}» за краем на {right - limit:F0}px");
        }
        // Сжатые соседом (подпись столбца рядом с заголовком): своя ширина
        // больше выданной. Мерим после раскладки — она уже не нужна.
        foreach (var tb in texts.Where(t => t.TextTrimming == TextTrimming.None
                                            && t.TextWrapping == TextWrapping.NoWrap && t.Text.Length > 0))
        {
            var given = tb.ActualWidth;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var natural = tb.DesiredSize.Width - tb.Margin.Left - tb.Margin.Right;   // отступ входит в DesiredSize
            if (natural > given + 0.5) bad.Add($"«{tb.Text}» сжат до {given:F0}px из {natural:F0}");
        }
        return bad;
    }

    private static bool Sorted(List<ChampTipRow> rows)
    {
        var wr = rows.Select(r => double.Parse(r.WrText.TrimEnd('%'), System.Globalization.CultureInfo.CurrentCulture)).ToList();
        return wr.Zip(wr.Skip(1)).All(x => x.First >= x.Second);
    }

    /// Наводим на слот: поднимаем то же событие, что подсказка перед открытием.
    /// Конструктор аргументов у WPF внутренний — берём его отражением.
    private static (bool Shown, ToolTip Tip) Open(FrameworkElement host)
    {
        var args = (ToolTipEventArgs)typeof(ToolTipEventArgs)
            .GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, [typeof(bool)], null)!
            .Invoke([true]);
        args.RoutedEvent = ToolTipService.ToolTipOpeningEvent;
        host.RaiseEvent(args);
        return (!args.Handled, (ToolTip)host.ToolTip);
    }

    /// Строки состава, у которых шаблон слота повесил карточку.
    private static List<FrameworkElement> Hosts(Window w, string list)
    {
        var items = (ItemsControl)w.FindName(list);
        return [.. Enumerable.Range(0, items.Items.Count)
            .Select(i => items.ItemContainerGenerator.ContainerFromIndex(i) as DependencyObject)
            .Where(c => c is not null)
            .SelectMany(c => Descendants<FrameworkElement>(c!))
            .Where(e => e.ToolTip is ToolTip { ContentTemplate: not null })];
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject d) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) yield return t;
            foreach (var x in Descendants<T>(c)) yield return x;
        }
    }

    /// Сцена со скриншота владельца: враги Джейс, Ноктюрн, Талон, Зиггс, Брэнд
    /// в соло/дуо (роли клиент не раскрывает); у нас — Гарен, Ли Син, Ари,
    /// Джинкс, а я в саппорте ещё не выбрал.
    private static DraftState Scene()
    {
        var my = new List<DraftPlayer>
        {
            new(0, 86,  0, "top", false),      // Гарен
            new(1, 64,  0, "jungle", false),   // Ли Син
            new(2, 103, 0, "middle", false),   // Ари
            new(3, 222, 0, "bottom", false),   // Джинкс
            new(4, 0,   0, "utility", true),   // я — ещё не выбрал
        };
        var their = new List<DraftPlayer>
        {
            new(5, 126, 0, "", false), new(6, 56, 0, "", false), new(7, 91, 0, "", false),
            new(8, 115, 0, "", false), new(9, 63, 0, "", false),
        };
        return new DraftState(
            MyTeam: my, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: my[4], MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false, MyBanDone: true);
    }

    private static string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(DataDragon.Name));

    private static string Few(List<string> xs) =>
        xs.Count == 0 ? "" : string.Join("; ", xs.Take(5)) + (xs.Count > 5 ? $" … и ещё {xs.Count - 5}" : "");

    /// Имена и иконки из папки игрока — копией в свою папку.
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
            Thread.Sleep(40);
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
