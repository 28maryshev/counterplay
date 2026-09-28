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

    private static string PoolsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "pools.json");

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
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
        Console.WriteLine(_fails == 0 ? "ИТОГ: период, ник, подвижная полоса и память места"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

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
        a.DuoPools.Add(new DuoPool { Id = "d1", FriendName = "supports + top", FriendNick = "Harribon",
                                     Manual = true });
        // Пул, собранный руками: назвать некого.
        a.DuoPools.Add(new DuoPool { Id = "d2", FriendName = "top + mid" });
        PoolStore.Persist();
        // Раздел связок говорит про ОТМЕЧЕННЫЙ дуо-пул — без звезды он и не про
        // кого вовсе, и проверять там нечего.
        PoolStore.SetFavourite(PoolKind.Duo, "d1");

        var w = new PoolSettingsWindow(() => { }) { Left = -4000, Top = -4000 };
        w.Show();
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
        w3.Show(); Pump();
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
            w2.Show(); Pump();
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
        app.Shutdown();
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
        win.Show(); Pump();
        var r2 = ((Grid)VisualTreeHelper.GetParent(Walk<GridSplitter>(win).First())).RowDefinitions;
        r2[0].Height = new GridLength(0.3, GridUnitType.Star);
        r2[2].Height = new GridLength(0.7, GridUnitType.Star);
        Pump();
        win.Close(); Pump();
        Check("доля запомнена", Math.Abs(AppSettings.Current.PoolSplit - 0.3) < 0.05,
              $"{AppSettings.Current.PoolSplit:0.00} (ждали 0.30)");

        Geometry();
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

        // ── Мусорное место: размер берём, место — нет ──────────────────────
        AppSettings.Current.PoolWinLeft   = -9000;
        AppSettings.Current.PoolWinTop    = -9000;
        AppSettings.Current.PoolWinWidth  = 900;
        AppSettings.Current.PoolWinHeight = 700;

        var w1 = new PoolSettingsWindow(() => { });
        w1.Show(); Pump();
        Check("размер с прошлого раза восстановлен", Math.Abs(w1.Width - 900) < 2, $"{w1.Width:0}");
        Check("место за краем экрана отброшено", w1.Left > -1000, $"{w1.Left:0}");
        w1.Close(); Pump();
        Check("и в настройки такое место не легло", AppSettings.Current.PoolWinLeft > -1000,
              $"{AppSettings.Current.PoolWinLeft:0}");

        // ── Нормальное место: открылось ровно там ──────────────────────────
        AppSettings.Current.PoolWinLeft   = 140;
        AppSettings.Current.PoolWinTop    = 90;
        AppSettings.Current.PoolWinWidth  = 880;
        AppSettings.Current.PoolWinHeight = 680;

        var w2 = new PoolSettingsWindow(() => { });
        w2.Show(); Pump();
        Check("окно открылось там, где его оставили",
              Math.Abs(w2.Left - 140) < 2 && Math.Abs(w2.Top - 90) < 2,
              $"{w2.Left:0},{w2.Top:0}");

        // Подвинули и растянули — при закрытии это должно лечь в настройки.
        w2.Left = 210; w2.Top = 130; w2.Width = 910; w2.Height = 720;
        Pump();
        w2.Close(); Pump();
        Check("новое место и размер запомнены",
              Math.Abs(AppSettings.Current.PoolWinLeft - 210) < 2 &&
              Math.Abs(AppSettings.Current.PoolWinHeight - 720) < 2,
              $"{AppSettings.Current.PoolWinLeft:0},{AppSettings.Current.PoolWinTop:0} "
              + $"{AppSettings.Current.PoolWinWidth:0}×{AppSettings.Current.PoolWinHeight:0}");

        // ── Первое открытие: высота по умолчанию, та самая «на треть выше» ──
        AppSettings.Current.PoolWinWidth = 0;
        AppSettings.Current.PoolWinHeight = 0;
        var w3 = new PoolSettingsWindow(() => { });
        w3.Show(); Pump();
        var want = 750 * AppSettings.Current.FontScale * OverlayWindow.ClientScaleFor(w3);
        var cap  = Math.Max(240, SystemParameters.WorkArea.Height - 40);
        Check("без памяти окно открывается высоким",
              Math.Abs(w3.Height - Math.Min(want, cap)) < 2,
              $"{w3.Height:0} (было 560)");
        w3.Close(); Pump();
    }

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

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
