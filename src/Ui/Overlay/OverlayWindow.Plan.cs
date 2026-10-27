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

// Окно драфта — фаза объявления: мои чемпионы на выданной роли.
public partial class OverlayWindow
{
    // ── Фаза объявления: мои чемпионы на выданной роли ───────────────────────

    // Фаза длится 15 секунд: пять строк читаются за секунду, десять — уже нет.
    private const int PlanRowCount = 5;

    // Винрейт цветом — от пяти игр, как рамки в окне пулов: «100%» с трёх игр
    // зелёным не горит.
    private const int PlanMinGamesColor = 5;

    // Метка «банят N%» — от 10%, порога оранжевого на панели банов: если мейна
    // банят в каждой десятой игре, стоит подумать о запасном.
    private const double PlanBanTag = 10;

    // Сдвиг к прошлому патчу меньше полупункта — шум, без цвета.
    private const double PlanDeltaColor = 0.5;

    private string _planMsg = "";

    private DateTime _planMsgUntil;

    /// <summary>
    /// Строки «мои чемпионы»: игры на ВЫДАННОЙ роли за месяц, самые игранные
    /// первыми (<see cref="SessionTracker.LaneChamps"/>). Игр на ней нет —
    /// чемпионы активного пула на эту роль, без своих чисел: винрейт в патче и
    /// бан-рейт у них всё равно есть.
    /// </summary>
    private List<PlanRow> PlanRowsFor(DraftState draft, string role, out bool fromPool)
    {
        fromPool = false;
        if (_engine is null || role.Length == 0) return [];
        var selected = _pickHoverId > 0 ? _pickHoverId : draft.Me?.PickIntentId ?? 0;

        var lane = SessionTracker.LaneChamps(role, _engine.RoleShare).Take(PlanRowCount).ToList();
        if (lane.Count > 0)
            return [.. lane.Select(c => PlanRowOf(c.ChampionId, c, role, selected))];

        var pool = PoolStore.ActiveForRole(role).Mine.Where(id => id > 0).Distinct().Take(PlanRowCount).ToList();
        fromPool = pool.Count > 0;
        return [.. pool.Select(id => PlanRowOf(id, null, role, selected))];
    }

    /// Заметка строки вместо «банят N%» / «из пула» и строки к её подсказке —
    /// у драфта без роли там роль и синергия с союзниками (OverlayWindow.Blind.cs).
    private readonly record struct PlanNote(string Text, string Brush, IReadOnlyList<string> Tip);

    private PlanRow PlanRowOf(int id, SessionTracker.LaneChamp? c, string role, int selected,
                              PlanNote? note = null)
    {
        var name  = DataDragon.Name(id);
        var patch = _engine!.PatchWr(id, role);
        var ban   = _engine.BanRate(id);

        var tip = new List<string> { name };
        if (c is not null)
        {
            tip.Add(Loc.T("plan.tipRecord", c.Wins, c.Games - c.Wins));
            var days = (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - c.LastTs) / 86400);
            tip.Add(days <= 0 ? Loc.T("plan.tipToday") : Loc.T("plan.tipLast", days));
        }
        if (patch is { } pw)
        {
            tip.Add(Loc.T("plan.tipPatch", _engine.Patch, pw.Wr.ToString("F1")));
            if (pw.Delta is { } d0) tip.Add(Loc.T("plan.tipPrev", (pw.Wr - d0).ToString("F1")));
        }
        if (ban > 0) tip.Add(Loc.T("ban.rateTip", name, ban.ToString("F1"), _engine.Patch));
        if (note is { } n) tip.AddRange(n.Tip);

        var delta = patch?.Delta;
        return new PlanRow
        {
            ChampionId = id,
            IsSelected = id == selected,
            Icon       = IconCache.Get(id),
            Name       = name,
            Games      = c?.Games ?? 0,
            Note       = note?.Text
                       ?? (ban >= PlanBanTag ? Loc.T("plan.banned", ban.ToString("0"))
                           : c is null ? Loc.T("plan.fromPool") : ""),
            NoteBrush  = note?.Brush
                       ?? (ban >= 2 * PlanBanTag ? "#FF5A4D" : ban >= PlanBanTag ? "#F0A93C" : "#6E8598"),
            WrText     = c is null ? "—" : $"{c.WinRate:0}%",
            WrBrush    = c is { Games: >= PlanMinGamesColor } ? WinrateColor.Hex(c.WinRate) : "#8AA0B2",
            GamesText  = c is null ? "" : $"· {c.Games}",
            Form       = c?.Form.Select(w => w ? "#57C98A" : "#E05B5B").ToList() ?? [],
            LpText     = c?.Lp is { } lp ? (lp > 0 ? "+" : "") + lp : "—",
            LpBrush    = c?.Lp is { } l ? l > 0 ? "#57C98A" : l < 0 ? "#E05B5B" : "#8AA0B2" : "#4E6275",
            PatchText  = patch is { } p ? $"{p.Wr:F1}%" : "—",
            PatchBrush = patch is { } p2 ? WinrateColor.Hex(p2.Wr) : "#4E6275",
            DeltaText  = delta is { } d ? (d >= 0 ? "▲" : "▼") + Math.Abs(d).ToString("F1") : "",
            DeltaBrush = delta >= PlanDeltaColor ? "#57C98A" : delta <= -PlanDeltaColor ? "#E05B5B" : "#6E8598",
            Tip        = string.Join("\n", tip),
        };
    }

    /// <summary>
    /// Фаза объявления, полный вид: вместо советов банов — мои чемпионы на
    /// выданной роли, под ними тот же тир-лист. Советы банов придут со стартом
    /// таймера банов.
    /// </summary>
    private void RenderPlanFull(DraftState draft)
    {
        var role = draft.MyPosition is { Length: > 0 } pos ? RecommendationEngine.LcuToDbRole(pos) : "";
        var roleName = RoleNameDb(role);
        PickHint.Visibility = Visibility.Collapsed;

        var rows = PlanRowsFor(draft, role, out var fromPool);
        ShowPlanPanel(rows, Loc.T(fromPool ? "plan.titlePool" : "plan.title", roleName),
                      Loc.T(fromPool ? "plan.colPool" : "plan.colGames"), Loc.T("plan.empty", roleName));
        PickBar.Visibility = Visibility.Collapsed;   // залочить в планировании нельзя
        RenderTierList(draft);
        RenderTeams(draft);
    }

    /// <summary>
    /// Панель «мои чемпионы» в центре: заголовок, столбцы, строки или пояснение
    /// вместо них; прочие части верхней строки центра прячутся. Нижнюю строку
    /// ставит вызывающий. Общая у объявления пиков и драфта без роли.
    /// </summary>
    private void ShowPlanPanel(List<PlanRow> rows, string title, string colGames, string empty)
    {
        PlanTitle.Text    = title;
        PlanColPatch.Text = Loc.T("plan.colPatch", _engine!.Patch);
        PlanColGames.Text = colGames;
        PlanList.ItemsSource  = rows;
        PlanHeader.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanEmpty.Text        = empty;
        PlanEmpty.Visibility  = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlanHint.Text = DateTime.UtcNow < _planMsgUntil ? _planMsg : Loc.T("plan.hint");
        PlanHint.Visibility = rows.Count > 0 && HoverHandler is not null ? Visibility.Visible : Visibility.Collapsed;

        RecScroll.Visibility     = Visibility.Collapsed;
        BanScroll.Visibility     = Visibility.Collapsed;
        BansDonePanel.Visibility = Visibility.Collapsed;
        PlanPanel.Visibility     = Visibility.Visible;
        PlanPanel.Margin         = new Thickness(0, 6, 0, 0);
        _banHoverId = 0;                                   // банить нечего
        BanBar.Visibility        = Visibility.Collapsed;
        RolePoolBar.Visibility   = Visibility.Collapsed;
        // Раскладка как у банов: панель по содержимому, нижняя строка — остаток.
        CenterGrid.RowDefinitions[0].Height = GridLength.Auto;
        CenterGrid.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
    }

    private void RenderPlanCompact(DraftState draft)
    {
        var role = draft.MyPosition is { Length: > 0 } pos ? RecommendationEngine.LcuToDbRole(pos) : "";
        PickHint.Visibility = Visibility.Collapsed;
        RecList.ItemsSource = PlanRowsFor(draft, role, out _).Select((r, i) => new RecCard
        {
            ChampionId = r.ChampionId,
            Rank       = $"{i + 1}.",
            Name       = r.Name,
            Score      = r.WrText,
            ScoreColor = r.WrBrush,
            Reason     = string.Join(" · ", new[] { r.GamesText.TrimStart('·', ' '),
                                                    r.LpText == "—" ? "" : r.LpText + " LP",
                                                    r.PatchText == "—" ? "" : r.PatchText + " " + r.DeltaText }
                                            .Where(x => x.Trim().Length > 0)),
            Icon       = r.Icon,
        }).ToList();
    }

    /// <summary>
    /// Клик по строке «мои чемпионы»: показать чемпиона союзникам — то же
    /// наведение пика, что у карточек подбора (в планировании клиент кладёт его
    /// в championPickIntent). Исход пишется в журнал: в настоящем драфте путь
    /// проверен не был, первая игра покажет, принимает ли клиент показ.
    /// </summary>
    internal async void PlanCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        if (HoverHandler is null) return;
        e.Handled = true;

        _pickHoverId = champId;
        RenderCurrentState();          // рамка выбранного золотится
        var code = await HoverHandler(champId);
        Log.Write($"планирование: показ {DataDragon.Name(champId)} — код {code}");
        if (code is < 200 or >= 300)
        {
            _planMsg = $"{Loc.T("plan.failed")} ({code})";
            _planMsgUntil = DateTime.UtcNow.AddSeconds(4);
            PlanHint.Text = _planMsg;
        }
    }
}
