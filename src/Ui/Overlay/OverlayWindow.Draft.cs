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

// Окно драфта — вход драфта из цикла клиента и выбор, что показывать (пики, баны,
// объявление; полный или компактный вид).
public partial class OverlayWindow
{
    // ── Вызывается из фонового потока (цикл событий LCU) ──────────────────
    // ВАЖНО: только InvokeAsync (неблокирующий). Блокирующий Invoke остановил бы
    // поток чтения WebSocket; при лавине событий (переход в игру) сокет LCU
    // перестал бы вычитываться, буфер клиента переполнялся бы и клиент зависал
    // («reconnect», игра не стартует). Никогда не делать здесь блокирующий вызов.

    /// <summary>
    /// Драфт кончился: ушли в игру, дроджнули или кто-то вышел из очереди.
    ///
    /// Раньше здесь просто показывали «ждём следующий драфт», и окно на этом
    /// застревало: при дродже клиент сообщает о новой фазе РАНЬШЕ, чем удаляет
    /// чемп-селект, — экран готовности успевал появиться, а потом его затирал
    /// статус. Человек видел «ждём драфт», пока клиент уже искал игру.
    ///
    /// Заодно гасим плашку «пикаешь раньше врагов»: она про конкретный драфт и
    /// живёт в шапке, поэтому смена вида её не убирала и она оставалась висеть.
    /// </summary>
    public void DraftEnded() =>
        Dispatcher.InvokeAsync(() =>
        {
            PickHint.Visibility = Visibility.Collapsed;
            StopPickTimer();       // и отсчёт моего хода с рамкой центра
            StopDamageWarnings();  // и мигание перекоса урона: полоса скрыта, а мигала бы
            _tierModeNow = null;   // следующий драфт — снова с вида из настроек
            _banAdviceShown = [];  // советы банов — свои у каждого драфта
            // Выбранный кликом — тоже: иначе новый драфт начинался бы с золотой
            // рамки на чемпионе прошлого.
            _pickHoverId = 0;
            _banHoverId = 0;
            // Панель банов команд — с чистого листа: «свежесть» мерится по её
            // видимости, а кончиться драфт мог и при ней на экране.
            BansDonePanel.Visibility = Visibility.Collapsed;
            ResetBanPanel();

            var phase = _readyPhaseRaw ?? _phaseRaw;
            var inGame = phase is "GameStart" or "InProgress" or "Reconnect";
            if (!string.IsNullOrEmpty(phase) && !inGame)
            {
                ShowReadyPhase(phase);
                return;
            }
            IdleStatusText.Text = Loc.T("status.waitNextDraft");
            DlBar.Visibility = Visibility.Collapsed;
            PulseAnim(false);
            SetLoadingMode();
            ShowIdle();
        });

    public void UpdateRecommendations(
        IReadOnlyList<Recommendation>? recs, DraftState? draft,
        RecommendationEngine? engine = null)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if ((draft is null) != (_lastDraft is null))
                Log.Write(draft is null ? "драфт закончился"
                    : $"драфт начался: роль {(draft.MyPosition is "" or null ? "?" : draft.MyPosition)}, " +
                      $"свои {draft.MyTeam.Count(x => x.ChampionId > 0)}/{draft.MyTeam.Count}, " +
                      $"враги {draft.TheirTeam.Count(x => x.ChampionId > 0)}/{draft.TheirTeam.Count}, " +
                      $"{(draft.InBanPhase ? "фаза банов" : "фаза пиков")}" +
                      $"{(draft.IsAram ? ", ARAM" : "")}, кандидатов {recs?.Count ?? 0}");
            UseEngine(engine);
            // Подбор выключен настройкой: в драфте прячемся в трей и ничего не
            // считаем. Программа остаётся тем, ради чего её оставили, —
            // информационной панелью между играми.
            if (!AppSettings.Current.DraftEnabled && draft is not null)
            {
                _lastDraft = null; _lastRawDraft = null; _lastRecs = null;
                _draftSuppressed = true;    // фолловеру: не возвращать окно до конца драфта
                if (!_inTray) HideToTray();
                return;
            }
            if (draft is null)
            {
                // Драфт кончился — неважно, дошёл он до игры или кто-то сдоджил
                // на середине. Положение запоминаем здесь: окно ещё стоит там,
                // где его оставили, а до вида сайдбара дело дойдёт следующим шагом.
                RememberDraftPlacement();
                StopPickTimer();               // додж в мой ход — отсчёт не переживает драфт
                StopDamageWarnings();          // и мигание перекоса урона
                _enemyRoleOverrides.Clear();   // конец драфта — сброс меток
                _draftSuppressed = false;      // …и запрет на авто-показ снят
                _placementApplied = false;     // следующий драфт снова расставит окно
            }
            _lastRawDraft = draft;
            var eff = draft != null ? ApplyEnemyRoleOverrides(draft) : null;
            _lastDraft = eff;
            if (eff is not null) EnterDraftLayout(eff);
            // Авто-раскладка ролей врагов + ручные метки меняют оппонента и
            // кросс-пары — пересчитываем подбор по эффективному драфту.
            if (recs != null && eff != null && _engine != null && !ReferenceEquals(eff, draft))
                recs = eff.IsAram
                    ? _engine.RecommendAram(eff, AppSettings.Current.DraftCount)
                    : _engine.Recommend(eff, AppSettings.Current.DraftCount);
            _lastRecs  = recs;
            _lastBans  = null;
            RenderCurrentState();
        });
    }

    /// hovers — наведения своих за драфт, с которыми посчитаны bans (копия:
    /// окно пересчитывает по ним советы после «не советовать»).
    public void UpdateBans(IReadOnlyList<BanRec>? bans, DraftState? draft,
                           RecommendationEngine? engine = null,
                           IReadOnlyDictionary<int, HashSet<int>>? hovers = null) =>
        Dispatcher.InvokeAsync(() =>
        {
            UseEngine(engine);   // нужен тир-листу (банфаза идёт до пиков)

            // Подбор выключен настройкой — банфаза тоже не наша: прячемся в трей.
            // Раньше проверка стояла только у пиков, и оверлей всё равно всплывал
            // на банах.
            if (!AppSettings.Current.DraftEnabled && draft is not null)
            {
                _lastDraft = null; _lastRawDraft = null; _lastBans = null;
                _draftSuppressed = true;
                if (!_inTray) HideToTray();
                return;
            }

            _lastBans  = bans;
            _lastRecs  = null;
            _lastHoverHistory = hovers;
            _lastRawDraft = draft;
            _lastDraft = draft != null ? ApplyEnemyRoleOverrides(draft) : null;

            // Банфаза начинается раньше пиков — раскладку окна ставим уже здесь,
            // иначе она применялась бы только с первым пиком.
            if (draft is null)
            {
                RememberDraftPlacement();      // тот же случай, но из банфазы
                _draftSuppressed = false;
                _placementApplied = false;
            }
            else EnterDraftLayout(_lastDraft!);

            RenderCurrentState();
        });

    // ── Рендер ────────────────────────────────────────────────────────────

    /// Окончательные пики: чемпион есть и ход кончился — то же правило, что у
    /// слежки за метаниями союзников (в свой ход наведённый уже стоит в
    /// championId). Драфт закончен, когда взяли все свои и пятеро врагов.
    /// Общее для подбора предметов и рун: оба ждут конца драфта и показывают
    /// одну шкалу «сколько игроков из десяти выбрали».
    private (int Locked, int Total, bool Done) DraftProgress()
    {
        if (_lastDraft is not { } d) return (0, 10, false);
        var mine = d.MyTeam.Count(p => AllyWaver.Locked(d, p));
        var theirs = d.TheirTeam.Count(p => AllyWaver.Locked(d, p));
        var team = Math.Max(d.MyTeam.Count, 1);
        return (mine + Math.Min(theirs, 5), team + 5, theirs >= 5 && mine >= team);
    }

    /// Окончательные пики обеих команд одной строкой: меняется на каждый лок
    /// (и на обмен чемпионами), но не на наведение.
    private string LockedPicksSig()
    {
        if (_lastDraft is not { } d) return "";
        return string.Join(",", d.MyTeam.Concat(d.TheirTeam)
            .Where(p => AllyWaver.Locked(d, p)).Select(p => $"{p.CellId}:{p.ChampionId}"));
    }

    // Пики, под которые уже посчитана строка подбора предметов. Нужны, чтобы
    // пересчитывать её на чужой пик, а не на каждое событие драфта.
    private string _buildsDraftSig = "";

    /// Пересчитать сборки, если кто-то из десяти окончательно выбрал.
    ///
    /// Подбор считается при отрисовке панели сборок, а её перерисовывает ховер
    /// или выбор строки — но не чужой пик. Из-за этого подбор, готовый уже на
    /// пятом враге, появлялся только когда игрок трогал панель: у одного это
    /// случилось за восемь секунд до начала матча. Свои — тоже: до 21.11 здесь
    /// смотрели только на врагов, и на красной стороне пик нашего пятого
    /// подбор не пересчитывал.
    private void RefreshBuildsForDraft()
    {
        if (_runeStats is null || RunesBar.Visibility != Visibility.Visible) return;

        var sig = LockedPicksSig();
        if (sig == _buildsDraftSig) return;

        _buildsDraftSig = sig;
        RenderBuilds(_runeStats);
    }

    private void RenderCurrentState()
    {
        var recs  = _lastRecs;
        var draft = _lastDraft;

        // Заглушку центра и вид «без роли» ставят только свои ветки; любой
        // другой вид начинается без них — снимаем здесь один раз, а не в каждом.
        CenterSkeleton.Visibility = Visibility.Collapsed;
        _blindView = false;

        // Плашки подтверждения пересчитываем на каждом снимке драфта, а не
        // только в ответ на клик по нашей карточке.
        //
        // Раньше их показывал ровно один путь — клик. А кнопка требует, чтобы
        // ход уже наступил. Кто выбирал чемпиона заранее (нормальное поведение:
        // навёл, посмотрел руны, ждёшь очереди) попадал в ловушку: на момент
        // клика хода ещё нет — плашки нет; ход наступает — а перерисовать её
        // некому. Снаружи это и есть «кнопка не работает»: жмёшь туда, где её
        // нет. Помогало только повторное нажатие по карточке уже в свой ход.
        UpdatePickBar();
        UpdateBanBar();
        // Полоса фраз — до раскладки центра: виды по содержимому берут из неё место.
        UpdatePhraseBar();

        // Кто-то взял чемпиона — пересчитываем подбор сразу, не дожидаясь, пока
        // игрок наведёт мышь на панель.
        RefreshBuildsForDraft();
        RefreshRunesForDraft();

        // Фаза банов: показываем рекомендуемые баны (в том же разделе, что и пики).
        // Свой бан сделан — советы уступают место банам обеих команд. Вражеские
        // клиент раскрывает только с концом таймера банов — уже на пиках, — и
        // панель держится ещё BanRevealHold, чтобы их увидеть.
        if (draft?.InBanPhase == true || HoldBanReveal(draft))
        {
            // Фаза объявления: советов банов ещё нет — мои чемпионы на роли.
            if (draft!.InPlanning && _engine is not null)
            {
                RestoreModeSize();
                if (_isFullMode)
                {
                    RenderPlanFull(draft);
                    CompactScroll.Visibility = Visibility.Collapsed;
                    FullView.Visibility      = Visibility.Visible;
                }
                else
                {
                    RenderPlanCompact(draft);
                    FullView.Visibility      = Visibility.Collapsed;
                    CompactScroll.Visibility = Visibility.Visible;
                }
                if (_inTray) return;
                Show();
                AnchorIfNotMoved();
                return;
            }

            var banDone = draft.MyBanDone && _engine is not null;
            if (!banDone && (_lastBans is null || _lastBans.Count == 0))
            {
                ShowDraftSkeleton(draft);
                return;
            }
            RestoreModeSize();
            if (banDone && _isFullMode)
            {
                RenderBansDone(draft);
                CompactScroll.Visibility = Visibility.Collapsed;
                FullView.Visibility      = Visibility.Visible;
            }
            else if (_isFullMode)
            {
                RenderBansFull(_lastBans ?? [], draft);
                CompactScroll.Visibility = Visibility.Collapsed;
                FullView.Visibility      = Visibility.Visible;
            }
            else
            {
                RenderBansCompact(_lastBans ?? [], draft);
                FullView.Visibility      = Visibility.Collapsed;
                CompactScroll.Visibility = Visibility.Visible;
            }
            if (_inTray) return;
            Show();
            AnchorIfNotMoved();
            return;
        }

        if (recs == null || recs.Count == 0)
        {
            if (draft is not null)
            {
                // Без роли подбора не будет до конца драфта — там мой пул; база
                // ещё не загружена или роль вот-вот придёт — заглушка.
                if (IsBlind(draft) && _engine is not null) ShowBlindPool(draft);
                else ShowDraftSkeleton(draft);
                return;
            }
            // Плашка про порядок пика живёт в шапке, вне переключаемых видов, —
            // без этого она оставалась висеть над экраном ожидания.
            PickHint.Visibility = Visibility.Collapsed;
            IdleStatusText.Text = Loc.T("status.waitDraft");
            ShowIdle();
            return;
        }

        RestoreModeSize();

        // Подсказка по порядку пика: пикаешь раньше врагов → риск контрпика.
        // В ARAM порядка пика нет. Строки «Роль: … — контрпик + синергия» над
        // ней больше нет (просьба владельца, 28.10): роль видна на моём слоте,
        // у панелей в центре свои заголовки.
        if (draft is { IsAram: false, ExposedToCounter: true })
        {
            PickHint.Text = Loc.T("draft.pickHint");
            PickHint.Visibility = Visibility.Visible;
        }
        else
        {
            PickHint.Visibility = Visibility.Collapsed;
        }

        if (_isFullMode)
        {
            RenderFull(recs, draft);
            CompactScroll.Visibility = Visibility.Collapsed;
            FullView.Visibility      = Visibility.Visible;
        }
        else
        {
            RenderCompact(recs);
            FullView.Visibility      = Visibility.Collapsed;
            CompactScroll.Visibility = Visibility.Visible;
        }

        if (_inTray) return; // во время игры окно скрыто в трее
        Show();
        AnchorIfNotMoved();
        KeepReachable();
        ApplyScale();
    }

    /// <summary>
    /// Драфт идёт, а центру показать нечего: советы банов ещё не посчитаны,
    /// подбор пуст (роль не раздана, скамейки ARAM ещё нет) или база не
    /// загружена. Окно остаётся видом драфта — команды по бокам, в центре две
    /// плашки-заглушки на месте советов и тир-листа.
    ///
    /// Раньше здесь звали экран ожидания, а он ужимает окно до ширины панели
    /// профиля. Кончался такой драфт — запоминалась раскладка 340×784, и каждый
    /// следующий открывался окном, куда влезает одна колонка своих: центр и
    /// враги уезжали за край (скриншот владельца, 15.10).
    /// </summary>
    private void ShowDraftSkeleton(DraftState draft)
    {
        RestoreModeSize();
        PutDraftSkeleton(draft);
        ShowDraftView();
    }

    /// Заглушка драфта: составы команд и мерцающий центр, без размера окна и
    /// без показа — их делают вызывающие (ShowDraftSkeleton, EnterDraftLayout).
    private void PutDraftSkeleton(DraftState draft)
    {
        PickHint.Visibility = Visibility.Collapsed;

        // Руны, если уже стояли, вернёт подбор (RenderFull) — как после банов.
        if (RunesBar.Visibility == Visibility.Visible) _runesWaiting = true;
        foreach (var part in new UIElement[] { RecScroll, BanScroll, BansDonePanel, PlanPanel,
                                               PickBar, BanBar, RolePoolBar, TierListBar, RunesBar })
            part.Visibility = Visibility.Collapsed;
        CenterSkelTop.Background = CenterSkelBottom.Background = Skeleton.Shimmer;
        CenterSkeleton.Visibility = Visibility.Visible;
        RenderTeams(draft);
    }

    /// Вид драфта на экран (компактный не используется); в трее — только отрисовать.
    private void ShowDraftView()
    {
        CompactScroll.Visibility = Visibility.Collapsed;
        FullView.Visibility      = Visibility.Visible;
        if (_inTray) return;
        Show();
        AnchorIfNotMoved();
    }

    // ── Фаза банов ─────────────────────────────────────────────────────────

    private void RenderBansCompact(IReadOnlyList<BanRec> bans, DraftState draft)
    {
        PickHint.Visibility = Visibility.Collapsed;

        RecList.ItemsSource = bans.Take(6).Select((b, i) => new RecCard
        {
            Rank       = $"{i + 1}.",
            Name       = DataDragon.Name(b.ChampionId),
            Score      = Loc.T("badge.ban"),
            ScoreColor = "#E0584F",
            Reason     = b.Reasons.FirstOrDefault() ?? "",
            Icon       = IconCache.Get(b.ChampionId),
        }).ToList();
    }

    // ── Компактный вид ────────────────────────────────────────────────────

    private void RenderCompact(IReadOnlyList<Recommendation> recs)
    {
        RecList.ItemsSource = recs.Take(4).Select(r => new RecCard
        {
            Rank       = $"{r.Rank}.",
            Name       = (r.IsMyPick ? "★ " : "") + DataDragon.Name(r.ChampionId),
            Score      = Signed(r.Score),
            ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
            Reason     = r.Reasons.FirstOrDefault() ?? "",
            Icon       = IconCache.Get(r.ChampionId),
        }).ToList();
    }
}
