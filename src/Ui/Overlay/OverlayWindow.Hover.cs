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

// Окно драфта — подсветка при наведении на кандидата: кольца и числа у портретов,
// скобки связок, примерка в мой слот (слой SynLinks).
public partial class OverlayWindow
{
                                    // (сессия перерисовывается каждый тик таймера)

    // Клик по карточке рекомендации: наводим чемпиона в клиенте (обратимо).
    // ── Линии синергии по наведению ─────────────────────────────────────────
    // Наводишь курсор на кандидата — видно, с кем из союзников он играет в паре
    // и насколько. Иначе синергия остаётся числом в полоске, из которого не
    // понять, кем она набрана.
    private const double SYN_LINK_MIN = 0.3;   // пп — ниже это шум выборки

    // Наведение на иконку в пуле роли: те же подсветки, что и у карточки
    // (связки, синергия с союзниками, матчапы против врагов), плюс строка
    // показателей — у чемпиона из пула карточки со шкалами нет.
    internal void PoolCell_Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int id || id <= 0) return;
        DrawSynergyLinks(id);
        ShowPoolMetrics(id);
    }

    internal void PoolCell_Leave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ClearHoverArt();
        RolePoolHint.Inlines.Clear();
    }

    private void ShowPoolMetrics(int champId)
    {
        RolePoolHint.Inlines.Clear();
        if (_engine is null || _lastDraft is null) return;

        // Считаем ровно тем же движком, что и карточки, только для одного
        // кандидата — числа обязаны совпадать с полосками.
        var r = _engine.Recommend(_lastDraft, topN: 1, only: [champId]).FirstOrDefault();
        if (r is null) return;

        void Add(string label, string value, string color)
        {
            RolePoolHint.Inlines.Add(new System.Windows.Documents.Run(label + " ")
            {
                Foreground = Brush("#7C97AC"),
            });
            RolePoolHint.Inlines.Add(new System.Windows.Documents.Run(value + "   ")
            {
                Foreground = Brush(color), FontWeight = FontWeights.Bold,
            });
        }

        RolePoolHint.Inlines.Add(new System.Windows.Documents.Run(DataDragon.Name(champId) + "   ")
        {
            Foreground = Brush("#E8EEF5"), FontWeight = FontWeights.Bold,
        });
        // Те же показатели, в том же порядке и цветах, что на карточке.
        foreach (var d in MetricRow.Defs) Add(Loc.T(d.Key), Signed(d.Value(r)), d.TextColor);
    }

    private static SolidColorBrush Brush(string hex) => new(
        (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    internal void RecCard_Enter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is int id) DrawSynergyLinks(id);
    }

    internal void RecCard_Leave(object sender, System.Windows.Input.MouseEventArgs e) => ClearHoverArt();

    // Чья подсветка сейчас нарисована (0 — ничья).
    private int _hoverArtId;

    // Подсветка нарисована в абсолютных координатах канваса, поэтому живёт ровно
    // до следующего изменения раскладки: перерисовка драфта сдвигает портреты, и
    // оставленные дуги/цифры повисают не на месте.
    private void ClearHoverArt()
    {
        _hoverArtId = 0;
        if (SynLinks.Children.Count > 0) SynLinks.Children.Clear();
        if (!ReferenceEquals(MyTeamCombos.ItemsSource, _myComboCards))
        {
            PinTeamScroll();
            MyTeamCombos.ItemsSource  = _myComboCards;
            MyCombosHeader.Visibility = _myComboCards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // Список команды сдвинулся — подсветку перерисовываем на новых местах, а не
    // стираем. Стирание давало «через одного»: кандидат со связкой добавляет
    // в этот же список её бледную карточку, список сдвигается, и его подсветка
    // тут же пропадала, а у соседа без связки — оставалась.
    private void TeamScroll_Changed(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 && _hoverArtId != 0) DrawSynergyLinks(_hoverArtId);
    }

    // Колесо у дна списка запоминает позицию ниже дна, и ScrollViewer доезжает
    // до неё, как только список вырастет: от одной бледной карточки связки
    // состав уезжал вверх на её высоту. Прибиваем запомненную позицию к
    // нынешней — список растёт вниз, портреты стоят на месте.
    private void PinTeamScroll() => TeamScroll.ScrollToVerticalOffset(TeamScroll.VerticalOffset);

    private void DrawSynergyLinks(int champId)
    {
        _hoverArtId = champId;
        SynLinks.Children.Clear();
        if (!AppSettings.Current.DraftHover) return;
        if (champId == 0 || _engine is null || _lastDraft is null) return;

        // championId → строка в списке союзников; отдельно мой слот — в него
        // подставляем кандидата, чтобы связка шла от него, как от готового пика.
        var rowOf = new Dictionary<int, int>();
        int myRow = -1;
        for (int i = 0; i < _lastDraft.MyTeam.Count && i < 5; i++)
        {
            var p = _lastDraft.MyTeam[i];
            if (p.IsLocalPlayer) myRow = i;
            var id = p.EffectiveChampionId;
            if (id != 0 && !rowOf.ContainsKey(id)) rowOf[id] = i;
        }
        if (myRow >= 0 && !rowOf.ContainsKey(champId)) rowOf[champId] = myRow;

        DrawPickPreview(myRow, champId);

        // Связки, в которые кандидат войдёт своим пиком: те самые цветные полоски
        // под его иконкой. Цвет и место скобки — как у готовых связок, новая
        // встаёт следующей за ними.
        // Роли передаём настоящие: связки бывают привязаны к линии — ульт
        // Калисты забрасывает только её саппорта, и без роли правило не
        // отличит его от кого угодно с заходом. Роль кандидата — моя.
        var team = _lastDraft.MyTeam
            .Where(p => p.EffectiveChampionId != 0 && p.EffectiveChampionId != champId)
            .Select(p => (Id: p.EffectiveChampionId,
                          Role: RecommendationEngine.LcuToDbRole(p.Position)))
            .ToList();
        team.Add((champId, RecommendationEngine.LcuToDbRole(_lastDraft.MyPosition)));
        int nextSlot = _comboColors.Count;
        var fresh = new List<TeamCombo>();

        foreach (var combo in TeamSynergies.Detect(team, forAlly: true))
        {
            if (!combo.ChampionIds.Contains(champId)) continue;
            var known = _comboColors.TryGetValue(combo.Name, out var color);
            if (!known) color = ComboColors[nextSlot % ComboColors.Length];
            var slot = known ? _comboIndex.GetValueOrDefault(combo.Name) : nextSlot++;

            var rows = combo.ChampionIds.Where(rowOf.ContainsKey).Select(id => rowOf[id])
                            .Distinct().OrderBy(r => r).ToList();
            if (rows.Count >= 2) DrawBracket(rows, color!, slot);
            if (!known) fresh.Add(combo);   // связку создаёт сам пик
        }

        // Названия связок — в самой панели связок, бледными карточками: на линии
        // подпись налезала на состав и не читалась.
        if (fresh.Count > 0)
        {
            PinTeamScroll();
            MyTeamCombos.ItemsSource = _myComboCards
                .Concat(ToCards(fresh, ally: true, ghost: true, colorFrom: _myComboCards.Count))
                .ToList();
            MyCombosHeader.Visibility = Visibility.Visible;
        }

        // С кем именно кандидат силён в паре: зелёное свечение портрета и величина
        // под ним. Без линий — здесь важна не связь, а сила.
        var myRole = RecommendationEngine.LcuToDbRole(_lastDraft.MyPosition);
        for (int i = 0; i < _lastDraft.MyTeam.Count && i < 5; i++)
        {
            var p    = _lastDraft.MyTeam[i];
            var ally = p.EffectiveChampionId;
            if (ally == 0 || ally == champId || p.IsLocalPlayer) continue;

            var syn = _engine.PairSynergy(champId, myRole, ally,
                                          RecommendationEngine.LcuToDbRole(p.Position));
            // Минус показываем наравне с плюсом. Раньше рисовался только плюс, и
            // «пара играет хуже среднего» выглядело ровно как «данных нет» —
            // два разных ответа одной пустотой. У врага красное кольцо есть, у
            // союзника не было.
            if (Math.Abs(syn) >= SYN_LINK_MIN) DrawAllySynergy(i, syn);
            // Смысловая связка есть, а прибавки в винрейте нет (энчантер + кэрри
            // выигрывают не чаще среднего) — отмечаем бирюзой, без числа: пара
            // работает по механикам, но статистикой это не подтверждается.
            else if (TeamSynergies.ExplainPair(champId, ally,
                         RecommendationEngine.LcuToDbRole(p.Position)) is not null)
                DrawAllyPair(i);
        }

        // Враги: как кандидат идёт против каждого лично (матчап) и против его
        // стиля. В полосках это одно усреднённое число — здесь видно, кем оно
        // набрано и с кем будет тяжело.
        for (int i = 0; i < _lastDraft.TheirTeam.Count && i < 5; i++)
        {
            var e = _lastDraft.TheirTeam[i];
            if (e.EffectiveChampionId == 0) continue;

            var vs = _engine.VersusDelta(champId, myRole, e.EffectiveChampionId,
                                         RecommendationEngine.LcuToDbRole(e.Position));
            var arch  = ChampionTraits.ChampArch(e.EffectiveChampionId);
            var style = arch is { } a ? RecommendationEngine.StyleVsArch(champId, a) : 0.0;
            // Значок стиля берём тот же, что стоит на самом портрете: янтарное
            // число рядом с ним — «против ЭТОГО стиля», а не против чемпиона.
            var glyph = arch is { } aa ? ArchBadge(e.EffectiveChampionId).Glyph : "";
            if (Math.Abs(vs) >= SYN_LINK_MIN || style >= 0.5)
                DrawEnemyMatchup(i, vs, style, glyph);
        }
    }

    // Кольцо вокруг портрета с разрывом в левом верхнем углу: там сидит значок
    // архетипа, и сплошная обводка перечёркивала его. Дуга идёт на 300°, пропуская
    // сектор под значком.
    private const double RING_GAP_FROM = 255, RING_GAP_SWEEP = 300;

    private static System.Windows.Shapes.Path RingArc(
        System.Windows.Point center, double radius, Brush stroke, double thickness,
        System.Windows.Media.Effects.Effect? effect = null)
    {
        System.Windows.Point At(double deg) => new(
            center.X + radius * Math.Cos(deg * Math.PI / 180),
            center.Y + radius * Math.Sin(deg * Math.PI / 180));

        var fig = new PathFigure { StartPoint = At(RING_GAP_FROM), IsClosed = false };
        fig.Segments.Add(new ArcSegment(
            At(RING_GAP_FROM + RING_GAP_SWEEP), new System.Windows.Size(radius, radius),
            0, RING_GAP_SWEEP > 180, SweepDirection.Clockwise, true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        return new System.Windows.Shapes.Path
        {
            Data = geo, Stroke = stroke, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Effect = effect,
        };
    }

    // Союзник в смысловой связке с кандидатом, но без статистического перевеса.
    private void DrawAllyPair(int row)
    {
        if (MyTeamList.ItemContainerGenerator.ContainerFromIndex(row) is not FrameworkElement c) return;
        var pt = c.TransformToVisual(SynLinks).Transform(new System.Windows.Point(36, 42));

        var cyan = System.Windows.Media.Color.FromRgb(0x36, 0xD6, 0xE7);
        SynLinks.Children.Add(RingArc(pt, 36, new SolidColorBrush(cyan) { Opacity = 0.5 }, 2,
            new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = cyan, ShadowDepth = 0, BlurRadius = 10, Opacity = 0.5,
            }));
    }

    // Портрет врага: свечение по знаку матчапа (зелёное — кандидат его бьёт,
    // красное — проигрывает), под ним само число, рядом — вклад против стиля.
    private void DrawEnemyMatchup(int row, double vs, double style, string archGlyph)
    {
        if (EnemyTeamList.ItemContainerGenerator.ContainerFromIndex(row) is not FrameworkElement c) return;
        var pt = c.TransformToVisual(SynLinks).Transform(new System.Windows.Point(36, 42));

        var good     = vs >= 0;
        var color    = good ? System.Windows.Media.Color.FromRgb(0x4C, 0xE3, 0x8B)
                            : System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x5E);
        var strength = Math.Min(1.0, Math.Abs(vs) / 3.0);

        if (Math.Abs(vs) >= SYN_LINK_MIN)
        {
            var glow = new SolidColorBrush(color) { Opacity = 0.35 + 0.6 * strength };
            glow.Freeze();
            SynLinks.Children.Add(RingArc(pt, 36, glow, 1.5 + 2.5 * strength,
                new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = color, ShadowDepth = 0,
                    BlurRadius = 8 + 16 * strength, Opacity = 0.5 + 0.5 * strength,
                }));
        }

        // Матчап и стиль в одну строку: цвет разделяет их лучше подписей —
        // зелёный/красный это «против него», янтарный — «против его стиля».
        var line = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        if (Math.Abs(vs) >= SYN_LINK_MIN)
        {
            // «vs» — чтобы число не читалось как синергия: у союзников такие же
            // зелёные цифры, но там это пара, а здесь матчап против врага.
            line.Children.Add(Num("vs", System.Windows.Media.Color.FromRgb(0x8A, 0xA0, 0xB2), 0.9));
            line.Children.Add(Num(Signed(vs), color, 0.6 + 0.4 * strength));
        }
        if (style >= 0.5)
        {
            // Со значком стиля впереди: без него янтарное число стояло голым и
            // читалось как второй матчап, хотя это совсем другая величина —
            // насколько кандидат хорош против такого СТИЛЯ игры.
            var amber = System.Windows.Media.Color.FromRgb(0xE8, 0xB8, 0x4B);
            if (archGlyph.Length > 0) line.Children.Add(Num(archGlyph, amber, 0.7));
            line.Children.Add(Num("+" + style.ToString("F1"), amber, 0.85));
        }
        if (line.Children.Count == 0) return;

        line.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(line, pt.X - line.DesiredSize.Width / 2);
        Canvas.SetTop(line, pt.Y + 40);
        SynLinks.Children.Add(line);
    }

    private static TextBlock Num(string text, System.Windows.Media.Color color, double opacity) =>
        new()
        {
            Text = text, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 0, 2, 0),
            Foreground = new SolidColorBrush(color) { Opacity = opacity },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black, ShadowDepth = 0, BlurRadius = 4, Opacity = 0.9,
            },
        };

    // Кандидат в моём пустом слоте: наводишь — видно, как он встанет в состав,
    // и связка идёт от него, а не откуда-то из карточки.
    private void DrawPickPreview(int myRow, int champId)
    {
        // В банфазе чемпион в моём слоте читался бы как «беру его» — а его как раз
        // собираются забанить.
        if (_lastDraft?.InBanPhase == true) return;
        if (myRow < 0 || IconCache.Get(champId) is not { } icon) return;
        // Свой пик уже сделан — подставлять в занятый слот чужую иконку незачем:
        // именно она и висела поверх состава, пока смотришь статистику остальных.
        if (_lastDraft is not null && myRow < _lastDraft.MyTeam.Count
            && _lastDraft.MyTeam[myRow].ChampionId > 0) return;
        if (MyTeamList.ItemContainerGenerator.ContainerFromIndex(myRow) is not FrameworkElement c) return;
        var pt = c.TransformToVisual(SynLinks).Transform(new System.Windows.Point(36, 42));

        // Значок роли в пустом слоте сливался бы с полупрозрачной иконкой — гасим
        // его глухим кружком цвета панели.
        var cover = new System.Windows.Shapes.Ellipse
        {
            Width = 70, Height = 70,
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x12, 0x1A)),
        };
        Canvas.SetLeft(cover, pt.X - 35);
        Canvas.SetTop(cover, pt.Y - 35);
        SynLinks.Children.Add(cover);

        var preview = new System.Windows.Shapes.Ellipse
        {
            Width = 68, Height = 68, Opacity = 0.45,   // примерка, а не пик
            Fill = new ImageBrush(icon) { Stretch = Stretch.UniformToFill },
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xD7, 0x7A)),
            StrokeThickness = 2,
        };
        Canvas.SetLeft(preview, pt.X - 34);
        Canvas.SetTop(preview, pt.Y - 34);
        SynLinks.Children.Add(preview);
    }

    // Скобка-коннектор между участниками связки — та же, что у готовых связок
    // команды (см. DrawTeamLines), только строится под наведённого кандидата.
    private void DrawBracket(List<int> rows, string color, int slot)
    {
        if (MyTeamList.ItemContainerGenerator.ContainerFromIndex(rows[0]) is not FrameworkElement first) return;

        double? RowY(int i) =>
            MyTeamList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c
                ? c.TransformToVisual(SynLinks).Transform(new System.Windows.Point(0, 42)).Y
                : null;

        var ys = rows.Select(RowY).Where(y => y.HasValue).Select(y => y!.Value).OrderBy(y => y).ToList();
        if (ys.Count < 2) return;

        // x берём через тот же слот списка: канвас шире колонки команды
        var portraitRight = first.TransformToVisual(SynLinks).Transform(new System.Windows.Point(72, 0)).X;
        var stemX = first.TransformToVisual(SynLinks)
                         .Transform(new System.Windows.Point(80 + slot * 9, 0)).X;

        var brush = new SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();

        SynLinks.Children.Add(new Line
        {
            X1 = stemX, Y1 = ys.First(), X2 = stemX, Y2 = ys.Last(),
            Stroke = brush, StrokeThickness = 2.5,
        });
        foreach (var y in ys)
            SynLinks.Children.Add(new Line
            {
                X1 = portraitRight, Y1 = y, X2 = stemX, Y2 = y,
                Stroke = brush, StrokeThickness = 2.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });

    }

    // Портрет союзника, с которым кандидат хорошо играет: чем сильнее пара, тем
    // ярче и шире зелёное свечение. Под портретом — сама величина в пунктах.
    /// <summary>
    /// Кольцо и число у союзника: насколько пара с ним сильнее (или слабее)
    /// среднего. Цвет по знаку — как у врагов: зелёное «вместе лучше», красное
    /// «вместе хуже». Молчание теперь значит ровно одно — данных нет.
    /// </summary>
    private void DrawAllySynergy(int row, double syn)
    {
        if (MyTeamList.ItemContainerGenerator.ContainerFromIndex(row) is not FrameworkElement c) return;
        // (36, 42) — центр портрета в координатах слота (см. DrawTeamLines)
        var pt = c.TransformToVisual(SynLinks).Transform(new System.Windows.Point(36, 42));

        var strength = Math.Min(1.0, Math.Abs(syn) / 3.0);
        // Те же два цвета, что у вражеских портретов: один язык на обе стороны.
        var green = syn >= 0 ? System.Windows.Media.Color.FromRgb(0x4C, 0xE3, 0x8B)
                             : System.Windows.Media.Color.FromRgb(0xFF, 0x6B, 0x5E);

        var glow = new SolidColorBrush(green) { Opacity = 0.35 + 0.6 * strength };
        glow.Freeze();
        SynLinks.Children.Add(RingArc(pt, 36, glow, 1.5 + 2.5 * strength,
            new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = green, ShadowDepth = 0,
                BlurRadius = 8 + 16 * strength, Opacity = 0.5 + 0.5 * strength,
            }));

        var num = new TextBlock
        {
            Text = (syn >= 0 ? "+" : "") + syn.ToString("F1"),
            Foreground = new SolidColorBrush(green) { Opacity = 0.6 + 0.4 * strength },
            FontSize = 11, FontWeight = FontWeights.Bold,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = System.Windows.Media.Colors.Black, ShadowDepth = 0,
                BlurRadius = 4, Opacity = 0.9,
            },
        };
        num.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(num, pt.X - num.DesiredSize.Width / 2);
        Canvas.SetTop(num, pt.Y + 40);   // под портретом, до разделителя ещё есть место
        SynLinks.Children.Add(num);
    }
}
