using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Правка кнопок фраз для чата драфта: язык фраз и кнопки — у каждой вид
/// (просто фраза, с выбором роли, с выбором «что нужно»), текст и «убрать»; у
/// кнопки с выбором — стандартные варианты или свои (копия стандартных, которую
/// правят, дополняют и сокращают). Внизу «+ Добавить кнопку» и «Вернуть
/// стандартные».
///
/// Один и тот же блок в двух местах — в окне с карандаша полосы фраз
/// (<see cref="PhrasesWindow"/>) и в разделе «Драфт» окна настроек. Пишет в
/// переданную копию настроек и зовёт <c>changed</c>; когда эта копия уйдёт в
/// программу, решает окно: настройки — по «Применить», окно фраз — по «Сохранить».
/// Нужен словарь стилей PoolUi в окне (тёмные поля и выпадающий список).
/// </summary>
public static class PhraseEditor
{
    private static readonly Color Text = Color.FromRgb(0xC9, 0xD2, 0xDC);
    private static readonly Color Mute = Color.FromRgb(0x8A, 0xA0, 0xB2);
    private static readonly Color Gold = Color.FromRgb(0xC8, 0x9B, 0x3C);

    /// Редактор на одной копии настроек: что раскрыто и как перестроиться.
    private sealed class Ctx(AppSettings s, Action changed)
    {
        public AppSettings S { get; } = s;
        public Action Changed { get; } = changed;
        public Action Rebuild { get; set; } = () => { };
        /// Кнопки, у которых раскрыт список своих вариантов.
        public HashSet<int> Open { get; } = [];
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
        var langs = new ComboBox
        {
            MinWidth = 190, Padding = new Thickness(8, 4, 8, 4), Cursor = Cursors.Hand,
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
        };
        foreach (var l in Loc.Languages)
            langs.Items.Add(new ComboBoxItem { Content = SettingsWindow.LangRow(l), Tag = l.Code });
        langs.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == ChatPhrases.Lang(s)));
        langs.SelectionChanged += (_, _) =>
        {
            if (langs.SelectedItem is not ComboBoxItem { Tag: string code } || code == ChatPhrases.Lang(s)) return;
            s.PhraseLang = code;   // свои фразы и варианты остаются, стандартные встают на новом языке
            ctx.Rebuild();
            changed();
        };
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
        reset.Click += (_, _) => { ChatPhrases.Reset(s); ctx.Open.Clear(); ctx.Rebuild(); changed(); };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);
        bottom.Children.Add(add);
        root.Children.Add(bottom);

        ctx.Rebuild();
        return root;
    }

    /// <summary>
    /// Строка кнопки: номер, вид, текст, «убрать». Под кнопкой с выбором — как
    /// фраза уйдёт в чат с первым вариантом («→ Топ, кого будешь брать?»), чтобы
    /// было видно, куда встаёт выбор, и «Стандартные варианты / Свои варианты».
    /// У своих — сводка «топчик, лес, мид… ✎», под ней по щелчку — их список.
    /// </summary>
    private static FrameworkElement Row(Ctx ctx, int i, ChatButton b)
    {
        var s = ctx.S;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), Tag = i };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(new TextBlock
        {
            Text = (i + 1).ToString(), FontFamily = SettingsWindow.Font("DisplayFont"),
            FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Gold),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var kind = Combo(ChatPhrases.Kinds.Select(k => (k, KindName(k))), b.Kind, k =>
        {
            ChatPhrases.SetKind(s, i, k);
            ctx.Open.Remove(i);
            ctx.Changed();
            ctx.Rebuild();   // подсказка и варианты появляются или пропадают
        });
        kind.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(kind, 1);
        grid.Children.Add(kind);

        var preview = new TextBlock
        {
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11, Foreground = new SolidColorBrush(Mute),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Сводка своих вариантов и подсказка «→ …» читают кнопку заново: её
        // правят и поле текста, и поля вариантов, а запись b — снимок.
        var summary = Link("", () =>
        {
            if (!ctx.Open.Remove(i)) ctx.Open.Add(i);
            ctx.Rebuild();
        });
        summary.Tag = "choices";
        void Refresh()
        {
            var cur = ChatPhrases.Buttons(s)[i];
            var lang = ChatPhrases.Lang(s);
            var first = ChatPhrases.Options(s, cur).Select(o => o.Name).FirstOrDefault();
            preview.Text = first is null ? "" : "→ " + ChatPhrases.Compose(lang, ChatPhrases.Text(s, cur), first);
            var names = string.Join(", ", ChatPhrases.Options(s, cur).Select(o => o.Name));
            summary.Text = (names.Length > 0 ? names : "—") + (ctx.Open.Contains(i) ? "  ▴" : "  ✎");
        }

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
            if (!filling) { ChatPhrases.SetText(s, i, box.Text); ctx.Changed(); }
            Refresh();
        };
        // Поле стандартной кнопки очистили и ушли — в нём снова стандартная.
        box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);

        var remove = SmallX(Loc.T("phrases.remove"), () =>
        {
            ChatPhrases.Remove(s, i);
            // Номера строк ниже сдвинулись — раскрытые сдвигаются с ними.
            var open = ctx.Open.Where(x => x != i).Select(x => x > i ? x - 1 : x).ToList();
            ctx.Open.Clear();
            ctx.Open.UnionWith(open);
            ctx.Changed();
            ctx.Rebuild();
        });
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        if (ChatPhrases.IsChoice(b))
        {
            // Строка 2: «→ …» и стандартные или свои варианты.
            var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0), LastChildFill = true };
            var mode = Combo([("std", Loc.T("phrases.choicesStd")), ("own", Loc.T("phrases.choicesOwn"))],
                ChatPhrases.OwnChoices(b) ? "own" : "std", m =>
                {
                    ChatPhrases.SetOwnChoices(s, i, m == "own");
                    if (m == "own") ctx.Open.Add(i); else ctx.Open.Remove(i);   // свои — сразу раскрыты
                    ctx.Changed();
                    ctx.Rebuild();
                });
            mode.FontSize = 11;
            mode.Tag = "choiceMode";
            DockPanel.SetDock(mode, Dock.Right);
            line.Children.Add(mode);
            line.Children.Add(preview);
            Grid.SetRow(line, 1);
            Grid.SetColumn(line, 2);
            Grid.SetColumnSpan(line, 2);
            grid.Children.Add(line);

            if (ChatPhrases.OwnChoices(b))
            {
                summary.Margin = new Thickness(2, 4, 0, 0);
                Grid.SetRow(summary, 2);
                Grid.SetColumn(summary, 2);
                Grid.SetColumnSpan(summary, 2);
                grid.Children.Add(summary);
                if (ctx.Open.Contains(i))
                {
                    var panel = ChoicesPanel(ctx, i, b, Refresh);
                    Grid.SetRow(panel, 3);
                    Grid.SetColumn(panel, 2);
                    Grid.SetColumnSpan(panel, 2);
                    grid.Children.Add(panel);
                }
            }
        }
        Refresh();
        return grid;
    }

    /// <summary>
    /// Свои варианты кнопки i: поле на вариант (у роли — её иконка; у
    /// переименованной тоже — «топчик» остаётся топом), «✕» и «+ Вариант».
    /// Поле стандартного варианта очистили и ушли — в нём снова стандартное
    /// название на языке фраз.
    /// </summary>
    private static FrameworkElement ChoicesPanel(Ctx ctx, int i, ChatButton b, Action refresh)
    {
        var s = ctx.S;
        var stack = new StackPanel();
        stack.Children.Add(Hint(Loc.T("phrases.choicesHint"), new Thickness(0, 0, 0, 6)));
        var items = ChatPhrases.ChoiceItems(b);
        for (int n = 0; n < items.Count; n++)
        {
            var j = n;
            var c = items[j];
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (b.Kind == ChatPhrases.Role && c.Key.Length > 0
                && RoleIcons.Get(ChatPhrases.RolePositions[Array.IndexOf(ChatPhrases.Roles, c.Key)]) is { } icon)
                row.Children.Add(new Image
                {
                    Source = icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                });

            var filling = false;
            var box = new TextBox
            {
                MaxLength = 40, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 12,
                Padding = new Thickness(6, 3, 6, 3), Tag = $"choice:{j}",
            };
            void Fill()
            {
                filling = true;
                box.Text = ChatPhrases.Name(s, b.Kind, ChatPhrases.ChoiceItems(ChatPhrases.Buttons(s)[i])[j]);
                filling = false;
            }
            Fill();
            box.TextChanged += (_, _) =>
            {
                if (filling) return;
                ChatPhrases.SetChoiceText(s, i, j, box.Text);
                ctx.Changed();
                refresh();
            };
            box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            var remove = SmallX(Loc.T("phrases.removeChoice"), () =>
            {
                ChatPhrases.RemoveChoice(s, i, j);
                ctx.Changed();
                ctx.Rebuild();
            });
            Grid.SetColumn(remove, 2);
            row.Children.Add(remove);
            stack.Children.Add(row);
        }

        var add = PoolUi.Btn(Loc.T("phrases.addChoice"));
        add.FontSize = 11;
        add.Padding = new Thickness(10, 3, 10, 3);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(24, 2, 0, 0);
        add.IsEnabled = items.Count < ChatPhrases.MaxChoices;
        add.Click += (_, _) => { ChatPhrases.AddChoice(s, i); ctx.Changed(); ctx.Rebuild(); };
        stack.Children.Add(add);

        return new Border
        {
            Child = stack, Margin = new Thickness(0, 6, 0, 2), Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xC8, 0x9B, 0x3C)),
        };
    }

    // ── Мелкие блоки ─────────────────────────────────────────────────────────

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

    private static Button SmallX(string tip, Action onClick)
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
    private static TextBlock Link(string text, Action onClick)
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

    private static string KindName(string kind) => Loc.T(kind switch
    {
        ChatPhrases.Role => "phrases.kindRole",
        ChatPhrases.Need => "phrases.kindNeed",
        _ => "phrases.kindText",
    });

    private static TextBlock Hint(string text, Thickness margin) => new()
    {
        Text = text, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11,
        Foreground = new SolidColorBrush(Mute), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };
}
