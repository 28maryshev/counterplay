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

// Окно драфта — окно настроек, применение настроек и смена языка.
public partial class OverlayWindow
{
    private void OnLanguageChanged()
    {
        _tierCols = null;             // роли/тултипы тир-листа под новую локаль
        _banTierCols = null;
        // Полоса чемпионов роли кэшируется по роли и при простой перерисовке не
        // пересобирается — иначе её заголовок и подсказки остаются на прежнем
        // языке, пока игрок не сменит роль. Сбрасываем кэш вместе с языком.
        _rolePoolCells = null;
        _rolePoolRole  = "";
        // Обоснования рекомендаций — ГОТОВЫЕ строки, собранные движком через Loc.T
        // на прежнем языке. Перерисовка их не переводит — перегенерируем движком из
        // сохранённого драфта, иначе центральная колонка остаётся на старом языке.
        if (_engine is not null && _lastDraft is not null && _lastRecs is not null)
            _lastRecs = _lastDraft.IsAram
                ? _engine.RecommendAram(_lastDraft, AppSettings.Current.DraftCount)
                : _engine.Recommend(_lastDraft, AppSettings.Current.DraftCount);
        RenderCurrentState();         // мгновенно перерисовываем интерфейс

        // Тексты, выставляемые из кода: строка «Готов · фаза» и панель трекера
        // (ранг/W-L/подсказка) — перелокализуем из сохранённого сырья.
        ApplyReadyText();
        ShowSession(_session);
        ApplyNotice();                // текст сообщения — под новый язык

        _ = ReloadNamesAsync();       // имена чемпионов под новую локаль
    }

    private async Task ReloadNamesAsync()
    {
        try
        {
            // Имена чемпионов + названия/описания рун + названия предметов — всё
            // под новую локаль (руны/предметы приходят из Data Dragon переведёнными).
            await DataDragon.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
            await RuneIcons.LoadAsync(Loc.DDragonLocale, CancellationToken.None);
            await ItemIcons.LoadNamesAsync(Loc.DDragonLocale, CancellationToken.None);
            await Dispatcher.InvokeAsync(() =>
            {
                RenderCurrentState();
                // Панель рун открыта — перерисовать с переведёнными названиями.
                if (_runeStats is not null && RunesBar.Visibility == Visibility.Visible)
                    ShowRunes(_runeStats, _runeChampId, _runeChampName, _runeRole, _lastOpponentId);
            });
        }
        catch { /* офлайн — имена обновятся при следующей загрузке */ }
    }

    private SettingsWindow? _settingsWin;

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWin is { IsVisible: true }) { _settingsWin.Activate(); return; }
        try
        {
            _settingsWin = new SettingsWindow { Owner = this };
            _settingsWin.Closed += (_, _) => _settingsWin = null;
            _settingsWin.Show();
        }
        catch (Exception ex)
        {
            // Настройки — не тот повод, чтобы ронять оверлей посреди драфта:
            // показываем причину в строке состояния и продолжаем работать.
            _settingsWin = null;
            Console.WriteLine("Settings window failed: " + ex);
            ShowStatus("Settings: " + ex.Message);
        }
    }

    /// Применяет настройки к уже построенным экранам. Вызывается при запуске и
    /// после каждого изменения — окно настроек можно не закрывать, эффект виден
    /// сразу на живом оверлее.
    private void ApplySettings()
    {
        var s = AppSettings.Current;
        Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

        // Экран ожидания
        RankCard.Visibility       = V(s.ReadyRank);
        Last5Panel.Visibility     = V(s.ReadyLast5);
        WinrateRow.Visibility     = V(s.ReadyWinrate);
        // Пул нужен только подбору: выключен подбор — вместо кнопок объяснение,
        // почему их нет и где включить обратно.
        var poolUseful = s.ReadyPool && s.DraftEnabled;
        PoolTitleRow.Visibility   = V(poolUseful);
        PoolButtonsRow.Visibility = V(poolUseful);
        DraftOffNotice.Visibility = V(!s.DraftEnabled);
        ReadyStatusText.Visibility = V(s.ReadyPhase);
        BetaCard.Visibility       = V(s.ReadyBeta);
        // Полосу чемпионов гасим только принудительно: сама она появляется
        // лишь когда игры есть (см. FillMyChamps).
        if (!s.ReadyChamps) MyChampsBox.Visibility = Visibility.Collapsed;
        WrChart.Visibility = V(s.ReadyWinrate && s.ChartMode != "off");
        DrawWrChart();

        // Драфт
        // Показывать полосу урона решает RenderDamageMix (у неё есть данные);
        // отсюда только гасим, если настройка выключена.
        if (!s.DraftDamage)
        {
            MyDmgPanel.Visibility    = Visibility.Collapsed;
            EnemyDmgPanel.Visibility = Visibility.Collapsed;
        }
        if (!s.DraftCombos)
        {
            MyCombosHeader.Visibility    = Visibility.Collapsed;
            MyTeamCombos.Visibility      = Visibility.Collapsed;
            EnemyCombosHeader.Visibility = Visibility.Collapsed;
            EnemyTeamCombos.Visibility   = Visibility.Collapsed;
            MyTeamLines.Children.Clear();
            EnemyTeamLines.Children.Clear();
        }
        else
        {
            MyTeamCombos.Visibility    = Visibility.Visible;
            EnemyTeamCombos.Visibility = Visibility.Visible;
        }
        if (!s.DraftRunes) RunesBar.Visibility = Visibility.Collapsed;
        if (!s.DraftRolePool) RolePoolBar.Visibility = Visibility.Collapsed;
        if (!s.BansTierList) TierListBar.Visibility = Visibility.Collapsed;

        // Прозрачность, масштаб и «поверх всех» — свойства самого окна.
        Opacity = Math.Clamp(s.Opacity, 0.5, 1.0);
        ApplyScale();
        if (s.AlwaysOnTop) Topmost = true;

        // Размер окна тоже пересчитываем: настройка «под клиент» меняет и его.
        if (SizeToContent == SizeToContent.Height) Width = Scaled(IdleW);
        else if (!_placementApplied) { MinWidth = Scaled(MinW); MinHeight = Scaled(MinH); }

        // Компактная карточка ранга: эмблема мельче, полосы прогресса нет.
        RankEmblem.Height = s.ReadyCompact ? 46 : 74;
        RankProgressTrack.Visibility = V(s.ReadyRank && !s.ReadyCompact);
        // Засечки живут вместе с полосой: без неё они висели бы в пустоте.
        RankTicks.Visibility = V(s.ReadyRank && !s.ReadyCompact);

        // Команды местами: моя команда справа, враги слева.
        Grid.SetColumn(MyTeamPanel,    s.DraftMirror ? 2 : 0);
        Grid.SetColumn(EnemyTeamPanel, s.DraftMirror ? 0 : 2);
        MyTeamPanel.Margin    = s.DraftMirror ? new Thickness(10, 0, 0, 0) : new Thickness(0, 0, 10, 0);
        EnemyTeamPanel.Margin = s.DraftMirror ? new Thickness(0, 0, 10, 0) : new Thickness(10, 0, 0, 0);

        // Карточки и слоты перестраиваются с новыми правилами.
        if (_lastDraft is not null) RenderCurrentState();
    }
}
