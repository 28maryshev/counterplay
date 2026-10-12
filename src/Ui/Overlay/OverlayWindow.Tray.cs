using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Counterplay;

// Окно драфта — трей и видимость окна на время игры.
public partial class OverlayWindow
{
    // Сворачивание в системный трей на время игры.
    private System.Windows.Forms.NotifyIcon? _tray;

    private System.Windows.Forms.ToolStripMenuItem? _autostartItem; // галочка «запускать с Windows»

    private bool _inTray;

    // true = свёрнуто пользователем (крестик) — автоматический возврат не разворачивает.
    private bool _userHidden;

    // true = идёт игра / вход в игру: оверлей специально скрыт, авто-показ подавлен.
    // Управляется из геймфлоу (Program). В меню/лобби/чемп-селекте — false.
    private bool _gameActive;

    // true = LCU реально подключён. Пока НЕ подключён, авто-возврат из трея по
    // слежению за окном подавлен — иначе при автозапуске оверлей вылазил бы с
    // экраном «ожидание клиента», как только окно клиента появится (но LCU ещё
    // грузится). Ставится в Program при подключении, снимается при обрыве/ожидании.
    private bool _lcuReady;

    public void SetGameActive(bool active)
    {
        if (_gameActive != active) Log.Write(active ? "игра началась" : "игра закончилась");
        _gameActive = active;
    }

    /// <summary>
    /// Свёрнута ли программа в трей и идёт ли игра.
    ///
    /// Спрашивает перезапуск после обновления: посреди игры перезапускаться
    /// нельзя, а вернуться надо туда же, откуда ушли — был значок в трее,
    /// значит и поднимаемся в трей, а не окном поверх клиента.
    /// </summary>
    public bool InTray => _inTray;

    public bool GameActive => _gameActive;

    /// LCU подключён/отключён — гейтит авто-возврат из трея (см. _lcuReady).
    public void SetLcuReady(bool ready)
    {
        if (_lcuReady != ready) Log.Write(ready ? "клиент LoL подключён" : "клиент LoL отключён");
        _lcuReady = ready;
    }

    /// Разрешить авто-показ снова (сбросить ручное скрытие крестиком). Зовём при
    /// входе в чемп-селект: закрыл окно раньше — на драфте оно всё равно вернётся.
    public void AllowAutoShow() => Dispatcher.InvokeAsync(() => _userHidden = false);

    // ── Системный трей (скрытие на время игры) ────────────────────────────

    private void EnsureTray()
    {
        if (_tray != null) return;
        System.Drawing.Icon trayIcon;
        // Грузим иконку трея в малом системном размере (чётче 16×16, берёт нужный кадр ICO).
        try
        {
            trayIcon = File.Exists(LogoPath)
                ? new System.Drawing.Icon(LogoPath, System.Windows.Forms.SystemInformation.SmallIconSize)
                : System.Drawing.SystemIcons.Application;
        }
        catch { trayIcon = System.Drawing.SystemIcons.Application; }
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon    = trayIcon,
            Text    = "Counterplay",
            Visible = false,
        };
        // Восстановление по двойному клику / меню — принудительное (с ручного сворачивания).
        _tray.DoubleClick += (_, _) => RestoreFromTray(force: true);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(Loc.T("tray.show"), null, (_, _) => RestoreFromTray(force: true));

        // Автозапуск с Windows — переключатель (только в установленной версии).
        if (Autostart.Supported)
        {
            _autostartItem = new System.Windows.Forms.ToolStripMenuItem(Loc.T("tray.autostart"))
            {
                CheckOnClick = true,
                Checked      = Autostart.IsEnabled,
            };
            _autostartItem.Click += (_, _) =>
            {
                var on = Autostart.Set(_autostartItem.Checked);
                Settings.Set("autostart", on);
                _autostartItem.Checked = on; // фактическое состояние (реестр мог не поддаться)
            };
            menu.Items.Add(_autostartItem);
        }

        // Настройки — из трея тоже: окно может быть свёрнуто, а поменять что-то
        // (например, вернуть спрятанный блок) нужно именно тогда.
        menu.Items.Add(Loc.T("settings.title"), null, (_, _) =>
            Dispatcher.Invoke(() => OnSettings(this, new RoutedEventArgs())));

        menu.Items.Add(Loc.T("tray.exit"), null, (_, _) => System.Windows.Application.Current.Shutdown());
        _tray.ContextMenuStrip = menu;

        Closed += (_, _) => { _tray?.Dispose(); _tray = null; };
    }

    /// Старт сразу в трее (автозапуск с Windows): окно не показываем вовсе —
    /// иначе при входе в систему выскакивает «LCU is starting…». Оверлей сам
    /// поднимется, когда появится клиент (RestoreFromTray из фолловера/геймфлоу).
    /// HWND создаём явно: позиционирование и слежение за окном клиента опираются
    /// на него, а без Show() он бы не появился.
    public void StartInTray()
    {
        new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        EnsureTray();
        _inTray = true;
        _tray!.Visible = true;
    }

    /// Свернуть оверлей в трей. userInitiated=true (крестик) — не разворачивать
    /// автоматически (только по клику в трее). Вызывается из фонового потока тоже.
    public void HideToTray(bool userInitiated = false) => Dispatcher.InvokeAsync(() =>
    {
        // Уходим в трей в конце драфта (за 5 секунд до игры) — самое время
        // запомнить, где человек оставил окно.
        if (_lastDraft is not null) RememberDraftPlacement();
        EnsureTray();
        if (userInitiated) _userHidden = true;
        if (_inTray) return;
        Log.Write($"свернулся в трей ({(userInitiated ? "крестик" : "автоматически")})");
        _inTray = true;
        _tray!.Visible = true;
        Hide(); // тихо, без всплывающего уведомления
    });

    /// Вернуть оверлей из трея. force=true — по явному действию пользователя
    /// (клик в трее). Автовозврат (force=false) не сработает, если пользователь
    /// свернул окно сам.
    public void RestoreFromTray(bool force = false) => Dispatcher.InvokeAsync(() =>
    {
        if (_userHidden && !force) return; // свёрнуто вручную — ждём действия пользователя
        _userHidden = false;
        if (!_inTray) return;
        Log.Write($"вернулся из трея ({(force ? "вручную" : "автоматически")})");
        _inTray = false;
        if (_tray != null) _tray.Visible = false;
        Show();
    });
}
