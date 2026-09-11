using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Counterplay;

/// Что предмет ДАЁТ — в терминах, которыми думают в драфте: броня, сопротивление
/// магии, лечение врага, пробивание, стойкость.
///
/// Разбираем то, что отдаёт Data Dragon: числовые характеристики берём из stats,
/// остальное — из меток и описания. Метки не покрывают «Тяжёлые раны» и разрушение
/// щитов, поэтому описание читаем на английском независимо от языка интерфейса:
/// ключевые слова там стабильны, а в переводах формулировки гуляют от патча к патчу.
public static class ItemFacts
{
    public sealed record Fact(
        int Id, string Name,
        double Armor, double MagicResist, double Health,
        bool AntiHeal,     // «Тяжёлые раны» — режет лечение и восстановление
        bool Tenacity,     // стойкость: короче контроль
        bool ArmorPen,     // пробивание брони / летальность
        bool MagicPen,     // пробивание сопротивления магии
        bool PercentPen,   // ПРОЦЕНТНОЕ пробивание или урон по запасу здоровья:
                           // именно оно работает против набравших броню и HP,
                           // тогда как летальность — против хрупких целей
        bool AntiShield,   // режет щиты
        bool Stasis,       // стазис или воскрешение — пережить прыжок и взрыв
        bool SpellShield,  // щит от одного умения — против размена и подлова
        bool Cleanse,      // снимает контроль: с себя (Ртутные) или с союзника (Микаэль)
        bool BuffsAllyAttack,  // хил или щит даёт союзнику скорость атаки (Ardent)
        bool BuffsAllyPower,   // хил или щит даёт союзнику силу умений (Staff of Flowing Water)
        bool Lifesteal,    // вампиризм/омнивамп — свой отхил
        bool TargetHealth, // урон считается от здоровья ЦЕЛИ (Кинжал, Кракен, Лиандри):
                           // против набравших здоровье работает не хуже процентного
                           // пробивания, а у стрелка часто это единственный ответ
        bool ShredsArmor,  // сбивает броню цели (Чёрный тесак)
        bool ShredsMr,     // сбивает сопротивление магии цели (Проклятие, Злобность)
        bool Lifeline,     // щит на низком здоровье (Стеракс, Пасть, Щитолом) —
                           // пережить наскок и взрыв там, где нет стазиса
        bool SelfHealAmp,  // усиливает лечение НА СЕБЕ (Лик духа)
        bool AutoDefense,  // меньше урона от автоатак: от критов (Омен Рандуина)
                           // или от ударов вообще (Стальные набойки)
        bool SlowsAttack,  // сбивает скорость атаки вокруг (Ледяное сердце)
        bool Magical,      // предмет с силой умений: по нему понятно, каким уроном
                           // бьёт то, что от него зависит
        bool Boots,
        bool PureTank,     // только здоровье и сопротивления: ни ауры, ни актива,
                           // ни ускорения умений — такое покупают танки и бойцы,
                           // а не маги с энчантерами
        int Gold);

    // Что предмет даёт СОЮЗНИКУ после хила или щита. Ищем в куске описания после
    // фразы про хил/щит: сами характеристики предмета перечислены выше и сбили бы
    // проверку — сила умений есть у обоих.
    private static bool AllyBuff(string desc, string what)
    {
        var i = desc.IndexOf("an ally", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        return desc[i..].Contains(what, StringComparison.OrdinalIgnoreCase);
    }

    /// Урон, который считается от здоровья ЦЕЛИ: процент максимального, текущего
    /// или недостающего. Riot пишет это двумя способами — «2% max Health magic
    /// damage» и «damage … increased based on their missing Health», — поэтому
    /// смотрим и вперёд, и назад, но в пределах предложения.
    ///
    /// Слово «your» всё отменяет: «6% of your max Health» у Сердца титана — это
    /// про своё здоровье, а не про чужое, и против толстой цели не помогает.
    private static bool TargetHealthDamage(string desc)
    {
        foreach (Match m in Regex.Matches(
                     desc, @"(max|maximum|current|missing)\s+Health[^.]{0,40}damage",
                     RegexOptions.IgnoreCase))
        {
            var before = desc[Math.Max(0, m.Index - 14)..m.Index];
            if (!before.Contains("your", StringComparison.OrdinalIgnoreCase)) return true;
        }

        foreach (Match m in Regex.Matches(
                     desc, @"damage[^.]{0,60}based on[^.]{0,25}(max|maximum|current|missing)\s+Health",
                     RegexOptions.IgnoreCase))
        {
            if (!m.Value.Contains("your", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static Dictionary<int, Fact> _facts = new();
    private static string? _loadedVersion;

    public static bool Loaded => _facts.Count > 0;

    public static Fact? Of(int id) => _facts.GetValueOrDefault(id);

    public static async Task LoadAsync(CancellationToken ct)
    {
        if (_loadedVersion == DataDragon.Version && _facts.Count > 0) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var json = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{DataDragon.Version}/data/en_US/item.json", ct);
            using var doc = JsonDocument.Parse(json);

            var map = new Dictionary<int, Fact>();
            foreach (var it in doc.RootElement.GetProperty("data").EnumerateObject())
            {
                if (!int.TryParse(it.Name, out var id)) continue;
                var v = it.Value;

                // Только то, что можно купить на Ущелье: трофеи арены, зелья и
                // предметы других режимов в сборке чемпиона не нужны.
                if (v.TryGetProperty("maps", out var maps)
                    && maps.TryGetProperty("11", out var m11)
                    && m11.ValueKind == JsonValueKind.False) continue;
                if (v.TryGetProperty("gold", out var goldEl)
                    && goldEl.TryGetProperty("purchasable", out var pEl)
                    && pEl.ValueKind == JsonValueKind.False) continue;
                // Эликсиры и зелья в сборке не нужны: они дают те же свойства
                // (стойкость, защиту) на две минуты и сбивали бы подбор.
                if (v.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array
                    && tg.EnumerateArray().Any(x => x.GetString() == "Consumable")) continue;

                var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                // Описание приходит с разметкой: «<attention>35%</attention>
                // <ornnBonus>Armor Penetration</ornnBonus>». Теги вырезаем, иначе
                // число и то, к чему оно относится, разделены мусором.
                var raw = (v.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "")
                        + (v.TryGetProperty("passive", out var ps) ? ps.GetString() ?? "" : "");
                var desc = Regex.Replace(raw, "<[^>]+>", " ");
                var tags = v.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
                    : [];

                double Stat(string key) =>
                    v.TryGetProperty("stats", out var st) && st.TryGetProperty(key, out var sv)
                    && sv.ValueKind == JsonValueKind.Number ? sv.GetDouble() : 0;

                bool Says(params string[] words) =>
                    words.Any(w => desc.Contains(w, StringComparison.OrdinalIgnoreCase));

                // Срез лечения Riot пишет по-разному: у одних «Grievous Wounds»,
                // у других просто «40% Wounds». Общее — слово Wounds с большой
                // буквы; регистр тут и отличает механику от описательного текста.
                var antiHeal = desc.Contains("Wounds", StringComparison.Ordinal);

                // Процентное пробивание: «35%  Armor Penetration». Пробелов между
                // числом и словами бывает сколько угодно, поэтому не подстрока, а
                // выражение. Летальность сюда не попадает намеренно: против брони
                // и запаса здоровья работает именно процент.
                var pctPen = Regex.IsMatch(desc, @"\d+\s*%\s*(Armor|Magic)\s+Penetration",
                                           RegexOptions.IgnoreCase);

                // Ботинки дают меньше защиты, чем полноразмерный предмет, но
                // именно их и берут против урона нужного типа.
                var isBoots = tags.Contains("Boots");

                var gold = v.TryGetProperty("gold", out var g)
                           && g.TryGetProperty("total", out var tot) ? tot.GetInt32() : 0;

                map[id] = new Fact(
                    Id: id,
                    Name: name,
                    Armor: Stat("FlatArmorMod"),
                    MagicResist: Stat("FlatSpellBlockMod"),
                    Health: Stat("FlatHPPoolMod"),
                    // «Grievous Wounds» — единственная формулировка, которой Riot
                    // описывает срез лечения; меток для неё нет.
                    AntiHeal: antiHeal,
                    Tenacity: tags.Contains("Tenacity"),
                    ArmorPen: tags.Contains("ArmorPenetration") || Says("Lethality", "Armor Penetration"),
                    MagicPen: tags.Contains("MagicPenetration") || Says("Magic Penetration"),
                    PercentPen: pctPen,
                    AntiShield: Says("Shield Reaver", "shields", "shielding") && Says("reduc", "-50%"),
                    // Часовые Жони и Ангел-хранитель отвечают на одно и то же:
                    // тебя догнали и пытаются взорвать.
                    Stasis: Says("Stasis", "revive", "resurrect"),
                    // Щит от умения — ответ на подлов и дальний размен: ловят
                    // одним заклинанием, и оно не проходит.
                    SpellShield: Says("Spell Shield", "Spellshield"),
                    // Формулировка у Riot единая: «Remove all crowd control».
                    // Под неё попадают и Микаэль, и Ртутные, и Скимитар.
                    Cleanse: Regex.IsMatch(desc, @"remove[sd]?\s+all\s+crowd\s+control",
                                           RegexOptions.IgnoreCase),
                    // Предметы энчантера отличаются не статами, а тем, ЧТО они
                    // дают союзнику после хила или щита: Ardent — скорость атаки,
                    // Staff of Flowing Water — силу умений. Смотрим текст после
                    // общей для них фразы.
                    BuffsAllyAttack: AllyBuff(desc, "Attack Speed"),
                    BuffsAllyPower: !AllyBuff(desc, "Attack Speed") && AllyBuff(desc, "Ability Power"),
                    Lifesteal: tags.Contains("LifeSteal") || tags.Contains("SpellVamp")
                               || Says("Omnivamp", "Life Steal"),
                    TargetHealth: TargetHealthDamage(desc),
                    // Сбитая броня работает как пробивание, только достаётся всей
                    // команде: «reduces the target's Armor by 6%».
                    ShredsArmor: Regex.IsMatch(desc, @"reduc\w*[^.]{0,40}?\bArmor\b",
                                               RegexOptions.IgnoreCase),
                    ShredsMr: Regex.IsMatch(desc, @"reduc\w*[^.]{0,40}?Magic Resist",
                                            RegexOptions.IgnoreCase),
                    // Название механики у Riot одно на все такие предметы.
                    Lifeline: desc.Contains("Lifeline", StringComparison.OrdinalIgnoreCase),
                    // Именно «на тебе»: предметы энчантера усиливают то, что ты
                    // даёшь другим, а Лик духа — то, чем лечишься сам.
                    SelfHealAmp: Regex.IsMatch(desc, @"Heals? and Shields? on you are increased",
                                               RegexOptions.IgnoreCase),
                    // Набойки режут урон любых атак, Омен — только критов;
                    // против команды на автоатаках работает и то и другое.
                    AutoDefense: Says("damage from Critical Strikes",
                                      "incoming damage from Attacks"),
                    SlowsAttack: Regex.IsMatch(desc, @"reduc\w*[^.]{0,30}?Attack Speed",
                                               RegexOptions.IgnoreCase),
                    Magical: Stat("FlatMagicDamageMod") > 0,
                    Boots: isBoots,
                    // Отличает Jak'Sho (чистая броня и магзащита) от Locket и
                    // Knight's Vow: у последних есть аура, актив и ускорение
                    // умений — их и берут на поддержке.
                    PureTank: (Stat("FlatArmorMod") > 0 || Stat("FlatSpellBlockMod") > 0
                               || Stat("FlatHPPoolMod") > 0)
                              && !isBoots
                              && !tags.Intersect(new[]
                                 {
                                     "Aura", "Active", "AbilityHaste", "CooldownReduction",
                                     "SpellDamage", "Damage", "AttackSpeed", "CriticalStrike",
                                     "LifeSteal", "SpellVamp", "ManaRegen",
                                 }).Any(),
                    Gold: gold);
            }

            _facts = map;
            _loadedVersion = DataDragon.Version;
            Log.Write($"предметы: разобрано {map.Count} (броня/магзащита/раны/пробивание)");
        }
        catch (Exception ex)
        {
            Log.Write($"предметы: не разобрались — {ex.Message}");
        }
    }
}
