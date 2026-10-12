using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Counterplay;

// Песочница — подставные данные: пулы, мастерство, комфорт напарника, история игр.
static partial class TestMode
{
    // Почти все мидеры и саппорты — по доле игр на роли в базе (emerald, 16.18–
    // 16.19): мид от четверти игр, саппорт от половины. Мидеры уходят другу,
    // саппорты — мне: так пул «Друг» покрывает связку «я саппорт, он мид».
    internal static readonly int[] MidAll =
    [
        103, 711, 61, 38, 268, 127, 4, 55, 90, 7, 166, 105, 805, 1, 134, 893, 13, 3, 112, 142,
        8, 238, 84, 910, 69, 136, 34, 45, 157, 163, 800, 131, 101, 777, 245, 517, 39, 99, 246, 115,
    ];

    internal static readonly int[] SupportAll =
    [
        267, 902, 350, 201, 89, 412, 432, 117, 53, 888, 37, 526, 40, 555, 497, 16, 12, 111, 44, 43,
        235, 147, 26, 25, 518, 161, 143, 57, 63, 50, 99, 78, 223, 80,
    ];

    /// Тестовые пулы песочницы — если у её аккаунта пулов нет совсем.
    internal static void SeedTestPools()
    {
        var pools = PoolStore.Current();
        if (pools.Pools.Count > 0 || pools.DuoPools.Count > 0) { WidenTestDuo(pools); return; }
        // Мид и саппорт — широкие, как их собрал владелец в песочнице: на них
        // и держится подменная история (см. MakeGamePreview).
        var mine = new Dictionary<string, List<int>>
        {
            ["top"] = [86, 122, 54], ["jungle"] = [64, 19, 32],
            ["mid"] = [103, 238, 99, 34, 101, 131, 45, 268, 166, 142, 3, 893, 8, 38, 805, 55, 69, 127, 711],
            ["adc"] = [222, 22, 51],
            ["support"] = [.. SupportAll],
        };
        var friend = new Dictionary<string, List<int>>
        {
            ["top"] = [24, 92], ["jungle"] = [11, 60], ["mid"] = [.. MidAll],
            ["adc"] = [67, 236], ["support"] = [117, 40],
        };
        pools.Pools.Add(new ChampPool { Name = "Тест", ByRole = mine });
        // Дуо-пул сразу настроен на постоянного напарника подменной истории:
        // иначе раздел «винрейт связок» ждал бы, пока его опознают в драфте.
        pools.DuoPools.Add(new DuoPool
        {
            FriendName = "Друг", FriendPuuid = MateOften, FriendNick = MateOftenNick,
            Mine = mine, Friend = friend,
        });
        PoolStore.Persist();
        PoolStore.SetActive(PoolKind.Duo, pools.DuoPools[0].Id);
    }

    /// <summary>
    /// Разово дописать в уже собранный тестовый дуо-пул всех мидеров другу и всех
    /// саппортов мне — ПОВЕРХ того, что там собрано руками. Отметка в папке
    /// песочницы: второй раз не дописываем, иначе убранный чемпион возвращался бы
    /// при каждом запуске.
    /// </summary>
    private static void WidenTestDuo(AccountPools pools)
    {
        var mark = Path.Combine(Sandbox.Dir, "duo-widen-1");
        if (File.Exists(mark)) return;
        var d = pools.DuoPools.FirstOrDefault(x => x.FriendPuuid == MateOften);
        if (d is null) return;
        d.Friend["mid"]   = [.. d.FriendForRole("mid").Union(MidAll)];
        d.Mine["support"] = [.. d.MineForRole("support").Union(SupportAll)];
        PoolStore.Persist();
        try { File.WriteAllText(mark, ""); } catch { /* допишем в следующий раз — Union повторов не даст */ }
    }

    /// <summary>
    /// Тестовая история в дело (true) или прочь — профиль «мой аккаунт» (false).
    ///
    /// Подбор должен считать в песочнице так же, как в бою, поэтому из истории
    /// берётся всё, что в бою приходит из клиента и от напарника: журнал игр
    /// (личный винрейт, наигранность), очки мастерства и наигранность напарника
    /// по обмену.
    /// </summary>
    internal static void UseTestHistory(RecommendationEngine engine, bool on)
    {
        var games = on ? MakeGamePreview() : null;
        SessionTracker.PreviewGames = games;
        engine.Mastery = games is null ? new Dictionary<int, long>() : MakeMastery(games);
        DuoShare.Preview = games is null ? null : MakeMateComfort(games);
    }

    // Очков мастерства за игру. В бою они приходят из клиента; клиента в
    // песочнице нет, и без них наигранность держалась бы на одних играх за
    // месяц — не так, как в бою. Тысяча — порядок того, что Riot даёт за игру.
    private const long PointsPerGame = 1000;

    internal static Dictionary<int, long> MakeMastery(IEnumerable<SessionTracker.PreviewGame> games) =>
        games.Where(g => g.MyChampionId != 0)
             .GroupBy(g => g.MyChampionId)
             .ToDictionary(x => x.Key, x => x.Count() * PointsPerGame);

    /// <summary>
    /// Наигранность напарников — тем же расчётом, что DuoShare.Snapshot у
    /// настоящего напарника, только из его половины наших общих игр: его
    /// чемпион становится «моим», и дальше всё как у меня.
    /// </summary>
    internal static Dictionary<string, IReadOnlyDictionary<int, MateComfort>> MakeMateComfort(
        IReadOnlyList<SessionTracker.PreviewGame> games)
    {
        string[] queues = [.. SessionTracker.QueuesRanked, .. SessionTracker.QueuesNormal];
        var res = new Dictionary<string, IReadOnlyDictionary<int, MateComfort>>();
        foreach (var mate in games.GroupBy(g => g.AllyPuuid))
        {
            var his = mate.Select(g => g with { MyChampionId = g.AllyChampionId, AllyChampionId = g.MyChampionId })
                          .ToList();
            // Постоянному напарнику — ещё его игры без меня и пожизненные очки.
            var often = mate.Key == MateOften;
            if (often) his.AddRange(MakeMateSolo(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            var hist = SessionTracker.PreviewHistory(his, RecommendationEngine.FreshDays, queues);
            var pts  = MakeMastery(his);
            if (often)
                foreach (var (id, life) in MateLifetimeMastery)
                    pts[id] = Math.Max(pts.GetValueOrDefault(id), life);
            var map  = new Dictionary<int, MateComfort>();
            foreach (var id in his.Select(g => g.MyChampionId).Concat(pts.Keys).Where(x => x != 0).Distinct())
            {
                var (g, w) = hist.Recent(id);
                map[id] = new MateComfort(g, w, pts.GetValueOrDefault(id),
                                          hist.DaysSinceNth(id, RecommendationEngine.RegularGames));
            }
            res[mate.Key] = map;
        }
        return res;
    }

    // Любимые чемпионы тестового дуэта: на них игр больше всего.
    internal static readonly int[] MateMidMains   = [38, 134, 103, 61, 112, 7, 4, 45];      // Кассадин, Синдра, Ари, Орианна, Виктор, Ле Блан, Твистед Фэйт, Вейгар

    internal static readonly int[] MySupportMains = [235, 267, 412, 89, 117, 902, 161, 147]; // Сенна, Нами, Треш, Леона, Лулу, Милио, Вел'Коз, Серафина

    private const int MateRetiredMain = 84;   // Акали: много очков, полгода не брал

    /// <summary>
    /// Игры Harribon БЕЗ меня — его соло-очередь на миде. Наигранность человека
    /// не сводится к играм со мной: в бою она приходит от него целиком. Тут и
    /// мейны, и чемпионы «разок», и бывший мейн, которого он полгода не брал, —
    /// чтобы было видно, как подбор гасит устаревший опыт.
    /// </summary>
    private static List<SessionTracker.PreviewGame> MakeMateSolo(long now)
    {
        var res = new List<SessionTracker.PreviewGame>();
        var rng = new Random(20261008);
        void Solo(int champ, int games, double wr, double fromDays, double toDays)
        {
            for (var i = 0; i < games; i++)
            {
                var days = games == 1 ? fromDays : fromDays + (toDays - fromDays) * i / (games - 1);
                res.Add(new("", "", "solo", champ, 0, "mid", "",
                            now - (long)(days * 86400) - 2700L * i, rng.NextDouble() < wr));
            }
        }
        Solo( 38, 30, 0.57,   0.5,  29);   // Кассадин — главный мейн
        Solo(134, 22, 0.55,   1,    28);   // Синдра
        Solo(103, 18, 0.50,   1,    29);   // Ари
        Solo( 61, 14, 0.52,   2,    27);   // Орианна
        Solo(112, 10, 0.60,   3,    26);   // Виктор
        Solo( 45, 12, 0.58,   1,    25);   // Вейгар
        Solo(  4,  6, 0.50,   4,    22);   // Твистед Фэйт
        Solo(  7,  8, 0.38,   2,    20);   // Ле Блан — играет, но не идёт
        Solo(157,  3, 0.33,   5,    15);   // Ясуо — разок
        Solo(MateRetiredMain, 25, 0.55, 100, 160);   // Акали — бывший мейн, полгода не брал
        return res;
    }

    // Его пожизненные очки мастерства: в бою приходят от него вместе с играми.
    // Берём большее из этого и «1000 за игру» по тестовой истории.
    private static readonly Dictionary<int, long> MateLifetimeMastery = new()
    {
        [38] = 420_000, [134] = 260_000, [103] = 180_000, [61] = 150_000, [112] = 90_000,
        [45] = 60_000, [4] = 70_000, [7] = 120_000, [157] = 40_000, [84] = 300_000,
    };

    // Напарники подменной истории. Постоянный — тот, на кого настроен тестовый
    // дуо-пул «Друг» (см. SeedTestPools).
    internal const string MateOften = "TEST-mate-often-0000000000000000000000000000000000000000000000000";

    internal const string MateRare  = "TEST-mate-rare-00000000000000000000000000000000000000000000000000";

    internal const string MateOnce  = "TEST-mate-once-00000000000000000000000000000000000000000000000000";

    internal const string MateOftenNick = "Harribon";

    /// <summary>
    /// Подменная история совместных игр для песочницы — по игре на запись, с
    /// датой, ролями и исходом, как копит настоящий журнал.
    ///
    /// Раньше были готовые итоги по парам без дат, и почти все мимо тестового
    /// пула: в разделе «винрейт связок» оставалась одна плитка, а переключатель
    /// «за 30 дней / за всё время» ничего не менял.
    ///
    /// Постоянный напарник (Harribon): пары из обеих половин тестового пула «Друг»
    /// — от уверенно выигрышных до провальных, — плюс игры старше месяца (их видно
    /// только «за всё время») и пары мимо пула (их окно связок не показывает).
    /// Редкий и разовый напарники — для списка людей и чтобы чужие связки не
    /// лезли в пул.
    /// </summary>
    internal static List<SessionTracker.PreviewGame> MakeGamePreview()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new List<SessionTracker.PreviewGame>();

        // Игры одной пары: results — по букве на игру (W/L) от свежей к старой,
        // разложены ровно между fromDays и toDays дней назад.
        void Pair(string puuid, string nick, string queue, int mine, string myRole,
                  int his, string hisRole, string results, double fromDays, double toDays)
        {
            for (var i = 0; i < results.Length; i++)
            {
                var days = results.Length == 1 ? fromDays
                    : fromDays + (toDays - fromDays) * i / (results.Length - 1);
                games.Add(new(puuid, nick, queue, mine, his, myRole, hisRole,
                              now - (long)(days * 86400) - 1800L * i, results[i] == 'W'));
            }
        }
        const string H = MateOftenNick;

        // ── Harribon, последний месяц: я саппорт, он стрелок ──
        Pair(MateOften, H, "solo",    89, "support", 236, "adc",     "WWLWLWWLW", 1, 28);  // Леона + Люциан
        Pair(MateOften, H, "solo",   412, "support",  67, "adc",     "WWWLWLW",   1, 26);  // Треш + Вейн
        Pair(MateOften, H, "flex",   412, "support", 236, "adc",     "LWLWW",     3, 24);  // Треш + Люциан
        Pair(MateOften, H, "flex",    16, "support", 236, "adc",     "LLW",       5, 18);  // Сорака + Люциан
        Pair(MateOften, H, "solo",   235, "support",  67, "adc",     "WLWW",      2, 21);  // Сенна + Вейн
        Pair(MateOften, H, "flex",   201, "support", 236, "adc",     "LWL",       6, 19);  // Браум + Люциан

        // ── Harribon, последний месяц: я саппорт, он мид ──
        Pair(MateOften, H, "solo",   412, "support",   4, "mid",     "WLWWW",     1, 25);  // Треш + Твистед Фэйт
        Pair(MateOften, H, "solo",    89, "support",  45, "mid",     "WWL",       4, 17);  // Леона + Вейгар
        Pair(MateOften, H, "flex",   267, "support",   4, "mid",     "WWLW",      2, 23);  // Нами + Твистед Фэйт
        Pair(MateOften, H, "flex",   235, "support",  45, "mid",     "LWW",       7, 20);  // Сенна + Вейгар
        Pair(MateOften, H, "solo",    43, "support",   4, "mid",     "WLW",       5, 27);  // Карма + Твистед Фэйт
        Pair(MateOften, H, "normal", 201, "support",  45, "mid",     "LL",        9, 11);  // Браум + Вейгар
        Pair(MateOften, H, "solo",   432, "support",   4, "mid",     "W",         3,  3);  // Бард + Твистед Фэйт
        Pair(MateOften, H, "flex",   161, "support",  45, "mid",     "WLLW",      8, 29);  // Вел'Коз + Вейгар

        // ── Harribon: я саппорт, он мид — широко, по всему пулу ──
        // Пар много, и вручную их не расписать: генерируем с постоянным зерном —
        // история одна и та же от запуска к запуску. Как у настоящего дуэта, есть
        // любимые: его мейны на миде и мои мейны в саппорте выпадают в половине
        // случаев, и на паре «мейн + мейн» игр больше — от трёх до десяти.
        // Остальные пары — одна-три игры, изредка до шести. Винрейт пары от 30
        // до 75%; у каждой четвёртой есть игры старше месяца.
        var rng = new Random(20261007);
        int PickOf(int[] mains, int[] all) =>
            rng.Next(2) == 0 ? mains[rng.Next(mains.Length)] : all[rng.Next(all.Length)];
        string Results(int count, double wr) =>
            new([.. Enumerable.Range(0, count).Select(_ => rng.NextDouble() < wr ? 'W' : 'L')]);
        var made = new HashSet<(int, int)>();
        for (var n = 0; n < 260; n++)
        {
            var sup = PickOf(MySupportMains, SupportAll);
            var mid = PickOf(MateMidMains, MidAll);
            // Бывший мейн друга заброшен и со мной тоже: иначе свежие общие игры
            // на нём прятали бы, как подбор гасит устаревший опыт.
            if (mid == MateRetiredMain) continue;
            if (sup == mid || !made.Add((sup, mid))) continue;
            var favourite = MySupportMains.Contains(sup) && MateMidMains.Contains(mid);
            var wr    = 0.30 + rng.NextDouble() * 0.45;
            var count = favourite ? 3 + rng.Next(8) : 1 + rng.Next(rng.Next(3) == 0 ? 6 : 3);
            var from  = 0.5 + rng.NextDouble() * 10;
            var to    = Math.Min(29.5, from + 5 + rng.NextDouble() * 18);
            var queue = rng.Next(10) switch { < 5 => "solo", < 8 => "flex", _ => "normal" };
            Pair(MateOften, H, queue, sup, "support", mid, "mid", Results(count, wr), from, to);
            if (rng.Next(4) == 0)
                Pair(MateOften, H, queue, sup, "support", mid, "mid",
                     Results(1 + rng.Next(favourite ? 5 : 3), wr), 32 + rng.NextDouble() * 5, 40 + rng.NextDouble() * 18);
        }

        // ── Harribon, последний месяц: я мид, он лес ──
        Pair(MateOften, H, "solo",   103, "mid",      60, "jungle",  "WLWWLWW",   2, 28);  // Ари + Элиза
        Pair(MateOften, H, "solo",   238, "mid",      11, "jungle",  "WWLLW",     3, 25);  // Зед + Мастер Йи
        Pair(MateOften, H, "normal",  99, "mid",      11, "jungle",  "LLWL",      9, 21);  // Люкс + Мастер Йи
        Pair(MateOften, H, "solo",    34, "mid",      60, "jungle",  "WWWLWW",    1, 27);  // Анивия + Элиза
        Pair(MateOften, H, "flex",   101, "mid",      60, "jungle",  "LWLW",      5, 20);  // Зерат + Элиза
        Pair(MateOften, H, "solo",   131, "mid",      11, "jungle",  "WLW",       6, 15);  // Диана + Мастер Йи
        Pair(MateOften, H, "solo",    45, "mid",      60, "jungle",  "WWLWWLW",   2, 29);  // Вейгар + Элиза
        Pair(MateOften, H, "flex",   268, "mid",      11, "jungle",  "LLLW",      4, 24);  // Азир + Мастер Йи
        Pair(MateOften, H, "normal", 166, "mid",      60, "jungle",  "WL",        8, 12);  // Акшан + Элиза
        Pair(MateOften, H, "solo",   142, "mid",      60, "jungle",  "WWWW",      3, 18);  // Зои + Элиза
        Pair(MateOften, H, "flex",     3, "mid",      11, "jungle",  "WLWLW",     2, 26);  // Галио + Мастер Йи
        Pair(MateOften, H, "solo",   893, "mid",      60, "jungle",  "LW",        7,  9);  // Аврора + Элиза
        Pair(MateOften, H, "solo",     8, "mid",      11, "jungle",  "LWLLW",     5, 28);  // Владимир + Мастер Йи
        Pair(MateOften, H, "flex",    38, "mid",      11, "jungle",  "WWLW",      6, 23);  // Кассадин + Мастер Йи
        Pair(MateOften, H, "solo",   805, "mid",      60, "jungle",  "W",         4,  4);  // Локк + Элиза
        Pair(MateOften, H, "solo",    55, "mid",      11, "jungle",  "LLWL",      9, 21);  // Катарина + Мастер Йи
        Pair(MateOften, H, "flex",    69, "mid",      60, "jungle",  "WLW",      11, 19);  // Кассиопея + Элиза
        Pair(MateOften, H, "solo",   127, "mid",      60, "jungle",  "WWLWW",     2, 24);  // Лиссандра + Элиза
        Pair(MateOften, H, "normal", 711, "mid",      11, "jungle",  "LW",       13, 17);  // Векс + Мастер Йи

        // ── Harribon, последний месяц: остальное из пула ──
        Pair(MateOften, H, "flex",   222, "adc",     117, "support", "WWLW",      6, 27);  // Джинкс + Лулу
        Pair(MateOften, H, "solo",    51, "adc",      40, "support", "WL",       10, 16);  // Кейтлин + Жанна
        Pair(MateOften, H, "solo",    86, "top",      60, "jungle",  "W",         8,  8);  // Гарен + Элиза

        // ── Harribon, старше месяца: видно только «за всё время» ──
        Pair(MateOften, H, "solo",    89, "support", 236, "adc",     "LLW",      35, 55);
        Pair(MateOften, H, "flex",   412, "support",  67, "adc",     "WW",       40, 48);
        Pair(MateOften, H, "flex",    89, "support",  67, "adc",     "WLW",      33, 45);  // Леона + Вейн
        Pair(MateOften, H, "solo",   412, "support",   4, "mid",     "WWL",      36, 58);
        Pair(MateOften, H, "solo",   103, "mid",      60, "jungle",  "L",        50, 50);
        Pair(MateOften, H, "solo",    34, "mid",      60, "jungle",  "WL",       35, 45);
        Pair(MateOften, H, "flex",     3, "mid",      11, "jungle",  "W",        38, 38);
        Pair(MateOften, H, "solo",    45, "mid",      60, "jungle",  "LW",       33, 52);
        Pair(MateOften, H, "solo",   238, "mid",      11, "jungle",  "L",        40, 40);

        // ── Harribon мимо пула: в окне связок их нет, в сайдбаре есть ──
        Pair(MateOften, H, "normal", 555, "support",  22, "adc",     "LLL",       7, 19);  // Пайк + Эш
        Pair(MateOften, H, "flex",   412, "support",  21, "adc",     "WL",       11, 14);  // Треш + Мисс Фортуна

        // ── Другие люди ──
        Pair(MateRare, "Ozzy",  "normal", 103, "mid", 64,  "jungle", "WWLWLW", 10, 40);  // Ари + Ли Син
        Pair(MateRare, "Ozzy",  "normal", 157, "mid", 254, "jungle", "WL",     20, 25);  // Ясуо + Вай
        Pair(MateOnce, "Sanya", "solo",    86, "top", 122, "jungle", "W",      15, 15);  // Гарен + Дариус

        return games;
    }
}
