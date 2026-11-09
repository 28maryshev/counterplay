namespace Counterplay;

// Теги ключевых чемпионов для семантической синергии.
// Идентификаторы по championId из LCU / Data Dragon.
public static class ChampionTags
{
    // Один чемпион — одна запись: при повторе номера действует последняя, а
    // первая молча врёт читающему (до 09.11 так висели Ксин Жао, Аврора, Юми).
    private static readonly Dictionary<int, string[]> T = new()
    {
        // ── Supports ──────────────────────────────────────────────────────────
        [412] = ["hook", "engage", "hard_cc"],              // Thresh
        [111] = ["hook", "engage", "hard_cc"],         // Nautilus
        [53]  = ["hook", "engage", "hard_cc"],              // Blitzcrank
        // Пайк стоял в одном ряду с Трешем и Блицкранком — как хук-ТАНК. Но у
        // Riot он Support+Assassin, и полкита не было записано вовсе:
        // невидимость на W, рывок на E, добивающий ульт. Без них архетип
        // считался чистым подловом, хотя дайва в нём не меньше.
        [555] = ["hook", "hard_cc", "roam", "burst", "stealth", "mobility"], // Pyke
        [89]  = ["engage", "hard_cc"],                 // Leona
        [526] = ["engage", "hard_cc"],                 // Rell
        [497] = ["engage", "hard_cc", "xayah_pair"],        // Rakan
        [44]  = ["peel", "heal", "ult_invuln"],        // Taric
        [117] = ["peel", "shield", "hard_cc"],              // Lulu
        [40]  = ["peel", "disengage", "shield"],       // Janna
        // 147 — Серафина, не Сорака (подпись была неверной до 09.11): щит и
        // лечение W, корень и оглушение E, ульт очаровывает.
        [147] = ["peel", "heal", "shield", "hard_cc"],      // Seraphine
        [16]  = ["peel", "heal"],                      // Soraka
        [267] = ["peel", "hard_cc", "nami_e"],              // Nami
        [43]  = ["peel", "poke", "range_poke", "shield"], // Karma (поук-энчантер → пик/пок)
        [235] = ["poke", "peel"],                      // Senna
        [161] = ["poke", "burst"],                     // Vel'Koz
        [143] = ["poke", "hard_cc", "zone"],                // Zyra
        [63]  = ["poke", "burst"],                     // Brand
        [25]  = ["shield", "hard_cc", "poke"],              // Morgana
        [201] = ["peel", "engage", "passive_auto"],    // Braum
        [37]  = ["peel", "heal", "shield", "hard_cc"], // Sona (AoE-стан R, баффы/хил)
        // До 09.11 номера перепутаны: под 902 стояла «Рената», под 888 — «Ренгар»
        // с метками «заход, поук». 888 — Рената (заходом и поуком она не
        // занимается: щиты, спасение союзника, ульт обращает их заход против
        // своих), 902 — Милио (щит и ускорение, отбрасывание, ульт лечит и
        // снимает контроль). Ренгар — 107, записан у джанглеров.
        [888] = ["peel", "shield", "utility", "hard_cc"],   // Renata Glasc
        [902] = ["peel", "shield", "heal", "utility", "hard_cc"], // Milio
        // Алистара не было вовсе — архетип шёл от класса Data Dragon, а заход и
        // пил считались нулём. Заход Q+W, удар головой, ульт — живучесть.
        [12]  = ["engage", "hard_cc", "tank", "peel"], // Alistar

        // ── ADC ───────────────────────────────────────────────────────────────
        [498] = ["xayah_pair", "scale"],               // Xayah
        [51]  = ["trap", "range_poke"],                // Caitlyn
        [119] = ["aggressive", "kill_lane", "hypercarry"], // Draven (АДК в строю → фронт-ту-бэк)
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
        [79]  = ["engage", "hard_cc", "dive"],              // Gragas
        [59]  = ["engage", "hard_cc", "ult_trap"],     // Jarvan IV
        [254] = ["dive", "hard_cc"],                   // Vi (нокбэк Q, нокап/супресс R)
        [120] = ["engage", "dive", "hard_cc"],         // Hecarim (R — фир + отбрасывание)
        [154] = ["engage", "hard_cc"],                 // Zac (нокап E, отбрасывание R)
        [107] = ["dive", "burst"],                     // Rengar
        [141] = ["dive", "burst"],                     // Kayn
        [64]  = ["dive", "hard_cc"],                        // Lee Sin
        [203] = ["cc", "utility"],                     // Kindred
        [56]  = ["engage", "hard_cc"],                      // Nocturne (fear+dive)
        [113] = ["engage", "hard_cc"],                 // Sejuani (стан E/R)

        // ── Midlaners ─────────────────────────────────────────────────────────
        [61]  = ["ult_orianna", "poke"],               // Orianna
        // У Ясуо и Йоне теги заводились ради одной связки — «ульт по
        // подброшенным», — и кроме неё они не описывали ничего. Riot считает
        // обоих убийцами, и оба ныряют: Ясуо дэшем через миньонов, Йоне —
        // рывком души и ультом сквозь строй. Без этого оба числились подловом.
        [157] = ["ult_airborne", "dash", "dive", "mobility"],   // Yasuo
        [777] = ["ult_airborne", "dash", "dive", "mobility", "burst"], // Yone
        [54]  = ["engage", "hard_cc", "ult_malphite"], // Malphite
        [134] = ["poke", "zone_control", "burst", "hard_cc"], // Syndra (пик-маг, не дайв)
        // Ари — не поук-маг: она заходит чармом, снимает цель и уходит тремя
        // рывками ульта. Ни взрыва, ни подвижности в тегах не было.
        [103] = ["poke", "hard_cc", "burst", "mobility"],   // Ahri
        [84]  = ["dive", "burst"],                     // Akali
        [105] = ["dive", "burst"],                     // Fizz
        [85]  = ["engage", "hard_cc", "dive"],         // Kennen (AoE-стан R/W-проки)

        // ── Топы ──────────────────────────────────────────────────────────────
        [122] = ["dive", "juggernaut", "hard_cc"],     // Darius
        [31]  = ["hard_cc", "dive", "scale", "tank"],       // Cho'Gath
        [114] = ["dive", "hard_cc"],                        // Fiora
        [86]  = ["juggernaut", "tank"],                // Garen
        // Иллаой — не инициатор и не дайвер (было «cc, engage, dive»): драку не
        // начинает, в тыл нырнуть нечем, жёсткого контроля нет. Её игра —
        // стоять и драться в своей зоне щупалец. Решение владельца 09.11.
        [420] = ["juggernaut", "zone_control"],        // Illaoi
        [516] = ["engage", "cc", "hard_cc", "tank"],   // Ornn
        [150] = ["engage", "hard_cc", "poke", "scale"],// Gnar
        [126] = ["poke", "hard_cc", "burst"],               // Jayce
        [10]  = ["scale", "hypercarry", "ult_invuln"], // Kayle
        [240] = ["aggressive", "dive", "hard_cc"],     // Kled
        [897] = ["tank", "engage", "hard_cc"],         // K'Sante
        [75]  = ["juggernaut", "scale", "cc"],         // Nasus
        [92]  = ["dive", "burst", "mobility"],         // Riven
        [14]  = ["engage", "hard_cc", "tank"],         // Sion
        [98]  = ["engage", "hard_cc", "peel", "tank"], // Shen
        [27]  = ["hard_cc", "tank"],                        // Singed
        [23]  = ["dive", "scale", "kill_lane"],        // Tryndamere (сплит-дайв, не гиперкэрри)
        [48]  = ["juggernaut", "sustain", "hard_cc"],       // Trundle (джаггернаут фронта, не дайв)
        [106] = ["dive", "engage", "hard_cc", "tank"], // Volibear
        [62]  = ["engage", "hard_cc", "dive", "burst"],// Wukong
        [83]  = ["zone_control", "scale"],             // Yorick
        [6]   = ["burst", "hard_cc"],                  // Urgot
        [887] = ["dive", "sustain", "scale"],          // Gwen
        // ── Дополнительные джанглеры ─────────────────────────────────────────
        [121] = ["dive", "burst", "stealth", "mobility"],      // Kha'Zix
        [5]   = ["dive", "cc", "hard_cc", "engage"],           // Xin Zhao
        [9]   = ["channels_ult", "cc", "hard_cc"],             // Fiddlesticks
        [28]  = ["dive", "burst", "stealth", "mobility", "hard_cc"], // Evelynn (чарм W)
        [30]  = ["channels_ult", "poke", "burst", "scale"],    // Karthus
        // Джанглеры с реальным контролем, которых не было в списке (иначе движок
        // считал их «агрессивными без CC» и слал ложную причину jgSetup).
        [427] = ["hard_cc", "peel", "shield"],                 // Ivern (корень Q, нокап Дейзи, щиты)
        [32]  = ["engage", "hard_cc", "tank"],                 // Amumu (стан бинтом + AoE-стан R)
        [19]  = ["dive", "hard_cc", "sustain"],                // Warwick (супрессия R)
        [20]  = ["engage", "hard_cc"],                         // Nunu & Willump (корень E, снежок R)
        [60]  = ["dive", "hard_cc"],                           // Elise (стан-кокон)
        [421] = ["dive", "hard_cc", "sustain"],                // Rek'Sai (нокап при вылезании)
        [234] = ["dive", "hard_cc", "sustain"],                // Viego (стан W)
        [33]  = ["engage", "hard_cc", "tank"],                 // Rammus
        [35]  = ["burst", "stealth", "hard_cc"],                    // Shaco
        [72]  = ["engage", "hard_cc", "dive", "tank"],         // Skarner
        [77]  = ["engage", "hard_cc", "tank", "dive"],         // Udyr
        [876] = ["cc", "hard_cc", "mobility", "poke"],         // Lillia
        [429] = ["scale", "aggressive", "hypercarry"],         // Kalista
        [200] = ["dive", "burst", "mobility", "hypercarry"],   // Bel'Veth
        // ── Дополнительные мидеры ────────────────────────────────────────────
        [1]   = ["burst", "hard_cc", "engage"],                // Annie
        [3]   = ["engage", "hard_cc", "tank", "peel"],         // Galio
        [13]  = ["poke", "burst", "scale", "hard_cc"],              // Ryze
        [34]  = ["cc", "hard_cc", "zone_control"],             // Anivia
        [38]  = ["dive", "burst", "mobility", "cc"],           // Kassadin
        [45]  = ["burst", "cc", "hard_cc", "scale"],           // Veigar
        [50]  = ["tank", "sustain", "hard_cc", "zone_control"],     // Swain (дрейн-танк тимфайта)
        [68]  = ["zone_control", "burst", "cc"],               // Rumble
        [74]  = ["poke", "zone_control", "hard_cc"],                // Heimerdinger
        [91]  = ["dive", "burst", "stealth", "mobility"],      // Talon
        [99]  = ["burst", "cc", "hard_cc", "poke", "shield"], // Lux
        [101] = ["poke", "burst", "cc", "hard_cc"],            // Xerath
        [115] = ["poke", "burst", "zone_control"],             // Ziggs
        [127] = ["burst", "cc", "hard_cc", "engage"],          // Lissandra
        [131] = ["dive", "burst", "cc", "hard_cc", "engage"],  // Diana
        [136] = ["poke", "zone_control", "channels_ult", "hard_cc", "scale"], // Aurelion Sol (зон-маг)
        [142] = ["burst", "poke", "cc", "hard_cc"],            // Zoe
        [163] = ["poke", "zone_control", "cc", "hard_cc"],     // Taliyah
        [166] = ["poke", "mobility", "cc"],                    // Akshan
        [246] = ["burst", "dive", "mobility", "cc", "hard_cc"],// Qiyana
        [268] = ["poke", "zone_control", "hard_cc", "scale"],  // Azir
        [517] = ["dive", "burst", "mobility", "hard_cc"],           // Sylas
        [518] = ["poke", "zone_control", "burst", "cc", "hard_cc", "engage"], // Neeko (пик-маг)
        // ── Дополнительные ADC ───────────────────────────────────────────────
        [67]  = ["scale", "hypercarry", "needs_peel", "dive"], // Vayne
        [110] = ["poke", "cc", "hard_cc", "scale"],            // Varus
        [145] = ["scale", "hypercarry", "mobility", "dive"],   // Kai'Sa
        [221] = ["scale", "hypercarry", "mobility"],           // Zeri
        [523] = ["scale", "hypercarry", "needs_peel", "poke"], // Aphelios
        [895] = ["aggressive", "dive", "scale"],               // Nilah
        // ── Дополнительные саппорты ──────────────────────────────────────────
        [26]  = ["utility", "cc", "hard_cc", "peel", "ult_invuln"], // Zilean
        [57]  = ["engage", "hard_cc", "tank", "peel"],         // Maokai
        [78]  = ["engage", "hard_cc", "peel", "tank"],         // Poppy
        [80]  = ["engage", "hard_cc", "dive", "burst"],        // Pantheon
        [223] = ["peel", "tank", "hard_cc", "sustain"],        // Tahm Kench
        [350] = ["peel", "heal", "shield", "hard_cc"],              // Yuumi

        // Ревизия архетипов: раньше сидели на класс-фолбэке Data Dragon и
        // попадали не в свой стиль (Гангпланк «Fighter» → фронт-ту-бэк и т.п.).
        [41]  = ["poke", "zone_control", "scale"],             // Gangplank — поук бочками/Q
        [104] = ["burst", "aggressive", "mobility"],           // Graves — дайв-стрелок
        [2]   = ["dive", "aggressive", "sustain"],             // Olaf — бежит в бэклайн
        [102] = ["dive", "scale"],                             // Shyvana — ульт в тыл
        [24]  = ["dive", "scale"],                             // Jax — дайв-дуэлянт
        [58]  = ["dive", "aggressive", "burst"],               // Renekton — агро-дайв
        [76]  = ["poke", "range_poke", "mobility"],            // Nidalee — копейный поук
        [893] = ["poke", "zone_control", "mobility", "burst"], // Aurora — кайт-поук

        // ── Ревизия 09.11: чемпионы, у которых меток не было вовсе ─────────
        // Всё шло по классу Data Dragon. Метки по наборам умений; Локк, Юнара и
        // Заахен оставлены на классе — их наборы не сверены.
        [799] = ["dive", "mobility", "hard_cc"],        // Ambessa
        [266] = ["juggernaut", "sustain", "hard_cc"],   // Aatrox
        [432] = ["peel", "hard_cc", "roam", "utility"], // Bard
        [233] = ["dive", "aggressive", "hard_cc", "sustain"],// Briar
        [711] = ["burst", "hard_cc", "zone_control"],   // Vex
        [112] = ["zone_control", "poke", "hard_cc", "scale"],// Viktor
        [8] = ["sustain", "scale", "burst"],            // Vladimir
        [36] = ["tank", "juggernaut", "sustain"],       // Dr. Mundo
        [238] = ["burst", "mobility", "dive"],          // Zed
        [39] = ["dive", "mobility", "hard_cc"],         // Irelia
        [164] = ["dive", "mobility", "hard_cc"],        // Camille
        [69] = ["zone_control", "hard_cc", "scale"],    // Cassiopeia
        [55] = ["burst", "mobility", "dive"],           // Katarina
        [133] = ["aggressive", "mobility", "roam"],     // Quinn
        [7] = ["burst", "mobility", "hard_cc"],         // LeBlanc
        [90] = ["zone_control", "hard_cc", "scale"],    // Malzahar
        [11] = ["dive", "scale"],                       // Master Yi
        [82] = ["juggernaut", "hard_cc", "sustain"],         // Mordekaiser
        [800] = ["poke", "burst", "hard_cc"],           // Mel
        [950] = ["burst", "dive", "mobility"],          // Naafiri
        [875] = ["juggernaut", "engage", "hard_cc"],    // Sett
        [901] = ["scale", "hypercarry", "poke"],        // Smolder
        [4] = ["roam", "hard_cc", "poke"],              // Twisted Fate
        [17] = ["poke", "zone_control"],                // Teemo
        [910] = ["poke", "zone_control", "hard_cc", "scale"],// Hwei
        [245] = ["burst", "mobility", "dive", "hard_cc"],// Ekko
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
