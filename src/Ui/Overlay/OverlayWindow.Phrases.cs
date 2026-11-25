using System.Windows;
using System.Windows.Input;

namespace Counterplay;

// Окно драфта — фразы в чат драфта: полоса из пяти кнопок внизу центра и
// карандаш слева, который открывает окно правки (PhrasesWindow).
public partial class OverlayWindow
{
    /// Написать текст в чат драфта. Текст → HTTP-код отправки (0 — сорвалось).
    /// Нет обработчика (нет связи с клиентом) — нет и полосы.
    public Func<string, Task<int>>? ChatHandler { get; set; }

    /// Место под полосой: в конце списков пиков и банов и под панелями «по
    /// содержимому» — иначе полоса легла бы на их последнюю строку. Высота
    /// кнопки 32 и поля полосы 4 + 6 (разметка PhraseBar).
    private const double PhraseRoom = 42;

    /// Не чаще одной фразы за это время: чат клиента режет частые сообщения
    /// («слишком быстро»), а двойной щелчок не должен давать две фразы.
    private static readonly TimeSpan PhraseCooldown = TimeSpan.FromSeconds(1.5);

    private DateTime _phraseNext;
    private int _phraseFlash = -1;           // какая кнопка вспыхивает (-1 — никакая)
    private string _phraseFlashState = "";
    private int _phraseFlashTicket;          // новая вспышка отменяет гашение прошлой
    private PhrasesWindow? _phrasesWindow;

    /// Место, которое сейчас занимает полоса (0 — её нет).
    private double PhraseRoomNow => PhraseBar.Visibility == Visibility.Visible ? PhraseRoom : 0;

    /// <summary>
    /// Показать или убрать полосу. Видна весь драфт — от объявления до конца
    /// выбора, — пока есть связь с клиентом и полоса не выключена в настройках.
    /// Зовётся на каждом снимке драфта до раскладки центра: виды «по
    /// содержимому» берут из неё место (PhraseRoomNow).
    /// </summary>
    private void UpdatePhraseBar()
    {
        var on = AppSettings.Current.DraftPhrases && ChatHandler is not null && _lastDraft is not null;
        PhraseBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        RecTail.Height = BanTail.Height = PhraseRoomNow;
        BansDonePanel.Margin = new Thickness(0, 6, 0, PhraseRoomNow);
        if (on) RenderPhrases();
    }

    private void RenderPhrases()
    {
        var s = AppSettings.Current;
        PhraseList.ItemsSource = Enumerable.Range(0, ChatPhrases.Count).Select(i =>
        {
            var text = ChatPhrases.Text(s, i);
            var state = i == _phraseFlash ? _phraseFlashState : "";
            return new PhraseChip
            {
                Index = i,
                Text  = state switch { "sent" => "✓ " + text, "fail" => Loc.T("phrases.failed"), _ => text },
                Tip   = text,
                State = state,
            };
        }).ToList();
    }

    /// Клик по кнопке фразы (шаблон PhraseTemplates): фраза уходит в чат драфта.
    internal async void Phrase_Click(object sender)
    {
        if (sender is not FrameworkElement { Tag: int i } || ChatHandler is null) return;
        var now = DateTime.UtcNow;
        if (now < _phraseNext) return;
        _phraseNext = now + PhraseCooldown;

        var code = await ChatHandler(ChatPhrases.Text(AppSettings.Current, i));
        Flash(i, code is >= 200 and < 300 ? "sent" : "fail");
    }

    private async void Flash(int i, string state)
    {
        _phraseFlash = i;
        _phraseFlashState = state;
        var ticket = ++_phraseFlashTicket;
        if (PhraseBar.Visibility == Visibility.Visible) RenderPhrases();
        await Task.Delay(state == "fail" ? 2500 : 1200);
        if (ticket != _phraseFlashTicket) return;
        _phraseFlash = -1;
        _phraseFlashState = "";
        if (PhraseBar.Visibility == Visibility.Visible) RenderPhrases();
    }

    private void PhraseEdit_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var w = OpenPhrases();
        if (!w.IsVisible) w.Show();
    }

    /// <summary>
    /// Окно правки фраз над окном драфта. Возвращает окно не показанным:
    /// проверки показывают его сами, невидимым. Уже открыто — то же окно.
    /// </summary>
    public PhrasesWindow OpenPhrases()
    {
        if (_phrasesWindow is { IsLoaded: true } open) { open.Activate(); return open; }
        _phrasesWindow = new PhrasesWindow { Owner = this };
        _phrasesWindow.Closed += (_, _) => _phrasesWindow = null;
        return _phrasesWindow;
    }
}
