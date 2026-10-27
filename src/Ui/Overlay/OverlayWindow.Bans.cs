using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — баны: советы, баны обеих команд, раскрытие вражеских.
public partial class OverlayWindow
{
    /// <summary>
    /// В центре не пики — советы или баны команд, объявление или заглушка.
    /// Вторая строка центра тогда за тир-листом (у заглушки — за её нижней
    /// плашкой), и ни пул роли, ни руны её не занимают. Вид «без роли» — не в
    /// счёт: панель у него та же, что в объявлении, а руны моего чемпиона ему
    /// нужны. Важно в первые секунды
    /// пиков, пока окно держит раскрытые вражеские баны: Program уже обновляет
    /// панель рун, и «рун нет — вернём пул роли» клало полосу чемпионов роли
    /// поверх тир-листа (скриншот владельца).
    /// </summary>
    private bool CenterNotPicks =>
        BanScroll.Visibility == Visibility.Visible || BansDonePanel.Visibility == Visibility.Visible
        || (PlanPanel.Visibility == Visibility.Visible && !_blindView)
        || CenterSkeleton.Visibility == Visibility.Visible;

    // Полный вид для фазы банов: тот же раздел (команды + центр), но в центре баны.
    private void RenderBansFull(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        PickHint.Visibility = Visibility.Collapsed;

        BanFullList.ItemsSource = ComposeBanAdvice(bans, draft).Select((b, i) => new RecCard
        {
            ChampionId = b.Id,
            IsSelected = b.Rec is not null && b.Id == _banHoverId,
            Gone   = b.Rec is null,
            Rank   = $"{i + 1}.",
            Name   = DataDragon.Name(b.Id),
            Reason = b.Rec is { } r ? BanReasonText(r.Reasons) : Loc.T("ban.allyBanned"),
            Icon   = IconCache.Get(b.Id),
        }).ToList();

        RecScroll.Visibility = Visibility.Collapsed; // показываем баны
        BanScroll.Visibility = Visibility.Visible;
        BansDonePanel.Visibility = Visibility.Collapsed;
        PlanPanel.Visibility = Visibility.Collapsed;
        PickBar.Visibility   = Visibility.Collapsed;  // пик в банфазе не нужен
        // Баны — 100% приоритет: их строка занимает всю нужную высоту (Auto),
        // тир-лист довольствуется остатком (звёздная строка со скроллом внутри).
        CenterGrid.RowDefinitions[0].Height = GridLength.Auto;
        CenterGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        // Сбрасываем наведённого, только если его уже забанили/взяли (выбор из
        // тир-листа при этом сохраняется — он вне списка рекомендаций).
        if (_banHoverId > 0 && !StillAvailable(draft, _banHoverId)) _banHoverId = 0;
        UpdateBanBar();
        RolePoolBar.Visibility = Visibility.Collapsed;   // в банфазе строку занимает тир-лист
        RenderTierList(draft);
        // Скрытые «не советовать» — окно из кнопки в шапке тир-листа; тир-лист
        // выключен в настройках — полосой над советами, иначе вернуть их негде.
        if (TierListBar.Visibility == Visibility.Visible) BanHiddenStrip.Visibility = Visibility.Collapsed;
        else ShowHiddenStrip(BanHiddenStrip, Dislikes.Kind.Ban);
        RenderTeams(draft);
    }

    // Советы банов, показанные в этом драфте, по порядку на экране.
    private List<int> _banAdviceShown = [];

    // Живых советов — сколько дал движок (пять), перечёркнутых — не больше двух.
    private const int BanAdviceGoneMax = 2;

    /// <summary>
    /// Советы банов на экран. Союзник забанил одного из показанных — карточка
    /// остаётся на своём месте перечёркнутой («уже забанил союзник»), а
    /// следующий кандидат встаёт вниз. Раньше движок просто выбрасывал
    /// забаненного, и карточка молча подменялась другим чемпионом: в песочнице
    /// союзники-боты банят с 4-й секунды, и владелец видел «через пять секунд
    /// поменялся один чемпион» почти в каждом прогоне. Rec = null — перечёркнутый.
    /// </summary>
    private List<(int Id, BanRec? Rec)> ComposeBanAdvice(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        var ours = draft.MyTeamBans.Where(id => id > 0).ToHashSet();
        var list = bans.Take(6).Select(b => (b.ChampionId, (BanRec?)b)).ToList();
        int gone = 0;
        for (int i = 0; i < _banAdviceShown.Count && gone < BanAdviceGoneMax; i++)
        {
            var id = _banAdviceShown[i];
            if (!ours.Contains(id) || list.Any(x => x.Item1 == id)) continue;
            list.Insert(Math.Min(i, list.Count), (id, null));
            gone++;
        }
        _banAdviceShown = [.. list.Select(x => x.Item1)];
        return list;
    }

    /// <summary>
    /// Фаза банов после своего бана: вместо советов — баны обеих команд с
    /// бан-рейтом в патче. Сверху наши пять, снизу вражеские; тир-лист под ними
    /// остаётся.
    ///
    /// В рейтинге клиент прячет вражеские баны до конца таймера банов. До тех
    /// пор видна только наша половина — по центру панели; раскрылись — наша
    /// уезжает вверх, их выезжает снизу (PlaceBanHalves).
    /// </summary>
    private void RenderBansDone(DraftState draft)
    {
        PickHint.Visibility = Visibility.Collapsed;
        BansDoneTitle.Text  = Loc.T("ban.doneTitle", _engine!.Patch);

        // Панель только что появилась — всё на ней новое: баны «выпрыгивают»,
        // волна проигрывается заново, если половина уже полна.
        if (BansDonePanel.Visibility != Visibility.Visible) ResetBanPanel();

        // Баны встают сразу: наши — в синем кольце, их — в красном. Волны и
        // появление по очереди были, но глючили (владелец: «убери, пусть
        // появляются сразу все»); остался только сдвиг половин при раскрытии.
        SetBanSlots(BansOurList, draft.MyTeamBans, "#36D6E7", "…", ref _ourSlotsSig);
        SetBanSlots(BansTheirList, draft.TheirTeamBans, "#FF5A4D", "?", ref _theirSlotsSig);
        var theirCount = draft.TheirTeamBans.Count(id => id > 0);
        _theirBansShown = theirCount;
        PlaceBanHalves(theirCount > 0);

        RecScroll.Visibility     = Visibility.Collapsed;
        BanScroll.Visibility     = Visibility.Collapsed;
        BansDonePanel.Visibility = Visibility.Visible;
        PlanPanel.Visibility     = Visibility.Collapsed;
        PickBar.Visibility       = Visibility.Collapsed;
        _banHoverId = 0;                                   // банить больше нечего
        BanBar.Visibility        = Visibility.Collapsed;
        // Раскладка как у советов: панель по содержимому, тир-лист — остаток.
        CenterGrid.RowDefinitions[0].Height = GridLength.Auto;
        CenterGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        RolePoolBar.Visibility = Visibility.Collapsed;
        RenderTierList(draft);
        RenderTeams(draft);
    }

    /// <summary>
    /// Фаза банов кончилась, а вражеских банов панель ещё не показывала:
    /// клиент раскрывает их вместе с концом таймера банов, то есть уже первым
    /// снимком пиков (так видит владелец). Без задержки окно сразу переходило
    /// к пикам, и их половину никто бы не увидел. Панель остаётся на
    /// BanRevealHold, затем пики. Подбор за это время уже посчитан и ждёт в
    /// _lastRecs.
    /// </summary>
    private bool HoldBanReveal(DraftState? draft)
    {
        if (draft is not { InBanPhase: false, IsAram: false, MyBanDone: true }) return false;
        if (DateTime.UtcNow < _banRevealUntil) return true;
        var theirs = draft.TheirTeamBans.Count(id => id > 0);
        if (BansDonePanel.Visibility != Visibility.Visible || FullView.Visibility != Visibility.Visible
            || theirs == 0 || theirs <= _theirBansShown)
            return false;

        var hold = BanRevealHold;
        _banRevealUntil = DateTime.UtcNow + hold;
        var t = new DispatcherTimer { Interval = hold + TimeSpan.FromMilliseconds(50) };
        // Окно могли закрыть посреди показа (выход из программы): тогда
        // перерисовывать нечего, а Show на закрытом окне — исключение.
        t.Tick += (_, _) => { t.Stop(); if (PresentationSource.FromVisual(this) is not null) RenderCurrentState(); };
        t.Start();
        Log.Write($"баны врагов раскрыты с концом фазы банов ({theirs}) — показываю {hold.TotalSeconds:0.#} с");
        return true;
    }

    private static readonly TimeSpan BanRevealHold = TimeSpan.FromSeconds(4);

    // Сдвиги половин панели банов: создаются здесь, а не в разметке, — их
    // анимируем, и прошлая анимация не должна держать значение.
    private readonly TranslateTransform _ourShift = new(), _theirShift = new();

    private bool _theirRevealed;   // их половина выехала (или едет)

    // Откуда их половина поднимается на место при раскрытии, px.
    private const double BanSlideFrom = 28;

    /// <summary>
    /// Раскладка половин панели банов. Вражеские не известны — их половины не
    /// видно, наша сдвинута вниз ровно на середину места под обе: стоит по
    /// центру. Раскрылись — единственная анимация панели: наша поднимается на
    /// своё место, их пять разом выезжают снизу и проявляются (владелец:
    /// «пусть останется анимация сдвига»). Один раз на раскрытие: следующие
    /// события драфта переезд не перезапускают. Место под обе половины
    /// держится всегда: тир-лист под панелью не прыгает.
    ///
    /// Высоту их половины меряем сами: панель могла появиться этим же рендером,
    /// и раскладки ещё не было — ActualHeight был бы нулём.
    /// </summary>
    private void PlaceBanHalves(bool revealed)
    {
        BansOurHalf.RenderTransform   = _ourShift;
        BansTheirHalf.RenderTransform = _theirShift;
        BansTheirHalf.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var center = (BansTheirHalf.DesiredSize.Height + BansDoneSep.Height
                      + BansDoneSep.Margin.Top + BansDoneSep.Margin.Bottom) / 2;

        static void Set(IAnimatable o, DependencyProperty p, double v)
        {
            o.BeginAnimation(p, null);
            ((DependencyObject)o).SetValue(p, v);
        }

        if (!revealed)
        {
            _theirRevealed = false;
            Set(_ourShift, TranslateTransform.YProperty, center);
            Set(_theirShift, TranslateTransform.YProperty, BanSlideFrom);
            Set(BansTheirHalf, OpacityProperty, 0);
            Set(BansDoneSep, OpacityProperty, 0);
            return;
        }
        if (_theirRevealed) return;   // уже на месте или едет
        _theirRevealed = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _ourShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(center, 0, TimeSpan.FromSeconds(0.5)) { EasingFunction = ease });
        _theirShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(BanSlideFrom, 0, TimeSpan.FromSeconds(0.5))
            { BeginTime = TimeSpan.FromSeconds(0.12), EasingFunction = ease });
        // Стартовые значения — базой: до BeginTime анимация показывает базу.
        Set(BansTheirHalf, OpacityProperty, 0);
        Set(BansDoneSep, OpacityProperty, 0);
        BansTheirHalf.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)) { BeginTime = TimeSpan.FromSeconds(0.12) });
        BansDoneSep.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4)) { BeginTime = TimeSpan.FromSeconds(0.2) });
    }

    /// <summary>
    /// Панель банов команд с чистого листа: слоты не собраны, вражеских не
    /// показывали. Новый драфт, а не продолжение прошлого: драфт мог кончиться
    /// при панели на экране (дродж, «Сброс» в песочнице).
    /// </summary>
    private void ResetBanPanel()
    {
        _theirBansShown = 0;
        _theirRevealed = false;
        // null, а не подпись пустого списка: совпади она с подписью нового
        // драфта, слоты не пересобрались бы — держали бы баны прошлого.
        _ourSlotsSig = _theirSlotsSig = null;
    }

    private DateTime _banRevealUntil;

    private int _theirBansShown;   // сколько вражеских банов уже стояло на панели

    // Состав, с которым слоты уже собраны: тот же — не пересобираем. Окно
    // перерисовывается на каждом событии драфта (наведения, таймер).
    private string? _ourSlotsSig, _theirSlotsSig;

    // То же для рядов банов под заголовками колонок команд.
    private string? _ourChipsSig, _theirChipsSig;

    /// <summary>
    /// Баны команд под заголовками «Моя команда» и «Враги»: по пять маленьких
    /// портретов. Панель банов в центре уходит с началом пиков, а кого
    /// забанили, нужно видеть и там (просьба владельца, 27.10).
    ///
    /// Ряд стоит с фазы банов (с объявления) до конца драфта: появись он только
    /// на пиках, составы под ним прыгали бы вниз. В режимах без банов (вслепую,
    /// ARAM) ряда нет. Пока идут баны, пустые кружки — «…» у нас и «?» у них
    /// (клиент прячет их баны до конца таймера), потом — просто пустые: бан
    /// пропущен. Движка ещё нет (база грузится, заглушка центра) — бан-рейт
    /// взять неоткуда, ряда тоже нет.
    /// </summary>
    private void RenderTeamBans(DraftState draft)
    {
        var show = _engine is not null && !draft.IsAram
                   && (draft.InBanPhase || draft.MyBanDone
                       || draft.MyTeamBans.Count > 0 || draft.TheirTeamBans.Count > 0);
        MyTeamBansStrip.Visibility = EnemyTeamBansStrip.Visibility =
            show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        var banning = draft.InBanPhase;
        SetBanSlots(MyTeamBansStrip, draft.MyTeamBans, "#36D6E7", banning ? "…" : "", ref _ourChipsSig);
        SetBanSlots(EnemyTeamBansStrip, draft.TheirTeamBans, "#FF5A4D", banning ? "?" : "", ref _theirChipsSig);
    }

    /// Слоты команды в список, если состав изменился. Заглушка пустых — в
    /// подписи: у рядов под заголовками она меняется с концом банов при тех
    /// же банах.
    private void SetBanSlots(ItemsControl list, IReadOnlyList<int> bans, string frame,
                             string placeholder, ref string? sig)
    {
        var now = placeholder + "|" + string.Join(",", bans);
        if (now == sig && list.ItemsSource is not null) return;
        sig = now;
        list.ItemsSource = BanSlots(bans, frame, placeholder);
    }

    /// Пять слотов команды: сделанные баны по порядку, остальное — пустые
    /// кружки (placeholder: «…» — ещё банят, «?» — скрыто клиентом). frame —
    /// цвет кольца команды.
    private List<BanSlotVm> BanSlots(IReadOnlyList<int> bans, string frame, string placeholder)
    {
        var mains = RecommendationEngine.MyMains();
        var slots = bans.Where(id => id > 0).Select(id =>
        {
            var rate  = _engine!.BanRate(id);
            var games = mains.GetValueOrDefault(id);
            var tip   = Loc.T("ban.rateTip", DataDragon.Name(id), $"{rate:F1}", _engine.Patch);
            return new BanSlotVm
            {
                MainGames = games,
                Icon      = IconCache.Get(id),
                Name      = DataDragon.Name(id),
                Rate      = $"{rate:F1}%",
                RateBrush = BanRateBrush(rate),
                Frame     = frame,
                Tip       = games > 0 ? tip + "\n" + Loc.T("ban.yourMain", games) : tip,
            };
        }).ToList();
        while (slots.Count < 5)
            slots.Add(new BanSlotVm { Frame = "#3A4B5F", Placeholder = placeholder });
        return slots;
    }

    /// <summary>
    /// Подпись карточки бана: два первых довода, каждый с новой строки. Движок
    /// уже расставил их по важности. Раньше показывался только первый, и вторая
    /// причина — например, контра твоему пулу у того, кто ещё и бьёт союзника, —
    /// пропадала. Невидимый знак довода в начале строки — служебный, убираем.
    /// </summary>
    private static string BanReasonText(IReadOnlyList<string> reasons)
    {
        var lines = reasons.Take(2).Select(r =>
            r.Length > 0 && r[0] is RecommendationEngine.SIGN_GOOD or RecommendationEngine.SIGN_BAD
                                     or RecommendationEngine.SIGN_KEY ? r[1..] : r).ToList();
        return lines.Count > 1 ? string.Join("\n", lines.Select(l => "• " + l)) : lines.FirstOrDefault() ?? "";
    }
}
