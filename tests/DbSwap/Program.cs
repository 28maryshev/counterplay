using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Counterplay;
using Microsoft.Data.Sqlite;

namespace Counterplay.Tests;

/// <summary>
/// Подмена базы, скачанной фоном.
///
/// Раньше база качалась на экране загрузки: нашлась новая версия — программа
/// вставала и до конца скачивания не годилась ни на что. Теперь она качается
/// рядом, а рабочую подменяет отдельным шагом. Держится это на двух вещах, и
/// обе тут проверяются.
///
/// 1. **Замок на файле.** Движок держит базу открытой, и подменить её в этот
///    момент нельзя. Больше того: одного Dispose мало — Microsoft.Data.Sqlite
///    кладёт закрытые соединения в пул, и файл остаётся открытым. Поэтому перед
///    подменой пул чистится явно. Если эту строку убрать, скачанная база не
///    применится НИКОГДА, а программа будет качать её каждый запуск.
///
/// 2. **Сама подмена.** На боевых именах файлов: data.db, data.db.new и
///    отложенная версия рядом. Свои файлы отодвигаются и возвращаются на место.
/// </summary>
internal static class Program
{
    private static int _fails;

    private static string Db        => DataDb.LocalPath;
    private static string Pending   => Db + ".new";
    private static string PendingV  => Db + ".new.ver";
    private static string VersionF  => Path.Combine(Path.GetDirectoryName(Db)!, "data-version.txt");

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Lock();
        Swap();
        Engine();
        Bar();

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: скачанная база подменяет рабочую"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Замок на файле базы ──────────────────────────────────────────────
    private static void Lock()
    {
        Console.WriteLine("Замок на файле базы");
        var dir = Path.Combine(Path.GetTempPath(), "cp-dbswap");
        Directory.CreateDirectory(dir);
        var db  = Path.Combine(dir, "a.db");
        var nw  = Path.Combine(dir, "a.db.new");
        foreach (var f in Directory.GetFiles(dir)) Try(() => File.Delete(f));

        Make(db); Make(nw);
        SqliteConnection.ClearAllPools();

        bool Move()
        {
            try { File.Move(nw, db, overwrite: true); return true; }
            catch { return false; }
        }

        // Под открытым соединением — нельзя. Ради этого подмена и ждёт
        // перерыва между драфтами, а не делается сразу после скачивания.
        var live = new SqliteConnection($"Data Source={db}");
        live.Open();
        using (var c = live.CreateCommand()) { c.CommandText = "SELECT count(*) FROM t"; c.ExecuteScalar(); }
        Check("под открытым соединением подменить нельзя", !Move(), "не даёт");

        // После Dispose — как повезёт: соединение уходит в пул, и файл обычно
        // остаётся открытым. Это не утверждение, а справка: наш код верен в
        // обоих случаях, но если тут «уже можно», значит пул перестал держать.
        live.Dispose();
        var afterDispose = Move();
        Console.WriteLine($"  (справка) после Dispose подменить {(afterDispose ? "УЖЕ можно" : "ещё нельзя — держит пул")}");

        // А вот это — то, на чём всё держится.
        if (afterDispose) Make(nw);
        SqliteConnection.ClearAllPools();
        Check("после очистки пула подмена проходит", Move(), "прошла");

        Try(() => Directory.Delete(dir, true));
    }

    // ── 3. Полоса внизу сайдбара ────────────────────────────────────────────
    //
    // Ради неё всё и затевалось: пока база качается, программа должна работать.
    // Проверяем не картинку, а последствие — фоновая полоса НЕ уводит окно на
    // экран загрузки, в отличие от полосы первого запуска.
    private static void Bar()
    {
        Console.WriteLine();
        Console.WriteLine("Полоса внизу сайдбара");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Запомненный размер окна на время проверки отключаем: ниже сравниваются
        // ШИРИНЫ (драфт против экрана загрузки), а раскладка задаёт их насильно и
        // делает обе одинаковыми. Поймалось на живом — запущенная программа
        // сохранила туда размер сайдбара, и проверка встала на ровном месте.
        var cfg = AppSettings.Current;
        var savedSize = (cfg.DraftWidth, cfg.DraftHeight);
        cfg.DraftWidth = 0; cfg.DraftHeight = 0;
        try
        {

        var w = new OverlayWindow { Left = -4000, Top = -4000 };
        ShowHidden(w);
        Pump();
        w.UpdateRecommendations([Rec()], Draft(), null);
        Pump();
        var draftWidth = w.Width;

        var dbBar  = w.FindName("SideDlDb")  as UIElement;
        var appBar = w.FindName("SideDlApp") as UIElement;
        Check("полос в подвале две", dbBar is not null && appBar is not null,
              $"база {(dbBar is null ? "нет" : "есть")}, релиз {(appBar is null ? "нет" : "есть")}");
        if (dbBar is null || appBar is null) return;

        Check("пока не качаем — обеих не видно",
              dbBar.Visibility == Visibility.Collapsed && appBar.Visibility == Visibility.Collapsed,
              $"{dbBar.Visibility} / {appBar.Visibility}");

        // ── База ───────────────────────────────────────────────────────────
        w.ShowSideProgress("Обновляю базу данных… 3,1 МБ/с", 0.4, "db");
        Pump();
        Check("полоса базы появилась", dbBar.Visibility == Visibility.Visible,
              dbBar.Visibility.ToString());
        Check("полоса релизов при этом молчит", appBar.Visibility == Visibility.Collapsed,
              appBar.Visibility.ToString());
        Check("доля дошла до полосы",
              w.FindName("SideDlDbFillT") is ScaleTransform t && Math.Abs(t.ScaleX - 0.4) < 0.01,
              (w.FindName("SideDlDbFillT") as ScaleTransform)?.ScaleX.ToString("0.00") ?? "—");
        Check("процент подписан",
              (w.FindName("SideDlDbPct") as TextBlock)?.Text == "40%",
              (w.FindName("SideDlDbPct") as TextBlock)?.Text ?? "—");
        Check("подробности ушли в подсказку",
              (dbBar as FrameworkElement)?.ToolTip as string is { } tip && tip.Contains("МБ/с"),
              (dbBar as FrameworkElement)?.ToolTip as string ?? "—");

        // Главное: окно осталось в раскладке драфта. Программа работает.
        Check("окно не ушло на экран загрузки", Math.Abs(w.Width - draftWidth) < 2,
              $"{w.Width:0} против {draftWidth:0}");

        // ── Обе разом ──────────────────────────────────────────────────────
        // Сторожа ходят раз в час и раз в три — рано или поздно совпадут.
        // Одна полоса на двоих показывала бы то одно, то другое.
        w.ShowSideProgress("Загружаю обновление… 10%", 0.1, "app");
        Pump();
        Check("обе полосы держатся разом",
              dbBar.Visibility == Visibility.Visible && appBar.Visibility == Visibility.Visible,
              $"{dbBar.Visibility} / {appBar.Visibility}");
        Check("и у каждой своя доля",
              (w.FindName("SideDlDbFillT")  as ScaleTransform)?.ScaleX is { } a && Math.Abs(a - 0.4) < 0.01 &&
              (w.FindName("SideDlAppFillT") as ScaleTransform)?.ScaleX is { } b && Math.Abs(b - 0.1) < 0.01,
              $"{(w.FindName("SideDlDbFillT") as ScaleTransform)?.ScaleX:0.00} / "
              + $"{(w.FindName("SideDlAppFillT") as ScaleTransform)?.ScaleX:0.00}");

        w.HideSideProgress("db");
        Pump();
        Check("своя полоса убирается", dbBar.Visibility == Visibility.Collapsed,
              dbBar.Visibility.ToString());
        Check("чужая остаётся догорать", appBar.Visibility == Visibility.Visible,
              appBar.Visibility.ToString());
        w.HideSideProgress("app");
        Pump();
        Check("и вторая убирается", appBar.Visibility == Visibility.Collapsed,
              appBar.Visibility.ToString());

        // Для сравнения — полоса первого запуска. Она как раз обязана уводить на
        // экран загрузки: считать там не на чем.
        w.ShowProgress("Скачиваю базу данных… 10%", 0.1);
        Pump();
        Check("полоса первого запуска по-прежнему уводит на экран загрузки",
              w.Width < draftWidth - 1, $"{w.Width:0} против {draftWidth:0}");

        w.Close();
        Pump();

        }
        finally { (cfg.DraftWidth, cfg.DraftHeight) = savedSize; }

        app.Shutdown();
    }

    private static DraftState Draft()
    {
        var me = new DraftPlayer(0, 0, 0, "utility", true);
        return new DraftState(
            MyTeam: [me], TheirTeam: [], MyTeamBans: [], TheirTeamBans: [],
            Me: me, MyPosition: "utility", DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: false, Bench: [], IsAram: false,
            MyPickActionId: -1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: -1, MyBanInProgress: false);
    }

    private static Recommendation Rec() => new(
        ChampionId: 412, Score: 1.0, BaseDelta: 1.0, DirectDelta: 0, OtherDelta: 0,
        SynergyDelta: 0, ComfortDelta: 0, StyleDelta: 0, Reasons: ["проверка"]);

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    // ── 2. Настоящая подмена ────────────────────────────────────────────────
    private static void Swap()
    {
        Console.WriteLine();
        Console.WriteLine("Подмена на боевых именах");

        // Своя база в профиле не годится: она открыта запущенной программой, и
        // проверка на ней либо не пройдёт, либо испортит человеку данные.
        // Берём пустую папку и те же имена файлов, что в работе.
        var dir = Path.Combine(Path.GetTempPath(), "cp-dbswap-dir");
        Try(() => Directory.Delete(dir, true));
        Directory.CreateDirectory(dir);
        DataDb.DirOverride = dir;

        try
        {
            File.WriteAllText(Db, "старая");
            File.WriteAllText(VersionF, "gold:26.18");
            File.WriteAllText(Pending, "новая");
            File.WriteAllText(PendingV, "gold:26.19");

            Check("скачанное видно как готовое", DataDb.SwapReady, "да");
            Check("подмена проходит", DataDb.ApplySwap(), "да");
            Check("рабочая база стала скачанной", File.ReadAllText(Db) == "новая",
                  File.ReadAllText(Db));
            Check("версия подтянулась вместе с ней", File.ReadAllText(VersionF).Trim() == "gold:26.19",
                  File.ReadAllText(VersionF).Trim());
            Check("хвостов не осталось", !File.Exists(Pending) && !File.Exists(PendingV),
                  File.Exists(Pending) ? "лежит .new" : "чисто");
            Check("больше подменять нечего", !DataDb.SwapReady && !DataDb.ApplySwap(), "нечего");

            // Половинчатый случай: база скачалась, а версию записать не успели
            // (оборвали на полуслове). Подмена всё равно должна пройти — иначе
            // файл завис бы навсегда; версия останется прежней, и при следующей
            // сверке её просто скачают заново.
            File.WriteAllText(Pending, "ещё новее");
            Check("без файла версии подмена тоже идёт", DataDb.ApplySwap(), "да");
            Check("и база обновилась", File.ReadAllText(Db) == "ещё новее", File.ReadAllText(Db));
            Check("версия при этом осталась прежней", File.ReadAllText(VersionF).Trim() == "gold:26.19",
                  File.ReadAllText(VersionF).Trim());

            // Пустой файл — не база. Такое остаётся от оборванной закачки, и
            // подменять им рабочую нельзя.
            File.WriteAllText(Pending, "");
            Check("пустой огрызок за базу не принимается", !DataDb.SwapReady, "нет");

            // ── Главный случай: базу перед подменой читали ──────────────────
            // Так делает движок: открыл, поработал, закрыл. Соединение ушло в
            // пул, файл остался занятым — и без очистки пула внутри ApplySwap
            // подмена бы не прошла. Это и есть тот отказ, из-за которого
            // скачанная фоном база не применилась бы никогда.
            Try(() => File.Delete(Db));
            Make(Db);
            using (var c = new SqliteConnection($"Data Source={Db}"))
            {
                c.Open();
                using var q = c.CreateCommand();
                q.CommandText = "SELECT count(*) FROM t";
                q.ExecuteScalar();
            }
            File.WriteAllText(Pending, "после движка");
            var swapped = DataDb.ApplySwap();
            Check("подмена идёт и после работы с базой", swapped, swapped ? "да" : "не пустил файл");
            // Читаем осторожно: если подмена не прошла, файл ещё держит пул, и
            // обычный ReadAllText свалится с ошибкой доступа вместо вердикта.
            var now = Read(Db);
            Check("рабочая база действительно заменена", now == "после движка", now);
        }
        finally
        {
            DataDb.DirOverride = null;
            Try(() => Directory.Delete(dir, true));
            Console.WriteLine();
            Console.WriteLine("временная папка убрана, боевая база не тронута");
        }
    }

    // ── 4. Движок после подмены ─────────────────────────────────────────────
    //
    // 8 ноября у владельца центр окна драфта висел на заглушке до перезапуска.
    // Связь с клиентом оборвалась, сессия вышла исключением и бросила движок
    // открытым — он держал базу. Скачанная фоном база не подменялась, а
    // программа при неудачной подмене оставалась вовсе без движка.
    private static void Engine()
    {
        Console.WriteLine();
        Console.WriteLine("Движок после подмены");

        var dir = Path.Combine(Path.GetTempPath(), "cp-dbswap-engine");
        Try(() => Directory.Delete(dir, true));
        Directory.CreateDirectory(dir);
        DataDb.DirOverride = dir;
        var clock = new DateTime(2026, 11, 8, 17, 0, 0, DateTimeKind.Utc);
        var swap = new EngineSwap(() => clock);
        SqliteConnection? leaked = null;

        try
        {
            Make(Db, 1);                 // рабочая база
            Make(Pending, 2);            // скачанная фоном
            SqliteConnection.ClearAllPools();

            // Брошенный движок прошлой сессии: соединение открыто, и его
            // никто не закроет.
            leaked = new SqliteConnection($"Data Source={Db}");
            leaked.Open();
            using (var q = leaked.CreateCommand()) { q.CommandText = "SELECT x FROM t"; q.ExecuteScalar(); }

            var first = new FakeEngine(Db);
            var engine = swap.Apply(first, () => new FakeEngine(DataDb.LocalPath), out var swapped);
            Check("файл держат — подмена не проходит", !swapped, swapped ? "прошла" : "не прошла");
            Check("движок при этом есть", engine is not null,
                  engine is null ? "движка нет: рекомендаций не будет до перезапуска" : "есть");
            Check("и считает на прежней базе", engine?.Value() == 1, $"читает {engine?.Value()}");
            Check("прежний движок закрыт", first.Disposed, first.Disposed ? "да" : "держит файл");

            // Пауза: на каждом событии лобби движок не пересобирается.
            clock += EngineSwap.RetryPause - TimeSpan.FromMinutes(1);
            var again = swap.Apply(engine, () => new FakeEngine(DataDb.LocalPath), out _);
            Check("в паузе после неудачи движок не трогают",
                  ReferenceEquals(again, engine) && engine is { Disposed: false },
                  ReferenceEquals(again, engine) ? "тот же" : "пересобран");

            // Брошенный отпустили, пауза вышла — подмена проходит.
            leaked.Dispose();
            leaked = null;
            clock += TimeSpan.FromMinutes(2);
            var fresh = swap.Apply(engine, () => new FakeEngine(DataDb.LocalPath), out swapped);
            Check("после паузы подмена проходит", swapped, swapped ? "да" : "нет");
            Check("движок на свежей базе", fresh?.Value() == 2, $"читает {fresh?.Value()}");
            fresh?.Dispose();

            // Закрытие при выходе из сессии исключением. Движок за сессию
            // пересобирается — закрыть надо последний, а не первый.
            Make(Pending, 3);
            SqliteConnection.ClearAllPools();
            FakeEngine? live = new(Db);
            var firstLive = live;
            try
            {
                using (EngineSwap.CloseOnExit(() => live))
                {
                    live.Dispose();
                    live = new FakeEngine(Db);
                    throw new IOException("связь с клиентом оборвалась");
                }
            }
            catch (IOException) { }
            Check("обрыв сессии закрывает последний движок", firstLive != live && live.Disposed,
                  live.Disposed ? "закрыт" : "держит файл");
            clock += EngineSwap.RetryPause;
            var afterDrop = swap.Apply<FakeEngine>(null, () => new FakeEngine(DataDb.LocalPath), out swapped);
            Check("после обрыва база подменяется", swapped && afterDrop?.Value() == 3,
                  swapped ? $"читает {afterDrop?.Value()}" : "файл держат");
            afterDrop?.Dispose();

            // Сессия должна этим пользоваться: Program разложен на части.
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Counterplay.csproj")))
                root = root.Parent;
            var session = root is null ? "" : string.Concat(
                Directory.GetFiles(Path.Combine(root.FullName, "src", "App"), "Program*.cs")
                         .Select(File.ReadAllText));
            Check("сессия закрывает движок при любом выходе",
                  session.Contains("EngineSwap.CloseOnExit(() => engine)"), "Program*.cs");
            Check("сессия подменяет базу через EngineSwap", session.Contains("engineSwap.Apply("), "Program*.cs");
        }
        finally
        {
            leaked?.Dispose();
            SqliteConnection.ClearAllPools();
            DataDb.DirOverride = null;
            Try(() => Directory.Delete(dir, true));
        }
    }

    /// Подставной движок: держит базу открытой так же, как настоящий.
    private sealed class FakeEngine : IDisposable
    {
        private readonly SqliteConnection _db;
        public bool Disposed { get; private set; }

        public FakeEngine(string path)
        {
            _db = new SqliteConnection($"Data Source={path}");
            _db.Open();
            Value();
        }

        public long Value()
        {
            using var q = _db.CreateCommand();
            q.CommandText = "SELECT x FROM t";
            return (long)(q.ExecuteScalar() ?? 0L);
        }

        public void Dispose() { _db.Dispose(); Disposed = true; }
    }

    private static void Make(string p, int value = 1)
    {
        Try(() => File.Delete(p));
        using var c = new SqliteConnection($"Data Source={p}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"CREATE TABLE IF NOT EXISTS t(x); INSERT INTO t VALUES({value});";
        cmd.ExecuteNonQuery();
    }

    /// Прочитать файл, не падая: занятый или отсутствующий даёт пометку.
    private static string Read(string p)
    {
        try { return File.ReadAllText(p); }
        catch (Exception e) { return "не прочитать — " + e.GetType().Name; }
    }

    private static void Try(Action a) { try { a(); } catch { /* нечего чистить */ } }

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
