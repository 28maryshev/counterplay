using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace Counterplay;

/// <summary>
/// Тестовый режим (запуск: dotnet run test) — песочница без клиента LoL.
/// Открывает панель, где вручную расставляются союзники/враги и роль игрока;
/// оверлей показывает живые рекомендации, как в настоящем драфте. Роли врагов
/// не задаются — их можно назначать кликом в карточке врага (как в бою).
/// </summary>
static class TestMode
{
    public static async Task RunAsync(OverlayWindow overlay, CancellationToken ct)
    {
        // Та же подготовка, что в боевом режиме: статика, иконки, база.
        overlay.ShowStatus(Loc.T("status.loadingChamps"));
        await DataDragon.LoadAsync(Loc.DDragonLocale, ct);
        overlay.ShowStatus(Loc.T("status.loadingIcons"));
        await IconCache.PreloadAllAsync(msg => overlay.ShowStatus(msg), ct);
        await RoleIcons.PreloadAsync(ct);
        await ItemIcons.PreloadAsync(ct);
        await DataDb.EnsureAsync((msg, frac) => overlay.ShowProgress(msg, frac), ct);

        // Руны: реальных данных в базе ещё нет — в тестовом режиме панель
        // наполняется правдоподобными моками, чтобы обкатать вид и кнопки.
        RunesClient.UseMock = true;
        await RuneIcons.LoadAsync(Loc.DDragonLocale, ct);
        await RunesClient.LoadManifestAsync(ct);

        // Импорт в клиент из теста не делаем (клиента может не быть) —
        // кнопки отвечают «как будто получилось», чтобы проверить сценарий.
        overlay.ApplyRunesHandler  = async (_, _) => { await Task.Delay(400, ct); return true; };
        overlay.ExportBuildHandler = async (_, _, _) => { await Task.Delay(400, ct); return true; };

        var dbPath = RecommendationEngine.FindDb();
        if (dbPath is null)
        {
            overlay.ShowStatus(Loc.T("status.noDb"));
            await Task.Delay(Timeout.Infinite, ct);
            return;
        }
        var engine = RecommendationEngine.Create(dbPath, "emerald");

        overlay.Dispatcher.Invoke(() =>
        {
            overlay.ShowReady(Loc.T("status.readyIdle") + " · TEST");
            new TestPanel(overlay, engine).Show();
        });

        await Task.Delay(Timeout.Infinite, ct); // живём до закрытия окна/Ctrl+C
    }
}

/// <summary>Панель тестового драфта: 5 своих (с ролями) + 5 врагов + фаза банов.</summary>
sealed class TestPanel : Window
{
    private static readonly string[] LcuRoles  = ["top", "jungle", "middle", "bottom", "utility"];
    private static readonly string[] RoleNames = ["TOP", "JGL", "MID", "BOT", "SUP"];

    private readonly OverlayWindow _overlay;
    private readonly RecommendationEngine _engine;
    private readonly Dictionary<string, int> _idByName;   // имя чемпиона → id
    private readonly List<string> _names;                 // "—" + имена по алфавиту

    private readonly ComboBox[]   _ally  = new ComboBox[5];
    private readonly ComboBox[]   _enemy = new ComboBox[5];
    private readonly RadioButton[] _meRadio = new RadioButton[5];
    private readonly CheckBox     _banPhase;
    private bool _ready; // подавляет пересчёт во время построения UI

    public TestPanel(OverlayWindow overlay, RecommendationEngine engine)
    {
        _overlay = overlay;
        _engine  = engine;

        _idByName = DataDragon.GetAllIconUrls().Keys
            .ToDictionary(id => DataDragon.Name(id), id => id);
        _names = ["—", .. _idByName.Keys.OrderBy(n => n, StringComparer.CurrentCulture)];

        Title  = "Counterplay — тестовый драфт";
        Width  = 560; Height = 420;
        Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x1D));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        root.Children.Add(Header("МОЯ КОМАНДА (точка = я)", 0, "#36D6E7"));
        root.Children.Add(Header("ВРАГИ (роли — кликом в оверлее)", 2, "#FF5A4D"));

        for (int i = 0; i < 5; i++)
        {
            // Свой ряд: радио «это я» + роль + чемпион
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            _meRadio[i] = new RadioButton
            {
                GroupName = "me", VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0), IsChecked = i == 2, // по умолчанию я — мид
                ToolTip = "Мой слот"
            };
            _meRadio[i].Checked += (_, _) => Recompute();
            DockPanel.SetDock(_meRadio[i], Dock.Left);
            row.Children.Add(_meRadio[i]);

            var roleLbl = new TextBlock
            {
                Text = RoleNames[i], Width = 34, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
                FontWeight = FontWeights.Bold, FontSize = 11
            };
            DockPanel.SetDock(roleLbl, Dock.Left);
            row.Children.Add(roleLbl);
            row.Children.Add(_ally[i] = MakeCombo());

            Grid.SetRow(row, i + 1); Grid.SetColumn(row, 0);
            root.Children.Add(row);

            // Вражеский ряд: только чемпион
            var erow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var eLbl = new TextBlock
            {
                Text = $"E{i + 1}", Width = 26, VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0x40, 0x40)),
                FontWeight = FontWeights.Bold, FontSize = 11
            };
            DockPanel.SetDock(eLbl, Dock.Left);
            erow.Children.Add(eLbl);
            erow.Children.Add(_enemy[i] = MakeCombo());
            Grid.SetRow(erow, i + 1); Grid.SetColumn(erow, 2);
            root.Children.Add(erow);
        }

        // Нижний ряд: фаза банов + сброс
        var bottom = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        _banPhase = new CheckBox
        {
            Content = "Фаза банов (советы по банам)",
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
        };
        _banPhase.Checked   += (_, _) => Recompute();
        _banPhase.Unchecked += (_, _) => Recompute();
        bottom.Children.Add(_banPhase);

        var reset = new Button
        {
            Content = "Сброс", Width = 90, HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(0, 3, 0, 3)
        };
        reset.Click += (_, _) =>
        {
            _ready = false;
            foreach (var cb in _ally.Concat(_enemy)) cb.SelectedIndex = 0;
            _ready = true;
            Recompute();
        };
        DockPanel.SetDock(reset, Dock.Right);
        bottom.Children.Insert(0, reset);

        Grid.SetRow(bottom, 6); Grid.SetColumn(bottom, 0); Grid.SetColumnSpan(bottom, 3);
        root.Children.Add(bottom);

        Content = root;
        _ready = true;
        Recompute();

        // Закрыл панель — выходим из приложения целиком.
        Closed += (_, _) => System.Windows.Application.Current.Shutdown();
    }

    private static TextBlock Header(string text, int col, string color)
    {
        var tb = new TextBlock
        {
            Text = text, FontWeight = FontWeights.Bold, FontSize = 12,
            Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(tb, 0); Grid.SetColumn(tb, col);
        return tb;
    }

    private ComboBox MakeCombo()
    {
        var cb = new ComboBox
        {
            IsEditable = true, ItemsSource = _names, SelectedIndex = 0,
            IsTextSearchEnabled = true, Margin = new Thickness(0, 0, 0, 0)
        };
        cb.SelectionChanged += (_, _) => Recompute();
        cb.LostKeyboardFocus += (_, _) => Recompute(); // подтверждение набранного текста
        return cb;
    }

    private int ChampOf(ComboBox cb)
    {
        var text = (cb.SelectedItem as string ?? cb.Text ?? "").Trim();
        if (text.Length == 0 || text == "—") return 0;
        if (_idByName.TryGetValue(text, out var id)) return id;
        // Частичное совпадение по началу имени (пользователь мог не дописать)
        var hit = _idByName.Keys.FirstOrDefault(n => n.StartsWith(text, StringComparison.CurrentCultureIgnoreCase));
        return hit != null ? _idByName[hit] : 0;
    }

    // Собирает DraftState из панели и обновляет оверлей.
    private void Recompute()
    {
        if (!_ready) return;

        int meIdx = Array.FindIndex(_meRadio, r => r.IsChecked == true);
        if (meIdx < 0) meIdx = 2;

        var my = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
        {
            var champ = ChampOf(_ally[i]);
            var isMe  = i == meIdx;
            // Мой чемпион — как ховер (PickIntent): подбор продолжает показывать список.
            my.Add(new DraftPlayer(i, isMe ? 0 : champ, isMe ? champ : 0, LcuRoles[i], isMe));
        }
        var their = new List<DraftPlayer>();
        for (int i = 0; i < 5; i++)
            their.Add(new DraftPlayer(5 + i, ChampOf(_enemy[i]), 0, "", false)); // роли скрыты, как в Solo/Duo

        // Прямой оппонент: враг в строке моей роли (в бою роли врагов вычисляются
        // эвристикой/вручную; тут — по позиции в списке, этого хватает для стенда).
        var opp = their[meIdx].EffectiveChampionId > 0 ? their[meIdx] : null;

        var draft = new DraftState(
            my, their, [], [], my[meIdx], LcuRoles[meIdx],
            opp, false, _banPhase.IsChecked == true, [], false);

        if (_banPhase.IsChecked == true)
        {
            _overlay.UpdateBans(_engine.RecommendBans(draft), draft);
            _overlay.HideRunes();
        }
        else
        {
            _overlay.UpdateRecommendations(_engine.Recommend(draft), draft, _engine);
            _ = Program.UpdateRunesAsync(_overlay, draft, CancellationToken.None);
        }
    }
}
