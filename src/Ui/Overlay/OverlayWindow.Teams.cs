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

// Окно драфта — составы по бокам: слоты, стиль, связки и линии, баланс урона, роли врагов.
public partial class OverlayWindow
{
    // Ручное назначение ролей врагам (cellId → LCU-позиция): игрок знает, куда
    // пойдёт флекс-пик (Ирелия мид и т.п.) — клик по роли в карточке врага.
    private readonly Dictionary<int, string> _enemyRoleOverrides = new();

    private static readonly string[] RoleCycle = ["", "top", "jungle", "middle", "bottom", "utility"];

    // Линейный график динамики винрейта (синий) по датам.
    /// Баланс урона команды: складываем доли физ/маг/чистого по взятым чемпионам
    /// и нормируем в проценты. Источник — ТОЛЬКО замеры по матчам (champion_damage).
    /// Оценки Data Dragon (info.attack/magic) сюда не годятся: это произвольная
    /// шкала 0..10, а не доли урона — у Кияны там attack=0 при чисто физическом
    /// уроне, и команда из двух АД-чемпионов показывалась бы как AP 64%.
    /// Пока замеров в базе нет — полосу не показываем вовсе.
    private (double Ad, double Ap, double Tru) DamageMix(IReadOnlyList<int> champIds)
    {
        double ad = 0, ap = 0, tru = 0;
        foreach (var id in champIds)
            if (_engine?.DamageShare(id) is { } s)
            { ad += s.Phys; ap += s.Magic; tru += s.True; }

        var sum = ad + ap + tru;
        return sum > 0 ? (ad / sum * 100, ap / sum * 100, tru / sum * 100) : (0, 0, 0);
    }

    // Перекос урона: с какой доли считаем его опасным и сколько чемпионов должно
    // быть взято, чтобы вообще судить.
    //
    // Раньше трёх пиков смысла нет: один маг в составе из двух — это 100% магии,
    // а команда ещё даже не сложилась. К трём картина уже показательная, и время
    // что-то поправить своим пиком ещё есть.
    //
    // 70% — та граница, за которой соперник закрывается ОДНИМ сопротивлением:
    // покупает мантию или кольчугу и обесценивает почти весь урон команды.
    private const int    DMG_WARN_PICKS = 3;

    private const double DMG_WARN_SHARE = 70.0;

    /// Мигание полосы урона. Включаем и выключаем ровно один раз на переход:
    /// перезапуск анимации на каждой перерисовке сбивал бы её фазу, и полоса
    /// дёргалась бы вместо ровного пульса.
    private void SetDamageWarning(Border bar, TextBlock warn, string? text)
    {
        var on = text is not null;
        // Состояние читаем с самой подписи: полос теперь две, и общий флаг на обе
        // гасил мигание у второй.
        if (on == (warn.Visibility == Visibility.Visible) && warn.Text == (text ?? "")) return;

        warn.Text = text ?? "";
        warn.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        if (on)
        {
            // Мигание должно ловиться боковым зрением: человек смотрит на список
            // рекомендаций, а не на полосу. Прежние 0,3 и 0,7 с читались как
            // «что-то там дышит» — провал глубже и ритм быстрее.
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 1.0, To = 0.12, Duration = TimeSpan.FromSeconds(0.45),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
            // Подпись мигает вместе с полосой: в противофазе это рябило бы.
            warn.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 1.0, To = 0.25, Duration = TimeSpan.FromSeconds(0.45),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            bar.BeginAnimation(OpacityProperty, null);
            bar.Opacity = 1.0;
            warn.BeginAnimation(OpacityProperty, null);
            warn.Opacity = 1.0;
        }
    }

    private void RenderDamageMix(IReadOnlyList<int> champIds, UIElement panel,
        ColumnDefinition adCol, ColumnDefinition apCol, ColumnDefinition truCol,
        TextBlock adText, TextBlock apText, TextBlock truText,
        Border? bar = null, TextBlock? warn = null, bool ally = true)
    {
        var (ad, ap, tru) = DamageMix(champIds);
        if (ad + ap + tru <= 0 || !AppSettings.Current.DraftDamage)
        {
            // Полосу убрали — гасим и мигание: иначе анимация продолжала бы идти
            // на невидимом элементе и вернулась бы вместе с ним не вовремя.
            if (bar is not null && warn is not null) SetDamageWarning(bar, warn, null);
            panel.Visibility = Visibility.Collapsed;
            return;
        }
        panel.Visibility = Visibility.Visible;

        // Доли — звёздочными ширинами: полоса сама тянется по ширине колонки.
        adCol.Width  = new GridLength(ad,  GridUnitType.Star);
        apCol.Width  = new GridLength(ap,  GridUnitType.Star);
        truCol.Width = new GridLength(tru, GridUnitType.Star);

        adText.Text  = $"AD {ad:0}%";
        apText.Text  = $"AP {ap:0}%";
        truText.Text = Loc.T("team.trueDmg", $"{tru:0}");
        // Чистый урон мал/отсутствует — не занимаем место подписью.
        truText.Visibility = tru >= 0.5 ? Visibility.Visible : Visibility.Collapsed;

        // Перекос важен у обеих команд, но вывод из него разный.
        //
        // У своей пишем, чего НЕ ХВАТАЕТ: на этапе драфта это единственное, что
        // человек может исправить своим пиком. У врагов — чем ЗАКРЫТЬСЯ: состав,
        // бьющий одним типом урона, останавливается одним предметом.
        if (bar is not null && warn is not null)
        {
            string? text = null;
            if (champIds.Count >= DMG_WARN_PICKS)
            {
                if (ad >= DMG_WARN_SHARE)
                    text = Loc.T(ally ? "team.needAp" : "team.enemyAd");
                else if (ap >= DMG_WARN_SHARE)
                    text = Loc.T(ally ? "team.needAd" : "team.enemyAp");
            }
            SetDamageWarning(bar, warn, text);
        }
    }

    // ── Роли врагов: авто-раскладка + ручные метки ────────────────────────

    // Раскладывает роли по вражеской команде: ручные метки фиксированы, остальным
    // чемпионам роли назначаются жадно по доле их игр на роли (каждая роль — один
    // раз). Итог: у Ирелии «топ», но стоит вручную поставить Чо'Гата на топ — она
    // автоматически съезжает на свою следующую лучшую свободную роль (мид).
    private DraftState ApplyEnemyRoleOverrides(DraftState d)
    {
        if (d.IsAram) return d; // в ARAM ролей нет
        var their = d.TheirTeam.ToArray();
        var usedRoles = new HashSet<string>();

        // 1. Ручные метки — фиксированы.
        for (int i = 0; i < their.Length; i++)
            if (_enemyRoleOverrides.TryGetValue(their[i].CellId, out var pos) && pos.Length > 0)
            {
                their[i] = their[i] with { Position = pos };
                usedRoles.Add(pos);
            }

        // 2. Остальные с чемпионами — жадное назначение по доле роли.
        if (_engine != null)
        {
            var freeIdx = Enumerable.Range(0, their.Length)
                .Where(i => !_enemyRoleOverrides.ContainsKey(their[i].CellId)
                            && their[i].EffectiveChampionId != 0)
                .ToList();
            var pairs = new List<(int Idx, string Role, double Share)>();
            foreach (var i in freeIdx)
                foreach (var lcuRole in RoleCycle.Skip(1)) // без пустой "авто"
                {
                    if (usedRoles.Contains(lcuRole)) continue;
                    var share = _engine.RoleShare(their[i].EffectiveChampionId,
                                                  RecommendationEngine.LcuToDbRole(lcuRole));
                    pairs.Add((i, lcuRole, share));
                }
            var assigned = new HashSet<int>();
            foreach (var (idx, role, share) in pairs.OrderByDescending(x => x.Share))
            {
                if (share <= 0 || assigned.Contains(idx) || usedRoles.Contains(role)) continue;
                their[idx] = their[idx] with { Position = role };
                assigned.Add(idx);
                usedRoles.Add(role);
            }
        }

        // 3. Прямой оппонент — враг, вставший на мою позицию.
        var list = their.ToList();
        var direct = !string.IsNullOrEmpty(d.MyPosition)
            ? list.FirstOrDefault(p => p.EffectiveChampionId != 0 && p.Position == d.MyPosition)
            : null;
        return d with { TheirTeam = list, DirectOpponent = direct ?? d.DirectOpponent };
    }

    // Клик по роли в карточке врага — выпадающий список: Авто / ТОП / ЛЕС / МИД / БОТ / САПП.
    internal void EnemyRole_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not int cellId || cellId < 0) return;
        e.Handled = true;

        var menu = new ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        var current = _enemyRoleOverrides.GetValueOrDefault(cellId, "");
        foreach (var lcuRole in RoleCycle) // "" = авто
        {
            var item = new MenuItem
            {
                Header      = lcuRole.Length == 0 ? Loc.T("slot.roleAuto") : RoleName(lcuRole),
                IsChecked   = current == lcuRole,
                IsCheckable = false,
                Style       = itemStyle,
            };
            var chosen = lcuRole;
            item.Click += (_, _) => SetEnemyRole(cellId, chosen);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = el;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    // Назначает роль врагу вручную. Если роль уже занята другой ручной меткой —
    // та снимается (вернётся в авто и переедет на следующую лучшую роль).
    private void SetEnemyRole(int cellId, string lcuRole)
    {
        if (lcuRole.Length == 0) _enemyRoleOverrides.Remove(cellId);
        else
        {
            foreach (var other in _enemyRoleOverrides
                         .Where(kv => kv.Key != cellId && kv.Value == lcuRole)
                         .Select(kv => kv.Key).ToList())
                _enemyRoleOverrides.Remove(other);
            _enemyRoleOverrides[cellId] = lcuRole;
        }

        if (_lastRawDraft is null) { RenderCurrentState(); return; }
        var eff = ApplyEnemyRoleOverrides(_lastRawDraft);
        _lastDraft = eff;
        if (_engine != null && _lastRecs != null)
            // Длину списка обязательно передаём: этот пересчёт (после раскладки
            // ролей врагов) идёт на КАЖДОМ событии драфта и перетирал результат
            // боевого вызова — сколько бы ни стояло в настройках, оставалось 6.
            _lastRecs = eff.IsAram
                ? _engine.RecommendAram(eff, AppSettings.Current.DraftCount)
                : _engine.Recommend(eff, AppSettings.Current.DraftCount);
        RenderCurrentState();
    }

    // Команды по бокам (баны под заголовком, слоты, стиль, связки, линии) — общее для пиков и банов.
    private void RenderTeams(DraftState draft)
    {
        ClearHoverArt();   // слоты сейчас переедут — старая подсветка станет мусором
        var myRole = RecommendationEngine.LcuToDbRole(draft.MyPosition);

        var allyIds  = draft.MyTeam.Where(p => p.EffectiveChampionId != 0)
                                   .Select(p => p.EffectiveChampionId).ToList();
        var enemyIds = draft.TheirTeam.Where(p => p.EffectiveChampionId != 0)
                                      .Select(p => p.EffectiveChampionId).ToList();
        _allyStyle = ChampionTraits.DominantStyle(allyIds);
        var enemyArch = ChampionTraits.DominantStyle(enemyIds);

        MyTeamList.ItemsSource    = BuildSlots(draft.MyTeam,    ally: true,  _engine, myRole,
                                               null, draft.ActiveCells, draft.FirstPickCell, _allyStyle);
        EnemyTeamList.ItemsSource = BuildSlots(draft.TheirTeam, ally: false, _engine, myRole,
                                               _enemyRoleOverrides, draft.ActiveCells, draft.FirstPickCell,
                                               enemyArch);

        var myStyle    = ChampionTraits.StyleLabel(allyIds);
        var enemyStyle = ChampionTraits.StyleLabel(enemyIds);
        MyTeamStyle.Text    = myStyle.Length    > 0 ? Loc.T("draft.style", myStyle)    : "";
        EnemyTeamStyle.Text = enemyStyle.Length > 0 ? Loc.T("draft.style", enemyStyle) : "";
        // Пустая строка внизу колонки держала бы отступ над балансом урона.
        MyTeamStyle.Visibility    = myStyle.Length    > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyTeamStyle.Visibility = enemyStyle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderTeamBans(draft);

        RenderDamageMix(allyIds,  MyDmgPanel,    MyDmgAdCol,    MyDmgApCol,    MyDmgTrueCol,
                        MyDmgAdText,    MyDmgApText,    MyDmgTrueText,
                        MyDmgBar, MyDmgWarn);
        RenderDamageMix(enemyIds, EnemyDmgPanel, EnemyDmgAdCol, EnemyDmgApCol, EnemyDmgTrueCol,
                        EnemyDmgAdText, EnemyDmgApText, EnemyDmgTrueText,
                        EnemyDmgBar, EnemyDmgWarn, ally: false);

        var myCombos    = DetectCombos(draft.MyTeam,    ally: true);
        var enemyCombos = DetectCombos(draft.TheirTeam, ally: false);
        _myComboCards                = ToCards(myCombos,    ally: true);
        MyTeamCombos.ItemsSource     = _myComboCards;
        EnemyTeamCombos.ItemsSource  = ToCards(enemyCombos, ally: false);
        var showCombos = AppSettings.Current.DraftCombos;
        MyCombosHeader.Visibility    = showCombos && myCombos.Count    > 0 ? Visibility.Visible : Visibility.Collapsed;
        EnemyCombosHeader.Visibility = showCombos && enemyCombos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        MyTeamCombos.Visibility      = showCombos ? Visibility.Visible : Visibility.Collapsed;
        EnemyTeamCombos.Visibility   = showCombos ? Visibility.Visible : Visibility.Collapsed;

        var myTeam    = draft.MyTeam;
        var enemyTeam = draft.TheirTeam;
        Dispatcher.InvokeAsync(() =>
        {
            DrawTeamLines(MyTeamLines,    MyTeamList,    myTeam,    myCombos);
            DrawTeamLines(EnemyTeamLines, EnemyTeamList, enemyTeam, enemyCombos);
        }, DispatcherPriority.Loaded);
    }

    // Палитра коннектор-линий (фиолетовый / бирюзовый / золотой), как в референсе.
    private static readonly string[] ComboColors = ["#8B7CF6", "#2FB7A0", "#E8B84B"];

    private static List<TeamCombo> DetectCombos(IReadOnlyList<DraftPlayer> players, bool ally)
    {
        var team = players
            .Where(p => p.EffectiveChampionId != 0)
            .Select(p => (Id: p.EffectiveChampionId, Role: RecommendationEngine.LcuToDbRole(p.Position)))
            .ToList();
        return TeamSynergies.Detect(team, ally).Take(3).ToList();
    }

    private static List<ComboCard> ToCards(List<TeamCombo> combos, bool ally,
                                           bool ghost = false, int colorFrom = 0) =>
        combos.Select((co, i) => new ComboCard
        {
            Ghost       = ghost,
            Title       = co.Name,
            Icons       = co.ChampionIds.Select(IconCache.Get).Where(x => x != null).Cast<ImageSource>().ToList(),
            Description = co.Description,
            Tip         = co.Tip,
            TipLabel    = ally ? Loc.T("combo.howToPlay") : Loc.T("combo.danger"),
            AccentColor = ally ? "#C89B3C" : "#C84040",
            LineColor   = ComboColors[(colorFrom + i) % ComboColors.Length],
        }).ToList();

    // Цветные «черточки» под иконкой рекомендации: кандидат входит в связку с
    // союзниками — уже сложившуюся (цвет её линии) или ту, которую он СОЗДАСТ
    // своим пиком (следующий свободный цвет). Раньше дашик появлялся только у
    // готовых связок — т.е. лишь после того, как чемпиона реально взяли.
    private static List<string> SynDashesFor(
        int candidateId, List<int> allyIds, Dictionary<string, string> comboColorByName)
    {
        var result = new List<string>();
        if (candidateId == 0 || allyIds.Count == 0) return result;

        // Кандидат может уже быть в команде (это мой собственный пик) — тогда не
        // дублируем его, но чёрточки его связок всё равно показываем.
        var ids = allyIds.Contains(candidateId) ? allyIds : allyIds.Append(candidateId).ToList();
        var team = ids.Select(id => (Id: id, Role: "")).ToList();
        int nextColor = comboColorByName.Count; // будущая связка получит следующий цвет
        foreach (var combo in TeamSynergies.Detect(team, forAlly: true))
        {
            if (!combo.ChampionIds.Contains(candidateId)) continue;          // кандидат — участник
            if (!comboColorByName.TryGetValue(combo.Name, out var color))    // связки ещё нет —
                color = ComboColors[nextColor++ % ComboColors.Length];       // пик её создаст
            if (!result.Contains(color)) result.Add(color);
        }
        return result;
    }

    // Рисует скобки-коннекторы, связывающие портреты участников каждой связки.
    private void DrawTeamLines(
        Canvas canvas, ItemsControl list,
        IReadOnlyList<DraftPlayer> players, List<TeamCombo> combos)
    {
        canvas.Children.Clear();
        if (combos.Count == 0 || !AppSettings.Current.DraftCombos) return;

        // championId → индекс строки (0..4)
        var rowOf = new Dictionary<int, int>();
        for (int i = 0; i < players.Count && i < 5; i++)
        {
            var id = players[i].EffectiveChampionId;
            if (id != 0 && !rowOf.ContainsKey(id)) rowOf[id] = i;
        }

        double? RowY(int i)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c) return null;
            var top = c.TransformToVisual(canvas).Transform(new System.Windows.Point(0, 0)).Y;
            // центр портрета: отступы Border(1+3) + Grid(2) + половина эллипса 36
            return top + 6 + 36;
        }

        const double portraitRight = 72;
        for (int ci = 0; ci < combos.Count; ci++)
        {
            var brush = new SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    ComboColors[ci % ComboColors.Length]));
            brush.Freeze();
            double vx = 80 + ci * 9; // вертикаль скобки со сдвигом на каждую связку

            var ys = combos[ci].ChampionIds
                .Where(rowOf.ContainsKey).Select(id => rowOf[id]).Distinct()
                .Select(RowY).Where(y => y.HasValue).Select(y => y!.Value)
                .OrderBy(y => y).ToList();
            if (ys.Count < 2) continue;

            // вертикальный ствол скобки
            canvas.Children.Add(new Line
            {
                X1 = vx, Y1 = ys.First(), X2 = vx, Y2 = ys.Last(),
                Stroke = brush, StrokeThickness = 2.5,
            });
            // горизонтальные отводы к каждому портрету
            foreach (var y in ys)
                canvas.Children.Add(new Line
                {
                    X1 = portraitRight, Y1 = y, X2 = vx, Y2 = y,
                    Stroke = brush, StrokeThickness = 2.5,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
        }
    }

    // Сколько иконок стоит под портретом. Ровно три: под них сверстана строка,
    // и «сколько нашлось» читается как поломка, а не как честность.
    private const int SIDE_ICONS = 3;

    private static List<ChampSlotCard> BuildSlots(
        IReadOnlyList<DraftPlayer> players, bool ally,
        RecommendationEngine? engine, string myRole,
        IReadOnlyDictionary<int, string>? roleOverrides = null,
        IReadOnlyList<int>? activeCells = null, int firstPickCell = -1,
        ChampionTraits.Arch? teamStyle = null)
    {
        return Enumerable.Range(0, 5).Select(i =>
        {
            if (i >= players.Count) return EmptySlot(i + 1, ally);

            var p        = players[i];
            var champId  = p.ChampionId > 0 ? p.ChampionId : p.PickIntentId;
            var isLocked = p.ChampionId > 0;
            var hasChamp = champId > 0;
            var isMe     = p.IsLocalPlayer;
            // Мигание: ход этого игрока пикать прямо сейчас (и ещё не залочен).
            var isPicking = !isLocked && activeCells?.Contains(p.CellId) == true;

            List<ImageSource> sideIcons = [];
            string sideLabel = "";
            if (hasChamp && engine != null && AppSettings.Current.DraftSideIcons)
            {
                if (!ally)
                {
                    // Контры считаем в роли, которую враг занял (её подставляет
                    // ApplyEnemyRoleOverrides — ручная метка или догадка по доле
                    // роли). Без роли это была бы смесь линий, которые никогда
                    // не встречаются. И добираем до трёх: место под три иконки,
                    // одна вместо трёх выглядит поломкой.
                    var enemyRole = RecommendationEngine.LcuToDbRole(p.Position);
                    var ids = engine.TopCounters(champId,
                        string.IsNullOrEmpty(enemyRole) ? null : enemyRole, SIDE_ICONS, fill: true);
                    sideIcons = ids.Select(id => IconCache.Get(id)).Where(x => x != null).Cast<ImageSource>().ToList();
                    if (sideIcons.Count > 0) sideLabel = Loc.T("slot.counters");
                }
                else if (!p.IsLocalPlayer)
                {
                    // Синергия — с точки зрения МОЕЙ роли: это кандидаты мне в
                    // пик, а не абстрактные напарники этого союзника. Роль
                    // самого союзника тоже важна: у Владимира на топе и на миде
                    // лучшие напарники разные.
                    var allyRole = RecommendationEngine.LcuToDbRole(p.Position);
                    var ids = engine.TopSynergies(champId, myRole, SIDE_ICONS,
                        string.IsNullOrEmpty(allyRole) ? null : allyRole);
                    sideIcons = ids.Select(id => IconCache.Get(id)).Where(x => x != null).Cast<ImageSource>().ToList();
                    if (sideIcons.Count > 0) sideLabel = Loc.T("slot.synergy");
                }
            }

            // Значок архетипа (камень/ножницы/бумага), определяется по чемпиону.
            var (archGlyph, archColor, archTip) = hasChamp && AppSettings.Current.DraftArch
                ? ArchBadge(champId) : ("", "#888888", "");
            // У команды выраженный стиль — подсвечиваем тех, кто его и задаёт,
            // остальных приглушаем: сразу видно, на чём состав держится.
            var archOfChamp = hasChamp ? ChampionTraits.ChampArch(champId) : null;
            var archStrong  = teamStyle != null && archOfChamp == teamStyle;
            var archDim     = teamStyle != null && archOfChamp != teamStyle;

            return new ChampSlotCard
            {
                // Имя чемпиона показываем и у врагов: правила Riot запрещают
                // раскрывать личность соперника (ник), а пик в драфте и так виден
                // в самом клиенте.
                Name        = hasChamp ? DataDragon.Name(champId).ToUpperInvariant() : "—",
                // У врага с чемпионом роль кликабельна («▾»): игрок может задать её
                // вручную, если знает, куда пойдёт флекс-пик.
                Role        = hasChamp
                    ? (ally ? RoleName(p.Position) : RoleName(p.Position) + " ▾")
                    : (isMe ? Loc.T("slot.youPick") : ""),
                RoleCellId  = (!ally && hasChamp) ? p.CellId : -1,
                RoleColor   = (!ally && roleOverrides != null && roleOverrides.ContainsKey(p.CellId))
                    ? "#F5D77A" : "#8AA0B2",
                RoleTip     = (!ally && hasChamp) ? Loc.T("slot.roleTip") : null,
                Icon        = hasChamp ? IconCache.Get(champId) : null,
                // В пустом моём слоте показываем иконку роли
                RoleIcon    = (isMe && !hasChamp) ? RoleIcons.Get(p.Position) : null,
                Opacity     = isLocked ? 1.0 : (isMe ? 1.0 : 0.55),
                // Мой слот подсвечиваем ярким золотом и толстой рамкой
                BorderColor = isMe ? "#F5D77A" : (ally ? "#C89B3C" : "#C84040"),
                BorderThickness = isMe ? 3.5 : 2.0,
                IsMe        = isMe,
                NameColor   = hasChamp ? (ally ? "White" : "#E07070") : (isMe ? "#F5D77A" : "#3A5060"),
                IsEmpty     = !hasChamp,
                ChampionId  = hasChamp ? champId : 0,
                StatsRole   = RecommendationEngine.LcuToDbRole(p.Position),
                IsAlly      = ally,
                SideLabel   = sideLabel,
                SideIcons   = sideIcons,
                ArchGlyph   = archGlyph,
                ArchColor   = archColor,
                ArchTip     = archTip,
                ArchStrong  = archStrong,
                ArchDim     = archDim,
                IsPicking   = isPicking,
                // Союзник пикает — синяя волна слева, враг — красная справа.
                PickGradStrong  = ally ? "#5C36D6E7" : "#5CFF5A4D",
                PickGradWeak    = ally ? "#1A36D6E7" : "#1AFF5A4D",
                PickAccentColor = ally ? "#36D6E7"   : "#FF5A4D",
                PickMirror      = ally ? 1.0 : -1.0,
                IsFirstPick = p.CellId == firstPickCell,
                FirstPickLabel = Loc.T("slot.firstPick"),
            };
        }).ToList();
    }

    private static ChampSlotCard EmptySlot(int n, bool ally) => new()
    {
        Name        = "—",
        Role        = "",
        BorderColor = ally ? "#C89B3C" : "#C84040",
        NameColor   = "#3A5060",
        IsEmpty     = true,
        Opacity     = 0.3,
    };

    // Имя роли по позиции LCU (top/jungle/middle/bottom/utility) — локализовано.
    private static string RoleName(string pos) => Loc.T(pos.ToLowerInvariant() switch
    {
        "top"     => "roles.top",
        "jungle"  => "roles.jungle",
        "middle"  => "roles.mid",
        "bottom"  => "roles.adc",
        "utility" => "roles.support",
        _         => "roles.unknown",
    });

    // Имя роли по ключу БД (top/jungle/mid/adc/support) — локализовано.
    private static string RoleNameDb(string dbRole) =>
        string.IsNullOrEmpty(dbRole) || dbRole == "—"
            ? Loc.T("roles.unknown")
            : Loc.T($"roles.{dbRole}");

    // Значок архетипа чемпиона: эмодзи камень-ножницы-бумага + цвет фона.
    // Фронт(✊,синий) бьёт Дайв(✌,красный), Дайв бьёт Пик(✋,зелёный), Пик бьёт Фронт.
    private static (string Glyph, string Color, string Tip) ArchBadge(int champId) =>
        ChampionTraits.ChampArch(champId) switch
        {
            ChampionTraits.Arch.FrontToBack => ("✊", "#3D7EC4", Loc.T("arch.frontToBack")),
            ChampionTraits.Arch.Dive        => ("✌", "#D85050", Loc.T("arch.dive")),
            ChampionTraits.Arch.PickPoke    => ("✋", "#4CAE6A", Loc.T("arch.pickPoke")),
            _                               => ("", "#888888", ""),
        };
}
