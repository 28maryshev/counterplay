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
    // + Джанна/Ракан/Ли Син/Синь Жао/Поппи/Браум/Атрокс/Талия/Каин/Йоне.
    private static readonly HashSet<int> Knockup =
        [54, 62, 79, 12, 111, 14, 150, 526, 59, 131, 154, 106, 31, 516, 254, 20,
         40, 497, 64, 5, 78, 201, 266, 163, 141, 777];
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
    // Мощные AoE-ульты для тимфайта. + Рамбл/Энни/Бранд/Зиггс/Лиссандра/Галио/
    // Гнар/Диана/Аурелион — все с решающим AoE-ультом по сгруппированным.
    private static readonly HashSet<int> AoeUlt =
        [37, 21, 32, 54, 30, 9, 61, 518, 85, 147, 29, 15, 157, 777, 50,
         68, 1, 63, 115, 127, 3, 150, 131, 136];
    // Орианна — её шар + инициация = «шаровая молния».
    private const int Orianna = 61;
    // Хуки — гарантированный пик при подхвате.
    private static readonly HashSet<int> Hooks = [412, 53, 111, 555];
    // Глобальные ульты — давление по всей карте. + Ноктюрн/Гангпланк.
    private static readonly HashSet<int> Global = [98, 4, 80, 3, 22, 30, 56, 41];
    // Сплит-пушеры — сильная боковая линия под глобальное давление (1-3-1).
    private static readonly HashSet<int> Splitters = [114, 24, 23, 164, 83, 41, 39, 92];
    // Дайверы без тега dive (ассасины): Зед/Ката/Экко/Ренгар/Ноктюрн/Гекарим/Кияна/Нафири.
    private static readonly HashSet<int> DiveExtra = [238, 55, 245, 107, 56, 120, 246, 950];
    // Особые пары/якоря новых связок.
    private const int Kalista = 429, Xayah = 498, Rakan = 497, Ashe = 22, Caitlyn = 51;

    // Надёжный контроль по ОДИНОЧНОЙ цели (чарм/стан/корень/подавление/зацеп) —
    // под него гарантированно заходит бёрст и удаляет цель.
    private static readonly HashSet<int> Lockdown =
        [103, 25, 99, 518, 45, 134, 4, 1, 7, 142, 22, 90, 127, 89, 497, 111, 53, 412, 555, 72, 19, 131];
    // Бёрст-чемпионы, не помеченные тегом "burst" (известные ассасины/маги).
    private static readonly HashSet<int> BurstExtra = [238, 7, 55, 245, 910, 103];
    // Артиллерия — большая дальность урона (покой/осада издалека).
    // + Джейс/Эзреаль/Ког'Мав/Мэл — дальний прострел/поук.
    private static readonly HashSet<int> Artillery =
        [101, 161, 115, 99, 202, 110, 30, 76, 51, 235, 910, 142, 126, 81, 96, 800];
    // Ноктюрн — ульт ныряет и изолирует цель.
    private const int Nocturne = 56;

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

        // a = рекомендуемый, b = союзник. Тексты — в i18n (ключи pair.*), {0} = имя союзника.
        // 1. Подброс ↔ ульт Ясуо/Йоне
        if (A(AirborneUlt, champId) && A(Knockup, allyId))
            return ("airborne", Loc.T("pair.airborne.a", name));
        if (A(Knockup, champId) && A(AirborneUlt, allyId))
            return ("airborne", Loc.T("pair.airborne.b", name));

        // 2. Шар Орианны
        if (champId == Orianna && (A(Knockup, allyId) || Has(allyId, "engage")))
            return ("orianna", Loc.T("pair.orianna.a", name));
        if (allyId == Orianna && (A(Knockup, champId) || Has(champId, "engage")))
            return ("orianna", Loc.T("pair.orianna.b", name));

        // 3. Неуязвимость/спасение
        if (A(Immortality, champId) && A(DiveCarry, allyId))
            return ("immortal", Loc.T("pair.immortal.a", name));
        if (A(Immortality, allyId) && A(DiveCarry, champId))
            return ("immortal", Loc.T("pair.immortal.b", name));

        // 4. Энчантер + хрупкий кэрри
        if (A(Enchanters, champId) && A(ImmobileCarry, allyId))
            return ("enchanter", Loc.T("pair.enchanter.a", name));
        if (A(Enchanters, allyId) && A(ImmobileCarry, champId))
            return ("enchanter", Loc.T("pair.enchanter.b", name));

        // 5. Хук + добивание
        if (A(Hooks, champId) && (Has(allyId, "burst") || Has(allyId, "dive")))
            return ("hook", Loc.T("pair.hook.a", name));
        if (A(Hooks, allyId) && (Has(champId, "burst") || Has(champId, "dive")))
            return ("hook", Loc.T("pair.hook.b", name));

        // 5b. Бёрст-пик: один фиксирует цель контролем, другой удаляет бёрстом
        bool Burst(int id) => Has(id, "burst") || BurstExtra.Contains(id);
        if (A(Lockdown, allyId) && Burst(champId))
            return ("burst_pick", Loc.T("pair.burst_pick.a", name));
        if (A(Lockdown, champId) && Burst(allyId))
            return ("burst_pick", Loc.T("pair.burst_pick.b", name));

        // 5c. Изоляция Ноктюрна: ныряет на цель, остальные добивают
        if (champId == Nocturne && (A(Artillery, allyId) || Burst(allyId)))
            return ("isolate", Loc.T("pair.isolate.a", name));
        if (allyId == Nocturne && (A(Artillery, champId) || Burst(champId)))
            return ("isolate", Loc.T("pair.isolate.b", name));

        // 5d. Артиллерия: оба бьют с большой дистанции
        if (A(Artillery, champId) && A(Artillery, allyId))
            return ("artillery", Loc.T("pair.artillery.a", name));

        // 6. Канал-ульт (МФ/Картус) под контролем
        if (Has(champId, "channels_ult") && (Has(allyId, "hard_cc") || Has(allyId, "engage")))
            return ("channel", Loc.T("pair.channel.a", name));
        if (Has(allyId, "channels_ult") && (Has(champId, "hard_cc") || Has(champId, "engage")))
            return ("channel", Loc.T("pair.channel.b", name));

        // 7. Защита гиперкэрри пилом
        if (Has(champId, "peel") && (Has(allyId, "hypercarry") || A(ImmobileCarry, allyId)))
            return ("peel", Loc.T("pair.peel.a", name));

        // 8. Инициатор + дальний урон
        if ((Has(champId, "engage") || Has(champId, "hard_cc")) && (Has(allyId, "poke") || Has(allyId, "scale")))
            return ("frontline", Loc.T("pair.frontline.a", name));
        if ((Has(allyId, "engage") || Has(allyId, "hard_cc")) && (Has(champId, "poke") || Has(champId, "scale")))
            return ("frontline", Loc.T("pair.frontline.b", name));

        // 9. Два инициатора
        if ((Has(champId, "engage") || Has(champId, "hard_cc")) && (Has(allyId, "engage") || Has(allyId, "hard_cc")))
            return ("double_engage", Loc.T("pair.double_engage.a", name));

        return null;
    }

    /// Есть ли у чемпиона мощный AoE/канал-ульт по площади (для оценки вомбо-состава).
    public static bool HasAoeUlt(int id) =>
        AoeUlt.Contains(id) || ChampionTags.Has(id, "channels_ult");

    /// Умеет ли чемпион собрать/зафиксировать группу врагов (нокап/жёсткий контроль/
    /// инициация) — то, подо что «раскрываются» AoE-ульты команды.
    public static bool IsGrouper(int id) =>
        Knockup.Contains(id) || ChampionTags.Has(id, "hard_cc") || ChampionTags.Has(id, "engage");

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
                Loc.T("combo.airborne.name"),
                [.. ku, .. air],
                Loc.T("combo.airborne.desc"),
                forAlly ? Loc.T("combo.airborne.tipAlly") : Loc.T("combo.airborne.tipEnemy")));

        // 2. Шаровая молния: шар Орианны + инициатор
        if (ids.Contains(Orianna) && ku.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.orianna.name"),
                [Orianna, .. ku.Take(2)],
                Loc.T("combo.orianna.desc"),
                forAlly ? Loc.T("combo.orianna.tipAlly") : Loc.T("combo.orianna.tipEnemy")));

        // 2b. Влюблённые: Шая + Ракан — способности пары усиливают друг друга.
        if (ids.Contains(Xayah) && ids.Contains(Rakan))
            combos.Add(new TeamCombo(
                Loc.T("combo.lovers.name"),
                [Xayah, Rakan],
                Loc.T("combo.lovers.desc"),
                forAlly ? Loc.T("combo.lovers.tipAlly") : Loc.T("combo.lovers.tipEnemy")));

        // 2c. «Зов судьбы»: Калиста забрасывает энгейдж-союзника в тыл врага.
        var kalistaMates = ids.Where(id => id != Kalista && (Has(id, "engage") || Hooks.Contains(id))).ToList();
        if (ids.Contains(Kalista) && kalistaMates.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.kalista.name"),
                [Kalista, .. kalistaMates.Take(1)],
                Loc.T("combo.kalista.desc"),
                forAlly ? Loc.T("combo.kalista.tipAlly") : Loc.T("combo.kalista.tipEnemy")));

        // 2d. Изоляция Ноктюрна: ульт гасит карту и ныряет на цель, контроль/бёрст
        // добивает (статистика: Ноктюрн+Ари +8.7пп чистой синергии).
        var nocMates = ids.Where(id => id != Nocturne && (Lockdown.Contains(id) || Has(id, "burst"))).ToList();
        if (ids.Contains(Nocturne) && nocMates.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.isolate.name"),
                [Nocturne, .. nocMates.Take(2)],
                Loc.T("combo.isolate.desc"),
                forAlly ? Loc.T("combo.isolate.tipAlly") : Loc.T("combo.isolate.tipEnemy")));

        // 2e. Стрела через карту: ульт Эш находит цель — нырок влетает следом
        // (статистика: Эш+Диана +8.1пп).
        var arrowDivers = ids.Where(id => id != Ashe && (DiveCarry.Contains(id) || Has(id, "dive"))).ToList();
        if (ids.Contains(Ashe) && arrowDivers.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.arrow.name"),
                [Ashe, .. arrowDivers.Take(2)],
                Loc.T("combo.arrow.desc"),
                forAlly ? Loc.T("combo.arrow.tipAlly") : Loc.T("combo.arrow.tipEnemy")));

        // 2f. Ловушки под контроль: капканы Кейтлин под хук/сетап союзника
        // (статистика: Кейтлин+Бард +6.8пп).
        var trapSetters = ids.Where(id => id != Caitlyn &&
            (Hooks.Contains(id) || Has(id, "engage") || id is 432 or 25 or 143)).ToList();
        if (ids.Contains(Caitlyn) && trapSetters.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.trap.name"),
                [Caitlyn, .. trapSetters.Take(1)],
                Loc.T("combo.trap.desc"),
                forAlly ? Loc.T("combo.trap.tipAlly") : Loc.T("combo.trap.tipEnemy")));

        // 3. Бессмертие: Тарик/Зилеан/Кайл + дайв-кэрри
        var imm  = In(Immortality);
        var dive = In(DiveCarry);
        if (imm.Count > 0 && dive.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.immortal.name"),
                [.. imm, .. dive.Take(2)],
                Loc.T("combo.immortal.desc"),
                forAlly ? Loc.T("combo.immortal.tipAlly") : Loc.T("combo.immortal.tipEnemy")));

        // 4. Разгон гиперкэрри: энчантер + неподвижный кэрри
        var ench  = In(Enchanters);
        var carry = In(ImmobileCarry);
        if (ench.Count > 0 && carry.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.enchanter.name"),
                [.. ench, .. carry.Take(2)],
                Loc.T("combo.enchanter.desc"),
                forAlly ? Loc.T("combo.enchanter.tipAlly") : Loc.T("combo.enchanter.tipEnemy")));

        // 5. Пик через хук
        var hooks = In(Hooks);
        var burst = ids.Where(id => Has(id, "burst") || Has(id, "dive")).ToList();
        if (hooks.Count > 0 && burst.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.hook.name"),
                [.. hooks, .. burst.Take(2)],
                Loc.T("combo.hook.desc"),
                forAlly ? Loc.T("combo.hook.tipAlly") : Loc.T("combo.hook.tipEnemy")));

        // 6. Тимфайт-ульты (несколько мощных AoE)
        var aoe = In(AoeUlt);
        if (aoe.Count >= 2)
            combos.Add(new TeamCombo(
                Loc.T("combo.aoe.name"),
                [.. aoe.Take(3)],
                Loc.T("combo.aoe.desc"),
                forAlly ? Loc.T("combo.aoe.tipAlly") : Loc.T("combo.aoe.tipEnemy")));

        // 7. Бёрст-пик: контроль по одиночке + бёрст-удаление
        var lockd = In(Lockdown);
        var bursters = ids.Where(id => Has(id, "burst") || BurstExtra.Contains(id)).ToList();
        var pickInvolved = lockd.Concat(bursters).Distinct().ToList();
        if (lockd.Count > 0 && bursters.Count > 0 && pickInvolved.Count >= 2)
            combos.Add(new TeamCombo(
                Loc.T("combo.burstPick.name"),
                [.. lockd.Take(1), .. bursters.Take(2)],
                Loc.T("combo.burstPick.desc"),
                forAlly ? Loc.T("combo.burstPick.tipAlly") : Loc.T("combo.burstPick.tipEnemy")));

        // 8. Артиллерия: несколько дальнобойных
        var arty = In(Artillery);
        if (arty.Count >= 2)
            combos.Add(new TeamCombo(
                Loc.T("combo.artillery.name"),
                [.. arty.Take(3)],
                Loc.T("combo.artillery.desc"),
                forAlly ? Loc.T("combo.artillery.tipAlly") : Loc.T("combo.artillery.tipEnemy")));

        // 9. Глобальное давление
        var glob = In(Global);
        if (glob.Count >= 2)
            combos.Add(new TeamCombo(
                Loc.T("combo.global.name"),
                [.. glob.Take(3)],
                Loc.T("combo.global.desc"),
                forAlly ? Loc.T("combo.global.tipAlly") : Loc.T("combo.global.tipEnemy")));

        // 10. Сплит-пуш (1-3-1): глобальный ульт покрывает сильную боковую линию.
        var splitters = In(Splitters).Where(id => !glob.Contains(id)).ToList();
        if (glob.Count > 0 && splitters.Count > 0)
            combos.Add(new TeamCombo(
                Loc.T("combo.splitpush.name"),
                [.. glob.Take(1), .. splitters.Take(2)],
                Loc.T("combo.splitpush.desc"),
                forAlly ? Loc.T("combo.splitpush.tipAlly") : Loc.T("combo.splitpush.tipEnemy")));

        // 11. Тотальный дайв: три и более нырка — бэклайн врага не переживает заход.
        var divers = ids.Where(id => Has(id, "dive") || DiveCarry.Contains(id) || DiveExtra.Contains(id)).ToList();
        if (divers.Count >= 3)
            combos.Add(new TeamCombo(
                Loc.T("combo.diveball.name"),
                [.. divers.Take(3)],
                Loc.T("combo.diveball.desc"),
                forAlly ? Loc.T("combo.diveball.tipAlly") : Loc.T("combo.diveball.tipEnemy")));

        // Убираем дубли иконок: чемпион может попасть в обе роли связки
        // (например, и контроль, и бёрст) — в карточке он должен быть один раз.
        return combos
            .Select(c => c with { ChampionIds = c.ChampionIds.Distinct().ToList() })
            .ToList();
    }
}
