using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Окно драфта — подбор рун под состав (Counterplay AI): третья карточка панели
// рун. Владелец (19.11): «на нижнее место добавим Counterplay AI runes, которые
// будут работать как наши предметы, — подбирать руны после финальных пиков».
// Как строка подбора сборки: пока пики идут — место со знаком и шкалой,
// драфт закончен — страница под состав (RuneAdvisor) с заменёнными рунами в
// кольце и причинами в подсказке.
public partial class OverlayWindow
{
    // Подбор для нынешнего состава; null — пики ещё идут (или судить не по чему).
    private RuneAdvisor.Advice? _runeAdvice;

    // Игрок сам выбрал карточку: появившийся подбор выбор не перехватывает.
    private bool _runeTouched;

    // Для какого чемпиона подбор уже появлялся: вспышка и перехват выбора —
    // событие «подбор пришёл», а не каждая перерисовка.
    private int _runeAiGlowFor;

    // Сколько шкалы показано (0…1) — растёт отсюда, а не с нуля (как _aiFillShown).
    private double _runeAiFillShown;

    // Что сделать, когда карточка встанет на экран: вырастить шкалу, вспыхнуть,
    // позвать к кнопке «Применить».
    private (double From, double To)? _runeAiGrow;
    private bool _runeAiFlash;
    private bool _runeAiPulse;

    // Состав, под который нарисована карточка: перерисовываем на чужой пик, а
    // не на каждое событие драфта.
    private string _runeDraftSig = "";

    private ContentPresenter? _runeAiPresenter;

    private static readonly Brush RuneAiEdge = new SolidColorBrush(Color.FromArgb(0x77, 0x36, 0xD6, 0xE7));

    private static readonly IReadOnlySet<int> NoRunes = new HashSet<int>();

    /// Новый чемпион — подбор и шкала с нуля, выбор снова за программой.
    private void ResetRuneAi()
    {
        _runeAdvice = null;
        _runeTouched = false;
        _runeAiGlowFor = 0;
        _runeAiFillShown = 0;
        _runeAiGrow = null;
        _runeAiFlash = _runeAiPulse = false;
        _runeDraftSig = RuneDraftSig();
    }

    /// Окончательные пики: чемпион есть и ход кончился — то же правило, что у
    /// слежки за метаниями союзников (в свой ход наведённый уже стоит в
    /// championId). Драфт закончен, когда взяли все свои и пятеро врагов.
    private (int Locked, int Total, bool Done) DraftProgress()
    {
        if (_lastDraft is not { } d) return (0, 10, false);
        var mine = d.MyTeam.Count(p => AllyWaver.Locked(d, p));
        var theirs = d.TheirTeam.Count(p => AllyWaver.Locked(d, p));
        var team = Math.Max(d.MyTeam.Count, 1);
        return (mine + Math.Min(theirs, 5), team + 5, theirs >= 5 && mine >= team);
    }

    // Оппонент по линии меняется и без нового пика: игрок переставил роль врага
    // в его карточке — поэтому он тоже в подписи.
    private string RuneDraftSig()
    {
        if (_lastDraft is not { } d) return "";
        return string.Join(",", d.MyTeam.Concat(d.TheirTeam)
            .Where(p => AllyWaver.Locked(d, p)).Select(p => $"{p.CellId}:{p.ChampionId}"))
            + $"|{LaneOpponent()}";
    }

    /// Оппонент по линии для подбора. Клиент называет его, только когда роли
    /// врагов открыты; в соло/дуо (и в песочнице) их прячет, и тогда — тот, кого
    /// окно само поставило на мою роль (по доле игр или по ручной метке в
    /// карточке врага). Без этого правила «по линии» в ранкеде молчали бы.
    private int? LaneOpponent() =>
        _lastDraft?.DirectOpponent?.EffectiveChampionId is { } id and not 0 ? id : _lastOpponentId;

    /// Разбор подбора — для песочницы: что подбор увидел и почему выбрал так.
    public Action<string>? RuneAiTrace { get; set; }

    /// Пик союзника или врага — пересчитать подбор, не дожидаясь клика по панели.
    private void RefreshRunesForDraft()
    {
        if (_runeStats is null || RunesBar.Visibility != Visibility.Visible) return;
        var sig = RuneDraftSig();
        if (sig == _runeDraftSig) return;
        _runeDraftSig = sig;
        RenderRuneOptions(_lastOpponentId);
    }

    /// Карточка подбора под номером index (сразу за вариантами). null — нечего
    /// и ставить: вариантов нет.
    private RuneOptionVm? RuneAiCard(int index)
    {
        if (_runeChoices.Count == 0) return null;
        var (locked, total, done) = DraftProgress();

        var enemies = _lastDraft?.TheirTeam
            .Select(p => p.EffectiveChampionId).Where(x => x != 0).Distinct().ToList() ?? [];
        _runeAdvice = done && _engine is not null && RuneIcons.Loaded && _runeStats is { } stats
            ? RuneAdvisor.Adapt(stats, _runeChampId, enemies, LaneOpponent(),
                                id => _engine.DamageShare(id), RuneIcons.SlotOf)
            : null;

        if (_runeAdvice is { } adv)
        {
            // Кейстоун подбор берёт против оппонента по линии — он может
            // отличаться от первого варианта, и тогда в кольце и он.
            var newKeystone = adv.Choice.Keystone != _runeChoices[0].Keystone;
            var news = adv.Swaps.Count > 0 || newKeystone;

            if (_runeAiGlowFor != _runeChampId)
            {
                _runeAiGlowFor = _runeChampId;
                _runeAiFlash = true;
                // Подбор пришёл, а игрок карточку не выбирал — подбор и есть
                // лучший ответ: выбираем его и зовём к кнопке. Если менять
                // нечего, это та же страница, что и первая, — выбор не трогаем.
                if (!_runeTouched && news)
                {
                    _runeSelected = index;
                    RunesStatus.Visibility = Visibility.Collapsed;
                    _runeAiPulse = true;
                }
            }

            var changed = adv.Swaps.Where(s => !s.Shard).Select(s => s.To).ToHashSet();
            var lines = new List<string>();
            if (adv.KeystoneWhy is { } kw) lines.Add($"• {RuneIcons.NameOf(adv.Choice.Keystone)} — {kw}");
            lines.AddRange((adv.Swaps.Count > 0 ? adv.Swaps : adv.Covered)
                           .Where(s => s.Why.Length > 0).Select(SwapLine));
            var head = news ? Loc.T("runes.aiTipHead") : Capitalize(Loc.T("runes.aiRunesCovered"));
            var tip = string.Join("\n", lines.Prepend(head));
            var card = RuneCard(adv.Choice, index, tip, changed);
            RuneAiTrace?.Invoke(Explain(adv));
            return card with
            {
                IsAi = true,
                BorderBrush = index == _runeSelected ? card.BorderBrush : RuneAiEdge,
                KeystoneRing = newKeystone ? AiBrush : Brushes.Transparent,
            };
        }

        RuneAiTrace?.Invoke(done
            ? "драфт закончен, но судить не по чему: " + (RuneIcons.Loaded ? "врагов не разобрать" : "справочник рун не загружен")
            : $"ждём пиков: выбрали {locked} из {total}");

        // Подбора нет (пики идут) — выбирать нечего.
        if (_runeSelected >= index) _runeSelected = 0;

        // Пустое место: блёклые кружки, а знак и шкала — сколько игроков уже
        // окончательно выбрали — горят. Шкала дошла до конца — на месте
        // кружков появляется страница. Панель не меняет высоту, и заранее
        // видно, где будет подбор и сколько его ждать.
        var fill = Math.Min(1.0, (double)locked / total);
        if (fill != _runeAiFillShown)
        {
            _runeAiGrow = (_runeAiFillShown, fill);
            _runeAiFillShown = fill;
        }
        var from = _runeAiGrow?.From ?? _runeAiFillShown;

        List<RuneVm> Empty(int k) =>
            [.. Enumerable.Range(0, k).Select(_ => new RuneVm(null, "", "") { Ring = SkelEdge })];

        return new RuneOptionVm(
            Index: -1, Label: "", WinrateText: "", GamesText: "", Tip: "",
            KeystoneIcon: null, WinrateBrush: MuteBrush,
            Background: SkelFill, BorderBrush: SkelEdge,
            PrimaryMini: Empty(3), SecondaryMini: Empty(2),
            TreeTitle: Loc.T("runes.aiRunesSoon"), SubTreeTitle: "",
            PrimaryRunes: [], SecondaryRunes: [], ShardRunes: [])
        {
            IsAi = true,
            FillVis = Visibility.Visible,
            // Шкала рисуется уже показанной долей; до новой дорастёт анимацией.
            FillWidth = AiFillMax * from,
            BodyOpacity = 0.38,
            KeystoneRing = SkelEdge,
        };
    }

    /// Разбор для песочницы: что увидел, что поставил.
    private static string Explain(RuneAdvisor.Advice a)
    {
        var p = a.Choice.Page;
        var page = $"{RuneIcons.StyleName(p.Primary)}: {string.Join(", ", p.Perks.Select(RuneIcons.NameOf))} · "
                 + $"{RuneIcons.StyleName(p.Sub)}: {string.Join(", ", p.Secondary.Select(RuneIcons.NameOf))} · "
                 + $"осколки {string.Join(",", p.Shards)}";
        var swaps = a.Swaps.Count == 0 ? "замен нет"
            : "замены: " + string.Join("; ", a.Swaps.Select(s =>
                (s.Shard ? $"осколок {s.From}→{s.To}" : $"{RuneIcons.NameOf(s.From)}→{RuneIcons.NameOf(s.To)}")
                + (s.Why.Length > 0 ? $" ({s.Why})" : "")));
        return string.Join("\n", a.Trace.Append(swaps).Append("страница: " + page));
    }

    /// «• Резня — толстые цели (3)».
    private static string SwapLine(RuneAdvisor.Swap s) =>
        "• " + (s.Shard ? Loc.T("runes.shardTenacity") : RuneIcons.NameOf(s.To)) + " — " + s.Why;

    /// Карточка встала в сетку: анимации ждут, пока у неё появится шаблон.
    private void AfterRuneAiRender()
    {
        if (_runeAiPulse) { _runeAiPulse = false; PulseApplyButton(); }
        if (_runeAiPresenter is not { } cp) return;

        if (_runeAiGrow is { } g)
        {
            _runeAiGrow = null;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                cp.ApplyTemplate();
                if (cp.ContentTemplate?.FindName("AiFillBar", cp) is FrameworkElement bar)
                    GrowFill(bar, g.From, g.To);
            }), DispatcherPriority.Loaded);
        }

        if (_runeAiFlash)
        {
            _runeAiFlash = false;
            Dispatcher.BeginInvoke(new Action(() => FlashAi(cp)), DispatcherPriority.Loaded);
        }
    }
}
