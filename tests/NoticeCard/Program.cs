using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Высота плашки сообщений не прыгает.
///
/// Сообщений может быть несколько, и они сменяются по кругу сами. Высота панели
/// менялась вместе с ними: короткое — сайдбар поджался, длинное — вырос.
/// Человек в этот момент ничего не делает, а окно под ним дёргается.
///
/// Теперь плашка держит высоту самого длинного из АКТИВНЫХ. Проверяем так:
/// меряем плашку с одним коротким сообщением, потом добавляем к нему длинное —
/// и коротким по-прежнему первым. Высота обязана вырасти до длинного и на нём
/// остаться.
///
/// Свой notice.json сохраняется и возвращается на место.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Short = "Коротко.";
    private const string Long  =
        "Крупное обновление пулов чемпионов: делитесь пулами с друзьями через "
        + "экспорт и импорт файлов, мгновенно создавайте дуо-составы, объединяя "
        + "два личных набора. Вся информация и инструменты теперь в настройках пула.";

    private static string Path_ => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterplay", "notice.json");

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        var before = File.Exists(Path_) ? File.ReadAllText(Path_) : null;
        Notice.Frozen = true;   // иначе окно сходит за настоящими и затрёт наши
        try { Run(); }
        finally
        {
            Notice.Frozen = false;
            if (before is not null) File.WriteAllText(Path_, before);
            else if (File.Exists(Path_)) File.Delete(Path_);
            Console.WriteLine("\nсвои сообщения возвращены на место");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: высота плашки не прыгает"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // ── Одно короткое: плашка по нему и сидит ──────────────────────────
        Write(Short);
        var (aloneH, aloneMin) = Measure();
        Check("с одним сообщением минимум не навязан", aloneMin == 0, $"{aloneMin:0}");

        // ── Одно длинное: вот его-то высоту и надо повторить ───────────────
        Write(Long);
        var (longH, _) = Measure();
        Check("длинное заметно выше короткого", longH > aloneH + 30,
              $"{longH:0} против {aloneH:0}");

        // ── Короткое и длинное, показывается короткое ──────────────────────
        Write(Short, Long);
        var (bothH, bothMin) = Measure();

        // Минимум задаётся ТЕКСТУ, а не плашке: живую плашку измерить не вышло
        // (WPF отдаёт размер от прежнего текста), и меряется только текст.
        // Поэтому сверяем не с высотой плашки, а с тем, что минимум вообще есть.
        Check("минимум тексту задан", bothMin > 0, $"{bothMin:0}");
        // А вот это — то, ради чего всё: на КОРОТКОМ сообщении плашка стоит
        // ровно такая же, как была бы на длинном. Высота не прыгает.
        Check("на коротком плашка не съёживается", bothH > aloneH + 30,
              $"{bothH:0} против {aloneH:0} у одиночного короткого");
        Check("и на коротком высота как у длинного", Math.Abs(bothH - longH) < 2,
              $"{bothH:0} против {longH:0}");

        // ── Длинное убрали — минимум снимается ─────────────────────────────
        // Иначе плашка навсегда осталась бы раздутой под сообщение, которого
        // больше нет.
        Write(Short);
        var (backH, backMin) = Measure();
        Check("длинное убрали — минимум снят", backMin == 0, $"{backMin:0}");
        Check("и высота вернулась", Math.Abs(backH - aloneH) < 2,
              $"{backH:0} против {aloneH:0}");

        app.Shutdown();
    }

    /// Открыть окно и снять высоту плашки.
    private static (double Height, double Min) Measure()
    {
        Notice.LoadCached();
        Console.WriteLine($"    (активных сообщений: {Notice.Active().Count})");
        var w = new OverlayWindow { Left = -4000, Top = -4000 };
        ShowHidden(w);
        w.ShowReadyPhase("Lobby");   // плашка живёт на экране готовности
        Pump();
        var card = (Border)w.FindName("BetaCard")!;
        var text = (TextBlock)w.FindName("BetaText")!;
        var r = (card.ActualHeight, text.MinHeight);
        w.Close();
        Pump();
        return r;
    }

    private static void Write(params string[] texts)
    {
        // text — ОБЪЕКТ по языкам, не строка: сообщения переводятся на все
        // языки программы, и строкой они просто не разбираются.
        var items = string.Join(",", texts.Select(t =>
            $"{{\"text\":{{\"ru\":{System.Text.Json.JsonSerializer.Serialize(t)}}}," +
            "\"kind\":\"info\",\"head\":\"update\"}"));
        File.WriteAllText(Path_, $"{{\"rotateSec\":45,\"items\":[{items}]}}");
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
