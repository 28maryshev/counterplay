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

// Окно драфта — сборки предметов: строки сборок, сборка под состав, экспорт в клиент.
public partial class OverlayWindow
{
    // ── Сборки ───────────────────────────────────────────────────────────────

    /// Слот предмета: CORE выделен золотой рамкой, ситуативные — тусклой.
    public sealed record SlotVm(ImageSource? Icon, string Tip, Brush Stroke, Thickness Thickness, double Dim)
    {
        /// Что предмет делает — кусками с цветом, как в клиенте.
        public IReadOnlyList<ItemDesc.Part> DescParts { get; init; } = [];
        public Visibility DescVis => DescParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        /// Цена: столько же во всех языках, поэтому просто число.
        public int Cost { get; init; }
        public string CostText => Cost > 0 ? Cost.ToString("N0") : "";
        public Visibility CostVis => Cost > 0 ? Visibility.Visible : Visibility.Collapsed;
        /// У пустых рамок подсказки нет: показывать в ней нечего.
        public bool HasTip => Tip.Length > 0;
    }

    /// Строка сборки: винрейт/игры слева, 6 слотов, кнопка экспорта. Выбранная — золотом.
    public sealed record BuildRowVm(
        int Index, IReadOnlyList<SlotVm> Slots, string ExportText,
        string WrText, Brush WrBrush, string GamesText,
        Brush RowBg, Brush RowStroke,
        string Reason, Visibility ReasonVis,
        Visibility WrVis, Visibility AiVis, string TipHead,
        // Пустое место под будущий подбор: блёклая строка без кнопки и без
        // знака вопроса — показывать там нечего, пока подбор не появился.
        Visibility HintVis, double RowOpacity, Visibility SkelVis,
        Brush ExportBg, Brush ExportStroke, Brush ExportFg);

    private static readonly Brush AiBrush    = new SolidColorBrush(Color.FromRgb(0x36, 0xD6, 0xE7));

    // Кнопка экспорта в двух видах: золотая у настоящей сборки и пустая рамка на
    // месте будущего подбора.
    private static readonly Brush ExportFill = new SolidColorBrush(Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C));

    private static readonly Brush ExportEdge = new SolidColorBrush(Color.FromArgb(0x55, 0xC8, 0x9B, 0x3C));

    private static readonly Brush ExportText = new SolidColorBrush(Color.FromRgb(0xF0, 0xD4, 0x8A));

    private static readonly Brush SkelFill   = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

    private static readonly Brush SkelEdge   = new SolidColorBrush(Color.FromArgb(0x33, 0x55, 0x70, 0x89));

    private static readonly Brush Transparent = new SolidColorBrush(Colors.Transparent);

    private static readonly Brush CoreStroke = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C));

    private static readonly Brush AltStroke  = new SolidColorBrush(Color.FromArgb(0x55, 0x55, 0x70, 0x89));

    // Подсветка выбранной сборки — золото (в тон кнопке экспорта).
    private static readonly Brush RowOn     = new SolidColorBrush(Color.FromArgb(0x22, 0xC8, 0x9B, 0x3C));

    private static readonly Brush RowOff    = new SolidColorBrush(Colors.Transparent);

    private static readonly Brush RowOnEdge = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C));

    private static readonly Brush RowOffEdge = new SolidColorBrush(Colors.Transparent);

    private int _buildSelected = -1;   // -1 = ничего не выбрано

    // Что показано в панели сейчас: обычные сборки либо подбор под состав врагов.
    // Экспорт и выделение работают с этим списком, а не со всем, что пришло с
    // сервера, — иначе кнопка отправляла бы в клиент не ту сборку, что на экране.
    private List<BuildData> _shownBuilds = [];

    private List<string> _buildReasons = [];

    // Что именно заменено под состав врагов — эти слоты подсвечиваем, иначе
    // подбор неотличим от обычной сборки: разница бывает в один предмет.
    private List<HashSet<int>> _buildChanged = [];

    private List<bool> _buildIsAi = [];

    // Для какого чемпиона уже показали вспышку подбора. Строка перерисовывается
    // на каждый ховер и выбор, а вспышка — событие «сборка появилась», и мигать
    // на каждую перерисовку она не должна.
    private int _aiGlowFor;

    private void RenderBuilds(ChampStats stats)
    {
        if (stats.Builds.Count == 0)
        {
            BuildList.Visibility = Visibility.Collapsed;
            return;
        }

        // Подбор под врагов считаем, только когда известны ВСЕ пятеро: пятый пик
        // способен перевернуть решение — один лечащийся терпим, двое требуют
        // среза лечения.
        var enemies = _lastDraft?.TheirTeam
            .Select(p => p.EffectiveChampionId).Where(x => x != 0).Distinct().ToList() ?? [];
        // Своя команда — для предметов поддержки: кого усиливать, зависит от того,
        // кто рядом.
        var allies = _lastDraft?.MyTeam
            .Select(p => p.EffectiveChampionId).Where(x => x != 0).Distinct().ToList() ?? [];
        var advice = enemies.Count >= 5 && _engine is not null
            ? BuildAdvisor.Adapt(stats, stats.Builds[0], enemies, allies, id => _engine.DamageShare(id))
            : new BuildAdvisor.Advice([], []);
        var adapted = advice.Builds;

        // Строк всегда ДВЕ: ходовая сборка и подбор под врагов. Раньше их было
        // то две, то три — сколько сборок пришло с сервера, столько и рисовали, —
        // и панель прыгала по высоте, стоило навести на другого чемпиона. Вторая
        // строка до появления подбора стоит блёклым пустым местом: видно, что там
        // что-то будет.
        _shownBuilds = [stats.Builds[0]];
        _buildReasons = [""];
        _buildChanged = [[]];
        _buildIsAi = [false];

        // Показываем ОДНУ сборку под состав, а не набор вариантов: игрок покупает
        // один набор предметов, и выбор из двух «умных» сборок перекладывает на
        // него ту работу, ради которой всё и затевалось.
        foreach (var a in adapted.Take(1))
        {
            _shownBuilds.Add(stats.Builds[0] with { Items = a.Items });
            _buildReasons.Add(string.Join("\n", a.Reasons.Select(r => "• " + r)));
            _buildChanged.Add([.. a.Changed]);
            _buildIsAi.Add(true);
        }

        // Что сказать во второй строке, пока подбора нет. Состав известен
        // целиком, а менять нечего — так и говорим: молчание выглядит как
        // поломка. И не просто «менять нечего», а что именно состав требует и
        // почему это уже куплено, иначе читается как отказ.
        var ghostTip = enemies.Count >= 5
            ? (advice.Covered.Count > 0
                ? Loc.T("runes.aiCovered") + "\n"
                  + string.Join("\n", advice.Covered.Select(c => "• " + c))
                : Loc.T("runes.aiCovered"))
            : Loc.T("runes.aiSoon");

        var rows = new List<BuildRowVm>();
        for (int i = 0; i < _shownBuilds.Count; i++)
        {
            var b = _shownBuilds[i];
            var reason = _buildReasons[i];
            var changed = _buildChanged[i];
            var isAi = _buildIsAi[i];
            var slots = new List<SlotVm>();

            // CORE — набор, который реально играли вместе (у него и винрейт).
            // Остальные слоты — ходовые докупки, которыми сборка добита до шести.
            var core = b.Core.Count > 0 ? b.Core.ToHashSet() : b.Items.Take(3).ToHashSet();
            var ordered = isAi
                ? b.Items.AsEnumerable()          // подбор показываем как есть, порядком закупки
                : b.Items.Where(core.Contains).Concat(b.Items.Where(x => !core.Contains(x)));
            foreach (var id in ordered.Take(6))
            {
                var isCore = core.Contains(id);
                var swapped = changed.Contains(id);
                slots.Add(new SlotVm(
                    ItemIcons.GetOrLoad(id),
                    ItemIcons.NameOf(id)
                        + (swapped ? $" · {Loc.T("runes.aiUnder")}" : isCore ? $" · {Loc.T("runes.core")}" : ""),
                    swapped ? AiBrush : isCore ? CoreStroke : AltStroke,
                    new Thickness(swapped ? 2 : isCore ? 1.5 : 1),
                    swapped || isCore ? 1.0 : 0.85)
                {
                    DescParts = ItemIcons.DescPartsOf(id),
                    Cost = ItemIcons.CostOf(id),
                });
            }
            // Добиваем до 6 слотов пустыми рамками — сетка ровная.
            while (slots.Count < 6)
                slots.Add(new SlotVm(null, "", AltStroke, new Thickness(1), 1.0));

            var selected = i == _buildSelected;
            rows.Add(new BuildRowVm(
                Index: i,
                Slots: slots,
                ExportText: Loc.T("runes.export"),
                WrText: isAi ? Loc.T("runes.aiBuild") : b.Winrate.ToString("0.0") + "%",
                WrBrush: isAi ? AiBrush : WinrateBrush(b.Winrate),
                GamesText: isAi ? Loc.T("runes.aiUnder") : FormatGames(b.Games),
                RowBg: selected ? RowOn : RowOff,
                RowStroke: selected ? RowOnEdge : RowOffEdge,
                Reason: reason,
                ReasonVis: reason.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
                WrVis: isAi ? Visibility.Collapsed : Visibility.Visible,
                AiVis: isAi ? Visibility.Visible : Visibility.Collapsed,
                // Заголовок подсказки. У подбора нет своего винрейта: такой
                // набор целиком никто не играл, и чужой процент был бы враньём —
                // вместо него подпись о том, откуда набор взялся.
                TipHead: isAi
                    ? Loc.T("runes.aiTipHead")
                    : Loc.T("runes.buildTip", b.Winrate.ToString("0.0"), FormatGames(b.Games)),
                HintVis: isAi ? Visibility.Visible : Visibility.Collapsed,
                RowOpacity: 1.0,
                SkelVis: Visibility.Collapsed,
                ExportBg: ExportFill, ExportStroke: ExportEdge, ExportFg: ExportText));
        }

        // Подбора пока нет — вторую строку занимает его блёклый след: шесть
        // пустых рамок и наш знак. Так панель не меняет высоту, когда подбор
        // появляется, и заранее видно, где он будет.
        if (rows.Count == 1)
        {
            var empty = new List<SlotVm>();
            for (var k = 0; k < 6; k++)
                empty.Add(new SlotVm(null, "", AltStroke, new Thickness(1), 1.0));

            rows.Add(new BuildRowVm(
                Index: -1,                      // не выбирается и не экспортируется
                Slots: empty,
                ExportText: "",
                // Ни знака, ни подписей: место под подбор — это пустое место, а
                // не выключенный подбор. Знак и подпись появятся вместе с ним.
                WrText: "",
                WrBrush: AiBrush,
                GamesText: "",
                RowBg: RowOff,
                RowStroke: RowOffEdge,
                Reason: "",
                ReasonVis: Visibility.Collapsed,
                WrVis: Visibility.Collapsed,
                AiVis: Visibility.Collapsed,
                TipHead: ghostTip,
                HintVis: Visibility.Collapsed,
                RowOpacity: 0.38,
                SkelVis: Visibility.Visible,
                // Та же кнопка, только пустая: текст прозрачный, чтобы ширина
                // осталась кнопочной.
                ExportBg: SkelFill, ExportStroke: SkelEdge, ExportFg: Transparent));
        }

        BuildList.ItemsSource = rows;
        BuildList.Visibility = Visibility.Visible;

        // Подбор появился — коротко подсвечиваем строку, чтобы её заметили: она
        // возникает в момент, когда человек смотрит на список чемпионов, а не на
        // панель сборок.
        if (adapted.Count > 0 && _aiGlowFor != stats.ChampionId)
        {
            _aiGlowFor = stats.ChampionId;
            // Контейнеры строк создаются на следующем проходе разметки — до него
            // подсвечивать нечего.
            Dispatcher.BeginInvoke(new Action(GlowAiRow), DispatcherPriority.Loaded);
        }
    }

    /// Вспышка вокруг строки подбора: свет вспыхивает белым почти мгновенно, а
    /// дальше медленно остывает в синий и гаснет — как настоящая вспышка, у
    /// которой резкий удар и длинный хвост. Эффект снимается совсем: постоянная
    /// подсветка отвлекала бы от самой сборки.
    private void GlowAiRow()
    {
        // Подбор всегда идёт второй строкой: первая — обычная, самая ходовая.
        if (BuildList.ItemContainerGenerator.ContainerFromIndex(1) is not UIElement row) return;

        // Кольцо из шаблона строки. Свет от него куда плотнее, чем от содержимого:
        // у строки прозрачный фон, и свечение, построенное по иконкам, выходило
        // жидким при большом радиусе.
        Border? ring = null;
        if (row is ContentPresenter cp)
        {
            cp.ApplyTemplate();
            ring = cp.ContentTemplate?.FindName("AiGlowRing", cp) as Border;
        }
        var ringGlow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(0x8C, 0xE6, 0xFF),
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0,
        };
        if (ring is not null) ring.Effect = ringGlow;

        var glow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(0x36, 0xD6, 0xE7),
            ShadowDepth = 0,
            BlurRadius = 0,
            Opacity = 0,
        };
        row.Effect = glow;

        // Удар вспышки — меньше десятой доли секунды: глаз воспринимает такое
        // как «появилось разом», а не как выехавшую анимацию.
        var flash = TimeSpan.FromSeconds(0.08);
        // Короткая полка на пике: свет держится на полной яркости ещё мгновение
        // и оттого читается ярче — мгновенный пик глаз сглаживает и теряет.
        var hold = TimeSpan.FromSeconds(0.12);
        var decay = TimeSpan.FromSeconds(3.0);
        var peak = KeyTime.FromTimeSpan(flash);
        var full = KeyTime.FromTimeSpan(flash + hold);
        var end = KeyTime.FromTimeSpan(flash + hold + decay);

        // Затухание неравномерное: сначала свет спадает быстро, потом долго
        // тлеет. Равномерное гасло бы как выключаемая лампа — заметно и скучно.
        static KeySpline Ease() => new(0.0, 0.7, 0.2, 1.0);

        var rise = new DoubleAnimationUsingKeyFrames();
        rise.KeyFrames.Add(new LinearDoubleKeyFrame(80, peak));
        rise.KeyFrames.Add(new LinearDoubleKeyFrame(80, full));
        rise.KeyFrames.Add(new SplineDoubleKeyFrame(0, end, Ease()));

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, peak));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, full));
        fade.KeyFrames.Add(new SplineDoubleKeyFrame(0, end, Ease()));

        // Вспышка ледяная-синяя, а не белая: белое читалось как засветка экрана,
        // синее — как наш свет. Пока гаснет, синий густеет и уходит в глубокий.
        var heat = new ColorAnimationUsingKeyFrames();
        heat.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0x8C, 0xE6, 0xFF), peak));
        heat.KeyFrames.Add(new LinearColorKeyFrame(Color.FromRgb(0x8C, 0xE6, 0xFF), full));
        heat.KeyFrames.Add(new SplineColorKeyFrame(Color.FromRgb(0x1E, 0x8F, 0xE8), end, Ease()));
        // Сама обводка тоже вспыхивает и гаснет: она и даёт ту плотность света,
        // которой не хватало halo.
        var edge = new DoubleAnimationUsingKeyFrames();
        edge.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, peak));
        edge.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, full));
        edge.KeyFrames.Add(new SplineDoubleKeyFrame(0, end, Ease()));

        // Эффекты снимаем после вспышки: висящий DropShadow дорог при перерисовке
        // и слегка размывает иконки предметов.
        fade.Completed += (_, _) =>
        {
            row.Effect = null;
            if (ring is not null) ring.Effect = null;
        };

        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, rise);
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fade);
        glow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, heat);

        if (ring is null) return;
        ring.BeginAnimation(UIElement.OpacityProperty, edge);
        ringGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.BlurRadiusProperty, rise.Clone());
        ringGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fade.Clone());
        ringGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty, heat.Clone());
    }

    /// Клик по строке сборки — выбор (подсветка золотом), без экспорта.
    private void BuildRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (_runeStats is null) return;
        // Пустое место под будущий подбор выбирать нечего.
        if (idx < 0 || idx >= _shownBuilds.Count) return;
        _buildSelected = idx;
        RenderBuilds(_runeStats);
        e.Handled = true;
    }

    /// <summary>
    /// Свечение вокруг кнопки «Применить руны»: яркая вспышка при выборе варианта,
    /// затем непрерывная пульсация — кнопка «зовёт», пока руны не применены.
    /// Каждый новый выбор перезапускает анимацию с нуля.
    /// </summary>
    private void PulseApplyButton(bool on = true)
    {
        if (!on)
        {
            // Применили — свечение гасим (действие больше не требуется).
            ApplyGlowRing.BeginAnimation(OpacityProperty, null);
            ApplyGlowRing.Opacity = 0;
            if (ApplyGlowRing.Effect is DropShadowEffect g)
                g.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            return;
        }

        // ОДНА вспышка при выборе (не мигает постоянно): резко разгорается и
        // оседает на ровном свечении — кнопка остаётся подсвеченной, но не
        // отвлекает морганием.
        var op = new DoubleAnimationUsingKeyFrames();
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0.0,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)),
                                                  new SineEase { EasingMode = EasingMode.EaseOut }));
        op.KeyFrames.Add(new EasingDoubleKeyFrame(0.75, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
                                                  new SineEase { EasingMode = EasingMode.EaseInOut }));
        op.FillBehavior = FillBehavior.HoldEnd;
        ApplyGlowRing.BeginAnimation(OpacityProperty, op);

        // Ореол: широкая вспышка → устойчивое сильное свечение.
        if (ApplyGlowRing.Effect is DropShadowEffect glow)
        {
            var blur = new DoubleAnimationUsingKeyFrames();
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(8,  KeyTime.FromTimeSpan(TimeSpan.Zero)));
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(34, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)),
                                                        new SineEase { EasingMode = EasingMode.EaseOut }));
            blur.KeyFrames.Add(new EasingDoubleKeyFrame(20, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900)),
                                                        new SineEase { EasingMode = EasingMode.EaseInOut }));
            blur.FillBehavior = FillBehavior.HoldEnd;
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blur);
        }
    }

    private async void ExportBuild_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ExportBuildHandler is null || _runeStats is null) return;
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (idx < 0 || idx >= _shownBuilds.Count) return;

        // Экспортируемая сборка становится выбранной — видно, что именно уехало.
        _buildSelected = idx;
        RenderBuilds(_runeStats);

        RunesStatus.Text = Loc.T("runes.exporting");
        RunesStatus.Foreground = MuteBrush;
        RunesStatus.Visibility = Visibility.Visible;

        // Три части набора: core (ключевые предметы этой сборки), полный билд из
        // 6 слотов и ситуативные — всё, что ещё часто берут на чемпионе.
        var b = _shownBuilds[idx];
        var core = b.Core.Count > 0 ? b.Core : b.Items.Take(2).ToList();
        var full = b.Items;
        var situational = _runeStats.Builds
            .SelectMany(x => x.Items)
            .Distinct()
            .Where(i => !full.Contains(i))
            .ToList();

        var ok = await ExportBuildHandler(core, full, situational, _runeRole, _runeChampId, _runeChampName);
        RunesStatus.Text = Loc.T(ok ? "runes.exportedToSets" : "runes.failed");
        RunesStatus.Foreground = ok ? WinBrush : LossBrush;
    }
}
