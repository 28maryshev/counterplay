using System.Windows;
using System.Windows.Input;

namespace Counterplay;

// Окно драфта — фразы в чат драфта: полоса кнопок внизу центра (у кнопок с
// выбором — список ролей или «что нужно») и карандаш слева, который открывает
// окно правки (PhrasesWindow).
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
        PhraseList.ItemsSource = ChatPhrases.Bar(s).Select((b, i) =>
        {
            var state = i == _phraseFlash ? _phraseFlashState : "";
            return new PhraseChip
            {
                Index = i,
                Text  = state switch
                {
                    "sent" => "✓ " + (_phraseFlashText.Length > 0 ? _phraseFlashText : ChatPhrases.Text(s, b)),
                    "fail" => Loc.T("phrases.failed"),
                    _      => ChatPhrases.Label(s, b),
                },
                Tip   = ChatPhrases.Text(s, b),
                State = state,
            };
        }).ToList();
    }

    private string _phraseFlashText = "";   // что ушло — у кнопки с выбором это уже готовая фраза

    /// <summary>
    /// Клик по кнопке фразы (шаблон PhraseTemplates). Просто фраза уходит в чат
    /// сразу; у кнопки с выбором сначала список (<see cref="PhraseMenu"/>) над
    /// кнопкой, и в чат уходит фраза с выбранным.
    /// </summary>
    internal void Phrase_Click(object sender)
    {
        if (sender is not FrameworkElement { Tag: int i } fe || ChatHandler is null) return;
        var bar = ChatPhrases.Bar(AppSettings.Current);
        if (i >= bar.Count) return;
        if (!ChatPhrases.IsChoice(bar[i])) { SendPhrase(i, ChatPhrases.Text(AppSettings.Current, bar[i])); return; }

        var menu = PhraseMenu(i);
        menu.PlacementTarget = fe;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Список выбора кнопки i полосы: роли (с иконками) или что нужно команде,
    /// названия на языке фраз. Пункт отправляет готовую фразу. Не открывает —
    /// это делает клик; проверки зовут пункты сами, не показывая список.
    /// </summary>
    public System.Windows.Controls.ContextMenu PhraseMenu(int i)
    {
        var s = AppSettings.Current;
        var menu = new System.Windows.Controls.ContextMenu { Style = (Style)FindResource("RoleMenuStyle") };
        var bar = ChatPhrases.Bar(s);
        if (i < 0 || i >= bar.Count) return menu;
        var b = bar[i];
        var lang = ChatPhrases.Lang(s);
        var itemStyle = (Style)FindResource("RoleMenuItemStyle");
        // Варианты кнопки: стандартные на языке фраз или свои. Иконка — у
        // ролей, которые остались стандартной ролью (переименованный «топчик»
        // — тоже топ); свой вариант без ключа — без иконки.
        foreach (var (choice, name) in ChatPhrases.Options(s, b))
        {
            var key = choice.Key;
            var header = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            if (b.Kind == ChatPhrases.Role && key.Length > 0
                && RoleIcons.Get(ChatPhrases.RolePositions[Array.IndexOf(ChatPhrases.Roles, key)]) is { } icon)
                header.Children.Add(new System.Windows.Controls.Image
                {
                    Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                });
            header.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = name, VerticalAlignment = System.Windows.VerticalAlignment.Center,
            });
            var item = new System.Windows.Controls.MenuItem { Header = header, Style = itemStyle, Tag = key.Length > 0 ? key : name };
            var text = ChatPhrases.Compose(lang, ChatPhrases.Text(s, b), name);
            item.ToolTip = text;
            item.Click += (_, _) => SendPhrase(i, text);
            menu.Items.Add(item);
        }
        return menu;
    }

    /// Фразу — в чат драфта: не чаще раза в PhraseCooldown, затем вспышка кнопки i.
    private async void SendPhrase(int i, string text)
    {
        if (ChatHandler is null || text.Length == 0) return;
        var now = DateTime.UtcNow;
        if (now < _phraseNext) return;
        _phraseNext = now + PhraseCooldown;

        var code = await ChatHandler(text);
        // Ответ мог прийти не в потоке окна (обработчик — сетевой запрос) —
        // вспышка трогает разметку, поэтому через Dispatcher.
        Dispatcher.Invoke(() =>
        {
            _phraseFlashText = text;
            Flash(i, code is >= 200 and < 300 ? "sent" : "fail");
        });
    }

    /// <summary>
    /// Вспышка кнопки i: 1,2 с «ушло», 2,5 с «не ушло». Гасит таймер окна, а не
    /// Task.Delay: продолжение после Task.Delay приходит в потоке окна, только
    /// если он сам ждёт в цикле сообщений, а иначе — в чужом, и разметка там
    /// падает. Новая вспышка отменяет гашение прошлой.
    /// </summary>
    private void Flash(int i, string state)
    {
        _phraseFlash = i;
        _phraseFlashState = state;
        _phraseFlashTimer?.Stop();
        if (PhraseBar.Visibility == Visibility.Visible) RenderPhrases();
        var timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(state == "fail" ? 2500 : 1200),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _phraseFlash = -1;
            _phraseFlashState = "";
            if (PhraseBar.Visibility == Visibility.Visible) RenderPhrases();
        };
        _phraseFlashTimer = timer;
        timer.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _phraseFlashTimer;

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
