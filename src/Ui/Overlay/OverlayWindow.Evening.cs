using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Counterplay;

// Окно — блок «вечер» на экране профиля: винрейт в начале вечера и дальше,
// после двух поражений подряд, и совет сделать перерыв, когда следующая игра —
// в слабой для игрока части вечера (SessionTracker.Evening).
public partial class OverlayWindow
{
    // Сколько игр нужно в каждой части, чтобы о ней говорить: на десятке игр
    // разница в 10 пунктов — шум.
    private const int EveningMinGames = 20;

    // С какой разницы, в пунктах винрейта, часть вечера «слабая» и стоит совета.
    private const double EveningGap = 5.0;

    private static readonly Brush EveningWarn = Frozen(Color.FromRgb(0xF2, 0xB8, 0x4B));
    private static readonly Brush EveningCalm = Frozen(Color.FromRgb(0x7F, 0xD8, 0x9A));

    private static double Pct((int Games, int Wins) x) => x.Games > 0 ? 100.0 * x.Wins / x.Games : 0;

    /// <summary>
    /// Блок «вечер». Видно, только если включено в настройках и в обеих частях
    /// вечера набралось по <see cref="EveningMinGames"/> игр. Строка «после двух
    /// поражений» — так же, от стольких игр после серии. Совет:
    /// <list type="bullet">
    /// <item>сейчас две и больше поражений подряд, а после такой серии игрок
    /// выигрывает на <see cref="EveningGap"/> пунктов реже обычного — «перерыв?»;
    /// после серии он играет как обычно — спокойная строка: дело не в подборе;</item>
    /// <item>иначе следующая игра — после третьей за вечер, а дальше в вечер он
    /// выигрывает заметно реже — «может, перерыв?».</item>
    /// </list>
    /// </summary>
    private void RenderEvening()
    {
        var e = _session?.Night;
        if (!AppSettings.Current.ReadyEvening || e is null
            || e.Early.Games < EveningMinGames || e.Late.Games < EveningMinGames)
        {
            EveningBox.Visibility = Visibility.Collapsed;
            return;
        }
        EveningBox.Visibility = Visibility.Visible;
        EveningBox.ToolTip = Loc.T("evening.tip", e.Early.Games + e.Late.Games);

        EveningNow.Text = e.Game > 1
            ? Loc.T("evening.nowScore", e.Game, e.Wins, e.Losses)
            : Loc.T("evening.now", e.Game);
        var early = Pct(e.Early);
        var late = Pct(e.Late);
        EveningSplit.Text = Loc.T("evening.split", SessionTracker.EveningEarly, $"{early:0}", $"{late:0}");

        var tiltKnown = e.AfterTwo.Games >= EveningMinGames;
        var after = Pct(e.AfterTwo);
        var rest = Pct(e.Rest);
        EveningAfter.Text = tiltKnown ? Loc.T("evening.afterTwo", $"{after:0}", $"{rest:0}") : "";
        EveningAfter.Visibility = tiltKnown ? Visibility.Visible : Visibility.Collapsed;

        string? advice = null;
        var warn = true;
        if (e.LossStreak >= 2 && tiltKnown)
        {
            if (after <= rest - EveningGap) advice = Loc.T("evening.tilt", $"{after:0}");
            else { advice = Loc.T("evening.noTilt"); warn = false; }
        }
        else if (e.Game > SessionTracker.EveningEarly && late <= early - EveningGap)
            advice = Loc.T("evening.tired", $"{late:0}");
        EveningAdvice.Text = advice ?? "";
        EveningAdvice.Foreground = warn ? EveningWarn : EveningCalm;
        EveningAdvice.Visibility = advice is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
