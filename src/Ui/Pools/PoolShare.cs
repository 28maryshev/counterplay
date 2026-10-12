using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace Counterplay;

/// <summary>
/// Обмен пулом: код строкой или файл.
///
/// Файл удобен не везде — в мессенджере проще кинуть строку. Поэтому «Экспорт»
/// открывает окно с готовым кодом (клик по полю — в буфер обмена), а файл
/// остаётся кнопкой в том же окне. На приёме всё зеркально: поле для кода и
/// кнопка «открыть файл».
/// </summary>
static class PoolShare
{
    /// <summary>Окно отдачи: код сверху, файл снизу.</summary>
    public static void Show(Window owner, string json, string suggestedFile)
    {
        var code = PoolFile.ToCode(json);

        var dlg = Frame(owner, Loc.T("pool.export"), out var body);

        body.Children.Add(Hint(Loc.T("pool.shareCodeHint")));

        // Поле с кодом. Только на чтение: править код руками незачем, а вот
        // случайно затереть — легко.
        var box = new TextBox
        {
            Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Height = 92, FontSize = 11, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Cursor = System.Windows.Input.Cursors.Hand,
            // Код длинный, и целиком он не нужен: показываем начало, остальное
            // уезжает за край. Копируется всё равно весь.
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        // Подсказка посередине поля — она же сообщает об успехе.
        var tip = new TextBlock
        {
            Text = Loc.T("pool.copyCode"), FontSize = 12, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEB, 0xD6, 0xA8)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center, IsHitTestVisible = false, Opacity = 0,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, BlurRadius = 8, ShadowDepth = 0, Opacity = 1 },
        };
        var veil = new Border
        {
            // Плотная: при 0xCC код просвечивал сквозь подсказку и мешал её читать.
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0F, 0x18, 0x22)),
            CornerRadius = new CornerRadius(5), IsHitTestVisible = false, Opacity = 0,
        };

        var stack = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(box);
        stack.Children.Add(veil);
        stack.Children.Add(tip);
        body.Children.Add(stack);

        box.MouseEnter += (_, _) => { veil.Opacity = 1; tip.Opacity = 1; };
        box.MouseLeave += (_, _) =>
        {
            veil.Opacity = 0; tip.Opacity = 0;
            tip.Text = Loc.T("pool.copyCode");   // вернуть подсказку к исходной
        };
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;                    // не даём ставить курсор в текст
            try
            {
                System.Windows.Clipboard.SetText(code);
                tip.Text = Loc.T("pool.codeCopied");
            }
            catch (Exception ex)
            {
                Log.Write($"код пула не скопировался: {ex.Message}");
                tip.Text = Loc.T("pool.codeCopyBad");
            }
        };

        var file = PoolUi.GoldBtn(Loc.T("pool.saveFile"));
        file.Content = PoolUi.IconLabel("⭱", Loc.T("pool.saveFile"));
        file.HorizontalAlignment = HorizontalAlignment.Stretch;
        file.Click += (_, _) => { if (SaveToFile(dlg, json, suggestedFile)) dlg.Close(); };
        body.Children.Add(file);

        body.Children.Add(CloseRow(dlg));
        dlg.ShowDialog();
    }

    /// <summary>Окно приёма: поле для кода или кнопка «открыть файл».</summary>
    /// <returns>true, если пул добавлен.</returns>
    public static bool Receive(Window owner, bool intoDuo)
    {
        var added = false;
        var dlg = Frame(owner, Loc.T("pool.importFile"), out var body);

        body.Children.Add(Hint(Loc.T("pool.pasteCodeHint")));

        var box = new TextBox
        {
            Text = "", TextWrapping = TextWrapping.Wrap, Height = 92, FontSize = 11,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 8),
        };
        body.Children.Add(box);

        var load = PoolUi.Btn(Loc.T("pool.loadCode"));
        load.HorizontalAlignment = HorizontalAlignment.Stretch;
        load.IsEnabled = false;
        // Кнопка оживает, только когда в поле что-то похожее на код: так видно,
        // что вставилось не то, ещё до нажатия.
        box.TextChanged += (_, _) => load.IsEnabled = PoolFile.LooksLikeCode(box.Text);

        // Вставка из буфера отдельной кнопкой: код прилетает из мессенджера уже
        // скопированным, и Ctrl+V в чужом окне вспоминают не все.
        var paste = PoolUi.Btn(Loc.T("pool.pasteFromClipboard"));
        paste.Click += (_, _) =>
        {
            try
            {
                var text = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
                if (string.IsNullOrWhiteSpace(text))
                {
                    Confirm.Tell(dlg, Loc.T("pool.importFile"), Loc.T("pool.clipboardEmpty"));
                    return;
                }
                box.Text = text.Trim();
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }
            catch (Exception ex)
            {
                Log.Write($"буфер обмена не прочитался: {ex.Message}");
                Confirm.Tell(dlg, Loc.T("pool.importFile"), Loc.T("pool.clipboardEmpty"));
            }
        };

        // Вставить слева, загрузить справа: слева то, что делают первым.
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        paste.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(paste, 0);
        Grid.SetColumn(load, 1);
        row.Children.Add(paste);
        row.Children.Add(load);
        load.Click += (_, _) =>
        {
            var json = PoolFile.FromCode(box.Text);
            if (json is null)
            {
                Confirm.Tell(dlg, Loc.T("pool.importFile"), Loc.T("pool.codeBad"));
                return;
            }
            if (!PoolImport.Apply(dlg, json, intoDuo, null)) return;
            added = true;
            dlg.Close();
        };
        body.Children.Add(row);

        var file = PoolUi.GoldBtn(Loc.T("pool.openFile"));
        file.Content = PoolUi.IconLabel("⭳", Loc.T("pool.openFile"));
        file.HorizontalAlignment = HorizontalAlignment.Stretch;
        file.Click += (_, _) =>
        {
            var open = new Microsoft.Win32.OpenFileDialog
            {
                DefaultExt = PoolFile.Extension,
                Filter = $"Counterplay (*{PoolFile.Extension})|*{PoolFile.Extension}|JSON (*.json)|*.json",
            };
            if (open.ShowDialog(dlg) != true) return;
            if (!PoolImport.Load(dlg, open.FileName, intoDuo)) return;
            added = true;
            dlg.Close();
        };
        body.Children.Add(file);

        body.Children.Add(CloseRow(dlg));
        dlg.ShowDialog();
        return added;
    }

    // ── общая обвязка окон ────────────────────────────────────────────────

    private static Window Frame(Window owner, string title, out StackPanel body)
    {
        var dlg = new Window
        {
            Title = title, Width = 420, SizeToContent = SizeToContent.Height, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(PoolSettingsWindow.Bg),
        };
        PoolUi.Apply(dlg);
        body = new StackPanel { Margin = new Thickness(16) };
        dlg.Content = PoolUi.Chrome(dlg, title, body);
        return dlg;
    }

    private static FrameworkElement Hint(string text) => new TextBlock
    {
        Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0xA0, 0xB2)),
        TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 17,
        Margin = new Thickness(0, 0, 0, 8),
    };

    private static FrameworkElement CloseRow(Window dlg)
    {
        var close = PoolUi.Btn(Loc.T("pool.close"));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.Margin = new Thickness(0, 10, 0, 0);
        close.Click += (_, _) => dlg.Close();
        return close;
    }

    private static bool SaveToFile(Window owner, string json, string suggestedFile)
    {
        var save = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFile,
            DefaultExt = PoolFile.Extension,
            Filter = $"Counterplay (*{PoolFile.Extension})|*{PoolFile.Extension}",
        };
        if (save.ShowDialog(owner) != true) return false;
        try
        {
            File.WriteAllText(save.FileName, json, System.Text.Encoding.UTF8);
            Confirm.Tell(owner, Loc.T("pool.export"), Loc.T("pool.exported", Path.GetFileName(save.FileName)));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"пул не выгрузился: {ex.Message}");
            Confirm.Tell(owner, Loc.T("pool.export"), Loc.T("pool.exportBad"));
            return false;
        }
    }
}
