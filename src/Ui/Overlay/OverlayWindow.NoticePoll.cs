using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — опрос на плашке сообщений: варианты ответа, голос и итог.
//
// Вопрос — обычный текст сообщения (OverlayWindow.Notice.cs), здесь только то,
// что под ним. До ответа варианты — кнопки; после — полосы с долей голосов, и
// свой вариант золотом. Голос один и окончательный (NoticePoll).
public partial class OverlayWindow
{
    /// Вариант опроса для шаблона PollOptionTemplate. Fill и Rest — доли
    /// полосы (до ответа полоса пустая), Score — «голосов · процент».
    public sealed record PollOptionVm(
        string NoticeId, string OptionId, string Text,
        bool CanVote, bool Voted, bool Mine,
        GridLength Fill, GridLength Rest, string Score);

    private static readonly Brush PollFootBrush  = Frozen(Color.FromRgb(0x7C, 0x97, 0xAC));

    private static readonly Brush PollTroubleBrush = Frozen(Color.FromRgb(0xFF, 0x6A, 0x5D));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// Опрос, голос за который сейчас уходит на сайт (варианты не нажимаются,
    /// очередь сообщений стоит).
    private string? _pollSending;

    /// Голос не дошёл или опрос закрыт — строка под вариантами говорит об
    /// этом до следующего удачного голоса. Храним исход, а не текст: язык
    /// могут сменить, пока строка висит.
    private readonly Dictionary<string, NoticePoll.Outcome> _pollTrouble = new();

    /// Варианты под текстом сообщения; у обычного сообщения их нет.
    private void RenderPoll(Notice.Item? n)
    {
        if (n?.Poll is null)
        {
            BetaPoll.ItemsSource = null;
            BetaPoll.Visibility = BetaPollFoot.Visibility = Visibility.Collapsed;
            return;
        }

        BetaPoll.ItemsSource = PollRows(n);
        BetaPoll.IsEnabled = _pollSending != n.Id;
        BetaPoll.Visibility = BetaPollFoot.Visibility = Visibility.Visible;

        var trouble = _pollTrouble.TryGetValue(n.Id, out var t) ? t : NoticePoll.Outcome.Ok;
        BetaPollFoot.Foreground = trouble == NoticePoll.Outcome.Ok ? PollFootBrush : PollTroubleBrush;
        BetaPollFoot.Text = trouble switch
        {
            NoticePoll.Outcome.Closed => Loc.T("notice.poll.closed"),
            NoticePoll.Outcome.Failed => Loc.T("notice.poll.failed"),
            _ => NoticePoll.Mine(n) is null
                ? Loc.T("notice.poll.hint")
                : Loc.T("notice.poll.votes", PollTotal(n)),
        };
    }

    private static int PollTotal(Notice.Item n)
    {
        var counts = NoticePoll.Counts(n);
        return n.Poll!.Sum(o => counts.GetValueOrDefault(o.Id));
    }

    /// Строки вариантов. До ответа доли не показываем: итог — награда за
    /// ответ, и видеть его заранее значило бы подталкивать к большинству.
    private static List<PollOptionVm> PollRows(Notice.Item n)
    {
        var mine = NoticePoll.Mine(n);
        var counts = NoticePoll.Counts(n);
        var total = PollTotal(n);

        return n.Poll!.Select(o =>
        {
            var votes = counts.GetValueOrDefault(o.Id);
            var share = mine is null || total == 0 ? 0 : (double)votes / total;
            return new PollOptionVm(
                n.Id, o.Id, o.Text,
                CanVote: mine is null, Voted: mine is not null, Mine: o.Id == mine,
                Fill: new GridLength(share, GridUnitType.Star),
                Rest: new GridLength(1 - share, GridUnitType.Star),
                Score: mine is null ? "" : $"{votes} · {Math.Round(share * 100, MidpointRounding.AwayFromZero)}%");
        }).ToList();
    }

    /// Нажали вариант (из PollTemplates).
    internal async void PollOption_Click(object sender)
    {
        if ((sender as FrameworkElement)?.Tag is not PollOptionVm { CanVote: true } vm) return;
        var item = Notice.Active().FirstOrDefault(i => i.Id == vm.NoticeId);
        if (item?.Poll is null) return;
        await VotePollAsync(item, vm.OptionId);
    }

    /// Голос: пока он уходит, варианты не нажимаются, а плашка стоит на опросе.
    /// Принят — на месте кнопок итог; опрос закрыли — строка об этом и
    /// свежий набор сообщений (закрытый опрос уйдёт с плашки сам).
    internal async Task VotePollAsync(Notice.Item item, string optionId)
    {
        if (_pollSending is not null) return;
        _pollSending = item.Id;
        _pollTrouble.Remove(item.Id);
        ApplyNotice();

        var outcome = await NoticePoll.VoteAsync(item, optionId);

        _pollSending = null;
        if (outcome != NoticePoll.Outcome.Ok) _pollTrouble[item.Id] = outcome;
        ApplyNotice();
        if (outcome == NoticePoll.Outcome.Closed) _ = RefreshNoticeAsync();
    }

    /// <summary>
    /// Высота опроса под текстом сообщения — для FitNoticeCard: плашка держит
    /// высоту самого высокого из сообщений в очереди, и опрос в ней выше
    /// обычной новости на свои варианты.
    ///
    /// Меряется отдельный, не показанный список с тем же шаблоном и теми же
    /// отступами: живой список WPF меряет по прежнему содержимому.
    /// </summary>
    private double MeasurePoll(Notice.Item item, double inner)
    {
        if (item.Poll is null) return 0;

        var block = new StackPanel { Width = inner };
        block.Children.Add(new ItemsControl
        {
            Margin = BetaPoll.Margin,
            ItemTemplate = BetaPoll.ItemTemplate,
            ItemsSource = PollRows(item),
        });
        block.Children.Add(new TextBlock
        {
            Margin = BetaPollFoot.Margin,
            FontFamily = BetaPollFoot.FontFamily,
            FontSize = BetaPollFoot.FontSize,
            TextWrapping = TextWrapping.NoWrap,
            Text = Loc.T("notice.poll.hint"),
        });
        block.Measure(new Size(inner, double.PositiveInfinity));
        return block.DesiredSize.Height;
    }
}
