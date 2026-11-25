using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно фраз: две части (слово владельца 25.11: «разделение должно быть не в
/// кнопке „редактировать“, а в основном окне редактирования фраз»). Сверху
/// галочка «Стандартные фразы» и справа их язык, под ними — сами фразы, как они
/// уйдут (с блоками на месте выбора); разделитель; снизу галочка «Свои фразы» —
/// с ней открывается правка кнопок: у каждой вид («Текст», «Роль»,
/// «Перечень»), текст и «убрать». У кнопки с выбором поле — с блоком, куда
/// встанет выбор (<see cref="SlotBox"/>), а под видом — «✎ Редактировать»:
/// варианты списка (<see cref="ChoicesWindow"/>). Внизу «+ Добавить кнопку» и
/// «Вернуть стандартные».
///
/// Один и тот же блок в двух местах — в окне с карандаша полосы фраз
/// (<see cref="PhrasesWindow"/>) и в разделе «Драфт» окна настроек. Пишет в
/// переданную копию настроек — в набор текущего аккаунта (ChatPhrases.Set) — и
/// зовёт <c>changed</c>; когда эта копия уйдёт в программу, решает окно:
/// настройки — по «Применить», окно фраз — по «Сохранить». Нужен словарь
/// стилей PoolUi в окне (тёмные поля и выпадающий список).
/// </summary>
public static class PhraseEditor
{
    private static readonly Color Text = Color.FromRgb(0xC9, 0xD2, 0xDC);
    internal static readonly Color Mute = Color.FromRgb(0x8A, 0xA0, 0xB2);
    internal static readonly Color Gold = Color.FromRgb(0xC8, 0x9B, 0x3C);

    /// Редактор на одной копии настроек: как перестроиться.
    private sealed class Ctx(AppSettings s, Action changed)
    {
        public AppSettings S { get; } = s;
        public Action Changed { get; } = changed;
        public Action Rebuild { get; set; } = () => { };
    }

    public static FrameworkElement Build(AppSettings s, Action changed)
    {
        var ctx = new Ctx(s, changed);
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        // Перестраивается целиком на любую смену: галочка, язык, состав кнопок,
        // вид, варианты. Набор текста не перестраивает — фокус остаётся в поле.
        ctx.Rebuild = () =>
        {
            root.Children.Clear();
            Standard(ctx, root);
            root.Children.Add(new Border
            {
                Height = 1, Margin = new Thickness(0, 10, 0, 12),
                Background = new SolidColorBrush(Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2)),
            });
            Own(ctx, root);
        };
        ctx.Rebuild();
        return root;
    }

    /// <summary>
    /// Верх: «Стандартные фразы», справа их язык, под ними — сами фразы с
    /// блоками. Язык — у стандартных: при своих выключен (свои кнопки,
    /// оставшиеся стандартными, идут за ним — сменить его можно, вернувшись к
    /// стандартным).
    /// </summary>
    private static void Standard(Ctx ctx, StackPanel root)
    {
        var s = ctx.S;
        var own = ChatPhrases.Own(s);
        var top = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Tick(Loc.T("phrases.stdTick"), !own, "std", () => SetOwn(ctx, false)));
        var langs = LangCombo(ChatPhrases.Lang(s), code =>
        {
            ChatPhrases.SetLang(s, code);
            ctx.Rebuild();
            ctx.Changed();
        });
        langs.Tag = "phraseLang";
        langs.IsEnabled = !own;
        langs.Opacity = own ? 0.45 : 1;
        Grid.SetColumn(langs, 1);
        top.Children.Add(langs);
        root.Children.Add(top);
        root.Children.Add(Hint(Loc.T("phrases.stdHint"), new Thickness(26, 0, 0, 6)));

        var list = new StackPanel { Margin = new Thickness(26, 0, 0, 0), Opacity = own ? 0.45 : 1 };
        var lang = ChatPhrases.Lang(s);
        foreach (var b in ChatPhrases.Defaults())
        {
            var line = SlotBox.Display(ChatPhrases.StdText(lang, b.Std), ChipName(b.Kind), Loc.T("phrases.slotTip"));
            line.FontFamily = SettingsWindow.Font("UiFont");
            line.FontSize = 12.5;
            line.Foreground = new SolidColorBrush(Text);
            line.Margin = new Thickness(0, 0, 0, 5);
            list.Children.Add(line);
        }
        root.Children.Add(list);
    }

    /// Низ: «Свои фразы» и, когда они выбраны, правка кнопок.
    private static void Own(Ctx ctx, StackPanel root)
    {
        var s = ctx.S;
        var own = ChatPhrases.Own(s);
        root.Children.Add(Tick(Loc.T("phrases.ownTick"), own, "own", () => SetOwn(ctx, true)));
        if (!own) return;
        root.Children.Add(Hint(Loc.T("phrases.ownHint"), new Thickness(26, 2, 0, 10)));

        var list = ChatPhrases.Buttons(s);
        for (int i = 0; i < list.Count; i++) root.Children.Add(Row(ctx, i, list[i]));
        root.Children.Add(Hint(Loc.T("phrases.slotHint"), new Thickness(0, 2, 0, 2)));
        root.Children.Add(Hint(Loc.T("phrases.fieldsHint"), new Thickness(0, 0, 0, 8)));

        var bottom = new DockPanel { LastChildFill = false };
        var add = PoolUi.Btn(Loc.T("phrases.add"));
        add.FontSize = 12;
        add.IsEnabled = list.Count < ChatPhrases.Max;
        add.Click += (_, _) => { ChatPhrases.Add(s); ctx.Rebuild(); ctx.Changed(); };
        var reset = PoolUi.Btn(Loc.T("phrases.reset"));
        reset.FontSize = 12;
        reset.Click += (_, _) => { ChatPhrases.Reset(s); ctx.Rebuild(); ctx.Changed(); };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);
        bottom.Children.Add(add);
        root.Children.Add(bottom);
    }

    private static void SetOwn(Ctx ctx, bool own)
    {
        if (ChatPhrases.Own(ctx.S) == own) return;
        ChatPhrases.SetOwn(ctx.S, own);
        ctx.Rebuild();
        ctx.Changed();
    }

    /// Подпись блока по виду: «роль» или «перечень».
    internal static string ChipName(string kind) =>
        Loc.T(kind == ChatPhrases.Role ? "phrases.chipRole" : "phrases.chipList");

    /// <summary>
    /// Строка кнопки: номер, вид, текст, «убрать». У кнопки с выбором поле — с
    /// блоком (<see cref="SlotBox"/>): текст пишется до него и после; под видом
    /// — «✎ Редактировать» (окно вариантов), справа — как фраза уйдёт в чат с
    /// первым вариантом («→ Топ, кого будешь брать?»).
    /// </summary>
    private static FrameworkElement Row(Ctx ctx, int i, ChatButton b)
    {
        var s = ctx.S;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), Tag = i };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = (i + 1).ToString(), FontFamily = SettingsWindow.Font("DisplayFont"),
            FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Gold),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var kind = Combo(ChatPhrases.Kinds.Select(k => (k, KindName(k))), b.Kind, k =>
        {
            ChatPhrases.SetKind(s, i, k);
            ctx.Changed();
            ctx.Rebuild();   // поле с блоком или без, «Редактировать» и «→ …»
        });
        kind.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(kind, 1);
        grid.Children.Add(kind);

        var preview = new TextBlock
        {
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11, Foreground = new SolidColorBrush(Mute),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 3, 0, 0),
        };
        // «→ …» читает кнопку заново: её правит поле, а запись b — снимок.
        void Refresh()
        {
            var cur = ChatPhrases.Buttons(s)[i];
            var first = ChatPhrases.Options(s, cur).Select(o => o.Name).FirstOrDefault();
            preview.Text = first is null ? "" : "→ " + ChatPhrases.Compose(ChatPhrases.Lang(s), ChatPhrases.Text(s, cur), first);
        }

        FrameworkElement field;
        if (ChatPhrases.IsChoice(b))
        {
            var slot = new SlotBox(ChipName(b.Kind),
                                   Loc.T("phrases.slotTip"), ChatPhrases.Text(s, b))
            {
                VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("phrases.field", i + 1),
            };
            slot.Box.Tag = i;   // PhrasesWindow.Fields() находит поле кнопки по номеру
            slot.Changed += text =>
            {
                ChatPhrases.SetText(s, i, text);
                ctx.Changed();
                Refresh();
            };
            field = slot;
        }
        else
        {
            var filling = false;   // поле заполняет код — это не правка человека
            var box = new TextBox
            {
                MaxLength = DraftChat.MaxLength, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
                AcceptsReturn = false, Tag = i, ToolTip = Loc.T("phrases.field", i + 1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            void Fill()
            {
                filling = true;
                box.Text = ChatPhrases.Text(s, ChatPhrases.Buttons(s)[i]);
                filling = false;
            }
            Fill();
            box.TextChanged += (_, _) =>
            {
                if (filling) return;
                ChatPhrases.SetText(s, i, box.Text);
                ctx.Changed();
            };
            // Поле стандартной кнопки очистили и ушли — в нём снова стандартная.
            box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
            field = box;
        }
        Grid.SetColumn(field, 2);
        grid.Children.Add(field);

        var remove = SmallX(Loc.T("phrases.remove"), () =>
        {
            ChatPhrases.Remove(s, i);
            ctx.Changed();
            ctx.Rebuild();
        });
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        if (ChatPhrases.IsChoice(b))
        {
            // «✎ Редактировать» — под выбором вида (слово владельца 25.11: «там,
            // где выпадающий список „что нужно“, внизу „редактировать“»). Окно
            // модальное: пока оно открыто, строки под ним не перестроятся и номер
            // кнопки не уедет.
            TextBlock edit = null!;
            edit = Link(Loc.T("phrases.editChoices"), () =>
            {
                var w = new ChoicesWindow(s, i, ctx.Changed) { Owner = Window.GetWindow(edit) };
                w.ShowDialog();
                ctx.Rebuild();   // «→ …» с новыми вариантами
            });
            edit.Tag = "editChoices";
            edit.Margin = new Thickness(2, 3, 6, 0);
            edit.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(edit, 1);
            Grid.SetColumn(edit, 1);
            grid.Children.Add(edit);

            Grid.SetRow(preview, 1);
            Grid.SetColumn(preview, 2);
            Grid.SetColumnSpan(preview, 2);
            grid.Children.Add(preview);
            Refresh();
        }
        return grid;
    }

    // ── Мелкие блоки (их же берёт окно вариантов) ────────────────────────────

    /// Список языков с флагами, как у языка программы.
    internal static ComboBox LangCombo(string value, Action<string> onSet)
    {
        var langs = new ComboBox
        {
            MinWidth = 190, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand,
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
        };
        foreach (var l in Loc.Languages)
            langs.Items.Add(new ComboBoxItem { Content = SettingsWindow.LangRow(l), Tag = l.Code });
        langs.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == value));
        langs.SelectionChanged += (_, _) =>
        {
            if (langs.SelectedItem is ComboBoxItem { Tag: string code } && code != value) { value = code; onSet(code); }
        };
        return langs;
    }

    private static ComboBox Combo(IEnumerable<(string Key, string Label)> options, string value, Action<string> onSet)
    {
        var combo = new ComboBox
        {
            Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand,
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (key, label) in options) combo.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        combo.SelectedIndex = combo.Items.OfType<ComboBoxItem>().ToList().FindIndex(x => (string)x.Tag == value);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string k } && k != value) onSet(k);
        };
        return combo;
    }

    internal static Button SmallX(string tip, Action onClick)
    {
        var x = new Button
        {
            Content = "✕", Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0), FontSize = 11, ToolTip = tip,
            Style = PoolUi.ButtonStyle, VerticalAlignment = VerticalAlignment.Center,
        };
        x.Click += (_, _) => onClick();
        return x;
    }

    /// Ссылка золотом: подчёркивание под курсором.
    internal static TextBlock Link(string text, Action onClick)
    {
        var t = new TextBlock
        {
            Text = text, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11, Cursor = Cursors.Hand,
            Foreground = new SolidColorBrush(Gold), TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.MouseEnter += (_, _) => t.TextDecorations = TextDecorations.Underline;
        t.MouseLeave += (_, _) => t.TextDecorations = null;
        t.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return t;
    }

    /// <summary>
    /// Галочка с подписью: щелчок по уже включённой ничего не делает — их две,
    /// и включена всегда ровно одна. Tag — для проверок (<see cref="Pick"/>).
    /// </summary>
    internal static FrameworkElement Tick(string label, bool on, string tag, Action onClick)
    {
        var box = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(on ? Gold : Color.FromRgb(0x5A, 0x6E, 0x82)),
            Background = new SolidColorBrush(on ? Color.FromArgb(0x33, 0xC8, 0x9B, 0x3C) : Colors.Transparent),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = on
                ? new TextBlock
                {
                    Text = "✓", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Gold),
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
            Foreground = new SolidColorBrush(on ? Colors.White : Mute), VerticalAlignment = VerticalAlignment.Center,
        });
        row.MouseLeftButtonDown += (_, e) => { e.Handled = true; onClick(); };
        return row;
    }

    /// Для проверок: щёлкнуть галочку окна с этим Tag («std», «own»).
    public static void Pick(DependencyObject root, string tag) =>
        PhrasesWindow.Find<StackPanel>(root).FirstOrDefault(p => (string?)p.Tag == tag)
            ?.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });

    internal static string KindName(string kind) => Loc.T(kind switch
    {
        ChatPhrases.Role => "phrases.kindRole",
        ChatPhrases.List => "phrases.kindList",
        _ => "phrases.kindText",
    });

    internal static TextBlock Hint(string text, Thickness margin) => new()
    {
        Text = text, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11,
        Foreground = new SolidColorBrush(Mute), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };
}
