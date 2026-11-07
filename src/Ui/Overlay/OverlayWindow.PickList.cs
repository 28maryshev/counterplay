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

// Окно драфта — список пиков: карточки кандидатов и доводы на них.
public partial class OverlayWindow
{
    // ── Полный вид ────────────────────────────────────────────────────────

    private void RenderFull(IReadOnlyList<Recommendation> recs, DraftState? draft)
    {
        // Связки союзников и их цвета — нужны и панели, и дашикам рекомендаций.
        var allyIds  = draft?.MyTeam.Where(p => p.EffectiveChampionId != 0)
                                    .Select(p => p.EffectiveChampionId).ToList() ?? [];
        var myCombos = draft != null ? DetectCombos(draft.MyTeam, ally: true) : [];
        var comboColorByName = new Dictionary<string, string>();
        for (int i = 0; i < myCombos.Count; i++)
            comboColorByName[myCombos[i].Name] = ComboColors[i % ComboColors.Length];
        _comboColors = comboColorByName;   // для подсветки связок при наведении
        _comboIndex  = myCombos.Select((c, i) => (c.Name, i))
                               .ToDictionary(x => x.Name, x => x.i);
        _allyIdsNow  = allyIds;

        // Лидер по каждому показателю среди показанных пиков: по нему полосы
        // масштабируются, а сам он — в золотой рамке (MetricRow).
        var metricMax = MetricRow.MaxOf(recs);

        // Союзники без меня — для расчёта контр-предметов состава.
        var allyNoMe = draft?.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0)
                                    .Select(p => p.EffectiveChampionId).ToList() ?? [];

        // Враги, уже показавшие чемпиона, — по ним видно, кто станет покупать
        // предметы, которыми нас «накажут».
        var enemyNow = draft?.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                       .Select(p => p.EffectiveChampionId).ToList() ?? [];

        // Что на этих врагах РЕАЛЬНО собирают. Класс говорит, кому предмет по
        // руке, а сборки — кто его берёт: Посох серафима магу по руке, но на
        // ЛеБлан и Зайру его не покупают, и показывать его там значит врать.
        var enemyRole = draft?.TheirTeam
            .Where(p => p.EffectiveChampionId != 0)
            .ToDictionary(p => p.EffectiveChampionId,
                          p => RecommendationEngine.LcuToDbRole(p.Position))
            ?? [];
        IReadOnlyCollection<int>? BuildOf(int champ)
        {
            var role = RunesClient.ResolveRole(champ, enemyRole.GetValueOrDefault(champ, ""));
            return role is null ? null : RunesClient.CachedItems(champ, role);
        }

        // Иконки карточки: от движка и окна (нет на аккаунте, контр-предметы,
        // не тот стиль). Какие из них общие — по общему подбору: у карточек
        // пула та же мерка, иначе одна иконка была бы яркой сверху и бледной ниже.
        var cfg = AppSettings.Current;
        List<PickIcon> AllIcons(Recommendation r)
        {
            var items = cfg.DraftItems
                ? ItemValue.CounterItems(r.ChampionId, allyNoMe, enemyNow, BuildOf).ToList() : [];
            var notOwned = _ownedChamps.Count > 0 && !_ownedChamps.Contains(r.ChampionId);
            return [.. r.Badges?.Icons ?? [],
                    .. WindowIcons(notOwned, items, ChampionTraits.ChampArch(r.ChampionId), _allyStyle)];
        }
        var commonIcons = CommonIcons([.. recs.Select(AllIcons)]);
        // Яркость тегов — от сильнейшего того же вида среди показанных (та же мерка у пула).
        var tagLeaders  = TagLeaders(recs);
        List<TagVm>  Tags(Recommendation r)  => cfg.DraftReasons ? TagsOf(r, tagLeaders) : [];
        List<IconVm> Icons(Recommendation r) => cfg.DraftReasons ? IconsOf(AllIcons(r), commonIcons) : [];

        // Пик из активного пула («ТВОЙ ПУЛ ПРОТИВ ВРАГОВ»): лучший из пула на мою
        // роль против врагов — отдельной СИНЕЙ карточкой сверху. Дуо-пул: рядом —
        // иконка чемпиона друга (кого назвать пикнуть).
        var poolCards = new List<FullRecCard>();
        var poolIds   = new HashSet<int>();

        // Кнопка «не советовать» — у всех карточек, кроме моего пика (его я
        // вижу всегда) и ARAM (там советы не из подбора по роли).
        var myNow = draft?.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;
        bool Dislikable(int id) => draft is { IsAram: false } && id != myNow;
        if (draft != null && _engine != null)
        {
            var myRoleDb = RecommendationEngine.LcuToDbRole(draft.MyPosition);
            if (!string.IsNullOrEmpty(myRoleDb))
            {
                var (mine, friend, isDuo) = PoolStore.ActiveForRole(myRoleDb);

                // Построение синей карточки пула (мой пик + опц. иконка друга для дуо).
                void AddPoolCard(Recommendation pr, string poolLabel, ImageSource? duoIcon, string duoName,
                                 bool fromDuo = false)
                {
                    var (pag, pac, pat) = ArchBadge(pr.ChampionId);
                    poolCards.Add(new FullRecCard
                    {
                        ChampionId = pr.ChampionId,
                        FromPool    = true,
                        FromDuoPool = fromDuo,
                        PoolLabel  = poolLabel,
                        Rank       = "★",
                        Name       = DataDragon.Name(pr.ChampionId),
                        Score      = Signed(pr.Score),
                        ScoreColor = pr.Score >= 0 ? "#6AB0FF" : "#E05050",
                        WinRate    = $"WR ~{50.0 + pr.BaseDelta:F1}%",
                        Icon       = IconCache.Get(pr.ChampionId),
                        Tags       = Tags(pr),
                        Icons      = Icons(pr),
                        Metrics    = MetricRow.Of(pr, metricMax, highlight: false),
                        ArchGlyph  = pag, ArchColor = pac, ArchTip = pat,
                        SynDashes  = SynDashesFor(pr.ChampionId, allyIds, comboColorByName),
                        DuoIcon    = duoIcon, DuoName = duoName,
                        NotOwned      = _ownedChamps.Count > 0 && !_ownedChamps.Contains(pr.ChampionId),
                        CanDislike    = Dislikable(pr.ChampionId),
                    });
                    poolIds.Add(pr.ChampionId);
                }

                if (isDuo)
                {
                    // Дуо-пул: пара «я + друг» как единое целое. Manual — фикс-пара,
                    // иначе автоподбор лучших пар (мой пул × пул друга).
                    var duo = PoolStore.ActiveDuo();

                    // Наигранность напарника — ИЗ КЭША, сразу и без сети: драфт
                    // начинается быстрее, чем отвечает сервер, а меняется она
                    // медленно (окно месяц). Свежее подтягивается фоном, как база.
                    // Нет ничего — его половина считается без личных факторов, как
                    // до обмена.
                    _engine.MateComfortByChamp = duo is null ? null : DuoShare.Cached(duo.FriendPuuid);

                    // Напарник УЖЕ взял чемпиона → пара определена. Ищем ЧЕЛОВЕКА
                    // из пати (см. Party), а не того, кто взял чемпиона из половины
                    // друга: у друга с широким пулом так опознавался случайный
                    // союзник. Заодно пара складывается и тогда, когда напарник взял
                    // что-то мимо своего пула — играем-то мы всё равно с ним.
                    var mateTaken = Party.MateChampion(draft, duo);

                    // Два условия, и они складываются. Человека нашли выше — по пати
                    // и puuid. Но сработать дуо-пул должен только когда напарник взял
                    // чемпиона ИЗ СВОЕЙ ПОЛОВИНЫ: связка задумана под конкретную пару,
                    // и подставлять её под случайный пик друга нечестно.
                    //
                    // Взял мимо половины (или напарника в драфте нет) — связки не
                    // показываем вовсе, идёт обычный подбор ниже. Моя половина при
                    // этом работает всегда: чемпионы из неё получают флор
                    // наигранности в движке, дуо-пул для этого срабатывать не обязан.
                    // Напарник взял чемпиона МИМО своей половины — связки не
                    // предлагаем вовсе: пара задумана под конкретных. Это НЕ то же
                    // самое, что «напарник ещё ничего не показал»: там связки как
                    // раз нужны, поэтому состояния разведены флагом, а не обнулением
                    // mateTaken, как было раньше.
                    var mateOffPool = mateTaken != 0 && duo != null && !duo.FriendHas(mateTaken);
                    if (mateOffPool)
                    {
                        // Один раз на чемпиона: метод зовётся на каждую перерисовку
                        // драфта, иначе журнал заполнится одной строкой.
                        if (_duoOffPool != mateTaken)
                        {
                            _duoOffPool = mateTaken;
                            Log.Write($"дуо «{duo!.FriendName}»: напарник взял "
                                      + $"{DataDragon.Name(mateTaken)} мимо своей половины — связки не предлагаем");
                        }
                        mateTaken = 0;
                    }
                    else if (mateTaken != 0) _duoOffPool = 0;

                    // Предлагаем, как только кончились баны, — не дожидаясь, пока
                    // напарник что-то покажет. Чаще всего он и не успевает: если он
                    // пикает после меня, к моему ходу у него пусто, и дуо-пул молчал
                    // именно тогда, когда подсказка нужнее всего — заранее, чтобы
                    // знать, что брать обоим. Показал или взял — подбираем под его
                    // чемпиона, как и раньше.
                    if (duo != null && !mateOffPool)
                    {
                        // На какой линии напарник СЕЙЧАС — из драфта, а не из того,
                        // под какую линию чемпион положен в его половину. Иначе
                        // человеку на миде предлагали чемпиона из его джангл-набора
                        // (жалоба на 1.3.54). Пусто — роль клиент не раскрыл,
                        // работаем по всей половине, как раньше.
                        var mateRoleDb = Party.MateRole(draft, duo);

                        if (duo.Manual)
                        {
                            // Фиксированные связки: мой пик из связки, где вторая
                            // половина — чемпион напарника. Пока он ничего не показал,
                            // идут все связки под мою роль, и каждая карточка несёт
                            // СВОЙ чемпион напарника — это и есть «что брать обоим».
                            var manual = new List<(Recommendation R, int Mate)>();
                            foreach (var mp in duo.ManualPairs)
                            {
                                int mineId, mateId;
                                if (mp.Mine != 0 && RoleFits(mp.MineRole, myRoleDb))
                                    (mineId, mateId) = (mp.Mine, mp.Friend);
                                else if (mp.Friend != 0 && RoleFits(mp.FriendRole, myRoleDb))
                                    (mineId, mateId) = (mp.Friend, mp.Mine);
                                else
                                    continue;
                                if (mateTaken != 0 && mateId != mateTaken) continue;   // только связки с ним
                                // Связка написана под его линию, а стоит он на другой —
                                // пара не состоится, предлагать её нечестно. Отсекаем,
                                // только когда известны ОБЕ роли: нераскрытая роль
                                // напарника не повод прятать связку.
                                if (mateTaken == 0 && mateRoleDb.Length > 0
                                    && mp.FriendRole.Length > 0 && mp.FriendRole != mateRoleDb) continue;

                                if (_engine.Recommend(draft, 1, new[] { mineId }).FirstOrDefault() is { } mr)
                                    manual.Add((mr, mateId));
                            }
                            // Не советовать — и в связках: игрок убрал чемпиона с карточки.
                            var ordered = Dislikes.Apply(manual.OrderByDescending(x => x.R.Score),
                                                         x => x.R.ChampionId, Dislikes.Kind.Pick, myRoleDb, keep: myNow);
                            var shown   = ordered.Where(x => x.R.Score > 0).Take(3).ToList();
                            if (shown.Count == 0 && ordered.Count > 0) shown = [ordered[0]];
                            foreach (var (mr, pairMate) in shown)
                                AddPoolCard(mr, Loc.T("pool.duoLabel"),
                                            pairMate != 0 ? IconCache.Get(pairMate) : null,
                                            pairMate == 0 ? ""
                                              : mateTaken != 0 ? DataDragon.Name(pairMate)
                                              : Loc.T("pool.duoSuggest", DataDragon.Name(pairMate)),
                                            fromDuo: true);
                        }
                        else
                        {
                            // Авто: свой пик из своей половины. Напарник взял или
                            // показал — синергию с ним движок уже учёл. Молчит —
                            // подбираем пару САМИ: карточка обязана отвечать «что
                            // брать обоим», иначе предлагать заранее незачем.
                            // Силу его половины в этом драфте считаем ОДИН раз: она
                            // зависит от врагов и союзников, а не от того, какой
                            // мой кандидат сейчас на карточке. Пара со мной
                            // добавляется уже поверх, на каждого кандидата.
                            var mateScores = mateTaken == 0
                                ? _engine.PartnerScores(draft, duo.Friend, mateRoleDb)
                                : null;
                            foreach (var pr in _engine.TopFromPool(draft, duo.MineForRole(myRoleDb), 3))
                            {
                                var suggested = mateScores is null ? 0
                                    : _engine.BestPartner(draft, pr.ChampionId, myRoleDb,
                                                          duo.Friend, mateScores, mateRoleDb);
                                var shownMate = mateTaken != 0 ? mateTaken : suggested;
                                AddPoolCard(pr, Loc.T("pool.duoLabel"),
                                            shownMate != 0 ? IconCache.Get(shownMate) : null,
                                            shownMate == 0 ? ""
                                              : suggested != 0 ? Loc.T("pool.duoSuggest", DataDragon.Name(shownMate))
                                              : DataDragon.Name(shownMate),
                                            fromDuo: true);
                            }
                        }
                    }
                }
                else if (mine.Count > 0)
                {
                    // Обычный пул: до 3 выгодных из пула (иначе один лучший).
                    foreach (var pr in _engine.TopFromPool(draft, mine, 3))
                        AddPoolCard(pr, Loc.T("pool.slotLabel"), null, "");
                }
            }
        }

        // Свой пик — всегда в списке, даже если его нет среди кандидатов.
        //
        // Общий подбор отсеивает офф-мета связки (Там Кенч на боте и подобные):
        // у них мало игр на роли, и в кандидаты они не проходят. Но человек уже
        // ВЗЯЛ такого чемпиона — и оставался без единой цифры: ни матчапа против
        // оппонента, ни синергии с командой. Считаем его отдельно, тем же
        // движком через only-список: там порог кандидатов не применяется.
        var myPickId = draft?.MyTeam.FirstOrDefault(p => p.IsLocalPlayer)?.EffectiveChampionId ?? 0;
        if (myPickId != 0 && draft != null && _engine != null
            && !poolIds.Contains(myPickId) && recs.All(r => r.ChampionId != myPickId))
        {
            try
            {
                if (_engine.Recommend(draft, 1, [myPickId]).FirstOrDefault() is { } mineRec)
                    recs = [mineRec, .. recs];
            }
            catch { /* нет данных по связке — покажем список как есть */ }
        }

        var generalCards = recs
            .Where(r => !poolIds.Contains(r.ChampionId))
            // Нет на аккаунте — можно не показывать вовсе: список станет короче,
            // но в нём останутся только те, кого реально можно взять.
            .Where(r => AppSettings.Current.DraftUnowned
                        || _ownedChamps.Count == 0 || _ownedChamps.Contains(r.ChampionId))
            .Select(r =>
        {
            var (ag, ac, at) = ArchBadge(r.ChampionId);
            var candArch = ChampionTraits.ChampArch(r.ChampionId);
            return new FullRecCard
            {
                ChampionId = r.ChampionId,
                Rank       = $"{r.Rank}.",
                IsMyPick   = r.IsMyPick,
                IsSelected = r.ChampionId == _pickHoverId,
                MineLabel  = Loc.T("rec.yourPick"),
                Name       = DataDragon.Name(r.ChampionId),
                Score      = Signed(r.Score),
                ScoreColor = r.Score >= 0 ? "#C89B3C" : "#E05050",
                WinRate    = $"WR ~{50.0 + r.BaseDelta:F1}%",
                Icon       = IconCache.Get(r.ChampionId),
                Tags       = Tags(r),
                Icons      = Icons(r),
                MetricsVisibility = cfg.DraftMetrics ? Visibility.Visible : Visibility.Collapsed,
                Metrics    = MetricRow.Of(r, metricMax, highlight: true),
                ArchGlyph  = cfg.DraftArch ? ag : "",
                ArchColor  = ac,
                ArchTip    = at,
                ArchStrong = _allyStyle != null && candArch == _allyStyle,
                ArchDim    = _allyStyle != null && candArch != _allyStyle,
                SynDashes  = SynDashesFor(r.ChampionId, allyIds, comboColorByName),
                // Чемпиона нет на аккаунте (только если владение вообще известно).
                NotOwned      = _ownedChamps.Count > 0 && !_ownedChamps.Contains(r.ChampionId),
                CanDislike    = Dislikable(r.ChampionId),
            };
        }).ToList();

        var allCards = new List<FullRecCard>();
        allCards.AddRange(poolCards);
        allCards.AddRange(generalCards);

        // Выбранный чемпион — первым. Его показатели смотрят иначе, чем чужие:
        // не «кого взять», а «что у меня против них и с ними», и искать свою
        // карточку в середине списка неудобно. Ищем по id, а не по пометке
        // IsMyPick: если чемпион пришёл из пула, пометки на карточке нет.
        if (myPickId != 0)
        {
            var mineIdx = allCards.FindIndex(c => c.ChampionId == myPickId);
            if (mineIdx > 0)
            {
                var mineCard = allCards[mineIdx];
                allCards.RemoveAt(mineIdx);
                allCards.Insert(0, mineCard);
            }
        }
        FullRecList.ItemsSource = allCards;
        if (draft is { IsAram: false }) ShowHiddenStrip(PickHiddenStrip, Dislikes.Kind.Pick);
        else PickHiddenStrip.Visibility = Visibility.Collapsed;

        ApplyScale();                                // масштаб под текущую ширину
        RecScroll.Visibility = Visibility.Visible;   // показываем пики
        BanScroll.Visibility = Visibility.Collapsed;
        BansDonePanel.Visibility = Visibility.Collapsed;
        PlanPanel.Visibility = Visibility.Collapsed;
        BanBar.Visibility      = Visibility.Collapsed;  // бан-плашка — только в банфазе
        TierListBar.Visibility = Visibility.Collapsed;  // тир-лист — только под банами
        // Пока рун нет (чемпион не выбран) — показываем пул роли в той же строке.
        if (_runesWaiting && _runeStats is { Keystones.Count: > 0 } && AppSettings.Current.DraftRunes)
            RunesBar.Visibility = Visibility.Visible;   // руны пришли, пока держалась панель банов
        _runesWaiting = false;
        if (RunesBar.Visibility == Visibility.Visible) RolePoolBar.Visibility = Visibility.Collapsed;
        else RenderRolePool(draft);
        // Пики заполняют список (звёздная строка), руны — по контенту (Auto).
        CenterGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
        CenterGrid.RowDefinitions[1].Height = GridLength.Auto;
        // Сбрасываем наведённого, только если его уже забанили/взяли — а не
        // просто «выпал из рекомендаций» (иначе гасили бы свой же выбор).
        if (_pickHoverId > 0 && draft != null && !StillAvailable(draft, _pickHoverId))
            _pickHoverId = 0;
        UpdatePickBar();
        if (draft != null) RenderTeams(draft);
    }
}
