namespace Counterplay;

/// <summary>
/// Слой качественных атрибутов чемпионов (архетип, нейтральность, инструменты).
/// Авто-сид: выводится из тегов <see cref="ChampionTags"/> (ручная разметка) и
/// классов Data Dragon (Fighter/Tank/Mage/…). Используется для драфт-фич:
/// нейтральные пики, трифекта композиций, анти-стиль.
/// </summary>
public static class ChampionTraits
{
    public enum Arch { FrontToBack, Dive, PickPoke }

    // Нейтральные чемпионы — лезут в любую компу без жёсткой завязки (ценны в блайнде).
    private static readonly HashSet<int> NeutralSet =
    [
        103, // Ахри
        61,  // Орианна
        267, // Нами
        43,  // Карма
        222, // Джинкс
        523, // Афелиос
        15,  // Сивир
        81,  // Эзреаль
        254, // Вай
        104, // Грейвз
        3,   // Галио
        113, // Седжуани
    ];

    // championId → вектор архетипа (frontToBack, dive, pickPoke), нормированный.
    public static (double F2B, double Dive, double Pick) Archetype(int id)
    {
        double f = 0, d = 0, p = 0;
        var t = ChampionTags.Get(id);

        void Add(string tag, double F, double D, double P)
        {
            if (t.Contains(tag)) { f += F; d += D; p += P; }
        }

        // Фронт-ту-бэк: танки/фронт/защита/скейл-керри сзади
        Add("tank",         0.7, 0.0, 0.0);
        Add("juggernaut",   0.6, 0.0, 0.0);
        Add("peel",         0.4, 0.0, 0.0);
        Add("shield",       0.3, 0.0, 0.0);
        Add("heal",         0.3, 0.0, 0.0);
        Add("scale",        0.4, 0.0, 0.0);
        Add("hypercarry",   0.5, 0.0, 0.0);
        // Дайв: заход/бёрст/мобильность по бэклайну
        Add("dive",         0.0, 0.6, 0.0);
        Add("burst",        0.0, 0.4, 0.1);
        Add("mobility",     0.0, 0.3, 0.0);
        Add("stealth",      0.0, 0.3, 0.0);
        Add("aggressive",   0.0, 0.3, 0.0);
        Add("kill_lane",    0.0, 0.2, 0.0);
        Add("engage",       0.2, 0.4, 0.0);
        Add("hard_cc",      0.2, 0.1, 0.2);
        // Пик/пок: дальний рейндж, ловля, простел
        Add("poke",         0.0, 0.0, 0.6);
        Add("range_poke",   0.0, 0.0, 0.5);
        Add("zone_control", 0.0, 0.0, 0.4);
        Add("hook",         0.0, 0.0, 0.5);
        Add("cc",           0.0, 0.0, 0.2);
        Add("channels_ult", 0.0, 0.0, 0.2);

        // Фолбэк по классу Data Dragon, если тегов нет.
        if (f + d + p == 0)
        {
            foreach (var c in DataDragon.ClassTags(id))
                switch (c)
                {
                    case "Tank":     f += 1.0; break;
                    case "Fighter":  f += 0.6; d += 0.4; break;
                    case "Assassin": d += 1.0; break;
                    case "Mage":     p += 0.7; f += 0.3; break;
                    case "Marksman": f += 0.6; p += 0.4; break;
                    case "Support":  f += 0.5; p += 0.5; break;
                }
        }

        var s = f + d + p;
        return s <= 0 ? (0, 0, 0) : (f / s, d / s, p / s);
    }

    public static (double F2B, double Dive, double Pick) TeamArchetype(IEnumerable<int> ids)
    {
        double f = 0, d = 0, p = 0;
        foreach (var id in ids) { var (a, b, c) = Archetype(id); f += a; d += b; p += c; }
        var s = f + d + p;
        return s <= 0 ? (0, 0, 0) : (f / s, d / s, p / s);
    }

    public static Arch Dominant(IEnumerable<int> ids)
    {
        var (f, d, p) = TeamArchetype(ids);
        if (f >= d && f >= p) return Arch.FrontToBack;
        return d >= p ? Arch.Dive : Arch.PickPoke;
    }

    public static bool IsNeutral(int id) => NeutralSet.Contains(id);

    // Инструменты (грубые 0..2) — из тегов ChampionTags.
    public static int Engage(int id)   => ChampionTags.Has(id, "engage") ? 2 : ChampionTags.Has(id, "hard_cc") ? 1 : 0;
    public static int Gapclose(int id) => ChampionTags.Has(id, "dive") ? 2 : ChampionTags.Has(id, "mobility") ? 1 : 0;
    public static int Peel(int id)     => ChampionTags.Has(id, "peel") ? 2 : ChampionTags.Has(id, "shield") || ChampionTags.Has(id, "heal") ? 1 : 0;
    public static int Disengage(int id)=> ChampionTags.Has(id, "disengage") ? 2 : ChampionTags.Has(id, "shield") ? 1 : 0;
    public static int Burst(int id)    => ChampionTags.Has(id, "burst") ? 2 : 0;
    public static bool LongRange(int id)=> ChampionTags.Has(id, "poke") || ChampionTags.Has(id, "range_poke") || ChampionTags.Has(id, "zone_control");
}
