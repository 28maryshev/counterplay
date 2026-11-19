using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using System.Windows.Media.Imaging;

namespace Counterplay;

/// <summary>
/// Настройки интерфейса: что показывать на каждом экране программы.
/// Разделы совпадают с экранами (ожидание / драфт / баны), чтобы искать
/// переключатель там же, где видишь мешающий блок.
///
/// Правки копятся в копии настроек и уходят в оверлей по кнопке «Применить»:
/// перерисовка драфта — не бесплатная операция, а щёлкать тумблерами человек
/// может подряд. Закрыть окно, не нажав «Применить», = отказаться от правок.
/// </summary>
sealed partial class SettingsWindow : Window
{
    private static readonly Color Bg   = Color.FromRgb(0x0E, 0x14, 0x1D);
    private static readonly Color Gold = Color.FromRgb(0xC8, 0x9B, 0x3C);
    private static readonly Color Text = Color.FromRgb(0xC9, 0xD2, 0xDC);
    private static readonly Color Mute = Color.FromRgb(0x8A, 0xA0, 0xB2);

    /// Шрифты объявлены в ресурсах ОКНА оверлея, а не приложения, поэтому
    /// Application.Current.FindResource на них падает (и роняет программу при
    /// открытии настроек). Ищем по открытым окнам, а если не нашли — системный.
    private static FontFamily Font(string key)
    {
        if (Application.Current is { } app)
        {
            if (app.TryFindResource(key) is FontFamily appFont) return appFont;
            foreach (Window w in app.Windows)
                if (w.TryFindResource(key) is FontFamily winFont) return winFont;
        }
        return new FontFamily("Segoe UI");
    }

    public SettingsWindow()
    {
        Title = Loc.T("settings.title");
        Width = 560; Height = 640;
        // Системная белая рамка выбивалась из тёмного интерфейса — рисуем свою,
        // как у оверлея: заголовок с крестиком, перетаскивание за шапку.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Background = System.Windows.Media.Brushes.Transparent;
        // Поверх всего: оверлей по своей логике ныряет под окно клиента, а
        // настройки — его дочернее окно и уходили вниз вместе с ним.
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PoolUi.Apply(this);

        _body = new ContentControl { Content = Build() };
        Content = Shell();
        Closed += (_, _) => HidePreview();
    }

    // Код страны для флага. Язык и страна совпадают не всегда: португальский
    // ведём по Бразилии (там аудитория), английский — по Великобритании,
    // украинский — по Украине, а не по коду языка.
    private static readonly Dictionary<string, string> FlagOf = new()
    {
        ["en"] = "GB", ["es"] = "ES", ["pt"] = "BR", ["de"] = "DE", ["fr"] = "FR",
        ["it"] = "IT", ["pl"] = "PL", ["ru"] = "RU", ["uk"] = "UA", ["tr"] = "TR",
        ["vi"] = "VN", ["th"] = "TH", ["ja"] = "JP", ["ko"] = "KR", ["zh"] = "CN",
    };

    /// Флаг картинкой из ресурсов (assets/flags/de.png). Эмодзи-флаги не годятся:
    /// Windows их не рисует — вместо флага показывает две буквы в рамке.
    private static ImageSource? FlagImage(string code)
    {
        if (!FlagOf.TryGetValue(code, out var cc)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri($"pack://application:,,,/assets/flags/{cc.ToLowerInvariant()}.png");
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }   // ресурса нет — обойдёмся одним названием языка
    }

    /// Строка списка языков: флаг + родное название. Флаг — чтобы человек,
    /// случайно переключившийся на незнакомый язык, нашёл свой по картинке,
    /// а не по надписи, которую он не может прочитать.
    private static UIElement LangRow(Loc.Lang l)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (FlagImage(l.Code) is { } flag)
            row.Children.Add(new Border
            {
                Width = 20, Height = 14, CornerRadius = new CornerRadius(2),
                ClipToBounds = true, Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Background = new ImageBrush(flag) { Stretch = Stretch.UniformToFill }
            });
        row.Children.Add(new TextBlock
        {
            Text = l.Native, FontFamily = Font("UiFont"), FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    private ContentControl _body = null!;

    /// Рамка окна в стиле программы: тёмный фон, золотая шапка, свой крестик.
    private UIElement Shell()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var header = new Grid { Height = 38, Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)) };
        header.MouseLeftButtonDown += (_, e) =>
        { if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove(); };

        var title = new TextBlock
        {
            Text = Loc.T("settings.title"),
            FontFamily = Font("DisplayFont"), FontSize = 14, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Gold),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        header.Children.Add(title);

        var close = new Button
        {
            Content = "✕", Width = 40,
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Gold),
            FontSize = 13, Cursor = Cursors.Hand
        };
        close.Click += (_, _) => Close();
        header.Children.Add(close);
        grid.Children.Add(header);

        Grid.SetRow(_body, 1);
        grid.Children.Add(_body);

        return new Border
        {
            Background = new SolidColorBrush(Bg),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xC8, 0x9B, 0x3C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = grid
        };
    }

    private AppSettings _draft = AppSettings.Current.Clone();
    private Button? _apply;
    private bool _dirty;

    private void MarkDirty()
    {
        _dirty = true;
        if (_apply is null) return;
        _apply.IsEnabled = true;
        _apply.Opacity = 1.0;
    }

    /// Всё содержимое окна. Что в каком разделе и подразделе — в части
    /// SettingsWindow.Sections, по методу на раздел.
    private UIElement Build()
    {
        var s = _draft;
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        AddSyncAndLanguage(body);
        AddProfile(body, s);
        AddDraft(body, s);
        AddPlacement(body, s);
        AddBans(body, s);
        AddGeneral(body, s);
        AddButtons(body);
        return new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    /// Выбор из нескольких значений с картинкой на каждый (SettingsWindow.Preview).
    private UIElement PreviewedChoice(
        string key, string title, string hint, (string Key, string Label)[] options,
        string value, Action<string> onSet) =>
        Previewed(Choice(title, hint, options, value, onSet), key, options);

    private static Button ActionButton(string text, string fg, string border) => new()
    {
        Content = text,
        Margin = new Thickness(0, 0, 10, 0),
        Padding = new Thickness(16, 7, 16, 7),
        Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFrom(border)!,
        Foreground = (SolidColorBrush)new BrushConverter().ConvertFrom(fg)!,
        FontWeight = FontWeights.Bold,
        Cursor = Cursors.Hand
    };

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontFamily = Font("DisplayFont"),
        FontSize = 15, FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Gold),
        Margin = new Thickness(0, 18, 0, 6)
    };

    /// Окно скрытых чемпионов. Через окно драфта, если оно есть: вернули
    /// чемпиона — оно пересчитает советы. Поверх настроек: они Topmost, и
    /// окно, принадлежащее драфту, ушло бы под них.
    private void OpenHidden()
    {
        var w = HiddenWindow();
        if (w.IsVisible) w.Activate(); else w.Show();
    }

    /// Окно скрытых, готовое к показу, но не показанное: дымовая проверка
    /// показывает его сама, невидимым (Smoke, шаг «окно настроек»).
    internal DislikesWindow HiddenWindow()
    {
        var overlay = Owner as OverlayWindow
                      ?? Application.Current?.Windows.OfType<OverlayWindow>().FirstOrDefault();
        var w = overlay?.OpenDislikes() ?? new DislikesWindow("", _ => { });
        w.Owner = this;
        return w;
    }

    /// Небольшая кнопка для строки настройки — на месте тумблера.
    private static Button SmallButton(string text, Action onClick)
    {
        var b = ActionButton(text, "#36D6E7", "#36D6E7");
        b.Margin = new Thickness(0);
        b.Padding = new Thickness(12, 4, 12, 4);
        b.VerticalAlignment = VerticalAlignment.Center;
        b.Click += (_, _) => onClick();
        return b;
    }

    /// Строка настройки: название и пояснение слева, тумблер справа.
    private static UIElement Row(string title, string hint, bool value, Action<bool> onSet) =>
        Row(title, hint, Toggle(value, onSet));

    /// Строка настройки с чем угодно справа (тумблер, кнопка).
    private static UIElement Row(string title, string hint, UIElement right)
    {
        var grid = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel();
        texts.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Font("UiFont"),
            FontSize = 13, Foreground = new SolidColorBrush(Text), TextWrapping = TextWrapping.Wrap
        });
        if (hint.Length > 0)
            texts.Children.Add(new TextBlock
            {
                Text = hint,
                FontFamily = Font("UiFont"),
                FontSize = 11, Foreground = new SolidColorBrush(Mute),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0)
            });
        grid.Children.Add(texts);

        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    /// Тумблер: скруглённая дорожка и кружок, который переезжает вправо.
    private static UIElement Toggle(bool value, Action<bool> onSet)
    {
        var knob = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(3, 0, 0, 0)
        };
        var track = new Border
        {
            Width = 40, Height = 20, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand, Child = knob
        };

        void Paint(bool on)
        {
            track.Background = new SolidColorBrush(on
                ? Color.FromRgb(0x36, 0xD6, 0xE7)
                : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = on ? new Thickness(0, 0, 3, 0) : new Thickness(3, 0, 0, 0);
        }
        Paint(value);

        var state = value;
        track.MouseLeftButtonDown += (_, _) => { state = !state; Paint(state); onSet(state); };
        return track;
    }

    /// Выбор из многих вариантов — списком: у каждого своё пояснение, и в строку
    /// они бы не поместились.
    private static UIElement ChoiceList(
        string title, (string Key, string Label, string Hint)[] options,
        string value, Action<string> onSet)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        wrap.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Font("UiFont"), FontSize = 13,
            Foreground = new SolidColorBrush(Text), Margin = new Thickness(0, 0, 0, 6)
        });

        var rows = new List<Border>();
        var state = value;

        void Paint()
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var on = options[i].Key == state;
                rows[i].Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x2A, 0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
                rows[i].BorderBrush = new SolidColorBrush(on
                    ? Color.FromRgb(0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x30, 0x8A, 0xA0, 0xB2));
            }
        }

        foreach (var (key, label, hint) in options)
        {
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock
            {
                Text = label, FontFamily = Font("UiFont"), FontSize = 12.5,
                FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Text)
            });
            if (hint.Length > 0)
                texts.Children.Add(new TextBlock
                {
                    Text = hint, FontFamily = Font("UiFont"), FontSize = 11,
                    Foreground = new SolidColorBrush(Mute),
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
                });

            var row = new Border
            {
                CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Padding = new Thickness(11, 8, 11, 8), Margin = new Thickness(0, 0, 0, 5),
                Cursor = Cursors.Hand, Child = texts
            };
            row.MouseLeftButtonDown += (_, _) => { state = key; Paint(); onSet(key); };
            rows.Add(row);
            wrap.Children.Add(row);
        }
        Paint();
        return wrap;
    }

    /// Выбор из нескольких значений — сегментированная кнопка.
    private static UIElement Choice(
        string title, string hint, (string Key, string Label)[] options,
        string value, Action<string> onSet)
    {
        var wrap = new StackPanel { Margin = new Thickness(0, 7, 0, 7) };
        wrap.Children.Add(new TextBlock
        {
            Text = title,
            FontFamily = Font("UiFont"),
            FontSize = 13, Foreground = new SolidColorBrush(Text)
        });
        if (hint.Length > 0)
            wrap.Children.Add(new TextBlock
            {
                Text = hint,
                FontFamily = Font("UiFont"),
                FontSize = 11, Foreground = new SolidColorBrush(Mute),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0)
            });

        // С переносом: у очереди и размера под клиент по пять вариантов, и на
        // длинных языках они не влезали в ширину окна — последний обрезался.
        // Нижний отступ кнопок съедается отрицательным полем ряда.
        var row = new WrapPanel { Margin = new Thickness(0, 7, 0, -6) };
        var buttons = new List<Border>();
        var state = value;

        void Paint()
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                var on = options[i].Key == state;
                buttons[i].Background = new SolidColorBrush(on
                    ? Color.FromArgb(0x33, 0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
                buttons[i].BorderBrush = new SolidColorBrush(on
                    ? Color.FromRgb(0x36, 0xD6, 0xE7)
                    : Color.FromArgb(0x40, 0x8A, 0xA0, 0xB2));
                ((TextBlock)buttons[i].Child).Foreground =
                    new SolidColorBrush(on ? Colors.White : Mute);
            }
        }

        foreach (var (key, label) in options)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontFamily = Font("UiFont"),
                    FontSize = 12, FontWeight = FontWeights.Bold
                }
            };
            b.MouseLeftButtonDown += (_, _) => { state = key; Paint(); onSet(key); };
            buttons.Add(b);
            row.Children.Add(b);
        }
        Paint();
        wrap.Children.Add(row);
        return wrap;
    }
}
