using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using ToolTip = System.Windows.Controls.ToolTip;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using DockPanel = System.Windows.Controls.DockPanel;
using Dock = System.Windows.Controls.Dock;

namespace Counterplay;

//// <summary>
/// Пароль синхронизации: состояние, кнопки и строка «когда ходили и чем
/// кончилось». Один вид на все окна.
///
/// Жил в окне пулов — и это была ошибка. На НОВОМ компьютере человек идёт в
/// настройки, а не в пулы: пулов у него там ещё нет, он за ними и пришёл.
/// Поэтому вид общий, а окна его просто размещают у себя.
/// </summary>
public static class SyncBadge
{
    private static readonly Color Blue = Color.FromRgb(0x5A, 0x8A, 0xC8);
    private static readonly Color Muted = Color.FromRgb(0x8A, 0xA0, 0xB2);
    private static readonly Color Text = Color.FromRgb(0xD7, 0xDE, 0xE6);
    private static readonly Color Warn = Color.FromRgb(0xE0, 0xA8, 0x5A);

    /// <summary>
    /// Ряд «кнопка + знак вопроса» и под ним строка состояния.
    /// <paramref name="ask"/> открывает окно ввода (у каждого окна свои кнопки
    /// и стиль), <paramref name="changed"/> зовётся после смены пароля.
    /// </summary>
    public static FrameworkElement Row(Func<bool> ask, Action? changed = null)
    {
        var box = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Fill(box, ask, changed);

        // Синхронизация ходит и сама — после игры, при входе в аккаунт, — и
        // строка обязана меняться, пока окно открыто, а не только по кнопке.
        // Подписка живёт, пока ряд на экране: закрытое окно не держится в
        // памяти ради события.
        void OnChanged() => box.Dispatcher.InvokeAsync(() => Fill(box, ask, changed));
        box.Loaded += (_, _) => { SyncStatus.Changed -= OnChanged; SyncStatus.Changed += OnChanged; };
        box.Unloaded += (_, _) => SyncStatus.Changed -= OnChanged;
        return box;
    }

    private static void Fill(StackPanel box, Func<bool> ask, Action? changed)
    {
        box.Children.Clear();
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        box.Children.Add(row);

        if (SyncPassword.IsSet)
        {
            row.Children.Add(new TextBlock
            {
                Text = "✓ " + Loc.T("sync.set"),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x8A)),
                FontSize = 11, FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            row.Children.Add(MakeButton(Loc.T("sync.change"), box, ask, changed));

            // Сам перенос. Только при заданном пароле: без него жать не на что.
            // На время прохода — любого, и по кнопке, и тихого — кнопка гаснет:
            // второе нажатие посреди первого всё равно ждало бы его конца.
            var running = SyncStatus.Running;
            var now = MakeButton(Loc.T(running ? "sync.doing" : "sync.now"), box, () => false, null);
            now.Margin = new Thickness(6, 0, 0, 0);
            now.IsEnabled = !running;
            now.Click += (_, _) => Run("кнопка");
            row.Children.Add(now);
        }
        else row.Children.Add(MakeButton(Loc.T("sync.add"), box, ask, changed));

        // «?» нужен в обоих состояниях: и тому, кто ещё не завёл пароль, и тому,
        // кто забыл, зачем он.
        row.Children.Add(Help());

        if (SyncPassword.IsSet) box.Children.Add(StatusLines());
    }

    private static Button MakeButton(
        string text, StackPanel box, Func<bool> ask, Action? changed)
    {
        var b = new Button
        {
            Content = text, FontSize = 11, FontWeight = FontWeights.Bold,
            Padding = new Thickness(9, 2, 9, 3),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            Foreground = System.Windows.Media.Brushes.White,
        };
        b.Click += (_, _) =>
        {
            if (!ask()) return;
            Fill(box, ask, changed);   // состояние поменялось — перерисовать
            changed?.Invoke();
            // Пароль задан — синхронизироваться сразу, а не ждать конца игры.
            if (SyncPassword.IsSet) Run("новый пароль");
        };
        return b;
    }

    /// Проход по просьбе человека. Чем кончился, покажет строка состояния.
    private static async void Run(string reason)
    {
        try { await SyncClient.NowAsync(PoolStore.AccountPuuid, reason); }
        catch (Exception e) { Log.Write($"синхронизация ({reason}): {e.Message}"); }
    }

    /// <summary>
    /// Под кнопкой: когда синхронизировались и чем кончилось (<see cref="SyncStatus"/>).
    ///
    /// Первая строка — последний проход: «синхронизировано сегодня в 12:41»
    /// или причина неудачи. Ниже, мельче — то, по чему видно, доехало ли:
    /// когда в последний раз пришло что-то с другого компьютера и что именно, и
    /// «на сервере было пусто», если этот компьютер первый с таким паролем.
    /// Последнее — подсказка тому, кто ждёт данные с первого компьютера, а
    /// пароль набрал иначе: другой пароль — другая строка на сервере.
    /// </summary>
    private static FrameworkElement StatusLines()
    {
        var s = SyncStatus.Current;
        var panel = new StackPanel
        {
            Margin = new Thickness(0, 5, 0, 0), MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        if (SyncStatus.Running) panel.Children.Add(Line(Loc.T("sync.doing"), Muted, 11));
        else if (s.Failed)
            panel.Children.Add(Line($"{Loc.T(s.FailKey ?? "sync.failedNet")} · {When(s.FailAt)}", Warn, 11));
        else if (s.OkAt > 0) panel.Children.Add(Line(Loc.T("sync.ok", When(s.OkAt)), Text, 11));
        else panel.Children.Add(Line(Loc.T("sync.never"), Muted, 11));

        if (s.Failed && s.OkAt > 0) panel.Children.Add(Line(Loc.T("sync.ok", When(s.OkAt)), Muted, 10));
        if (!s.Failed && s.OkAt > 0 && s.WasEmpty) panel.Children.Add(Line(Loc.T("sync.empty"), Muted, 10));
        if (s.GotAt > 0) panel.Children.Add(Line(Loc.T("sync.got", When(s.GotAt), What(s)), Muted, 10));
        return panel;
    }

    private static TextBlock Line(string text, Color color, double size) => new()
    {
        Text = text, Foreground = new SolidColorBrush(color), FontSize = size,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 1)
    };

    /// «сегодня в 12:41» или «13.10 в 23:51». Без культуры: двоеточие в формате
    /// времени — разделитель культуры, а не буква.
    private static string When(long unix)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var time = t.ToString("HH:mm", inv);
        return t.Date == DateTime.Today
            ? Loc.T("sync.today", time)
            : Loc.T("sync.onDate", t.ToString("d.MM", inv), time);
    }

    /// «игр: 6, пулы, настройки».
    private static string What(SyncStatus.State s)
    {
        var parts = new List<string>();
        if (s.GotGames > 0) parts.Add(Loc.T("sync.gotGames", s.GotGames));
        if (s.GotPools) parts.Add(Loc.T("sync.gotPools"));
        if (s.GotSettings) parts.Add(Loc.T("sync.gotSettings"));
        return string.Join(", ", parts);
    }

    /// «?» с пояснением: что это за пароль и зачем.
    private static FrameworkElement Help()
    {
        var body = new StackPanel { MaxWidth = 340 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.hint"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
            FontSize = 12, LineHeight = 18
        });

        // Перечень — списком, а не абзацем: сюда заглядывают именно чтобы
        // увидеть, что попадёт на второй компьютер, и сплошной текст для этого
        // читать неудобно.
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.whatTitle"),
            Foreground = new SolidColorBrush(Color.FromRgb(0x5A, 0x8A, 0xC8)),
            FontSize = 11, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 10, 0, 5)
        });
        foreach (var item in Loc.TArray("sync.whatList"))
        {
            var line = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
            var dot = new TextBlock
            {
                Text = "•", Foreground = new SolidColorBrush(Muted),
                FontSize = 11, Margin = new Thickness(2, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(dot, Dock.Left);
            line.Children.Add(dot);
            line.Children.Add(new TextBlock
            {
                Text = item, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD7, 0xDE, 0xE6)),
                FontSize = 11, LineHeight = 16
            });
            body.Children.Add(line);
        }

        body.Children.Add(new TextBlock
        {
            Text = Loc.T("sync.warn"), TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Muted),
            FontSize = 11, LineHeight = 16, Margin = new Thickness(0, 10, 0, 0)
        });

        var tip = new ToolTip
        {
            Content = body, Padding = new Thickness(12, 10, 12, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x1A, 0x24)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x35, 0x48, 0x5A)),
            BorderThickness = new Thickness(1)
        };

        var q = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9),
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5A, 0x8A, 0xC8)),
            BorderBrush = new SolidColorBrush(Blue), BorderThickness = new Thickness(1),
            ToolTip = tip,
            Child = new TextBlock
            {
                Text = "?", Foreground = new SolidColorBrush(Blue),
                FontWeight = FontWeights.Bold, FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        ToolTipService.SetShowDuration(q, 60000);
        ToolTipService.SetInitialShowDelay(q, 150);
        return q;
    }
}
