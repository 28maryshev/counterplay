using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Избранное в двух половинах.
///
/// Раньше звезда и переключатель были одним и тем же: отметил дуо — личный
/// выбор пропадал, и вернуться к нему можно было только отметив пул заново.
/// Теперь звезда живёт в своей половине, а какая из них в деле — решает кнопка
/// в сайдбаре.
///
/// Работаем с НАСТОЯЩИМ pools.json (другого пути к PoolStore нет). Прежнее
/// содержимое сохраняем и возвращаем на место, даже если проверка упала.
/// </summary>
internal static class Program
{
    private static int _fails;

    /// Файл пулов ПРОВЕРКИ, не игрока.
    ///
    /// Здесь стоял настоящий `%APPDATA%`, и проверка его УДАЛЯЛА — «чистый
    /// лист, прежнее вернём в finally». Любой сбой в середине, или живая
    /// программа, пишущая в тот же файл, — и возвращать уже нечего. Так у
    /// владельца и пропали пулы, собиравшиеся месяцами.
    private static string Path_ => System.IO.Path.Combine(PoolStore.DirOverride!, "pools.json");

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;   // не сорить в журнал игрока

        // Пулы — в свою папку, НЕ в файл игрока. Проверки однажды уже оставили
        // в его pools.json тестовые аккаунты, а свои пулы — пустыми.
        PoolStore.DirOverride = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "counterplay-test-pools", "DuoPools");
        System.IO.Directory.CreateDirectory(PoolStore.DirOverride);
        // Reload здесь НЕ зовём: ниже проверяется переход со старого формата, а
        // хранилище читает файл один раз за запуск — прочитать его надо уже
        // после того, как Migration() положит старый формат на место.


        var existed = File.Exists(Path_);
        var before = existed ? File.ReadAllText(Path_) : null;
        try
        {
            if (existed) File.Delete(Path_);   // чистый лист, прежнее вернём в finally
            // Переход со старого формата проверяем ПЕРВЫМ: PoolStore читает файл
            // один раз за запуск, и после любого обращения подменять его поздно.
            Migration();
            Run();
        }
        finally
        {
            if (before is not null) File.WriteAllText(Path_, before);
            else if (File.Exists(Path_)) File.Delete(Path_);
            Console.WriteLine(existed ? "\nпрежний pools.json возвращён на место"
                                      : "\nсвоего pools.json не было — временный убран");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: избранное живёт в двух половинах"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// <summary>
    /// Старый файл: звезды в нём нет, есть только «что было активным». После
    /// обновления звёзды обязаны загореться сами — иначе игрок откроет
    /// настройки и увидит, что все его отметки пропали.
    /// </summary>
    private static void Migration()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        // ActiveKind = 2 (Duo), плюс запомненный по очередям личный пул.
        File.WriteAllText(Path_, """
        {
          "old-acc": {
            "Pools":    [{"Id":"oldp","Name":"OLD"}],
            "DuoPools": [{"Id":"oldd","FriendName":"OLD DUO"}],
            "ActiveKind": 2,
            "ActiveId": "oldd",
            "ByQueue": { "solo": { "Kind": 1, "Id": "oldp" } }
          }
        }
        """);

        PoolStore.SetAccount("old-acc", "old");
        Check("старый активный дуо стал избранным", PoolStore.Favourite(PoolKind.Duo) == "oldd",
              PoolStore.Favourite(PoolKind.Duo) ?? "—");
        Check("личный из памяти очередей тоже зажёгся", PoolStore.Favourite(PoolKind.Pool) == "oldp",
              PoolStore.Favourite(PoolKind.Pool) ?? "—");
    }

    private static void Run()
    {
        PoolStore.SetAccount("test-puuid-duopools", "tester");
        var a = PoolStore.Current();
        a.Pools.Add(new ChampPool { Id = "p1", Name = "MAIN" });
        a.DuoPools.Add(new DuoPool { Id = "d1", FriendName = "Harribon" });
        PoolStore.Persist();

        // Звезда на личном пуле.
        PoolStore.SetFavourite(PoolKind.Pool, "p1");
        Check("личный отмечен", PoolStore.Favourite(PoolKind.Pool) == "p1",
              PoolStore.Favourite(PoolKind.Pool) ?? "—");

        // Звезда на дуо — личная НЕ гаснет. Это и есть суть правки.
        PoolStore.SetFavourite(PoolKind.Duo, "d1");
        Check("дуо отмечен", PoolStore.Favourite(PoolKind.Duo) == "d1",
              PoolStore.Favourite(PoolKind.Duo) ?? "—");
        Check("личный остался отмечен", PoolStore.Favourite(PoolKind.Pool) == "p1",
              PoolStore.Favourite(PoolKind.Pool) ?? "—");

        // Кнопка в сайдбаре переключает, что в деле; звёзды не трогает.
        PoolStore.SetActive(PoolKind.Duo, "d1");
        Check("в деле дуо", PoolStore.Current().ActiveKind == PoolKind.Duo,
              PoolStore.Current().ActiveKind.ToString());
        PoolStore.SetActive(PoolKind.Pool, "p1");
        Check("в деле личный", PoolStore.Current().ActiveKind == PoolKind.Pool,
              PoolStore.Current().ActiveKind.ToString());
        Check("обе звезды на месте после переключений",
              PoolStore.Favourite(PoolKind.Pool) == "p1" && PoolStore.Favourite(PoolKind.Duo) == "d1",
              $"{PoolStore.Favourite(PoolKind.Pool)} / {PoolStore.Favourite(PoolKind.Duo)}");

        // Повторный клик по звезде снимает её — и только её.
        PoolStore.SetFavourite(PoolKind.Duo, "d1");
        Check("дуо снят повторным кликом", PoolStore.Favourite(PoolKind.Duo) is null,
              PoolStore.Favourite(PoolKind.Duo) ?? "—");
        Check("личный не задет", PoolStore.Favourite(PoolKind.Pool) == "p1",
              PoolStore.Favourite(PoolKind.Pool) ?? "—");

        // Сняли звезду с той половины, что была в деле → подбор возвращается
        // в обычный режим, иначе работал бы пул без звезды.
        PoolStore.SetActive(PoolKind.Duo, "d1");
        PoolStore.SetFavourite(PoolKind.Duo, "d1");   // ставим обратно
        PoolStore.SetFavourite(PoolKind.Duo, "d1");   // и снимаем, будучи в деле
        Check("снятие активной звезды выключает пул",
              PoolStore.Current().ActiveKind == PoolKind.Normal,
              PoolStore.Current().ActiveKind.ToString());

        CardColours();
    }

    /// <summary>
    /// Цвет плашки говорит, ИЗ КАКОГО пула пик.
    ///
    /// Личный — синий, дуо — бирюзовый, тем же тоном, что пометка дуо в полосе
    /// ролей. Одно время бирюзой красили оба, и разницы было не видно: человек
    /// с личным пулом получал метку дуо. Владелец это и заметил.
    /// </summary>
    private static void CardColours()
    {
        Console.WriteLine();
        var solo    = new FullRecCard { FromPool = true };
        var duo     = new FullRecCard { FromPool = true, FromDuoPool = true };
        var neither = new FullRecCard();

        Check("личный пул — синий", solo.CardBg == "#22315C8A", solo.CardBg);
        Check("дуо-пул — бирюзовый", duo.CardBg == "#2E3FD9C8", duo.CardBg);
        Check("и это разные цвета", solo.CardBg != duo.CardBg,
              $"{solo.CardBg} против {duo.CardBg}");
        Check("без пула плашка обычная", neither.CardBg != solo.CardBg && neither.CardBg != duo.CardBg,
              neither.CardBg);

        // Выбранная карточка обводится золотом, но из пула она или нет — всё
        // равно видно по заливке: подпись «пик из пула» одна на все случаи.
        var picked = new FullRecCard { FromPool = true, IsSelected = true };
        Check("выбор не стирает пометку пула", picked.CardBg == solo.CardBg, picked.CardBg);
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
