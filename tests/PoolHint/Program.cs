using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Counterplay;
using Button = System.Windows.Controls.Button;

namespace Counterplay.Tests;

/// <summary>
/// Подсказка режима пула в лобби.
///
/// Владелец 15 октября: режим пула помнится по очереди, а в соло/дуо играют то
/// вдвоём, то одни. Дуо-пул без напарника молчит, а забытый личный пул при
/// напарнике лишает подсказок для пары. Просьба — подсказывать: с напарником
/// включить дуо, одному — личный пул, а если его нет — обычный режим; карточкой
/// поверх кнопок пула и полосы чемпионов.
///
/// Первая часть — правила (<see cref="PoolHint.Decide"/>), вторая — карточка в
/// настоящем окне: появляется только в лобби, «Не сейчас» её убирает до смены
/// состава, «Переключить» включает режим, как его кнопка.
///
/// Своя папка (<c>AppPaths.RootOverride</c>, <c>PoolStore.DirOverride</c>),
/// окно — невидимое.
/// </summary>
internal static class Program
{
    private static int _fails;

    private const string Friend = "TEST-poolhint-friend";
    private const string Other  = "TEST-poolhint-other";
    private const string Third  = "TEST-poolhint-third";

    /// Игрок с напарником играет с драфтом: без игр в ранкеде и нормалах
    /// кнопок пула нет (решение владельца, 24.10), а с ними и подсказки.
    private static void SeedJournal()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var games = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(1, 5).Select(i =>
            (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            { ["Ts"] = now - i * 3600, ["ChampionId"] = 86, ["Win"] = true }).ToArray());
        File.WriteAllText(Path.Combine(AppPaths.Root, "session.json"), new System.Text.Json.Nodes.JsonObject
        {
            ["LastAccount"] = "me",
            ["Accounts"] = new System.Text.Json.Nodes.JsonObject
            {
                ["me"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Queues"] = new System.Text.Json.Nodes.JsonObject
                    { ["normal"] = new System.Text.Json.Nodes.JsonObject { ["Games"] = games } },
                },
            },
        }.ToJsonString());
    }

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        AppPaths.RootOverride = Path.Combine(Path.GetTempPath(), "counterplay-test-root", "PoolHint");
        try { Directory.Delete(AppPaths.RootOverride, true); } catch { }
        Directory.CreateDirectory(AppPaths.RootOverride);
        PoolStore.DirOverride = Path.Combine(AppPaths.RootOverride, "pools");
        Directory.CreateDirectory(PoolStore.DirOverride);
        PoolStore.Reload();
        SeedJournal();
        Log.FileDisabled = true;
        Loc.Init();   // без него нет английского словаря, на который падают остальные
        Loc.SetLanguage("ru");

        try
        {
            Rules();
            Window();
        }
        finally { try { Directory.Delete(AppPaths.RootOverride, true); } catch { } }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: подсказка режима пула предлагает то, что нужно, и только в лобби"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    // ── 1. Правила ─────────────────────────────────────────────────────────

    /// Аккаунт: личный пул «Мейны» (со звездой), дуо-пул с Ваней (закреплён за
    /// ним) и дуо-пул «Новый» без напарника.
    private static AccountPools Account(PoolKind kind, string? id, bool pools = true, bool fav = true)
    {
        var a = new AccountPools();
        if (pools)
        {
            a.Pools.Add(new ChampPool { Id = "mains", Name = "Мейны" });
            if (!fav) a.Pools.Add(new ChampPool { Id = "second", Name = "Второй" });
            if (fav) a.FavPoolId = "mains";
        }
        a.DuoPools.Add(new DuoPool { Id = "ivan", FriendName = "саппы + топ", FriendPuuid = Friend, FriendNick = "Ваня" });
        a.DuoPools.Add(new DuoPool { Id = "free", FriendName = "Новый" });
        a.ActiveKind = kind;
        a.ActiveId = id;
        return a;
    }

    private static AccountPools Add(AccountPools a, DuoPool d) { a.DuoPools.Add(d); return a; }

    private static Dictionary<string, string> Lobby(params (string Puuid, string Name)[] m) =>
        m.ToDictionary(x => x.Puuid, x => x.Name);

    private static PoolSuggestion? Decide(AccountPools a, Dictionary<string, string> mates,
                                          string queue = "solo", bool known = true) =>
        PoolHint.Decide(a, queue, known, mates.Count, mates);

    private static string Show(PoolSuggestion? s) =>
        s is null ? "нет подсказки" : $"{s.Case} → {s.Kind} {s.Id ?? "(меню)"} «{s.Target}» о «{s.Who}»";

    private static void Rules()
    {
        Console.WriteLine("правила:");
        var alone = Lobby();
        var ivan  = Lobby((Friend, "Ivan"));
        var petya = Lobby((Other, "Petya"));

        var s = Decide(Account(PoolKind.Duo, "ivan"), alone);
        Check("один, включён дуо — на личный пул со звездой",
              s is { Case: PoolHintCase.Alone, Kind: PoolKind.Pool, Id: "mains", Target: "Мейны" }, Show(s));

        s = Decide(Account(PoolKind.Duo, "ivan", pools: false), alone);
        Check("один, включён дуо, личных пулов нет — на обычный режим",
              s is { Case: PoolHintCase.Alone, Kind: PoolKind.Normal, Id: null }, Show(s));

        s = Decide(Account(PoolKind.Duo, "ivan", fav: false), alone);
        Check("один, личных пулов два и звезды нет — выбор из меню",
              s is { Kind: PoolKind.Pool, Id: null }, Show(s));

        s = Decide(Account(PoolKind.Duo, "free"), alone);
        Check("один, включён дуо без напарника — тоже выключить", s is { Case: PoolHintCase.Alone }, Show(s));

        Check("один, включён личный пул — молчим", Decide(Account(PoolKind.Pool, "mains"), alone) is null, "");
        Check("один, обычный режим — молчим", Decide(Account(PoolKind.Normal, null), alone) is null, "");

        s = Decide(Account(PoolKind.Pool, "mains"), ivan);
        Check("в лобби Ваня, включён личный пул — дуо-пул с ним, по нику из лобби",
              s is { Case: PoolHintCase.WithMate, Kind: PoolKind.Duo, Id: "ivan", Who: "Ivan" }, Show(s));

        s = Decide(Account(PoolKind.Normal, null), ivan);
        Check("в лобби Ваня, обычный режим — тоже дуо-пул с ним", s is { Kind: PoolKind.Duo, Id: "ivan" }, Show(s));

        Check("в лобби Ваня, его дуо-пул уже включён — молчим",
              Decide(Account(PoolKind.Duo, "ivan"), ivan) is null, "");

        s = Decide(Account(PoolKind.Duo, "free"), ivan);
        Check("в лобби Ваня, включён чужой дуо-пул — переключить на его",
              s is { Case: PoolHintCase.WithMate, Id: "ivan" }, Show(s));

        s = Decide(Account(PoolKind.Pool, "mains"), petya);
        Check("в лобби незнакомый — дуо-пул без напарника, по его нику",
              s is { Case: PoolHintCase.WithMate, Id: "free", Target: "Новый", Who: "Petya" }, Show(s));

        Check("в лобби незнакомый, включён дуо-пул без напарника — молчим: он и станет напарником",
              Decide(Account(PoolKind.Duo, "free"), petya) is null, "");

        s = Decide(Account(PoolKind.Duo, "ivan"), petya);
        Check("в лобби не Ваня, а включён пул с Ваней — Вани нет, на личный пул",
              s is { Case: PoolHintCase.MateAway, Kind: PoolKind.Pool, Who: "Ваня" }, Show(s));

        var flex = Lobby((Other, "Petya"), (Third, "Kolya"));
        Check("флекс без Вани, дуо-пул без напарника — молчим: человек найдётся в пати",
              Decide(Account(PoolKind.Duo, "free"), flex) is null, "");
        s = Decide(Account(PoolKind.Pool, "mains"), flex, "flex");
        Check("флекс без Вани, личный пул — хотя бы предложить дуо-пул без напарника, «с друзьями»",
              s is { Case: PoolHintCase.WithParty, Kind: PoolKind.Duo, Id: "free", Who: "" }, Show(s));

        // Пул без закреплённого напарника, подписанный его ником: во флексе
        // ники сопартийцев известны из лобби.
        var nickAcc = Account(PoolKind.Pool, "mains");
        nickAcc.DuoPools.Add(new DuoPool { Id = "kolya", FriendName = "мид + джунгли", FriendNick = "kolya#EUW" });
        s = Decide(nickAcc, flex, "flex");
        Check("флекс: пул с ником Коли, Коля в лобби — его пул, по нику",
              s is { Case: PoolHintCase.WithMate, Id: "kolya", Who: "Kolya" }, Show(s));
        var tileAcc = Account(PoolKind.Normal, null);
        tileAcc.DuoPools.Add(new DuoPool { Id = "tile", FriendName = "Kolya" });
        s = Decide(tileAcc, flex, "flex");
        Check("флекс: ник в названии плитки тоже годится", s is { Id: "tile", Who: "Kolya" }, Show(s));
        var onAcc = Add(Account(PoolKind.Duo, "kolya"), new DuoPool { Id = "kolya", FriendNick = "Kolya" });
        Check("флекс: включён пул с ником Коли — молчим", Decide(onAcc, flex, "flex") is null, "");
        var vasyaAcc = new AccountPools();
        vasyaAcc.DuoPools.Add(new DuoPool { Id = "vasya", FriendNick = "Vasya" });
        Check("флекс: пул с ником Васи, Васи в лобби нет — его не предлагаем",
              Decide(vasyaAcc, flex, "flex") is null, "");
        s = Decide(Account(PoolKind.Pool, "mains"), Lobby((Friend, "Ivan"), (Other, "Petya")), "flex");
        Check("флекс с Ваней — его дуо-пул", s is { Kind: PoolKind.Duo, Id: "ivan" }, Show(s));

        Check("состав пати неизвестен — молчим",
              Decide(Account(PoolKind.Duo, "ivan"), alone, known: false) is null, "");
        Check("ARAM — молчим: чемпиона там не выбирают",
              Decide(Account(PoolKind.Duo, "ivan"), alone, "aram") is null, "");

        var k1 = Decide(Account(PoolKind.Pool, "mains"), ivan)?.Key;
        var k2 = Decide(Account(PoolKind.Pool, "mains"), ivan)?.Key;
        var k3 = Decide(Account(PoolKind.Pool, "mains"), ivan, "flex")?.Key;
        Check("«Не сейчас» помнится по составу и очереди: тот же состав — тот же ключ, другая очередь — другой",
              k1 is not null && k1 == k2 && k1 != k3, $"{k1} / {k3}");
    }

    // ── 2. Окно ────────────────────────────────────────────────────────────

    private static void Window()
    {
        Console.WriteLine("\nокно:");
        var a = PoolStore.Current();
        a.Pools.Add(new ChampPool { Id = "mains", Name = "Мейны" });
        a.DuoPools.Add(new DuoPool { Id = "ivan", FriendName = "саппы + топ", FriendPuuid = Friend, FriendNick = "Ваня" });
        PoolStore.SetQueue("solo");
        PoolStore.SetActive(PoolKind.Pool, "mains");

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var w = new OverlayWindow();
        ShowHidden(w);
        try
        {
            var card  = (FrameworkElement)w.FindName("PoolHintCard");
            var title = (TextBlock)w.FindName("PoolHintTitle");
            var text  = (TextBlock)w.FindName("PoolHintText");
            var apply = (Button)w.FindName("PoolHintApplyBtn");
            var later = (Button)w.FindName("PoolHintLaterBtn");
            bool Shown() => card.Visibility == Visibility.Visible && card.IsHitTestVisible;

            PartyOf(Friend, "Ivan");
            Phase(w, "None");
            Check("в меню (не в лобби) — подсказки нет, хоть Ваня и был в прошлом лобби", !Shown(), "");

            Phase(w, "Lobby");
            Check("в лобби с Ваней, включён личный пул — карточка поверх кнопок пула", Shown(), "");
            // Снимки (CP_SNAP=<папка>): посреди блика и в покое.
            var snap = Environment.GetEnvironmentVariable("CP_SNAP");
            if (snap is { Length: > 0 })
            {
                Wait(420); Snap(w, snap, "pool-hint-shine.png");
                Wait(1200); Snap(w, snap, "pool-hint.png");
            }
            Check("заголовок — с кем играешь", title.Text == Loc.T("poolHint.withMate", "Ivan"), title.Text);
            Check("текст — какой дуо-пул включить", text.Text == Loc.T("poolHint.toDuo", "саппы + топ"), text.Text);

            Phase(w, "Matchmaking");
            Check("в поиске игры — карточка стоит", Shown(), "");

            Click(later);
            Check("«Не сейчас» — карточка уходит", !Shown(), "");
            Wait(400);
            Check("…и убирается совсем, а не висит прозрачной", card.Visibility == Visibility.Collapsed, $"{card.Visibility}");
            var under = (UIElement)w.FindName("PoolUnder");
            Check("кнопки пула, погашенные под карточкой, снова видны", under.Opacity > 0.99, $"{under.Opacity:F2}");
            PartyOf(Friend, "Ivan");
            Phase(w, "Lobby");
            Check("тот же состав — после «Не сейчас» не возвращается", !Shown(), "");

            PartyOf(null);
            Check("ушёл из пати, включён личный пул — подсказывать нечего", !Shown(), "");

            PoolStore.SetActive(PoolKind.Duo, "ivan");
            w.RefreshPoolMode();
            Pump();
            Check("один, а включён дуо-пул — карточка «играешь один»",
                  Shown() && title.Text == Loc.T("poolHint.alone"), title.Text);
            Check("…и предлагает личный пул", text.Text == Loc.T("poolHint.toPool", "Мейны"), text.Text);

            Click(apply);
            var now = PoolStore.Current();
            Check("«Переключить» включает личный пул", now.ActiveKind == PoolKind.Pool && now.ActiveId == "mains",
                  $"{now.ActiveKind} {now.ActiveId}");
            Check("…и карточка уходит", !Shown(), "");

            PartyOf(Friend, "Ivan");
            Check("Ваня вернулся — после «Не сейчас» с ним уже не зовём, состав тот же", !Shown(), "");
            PartyOf(Friend, "Ivan", (Other, "Petya"));
            Check("состав сменился — снова зовём", Shown() && title.Text == Loc.T("poolHint.withMate", "Ivan"), title.Text);
            Click(apply);
            now = PoolStore.Current();
            Check("«Включить» включает дуо-пул с Ваней", now.ActiveKind == PoolKind.Duo && now.ActiveId == "ivan",
                  $"{now.ActiveKind} {now.ActiveId}");

            PartyOf(null);
            Check("снова один — снова подсказка", Shown(), "");
            AppSettings.Current.ReadyPool = false;
            AppSettings.Save();
            Pump();
            Check("блок пула выключен в настройках — подсказки нет", !Shown(), "");
            AppSettings.Current.ReadyPool = true;
            AppSettings.Save();
            Pump();
            Check("включили обратно — подсказка на месте", Shown(), "");

            Loc.SetLanguage("en");
            Pump();
            Check("сменили язык — текст на новом языке",
                  title.Text == Loc.T("poolHint.alone") && !title.Text.StartsWith("poolHint"), title.Text);
            Loc.SetLanguage("ru");

            // Песочница: «Поиск игры» — новое лобби, отложенное снова в силе.
            // Владелец отложил карточку и ждал её на поиске с тем же другом.
            Click(later);
            Check("отложил — карточки нет", !Shown(), "");
            w.ForgetPoolHintLater();
            Pump();
            Check("песочница начала поиск — отложенная карточка вернулась", Shown(), title.Text);

            // Полоса ожидания: время вышло — карточка уходит сама, как по «Не
            // сейчас» (владелец: «через 15 сек скрывается, если ничего не
            // нажать»). Курсор над карточкой отсчёт держит. Срок укорочен до 1 с.
            var seconds = typeof(OverlayWindow).GetField("PoolHintSeconds",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            seconds.SetValue(null, 1.0);
            var bar = (System.Windows.Media.ScaleTransform)((FrameworkElement)w.FindName("PoolHintTimer")).RenderTransform;
            Click(later);
            w.ForgetPoolHintLater();
            Pump();
            Check("новая карточка — полоса ожидания полная", Shown() && bar.ScaleX > 0.8, $"{bar.ScaleX:F2}");
            Wait(450);
            Check("полоса тает", bar.ScaleX is > 0.05 and < 0.9, $"{bar.ScaleX:F2}");
            Wait(1000);
            Check("время вышло — карточка ушла сама", !Shown(), "");
            PartyOf(null);
            Check("…и не возвращается, как после «Не сейчас»", !Shown(), "");

            w.ForgetPoolHintLater();
            Pump();
            card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseEnterEvent });
            Wait(1500);
            Check("курсор над карточкой — отсчёт стоит", Shown(), $"{bar.ScaleX:F2}");
            card.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = System.Windows.Input.Mouse.MouseLeaveEvent });
            Wait(1500);
            Check("курсор ушёл — отсчёт дошёл до конца", !Shown(), "");
            seconds.SetValue(null, 15.0);
        }
        finally { w.Close(); }
    }

    /// Лобби, как его присылает клиент: я и сопартийцы. null — играю один.
    private static void PartyOf(string? puuid, string name = "", params (string Puuid, string Name)[] more)
    {
        var members = new List<object> { new { summonerId = 1, puuid = "TEST-poolhint-me", gameName = "Я" } };
        var id = 2;
        if (puuid is not null) members.Add(new { summonerId = id++, puuid, gameName = name, tagLine = "EUW" });
        foreach (var (p, n) in more) members.Add(new { summonerId = id++, puuid = p, gameName = n });
        var json = JsonSerializer.Serialize(new { localMember = new { summonerId = 1, puuid = "TEST-poolhint-me" }, members });
        Party.Update(JsonDocument.Parse(json).RootElement);
        Pump();
    }

    private static void Phase(OverlayWindow w, string phase)
    {
        w.ShowReadyPhase(phase);
        Pump();
    }

    private static void Click(Button b)
    {
        b.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Pump();
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private static void Wait(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
    }

    private static void Snap(Window w, string dir, string file)
    {
        Directory.CreateDirectory(dir);
        var root = (FrameworkElement)w.Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)root.ActualWidth * 2, (int)root.ActualHeight * 2, 192, 192, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(dir, file));
        enc.Save(fs);
        Console.WriteLine($"  снимок: {Path.Combine(dir, file)}");
    }

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

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(detail.Length > 0 ? "  — " + detail : "")}");
        if (!ok) _fails++;
    }
}
