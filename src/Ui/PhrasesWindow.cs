using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Counterplay;

/// <summary>
/// Окно «Фразы для чата драфта» — открывает карандаш слева на полосе фраз
/// (OverlayWindow.Phrases). Внутри — общий редактор <see cref="PhraseEditor"/>:
/// язык фраз и кнопки. Правки уходят в программу по «Сохранить»; закрыть
/// крестиком — отказаться от них, как в окне настроек.
/// </summary>
public sealed class PhrasesWindow : Window
{
    private readonly AppSettings _draft = AppSettings.Current.Clone();
    private readonly System.Windows.Controls.Button _save;

    public PhrasesWindow()
    {
        Title = Loc.T("phrases.windowTitle");
        Width = 580;
        SizeToContent = SizeToContent.Height;
        // Поверх всего, как настройки: окно драфта по своей логике ныряет под
        // окно клиента и унесло бы это окно с собой.
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(PoolSettingsWindow.Bg);
        PoolUi.Apply(this);

        var body = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        body.Children.Add(PhraseEditor.Build(_draft, () => _save!.IsEnabled = true));

        _save = PoolUi.GoldBtn(Loc.T("phrases.save"));
        _save.IsEnabled = false;
        _save.HorizontalAlignment = HorizontalAlignment.Right;
        _save.Margin = new Thickness(0, 14, 0, 0);
        _save.Padding = new Thickness(18, 6, 18, 6);
        _save.Click += (_, _) => Save();
        body.Children.Add(_save);

        // Раскрытые списки своих вариантов растят окно; выше экрана — прокрутка.
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        var decorated = new Grid();
        decorated.Children.Add(PoolSettingsWindow.DecorBackground());
        decorated.Children.Add(new System.Windows.Controls.ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
        });
        Content = PoolUi.Chrome(this, Title, decorated);
    }

    /// Отдать правки программе: настройки сохранятся, окно драфта перерисует полосу.
    public void Save()
    {
        // Копию, а не сам _draft: редактор держит ссылку на него (см. SettingsWindow).
        // Остальное берём из живых настроек — пока окно было открыто, их могли
        // поменять, и откатывать чужие правки фразами нельзя.
        var next = AppSettings.Current.Clone();
        next.PhraseLang = _draft.PhraseLang;
        next.ChatButtons = ChatPhrases.Normalize(_draft.ChatButtons);   // пустые новые кнопки — прочь
        AppSettings.Apply(next);
        Close();
    }

    /// Для проверок: поля текста кнопок по порядку (у поля — номер кнопки).
    public IReadOnlyList<System.Windows.Controls.TextBox> Fields() =>
        Find<System.Windows.Controls.TextBox>(this).Where(t => t.Tag is int).ToList();

    /// Для проверок: поля раскрытых своих вариантов («choice:N»).
    public IReadOnlyList<System.Windows.Controls.TextBox> ChoiceFields() =>
        Find<System.Windows.Controls.TextBox>(this).Where(t => t.Tag is string tag && tag.StartsWith("choice:")).ToList();

    /// Для проверок: элементы окна по логическому дереву.
    public static IEnumerable<T> Find<T>(DependencyObject d) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(d).OfType<DependencyObject>())
        {
            if (child is T t) yield return t;
            foreach (var deeper in Find<T>(child)) yield return deeper;
        }
    }
}
