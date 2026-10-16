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

// Окно драфта — панель рун: варианты страниц, применение рун и спеллов в клиент.
public partial class OverlayWindow
{
    // ── Руны и билд ──────────────────────────────────────────────────────────

    private ChampStats? _runeStats;

    private IReadOnlyList<RuneChoice> _runeChoices = [];

    private int _runeSelected;

    private int _runeChampId;

    private string _runeChampName = "";

    /// Функция импорта в клиент. Ставится из Program (в тестовом режиме — null).
    public Func<RunePage, int, string, Task<bool>>? ApplyRunesHandler { get; set; }

    /// (core, полный билд, ситуативные, role, championId, имя) → успех.
    public Func<IReadOnlyList<int>, IReadOnlyList<int>, IReadOnlyList<int>, string, int, string, Task<bool>>? ExportBuildHandler { get; set; }

    /// Выставить саммонер-спеллы (пара id) — вызывается вместе с рунами.
    public Func<IReadOnlyList<int>, Task<bool>>? ApplySpellsHandler { get; set; }

    /// Руна в подсказке/на кнопке: иконка, название и короткое описание.
    public sealed record RuneVm(BitmapImage? Icon, string Name, string Desc);

    /// Элемент кнопки варианта рун (привязка в XAML).
    public sealed record RuneOptionVm(
        int Index, string Label, string WinrateText, string GamesText, string Tip,
        BitmapImage? KeystoneIcon, Brush WinrateBrush, Brush Background, Brush BorderBrush,
        // Мелкие иконки на кнопке: первая строка — основное дерево (без кейстоуна),
        // вторая — вторичное. Видно, чем варианты отличаются, без наведения.
        IReadOnlyList<RuneVm> PrimaryMini,
        IReadOnlyList<RuneVm> SecondaryMini,
        // Полное дерево в подсказке.
        string TreeTitle, string SubTreeTitle,
        IReadOnlyList<RuneVm> PrimaryRunes,
        IReadOnlyList<RuneVm> SecondaryRunes,
        IReadOnlyList<RuneVm> ShardRunes);

    private string _runeRole = "";

    /// Показать панель рун/билда. stats=null — панели нет (данных мало/нет сети).
    public void ShowRunes(ChampStats? stats, int championId, string championName, string role, int? opponentId) =>
        Dispatcher.InvokeAsync(() =>
        {
            _runeStats = stats;
            _runeChampId = championId;
            _runeChampName = championName;
            _runeRole = role;
            _lastOpponentId = opponentId;

            if (stats is null || stats.Keystones.Count == 0)
            {
                _runesWaiting = false;
                RunesBar.Visibility = Visibility.Collapsed;
                if (CenterNotPicks) return;                        // строку держит вид банов или заглушка
                RunesGoneRow();
                return;
            }

            _runeChoices = RunesClient.Choices(stats, opponentId);
            _runeSelected = 0;
            _buildSelected = -1;   // новый чемпион — прошлый выбор сборки не в счёт
            _aiGlowFor = 0;        // и подсветку подбора показываем заново
            RenderRuneOptions(opponentId);

            ApplyRunesText.Text = Loc.T("runes.apply");
            RenderBuilds(stats);

            RunesStatus.Visibility = Visibility.Collapsed;
            // Ещё видна панель банов (первые секунды пиков) — руны готовы, но
            // строку не трогаем: покажет RenderFull, когда окно перейдёт к пикам.
            if (CenterNotPicks) { _runesWaiting = true; return; }
            RunesBar.Visibility = AppSettings.Current.DraftRunes
                ? Visibility.Visible : Visibility.Collapsed;
            TierListBar.Visibility = Visibility.Collapsed;   // руны заняли Row 1 — тир-лист прячем
            RolePoolBar.Visibility = Visibility.Collapsed;   // и пул роли тоже
            PulseApplyButton();   // сразу подсвечиваем: вариант выбран по умолчанию
        });

    private void RenderRuneOptions(int? opponentId)
    {
        var opp = opponentId is { } o ? DataDragon.Name(o) : null;
        RunesTitle.Text = opp != null
            ? Loc.T("runes.titleVs", _runeChampName, opp)
            : Loc.T("runes.title", _runeChampName);

        var vms = new List<RuneOptionVm>();
        for (int i = 0; i < _runeChoices.Count; i++)
        {
            var c = _runeChoices[i];
            var total = c.Winrate + c.VsDelta;
            var selected = i == _runeSelected;

            // Подсказка: честно показываем размер выборки и вклад матчапа —
            // чтобы было видно, где цифра надёжна, а где это лишь намёк.
            var tip = new StringBuilder();
            tip.AppendLine(Loc.T("runes.tipBase", c.Winrate.ToString("0.0"), c.Games.ToString("N0")));
            if (c.VsGames > 0 && Math.Abs(c.VsDelta) >= 0.1)
                tip.AppendLine(Loc.T("runes.tipVs", (c.VsDelta >= 0 ? "+" : "") + c.VsDelta.ToString("0.0"), c.VsGames.ToString("N0")));
            tip.Append(Loc.T("runes.tipPick", c.PickRate.ToString("0")));

            RuneVm Vm(int id) => new(RuneIcons.Icon(id), RuneIcons.NameOf(id), RuneIcons.DescOf(id));

            // Неизвестные руны не показываем: Riot убирает их между сезонами
            // (например, Eyeball Collection), и в старой статистике они ещё есть —
            // рисовать «#8138» вместо названия нельзя.
            List<RuneVm> Vms(IEnumerable<int> ids) =>
                ids.Where(RuneIcons.Known).Select(Vm).ToList();

            // Основное дерево: 4 руны (первая — кейстоун), вторичное: 2, плюс осколки.
            var primary   = Vms(c.Page.Perks);
            var secondary = Vms(c.Page.Secondary);
            var shards    = Vms(c.Page.Shards);

            // На кнопке — две строки иконок: сверху остаток основного дерева,
            // снизу вторичное. Так видно структуру страницы, а кнопка остаётся узкой.
            var primaryMini   = Vms(c.Page.Perks.Skip(1));
            var secondaryMini = secondary;

            vms.Add(new RuneOptionVm(
                Index: i,
                Label: RuneIcons.NameOf(c.Keystone),
                WinrateText: total.ToString("0.0") + "%",
                GamesText: FormatGames(c.Games),
                Tip: tip.ToString(),
                KeystoneIcon: RuneIcons.Icon(c.Keystone),
                WinrateBrush: WinrateBrush(total),
                Background: new SolidColorBrush(selected
                    ? Color.FromArgb(0x28, 0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush: new SolidColorBrush(selected
                    ? Color.FromRgb(0x36, 0xD6, 0xE7)
                    : Color.FromRgb(0x2A, 0x3A, 0x4F)),
                PrimaryMini: primaryMini,
                SecondaryMini: secondaryMini,
                TreeTitle: $"{RuneIcons.NameOf(c.Keystone)} · {RuneIcons.StyleName(c.Page.Primary)}",
                SubTreeTitle: RuneIcons.StyleName(c.Page.Sub),
                PrimaryRunes: primary,
                SecondaryRunes: secondary,
                ShardRunes: shards));
        }
        // Адаптивная раскладка по числу вариантов:
        //   1 → [руна][кнопка] в один ряд, по центру;
        //   2 → две руны в верхнем ряду, кнопка под левой;
        //   3 → 2 сверху + 1 снизу, кнопка в свободной ячейке (1,1).
        foreach (var child in RuneGrid.Children.OfType<ContentPresenter>().ToList())
            RuneGrid.Children.Remove(child);

        var template = (DataTemplate)RunesBar.FindResource("RuneOptionTemplate");
        int n = Math.Min(vms.Count, 3);

        (int row, int col) Cell(int i) => n switch
        {
            1 => (0, 0),                 // одна руна слева, кнопка справа (см. ниже)
            2 => (0, i),                 // обе в верхнем ряду
            _ => (i / 2, i % 2),         // 2 сверху, 1 снизу
        };

        for (int i = 0; i < n; i++)
        {
            var cp = new ContentPresenter { Content = vms[i], ContentTemplate = template };
            var (row, col) = Cell(i);
            Grid.SetRow(cp, row);
            Grid.SetColumn(cp, col);
            RuneGrid.Children.Add(cp);
        }

        // Кнопка «Применить»: 1 → справа от руны (ряд 0, кол 1);
        //                     2 → под левой руной (ряд 1, кол 0);
        //                     3 → свободная ячейка (ряд 1, кол 1).
        var (btnRow, btnCol) = n switch
        {
            1 => (0, 1),
            2 => (1, 0),
            _ => (1, 1),
        };
        Grid.SetRow(ApplyCell, btnRow);
        Grid.SetColumn(ApplyCell, btnCol);
    }

    private static string FormatGames(int g) =>
        g >= 1000 ? $"{g / 1000.0:0.#}k" : g.ToString();

    internal void RuneOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int idx) return;
        if (idx < 0 || idx >= _runeChoices.Count) return;
        _runeSelected = idx;
        RenderRuneOptions(_lastOpponentId);
        PulseApplyButton();      // выбор сменился — подсветка вспыхивает заново
        RunesStatus.Visibility = Visibility.Collapsed;  // прошлый «применено» уже неактуален
        e.Handled = true;
    }

    private int? _lastOpponentId;

    private async void ApplyRunes_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (ApplyRunesHandler is null || _runeChoices.Count == 0) return;
        var page = _runeChoices[_runeSelected].Page;

        RunesStatus.Text = Loc.T("runes.applying");
        RunesStatus.Foreground = MuteBrush;
        RunesStatus.Visibility = Visibility.Visible;

        var ok = await ApplyRunesHandler(page, _runeChampId, _runeChampName);

        // Вместе с рунами выставляем и саммонер-спеллы (пара из статистики;
        // привычный слот Флеша сохраняет импортёр). Спеллы берём из выбранной
        // сборки, иначе — из первой.
        var spellsOk = false;
        if (ok && ApplySpellsHandler != null && _runeStats is { Builds.Count: > 0 })
        {
            var bIdx = _buildSelected >= 0 && _buildSelected < _runeStats.Builds.Count
                ? _buildSelected : 0;
            var spells = _runeStats.Builds[bIdx].Spells;
            if (spells.Count >= 2) spellsOk = await ApplySpellsHandler(spells);
        }

        RunesStatus.Text = Loc.T(!ok ? "runes.failed"
                                 : spellsOk ? "runes.appliedSpells" : "runes.applied");
        RunesStatus.Foreground = ok ? WinBrush : LossBrush;
        if (ok) PulseApplyButton(on: false);   // применено — кнопка больше не «зовёт»
    }

    /// Справочники Riot доехали позже панели — так бывает, когда программу
    /// запустили без сети. Перерисовать то, что уже на экране: без справочника
    /// у вариантов рун нет ни названий, ни иконок, у сборки — названий предметов.
    /// Выбранный вариант и строка сборки сохраняются.
    public void RefsArrived() => Dispatcher.InvokeAsync(() =>
    {
        if (_runeStats is not { Keystones.Count: > 0 } stats) return;
        RenderRuneOptions(_lastOpponentId);
        RenderBuilds(stats);
    });

    public void HideRunes() => Dispatcher.InvokeAsync(() =>
    {
        _runesWaiting = false;
        RunesBar.Visibility = Visibility.Collapsed;
        if (_lastDraft?.InBanPhase != true && !CenterNotPicks) RunesGoneRow();
    });

    /// Рун нет — что встаёт в нижнюю строку центра: у подбора пул роли, у
    /// драфта без роли — тир-лист (пула роли там нет: роли нет).
    private void RunesGoneRow()
    {
        if (_blindView) { RenderTierList(_lastDraft); return; }
        TierListBar.Visibility = Visibility.Collapsed;   // тир-лист — только под банами
        RenderRolePool(_lastDraft);
    }

    // Руны пришли, пока на экране были баны: показать, когда окно перейдёт к пикам.
    private bool _runesWaiting;
}
