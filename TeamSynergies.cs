namespace Counterplay;

/// <summary>Распознанная командная связка (комбо нескольких чемпионов).</summary>
public sealed record TeamCombo(
    string             Name,         // короткое название связки
    IReadOnlyList<int> ChampionIds,  // участники (для иконок)
    string             Description,  // что связка делает
    string             Tip);         // как играть (союз.) / чего опасаться (враг)

/// <summary>
/// Курируемая база командных синергий. В отличие от ChampionTags.DetectSynergies
/// (синергия рекомендуемого чемпиона с союзниками) — ищет готовые комбо ВНУТРИ
/// состава команды: подброс+ульт, бессмертие, разгон гиперкэрри и т.д.
/// championId — корректные id из Data Dragon.
/// </summary>
public static class TeamSynergies
{
    // Подбросы в воздух (активируют ульт Ясуо/Йоне, собирают группу под AoE).
    private static readonly HashSet<int> Knockup =
        [54, 62, 79, 12, 111, 14, 150, 526, 59, 131, 154, 106, 31, 516, 254, 20];
    // Ясуо / Йоне — ульт по подброшенным.
    private static readonly HashSet<int> AirborneUlt = [157, 777];
    // Энчантеры — баффы скорости атаки/передвижения, щиты, хилл.
    private static readonly HashSet<int> Enchanters = [16, 37, 40, 117, 267, 350, 888, 902, 43];
    // Неподвижные гиперкэрри, которым нужен разгон и пил.
    private static readonly HashSet<int> ImmobileCarry = [96, 222, 29, 67, 523, 51, 22, 202];
    // Дающие неуязвимость/защиту на союзника (ульт).
    private static readonly HashSet<int> Immortality = [44, 26, 10];
    // Дайв-кэрри — окупают неуязвимость, ныряя в тиму.
    private static readonly HashSet<int> DiveCarry = [11, 23, 24, 114, 59, 157, 777, 164, 2, 39, 266, 234];
    // Мощные AoE-ульты для тимфайта.
    private static readonly HashSet<int> AoeUlt = [37, 21, 32, 54, 30, 9, 61, 518, 85, 147];
    // Орианна — её шар + инициация = «шаровая молния».
    private const int Orianna = 61;
    // Хуки — гарантированный пик при подхвате.
    private static readonly HashSet<int> Hooks = [412, 53, 111, 555];
    // Глобальные ульты — давление по всей карте.
    private static readonly HashSet<int> Global = [98, 4, 80, 3, 22, 30];

    /// <summary>
    /// Развёрнутое объяснение «почему рекомендуемый чемпион хорошо сочетается
    /// с конкретным союзником» — простым языком, для новичков. null — если
    /// заметной семантической связки между ними нет.
    /// champId — рекомендуемый, allyId — уже выбранный союзник.
    /// </summary>
    // Kind — тип связки (для дедупликации одинаковых по смыслу объяснений),
    // Text — само объяснение простым языком.
    public static (string Kind, string Text)? ExplainPair(int champId, int allyId, string allyRole)
    {
        if (champId == 0 || allyId == 0 || champId == allyId) return null;

        var name = DataDragon.Name(allyId);
        bool Has(int id, string tag) => ChampionTags.Has(id, tag);
        bool A(HashSet<int> set, int id) => set.Contains(id);

        // a = рекомендуемый, b = союзник
        // 1. Подброс ↔ ульт Ясуо/Йоне
        if (A(AirborneUlt, champId) && A(Knockup, allyId))
            return ("airborne", $"{name} подбрасывает врагов в воздух — это включает твой ультимейт, и ты бьёшь им сразу по всей подброшенной команде.");
        if (A(Knockup, champId) && A(AirborneUlt, allyId))
            return ("airborne", $"Ты подбрасываешь врагов в воздух, а {name} тут же влетает ультом по всем подброшенным — мощное комбо в драке.");

        // 2. Шар Орианны
        if (champId == Orianna && (A(Knockup, allyId) || Has(allyId, "engage")))
            return ("orianna", $"Повесь свой шар на {name}: когда он заходит в драку, твой ультимейт стянет и взорвёт всех врагов вокруг него.");
        if (allyId == Orianna && (A(Knockup, champId) || Has(champId, "engage")))
            return ("orianna", $"Ты заходишь в драку первым, а {name} вешает на тебя шар и ультом собирает врагов вокруг — огромный урон по площади.");

        // 3. Неуязвимость/спасение
        if (A(Immortality, champId) && A(DiveCarry, allyId))
            return ("immortal", $"Твой ультимейт делает {name} неуязвимым — он сможет нырнуть в самую гущу врагов и не умереть.");
        if (A(Immortality, allyId) && A(DiveCarry, champId))
            return ("immortal", $"{name} своим ультом сделает тебя неуязвимым или спасёт от смерти — можешь нырять в драку смелее.");

        // 4. Энчантер + хрупкий кэрри
        if (A(Enchanters, champId) && A(ImmobileCarry, allyId))
            return ("enchanter", $"{name} наносит огромный урон, но хрупкий и без рывков. Ты прикрываешь его щитами/лечением и разгоняешь — так он раскрывается на полную.");
        if (A(Enchanters, allyId) && A(ImmobileCarry, champId))
            return ("enchanter", $"Ты сильный, но хрупкий кэрри. {name} прикроет тебя щитами и лечением и усилит — держись рядом и спокойно наноси урон.");

        // 5. Хук + добивание
        if (A(Hooks, champId) && (Has(allyId, "burst") || Has(allyId, "dive")))
            return ("hook", $"Ты вытаскиваешь врага крюком из строя, а {name} мгновенно добивает пойманного своим бёрст-уроном.");
        if (A(Hooks, allyId) && (Has(champId, "burst") || Has(champId, "dive")))
            return ("hook", $"{name} цепляет врага крюком и вытаскивает его — ты добиваешь пойманного своим уроном. Лёгкие убийства на линии.");

        // 6. Канал-ульт (МФ/Картус) под контролем
        if (Has(champId, "channels_ult") && (Has(allyId, "hard_cc") || Has(allyId, "engage")))
            return ("channel", $"Пока {name} держит врагов под контролем на месте, ты успеваешь выпустить по ним весь свой ультимейт.");
        if (Has(allyId, "channels_ult") && (Has(champId, "hard_cc") || Has(champId, "engage")))
            return ("channel", $"Ты задерживаешь врагов контролем, а {name} в это время прожимает мощный ульт по стоящим на месте — большой урон.");

        // 7. Защита гиперкэрри пилом
        if (Has(champId, "peel") && (Has(allyId, "hypercarry") || A(ImmobileCarry, allyId)))
            return ("peel", $"{name} — главный источник урона команды, но уязвимый. Ты отгоняешь от него врагов, чтобы он спокойно бил в драке.");

        // 8. Инициатор + дальний урон
        if ((Has(champId, "engage") || Has(champId, "hard_cc")) && (Has(allyId, "poke") || Has(allyId, "scale")))
            return ("frontline", $"Ты начинаешь драки и связываешь врагов контролем, прикрывая {name}, который наносит урон с дистанции.");
        if ((Has(allyId, "engage") || Has(allyId, "hard_cc")) && (Has(champId, "poke") || Has(champId, "scale")))
            return ("frontline", $"{name} начинает драки и связывает врагов контролем — ты в это время безопасно наносишь урон со спины.");

        // 9. Два инициатора
        if ((Has(champId, "engage") || Has(champId, "hard_cc")) && (Has(allyId, "engage") || Has(allyId, "hard_cc")))
            return ("double_engage", $"Вы оба умеете начинать драки и ловить врагов в контроль — вместе легко ловить одиночек и выигрывать пики.");

        return null;
    }

    public static List<TeamCombo> Detect(IReadOnlyList<(int Id, string Role)> team, bool forAlly)
    {
        var ids = team.Where(t => t.Id != 0).Select(t => t.Id).ToHashSet();
        var combos = new List<TeamCombo>();

        List<int> In(HashSet<int> set) => ids.Where(set.Contains).ToList();
        bool Has(int id, string tag) => ChampionTags.Has(id, tag);

        // 1. Подброс → ульт Ясуо/Йоне
        var ku  = In(Knockup);
        var air = In(AirborneUlt);
        if (ku.Count > 0 && air.Count > 0)
            combos.Add(new TeamCombo(
                "Связка ультов: подброс → Ясуо/Йоне",
                [.. ku, .. air],
                "Подброс в воздух активирует ультимейт Ясуо/Йоне по всей сгруппированной команде.",
                forAlly
                    ? "Инициируйте подбросом — Ясуо/Йоне влетает ультом следом. Бейте по сгруппированным."
                    : "Не группируйтесь и не подставляйтесь под подброс — это их сигнал к вомбо-комбо. Держите рассеивание/очистку (ртуть, QSS)."));

        // 2. Шаровая молния: шар Орианны + инициатор
        if (ids.Contains(Orianna) && ku.Count > 0)
            combos.Add(new TeamCombo(
                "Шаровая молния (Орианна + инициация)",
                [Orianna, .. ku.Take(2)],
                "Шар на инициаторе: тот ныряет, Орианна ультом стягивает и сносит всю группу.",
                forAlly
                    ? "Отдайте шар на ныряющего инициатора и бейте следом за его заходом."
                    : "Следите, на ком шар Орианны — на нём будет заход. Рассредоточьтесь до их инициации."));

        // 3. Бессмертие: Тарик/Зилеан/Кайл + дайв-кэрри
        var imm  = In(Immortality);
        var dive = In(DiveCarry);
        if (imm.Count > 0 && dive.Count > 0)
            combos.Add(new TeamCombo(
                "Бессмертие (защитный ульт + дайв)",
                [.. imm, .. dive.Take(2)],
                "Ульт неуязвимости/спасения позволяет кэрри нырять в самую гущу без риска умереть.",
                forAlly
                    ? "Ныряйте агрессивно — держите защитный ульт на пик урона кэрри."
                    : "Не тратьте фокус, пока активна их неуязвимость — переждите её, затем убивайте кэрри."));

        // 4. Разгон гиперкэрри: энчантер + неподвижный кэрри
        var ench  = In(Enchanters);
        var carry = In(ImmobileCarry);
        if (ench.Count > 0 && carry.Count > 0)
            combos.Add(new TeamCombo(
                "Разгон гиперкэрри (энчантер + кэрри)",
                [.. ench, .. carry.Take(2)],
                "Бафы скорости атаки/передвижения и щиты превращают неподвижного кэрри в машину урона.",
                forAlly
                    ? "Держитесь за кэрри, баффайте и пильте его — он ваш главный источник урона."
                    : "Убейте кэрри быстро или зайдите ему за спину: без энчантера он беспомощен. Душите его на линии."));

        // 5. Пик через хук
        var hooks = In(Hooks);
        var burst = ids.Where(id => Has(id, "burst") || Has(id, "dive")).ToList();
        if (hooks.Count > 0 && burst.Count > 0)
            combos.Add(new TeamCombo(
                "Пик через хук",
                [.. hooks, .. burst.Take(2)],
                "Зацеп вырывает цель из строя, бёрст-чемпионы добивают её до прихода помощи.",
                forAlly
                    ? "Ищите хук по одиночкам — сразу фокусьте пойманного всей командой."
                    : "Не ходите поодиночке и держите дистанцию от хук-чемпионов. Уважайте вижн в реке."));

        // 6. Тимфайт-ульты (несколько мощных AoE)
        var aoe = In(AoeUlt);
        if (aoe.Count >= 2)
            combos.Add(new TeamCombo(
                "Тимфайт-ульты (двойное AoE)",
                [.. aoe.Take(3)],
                "Несколько мощных AoE-ультов накладываются и сносят сгруппированную команду разом.",
                forAlly
                    ? "Затевайте драки в узких местах, когда оба ульта готовы — пробросьте их по группе врага."
                    : "Не стойте кучей и не лезьте в чокпоинты при их готовых ультах. Разбивайтесь и заходите по одному."));

        // 7. Глобальное давление
        var glob = In(Global);
        if (glob.Count >= 2)
            combos.Add(new TeamCombo(
                "Глобальное давление",
                [.. glob.Take(3)],
                "Глобальные ульты дают помощь и пики по всей карте — сильное давление в сплит-пуше.",
                forAlly
                    ? "Давите в сплите и забирайте кроссмап-пики глобалками — растягивайте врага."
                    : "Не переоставайтесь в стычках 4в5 — их глобалки выравнивают числа. Уважайте отсутствие их героев на карте."));

        return combos;
    }
}
