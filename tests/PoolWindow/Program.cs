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
        try
        {
            if (before is not null) File.Delete(PoolsPath);
            Run();
        }
        finally
        {
            AppSettings.Current.WinratesAllTime = wasAllTime;
            AppSettings.SaveQuiet();
            if (before is not null) File.WriteAllText(PoolsPath, before);
            else if (File.Exists(PoolsPath)) File.Delete(PoolsPath);
            Console.WriteLine("\nсвои пулы и настройки возвращены на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: период переключается, ник на плитке виден"
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

        w.Close();
        Pump();
        app.Shutdown();
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
