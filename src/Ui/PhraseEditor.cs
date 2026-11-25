using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Правка фраз для чата драфта: язык фраз, пять полей и «Вернуть стандартные».
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
        var boxes = new TextBox[ChatPhrases.Count];
        var filling = false;   // поля заполняет код — это не правка человека

        // Поле без своей фразы показывает стандартную на текущем языке фраз.
        void Fill(int i)
        {
            filling = true;
            boxes[i].Text = ChatPhrases.Text(s, i);
            filling = false;
        }

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
            s.PhraseLang = code;
            // Свои фразы остаются, стандартные встают на новом языке.
            for (int i = 0; i < boxes.Length; i++)
                if (ChatPhrases.Custom(s, i).Length == 0) Fill(i);
            changed();
        };
        Grid.SetColumn(langs, 1);
        langRow.Children.Add(langs);
        root.Children.Add(langRow);
        root.Children.Add(Hint(Loc.T("phrases.langHint"), new Thickness(0, 0, 0, 10)));

        for (int n = 0; n < ChatPhrases.Count; n++)
        {
            var i = n;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = (i + 1).ToString(), FontFamily = SettingsWindow.Font("DisplayFont"),
                FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Gold),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new TextBox
            {
                MaxLength = DraftChat.MaxLength, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 13,
                AcceptsReturn = false, Tag = i,
                ToolTip = Loc.T("phrases.field", i + 1),
            };
            boxes[i] = box;
            Fill(i);
            box.TextChanged += (_, _) =>
            {
                if (filling) return;
                ChatPhrases.Set(s, i, box.Text);
                changed();
            };
            // Поле очистили и ушли из него — показываем, что вернулась стандартная.
            box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(i); };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            root.Children.Add(row);
        }

        var bottom = new DockPanel { Margin = new Thickness(22, 2, 0, 0), LastChildFill = true };
        var reset = PoolUi.Btn(Loc.T("phrases.reset"));
        reset.FontSize = 12;
        reset.Click += (_, _) =>
        {
            s.Phrases = ChatPhrases.Normalize(null);
            for (int i = 0; i < boxes.Length; i++) Fill(i);
            changed();
        };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Add(reset);
        var fieldsHint = Hint(Loc.T("phrases.fieldsHint"), new Thickness(0, 0, 10, 0));
        fieldsHint.VerticalAlignment = VerticalAlignment.Center;
        bottom.Children.Add(fieldsHint);
        root.Children.Add(bottom);
        return root;
    }

    private static TextBlock Hint(string text, Thickness margin) => new()
    {
        Text = text, FontFamily = SettingsWindow.Font("UiFont"), FontSize = 11,
        Foreground = new SolidColorBrush(Mute), TextWrapping = TextWrapping.Wrap, Margin = margin,
    };
}
