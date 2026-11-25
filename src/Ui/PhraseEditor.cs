using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Правка кнопок фраз для чата драфта: язык фраз и кнопки — у каждой вид
/// (просто фраза, с выбором роли, с выбором «что нужно»), текст и «убрать»;
/// внизу «+ Добавить кнопку» и «Вернуть стандартные».
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

    public static FrameworkElement Build(AppSettings s, Action changed)
    {
        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        var rows = new StackPanel();
        var add = PoolUi.Btn(Loc.T("phrases.add"));

        // Строки перестраиваются целиком на любую смену состава: вид, добавить,
        // убрать, сброс, язык. Набор текста строки не перестраивает — фокус
        // остаётся в поле.
        void Rebuild()
        {
            rows.Children.Clear();
            var list = ChatPhrases.Buttons(s);
            for (int i = 0; i < list.Count; i++) rows.Children.Add(Row(s, i, list[i], Edited, Rebuild));
            add.IsEnabled = list.Count < ChatPhrases.Max;
        }
        void Edited() => changed();

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
            s.PhraseLang = code;   // свои фразы остаются, стандартные встают на новом языке
            Rebuild();
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
        add.Click += (_, _) => { ChatPhrases.Add(s); Rebuild(); changed(); };
        var reset = PoolUi.Btn(Loc.T("phrases.reset"));
        reset.FontSize = 12;
        reset.Click += (_, _) => { ChatPhrases.Reset(s); Rebuild(); changed(); };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);
        bottom.Children.Add(add);
        root.Children.Add(bottom);

        Rebuild();
        return root;
    }

    /// <summary>
    /// Строка кнопки: номер, вид, текст, «убрать»; под кнопкой с выбором —
    /// как фраза уйдёт в чат с первым вариантом («→ Топ, кого будешь брать?»),
    /// чтобы было видно, куда встаёт выбор.
    /// </summary>
    private static FrameworkElement Row(AppSettings s, int i, ChatButton b, Action changed, Action rebuild)
    {
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

        var kind = new ComboBox
        {
            Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand,
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var k in ChatPhrases.Kinds)
            kind.Items.Add(new ComboBoxItem { Content = KindName(k), Tag = k });
        kind.SelectedIndex = Array.IndexOf(ChatPhrases.Kinds, b.Kind);
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is not ComboBoxItem { Tag: string k }) return;
            ChatPhrases.SetKind(s, i, k);
            changed();
            rebuild();   // подсказка «→ …» появляется или пропадает
        };
        Grid.SetColumn(kind, 1);
        grid.Children.Add(kind);

        var preview = new TextBlock
        {
            FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11, Foreground = new SolidColorBrush(Mute),
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 3, 0, 0),
        };
        void ShowPreview(string text)
        {
            var lang = ChatPhrases.Lang(s);
            var first = ChatPhrases.Choices(b.Kind).FirstOrDefault();
            preview.Text = first is null ? "" : "→ " + ChatPhrases.Compose(lang, text, ChatPhrases.ChoiceName(lang, b.Kind, first));
            preview.Visibility = first is null ? Visibility.Collapsed : Visibility.Visible;
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
            ShowPreview(box.Text);
        }
        Fill();
        box.TextChanged += (_, _) =>
        {
            ShowPreview(box.Text);
            if (filling) return;
            ChatPhrases.SetText(s, i, box.Text);
            changed();
        };
        // Поле стандартной кнопки очистили и ушли — в нём снова стандартная.
        box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);

        var remove = new Button
        {
            Content = "✕", Width = 26, Height = 26, Margin = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(0), FontSize = 11, ToolTip = Loc.T("phrases.remove"),
            Style = PoolUi.ButtonStyle, VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => { ChatPhrases.Remove(s, i); changed(); rebuild(); };
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        Grid.SetRow(preview, 1);
        Grid.SetColumn(preview, 2);
        Grid.SetColumnSpan(preview, 2);
        grid.Children.Add(preview);
        return grid;
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
