using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Border = System.Windows.Controls.Border;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно «Варианты: Нам нужен …» — с «✎ Редактировать» под видом кнопки в
/// редакторе фраз (<see cref="PhraseEditor"/>). Две части (слово владельца
/// 25.11): сверху галочка «Стандартные варианты» и справа язык этих вариантов,
/// под ними — сами варианты; разделитель; снизу галочка «Свои варианты» — с
/// ней открывается их правка: копия стандартных, которую переименовывают,
/// убирают и дополняют. Галочки взаимоисключающие.
///
/// Правит ту же копию настроек, что и редактор, — кнопку i набора текущего
/// аккаунта; в программу правки уходят вместе с остальными («Сохранить» окна
/// фраз или «Применить» настроек). Открывается модально: пока оно открыто,
/// строки редактора под ним не перестроятся и номер кнопки не уедет.
/// </summary>
public sealed class ChoicesWindow : Window
{
    private readonly AppSettings _s;
    private readonly int _i;
    private readonly Action _changed;
    private readonly StackPanel _body = new() { Margin = new Thickness(18, 14, 18, 8) };

    public ChoicesWindow(AppSettings s, int i, Action changed)
    {
        _s = s;
        _i = i;
        _changed = changed;
        Title = Loc.T("phrases.choicesTitle", ChatPhrases.Text(s, Button));
        Width = 460;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        Topmost = true;   // окно фраз и настройки — поверх всего, это — над ними
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        PoolUi.Apply(this);

        var done = PoolUi.GoldBtn(Loc.T("phrases.done"));
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 6, 18, 14);
        done.Padding = new Thickness(18, 6, 18, 6);
        done.Click += (_, _) => Close();

        var layout = new DockPanel();
        DockPanel.SetDock(done, Dock.Bottom);
        layout.Children.Add(done);
        layout.Children.Add(new ScrollViewer
        {
            Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });
        var decorated = new Grid();
        decorated.Children.Add(PoolSettingsWindow.DecorBackground());
        decorated.Children.Add(layout);
        Content = PoolUi.Chrome(this, Title, decorated);
        Build();
    }

    private ChatButton Button => ChatPhrases.Buttons(_s)[_i];

    /// Перестроить обе части: на смену галочки, языка, состава своих вариантов.
    private void Build()
    {
        _body.Children.Clear();
        var b = Button;
        var own = ChatPhrases.OwnChoices(b);

        // ── Верх: стандартные варианты и их язык ─────────────────────────────
        var top = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var std = Tick(Loc.T("phrases.choicesStd"), !own, "std", () => Switch(false));
        top.Children.Add(std);
        var lang = PhraseEditor.LangCombo(ChatPhrases.ChoiceLang(_s, b), code =>
        {
            ChatPhrases.SetChoiceLang(_s, _i, code);
            _changed();
            Build();
        });
        lang.MinWidth = 170;
        lang.IsEnabled = !own;   // у своих вариантов язык уже не при чём
        lang.Opacity = own ? 0.45 : 1;
        lang.Tag = "choiceLang";
        Grid.SetColumn(lang, 1);
        top.Children.Add(lang);
        _body.Children.Add(top);
        _body.Children.Add(PhraseEditor.Hint(Loc.T("phrases.stdHint"), new Thickness(26, 0, 0, 6)));

        // Сами стандартные — на выбранном языке; у ролей — иконки.
        var names = new WrapPanel { Margin = new Thickness(26, 0, 0, 4), Opacity = own ? 0.45 : 1 };
        foreach (var key in ChatPhrases.StdChoices(b.Kind))
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 6) };
            if (RoleIcon(b, key) is { } icon) chip.Children.Add(icon);
            chip.Children.Add(new TextBlock
            {
                Text = ChatPhrases.ChoiceName(ChatPhrases.ChoiceLang(_s, b), b.Kind, key),
                FontFamily = SettingsWindow.Font("UiFont"), FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xD2, 0xDC)), VerticalAlignment = VerticalAlignment.Center,
            });
            names.Children.Add(chip);
        }
        _body.Children.Add(names);

        _body.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 8, 0, 12),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2)),
        });

        // ── Низ: свои варианты ───────────────────────────────────────────────
        _body.Children.Add(Tick(Loc.T("phrases.choicesOwn"), own, "own", () => Switch(true)));
        if (!own) return;
        _body.Children.Add(PhraseEditor.Hint(Loc.T("phrases.choicesHint"), new Thickness(26, 4, 0, 8)));
        var items = ChatPhrases.ChoiceItems(b);
        for (int n = 0; n < items.Count; n++) _body.Children.Add(OwnRow(n, items[n]));
        var add = PoolUi.Btn(Loc.T("phrases.addChoice"));
        add.FontSize = 11;
        add.Padding = new Thickness(10, 3, 10, 3);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(50, 2, 0, 0);
        add.IsEnabled = items.Count < ChatPhrases.MaxChoices;
        add.Click += (_, _) => { ChatPhrases.AddChoice(_s, _i); _changed(); Build(); };
        _body.Children.Add(add);
    }

    /// <summary>
    /// Свой вариант: иконка роли (и у переименованного — «топчик» остаётся
    /// топом), поле, «✕». Поле стандартного очистили и ушли — в нём снова
    /// стандартное название.
    /// </summary>
    private FrameworkElement OwnRow(int j, ChoiceItem c)
    {
        var row = new Grid { Margin = new Thickness(26, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (RoleIcon(Button, c.Key) is { } icon) row.Children.Add(icon);

        var filling = false;
        var box = new TextBox
        {
            MaxLength = 40, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 12,
            Padding = new Thickness(6, 3, 6, 3), Tag = $"choice:{j}",
        };
        void Fill()
        {
            filling = true;
            box.Text = ChatPhrases.Name(_s, Button, ChatPhrases.ChoiceItems(Button)[j]);
            filling = false;
        }
        Fill();
        box.TextChanged += (_, _) =>
        {
            if (filling) return;
            ChatPhrases.SetChoiceText(_s, _i, j, box.Text);
            _changed();
        };
        box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var remove = PhraseEditor.SmallX(Loc.T("phrases.removeChoice"), () =>
        {
            ChatPhrases.RemoveChoice(_s, _i, j);
            _changed();
            Build();
        });
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    private void Switch(bool own)
    {
        if (ChatPhrases.OwnChoices(Button) == own) return;
        ChatPhrases.SetOwnChoices(_s, _i, own);   // свои — копия стандартных на языке вариантов
        _changed();
        Build();
    }

    /// Галочка с подписью: щелчок по уже включённой ничего не делает — их две,
    /// и включена всегда ровно одна.
    private static FrameworkElement Tick(string label, bool on, string tag, Action onClick)
    {
        var box = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(on ? PhraseEditor.Gold : Color.FromRgb(0x5A, 0x6E, 0x82)),
            Background = new SolidColorBrush(on ? Color.FromArgb(0x33, 0xC8, 0x9B, 0x3C) : Colors.Transparent),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = on
                ? new TextBlock
                {
                    Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(PhraseEditor.Gold),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }
                : null,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Cursor = Cursors.Hand, Background = Brushes.Transparent,
            VerticalAlignment = VerticalAlignment.Center, Tag = tag,
        };
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = label, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(on ? Colors.White : PhraseEditor.Mute), VerticalAlignment = VerticalAlignment.Center,
        });
        row.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return row;
    }

    private static Image? RoleIcon(ChatButton b, string key)
    {
        if (b.Kind != ChatPhrases.Role || key.Length == 0) return null;
        var at = Array.IndexOf(ChatPhrases.Roles, key);
        if (at < 0 || RoleIcons.Get(ChatPhrases.RolePositions[at]) is not { } src) return null;
        return new Image
        {
            Source = src, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // ── Для проверок ─────────────────────────────────────────────────────────

    /// Поля своих вариантов по порядку («choice:N»).
    public IReadOnlyList<TextBox> Fields() =>
        PhrasesWindow.Find<TextBox>(this).Where(t => t.Tag is string tag && tag.StartsWith("choice:")).ToList();

    /// Щёлкнуть галочку: «std» — стандартные, «own» — свои.
    public void Pick(string tag) =>
        PhrasesWindow.Find<StackPanel>(this).FirstOrDefault(p => (string?)p.Tag == tag)
            ?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = MouseLeftButtonDownEvent });
}
