using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Перекос по типу урона замечается даже у тех, кого Riot не разметил.
///
/// Владелец: «в команде 4 АП, а мне предлагают дальше АП брать». И правда:
/// Серафина стояла первой с +12,1, хотя пятым магом должна была получить
/// крупный штраф.
///
/// Причина — признак «маг или физик» брался ТОЛЬКО из оценок Data Dragon
/// (attack против magic). У семи чемпионов из 173 они равны, и у ЧЕТЫРЁХ там
/// нули: Riot не заполняет этот блок для новых. Серафина из таких — её не
/// считали ни магом, ни физиком, и баланс урона возвращал для неё ноль.
///
/// Теперь при равных оценках смотрим классы.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Seraphine = 147, Zoe = 142, Rumble = 68, Viktor = 112, Amumu = 32;
    private const int Alistar = 12, Leona = 89;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        if (!DataDragon.LoadFromCache("ru_RU"))
        {
            Console.WriteLine("справочника чемпионов нет — проверять нечего");
            return 0;
        }

        // ── Тот, кого Riot не разметил, всё равно опознан ──────────────────
        Check("Серафина опознана как маг", DataDragon.IsApChampion(Seraphine),
              DataDragon.IsApChampion(Seraphine) ? "да"
              : DataDragon.IsAdChampion(Seraphine) ? "сочли физиком" : "никем");

        // ── Размеченных не сломали ─────────────────────────────────────────
        Check("Зои по-прежнему маг", DataDragon.IsApChampion(Zoe) && !DataDragon.IsAdChampion(Zoe), "да");
        Check("Леона по-прежнему физик", DataDragon.IsAdChampion(Leona) && !DataDragon.IsApChampion(Leona), "да");

        // ── Пятый маг в маговой команде получает штраф ─────────────────────
        int[] apTeam  = [Zoe, Rumble, Viktor, Amumu];          // четыре мага
        int[] enemies = [429, 63, 19, 3, 777];                 // среди них два танка

        var (sera, seraStack, _) = ItemValue.DamageBalance(Seraphine, apTeam, enemies);
        Check("пятому магу — штраф за перекос", sera <= -5.0 && seraStack, $"{sera:+0.0;-0.0}");

        var (ali, _, _) = ItemValue.DamageBalance(Alistar, apTeam, enemies);
        Check("разбавляющему — наоборот, прибавка", ali > 0, $"{ali:+0.0;-0.0}");
        Check("   и он выигрывает у пятого мага", ali - sera >= 5.0,
              $"разница {ali - sera:0.0} очка");

        // ── Ни один чемпион не остался «никем» из-за пустых оценок ─────────
        // Ради этого правило и заводилось: нули в info у новых чемпионов.
        var unknown = All().Where(id => !DataDragon.IsApChampion(id) && !DataDragon.IsAdChampion(id)).ToList();
        Console.WriteLine($"  (без типа урона осталось: {unknown.Count} — "
                          + string.Join(", ", unknown.Take(6).Select(DataDragon.Name)) + ")");
        Check("нерасмеченных стало мало", unknown.Count <= 4, $"{unknown.Count}");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: перекос по урону виден и у новых чемпионов"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Все чемпионы, которых знает справочник.
    private static IEnumerable<int> All() =>
        DataDragon.GetAllIconUrls().Keys;

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
