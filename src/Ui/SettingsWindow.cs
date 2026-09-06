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
sealed class SettingsWindow : Window
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
        Background = new SolidColorBrush(Bg);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        PoolUi.Apply(this);

        Content = Build();
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

    private UIElement Build()
    {
        var s = _draft;
        var body = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };

        // Язык — самым верхом и в обход «Применить»: смена языка перерисовывает
        // само окно настроек, ждать кнопки тут не от чего.
        body.Children.Add(Section(Loc.T("settings.language")));
        var langs = new ComboBox
        {
            Margin = new Thickness(0, 2, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 220, Padding = new Thickness(8, 4, 8, 4),
            FontFamily = Font("UiFont"), FontSize = 13, Cursor = Cursors.Hand
        };
        foreach (var l in Loc.Languages)
            langs.Items.Add(new ComboBoxItem { Content = l.Native, Tag = l.Code, FontFamily = Font("UiFont") });
        langs.SelectedIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == Loc.Current));
        langs.SelectionChanged += (_, _) =>
        {
            if (langs.SelectedItem is ComboBoxItem { Tag: string code } && code != Loc.Current)
            {
                Loc.SetLanguage(code);
                Content = Build();      // перестраиваем окно на новом языке
            }
        };
        body.Children.Add(langs);

        body.Children.Add(Section(Loc.T("settings.ready")));
        body.Children.Add(Row(Loc.T("settings.readyRank"),    Loc.T("settings.readyRankHint"),
            s.ReadyRank,    v => { s.ReadyRank = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.readyLast5"),   Loc.T("settings.readyLast5Hint"),
            s.ReadyLast5,   v => { s.ReadyLast5 = v;   MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.readyWinrate"), Loc.T("settings.readyWinrateHint"),
            s.ReadyWinrate, v => { s.ReadyWinrate = v; MarkDirty(); }));

        // Переключатель данных графика — единственная настройка, где выбор не
        // «да/нет», а «что показывать».
        body.Children.Add(Choice(Loc.T("settings.chart"), Loc.T("settings.chartHint"),
            [("winrate", Loc.T("settings.chartWinrate")),
             ("rating",  Loc.T("settings.chartRating")),
             ("off",     Loc.T("settings.chartOff"))],
            s.ChartMode, v => { s.ChartMode = v; MarkDirty(); }));

        body.Children.Add(Choice(Loc.T("settings.chartDays"), Loc.T("settings.chartDaysHint"),
            [("30", Loc.T("settings.days30")), ("90", Loc.T("settings.days90"))],
            s.ChartDays.ToString(), v => { s.ChartDays = int.Parse(v); MarkDirty(); }));

        body.Children.Add(Choice(Loc.T("settings.queue"), Loc.T("settings.queueHint"),
            [("last", Loc.T("settings.queueLast")), ("solo", "Solo/Duo"), ("flex", "Flex")],
            s.DefaultQueue, v => { s.DefaultQueue = v; MarkDirty(); }));

        body.Children.Add(Row(Loc.T("settings.readyCompact"), Loc.T("settings.readyCompactHint"),
            s.ReadyCompact, v => { s.ReadyCompact = v; MarkDirty(); }));

        body.Children.Add(Row(Loc.T("settings.readyPool"),   Loc.T("settings.readyPoolHint"),
            s.ReadyPool,   v => { s.ReadyPool = v;   MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.readyChamps"), Loc.T("settings.readyChampsHint"),
            s.ReadyChamps, v => { s.ReadyChamps = v; MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.readyPhase"),  Loc.T("settings.readyPhaseHint"),
            s.ReadyPhase,  v => { s.ReadyPhase = v;  MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.readyBeta"),   Loc.T("settings.readyBetaHint"),
            s.ReadyBeta,   v => { s.ReadyBeta = v;   MarkDirty(); }));

        body.Children.Add(Section(Loc.T("settings.draft")));
        body.Children.Add(Row(Loc.T("settings.draftEnabled"), Loc.T("settings.draftEnabledHint"),
            s.DraftEnabled, v => { s.DraftEnabled = v; MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftRolePool"), Loc.T("settings.draftRolePoolHint"),
            s.DraftRolePool, v => { s.DraftRolePool = v; MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftReasons"),  Loc.T("settings.draftReasonsHint"),
            s.DraftReasons,  v => { s.DraftReasons = v;  MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftMetrics"),  Loc.T("settings.draftMetricsHint"),
            s.DraftMetrics,  v => { s.DraftMetrics = v;  MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftItems"),    Loc.T("settings.draftItemsHint"),
            s.DraftItems,    v => { s.DraftItems = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftHover"),    Loc.T("settings.draftHoverHint"),
            s.DraftHover,    v => { s.DraftHover = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftCombos"),   Loc.T("settings.draftCombosHint"),
            s.DraftCombos,   v => { s.DraftCombos = v;   MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftArch"),     Loc.T("settings.draftArchHint"),
            s.DraftArch,     v => { s.DraftArch = v;     MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftSideIcons"), Loc.T("settings.draftSideIconsHint"),
            s.DraftSideIcons, v => { s.DraftSideIcons = v; MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftDamage"),   Loc.T("settings.draftDamageHint"),
            s.DraftDamage,   v => { s.DraftDamage = v;   MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftRunes"),    Loc.T("settings.draftRunesHint"),
            s.DraftRunes,    v => { s.DraftRunes = v;    MarkDirty(); }));

        body.Children.Add(Choice(Loc.T("settings.draftCount"), Loc.T("settings.draftCountHint"),
            [("6", "6"), ("8", "8"), ("10", "10")],
            s.DraftCount.ToString(), v => { s.DraftCount = int.Parse(v); MarkDirty(); }));

        body.Children.Add(Row(Loc.T("settings.draftUnowned"), Loc.T("settings.draftUnownedHint"),
            s.DraftUnowned, v => { s.DraftUnowned = v; MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.draftMirror"),  Loc.T("settings.draftMirrorHint"),
            s.DraftMirror,  v => { s.DraftMirror = v;  MarkDirty(); }));

        body.Children.Add(Section(Loc.T("settings.bans")));
        body.Children.Add(Row(Loc.T("settings.bansTier"), Loc.T("settings.bansTierHint"),
            s.BansTierList, v => { s.BansTierList = v; MarkDirty(); }));

        body.Children.Add(Section(Loc.T("settings.general")));
        body.Children.Add(Choice(Loc.T("settings.opacity"), Loc.T("settings.opacityHint"),
            [("1", "100%"), ("0.85", "85%"), ("0.7", "70%")],
            s.Opacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => { s.Opacity = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                   MarkDirty(); }));
        body.Children.Add(Choice(Loc.T("settings.scale"), Loc.T("settings.scaleHint"),
            [("0.9", Loc.T("settings.scaleS")), ("1", Loc.T("settings.scaleM")), ("1.15", Loc.T("settings.scaleL"))],
            s.FontScale.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => { s.FontScale = double.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                   MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.onTop"),   Loc.T("settings.onTopHint"),
            s.AlwaysOnTop,    v => { s.AlwaysOnTop = v;    MarkDirty(); }));
        body.Children.Add(Row(Loc.T("settings.inGame"),  Loc.T("settings.inGameHint"),
            s.KeepDuringGame, v => { s.KeepDuringGame = v; MarkDirty(); }));

        var reset = ActionButton(Loc.T("settings.reset"), "#8AA0B2", "#35485A");
        reset.Click += (_, _) => { _draft = new AppSettings(); MarkDirty(); Content = Build(); };

        _apply = ActionButton(Loc.T("settings.apply"), "#0E141D", "#36D6E7");
        _apply.Background = new SolidColorBrush(Color.FromRgb(0x36, 0xD6, 0xE7));
        _apply.IsEnabled = _dirty;
        _apply.Opacity = _dirty ? 1.0 : 0.45;
        _apply.Click += (_, _) =>
        {
            AppSettings.Apply(_draft);       // сохранить и перерисовать оверлей разом
            _draft = AppSettings.Current.Clone();
            _dirty = false;
            _apply.IsEnabled = false;
            _apply.Opacity = 0.45;
        };

        var bottom = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 20, 0, 0)
        };
        bottom.Children.Add(_apply);
        bottom.Children.Add(reset);
        body.Children.Add(bottom);
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("settings.applyHint"),
            FontFamily = Font("UiFont"), FontSize = 11,
            Foreground = new SolidColorBrush(Mute),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0)
        });

        return new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

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

    /// Строка настройки: название и пояснение слева, тумблер справа.
    private static UIElement Row(string title, string hint, bool value, Action<bool> onSet)
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

        var toggle = Toggle(value, onSet);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(toggle);
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

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
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
                Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 6, 0),
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
