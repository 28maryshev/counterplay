using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Окно «Варианты: Нам нужен …» — с «✎ Редактировать» под видом кнопки в
/// редакторе фраз (<see cref="PhraseEditor"/>): варианты списка кнопки —
/// роли или перечень. Поле на вариант, «✕», «+ Вариант», «Вернуть
/// стандартные». Сначала в полях стандартные варианты на языке фраз; первая
/// же правка делает список своим — копией стандартных с этой правкой (слово
/// владельца 25.11: «выбираются такие же стандартные, которые можно
/// редактировать»). Деления «стандартные / свои» здесь нет: оно в главном окне
/// фраз (владелец: «разделение должно быть не в кнопке „редактировать“»).
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
        Width = 420;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;
        Topmost = true;   // окно фраз и настройки — поверх всего, это — над ними
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        PoolUi.Apply(this);

        var reset = PoolUi.Btn(Loc.T("phrases.reset"));
        reset.FontSize = 12;
        reset.Click += (_, _) =>
        {
            ChatPhrases.SetOwnChoices(_s, _i, false);
            _changed();
            Build();
        };
        var done = PoolUi.GoldBtn(Loc.T("phrases.done"));
        done.Padding = new Thickness(18, 6, 18, 6);
        done.Click += (_, _) => Close();
        var bottom = new DockPanel { LastChildFill = false, Margin = new Thickness(18, 6, 18, 14) };
        DockPanel.SetDock(done, Dock.Right);
        bottom.Children.Add(done);
        bottom.Children.Add(reset);

        var layout = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);
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

    /// Перестроить список: на «✕», «+ Вариант», «Вернуть стандартные».
    private void Build()
    {
        _body.Children.Clear();
        _body.Children.Add(PhraseEditor.Hint(Loc.T("phrases.choicesHint"), new Thickness(0, 0, 0, 8)));
        var items = ChatPhrases.ChoiceItems(Button);
        for (int n = 0; n < items.Count; n++) _body.Children.Add(Row(n, items[n]));
        var add = PoolUi.Btn(Loc.T("phrases.addChoice"));
        add.FontSize = 11;
        add.Padding = new Thickness(10, 3, 10, 3);
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(24, 2, 0, 0);
        add.IsEnabled = items.Count < ChatPhrases.MaxChoices;
        add.Click += (_, _) => { MakeOwn(); ChatPhrases.AddChoice(_s, _i); _changed(); Build(); };
        _body.Children.Add(add);
    }

    /// <summary>
    /// Вариант: иконка роли (и у переименованного — «топчик» остаётся топом),
    /// поле, «✕». Поле стандартного очистили и ушли — в нём снова стандартное
    /// название.
    /// </summary>
    private FrameworkElement Row(int j, ChoiceItem c)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
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
            MakeOwn();
            ChatPhrases.SetChoiceText(_s, _i, j, box.Text);
            _changed();
        };
        box.LostKeyboardFocus += (_, _) => { if (box.Text.Trim().Length == 0) Fill(); };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        var remove = PhraseEditor.SmallX(Loc.T("phrases.removeChoice"), () =>
        {
            MakeOwn();
            ChatPhrases.RemoveChoice(_s, _i, j);
            _changed();
            Build();
        });
        Grid.SetColumn(remove, 2);
        row.Children.Add(remove);
        return row;
    }

    // Первая правка стандартных: список становится своим — копией стандартных
    // (те же названия на языке фраз, тот же порядок), и правка ложится на неё.
    private void MakeOwn()
    {
        if (!ChatPhrases.OwnChoices(Button)) ChatPhrases.SetOwnChoices(_s, _i, true);
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

    /// Для проверок: поля вариантов по порядку («choice:N»).
    public IReadOnlyList<TextBox> Fields() =>
        PhrasesWindow.Find<TextBox>(this).Where(t => t.Tag is string tag && tag.StartsWith("choice:")).ToList();
}
