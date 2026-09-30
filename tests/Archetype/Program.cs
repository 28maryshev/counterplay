using System.Text;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Архетип чемпиона: фронт-ту-бэк, дайв или подлов.
///
/// Владелец про Пайка: «он должен быть dive чемпионом, разве нет?». Оказалось,
/// что да: Пайк был оттегирован как хук-ТАНК, в один ряд с Трешем и
/// Блицкранком, и полкита не было записано вовсе — невидимость на W, рывок на
/// E, добивающий ульт. Архетип считался чистым подловом.
///
/// Считается всё из рукописной таблицы `ChampionTags`, и дыра в ней ничем себя
/// не выдаёт: чемпион просто оказывается не в той трети треугольника, а от неё
/// зависят и подсказка по стилю, и «против их стиля» в оценке.
///
/// Поэтому тут не столько про Пайка, сколько про КЛАСС ошибки: чемпион, которого
/// сам Riot считает убийцей, не может остаться вовсе без признаков дайва.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const int Pyke = 555, Thresh = 412, Blitzcrank = 53, Nautilus = 111;

    /// Признаки, из которых складывается «дайв».
    private static readonly string[] DiveTags =
        ["dive", "burst", "mobility", "stealth", "aggressive", "kill_lane", "engage"];

    /// <summary>
    /// Стрелки, которым Riot приписал класс Assassin: Тристана за прыжок,
    /// Твич за невидимость. Дайверами они от этого не становятся — их дело
    /// стоять сзади и скейлиться, и теги это говорят верно.
    /// </summary>
    private static readonly HashSet<int> NotReallyAssassins = [18, 29];

    /// <summary>
    /// Известные дыры, до которых ещё не дошли руки. Список нарочно отдельный
    /// от предыдущего: там «мы так решили», а здесь «мы это видим и не
    /// поправили». Проверка следит, чтобы он не РОС.
    /// </summary>
    private static readonly HashSet<int> KnownGaps = [103, 157, 777]; // Ари, Ясуо, Йоне

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

        // ── Случай владельца ───────────────────────────────────────────────
        Check("Пайк — дайв", ChampionTraits.ChampArch(Pyke) == ChampionTraits.Arch.Dive,
              Where(Pyke));

        // А хук-танки при этом остались подловом: правка не должна была
        // утащить за собой весь класс хуков.
        foreach (var id in new[] { Thresh, Blitzcrank, Nautilus })
            Check($"   {DataDragon.Name(id)} по-прежнему подлов",
                  ChampionTraits.ChampArch(id) == ChampionTraits.Arch.PickPoke, Where(id));

        // ── Класс ошибки ───────────────────────────────────────────────────
        // Кого Riot считает убийцей — у того должен быть хоть один признак
        // дайва. Иначе он попадёт в подлов и будет «контрить» не тех.
        var gaps = DataDragon.GetAllIconUrls().Keys
            .Where(id => DataDragon.ClassTags(id).Contains("Assassin"))
            .Where(id => ChampionTags.Get(id).Count > 0)      // без тегов работает откат по классу
            .Where(id => !NotReallyAssassins.Contains(id))
            .Where(id => !DiveTags.Any(t => ChampionTags.Has(id, t)))
            .ToList();

        Console.WriteLine("  (убийцы без признаков дайва: "
                          + (gaps.Count == 0 ? "нет" : string.Join(", ", gaps.Select(DataDragon.Name))) + ")");

        Check("новых дыр не появилось", gaps.All(KnownGaps.Contains),
              string.Join(", ", gaps.Where(g => !KnownGaps.Contains(g)).Select(DataDragon.Name)) is { Length: > 0 } s
                  ? "новые: " + s : "нет");
        Check("   и Пайка среди них больше нет", !gaps.Contains(Pyke), Where(Pyke));

        // Список известных дыр должен таять, а не жить вечно: если его чистили,
        // проверку надо поправить следом.
        var stale = KnownGaps.Where(id => !gaps.Contains(id)).Select(DataDragon.Name).ToList();
        if (stale.Count > 0)
            Console.WriteLine($"  (дыры закрыты, уберите из списка: {string.Join(", ", stale)})");

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: архетипы на месте"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Чемпион с его вектором архетипа — чтобы в выводе было видно, почему.
    private static string Where(int id)
    {
        var (f, d, p) = ChampionTraits.Archetype(id);
        return $"фронт {f:0.0} / дайв {d:0.0} / подлов {p:0.0}";
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}  — {detail}");
        if (!ok) _fails++;
    }
}
