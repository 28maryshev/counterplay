namespace Counterplay;

// Теги ключевых чемпионов для семантической синергии.
// Идентификаторы по championId из LCU / Data Dragon.
public static class ChampionTags
{
    private static readonly Dictionary<int, string[]> T = new()
    {
        // ── Supports ──────────────────────────────────────────────────────────
        [412] = ["hook", "engage", "cc"],              // Thresh
        [111] = ["hook", "engage", "hard_cc"],         // Nautilus
        [53]  = ["hook", "engage", "cc"],              // Blitzcrank
        [555] = ["hook", "cc", "roam"],                // Pyke
        [89]  = ["engage", "hard_cc"],                 // Leona
        [526] = ["engage", "hard_cc"],                 // Rell
        [497] = ["engage", "cc", "xayah_pair"],        // Rakan
        [44]  = ["peel", "heal", "ult_invuln"],        // Taric
        [117] = ["peel", "shield", "cc"],              // Lulu
        [40]  = ["peel", "disengage", "shield"],       // Janna
        [147] = ["peel", "heal"],                      // Soraka
        [16]  = ["peel", "heal"],                      // Soraka (alias)
        [350] = ["peel", "heal"],                      // Yuumi
        [267] = ["peel", "cc", "nami_e"],              // Nami
        [43]  = ["peel", "poke", "shield"],            // Karma
        [235] = ["poke", "peel"],                      // Senna
        [161] = ["poke", "burst"],                     // Vel'Koz
        [143] = ["poke", "cc", "zone"],                // Zyra
        [63]  = ["poke", "burst"],                     // Brand
        [25]  = ["shield", "cc", "poke"],              // Morgana
        [201] = ["peel", "engage", "passive_auto"],    // Braum
        [902] = ["utility", "cc"],                     // Renata Glasc
        [888] = ["engage", "poke"],                    // Rengar (sup rare)

        // ── ADC ───────────────────────────────────────────────────────────────
        [498] = ["xayah_pair", "scale"],               // Xayah
        [51]  = ["trap", "range_poke"],                // Caitlyn
        [119] = ["aggressive", "kill_lane"],           // Draven
        [236] = ["aggressive", "lucian"],              // Lucian
        [360] = ["aggressive", "dash"],                // Samira
        [222] = ["scale", "hypercarry"],               // Jinx
        [96]  = ["scale", "hypercarry", "needs_peel"], // Kog'Maw
        [29]  = ["scale", "hypercarry"],               // Twitch
        [21]  = ["poke", "channels_ult"],              // Miss Fortune
        [81]  = ["poke", "mobility"],                  // Ezreal
        [202] = ["poke", "cc_passive"],                // Jhin
        [22]  = ["cc_ult", "poke"],                    // Ashe
        [15]  = ["scale", "hypercarry"],               // Sivir
        [18]  = ["scale", "hypercarry"],               // Tristana
        [42]  = ["poke", "mobility"],                  // Corki

        // ── Junglers ──────────────────────────────────────────────────────────
        [79]  = ["engage", "cc", "dive"],              // Gragas
        [59]  = ["engage", "hard_cc", "ult_trap"],     // Jarvan IV
        [254] = ["dive", "cc"],                        // Vi
        [5]   = ["dive", "cc"],                        // Xin Zhao
        [120] = ["engage", "dive"],                    // Hecarim
        [154] = ["engage", "cc"],                      // Zac
        [107] = ["dive", "burst"],                     // Rengar
        [141] = ["dive", "burst"],                     // Kayn
        [64]  = ["dive", "cc"],                        // Lee Sin
        [203] = ["cc", "utility"],                     // Kindred
        [56]  = ["engage", "cc"],                      // Nocturne (fear+dive)
        [113] = ["engage", "cc"],                      // Sejuani

        // ── Midlaners ─────────────────────────────────────────────────────────
        [61]  = ["ult_orianna", "poke"],               // Orianna
        [157] = ["ult_airborne", "dash"],              // Yasuo
        [777] = ["ult_airborne", "dash"],              // Yone
        [54]  = ["engage", "hard_cc", "ult_malphite"], // Malphite
        [134] = ["burst", "cc"],                       // Syndra
        [103] = ["poke", "cc"],                        // Ahri
        [84]  = ["dive", "burst"],                     // Akali
        [105] = ["dive", "burst"],                     // Fizz

        // ── Топы ──────────────────────────────────────────────────────────────
        [122] = ["dive", "juggernaut"],                // Darius
        [31]  = ["cc", "dive", "scale"],               // Cho'Gath
        [114] = ["dive", "cc"],                        // Fiora
        [86]  = ["juggernaut"],                        // Garen
        [420] = ["cc", "engage", "dive"],              // Illaoi
        [516] = ["engage", "cc"],                      // Ornn
        [57]  = ["engage", "cc"],                      // Maokai
    };

    public static IReadOnlyCollection<string> Get(int champId) =>
        T.TryGetValue(champId, out var tags) ? tags : Array.Empty<string>();

    public static bool Has(int champId, string tag) =>
        T.TryGetValue(champId, out var tags) && tags.Contains(tag);

    // Возвращает список семантических меток синергии между рекомендуемым саппортом
    // и союзниками/врагами (для вывода в причинах).
    public static List<string> DetectSynergies(
        int supId,
        IEnumerable<(int Id, string Role)> allies)
    {
        var sup = Get(supId);
        var labels = new List<string>();

        foreach (var (id, role) in allies)
        {
            var ally = Get(id);
            var name = DataDragon.Name(id);

            // Xayah–Rakan эксклюзивное дуо
            if ((supId == 497 && id == 498) || (supId == 498 && id == 497))
            {
                labels.Add($"Ксая–Рейкан: эксклюзивный бот-лайн дуэт");
                continue;
            }

            // Hook/engage support + Caitlyn = trap+hook combo
            if (id == 51 && (sup.Contains("hook") || sup.Contains("engage")))
            {
                labels.Add($"Ловушка + хук с Кейтлин — гарантированный килл");
                continue;
            }

            // Nami E (nami_e) + Lucian = бонусные авто-атаки
            if (supId == 267 && id == 236)
            {
                labels.Add($"Нами Е → авто Люциана: сильный дуэт");
                continue;
            }

            // Engage/hook sup + dive jungler = ганк потенциал
            if (role == "jungle" && ally.Contains("dive") &&
                (sup.Contains("engage") || sup.Contains("hook") || sup.Contains("hard_cc")))
            {
                labels.Add($"Ганк-синергия с {name}: СС + дайв гарантирует убийство");
                continue;
            }

            // CC support + MF (channels ult) = держим пока МФ стреляет
            if (ally.Contains("channels_ult") &&
                (sup.Contains("hard_cc") || sup.Contains("cc") || sup.Contains("engage")))
            {
                labels.Add($"Ульт-синергия с {name}: СС держит врагов под ультом");
                continue;
            }

            // CC support + Orianna = собирает мяч и разгоняет ультом
            if (ally.Contains("ult_orianna") &&
                (sup.Contains("engage") || sup.Contains("hard_cc")))
            {
                labels.Add($"Ульт-синергия с {name}: сбор под ульт-мяч");
                continue;
            }

            // Hard CC + Yasuo/Yone = ульт по взлетевшим
            if (ally.Contains("ult_airborne") &&
                (sup.Contains("hard_cc") || sup.Contains("engage")))
            {
                labels.Add($"Ульт-синергия с {name}: кукан активирует ульт");
                continue;
            }

            // Malphite ult + burst ally = массовый инициатор
            if (ally.Contains("ult_malphite") && sup.Contains("peel"))
            {
                labels.Add($"Синергия с {name}: прикрытие после ульт-инициации");
                continue;
            }

            // Peel support + hypercarry = защита
            if (ally.Contains("hypercarry") && sup.Contains("peel"))
            {
                labels.Add($"Защита гиперкэрри {name}");
                continue;
            }

            // Engage support + aggressive ADC = килл-лайн
            if (role == "bottom" && ally.Contains("aggressive") &&
                (sup.Contains("engage") || sup.Contains("hook")))
            {
                labels.Add($"Килл-лайн с {name}: агрессивный бот-лайн");
                continue;
            }

            // Taric ult invuln + dive ally
            if (sup.Contains("ult_invuln") && ally.Contains("dive"))
            {
                labels.Add($"Ульт-синергия с {name}: неуязвимость во время дайва");
                continue;
            }
        }

        return labels;
    }
}
