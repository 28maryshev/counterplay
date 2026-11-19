namespace Counterplay;

/// <summary>
/// Чем неудобен состав врагов: каким уроном бьют, сколько у них лечения,
/// контроля, щитов, танков, убийц и какой стиль. Один разбор на подбор
/// предметов (<see cref="BuildAdvisor"/>) и подбор рун (<see cref="RuneAdvisor"/>):
/// если считать в двух местах по-разному, подсказка предметов скажет «много
/// контроля (3)», а рун — «(2)» про тех же пятерых.
/// </summary>
public sealed record EnemyComp(
    double Phys, double Magic,     // доли урона, в сумме 1
    int Heal, int Cc, int LongCc, int Autos, int Burst, double Shield, int Tanks,
    ChampionTraits.Arch? Style)
{
    // ── Черты, которых нет в данных Riot ──────────────────────────────────
    //
    // Доли урона и классы чемпионов берём из матчей и Data Dragon, а «много
    // лечится», «много контролит», «раздаёт щиты» там не выражены никак. Списки
    // ручные и намеренно короткие: сюда попадают только те, чья черта решает
    // покупку, а не любой, у кого в ките есть хил или станище.

    /// Заметное лечение или вампиризм — против них берут срез лечения.
    private static readonly HashSet<string> Healers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Soraka", "Yuumi", "Nami", "Sona", "Seraphine", "Senna", "Milio", "Taric",
        "Vladimir", "Aatrox", "DrMundo", "Warwick", "Swain", "Sylas", "Nasus",
        "Fiora", "Sett", "Olaf", "Maokai", "Briar", "Gwen", "Zac", "Ivern",
        "Mundo", "Volibear", "Trundle", "Yorick", "Illaoi", "Katarina",
    };

    /// Долгий и жёсткий контроль — против него берут стойкость.
    private static readonly HashSet<string> HardCc = new(StringComparer.OrdinalIgnoreCase)
    {
        "Leona", "Nautilus", "Malphite", "Amumu", "Sejuani", "Rell", "Alistar",
        "Morgana", "Lissandra", "Veigar", "Thresh", "Blitzcrank", "Skarner",
        "Ornn", "Sion", "JarvanIV", "Vi", "Ashe", "Rakan", "Maokai", "Nunu",
        "Warwick", "Camille", "Galio", "Neeko", "Zac", "Poppy", "Taric",
    };

    /// Щиты в умениях. Дополняет тег «shield» движка — там отмечены не все, а
    /// разрушение щитов имеет смысл считать по обоим спискам сразу.
    private static readonly HashSet<string> Shielders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lulu", "Karma", "Janna", "Orianna", "Seraphine", "Ivern", "Milio",
        "Renata", "Taric", "Shen", "Riven", "Sivir", "Nocturne", "Morgana",
        "Lux", "Yasuo", "Sett", "Rakan",
    };

    /// Долгий контроль по ОДНОЙ цели: провокация Раммуса, связывание Морганы,
    /// страх Фиддлстикса, подавление Малзахара. От такого не спасает стойкость —
    /// она сокращает длительность, но не отменяет; нужен предмет, который снимет
    /// контроль (Микаэль на поддержке, Ртутные на остальных).
    private static readonly HashSet<string> LongCcKit = new(StringComparer.OrdinalIgnoreCase)
    {
        "Rammus", "Morgana", "FiddleSticks", "Malzahar", "Warwick", "Skarner",
        "Urgot", "Shen", "Ahri", "Evelynn", "Elise", "Varus", "Jhin", "Ashe",
        "Sion", "Zoe", "Lissandra", "Veigar", "Cassiopeia", "Swain", "Nami",
        "Thresh", "Neeko", "Twisted Fate", "TwistedFate", "Amumu",
    };

    /// Живёт ли чемпион лечением — по тому же списку, что и «лечение у врага».
    public static bool IsHealer(int champ)
    {
        var dd = DataDragon.DdId(champ);
        return dd.Length > 0 && Healers.Contains(dd);
    }

    /// Разобрать состав. null — судить не по чему: врагов нет или урон не
    /// оценить ни по матчам, ни по Data Dragon.
    public static EnemyComp? Read(
        IReadOnlyList<int> enemies,
        Func<int, (double Phys, double Magic, double True)?> damageShare)
    {
        if (enemies.Count == 0) return null;

        // ── Каким уроном нас будут бить ───────────────────────────────────
        double phys = 0, magic = 0;
        foreach (var e in enemies)
        {
            var d = damageShare(e);
            if (d is { } v && v.Phys + v.Magic > 0)
            {
                phys += v.Phys;
                magic += v.Magic;
            }
            else
            {
                // Данных по чемпиону нет — грубая оценка по Data Dragon.
                if (DataDragon.IsApChampion(e)) magic += 0.7; else phys += 0.7;
            }
        }
        var dmgSum = phys + magic;
        if (dmgSum <= 0) return null;
        phys /= dmgSum;
        magic /= dmgSum;

        // ── Чем этот состав неудобен ──────────────────────────────────────
        int heal = 0, cc = 0, tanks = 0, longCc = 0, autos = 0, burst = 0;
        double shield = 0;
        foreach (var e in enemies)
        {
            var dd = DataDragon.DdId(e);
            if (dd.Length > 0)
            {
                if (Healers.Contains(dd)) heal++;
                if (HardCc.Contains(dd)) cc++;
                if (LongCcKit.Contains(dd)) longCc++;
            }

            // Кто бьёт автоатаками. Их урон растёт со временем и режется не так,
            // как разовый удар: против него берут не просто броню, а предметы
            // против критов и скорости атаки.
            if (DataDragon.ClassTags(e).Contains("Marksman") || ChampionTags.Has(e, "hypercarry"))
                autos++;

            // Кто убивает с наскока. Убийцы и те, кто прыгает с бёрстом: против
            // них помогает не броня (её не хватит), а то, что даёт пережить
            // первые секунды, — стазис или щит на низком здоровье.
            if (DataDragon.ClassTags(e).Contains("Assassin")
                || (ChampionTags.Has(e, "burst") && ChampionTags.Has(e, "dive"))) burst++;

            // Щит бывает не только в умениях. Бойцы вроде Олафа берут Стеракса
            // или Шилдбоу, и щит там не меньше — а режется он тем же предметом.
            // Считаем их за половину: покупают такое не все и не всегда, а вот
            // энчантер со щитом в умениях даёт его гарантированно.
            if (Shielders.Contains(dd) || ChampionTags.Has(e, "shield")) shield += 1;
            else if (DataDragon.ClassTags(e).Contains("Fighter")) shield += 0.5;

            if (DataDragon.ClassTags(e).Contains("Tank")) tanks++;
        }

        return new EnemyComp(phys, magic, heal, cc, longCc, autos, burst, shield, tanks,
                             ChampionTraits.DominantStyle(enemies));
    }

    /// Ближний бой: в нём контроль решает — пока тебя держат, ты стоишь
    /// вплотную и получаешь всё. Поэтому бойцу, танку и убийце стойкость нужна
    /// уже от двух таких врагов, а тому, кто бьёт издалека, — от трёх.
    public static bool Close(int champ)
    {
        var c = DataDragon.ClassTags(champ);
        return c.Contains("Tank") || c.Contains("Fighter") || c.Contains("Assassin");
    }

    /// Контроля столько, что стоит брать стойкость.
    public bool ManyCc(int me) => Cc >= (Close(me) ? 2 : 3);
}
