using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Counterplay;

// Панель песочницы — раскладка окна: карточки «Драфт» (запуск, «Заново»,
// «Сброс», строка состояния), «Составы» (две команды, роли, друг), «Окно
// программы» (экран, сценарий профиля), «Подбор рун», боевой режим внизу;
// вид кнопок и карточек.
//
// До 22.11 всё стояло рядами одной сетки: кнопки этапов, запуска, сброса и
// ролей — одной строкой вперемешку; поиск, друг, сценарий, боевой режим —
// каждый своей строкой с подписью сбоку. Владелец: «добавилось очень много
// всего в песочницу — сделай нормальный удобный UI». Раскладку меряет
// tests/SandboxPanel: всё в окне, ничего не наезжает, подписи целиком.
sealed partial class TestPanel
{
    // ── Палитра ──────────────────────────────────────────────────────────────

    private static SolidColorBrush Rgb(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Brush WindowBg    = Rgb(0x0E, 0x14, 0x1D);
    private static readonly Brush CardBg      = Rgb(0x14, 0x1C, 0x27);
    private static readonly Brush CardBorder  = Rgb(0x22, 0x30, 0x3F);
    private static readonly Brush CardTitleFg = Rgb(0x6F, 0x83, 0x99);
    private static readonly Brush TextFg      = Rgb(0x9F, 0xB3, 0xC8);
    private static readonly Brush DimFg       = Rgb(0x6A, 0x78, 0x86);

    // Кнопки: запуск драфта — синие, прочие — тёмные, идущее сейчас — светлее.
    private static readonly Brush LaunchBg     = Rgb(0x17, 0x2C, 0x46);
    private static readonly Brush LaunchBorder = Rgb(0x2E, 0x5F, 0x9A);
    private static readonly Brush LaunchFg     = Rgb(0xDD, 0xEA, 0xF8);
    private static readonly Brush PlainBg      = Rgb(0x19, 0x22, 0x2E);
    private static readonly Brush PlainBorder  = Rgb(0x2C, 0x3A, 0x4A);
    private static readonly Brush PlainFg      = Rgb(0xC3, 0xD0, 0xDD);
    private static readonly Brush ActiveBg     = Rgb(0x23, 0x4A, 0x78);
    private static readonly Brush ActiveBorder = Rgb(0x5A, 0x9A, 0xE0);

    private enum BtnKind { Launch, Plain }

    // Плоская кнопка: стандартный шаблон Windows на тёмном фоне — светлая
    // плашка, и наведение перекрашивает её в свой голубой поверх наших цветов.
    // Подсветка наведения здесь — полупрозрачный слой, цвет кнопки не трогает.
    private static readonly ControlTemplate ButtonTemplate = (ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="Button">
          <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="4" SnapsToDevicePixels="True">
            <Grid>
              <Border x:Name="Hl" Background="White" Opacity="0" CornerRadius="3"/>
              <ContentPresenter Margin="{TemplateBinding Padding}" RecognizesAccessKey="False"
                                HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Grid>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Hl" Property="Opacity" Value="0.07"/></Trigger>
            <Trigger Property="IsPressed" Value="True"><Setter TargetName="Hl" Property="Opacity" Value="0.15"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static Button StyleButton(Button b, string text, string tip, BtnKind kind)
    {
        b.Content = text;
        b.ToolTip = tip;
        b.Template = ButtonTemplate;
        b.Padding = new Thickness(10, 5, 10, 5);
        b.Margin = new Thickness(0, 0, 6, 0);
        b.BorderThickness = new Thickness(1);
        b.Cursor = System.Windows.Input.Cursors.Hand;
        b.VerticalAlignment = VerticalAlignment.Center;
        Mark(b, false, kind);
        return b;
    }

    /// Цвет кнопки: обычный по виду или «идёт сейчас».
    private static void Mark(Button b, bool on, BtnKind kind)
    {
        var launch = kind == BtnKind.Launch;
        b.Background  = on ? ActiveBg : launch ? LaunchBg : PlainBg;
        b.BorderBrush = on ? ActiveBorder : launch ? LaunchBorder : PlainBorder;
        b.Foreground  = on ? System.Windows.Media.Brushes.White : launch ? LaunchFg : PlainFg;
        b.FontWeight  = on ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// Карточка: заголовок мелкими прописными и содержимое под ним.
    private static Border Card(string title, UIElement body, double bottom = 10)
    {
        var dock = new DockPanel();
        var head = new TextBlock
        {
            Text = title, FontSize = 10.5, FontWeight = FontWeights.Bold,
            Foreground = CardTitleFg, Margin = new Thickness(0, 0, 0, 8),
        };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);
        dock.Children.Add(body);
        return new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 9, 12, 11),
            Margin = new Thickness(0, 0, 0, bottom), Child = dock,
        };
    }

    private static TextBlock Caption(string text, double size = 12) => new()
    {
        Text = text, Foreground = TextFg, FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Header(string text, int col, string color)
    {
        var tb = new TextBlock
        {
            Text = text, FontWeight = FontWeights.Bold, FontSize = 11.5,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            Margin = new Thickness(0, 0, 0, 2)
        };
        Grid.SetRow(tb, 0); Grid.SetColumn(tb, col);
        return tb;
    }

    // ── Окно целиком ─────────────────────────────────────────────────────────

    private UIElement BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(12) };
        var live = LiveRow();
        DockPanel.SetDock(live, Dock.Bottom);
        root.Children.Add(live);
        foreach (var card in new[] { DraftCard(), TeamsCard(), WindowCard() })
        {
            DockPanel.SetDock(card, Dock.Top);
            root.Children.Add(card);
        }
        // Разбор подбора рун — на всё оставшееся место, длинный — с прокруткой.
        root.Children.Add(Card("ПОДБОР РУН (COUNTERPLAY AI)", RuneAiBody()));
        return root;
    }

    // ── Драфт: запуск, «Заново», «Сброс», состояние ─────────────────────────

    private readonly Button _restartBtn = new();

    private readonly TextBlock _status = new();   // что идёт сейчас: этап, ход, кто первый

    private UIElement DraftCard()
    {
        StyleButton(_searchBtn, SearchIdle,
            $"Экран ожидания в фазе «идёт поиск игры» на {SearchSeconds:0} с — успеть посмотреть подсказку " +
            "режима пула (отложенная «Не сейчас» снова в силе); затем объявление, баны и пики, как «Баны». " +
            "Ещё раз — отсчёт заново", BtnKind.Launch);
        _searchBtn.Click += (_, _) => StartSearch();
        StyleButton(_stageBans, "Баны",
            "Как в рейтинге, на твоих данных: объявление пиков, баны, затем пики. Выбранные в слотах " +
            "чемпионы — план: союзники наводят их и берут в свой ход", BtnKind.Launch);
        _stageBans.Click += (_, _) => SetStage(TestStage.Bans);
        StyleButton(_simBtn, "▶ Авто-драфт",
            "Только пики, по очереди LoL; кто пикает первым — монетка. Выбранные в слотах чемпионы стоят " +
            "наведением и берутся в ход своей клетки, пустые слоты заполняют боты. Ещё раз — стоп",
            BtnKind.Launch);
        _simBtn.Click += (_, _) => ToggleSim();
        var instant = StyleButton(new Button(), "⚡ Моментально",
            "Заполнить обе команды сразу. Уже выбранные чемпионы остаются на месте — добираются только " +
            "пустые слоты", BtnKind.Launch);
        instant.Click += (_, _) => InstantDraft();
        instant.Margin = new Thickness(0);

        var launch = new UniformGrid { Rows = 1 };
        foreach (var b in new[] { _searchBtn, _stageBans, _simBtn, instant })
        {
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            launch.Children.Add(b);
        }

        StyleButton(_restartBtn, "↻ Заново", "", BtnKind.Plain);
        _restartBtn.Click += (_, _) => RestartDraft();
        var reset = StyleButton(new Button(), "✕ Сброс",
            "Остановить драфт и очистить слоты. Окно уходит с драфта на экран ожидания — настрой " +
            "составы, роль и друга и запусти заново", BtnKind.Plain);
        reset.Click += (_, _) => ResetDraft();

        var control = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_restartBtn, Dock.Left);
        control.Children.Add(_restartBtn);
        DockPanel.SetDock(reset, Dock.Left);
        control.Children.Add(reset);
        _status.Foreground = TextFg;
        _status.FontSize = 11.5;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Margin = new Thickness(6, 0, 0, 0);
        control.Children.Add(_status);

        var body = new StackPanel();
        body.Children.Add(launch);
        body.Children.Add(control);
        return Card("ДРАФТ", body);
    }

    // ── Составы: две команды, роли, друг ────────────────────────────────────

    private UIElement TeamsCard()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(Header("МОЯ КОМАНДА · точка — это я", 0, "#36D6E7"));
        grid.Children.Add(Header("ВРАГИ", 2, "#FF5A4D"));
        for (int i = 0; i < 5; i++)
        {
            var ally = AllyRow(i);
            Grid.SetRow(ally, i + 1); Grid.SetColumn(ally, 0);
            grid.Children.Add(ally);
            var enemy = EnemyRow(i);
            Grid.SetRow(enemy, i + 1); Grid.SetColumn(enemy, 2);
            grid.Children.Add(enemy);
        }

        // Под командами: перемешать роли и друг.
        var shuffle = StyleButton(new Button(), "🔀 Перемешать роли",
            "Перемешать роли по строкам — очередь пика у ролей будет разной", BtnKind.Plain);
        shuffle.Click += (_, _) => ShuffleRoles();
        var tools = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(shuffle, Dock.Left);
        tools.Children.Add(shuffle);
        tools.Children.Add(MateRow());

        _mateInfo.Foreground = DimFg;
        _mateInfo.FontSize = 11;
        _mateInfo.TextWrapping = TextWrapping.Wrap;
        _mateInfo.Margin = new Thickness(0, 4, 0, 0);
        _mateInfo.HorizontalAlignment = HorizontalAlignment.Right;

        var body = new StackPanel();
        body.Children.Add(grid);
        body.Children.Add(tools);
        body.Children.Add(_mateInfo);
        return Card("СОСТАВЫ", body);
    }

    // Своя строка: «это я» + пометка друга + роль + чемпион.
    private UIElement AllyRow(int i)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _meRadio[i] = new RadioButton
        {
            GroupName = "me", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0), IsChecked = i == 2, // по умолчанию я — мид
            ToolTip = "Мой слот"
        };
        _meRadio[i].Checked += (_, _) => { _myLocked = false; if (_banSim) ReseatBans(); Recompute(); };
        _meRadio[i].Click += (_, _) => _meChosen = true;   // только клик мышью, не установка из кода
        DockPanel.SetDock(_meRadio[i], Dock.Left);
        row.Children.Add(_meRadio[i]);

        // Пометка «это друг» — видна только у его строки.
        _mateMark[i] = new TextBlock
        {
            Text = "друг", FontSize = 10, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x8A, 0xC8)),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "Этот союзник — твой друг по дуо-пулу: программа опознаёт его по puuid, как в настоящей игре"
        };
        DockPanel.SetDock(_mateMark[i], Dock.Left);
        row.Children.Add(_mateMark[i]);

        _roleCombos[i] = MakeRoleCombo(_rowRoles, _roleCombos, i, "#8AA0B2");
        DockPanel.SetDock(_roleCombos[i], Dock.Left);
        row.Children.Add(_roleCombos[i]);
        row.Children.Add(_ally[i] = MakeCombo(i));
        return row;
    }

    // Вражеская строка: роль (нужна только авто-драфту — кого брать на линию) + чемпион.
    private UIElement EnemyRow(int i)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        _enemyRoleCombos[i] = MakeRoleCombo(_enemyRoles, _enemyRoleCombos, i, "#C84040");
        DockPanel.SetDock(_enemyRoleCombos[i], Dock.Left);
        row.Children.Add(_enemyRoleCombos[i]);
        row.Children.Add(_enemy[i] = MakeCombo(5 + i));
        return row;
    }

    // «Друг: [кто] на [роль]» — справа от «Перемешать роли».
    private UIElement MateRow()
    {
        var row = new DockPanel { Margin = new Thickness(8, 0, 0, 0) };
        var label = Caption("Друг:");
        label.Margin = new Thickness(0, 0, 6, 0);
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);

        _mateRoleCombo.ItemsSource = RoleNames;
        _mateRoleCombo.Width = 54;
        _mateRoleCombo.FontSize = 11;
        _mateRoleCombo.FontWeight = FontWeights.Bold;
        _mateRoleCombo.VerticalAlignment = VerticalAlignment.Center;
        _mateRoleCombo.IsEnabled = false;
        _mateRoleCombo.ToolTip = "Роль друга в этом драфте — его станет союзник на этой роли";
        _mateRoleCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingMates || _mate is null) return;
            var i = _mateRoleCombo.SelectedIndex;
            if (i < 0) return;
            _mateRole = LcuRoles[i];
            Recompute();
        };
        DockPanel.SetDock(_mateRoleCombo, Dock.Right);
        row.Children.Add(_mateRoleCombo);
        var on = Caption("на");
        on.Margin = new Thickness(6, 0, 6, 0);
        DockPanel.SetDock(on, Dock.Right);
        row.Children.Add(on);

        _mateCombo.VerticalAlignment = VerticalAlignment.Center;
        _mateCombo.ToolTip = "С кем играешь в дуо. Список — твои дуо-пулы: выбор включает пул, "
                           + "настроенный на этого друга. На профиле «мой аккаунт» это твои настоящие пулы";
        _mateCombo.SelectionChanged += (_, _) =>
        {
            if (_fillingMates) return;
            var i = _mateCombo.SelectedIndex;
            SelectMate(i >= 0 && i < _mateOptions.Count ? _mateOptions[i] : null);
        };
        row.Children.Add(_mateCombo);
        return row;
    }

    // ── Окно программы: экран и сценарий профиля ────────────────────────────

    private readonly Button _showBtn = new();   // «Показать окно» — вернуть спрятанное окно драфта

    private UIElement WindowCard()
    {
        StyleButton(_stageReady, "Лобби · Ready / пул",
            "Экран ожидания: ник, ранг, график, кнопки пула. Драфт в окно не идёт", BtnKind.Plain);
        _stageReady.Click += (_, _) => SetStage(TestStage.Ready);
        StyleButton(_stageDraft, "Драфт вручную",
            "Окно драфта по слотам панели, без ходов и таймеров: меняешь слот — окно пересчитывает",
            BtnKind.Plain);
        _stageDraft.Click += (_, _) => SetStage(TestStage.Draft);
        StyleButton(_showBtn, "Показать окно",
            "Вернуть окно драфта, если его закрыли крестиком, свернули или спрятали Esc. Программа в " +
            "песочнице закрывается только вместе с этой панелью", BtnKind.Plain);
        _showBtn.Click += (_, _) => ShowOverlay();
        InitScenario();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int r = 0; r < 3; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row); Grid.SetColumn(e, col);
            grid.Children.Add(e);
        }
        var screen = Caption("Экран");
        screen.Margin = new Thickness(0, 0, 10, 0);
        Put(screen, 0, 0);
        var stages = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        stages.Children.Add(_stageReady);
        stages.Children.Add(_stageDraft);
        stages.Children.Add(_showBtn);
        Put(stages, 0, 1);

        var scenario = Caption("Сценарий");
        scenario.Margin = new Thickness(0, 8, 10, 0);
        Put(scenario, 1, 0);
        _profile.Margin = new Thickness(0, 8, 0, 0);
        _profile.VerticalAlignment = VerticalAlignment.Center;
        Put(_profile, 1, 1);

        var checks = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0)
        };
        foreach (var c in new[] { _emptyProfile, _missingChamps })
        {
            c.Foreground = TextFg;
            c.VerticalAlignment = VerticalAlignment.Center;
            c.Margin = new Thickness(0, 0, 16, 0);
            checks.Children.Add(c);
        }
        Put(checks, 2, 1);
        return Card("ОКНО ПРОГРАММЫ", grid);
    }

    // ── Боевой режим — внизу, отдельно от сценариев песочницы ───────────────

    private UIElement LiveRow()
    {
        var live = StyleButton(new Button(), "⚔ Боевой режим",
            "Отключить песочницу и подключиться к своему клиенту LoL: окно свернётся в трей и поднимется, "
            + "когда начнётся драфт", BtnKind.Plain);
        live.Click += (_, _) =>
        {
            if (!ConfirmWindow.Ask(
                    "Перейти в боевой режим? Песочница закроется, программа подключится "
                    + "к твоему клиенту LoL и свернётся в трей до начала драфта.",
                    "Перейти", "Отмена", this)) return;
            StopSim();
            StopSearch();
            TestMode.RequestLiveMode();
        };
        var row = new DockPanel();
        DockPanel.SetDock(live, Dock.Left);
        row.Children.Add(live);
        var note = Caption("песочница закроется, данные пойдут из твоего клиента", 11);
        note.Foreground = DimFg;
        note.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(note);
        return row;
    }
}
