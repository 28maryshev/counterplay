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

    /// Доминирующий архетип одного чемпиона (для значка). null — неизвестен.
    public static Arch? ChampArch(int id)
    {
        var (f, d, p) = Archetype(id);
        if (f + d + p <= 0) return null;
        if (f >= d && f >= p) return Arch.FrontToBack;
        return d >= p ? Arch.Dive : Arch.PickPoke;
    }

    /// Человекочитаемый ярлык стиля команды по составу. Пусто, если чемпионов < 2.
    public static string StyleLabel(IReadOnlyList<int> ids)
    {
        if (ids.Count < 2) return "";
        var (f, d, p) = TeamArchetype(ids);
        if (f + d + p <= 0) return "";

        var max = Math.Max(f, Math.Max(d, p));
        // Второй по величине — чтобы отличить «явный» стиль от «смешанного».
        var second = (f == max ? Math.Max(d, p) : d == max ? Math.Max(f, p) : Math.Max(f, d));
        if (max < 0.45 || max - second < 0.12) return "Смешанный";

        if (f == max) return "Фронт-ту-бэк";
        return d == max ? "Дайв" : "Пик/пок";
    }

    // Инструменты (грубые 0..2) — из тегов ChampionTags.
    public static int Engage(int id)   => ChampionTags.Has(id, "engage") ? 2 : ChampionTags.Has(id, "hard_cc") ? 1 : 0;
    public static int Gapclose(int id) => ChampionTags.Has(id, "dive") ? 2 : ChampionTags.Has(id, "mobility") ? 1 : 0;
    public static int Peel(int id)     => ChampionTags.Has(id, "peel") ? 2 : ChampionTags.Has(id, "shield") || ChampionTags.Has(id, "heal") ? 1 : 0;
    public static int Disengage(int id)=> ChampionTags.Has(id, "disengage") ? 2 : ChampionTags.Has(id, "shield") ? 1 : 0;
    public static int Burst(int id)    => ChampionTags.Has(id, "burst") ? 2 : 0;
    public static bool LongRange(int id)=> ChampionTags.Has(id, "poke") || ChampionTags.Has(id, "range_poke") || ChampionTags.Has(id, "zone_control");

    // ── Уязвимости (для item value, п.1) ────────────────────────────────────
    // Вклад чемпиона в «CC-уязвимость» 0..1 (стак → враг берёт Mercs/тенасити).
    public static double CcWeight(int id) =>
        ChampionTags.Has(id, "hard_cc") ? 1.0 : ChampionTags.Has(id, "cc") ? 0.5 : 0.0;

    // Завязан на автоатаки/крит/он-хит (бьётся Steelcaps/Frozen Heart/Randuin's).
    private static readonly HashSet<int> AutoExtra =
        [157, 777, 11, 23, 24, 10, 114, 164, 2, 5]; // Ясуо, Йоне, Йи, Тринд, Джакс, Кайл, Фиора, Камилла, Олаф, Син Чжао
    private static readonly HashSet<int> CasterAdc = [81, 110, 161, 202]; // Эзреаль, Варус, ВелКоз, Джин — не крит/авто
    public static bool AutoReliant(int id) =>
        !CasterAdc.Contains(id) &&
        (DataDragon.ClassTags(id).Contains("Marksman") || AutoExtra.Contains(id));

    // Даёт командные щиты как ключевую ценность (бьётся Serpent's Fang).
    public static bool ShieldReliant(int id) => ChampionTags.Has(id, "shield");

    // Даёт хил/реген (свой или команде) — бьётся Grievous Wounds.
    private static readonly HashSet<int> HealExtra =
        [266, 8, 36, 19, 50, 517, 421, 223]; // Аатрокс, Влад, Мундо, Варвик, Свейн, Сайлас, РекСай?, Тамкенч
    public static bool HealReliant(int id) =>
        ChampionTags.Has(id, "heal") || HealExtra.Contains(id);

    public static bool ApBurst(int id) => DataDragon.IsApChampion(id) && Burst(id) >= 2;

    // ── Структурная синергия jg↔sup (п.4) ───────────────────────────────────
    // Сила в ранней игре 0..3.
    public static int EarlyPower(int id)
    {
        if (ChampionTags.Has(id, "aggressive") || ChampionTags.Has(id, "kill_lane")) return 3;
        if (ChampionTags.Has(id, "hypercarry")) return 0; // гиперкэрри слабы рано
        if (ChampionTags.Has(id, "scale"))      return 1;
        return 2;
    }

    // Сила жёсткого контроля 0..2.
    public static int HardCc(int id) =>
        ChampionTags.Has(id, "hard_cc") ? 2 : ChampionTags.Has(id, "cc") ? 1 : 0;

    // АДК, которым нужен инициатор-саппорт (агрессивные оллин-керри).
    private static readonly HashSet<int> EngageAdc = [145, 360, 119, 236, 895, 429, 18];
    // АДК-скейлеры/авто, которым лучше энчантер/пил (нейтрализовать линию, дать скейлить).
    private static readonly HashSet<int> ScaleAdc  = [901, 15, 222, 523, 96, 67, 29, 202];
    public static bool EngageDependentAdc(int id) => EngageAdc.Contains(id);
    public static bool ScaleAdcCarry(int id)      => ScaleAdc.Contains(id);
}
