using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// 1и. Редких в бан не советует (владелец 14.11).
///
/// Было видно: саппорт, союзники навели топа и ещё кого-то, и пятёрка
/// предлагала Маокая — 0,3% банов, пик 0,9% на саппорте (средний пик роли
/// 2,5%). Сцена «Ёнэ на топе, Мисс Фортуна на боте, пул владельца»: Маокай
/// четвёртым с подписью «Жёсткая контра: Сона −4,5% WR · пик 0,9%», а Зайра
/// (1,4%) — в пятёрке у 654 драфтов из 1125.
///
/// Причины две.
/// 1. Встречаемость шла корнем из пикрейта: кого берут в восемь раз реже,
///    весил меньше лишь втрое. А бан спасает ровно столько, как часто врага
///    встречаешь, × сколько он отнимает — встречаемость линейна.
/// 2. «Жёсткая контра» мерилась только силой и получала × 1,6 и гарантированное
///    место в пятёрке, даже если её почти не берут.
///
/// Правило: бан весит по ожидаемому вреду — шанс встретить (пикрейт с
/// поправкой на баны: забаненного не берут) × сила. Жёсткой контрой считается
/// только та, кого берут не реже среднего пика роли.
/// </summary>
internal static partial class Program
{
    /// Пул владельца на 14.11: журнал саппорта за месяц и активный пул «Super Main».
    private static readonly Dictionary<int, int> OwnerSupport = new()
        { [201] = 25, [37] = 23, [910] = 17, [161] = 10, [16] = 9, [98] = 7, [12] = 4, [117] = 3, [22] = 3, [101] = 2 };

    private static readonly int[] OwnerSupportPool = [161, 117, 25, 37, 201, 98, 101, 910, 22, 16, 50, 164];

    private static void Rare(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("редких в бан не советует:");
        SessionTracker.HistoryOverride = Hist(OwnerSupport);
        var poolDef = new ChampPool { Name = "super", ByRole = new() { ["support"] = [.. OwnerSupportPool] } };
        PoolStore.Current().Pools.Add(poolDef);
        PoolStore.SetActive(PoolKind.Pool, poolDef.Id);

        var picks = Lanes.ToDictionary(r => r, r => engine.PickRates(r));
        var refs = Lanes.ToDictionary(r => r, r => picks[r].Values.Where(p => p >= 0.5).DefaultIfEmpty(0).Average());
        var banRates = engine.BanRates();
        // Шанс встретить кандидата на его лучшей линии относительно среднего пика
        // этой линии: 1 — как типичный пик роли.
        double Meet(int id) => Lanes.Max(r =>
            refs[r] > 0 ? picks[r].GetValueOrDefault(id) / (1 - banRates.GetValueOrDefault(id) / 100) / refs[r] : 0);

        var tops = Top(picks["top"], 20);
        var others = new (string Pos, List<int> Ids)[]
            { ("jungle", Top(picks["jungle"], 10)), ("middle", Top(picks["mid"], 10)), ("bottom", Top(picks["adc"], 10)) };
        int drafts = 0, both = 0, tierFirst = 0;
        // Доводы: защита пиков союзников и место в тир-листе (см. Shown).
        var allyHead = Loc.T("reason.countersAlly", "\u0001").Split('\u0001')[0];
        var manyHead = Loc.T("reason.countersMany", "\u0001").Split('\u0001')[0];
        var tierHead = Loc.T("reason.tierGrade", "\u0001").Split('\u0001')[0];
        var tierTail = Loc.T("reason.tierGrade", "\u0001").Split('\u0001')[1];
        var rare = new Dictionary<int, int>();
        var rareHard = new Dictionary<int, int>();
        foreach (var t in tops)
            foreach (var (pos, ids) in others)
                foreach (var o in ids)
                {
                    var team = new List<DraftPlayer>
                    {
                        new(0, 0, t, "top", false),
                        new(1, 0, pos == "jungle" ? o : 0, "jungle", false),
                        new(2, 0, pos == "middle" ? o : 0, "middle", false),
                        new(3, 0, pos == "bottom" ? o : 0, "bottom", false),
                        new(4, 0, 0, "utility", true),
                    };
                    var cell = pos switch { "jungle" => 1, "middle" => 2, _ => 3 };
                    var bans = engine.RecommendBans(Snapshot(team, team[4], "utility"),
                        new Dictionary<int, HashSet<int>> { [0] = [t], [cell] = [o] }, top: 5);
                    drafts++;
                    foreach (var b in bans)
                    {
                        var guard = Array.FindIndex(b.Reasons, r => r.StartsWith(allyHead, StringComparison.Ordinal)
                                                                 || r.StartsWith(manyHead, StringComparison.Ordinal));
                        var tierAt = Array.FindIndex(b.Reasons, r => r.StartsWith(tierHead, StringComparison.Ordinal)
                                                                  && r.EndsWith(tierTail, StringComparison.Ordinal));
                        if (guard >= 0 && tierAt >= 0) { both++; if (guard > tierAt) tierFirst++; }
                        var m = Meet(b.ChampionId);
                        if (m < 0.5) rare[b.ChampionId] = rare.GetValueOrDefault(b.ChampionId) + 1;
                        if (m < 1 && b.Reasons.Any(r => r.StartsWith(HardHead, StringComparison.Ordinal)))
                            rareHard[b.ChampionId] = rareHard.GetValueOrDefault(b.ChampionId) + 1;
                    }
                }

        string List(Dictionary<int, int> d) => d.Count == 0 ? "нет" : string.Join(", ",
            d.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{DataDragon.Name(kv.Key)} ×{kv.Value} (встречаемость {Meet(kv.Key):F2})"));
        Console.WriteLine($"  драфтов {drafts}; Маокай: встречаемость {Meet(57):F2} от среднего пика");
        Check("в пятёрке нет тех, кого берут вдвое реже среднего пика своей роли", rare.Count == 0, List(rare));
        Check("«жёсткая контра» — только у тех, кого берут не реже среднего пика роли", rareHard.Count == 0, List(rareHard));
        Check("перебор: защита пиков — раньше места в тир-листе (карточки с обоими есть)",
              both > 0 && tierFirst == 0, $"{both} карточек с обоими, тир раньше — {tierFirst}");

        PoolStore.SetActive(PoolKind.Pool, null);
        PoolStore.Current().Pools.Remove(poolDef);
        SessionTracker.HistoryOverride = null;

        // Само правило — без данных: встречаемость пропорциональна пикрейту и
        // поправлена на баны. Возврат корня сцена выше не ловит: без надбавки за
        // «жёсткую» Маокай выпадает из пятёрки и с корнем, а Зайра остаётся
        // в ней у каждого третьего драфта. Метод приватный — через отражение;
        // переименуешь — проверка скажет первой.
        var meet = typeof(RecommendationEngine).GetMethod("Meet",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        double M(double pick, double refPct, double ban) => (double)meet!.Invoke(null, [pick, refPct, ban])!;
        Check("встречаемость найдена (RecommendationEngine.Meet)", meet is not null, "");
        if (meet is not null)
        {
            Check("встречаемость пропорциональна пикрейту: вчетверо реже — вчетверо меньше",
                  Math.Abs(M(0.6, 2.4, 0) / M(2.4, 2.4, 0) - 0.25) < 1e-9,
                  $"0,6% → {M(0.6, 2.4, 0):F2}, 2,4% → {M(2.4, 2.4, 0):F2}");
            Check("и поправлена на баны: банят в половине игр — берут вдвое чаще, чем видно",
                  Math.Abs(M(2.4, 2.4, 50) / M(2.4, 2.4, 0) - 2) < 1e-9,
                  $"без банов {M(2.4, 2.4, 0):F2}, бан 50% → {M(2.4, 2.4, 50):F2}");
        }

        static List<int> Top(IReadOnlyDictionary<int, double> p, int n) =>
            [.. p.OrderByDescending(kv => kv.Value).Take(n).Select(kv => kv.Key)];
    }
}
