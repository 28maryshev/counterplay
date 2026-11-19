using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using System.Windows.Media;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Переключение периода в окне пулов и ник на плитке.
///
/// Обе вещи однажды уже подвели. Переключение падало: обработчик пересобирал
/// каркас, а колонки оставались детьми прежней сетки — WPF на это отвечает
/// «элемент уже вложен в другой». Ник не показывался, потому что правило для
/// плитки требовало опознанного напарника, хотя на плитке вопрос другой —
/// «с кем этот пул».
///
/// Открываем НАСТОЯЩЕЕ окно и жмём настоящие кнопки. Свой pools.json и
/// настройки сохраняем и возвращаем на место.
/// </summary>
internal static class Program
{
    private static int _fails;

        /// Файл пулов ПРОВЕРКИ, не игрока. Здесь стоял настоящий `%APPDATA%`, и
    /// проверки его удаляли «на время» — так у владельца и пропали пулы.
    private static string PoolsPath => System.IO.Path.Combine(PoolStore.DirOverride!, "pools.json");

    [STAThread]
    private static int Main()
    {
        // Своя папка вместо папки игрока — до первого обращения к любому
        // хранилищу. Раньше проверка писала в настоящие файлы и «возвращала
        // как было»: правку, сделанную рядом работающей программой, это откатывало.
        AppPaths.RootOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "counterplay-test-root", "PoolWindow");
        System.IO.Directory.CreateDirectory(AppPaths.RootOverride);
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        // Пулы — в свою папку, НЕ в файл игрока. Проверки однажды уже оставили
        // в его pools.json тестовые аккаунты, а свои пулы — пустыми.
        PoolStore.DirOverride = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "counterplay-test-pools", "PoolWindow");
        System.IO.Directory.CreateDirectory(PoolStore.DirOverride);
        // И начинаем с ЧИСТОГО листа: состояние, оставшееся от прошлого
        // запуска, делает проверку неповторяемой. SetFavourite, например,
        // переключает — со звездой от прошлого раза он её гасит, и проверка
        // падает через раз.
        var stale = System.IO.Path.Combine(PoolStore.DirOverride, "pools.json");
        if (System.IO.File.Exists(stale)) System.IO.File.Delete(stale);
        PoolStore.Reload();

        Loc.SetLanguage("ru");

        var before = File.Exists(PoolsPath) ? File.ReadAllText(PoolsPath) : null;
        var wasAllTime = AppSettings.Current.WinratesAllTime;
        // Окно теперь запоминает, как его растянули, — свои значения бережём.
        var wasSplit = AppSettings.Current.PoolSplit;
        var wasGeom = (AppSettings.Current.PoolWinLeft, AppSettings.Current.PoolWinTop,
                       AppSettings.Current.PoolWinWidth, AppSettings.Current.PoolWinHeight);
        try
        {
            if (before is not null) File.Delete(PoolsPath);
            Run();
        }
        finally
        {
            AppSettings.Current.WinratesAllTime = wasAllTime;
            AppSettings.Current.PoolSplit = wasSplit;
            (AppSettings.Current.PoolWinLeft, AppSettings.Current.PoolWinTop,
             AppSettings.Current.PoolWinWidth, AppSettings.Current.PoolWinHeight) = wasGeom;
            AppSettings.SaveQuiet();
            if (before is not null) File.WriteAllText(PoolsPath, before);
            else if (File.Exists(PoolsPath)) File.Delete(PoolsPath);
            Console.WriteLine("\nсвои пулы и настройки возвращены на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: период (и подпись по нему), ник, подвижная полоса, память места, подсказка выбора и своя половина из личного пула"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 }) SnapRoleIcons();

        // Связки как в песочнице — иначе правой половине нечего показывать.
        const string Him = "FR1111111111111111111111111111111111111111111111111111111111111111111111";
        SessionTracker.Preview =
        [
            new(Him, "Harribon", "flex", 412, 22, 11, 8),
            new(Him, "Harribon", "solo", 89, 51, 3, 1),
        ];

        PoolStore.SetAccount("test-poolwindow", "tester");
        var a = PoolStore.Current();
        var p = new ChampPool { Id = "p1", Name = "supports" };
        p.ByRole["support"] = [412, 89];
        a.Pools.Add(p);
        // Пул, полученный файлом: ник есть, вместе ещё не играли.
        //
        // Пара в пуле нужна ПО ДЕЛУ: лента связок показывает только то, что в
        // пуле задумано, и у пустого ручного пула ей нечего показывать. Раньше
        // туда шли любые совместные игры, и пул мог быть каким угодно.
        var d1 = new DuoPool { Id = "d1", FriendName = "supports + top", FriendNick = "Harribon",
                               Manual = true };
        d1.ManualPairs.Add(new ManualDuoPair { Mine = 412, MineRole = "support",
                                               Friend = 22, FriendRole = "adc" });
        a.DuoPools.Add(d1);
        // Пул, собранный руками: назвать некого.
        a.DuoPools.Add(new DuoPool { Id = "d2", FriendName = "top + mid" });
        PoolStore.Persist();
        // Раздел связок говорит про ОТМЕЧЕННЫЙ дуо-пул — без звезды он и не про
        // кого вовсе, и проверять там нечего.
        PoolStore.SetFavourite(PoolKind.Duo, "d1");

        var w = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        ShowHidden(w);
        Pump();

        // ── Ник на плитке ──────────────────────────────────────────────────
        var texts = Walk<TextBlock>(w).Select(t => t.Text).ToList();
        Check("ник из файла виден на плитке", texts.Contains("Harribon"),
              texts.Contains("Harribon") ? "есть" : "нет");
        Check("подпись плитки осталась прежней", texts.Contains("supports + top"), "да");
        // Режим подбора ушёл с плитки в подсказку — в тексте его быть не должно.
        Check("«Ручной» с плитки убран", !texts.Contains("Ручной"),
              texts.Contains("Ручной") ? "всё ещё на плитке" : "нет");

        // ── Пароль синхронизации ───────────────────────────────────────────
        // Кнопка живёт справа от «Как это работает» и меняет вид, когда пароль
        // задан. Сам пароль — секрет, поэтому проверяем не его, а состояние.
        // ── Связки: только с напарником по пулу ────────────────────────────
        // Подменные связки заведены на Him. У отмеченного пула (d1) хозяин не
        // опознан — значит показывать чужие связки нельзя, даже если чемпионы
        // совпадают с половинами пула.
        Check("без опознанного напарника связки не показываются",
              !texts.Any(t => t.Contains("73%") || t.Contains("8-3")),
              texts.Any(t => t.Contains("8-3")) ? "показаны чужие" : "нет");
        Check("сказано, чего не хватает",
              texts.Any(t => t == Loc.T("pool.duoNoMate")), Loc.T("pool.duoNoMate")[..28] + "…");

        // Тот же пул, но хозяин известен — связки появляются.
        PoolStore.Current().DuoPools[0].FriendPuuid = Him;
        PoolStore.Persist();
        var w3 = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        ShowHidden(w3); Pump();
        var t3 = Walk<TextBlock>(w3).Select(x => x.Text).ToList();
        Check("с опознанным напарником связки появились",
              t3.Any(t => t.Contains("8-3")), t3.Any(t => t.Contains("8-3")) ? "есть" : "нет");
        w3.Close(); Pump();

        var hadPass = SyncPassword.IsSet;
        if (hadPass)
        {
            Check("с паролем показано «добавлен»",
                  texts.Any(t => t.Contains(Loc.T("sync.set"))), Loc.T("sync.set"));
            Check("и кнопка «изменить»",
                  Walk<Button>(w).Any(b => (string?)b.Content == Loc.T("sync.change")),
                  Loc.T("sync.change"));
        }
        else
        {
            Check("без пароля предлагают его завести",
                  Walk<Button>(w).Any(b => (string?)b.Content == Loc.T("sync.add")),
                  Loc.T("sync.add"));
            Check("пометки «пароль добавлен» пока нет",
                  !texts.Any(t => t.Contains(Loc.T("sync.set"))), "нет");
        }

        if (!hadPass)
        {
            Check("пароль сохраняется", SyncPassword.Set("проверка-пароля"), "да");
            Check("пароль виден как заданный", SyncPassword.IsSet, "да");

            // Ключ выводится из пароля и puuid: у одного человека на двух
            // компьютерах он обязан совпасть, у разных людей — нет.
            var k1 = SyncPassword.KeyFor("puuid-один");
            var k2 = SyncPassword.KeyFor("puuid-один");
            var k3 = SyncPassword.KeyFor("puuid-другой");
            Check("ключ получен", k1 is { Length: 32 }, $"{k1?.Length ?? 0} байт");
            Check("на том же аккаунте ключ тот же", k1!.SequenceEqual(k2!), "да");
            Check("у другого аккаунта ключ другой", !k1.SequenceEqual(k3!), "да");

            // Окно, открытое заново, показывает уже другое состояние.
            var w2 = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        ShowHidden(w2); Pump();
            var t2 = Walk<TextBlock>(w2).Select(x => x.Text).ToList();
            Check("с паролем показано «добавлен»",
                  t2.Any(t => t.Contains(Loc.T("sync.set"))), Loc.T("sync.set"));
            Check("и кнопка «изменить»", Walk<Button>(w2).Any(b => (string?)b.Content == Loc.T("sync.change")),
                  Loc.T("sync.change"));
            w2.Close(); Pump();

            SyncPassword.Set(null);
            Check("пароль убирается", !SyncPassword.IsSet, "да");
        }
        else Console.WriteLine("  (свой пароль уже задан — проверку записи пропускаю)");

        // ── Переключение периода ───────────────────────────────────────────
        // Кнопок теперь четыре: своя пара у каждой половины. Порядок обхода
        // дерева сверху вниз и слева направо, поэтому первые две — левые.
        var buttons = Walk<Button>(w)
            .Where(b => b.Content is string s && (s.Contains("дней") || s.Contains("всё время")))
            .ToList();
        Check("у каждой половины своя пара кнопок", buttons.Count == 4, $"{buttons.Count}");
        if (buttons.Count != 4) { w.Close(); app.Shutdown(); return; }

        // Жмём все, и по два раза: падало именно на ПОВТОРНОЙ сборке.
        foreach (var round in new[] { 1, 2 })
            foreach (var b in buttons)
            {
                try
                {
                    b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    Pump();
                }
                catch (Exception e)
                {
                    Check($"нажатие «{b.Content}» (круг {round})", false, e.GetType().Name + ": " + e.Message);
                    w.Close(); app.Shutdown(); return;
                }
            }
        Check("все кнопки нажимаются по два раза", true, "без ошибок");

        // Половины независимы: ставим им РАЗНЫЕ периоды и смотрим, что оба
        // держатся. Ради этого разделение и делалось.
        Click(buttons, "За 30 дней", first: true);      // слева — месяц
        Click(buttons, "За всё время", first: false);   // справа — всё время
        Check("слева месяц, справа всё время",
              !AppSettings.Current.WinratesAllTime && AppSettings.Current.DuoWinratesAllTime,
              $"слева {(AppSettings.Current.WinratesAllTime ? "всё" : "месяц")}, "
              + $"справа {(AppSettings.Current.DuoWinratesAllTime ? "всё" : "месяц")}");

        Click(buttons, "За всё время", first: true);    // слева переключаем
        Check("переключение слева не тронуло правую",
              AppSettings.Current.WinratesAllTime && AppSettings.Current.DuoWinratesAllTime,
              $"слева {(AppSettings.Current.WinratesAllTime ? "всё" : "месяц")}, "
              + $"справа {(AppSettings.Current.DuoWinratesAllTime ? "всё" : "месяц")}");

        Splitter(w);

        w.Close();
        Pump();

        ChipFollowsPeriod();
        Pump();

        NothingCut();
        Pump();

        ByRole();
        Pump();

        MateVisible(d1);
        Pump();

        TakeMine(p);
        Pump();

        PickerHint();
        Pump();

        app.Shutdown();
    }

    /// <summary>
    /// Окно выбора чемпиона при наборе пула: под поиском подсказка «выбирай тех,
    /// на ком играешь уверенно» — пул чемпионы получают первыми в подборе, и
    /// «все, на ком когда-то сыграл» его размывают. В выборе напарника по
    /// одному подсказки нет. Снимок — в CP_SNAP, если задана.
    /// </summary>
    private static void PickerHint()
    {
        Console.WriteLine();
        var type = typeof(OverlayWindow).Assembly.GetType("Counterplay.ChampionPickerWindow")!;
        var names = new List<string> { "Ahri", "Sona" };
        var ids = new Dictionary<string, int> { ["Ahri"] = 103, ["Sona"] = 37 };
        var hint = Loc.T("pool.pickHint");
        foreach (var multi in new[] { true, false })
        {
            var win = (Window)Activator.CreateInstance(type,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic,
                null, [names, ids, Array.Empty<int>(), null, multi], null)!;
            ShowHidden(win); Pump();
            var texts = new List<string>();
            var stack = new Stack<DependencyObject>([win]);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (d is TextBlock tb) texts.Add(tb.Text);
                foreach (var c in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>()) stack.Push(c);
            }
            if (multi)
            {
                Check("набор пула: под поиском подсказка «уверенные пики»", texts.Contains(hint), hint);
                var snap = Environment.GetEnvironmentVariable("CP_SNAP");
                if (snap is { Length: > 0 }) Snap(win, snap, "picker-hint.png");
            }
            else
                Check("выбор напарника: подсказки нет", !texts.Contains(hint), "");
            win.Close(); Pump();
        }
    }

    /// <summary>
    /// Только для снимков: иконки ролей лежат ресурсами в сборке программы, а
    /// «pack://application» ищет их в запущенной — сборке проверки. Подменить
    /// её нельзя (ResourceAssembly уже задан), поэтому кладём картинки из папки
    /// assets/roles прямо в кэш RoleIcons. Без этого на снимке нет иконок ролей.
    /// </summary>
    private static void SnapRoleIcons()
    {
        var cache = typeof(RoleIcons).GetField("_icons",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)?.GetValue(null)
            as System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?>;
        // bin/Release/net8.0-windows/win-x64 → корень репозитория: шесть уровней вверх.
        var dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "assets", "roles");
        foreach (var pos in new[] { "top", "jungle", "middle", "bottom", "utility" })
        {
            var file = Path.GetFullPath(Path.Combine(dir, pos + ".png"));
            if (cache is null || !File.Exists(file)) continue;
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(file);
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            cache[pos] = bmp;
        }
    }

    private static void Snap(Window w, string dir, string file, System.Windows.Media.Brush? background = null)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        if (background is not null)
        {
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen())
                dc.DrawRectangle(background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bmp.Render(bg);
        }
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    /// <summary>
    /// Подвижная полоса между пулами и винрейтами.
    ///
    /// Три вопроса. Полоса вообще есть и её можно тянуть? Винрейты при этом
    /// действительно получают место — раньше у лент был жёсткий потолок в 150
    /// точек, и отданная высота пропадала впустую. И запоминается ли доля.
    /// </summary>
    private static void Splitter(Window w)
    {
        Console.WriteLine();
        var sp = Walk<GridSplitter>(w).FirstOrDefault();
        Check("полоса между половинами есть", sp is not null, sp is null ? "нет" : "да");
        if (sp is null) return;

        var frame = (Grid)VisualTreeHelper.GetParent(sp);
        var rows = frame.RowDefinitions;
        Check("делит ровно на две части", rows.Count == 3, $"{rows.Count} строки");

        // Нижняя часть — только винрейты. Ходить по ВСЕМУ окну тут нельзя:
        // у пулов свои полосы прокрутки, и они бы отвечали за чужую половину.
        var lower = frame.Children.Cast<UIElement>().First(c => Grid.GetRow(c) == 2);

        // Высота лент ДО и ПОСЛЕ сдвига. Меряем нижнюю строку целиком: в ней и
        // кнопки периода, и заголовки, и сами ленты.
        var wasBottom = rows[2].ActualHeight;
        var wasTop    = rows[0].ActualHeight;
        Check("обе части заметного размера", wasTop > 100 && wasBottom > 100,
              $"{wasTop:0} / {wasBottom:0}");

        // Тянем полосу вверх: у пулов забираем, винрейтам отдаём.
        const double Drag = 120;
        rows[0].Height = new GridLength(Math.Max(120, wasTop - Drag), GridUnitType.Star);
        rows[2].Height = new GridLength(wasBottom + Drag, GridUnitType.Star);
        Pump();

        Check("винрейтам досталось больше места", rows[2].ActualHeight > wasBottom + Drag * 0.5,
              $"{wasBottom:0} → {rows[2].ActualHeight:0}");

        // Главное: ленту тоже растянуло. Жёсткий потолок съел бы прибавку.
        var strip = Walk<ScrollViewer>(lower)
            .Where(s => s.ActualHeight > 0)
            .OrderByDescending(s => s.ActualHeight)
            .ToList();
        Check("ленты не упираются в потолок",
              strip.Any(s => s.ActualHeight > 170), $"самая высокая {strip.FirstOrDefault()?.ActualHeight ?? 0:0}");

        // Доля должна лечь в настройки при закрытии.
        var win = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        ShowHidden(win); Pump();
        var r2 = ((Grid)VisualTreeHelper.GetParent(Walk<GridSplitter>(win).First())).RowDefinitions;
        r2[0].Height = new GridLength(0.3, GridUnitType.Star);
        r2[2].Height = new GridLength(0.7, GridUnitType.Star);
        Pump();
        win.Close(); Pump();
        Check("доля запомнена", Math.Abs(AppSettings.Current.PoolSplit - 0.3) < 0.05,
              $"{AppSettings.Current.PoolSplit:0.00} (ждали 0.30)");

        MiddleBand(w, sp, frame);
        Geometry();
    }

    /// <summary>
    /// Средняя полоса — строка с «?» и паролем — тянется за любое пустое место.
    ///
    /// Владелец (19.11): «сделай, чтобы центральную часть можно было двигать».
    /// Полоса была, но тянулась только черта в 9 точек НАД этой строкой, а
    /// сама строка лежала в нижней части и мышь не брала. Человек хватается
    /// за середину — и ничего не происходит.
    ///
    /// Две вещи: мышь, нажатая на пустом месте строки, попадает в полосу; и
    /// перетаскивание полосы действительно перекладывает высоту между частями
    /// (раньше строки выставлялись напрямую, саму полосу никто не тянул).
    /// </summary>
    private static void MiddleBand(Window w, GridSplitter sp, Grid frame)
    {
        // Строка «? Как это работает … пароль» — ближайшая DockPanel над подписью.
        var label = Walk<TextBlock>(w).FirstOrDefault(t => t.Text == Loc.T("pool.helpLabel"));
        DependencyObject? row = label;
        while (row is not null and not DockPanel) row = VisualTreeHelper.GetParent(row);
        Check("строка с «?» и паролем найдена", row is DockPanel, row is null ? "нет" : "да");
        if (row is not DockPanel strip) return;

        // Ведём мышью поперёк строки по её середине и считаем, сколько точек
        // попадает в полосу: пустое место между «?» и паролем должно браться.
        var box = strip.TransformToAncestor(frame).TransformBounds(new Rect(strip.RenderSize));
        var y = box.Top + box.Height / 2;
        var grab = 0;
        for (var x = box.Left; x < box.Right; x += 2)
        {
            var hit = frame.InputHitTest(new System.Windows.Point(x, y)) as DependencyObject;
            for (var d = hit; d is not null; d = VisualTreeHelper.GetParent(d))
                if (d == sp) { grab += 2; break; }
        }
        Check("среднюю строку можно тянуть за пустое место", grab >= 80,
              $"{grab} точек из {box.Width:0}");
        if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 } snap)
            Snap(w, snap, "middle-band.png", w.Background);

        // Тянем саму полосу на 100 точек вверх — как мышью.
        var rows = frame.RowDefinitions;
        var top = rows[0].ActualHeight;
        var bottom = rows[2].ActualHeight;
        sp.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
        sp.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, -100)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        sp.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, -100, false)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
        Pump();
        Check("перетаскивание полосы отдаёт место винрейтам",
              rows[0].ActualHeight < top - 50 && rows[2].ActualHeight > bottom + 50,
              $"пулы {top:0} → {rows[0].ActualHeight:0}, винрейты {bottom:0} → {rows[2].ActualHeight:0}");

        // Доля — в файле сразу, как отпустили полосу, а не при закрытии окна:
        // программа может кончиться и при открытом (владелец, 19.11).
        var want = Math.Clamp(rows[0].ActualHeight / (rows[0].ActualHeight + rows[2].ActualHeight), 0.25, 0.85);
        var disk = Saved("PoolSplit");
        Check("граница в файле, хотя окно не закрывали", disk is { } s && Math.Abs(s - want) < 0.02,
              $"{disk?.ToString("0.00") ?? "нет"} (ждали {want:0.00})");
    }

    /// <summary>
    /// Память места и размера.
    ///
    /// Два случая. Обычный: окно открывается там, где его оставили. И тот, из-за
    /// которого проверка вообще написана: монитор отключили, запомненное место
    /// оказалось за краем — размер берём, место нет, иначе окно уедет туда,
    /// откуда его не достать мышью.
    /// </summary>
    private static void Geometry()
    {
        Console.WriteLine();

        // Масштаб окон зависит от разрешения клиента LoL, поэтому размеры для
        // проверки берём С ЗАПАСОМ над минимумом — иначе при запущенном клиенте
        // они упрутся в него, и проверка будет мерить не то, что задумано.
        var probe = new PoolSettingsWindow(() => { });
        ShowHidden(probe); Pump();
        var minW = probe.MinWidth; var minH = probe.MinHeight;
        probe.Close(); Pump();
        var wideW = minW + 180; var wideH = minH + 120;

        // ── Мусорное место: размер берём, место — нет ──────────────────────
        AppSettings.Current.PoolWinLeft   = -9000;
        AppSettings.Current.PoolWinTop    = -9000;
        AppSettings.Current.PoolWinWidth  = wideW;
        AppSettings.Current.PoolWinHeight = wideH;

        var w1 = new PoolSettingsWindow(() => { });
        ShowHidden(w1); Pump();
        Check("размер с прошлого раза восстановлен", Math.Abs(w1.Width - wideW) < 2,
              $"{w1.Width:0} (ждали {wideW:0})");
        // Сверяем не «окно на экране», а «окно НЕ уехало в мусорное место»:
        // проверки теперь и стартуют за краем, чтобы не мигать поверх игры, и
        // по одному положению эти два случая уже не различить.
        Check("мусорное место не применено", Math.Abs(w1.Left - (-9000)) > 1, $"{w1.Left:0}");
        w1.Close(); Pump();
        // Окно закрылось за краем экрана — такое место запоминать нельзя, и
        // настройка обязана остаться прежней (той, что положили выше).
        Check("закрытие за краем не записалось",
              Math.Abs(AppSettings.Current.PoolWinLeft - (-9000)) < 1,
              $"{AppSettings.Current.PoolWinLeft:0}");

        // ── Нормальное место: открылось ровно там ──────────────────────────
        AppSettings.Current.PoolWinLeft   = 140;
        AppSettings.Current.PoolWinTop    = 90;
        AppSettings.Current.PoolWinWidth  = wideW;
        AppSettings.Current.PoolWinHeight = wideH;

        var w2 = new PoolSettingsWindow(() => { });
        ShowHidden(w2); Pump();
        Check("окно открылось там, где его оставили",
              Math.Abs(w2.Left - 140) < 2 && Math.Abs(w2.Top - 90) < 2,
              $"{w2.Left:0},{w2.Top:0}");

        // Подвинули и растянули — при закрытии это должно лечь в настройки.
        w2.Left = 210; w2.Top = 130; w2.Width = wideW + 30; w2.Height = wideH + 40;
        Pump();
        w2.Close(); Pump();
        Check("новое место и размер запомнены",
              Math.Abs(AppSettings.Current.PoolWinLeft - 210) < 2 &&
              Math.Abs(AppSettings.Current.PoolWinHeight - (wideH + 40)) < 2,
              $"{AppSettings.Current.PoolWinLeft:0},{AppSettings.Current.PoolWinTop:0} "
              + $"{AppSettings.Current.PoolWinWidth:0}×{AppSettings.Current.PoolWinHeight:0}");

        // ── Без закрытия: программа может кончиться при открытом окне ──────
        // Владелец (19.11): «если выставили в одном размере, пусть такое и
        // остаётся». Размер писался только при закрытии окна, а программа
        // кончилась при открытом (снятый процесс, перезапуск после обновления),
        // и растянутое пропало. Смотрим ФАЙЛ: память процесса умрёт вместе с ним.
        var wLive = new PoolSettingsWindow(() => { });
        ShowHidden(wLive); Pump();
        wLive.Left = 230; wLive.Top = 150; wLive.Width = wideW + 60; wLive.Height = wideH + 70;
        Wait(1200);
        var onDisk = SavedGeometry();
        Check("размер и место в файле, хотя окно не закрывали",
              onDisk is { } g && Math.Abs(g.L - 230) < 2 && Math.Abs(g.W - (wideW + 60)) < 2
                              && Math.Abs(g.H - (wideH + 70)) < 2,
              onDisk is { } d ? $"{d.L:0},{d.T:0} {d.W:0}×{d.H:0} (ждали 230,150 {wideW + 60:0}×{wideH + 70:0})" : "в файле нет");
        wLive.Close(); Pump();

        // ── Размер меньше нынешнего минимума: поджимается, место не теряется ──
        // Клиент LoL сменил разрешение — вместе с ним растёт масштаб окон, и
        // запомненный размер может оказаться мал. Раньше из-за этого молча
        // терялось и место.
        AppSettings.Current.PoolWinLeft   = 175;
        AppSettings.Current.PoolWinTop    = 105;
        AppSettings.Current.PoolWinWidth  = 50;
        AppSettings.Current.PoolWinHeight = 50;
        var w4 = new PoolSettingsWindow(() => { });
        ShowHidden(w4); Pump();
        Check("размер меньше минимума поджимается", Math.Abs(w4.Width - w4.MinWidth) < 2,
              $"{w4.Width:0} при минимуме {w4.MinWidth:0}");
        Check("место при этом не потеряно",
              Math.Abs(w4.Left - 175) < 2 && Math.Abs(w4.Top - 105) < 2,
              $"{w4.Left:0},{w4.Top:0}");
        w4.Close(); Pump();

        // ── Первое открытие: высота по умолчанию, та самая «на треть выше» ──
        AppSettings.Current.PoolWinWidth = 0;
        AppSettings.Current.PoolWinHeight = 0;
        var w3 = new PoolSettingsWindow(() => { });
        ShowHidden(w3); Pump();
        var want = 750 * AppSettings.Current.FontScale * OverlayWindow.ClientScaleFor(w3);
        var cap  = Math.Max(240, SystemParameters.WorkArea.Height - 40);
        Check("без памяти окно открывается высоким",
              Math.Abs(w3.Height - Math.Min(want, cap)) < 2,
              $"{w3.Height:0} (было 560)");
        w3.Close(); Pump();
    }

    /// <summary>
    /// Подпись под иконкой и рамка слота стоят на одних числах.
    ///
    /// Рамка красилась по выбранному периоду, а подпись всегда считала месяц.
    /// «За всё время» давало «25% / 4» в оранжевой рамке: рамка видела 65% на
    /// двадцати играх, подпись — только последние четыре. Со стороны это
    /// выглядело как цветные рамки у чемпионов с плохим счётом.
    /// </summary>
    private static void ChipFollowsPeriod()
    {
        Console.WriteLine();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();
        // Месяц: 1 из 4. Раньше: 12 из 16. Всё время: 13 из 20 — 65%.
        for (var i = 0; i < 4; i++)
            games.Add(new("ally", "Ally", "solo", 412, 22, "support", "adc", now - (i + 1) * 86400L, i == 0));
        for (var i = 0; i < 16; i++)
            games.Add(new("ally", "Ally", "solo", 412, 22, "support", "adc", now - (60 + i) * 86400L, i < 12));
        SessionTracker.PreviewGames = games;
        try
        {
            foreach (var (allTime, want) in new[] { (true, "65% / 20"), (false, "25% / 4") })
            {
                AppSettings.Current.WinratesAllTime = allTime;
                var w = new PoolSettingsWindow(() => { });
                ShowHidden(w); Pump();

                var slots = Walk<Border>(w)
                    .Select(b => (Frame: b, Chip: (b.Child as StackPanel)?.Children.OfType<TextBlock>()
                                                   .FirstOrDefault(t => t.Text.Contains("% / "))))
                    .Where(x => x.Chip is not null)
                    .ToList();
                var period = allTime ? "за всё время" : "за 30 дней";
                var shown  = slots.Select(x => x.Chip!.Text).ToList();
                Check($"{period}: подпись считает тот же период", shown.Contains(want),
                      $"{(shown.Count > 0 ? string.Join(", ", shown) : "подписей нет")} (ждали {want})");
                Check($"{period}: рамка того же цвета, что подпись",
                      slots.Count > 0 && slots.All(x => SameColor(x.Frame.BorderBrush, x.Chip!.Foreground)),
                      string.Join(", ", slots.Select(x =>
                          $"{x.Chip!.Text}: рамка {Hex(x.Frame.BorderBrush)}, подпись {Hex(x.Chip.Foreground)}")));
                w.Close(); Pump();
            }
        }
        finally { SessionTracker.PreviewGames = null; }
    }

    /// <summary>
    /// Владелец 24 октября: вчера играл в нормалах на Камилле и Сайласе, а в
    /// «Твоём винрейте» их нет. Окно показывало 30 самых сыгранных, а при
    /// равенстве игр ранговые стояли впереди нормалов просто по порядку
    /// чтения, так что одиночные нормалы срезались первыми (у владельца 36
    /// чемпионов за месяц, они — 35-й и 36-й). Здесь 31 чемпион с двумя
    /// ранговыми играми и вчерашние Камилла и Сайлас с одной в нормалах.
    /// </summary>
    private static void NothingCut()
    {
        Console.WriteLine();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();
        for (var champ = 1; champ <= 31; champ++)
            for (var i = 0; i < 2; i++)
                games.Add(new("", "", "solo", champ, 0, "", "", now - 2 * 86400L - champ * 3600L, i == 0));
        games.Add(new("", "", "normal", 164, 0, "", "", now - 86400, true));    // Камилла
        games.Add(new("", "", "normal", 517, 0, "", "", now - 80000, false));   // Сайлас
        SessionTracker.PreviewGames = games;
        var a = PoolStore.Current();
        var fav = a.FavPoolId;
        a.FavPoolId = null;   // без звезды — все чемпионы, а не один пул
        try
        {
            AppSettings.Current.WinratesAllTime = false;
            var w = new PoolSettingsWindow(() => { });
            ShowHidden(w); Pump();
            var chips = Walk<TextBlock>(w).Count(t => t.Text.Contains("% / "));
            Check("за 30 дней: все 33 чемпиона на месте, вчерашние нормалы не срезаны",
                  chips == 33, $"{chips} подписей");
            w.Close(); Pump();
        }
        finally
        {
            SessionTracker.PreviewGames = null;
            a.FavPoolId = fav;
        }
    }

    /// <summary>
    /// Владелец 19 ноября: над чемпионами — винрейт по пяти линиям дугами, а
    /// сами чемпионы — по линиям, с иконкой роли в углу; чемпион с двух линий —
    /// два слота. Здесь Шен на саппорте (2 из 3) и в топе (1 из 2), Ари в
    /// миде (3 из 4); леса и бота нет вовсе.
    /// </summary>
    private static void ByRole()
    {
        Console.WriteLine();
        const int Shen = 98, Ahri = 103;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();
        void Add(string queue, int champ, string role, bool win, int day) =>
            games.Add(new("", "", queue, champ, 0, role, "", now - day * 86400L, win));
        Add("solo", Shen, "support", true, 1); Add("solo", Shen, "support", true, 2);
        Add("solo", Shen, "support", false, 3);
        Add("normal", Shen, "top", true, 4); Add("normal", Shen, "top", false, 5);
        Add("solo", Ahri, "mid", true, 1); Add("solo", Ahri, "mid", true, 2);
        Add("flex", Ahri, "mid", true, 3); Add("solo", Ahri, "mid", false, 6);
        // Ещё две в миде: 5 из 6 (83%) — выборка уже не «мало игр», и цвет
        // винрейта у плитки и группы виден, а не глушится серым.
        Add("solo", Ahri, "mid", true, 7); Add("normal", Ahri, "mid", true, 8);
        // За окном в 30 дней: в «за 30 дней» его быть не должно.
        Add("solo", Ahri, "jungle", true, 60);

        SessionTracker.PreviewGames = games;
        var a = PoolStore.Current();
        var fav = a.FavPoolId;
        a.FavPoolId = null;   // без звезды — все чемпионы
        try
        {
            AppSettings.Current.WinratesAllTime = false;
            var w = new PoolSettingsWindow(() => { }) { Width = 820, Height = 640 };
            ShowHidden(w); Pump();

            // Плитки линий — ряд из пяти в UniformGrid; у каждой свои надписи.
            var tiles = Walk<System.Windows.Controls.Primitives.UniformGrid>(w)
                .FirstOrDefault(g => g.Children.Count == 5)?.Children.OfType<Border>().ToList() ?? [];
            Check("над чемпионами пять плиток линий", tiles.Count == 5, $"{tiles.Count}");
            // Счёт собран из цветных кусков (Run), а у такого TextBlock свойство
            // Text пустое — читаем сами куски.
            static string Plain(TextBlock t) => t.Inlines.Count > 0
                ? string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text))
                : t.Text;
            string Texts(Border b) => string.Join(" ", Walk<TextBlock>(b).Select(Plain));
            // Порядок — по числу игр (владелец, 19.11): мид 6, саппорт 3, топ 2;
            // пустые лес и бот — в конце, между собой в обычном порядке линий.
            var want = new[] { "83% 5В – 1П", "67% 2В – 1П", "50% 1В – 1П", "—", "—" };
            var got  = tiles.Select(Texts).ToList();
            var tips = tiles.Select(t => (t.ToolTip as string ?? "").Split(" · ")[0]).ToList();
            var wantTips = new[] { "mid", "support", "top", "jungle", "adc" }.Select(r => Loc.T("roles." + r));
            Check("плитки по числу игр: мид, саппорт, топ, затем пустые лес и бот — счёт верный",
                  got.SequenceEqual(want) && tips.SequenceEqual(wantTips),
                  string.Join(" | ", tiles.Select((t, i) => $"{tips[i]} {got[i]}")));
            var arcs = tiles.Select(t => Walk<System.Windows.Shapes.Path>(t).Count()).ToList();
            Check("у сыгранной линии дуга из побед и поражений, у пустой — одна подложка",
                  arcs.SequenceEqual([2, 2, 2, 1, 1]), string.Join(",", arcs));

            // Рамка и подложка плитки — цвет винрейта линии, как у слотов
            // чемпионов: мид 83% на 6 играх — по шкале, саппорт и топ на 3 и 2
            // играх — серые (мало игр).
            var tileWr = new (double Wr, int Games)[] { (500.0 / 6, 6), (200.0 / 3, 3), (50, 2) };
            var tileOk = tileWr.Select((x, i) =>
                SameColor(tiles[i].BorderBrush, WinrateColor.BrushForSample(x.Wr, x.Games))
                && SameColor(tiles[i].Background, WinrateColor.TintForSample(x.Wr, x.Games))).ToList();
            Check("рамка и подложка плитки — цвет винрейта линии, как у слотов",
                  tileOk.All(ok => ok) && !SameColor(tiles[0].BorderBrush, tiles[1].BorderBrush),
                  string.Join(", ", tiles.Take(3).Select((t, i) => $"{tips[i]} {Hex(t.BorderBrush)}")));

            // Слоты чемпионов: Шен дважды, у каждого своя линия и свой счёт;
            // группы — в том же порядке, что плитки: мид, саппорт, топ.
            var slots = Walk<Border>(w)
                .Where(b => (b.Child as StackPanel)?.Children.OfType<TextBlock>().Any(t => t.Text.Contains("% / ")) == true)
                .Select(b => (Tip: b.ToolTip as string ?? "",
                              Chip: ((StackPanel)b.Child).Children.OfType<TextBlock>().First().Text))
                .ToList();
            var shown = slots.Select(s => $"{s.Tip} {s.Chip}").ToList();
            Check("Шен на двух линиях — два слота", slots.Count(s => s.Tip.StartsWith(DataDragon.Name(Shen))) == 2,
                  string.Join(" | ", shown));
            Check("слоты по линиям в порядке плиток: мид, саппорт, топ — у каждого свой счёт",
                  slots.Select(s => s.Chip).SequenceEqual(["83% / 6", "67% / 3", "50% / 2"])
                  && slots[0].Tip.EndsWith(Loc.T("roles.mid"))
                  && slots[1].Tip.EndsWith(Loc.T("roles.support"))
                  && slots[2].Tip.EndsWith(Loc.T("roles.top")),
                  string.Join(" | ", shown));

            // Группы линий: полоса на линию в том же порядке, между ними тонкие
            // линии; жирная черта справа и еле заметная заливка — цвет винрейта
            // линии (тот же, что у рамки её плитки).
            var groups = Walk<Border>(w).Where(b => b.Tag is string t && t.StartsWith("role-group-")).ToList();
            var seps   = Walk<Border>(w).Count(b => (b.Tag as string) == "role-sep");
            Check("группы по линиям: мид, саппорт, топ, между ними две тонкие линии",
                  groups.Select(g => (string)g.Tag).SequenceEqual(["role-group-mid", "role-group-support", "role-group-top"])
                  && seps == 2,
                  $"{string.Join(", ", groups.Select(g => g.Tag))}; линий {seps}");
            var groupOk = groups.Select((g, i) =>
            {
                var strip = Walk<Border>(g).FirstOrDefault(b => (b.Tag as string) == "role-strip");
                var want  = WinrateColor.ColorForSample(tileWr[i].Wr, tileWr[i].Games);
                return strip?.Background is SolidColorBrush s && s.Color == want
                    && g.Background is SolidColorBrush f && f.Color.A < 0x20
                    && f.Color.R == want.R && f.Color.G == want.G && f.Color.B == want.B;
            }).ToList();
            Check("черта и заливка группы — цвет винрейта линии, заливка почти прозрачная",
                  groupOk.Count == 3 && groupOk.All(ok => ok),
                  string.Join(", ", groups.Select(g =>
                      $"{g.Tag}: черта {Hex(Walk<Border>(g).FirstOrDefault(b => (b.Tag as string) == "role-strip")?.Background ?? System.Windows.Media.Brushes.Transparent)}, заливка {(g.Background as SolidColorBrush)?.Color}")));

            var snap = Environment.GetEnvironmentVariable("CP_SNAP");
            if (snap is { Length: > 0 })
            {
                // Фон окна лежит на самом окне, а снимок берёт содержимое — без
                // подложки тёмная тема выходит светлой.
                Snap(w, snap, "roles.png", w.Background);
            }
            w.Close(); Pump();
        }
        finally
        {
            SessionTracker.PreviewGames = null;
            a.FavPoolId = fav;
        }
    }

    private static bool SameColor(System.Windows.Media.Brush a, System.Windows.Media.Brush b) =>
        a is SolidColorBrush x && b is SolidColorBrush y && x.Color == y.Color;

    private static string Hex(System.Windows.Media.Brush b) =>
        b is SolidColorBrush s ? $"#{s.Color.R:X2}{s.Color.G:X2}{s.Color.B:X2}" : "?";

    /// Нажать кнопку с таким текстом: первую (левая половина) или вторую (правая).
    private static void Click(List<Button> all, string text, bool first)
    {
        var found = all.Where(b => (string?)b.Content == text).ToList();
        var b = first ? found.First() : found.Last();
        b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();
    }

    /// Все потомки нужного типа в дереве окна.
    private static IEnumerable<T> Walk<T>(DependencyObject root) where T : DependencyObject
    {
        var n = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in Walk<T>(c)) yield return x;
        }
    }

    /// Подождать, крутя очередь окна: сохранение идёт по таймеру, а таймер
    /// срабатывает только из очереди.
    private static void Wait(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) Pump();
    }

    /// Число из ui.json проверки — как оно лежит на диске, а не в памяти.
    private static double? Saved(string name)
    {
        var path = Path.Combine(AppPaths.Root, "ui.json");
        if (!File.Exists(path)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.TryGetProperty(name, out var v) ? v.GetDouble() : null;
    }

    /// Место и размер окна пулов, как они лежат в ui.json проверки.
    private static (double L, double T, double W, double H)? SavedGeometry() =>
        Saved("PoolWinLeft") is { } l && Saved("PoolWinTop") is { } t
            && Saved("PoolWinWidth") is { } w && Saved("PoolWinHeight") is { } h
            ? (l, t, w, h) : null;

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// <summary>
    /// Выбор напарника виден в окне сборки дуо-пула.
    ///
    /// Поле ника с плашкой выбора стояло в ОДНОЙ строке с именем пула, а справа
    /// в той же строке — Экспорт/Назад/Сброс/Сохранить. В окне шириной 620
    /// кнопки забирают около трёхсот точек, имени нужно ещё двести — ник с
    /// плашкой в остаток не влезал и уходил за правый край. Поле существовало,
    /// работало и было недоступно: владелец его просто не видел.
    ///
    /// Поэтому меряем не «есть ли элемент», а ГДЕ он оказался.
    /// </summary>
    private static void MateVisible(DuoPool duo)
    {
        var w = new PoolEditorWindow(duo, duo: true);
        ShowHidden(w);
        w.UpdateLayout();

        var badge = FindTagged(w, "mate-badge");
        if (badge is null)
        {
            Check("плашка напарника есть в окне сборки дуо-пула", false, "не найдена");
            w.Close();
            return;
        }

        var box = System.Windows.Media.VisualTreeHelper.GetDescendantBounds(badge);
        var tl  = badge.TransformToAncestor(w).Transform(new System.Windows.Point(0, 0));
        var right  = tl.X + box.Width;
        var bottom = tl.Y + box.Height;

        Check("плашка напарника нарисована", box.Width > 0 && box.Height > 0,
              $"{box.Width:F0}×{box.Height:F0}");
        Check("плашка напарника не уехала за правый край окна",
              right <= w.ActualWidth, $"правый край {right:F0} при ширине окна {w.ActualWidth:F0}");
        Check("и не уехала за нижний край",
              bottom <= w.ActualHeight, $"низ {bottom:F0} при высоте окна {w.ActualHeight:F0}");

        // Кнопка сброса стоит вплотную к плашке — разная высота рядом видна
        // сразу. Меряем, а не смотрим.
        if (FindTagged(w, "mate-reset") is { } rst)
            Check("кнопка сброса ростом с плашку",
                  Math.Abs(rst.ActualHeight - box.Height) < 1.5,
                  $"кнопка {rst.ActualHeight:F0}, плашка {box.Height:F0}");
        else Check("кнопка сброса найдена", false, "не найдена");

        // Главное. За край окна плашка и не уезжала — она залезала ПОД кнопки
        // справа, и те её закрывали. Сравнение с шириной окна этого не видело.
        if (FindTagged(w, "editor-actions") is { } acts)
        {
            var ax = acts.TransformToAncestor(w).Transform(new System.Windows.Point(0, 0)).X;
            var ay = acts.TransformToAncestor(w).Transform(new System.Windows.Point(0, 0)).Y;
            var ah = System.Windows.Media.VisualTreeHelper.GetDescendantBounds(acts).Height;
            var overlapsX = right > ax;
            var overlapsY = bottom > ay && tl.Y < ay + ah;
            Check("плашка напарника не заехала под кнопки",
                  !(overlapsX && overlapsY),
                  $"плашка {tl.X:F0}–{right:F0}, кнопки начинаются с {ax:F0}");
        }
        else Check("блок кнопок найден", false, "не найден");

        w.Close();
    }

    /// <summary>
    /// Свою половину дуо-пула можно взять из личного пула прямо в редакторе.
    ///
    /// Владелец 1 ноября: «выбрать за него героев можно, выбрать его ник
    /// можно, а добавить себя — только при импорте». Свой личный пул вставал
    /// в пару, только когда загружали пул друга; собирая пару руками, свою
    /// половину набирали заново по чемпиону.
    /// </summary>
    private static void TakeMine(ChampPool mine)
    {
        Console.WriteLine();
        var duo = new DuoPool { Id = "take", FriendName = "друг", Manual = false };
        duo.Mine["top"] = [24];
        duo.Friend["mid"] = [103];

        var w = new PoolEditorWindow(duo, duo: true);
        ShowHidden(w); Pump();
        Check("в авто-режиме у «Моя половина пула» есть «Выбрать из моих пулов»",
              FindTagged(w, "take-mine") is not null, FindTagged(w, "take-mine") is null ? "кнопки нет" : "есть");

        w.TakeMine(mine);
        Pump();
        var now = w.MineNow;
        var sup = string.Join(",", now["support"]);
        Check("своя половина стала личным пулом", sup == string.Join(",", mine.ByRole["support"]),
              $"support: {sup}");
        Check("то, что было в своей половине, ушло", now["top"].Count == 0,
              $"top: {string.Join(",", now["top"])}");
        Check("личный пул не связан с половиной (копия, а не тот же список)",
              !ReferenceEquals(now["support"], mine.ByRole["support"]), "копия");
        Check("пул в хранилище до «Сохранить» не тронут", duo.Mine["top"].SequenceEqual([24]),
              $"top: {string.Join(",", duo.Mine["top"])}");
        Check("кнопка на месте и после переноса", FindTagged(w, "take-mine") is not null, "да");
        if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 } snap) Snap(w, snap, "take-mine.png");
        w.Close(); Pump();

        // В ручном режиме половин по ролям нет — брать некуда.
        var manual = new DuoPool { Id = "take-m", FriendName = "друг", Manual = true };
        var wm = new PoolEditorWindow(manual, duo: true);
        ShowHidden(wm); Pump();
        Check("в ручном режиме кнопки нет", FindTagged(wm, "take-mine") is null, "нет");
        wm.Close(); Pump();

        // Новый пул (плитка «+» передаёт null) открывается в авто-режиме.
        var wn = new PoolEditorWindow(null, duo: true);
        ShowHidden(wn); Pump();
        // Content у «Экспорта» — значок с подписью, не строка: сравниваем через as.
        var auto = Walk<Button>(wn).FirstOrDefault(b => b.Content as string == Loc.T("pool.duoAuto"));
        var manualBtn = Walk<Button>(wn).FirstOrDefault(b => b.Content as string == Loc.T("pool.duoManual"));
        Check("новый дуо-пул открывается в авто-режиме",
              auto?.FontWeight == FontWeights.Bold && FindTagged(wn, "take-mine") is not null,
              auto is null ? "кнопки «Авто» нет" : auto.FontWeight == FontWeights.Bold ? "авто" : "ручной");

        // Владелец 1 ноября: три строки «Ник напарника», «Подбор пары», «Мои»
        // стояли вразнобой — у каждой элементы начинались со своего места.
        // Меряем, откуда начинается первый элемент каждой строки.
        double X(FrameworkElement? e) => e is null ? double.NaN
            : e.TransformToAncestor(wn).Transform(new System.Windows.Point(0, 0)).X;
        var xs = new[] { X(FindTagged(wn, "mate-badge")), X(manualBtn), X(FindTagged(wn, "take-mine")) };
        Check("ник, подбор пары и своя половина: элементы начинаются с одной линии",
              xs.All(x => !double.IsNaN(x)) && xs.Max() - xs.Min() < 1,
              string.Join(" / ", xs.Select(x => double.IsNaN(x) ? "нет" : x.ToString("F0"))));
        if (Environment.GetEnvironmentVariable("CP_SNAP") is { Length: > 0 } snapNew) Snap(wn, snapNew, "duo-new.png");
        wn.Close(); Pump();

        // Личных пулов нет — брать не из чего.
        var pools = PoolStore.Current().Pools;
        var kept = pools.ToList();
        pools.Clear();
        try
        {
            var we = new PoolEditorWindow(duo, duo: true);
            ShowHidden(we); Pump();
            Check("без личных пулов кнопки нет", FindTagged(we, "take-mine") is null, "нет");
            we.Close(); Pump();
        }
        finally { pools.AddRange(kept); }
    }

    /// Первый элемент с такой меткой во всём дереве окна.
    private static FrameworkElement? FindTagged(DependencyObject root, string tag)
    {
        if (root is FrameworkElement fe && Equals(fe.Tag, tag)) return fe;
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
            if (FindTagged(System.Windows.Media.VisualTreeHelper.GetChild(root, i), tag) is { } hit)
                return hit;
        return null;
    }

    /// <summary>
    /// Показать окно так, чтобы человек его не увидел.
    ///
    /// Одной прозрачности мало: она работает только у окон с
    /// AllowsTransparency, а у окна пулов его нет — там Opacity=0 давал чёрный
    /// прямоугольник посреди экрана. Поэтому ещё и уводим за край, отключив
    /// центрирование: WindowStartupLocation по умолчанию перебивает Left/Top,
    /// выставленные до Show.
    ///
    /// Раскладка, размеры и обход дерева при этом работают как обычно.
    /// </summary>
    private static void ShowHidden(System.Windows.Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;

        // Попиксельная прозрачность ОБЯЗАТЕЛЬНА: без неё Opacity=0 не прячет
        // окно, а красит его в чёрный — окно пулов так и вылезало чёрным
        // прямоугольником. Оба свойства можно менять только до Show, здесь это
        // и делается. У оверлея они уже такие, ему не повредит.
        //
        // Одного выноса за край тоже мало: окна возвращают себя на экран сами —
        // оверлей «вписывается в экран», окно пулов встаёт по запомненной
        // раскладке.
        w.WindowStyle = System.Windows.WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;

        w.Show();
        // ПОСЛЕ показа не двигаем: запомненную раскладку окно ставит себе само,
        // и проверки геометрии меряют именно её. Тем, у кого раскладки нет,
        // хватает вынесенного старта.
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
