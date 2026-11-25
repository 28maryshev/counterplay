using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Правка кнопок фраз для чата драфта: язык фраз и кнопки — у каждой вид
/// (просто фраза, с выбором роли, с выбором «что нужно»), текст и «убрать».
/// У кнопки с выбором поле — с блоком, куда встанет выбор (<see cref="SlotBox"/>),
/// а под видом — «✎ Редактировать»: окно вариантов (<see cref="ChoicesWindow"/>).
/// Внизу «+ Добавить кнопку» и «Вернуть стандартные».
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
        var rows = new StackPanel();
        var add = PoolUi.Btn(Loc.T("phrases.add"));

        // Строки перестраиваются целиком на любую смену состава: вид, варианты,
        // добавить, убрать, сброс, язык. Набор текста строки не перестраивает —
        // фокус остаётся в поле.
        ctx.Rebuild = () =>
        {
            rows.Children.Clear();
            var list = ChatPhrases.Buttons(s);
            for (int i = 0; i < list.Count; i++) rows.Children.Add(Row(ctx, i, list[i]));
            add.IsEnabled = list.Count < ChatPhrases.Max;
        };

        // Язык фраз: список как у языка программы, с флагами.
        var langRow = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        langRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        langRow.Children.Add(new TextBlock
        {
            Text = Loc.T("phrases.lang"), FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
            Foreground = new SolidColorBrush(Text), VerticalAlignment = VerticalAlignment.Center,
        });
        var langs = LangCombo(ChatPhrases.Lang(s), code =>
        {
            ChatPhrases.SetLang(s, code);   // свои фразы и варианты остаются, стандартные встают на новом языке
            ctx.Rebuild();
            changed();
        });
        Grid.SetColumn(langs, 1);
        langRow.Children.Add(langs);
        root.Children.Add(langRow);
        root.Children.Add(Hint(Loc.T("phrases.langHint"), new Thickness(0, 0, 0, 10)));

        root.Children.Add(rows);
        root.Children.Add(Hint(Loc.T("phrases.slotHint"), new Thickness(0, 2, 0, 2)));
        root.Children.Add(Hint(Loc.T("phrases.fieldsHint"), new Thickness(0, 0, 0, 8)));

        var bottom = new DockPanel { LastChildFill = false };
        add.FontSize = 12;
        add.Click += (_, _) => { ChatPhrases.Add(s); ctx.Rebuild(); changed(); };
        var reset = PoolUi.Btn(Loc.T("phrases.reset"));
        reset.FontSize = 12;
        reset.Click += (_, _) => { ChatPhrases.Reset(s); ctx.Rebuild(); changed(); };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);
        bottom.Children.Add(add);
        root.Children.Add(bottom);

        ctx.Rebuild();
        return root;
    }

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
            preview.Text = first is null ? "" : "→ " + ChatPhrases.Compose(ChatPhrases.ChoiceLang(s, cur), ChatPhrases.Text(s, cur), first);
        }

        FrameworkElement field;
        if (ChatPhrases.IsChoice(b))
        {
            var slot = new SlotBox(Loc.T(b.Kind == ChatPhrases.Role ? "phrases.chipRole" : "phrases.chipNeed"),
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

    internal static string KindName(string kind) => Loc.T(kind switch
    {
        ChatPhrases.Role => "phrases.kindRole",
        ChatPhrases.Need => "phrases.kindNeed",
        _ => "phrases.kindText",
    });

    internal static TextBlock Hint(string text, Thickness margin) => new()
    {
        Text = text, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11,
        Foreground = new SolidColorBrush(Mute), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };
}
