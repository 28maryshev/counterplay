using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// «Не советовать» (16.10). Владелец: Блицкранк первым баном против Соны, а он
/// знает, как его законтрить; Тарик в пиках, а играть на нём не умеет. Кнопка
/// 👎 в углу карточки убирает чемпиона из советов на своей роли.
///
/// 1. Движок: дизлайкнутый не советуется HideGames игр (ни в банах, ни в пиках,
///    ни из пула), список при этом полный; потом возвращается по месту за
///    игру и через ReturnGames игр стоит на своём. Другая роль не задета, мой
///    собственный пик не прячется, «вернуть» и повторный дизлайк работают.
/// 2. Счёт игр — по журналу: только игры с драфтом и только после отметки.
/// 3. Окно: клик по кнопке убирает карточку сразу и не наводит чемпиона в
///    клиенте; над списком — полоса «Скрыты», клик по иконке возвращает.
///
/// База — живая, только чтение; всё остальное — в своей папке.
/// Снимок окна для глаз: переменная CP_SNAP — папка, куда положить PNG.
/// </summary>
internal static class Program
{
    private static int _fails;

    // Сколько игр «сыграно» после каждой отметки — проверка двигает его сама.
    private static int _played;

    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "Dislikes");
        try { Directory.Delete(root, recursive: true); } catch { /* не было */ }
        AppPaths.RootOverride = root;
        Directory.CreateDirectory(root);
        var playerDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay");
        DataDb.DirOverride = playerDir;   // база матчей — настоящая, только чтение
        PoolStore.DirOverride = Path.Combine(root, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        Loc.SetLanguage("ru");

        Journal(root);

        var db = RecommendationEngine.FindDb();
        if (db is null) { Console.WriteLine("базы нет — движок и окно проверять нечем"); return Done(); }
        using var engine = RecommendationEngine.Create(db, "emerald");
        engine.Mastery = new Dictionary<int, long>();
        // Пул — как у владельца: Сона, Браум, Вел'Коз.
        SessionTracker.HistoryOverride = Hist([37, 201, 161]);
        Dislikes.GamesSinceOverride = _ => _played;

        Assets(playerDir);   // имена в выводе и иконки в окне
        Bans(engine);
        Picks(engine);
        Pool(engine);

        var snap = Environment.GetEnvironmentVariable("CP_SNAP");
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Window(engine, snap);
        app.Shutdown();
        return Done();
    }

    private static int Done()
    {
        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: «не советовать» скрывает и возвращает как задумано"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Баны ─────────────────────────────────────────────────────────────

    private static void Bans(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("баны:");
        Reset();
        var d = Draft("utility", ban: true);
        var before = Ids(engine.RecommendBans(d));
        var x = before[0];
        Console.WriteLine($"  до: {Names(before)}");

        Dislikes.Add(Dislikes.Kind.Ban, x, "support");
        var hidden = Ids(engine.RecommendBans(d));
        Check("скрытого нет в пятёрке, пятёрка полная", !hidden.Contains(x) && hidden.Count == before.Count,
              Names(hidden));
        Check("остальные советы — те же, кто был", before.Skip(1).All(hidden.Contains), Names(hidden));
        _played = Dislikes.HideGames - 1;
        Check($"после {Dislikes.HideGames - 1} игр всё ещё скрыт",
              !Ids(engine.RecommendBans(d, top: 15)).Contains(x), "");

        // Возврат: каждую игру на место выше.
        for (var p = Dislikes.HideGames; p <= Dislikes.HideGames + Dislikes.ReturnGames; p++)
        {
            _played = p;
            var wide = Ids(engine.RecommendBans(d, top: 10));
            var want = Math.Max(0, Dislikes.HideGames + Dislikes.ReturnGames - p);
            Check($"после {p} игр — {want + 1}-й", wide.IndexOf(x) == want, $"{wide.IndexOf(x) + 1}-й: {Names(wide)}");
        }
        _played = Dislikes.HideGames;
        Check("на первой игре возврата в пятёрку не попадает (опущен на пять мест)",
              !Ids(engine.RecommendBans(d)).Contains(x), "");
        _played = Dislikes.HideGames + Dislikes.ReturnGames;
        Check("отыграл срок — снова на своём месте", Ids(engine.RecommendBans(d)).SequenceEqual(before), "");

        // Роль: дизлайк саппорта не трогает баны на миде.
        _played = 0;
        Reset();
        var mid = Draft("middle", ban: true);
        var midBefore = Ids(engine.RecommendBans(mid));
        Dislikes.Add(Dislikes.Kind.Ban, midBefore[0], "support");
        Check("дизлайк на другой роли баны не меняет", Ids(engine.RecommendBans(mid)).SequenceEqual(midBefore), "");

        // Вернуть сразу и повторный дизлайк.
        Reset();
        Dislikes.Add(Dislikes.Kind.Ban, x, "support");
        Dislikes.Remove(Dislikes.Kind.Ban, x, "support");
        Check("«вернуть» — снова первым", Ids(engine.RecommendBans(d)).SequenceEqual(before), "");
        Dislikes.Add(Dislikes.Kind.Ban, x, "support");
        _played = Dislikes.HideGames + 2;
        Check("возвращается…", Ids(engine.RecommendBans(d, top: 10)).IndexOf(x) > 0, "");
        _played = 0;   // повторный дизлайк — новая отметка, игр после неё ноль
        Dislikes.Add(Dislikes.Kind.Ban, x, "support");
        Check("…повторный дизлайк скрывает заново", !Ids(engine.RecommendBans(d, top: 15)).Contains(x), "");
        Check("запись одна", Entries().Count(e => e.Champ == x) == 1, $"{Entries().Count}");

        // Файл — в папке данных (её подменила проверка), не где-то ещё.
        Check("файл в папке данных", File.Exists(Path.Combine(AppPaths.Root, "dislikes.json")), "");
        Reset();
    }

    // ── 2. Пики ─────────────────────────────────────────────────────────────

    private static void Picks(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("пики:");
        Reset();
        var d = Draft("utility", ban: false, enemies: [222, 51]);
        var before = Ids(engine.Recommend(d, 6));
        var x = before[0];
        Console.WriteLine($"  до: {Names(before)}");

        Dislikes.Add(Dislikes.Kind.Pick, x, "support");
        var now = Ids(engine.Recommend(d, 6));
        Check("скрытого нет, список полный", !now.Contains(x) && now.Count == before.Count, Names(now));
        Check("бан-дизлайк пики не трогает и наоборот",
              Ids(engine.RecommendBans(Draft("utility", ban: true))).Count == 5, "");
        Check("номера мест — подряд с 1", engine.Recommend(d, 6).Select(r => r.Rank).SequenceEqual(Enumerable.Range(1, now.Count)), "");

        _played = Dislikes.HideGames + 2;
        var back = Ids(engine.Recommend(d, 10));
        Check("возвращается: на 3 места ниже своего", back.IndexOf(x) == 3, $"{back.IndexOf(x) + 1}-й");
        _played = Dislikes.HideGames + Dislikes.ReturnGames;
        Check("отыграл срок — на своём месте", Ids(engine.Recommend(d, 6)).SequenceEqual(before), "");

        // Свой пик (наведён мной) — виден всегда, даже скрытый.
        _played = 0;
        var mine = d with { MyTeam = [.. d.MyTeam.Select(p => p.IsLocalPlayer ? p with { PickIntentId = x } : p)] };
        mine = mine with { Me = mine.MyTeam.First(p => p.IsLocalPlayer) };
        Check("свой наведённый пик не прячется", Ids(engine.Recommend(mine, 6)).Contains(x), "");
        Check("а расчёт по имени (only) дизлайк не трогает",
              engine.Recommend(d, 1, [x]).FirstOrDefault()?.ChampionId == x, "");
        Reset();
    }

    // ── 3. Пул ──────────────────────────────────────────────────────────────

    private static void Pool(RecommendationEngine engine)
    {
        Console.WriteLine();
        Console.WriteLine("пул:");
        Reset();
        var d = Draft("utility", ban: false, enemies: [222, 51]);
        int[] pool = [37, 201, 161];
        var before = Ids(engine.TopFromPool(d, pool, 3));
        var x = before[0];
        Dislikes.Add(Dislikes.Kind.Pick, x, "support");
        var now = Ids(engine.TopFromPool(d, pool, 3));
        Check("дизлайкнутого нет и среди пиков из пула", !now.Contains(x), $"{Names(before)} → {Names(now)}");
        Reset();
    }

    // ── 4. Счёт игр по журналу ──────────────────────────────────────────────

    private static void Journal(string root)
    {
        Console.WriteLine("журнал:");
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10_000;
        string G(long ts) => $$"""{"Ts":{{ts}},"ChampionId":37,"Win":true}""";
        var json = $$"""
            { "LastAccount": "me", "Accounts": { "me": { "Queues": {
              "solo": { "Games": [ {{G(t0 - 100)}}, {{G(t0 + 10)}}, {{G(t0 + 20)}} ] },
              "normal": { "Games": [ {{G(t0 + 30)}} ] },
              "aram": { "Games": [ {{G(t0 + 40)}}, {{G(t0 + 50)}} ] } } } } }
            """;
        File.WriteAllText(Path.Combine(root, "session.json"), json);
        SessionTracker.DropCache();
        Check("после отметки — три игры с драфтом (ARAM не в счёт)", SessionTracker.GamesSince(t0) == 3,
              $"{SessionTracker.GamesSince(t0)}");
        Check("игра ровно в отметку не считается, раньше — тоже", SessionTracker.GamesSince(t0 + 10) == 2,
              $"{SessionTracker.GamesSince(t0 + 10)}");
        Check("после последней — ноль", SessionTracker.GamesSince(t0 + 1000) == 0, "");
        File.Delete(Path.Combine(root, "session.json"));
        SessionTracker.DropCache();
    }

    // ── 5. Окно ─────────────────────────────────────────────────────────────

    private static void Window(RecommendationEngine engine, string? snap)
    {
        Console.WriteLine();
        Console.WriteLine("окно:");
        Reset();
        var s = AppSettings.Current;
        s.DraftWidth = 1400; s.DraftHeight = 900; s.DraftPlacement = "remember";
        s.DraftLeft = -32000; s.DraftTop = -32000;

        var w = new OverlayWindow();
        ShowHidden(w);
        w.SetEngine(engine);
        var hovered = new List<int>();
        w.BanHoverHandler = id => { hovered.Add(id); return Task.FromResult(200); };
        w.HoverHandler    = id => { hovered.Add(id); return Task.FromResult(200); };
        Pump();

        // Баны.
        var d = Draft("utility", ban: true);
        var hov = new Dictionary<int, HashSet<int>>();
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        var banList  = (ItemsControl)w.FindName("BanFullList");
        var banStrip = (ContentControl)w.FindName("BanHiddenStrip");
        var popup    = (System.Windows.Controls.Primitives.Popup)w.FindName("HiddenPopup");
        var bansBtn  = (UIElement)w.FindName("HiddenBansBtn");
        var picksBtn = (UIElement)w.FindName("HiddenPicksBtn");
        // Список всплывает отдельным окошком — прозрачным, чтобы проверка его не показывала.
        popup.Child.Opacity = 0;
        string Text(string name) => ((TextBlock)w.FindName(name)).Text;
        bool Shown(string name) => ((FrameworkElement)w.FindName(name)).Visibility == Visibility.Visible;
        List<HiddenChampVm> Rows() => Cards<HiddenChampVm>((ItemsControl)w.FindName("HiddenPopupList"));
        UIElement? RestoreBtn() => Find<Border>(popup.Child, b => b.Tag is HiddenChampVm);
        void Toggle(UIElement btn) { Press(btn); Pump(); }
        var cards = Cards<RecCard>(banList);
        Check("у совета бана есть кнопка, без наведения скрыта",
              Button(w, banList, 0) is { Visibility: Visibility.Collapsed }, "");
        Check("кнопки «Баны» / «Пики» у переключателя тир-листа видны и без скрытых",
              Shown("TierListBar") && Shown("HiddenBansBtn") && Shown("HiddenPicksBtn")
              && Text("HiddenBansCount") == "" && Text("HiddenPicksCount") == "", "");
        Check("полосы над советами нет", banStrip.Visibility == Visibility.Collapsed, "");
        Toggle(bansBtn);
        Check("пустой список — подсказка, откуда берутся скрытые",
              popup.IsOpen && Rows().Count == 0 && Shown("HiddenPopupEmpty"), Text("HiddenPopupEmpty"));
        Toggle(bansBtn);
        Check("повторный клик по кнопке — список закрылся", !popup.IsOpen, "");
        if (snap is { Length: > 0 }) { Button(w, banList, 0)!.Visibility = Visibility.Visible; Snap(w, snap, "1-ban-button.png"); }

        // Положительный контроль: клик по самой карточке наводит бан.
        Press(Card(banList, 1)!);
        Pump();
        Check("клик по карточке наводит бан (сама проверка клика жива)", hovered.SequenceEqual([cards[1].ChampionId]),
              string.Join(",", hovered));
        hovered.Clear();

        var x = cards[0].ChampionId;
        Check("клик по 👎 до карточки не доходит", !PressReachesCard(Button(w, banList, 0)!, Card(banList, 0)!), "");
        Pump();
        var after = Cards<RecCard>(banList);
        Check("клик по 👎 — карточки сразу нет", after.All(c => c.ChampionId != x), Names(after.Select(c => c.ChampionId).ToList()));
        Check("советов столько же", after.Count == cards.Count, $"{cards.Count} → {after.Count}");
        Check("и бан в клиенте не наведён", hovered.Count == 0, string.Join(",", hovered));
        Check("на кнопке «Баны» — счётчик 1, на «Пики» — пусто",
              Text("HiddenBansCount") == "1" && Text("HiddenPicksCount") == "", $"{Text("HiddenBansCount")} / {Text("HiddenPicksCount")}");
        Check("полосы над советами нет — скрытые в списке у кнопок", banStrip.Visibility == Visibility.Collapsed, "");

        // Новое событие драфта — по-прежнему скрыт (не только в окне).
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        Check("следующее событие драфта его не возвращает", Cards<RecCard>(banList).All(c => c.ChampionId != x), "");

        Wait(0.35);   // закрытие кликом по кнопке гасит повторное открытие на 300 мс
        Toggle(bansBtn);
        var rows = Rows();
        Check("список банов: он, с именем и «игр до возвращения: 10»",
              popup.IsOpen && rows.Select(r => r.ChampionId).SequenceEqual([x]) && rows[0].Name == DataDragon.Name(x)
              && rows[0].Status == Loc.T("dislike.statusHidden", Dislikes.HideGames), rows.FirstOrDefault()?.Status ?? "");
        Check("заголовок — скрытые баны", Text("HiddenPopupTitle").StartsWith(Loc.T("dislike.titleBans", "").Split('·')[0].Trim()),
              Text("HiddenPopupTitle"));
        if (snap is { Length: > 0 }) SnapElement((FrameworkElement)popup.Child, snap, "2-hidden-list.png");
        Press(RestoreBtn()!);
        Pump();
        Check("«Вернуть» — совет снова на своём месте", Cards<RecCard>(banList).FirstOrDefault()?.ChampionId == x, "");
        Check("список открыт и пуст, счётчик пропал",
              popup.IsOpen && Rows().Count == 0 && Shown("HiddenPopupEmpty") && Text("HiddenBansCount") == "", "");
        popup.IsOpen = false;

        // Пики.
        var p = Draft("utility", ban: false, enemies: [222, 51]);
        w.UpdateRecommendations(engine.Recommend(p, s.DraftCount), p, engine);
        Pump();
        var pickList  = (ItemsControl)w.FindName("FullRecList");
        var pickStrip = (ContentControl)w.FindName("PickHiddenStrip");
        var picks = Cards<FullRecCard>(pickList);
        var y = picks.First(c => !c.FromPool).ChampionId;
        var yi = picks.FindIndex(c => c.ChampionId == y);
        Check("у карточки пика кнопка есть", Button(w, pickList, yi) is not null && picks[yi].CanDislike, "");
        if (snap is { Length: > 0 }) { Button(w, pickList, yi)!.Visibility = Visibility.Visible; Snap(w, snap, "3-pick-button.png"); }
        Check("клик по 👎 на пике до карточки не доходит", !PressReachesCard(Button(w, pickList, yi)!, Card(pickList, yi)!), "");
        Pump();
        var pAfter = Cards<FullRecCard>(pickList);
        Check("клик по 👎 — пика нет", pAfter.All(c => c.ChampionId != y), "");
        Check("и чемпион в клиенте не наведён", hovered.Count == 0, string.Join(",", hovered));
        Check("в фазе пиков тир-листа нет — полоса «Скрыты» над пиками", pickStrip.Visibility == Visibility.Visible, "");

        // Следующий драфт, фаза банов: скрытый пик — на кнопке «Пики», отдельно от банов.
        w.DraftEnded();
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        Check("в фазе банов скрытый пик считается на кнопке «Пики»",
              Text("HiddenPicksCount") == "1" && Text("HiddenBansCount") == "", $"{Text("HiddenBansCount")} / {Text("HiddenPicksCount")}");
        Press(Button(w, banList, 0)!);
        Pump();
        Check("скрыл и бан — у каждой кнопки свой счётчик",
              Text("HiddenBansCount") == "1" && Text("HiddenPicksCount") == "1", "");
        Toggle(picksBtn);
        Check("список пиков — только пик", popup.IsOpen && Rows().Select(r => r.ChampionId).SequenceEqual([y]), "");
        Toggle(bansBtn);
        Check("клик по «Баны» при открытых пиках — список банов", popup.IsOpen && Rows().Select(r => r.ChampionId).SequenceEqual([x]), "");
        Toggle(bansBtn);
        Wait(0.35);
        Toggle(picksBtn);
        Press(RestoreBtn()!);
        Pump();
        Check("вернул пик из списка — запись удалена, бан на месте",
              Dislikes.Active(Dislikes.Kind.Pick, "support").Count == 0 && Text("HiddenPicksCount") == ""
              && Text("HiddenBansCount") == "1", "");
        popup.IsOpen = false;

        // Возвращающийся (уже в советах, только ниже) — тоже в списке, и его можно вернуть на место.
        _played = Dislikes.HideGames + 2;
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        Wait(0.35);
        Toggle(bansBtn);
        Check("возвращающийся — в списке, «мест ниже своего: 3»",
              Rows().FirstOrDefault() is { } rr && rr.ChampionId == x && rr.Status == Loc.T("dislike.statusReturning", 3),
              Rows().FirstOrDefault()?.Status ?? "");
        Press(RestoreBtn()!);
        Pump();
        Check("вернул — сразу первым", Cards<RecCard>(banList).FirstOrDefault()?.ChampionId == x, "");
        popup.IsOpen = false;
        _played = 0;

        // Свой бан сделан: советов нет, тир-лист и кнопки — есть.
        Dislikes.Add(Dislikes.Kind.Ban, x, "support");
        var done = Draft("utility", ban: true) with { MyBanActionId = -1, MyBanInProgress = false, MyBanDone = true };
        w.UpdateBans([], done, engine, hov);
        Pump();
        Check("после своего бана кнопки на месте, счётчик есть", Shown("BansDonePanel") && Shown("TierListBar")
              && Shown("HiddenBansBtn") && Text("HiddenBansCount") == "1", "");
        Wait(0.35);
        Toggle(bansBtn);
        Press(RestoreBtn()!);
        Pump();
        Check("вернул и там — счётчик пропал, советы не появились",
              Text("HiddenBansCount") == "" && Shown("BansDonePanel"), "");

        // Пики начались — тир-лист ушёл, список закрывается вместе с ним.
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        Wait(0.35);
        Toggle(bansBtn);
        w.UpdateRecommendations(engine.Recommend(p, s.DraftCount), p, engine);
        Pump();
        Check("с уходом тир-листа список закрылся", !popup.IsOpen, "");

        // Тир-лист выключен в настройках — вернуть скрытый бан можно из полосы над советами.
        s.BansTierList = false;
        w.UpdateBans(engine.RecommendBans(d), d, engine, hov);
        Pump();
        Press(Button(w, banList, 0)!);
        Pump();
        Check("без тир-листа скрытый бан — в полосе над советами",
              !Shown("TierListBar") && banStrip.Visibility == Visibility.Visible, "");
        Press(Icon(banStrip)!);
        Pump();
        Check("и оттуда возвращается", banStrip.Visibility == Visibility.Collapsed, "");
        s.BansTierList = true;

        // Пик снова в подборе.
        w.UpdateRecommendations(engine.Recommend(p, s.DraftCount), p, engine);
        Pump();
        Check("возвращённый пик снова в списке", Cards<FullRecCard>(pickList).Any(c => c.ChampionId == y), "");

        // Мой пик — без кнопки: его я вижу всегда.
        var mine = p with { MyTeam = [.. p.MyTeam.Select(q => q.IsLocalPlayer ? q with { ChampionId = 37 } : q)] };
        mine = mine with { Me = mine.MyTeam.First(q => q.IsLocalPlayer) };
        w.UpdateRecommendations(engine.Recommend(mine, s.DraftCount), mine, engine);
        Pump();
        var my = Cards<FullRecCard>(pickList).FirstOrDefault(c => c.ChampionId == 37);
        Check("у моего пика кнопки нет", my is { CanDislike: false }, "");

        w.Close();
        Reset();
    }

    // ── Общее ────────────────────────────────────────────────────────────────

    private static void Reset()
    {
        var f = Path.Combine(AppPaths.Root, "dislikes.json");
        foreach (var e in Entries()) Dislikes.Remove(e.Kind, e.Champ, e.Role);
        if (File.Exists(f)) File.Delete(f);
        _played = 0;
    }

    private static List<Dislikes.Entry> Entries()
    {
        var f = Path.Combine(AppPaths.Root, "dislikes.json");
        return File.Exists(f)
            ? System.Text.Json.JsonSerializer.Deserialize<List<Dislikes.Entry>>(File.ReadAllText(f)) ?? []
            : [];
    }

    private static List<int> Ids(IEnumerable<BanRec> bans) => [.. bans.Select(b => b.ChampionId)];

    private static List<int> Ids(IEnumerable<Recommendation> recs) => [.. recs.Select(r => r.ChampionId)];

    private static string Names(List<int> ids) => string.Join(", ", ids.Select(DataDragon.Name));

    private static List<T> Cards<T>(ItemsControl list) => [.. (list.ItemsSource as IEnumerable<T>) ?? []];

    /// Карточка списка (корень шаблона: Border с чемпионом в Tag).
    private static Border? Card(ItemsControl list, int i) =>
        Find<Border>(list.ItemContainerGenerator.ContainerFromIndex(i), b => b.Tag is int);

    /// Кнопка «не советовать» на карточке.
    private static ContentControl? Button(Window w, ItemsControl list, int i) =>
        // По типу и чемпиону в Tag: стиль у каждого словаря шаблонов — своя
        // копия OverlayResources, и сравнивать с ресурсом окна нельзя.
        Find<ContentControl>(list.ItemContainerGenerator.ContainerFromIndex(i),
                             c => c.GetType() == typeof(ContentControl) && c.Tag is int);

    /// Первая иконка скрытого (полоса «Скрыты» или окошко в шапке тир-листа).
    private static FrameworkElement? Icon(DependencyObject where) =>
        Find<Grid>(where, g => g.Tag is HiddenChampVm);

    private static T? Find<T>(DependencyObject? d, Func<T, bool> ok) where T : DependencyObject
    {
        if (d is null) return null;
        if (d is T t && ok(t)) return t;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (Find(VisualTreeHelper.GetChild(d, i), ok) is { } r) return r;
        return null;
    }

    /// Нажатие левой кнопкой, как от мыши: MouseDown всплывает от элемента, а
    /// WPF по дороге поднимает MouseLeftButtonDown у каждого предка — так
    /// видно, доходит ли клик по кнопке до карточки.
    private static void Press(UIElement el) =>
        el.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                      { RoutedEvent = Mouse.MouseDownEvent });

    /// <summary>
    /// Нажать кнопку на карточке и узнать, дошёл ли клик до самой карточки.
    /// Карточка после 👎 исчезает сразу, и дошедший клик ничего бы не навёл —
    /// «чемпион не наведён» такого не ловит; Handled тоже ничего не говорит:
    /// его ставит ScrollViewer списка. Поэтому слушаем карточку напрямую.
    /// </summary>
    private static bool PressReachesCard(UIElement button, UIElement card)
    {
        var reached = false;
        card.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => reached = true));
        Press(button);
        return reached;
    }

    private static DraftState Draft(string pos, bool ban, List<int>? enemies = null)
    {
        var team = new List<DraftPlayer> { new(0, 0, 0, pos, true) };
        for (var i = 1; i < 5; i++) team.Add(new(i, 0, 0, "", false));
        var their = (enemies ?? []).Select((id, i) => new DraftPlayer(5 + i, id, 0, "", false)).ToList();
        return new DraftState(
            MyTeam: team, TheirTeam: their, MyTeamBans: [], TheirTeamBans: [],
            Me: team[0], MyPosition: pos, DirectOpponent: null, ExposedToCounter: false,
            InBanPhase: ban, Bench: [], IsAram: false,
            MyPickActionId: ban ? -1 : 1, MyPickInProgress: false, ActiveCells: [],
            FirstPickCell: -1, MyBanActionId: ban ? 0 : -1, MyBanInProgress: ban, MyBanDone: !ban);
    }

    /// Журнал: на каждом мейне по двадцать свежих игр.
    private static SessionTracker.PlayHistory Hist(int[] mains)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var times = Enumerable.Range(0, SessionTracker.PlayHistory.KeepTimes)
                              .Select(i => now - i * 86400L).ToArray();
        return new SessionTracker.PlayHistory(
            mains.ToDictionary(m => m, _ => (20, 10)),
            mains.ToDictionary(m => m, _ => times),
            60, now);
    }

    /// Имена и иконки чемпионов: кэш игрока копируем к себе, его папку не трогаем.
    private static void Assets(string playerDir)
    {
        foreach (var sub in new[] { "ddragon", "icons" })
        {
            var from = Path.Combine(playerDir, sub);
            if (!Directory.Exists(from)) continue;
            foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                var to = Path.Combine(AppPaths.Root, Path.GetRelativePath(playerDir, f));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (!File.Exists(to)) File.Copy(f, to);
            }
        }
        DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None).GetAwaiter().GetResult();
        IconCache.PreloadAllAsync(null, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// Подождать, не останавливая окно (таймеры и анимации идут).
    private static void Wait(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Thread.Sleep(60);
        }
    }

    /// Окно показываем так, чтобы человек его не увидел (см. AramWindow).
    private static void ShowHidden(Window w)
    {
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -32000; w.Top = -32000;
        w.WindowStyle = WindowStyle.None;
        w.AllowsTransparency = true;
        w.Opacity = 0;
        w.Show();
    }

    /// Снимок одного элемента (список скрытых живёт в своём всплывающем окошке).
    private static void SnapElement(FrameworkElement el, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var was = el.Opacity;
        el.Opacity = 1;
        el.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)el.ActualWidth + 20, (int)el.ActualHeight + 20, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(el);
        el.Opacity = was;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        w.UpdateLayout();   // кнопку могли показать только что — без раскладки её нет
        var root = (FrameworkElement)w.Content;
        var bmp = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

    private static void Check(string what, bool ok, string detail)
    {
        if (!ok) _fails++;
        Console.WriteLine($"  {(ok ? "ок  " : "FAIL")}  {what}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    }
}
