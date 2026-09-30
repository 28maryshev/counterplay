using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Предметы «контрят тебя и команду» показываются, только если их есть кому
/// собрать.
///
/// Владелец на живом драфте: «у них танк только Наутилус, который вряд ли
/// соберёт эти итемы, а на топе Триндамир, который пойдёт в керри». И правда:
/// список считался ТОЛЬКО по своей команде и на врага не смотрел вовсе.
///
/// Броня, магзащита и сопротивление — предметы ПЕРЕДНЕЙ ЛИНИИ. Нет у врага
/// двоих, кто их берёт, — и предупреждение висит в пустоте.
/// </summary>
internal static class Items
{
    // Свои: четыре мага, кандидат — пятый. Перекос, за который «накажут».
    private static readonly int[] ApTeam = [142, 68, 112, 32];   // Зои, Рамбл, Виктор, Амуму
    private const int Seraphine = 147;

    private const int Nautilus = 111, Trynda = 23, Kalista = 429, Brand = 63, Yone = 777, Zed = 238;
    private const int Ornn = 516, Galio = 3;

    /// Возвращает число провалов; сами проверки печатает переданный Check.
    public static int Run(Action<string, bool, string> check)
    {
        var fails = 0;
        void Say(string what, bool ok, string detail)
        {
            check(what, ok, detail);
            if (!ok) fails++;
        }

        // ── Случай владельца: из танков один Наутилус ──────────────────────
        var one = ItemValue.CounterItems(Seraphine, ApTeam, [Nautilus, Trynda, Kalista, Brand, Yone]);
        Say("с одним танком у врага предметов нет", one.Count == 0, $"{one.Count} шт.");

        // ── Двое, кто их правда соберёт ────────────────────────────────────
        var two = ItemValue.CounterItems(Seraphine, ApTeam, [Nautilus, Ornn, Kalista, Brand, Yone]);
        Say("с двумя — предупреждаем", two.Count > 0, $"{two.Count} шт.");

        // ── Враги ещё не показались ────────────────────────────────────────
        // Судить не о чем: лучше предупредить, чем промолчать.
        var blind = ItemValue.CounterItems(Seraphine, ApTeam, []);
        Say("пока врагов не видно — как раньше", blind.Count > 0, $"{blind.Count} шт.");

        // ── Состав врага из одних кэрри ────────────────────────────────────
        var carries = ItemValue.CounterItems(Seraphine, ApTeam, [Kalista, Brand, Yone, Trynda, Zed]);
        Say("против одних кэрри не пугаем", carries.Count == 0, $"{carries.Count} шт.");

        // Галио — и маг, и танк: он и в передней линии, и Морелло ему по руке.
        Say("танк-маг считается передней линией", ChampionTraits.IsTanky(Galio), "да");

        return fails;
    }
}
