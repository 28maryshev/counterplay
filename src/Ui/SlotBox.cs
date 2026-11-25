using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Counterplay;

/// <summary>
/// Поле фразы кнопки с выбором: текст и внутри него блок — место, куда в
/// драфте встанет выбранный вариант. Курсор ставится до блока или после, и
/// текст дописывается вокруг него (слово владельца 25.11: «в поле текста идёт
/// блок вставки… можешь нажать до этого блока или после и написать текст»).
///
/// Внутри — RichTextBox: обычное поле не умеет держать в тексте элемент. В
/// настройках текст хранится строкой, блок в ней — знак «…»
/// (<see cref="ChatPhrases.Slot"/>); набранный руками «…» становится «...»,
/// чтобы не стать вторым местом. Блок один: удалили — при уходе из поля он
/// встаёт в конец. Вставка — только простым текстом в одну строку.
/// </summary>
public sealed class SlotBox : Border
{
    private const string SlotTag = "slot";
    private static readonly Color Ink = Color.FromRgb(0xE6, 0xED, 0xF3);
    private static readonly Color Line = Color.FromRgb(0x35, 0x48, 0x5A);
    private static readonly Color Focus = Color.FromRgb(0x5A, 0x8A, 0xC8);

    private readonly RichTextBox _box;
    private readonly string _chip, _tip;
    private bool _filling;

    /// Текст поменял человек (не код): новый текст с «…» на месте блока.
    public event Action<string>? Changed;

    /// Само поле — для проверок (поставить курсор, вписать текст).
    public RichTextBox Box => _box;

    public SlotBox(string chip, string tip, string text)
    {
        _chip = chip;
        _tip = tip;
        CornerRadius = new CornerRadius(5);
        Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x18, 0x22));
        BorderBrush = new SolidColorBrush(Line);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(5, 4, 5, 4);

        _box = new RichTextBox
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Ink), CaretBrush = new SolidColorBrush(Ink),
            AcceptsReturn = false, AcceptsTab = false, Padding = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
        };
        _box.Document.PagePadding = new Thickness(0);
        SpellCheck.SetIsEnabled(_box, false);
        Child = _box;

        Text = text;
        _box.TextChanged += (_, _) => { if (!_filling) Changed?.Invoke(Text); };
        _box.PreviewKeyDown += OnKey;
        DataObject.AddPastingHandler(_box, OnPaste);
        _box.GotKeyboardFocus += (_, _) => BorderBrush = new SolidColorBrush(Focus);
        _box.LostKeyboardFocus += (_, _) =>
        {
            BorderBrush = new SolidColorBrush(Line);
            // Блок удалили — встаёт в конец: без него кнопке некуда вставить выбор.
            var now = Text;
            if (!now.Contains(ChatPhrases.Slot))
            {
                Text = (now.TrimEnd() + " " + ChatPhrases.Slot).TrimStart();
                Changed?.Invoke(Text);
            }
        };
    }

    /// Текст поля строкой: блок — «…».
    public string Text
    {
        get => Read(_box.Document);
        set
        {
            _filling = true;
            Write(value);
            _filling = false;
        }
    }

    private void Write(string template)
    {
        var at = template.IndexOf(ChatPhrases.Slot, StringComparison.Ordinal);
        var before = at < 0 ? (template.TrimEnd() + (template.Trim().Length > 0 ? " " : "")) : template[..at];
        var after = at < 0 ? "" : template[(at + ChatPhrases.Slot.Length)..].Replace(ChatPhrases.Slot, "...");
        var p = new Paragraph { Margin = new Thickness(0), LineHeight = 22 };
        if (before.Length > 0) p.Inlines.Add(new Run(before));
        p.Inlines.Add(Chip());
        p.Inlines.Add(new Run(after));
        _box.Document.Blocks.Clear();
        _box.Document.Blocks.Add(p);
    }

    /// Блок: золотая плашка с названием вида («что нужно», «роль»).
    private InlineUIContainer Chip()
    {
        var label = new TextBlock
        {
            Text = _chip, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11.5, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xD7, 0x7A)),
        };
        var plate = new Border
        {
            Child = label, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(1, 0, 1, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0xC8, 0x9B, 0x3C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x9B, 0x3C)),
            ToolTip = _tip,
        };
        // Элементы внутри редактируемого поля выключены — подсказка всё равно нужна.
        ToolTipService.SetShowOnDisabled(plate, true);
        return new InlineUIContainer(plate) { BaselineAlignment = BaselineAlignment.Center, Tag = SlotTag };
    }

    /// Строка из поля: текст как есть, блок — «…» (только первый), набранный «…» — «...».
    public static string Read(FlowDocument doc)
    {
        var sb = new StringBuilder();
        var slot = false;
        void Walk(Inline inline)
        {
            switch (inline)
            {
                case Run r: sb.Append(r.Text.Replace(ChatPhrases.Slot, "...")); break;
                case InlineUIContainer { Tag: SlotTag }:
                    if (!slot) { sb.Append(ChatPhrases.Slot); slot = true; }
                    break;
                case LineBreak: sb.Append(' '); break;
                case Span span: foreach (var child in span.Inlines) Walk(child); break;
            }
        }
        var first = true;
        foreach (var block in doc.Blocks.OfType<Paragraph>())
        {
            if (!first) sb.Append(' ');
            first = false;
            foreach (var inline in block.Inlines) Walk(inline);
        }
        return sb.ToString();
    }

    // Перевод строки в чате — отправка: Enter в поле ничего не делает.
    private static void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) e.Handled = true;
    }

    // Вставка — простым текстом в одну строку: оформление и переводы строк
    // из чужого текста полю не нужны, а копия блока стала бы вторым местом.
    private void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var clean = text.Replace("\r", " ").Replace("\n", " ").Replace(ChatPhrases.Slot, "...");
        _box.Selection.Text = clean;
        _box.CaretPosition = _box.Selection.End;
    }
}
