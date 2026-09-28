using System.IO;
using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Справочник чемпионов берётся с диска, без сети.
///
/// Раньше каждый запуск ходил в Data Dragon за versions.json и champion.json и
/// ЖДАЛ ответа, прежде чем показать хоть что-то. В хорошей сети это доли
/// секунды, в плохой — до двадцати (два запроса по десять), а если Riot прилёг,
/// ровно столько же впустую. Справочник при этом меняется раз в патч.
///
/// Проверка работает на ВЫДУМАННОЙ локали: боевые файлы игрока не трогаются.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Fake = "zz_ZZ";   // такой локали не бывает

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "ddragon");
    private static string Json => Path.Combine(Dir, $"champion-{Fake}.json");
    private static string Ver  => Path.Combine(Dir, $"champion-{Fake}.ver");

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;

        Directory.CreateDirectory(Dir);
        try { Run(); }
        finally
        {
            foreach (var p in new[] { Json, Ver })
                try { File.Delete(p); } catch { }
            Console.WriteLine("\nвыдуманная локаль убрана, свой справочник не тронут");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: справочник читается с диска"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    private static void Run()
    {
        // ── Кэша нет — честно говорим «нет» ────────────────────────────────
        try { File.Delete(Json); } catch { }
        try { File.Delete(Ver); } catch { }
        Check("без кэша не притворяемся", !DataDragon.LoadFromCache(Fake), "false");

        // ── Кэш есть — читаем ──────────────────────────────────────────────
        File.WriteAllText(Json, """
        {"data":{"Soraka":{"key":"16","id":"Soraka","name":"Сорака",
                           "tags":["Support","Mage"],"info":{"attack":2,"magic":7}},
                 "Garen":{"key":"86","id":"Garen","name":"Гарен",
                          "tags":["Fighter","Tank"],"info":{"attack":7,"magic":1}}}}
        """);
        File.WriteAllText(Ver, "16.19.1");

        Check("кэш прочитан", DataDragon.LoadFromCache(Fake), "да");
        Check("имя на месте", DataDragon.Name(16) == "Сорака", DataDragon.Name(16));
        Check("патч взят из кэша", DataDragon.Version == "16.19.1", DataDragon.Version);
        Check("английский идентификатор на месте", DataDragon.DdId(86) == "Garen", DataDragon.DdId(86));
        Check("классы разобраны", DataDragon.ClassTags(16).Contains("Support"),
              string.Join("/", DataDragon.ClassTags(16)));
        Check("маг опознан по урону", DataDragon.IsApChampion(16) && !DataDragon.IsAdChampion(16), "да");
        Check("боец опознан по урону", DataDragon.IsAdChampion(86) && !DataDragon.IsApChampion(86), "да");
        Check("ссылка на иконку собирается",
              DataDragon.IconUrl(86).EndsWith("/16.19.1/img/champion/Garen.png"), DataDragon.IconUrl(86));

        // ── Битый кэш не роняет запуск ─────────────────────────────────────
        // Файл могли оборвать на полуслове: место кончилось, свет выключили.
        File.WriteAllText(Json, "{\"data\":{\"Sora");
        var survived = true;
        try { Check("битый кэш отвергается", !DataDragon.LoadFromCache(Fake), "false"); }
        catch (Exception e) { survived = false; Check("битый кэш отвергается", false, e.GetType().Name); }
        Check("и не роняет программу", survived, "да");

        // ── Нет файла версии — кэшу верить нельзя ──────────────────────────
        // Без него непонятно, какого патча картинки просить.
        File.WriteAllText(Json, "{\"data\":{}}");
        try { File.Delete(Ver); } catch { }
        Check("без файла версии кэш не берётся", !DataDragon.LoadFromCache(Fake), "false");
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
