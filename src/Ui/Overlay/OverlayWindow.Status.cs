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

// Окно драфта — строка состояния, экран ожидания, полосы загрузки, версия и обновление.
public partial class OverlayWindow
{
    /// Геймфлоу сообщает, идёт ли игра (или вход в неё). В это время авто-возврат
    /// оверлея из трея по слежению за окном подавлен — иначе окно всплыло бы поверх игры.
    /// Версия программы под дисклеймером — чтобы в поддержке было видно, с какой
    /// сборки пришёл отчёт, и человек сам понимал, обновился он или нет.
    public void SetVersion(string version) => Dispatcher.InvokeAsync(() =>
        VersionText.Text = "v" + version);

    /// Обновление скачано и встанет при следующем запуске: говорим об этом прямо
    /// и мигаем — иначе программа, живущая в трее сутками, так и останется старой.
    ///
    /// Мигание — несколько раз при каждом появлении окна, потом надпись просто
    /// горит жёлтым. Вечное мигание до перезапуска рисовало окно 60 раз в
    /// секунду сутками (игрок: «10% видеокарты», 17.11.2026), а запущенное, пока
    /// окно в трее, и вовсе отмигало бы впустую.
    public void ShowUpdateReady(string version) => Dispatcher.InvokeAsync(() =>
    {
        VersionText.Text = Loc.T("ready.updateReady", version);
        VersionText.Foreground = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xF5, 0xD7, 0x7A));
        VersionText.FontWeight = FontWeights.Bold;
        if (!_updateReady)
        {
            _updateReady = true;
            VersionText.IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) BlinkUpdateReady(); };
        }
        if (VersionText.IsVisible) BlinkUpdateReady();
    });

    private bool _updateReady;

    private void BlinkUpdateReady() =>
        VersionText.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 1.0, To = 0.25, Duration = TimeSpan.FromSeconds(0.9),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(4),
            FillBehavior = FillBehavior.Stop,   // доиграло — надпись снова непрозрачная
        });

    /// Точки экрана ожидания мигают, только пока их видно: панель ожидания
    /// скрыта, окно в трее — часы анимации стоят. Запуск по Loaded в разметке
    /// крутил их с первой секунды и до закрытия программы.
    private void InitLoadingDots() =>
        LoadingDots.IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) (_dotsWave ??= DotsWave()).Begin(this, isControllable: true);
            else _dotsWave?.Stop(this);   // точки возвращаются к тусклым 0,2 из разметки
        };

    private Storyboard? _dotsWave;

    /// Волна по трём точкам: каждая гаснет и разгорается за 1,4 с, следующая —
    /// на 0,4 с позже; цикл 2,2 с с паузой после третьей.
    private Storyboard DotsWave()
    {
        var wave = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            var blink = new DoubleAnimation(1.0, 0.2, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true,
                BeginTime = TimeSpan.FromSeconds(0.4 * i),
            };
            Storyboard.SetTarget(blink, dots[i]);
            Storyboard.SetTargetProperty(blink, new PropertyPath(OpacityProperty));
            wave.Children.Add(blink);
        }
        return wave;
    }

    /// <summary>
    /// Иконки доехали — перерисовать то, что сейчас на экране.
    ///
    /// Разогрев идёт фоном, а <see cref="IconCache.Get"/> отдаёт картинку один
    /// раз, в момент отрисовки. Карточка, нарисованная до разогрева, так и
    /// осталась бы без портрета — до следующего события драфта, а на экране
    /// ожидания и вовсе навсегда.
    /// </summary>
    public void IconsArrived() =>
        Dispatcher.InvokeAsync(() =>
        {
            // Тир-лист кэширует колонки ВМЕСТЕ с картинками — «статичен в
            // пределах патча». Собрался он до разогрева — и пустые слоты
            // остались бы до смены патча, никакая перерисовка бы не помогла.
            _tierCols = null;
            _banTierCols = null;

            // Экран ожидания рисуется один раз и сам себя не обновляет. Пустая
            // лента винрейтов держалась там до конца игры: владелец это и
            // увидел — «картинки вообще не прогружаются», а после игры пришли.
            if (_session is not null) ShowSession(_session);

            RenderCurrentState();
            if (_lastDraft is not null) UpdateRecommendations(_lastRecs, _lastDraft, _engine);
        });

    /// <summary>
    /// Подъехали ходовые предметы очередного врага.
    ///
    /// Карточки уже нарисованы, и строка контр-предметов в них собрана по
    /// одному лишь классу: «магу такое по руке». Настоящие сборки приходят
    /// секундой позже и половину этой строки отменяют — значит карточки надо
    /// пересобрать, иначе игрок так и останется с предметом, которого на том
    /// чемпионе никто не покупает.
    /// </summary>
    public void BuildsArrived() =>
        Dispatcher.InvokeAsync(() =>
        {
            if (_lastDraft is not null) UpdateRecommendations(_lastRecs, _lastDraft, _engine);
        });

    public void ShowStatus(string msg) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = msg;
            DlBar.Visibility = Visibility.Collapsed;
            PulseAnim(false);
            SetLoadingMode();
            ShowIdle();
        });

    // Состояние строки «Готов»: сырая фаза LCU и/или произвольный текст — храним
    // сырьё, чтобы при смене языка перелокализовать на лету.
    private string? _readyPhaseRaw;  // фаза геймфлоу; null — просто «Готов»

    private string? _readyCustom;    // произвольная строка (тестовый режим)

    // Готов и простаивает: вместо «Готов» — сноска о программе + карусель советов.
    // status == null → локализованное «Готов» (перерисуется при смене языка).
    public void ShowReady(string? status = null) =>
        Dispatcher.InvokeAsync(() =>
        {
            _readyCustom = status;
            _readyPhaseRaw = null;
            ShowReadyCore();
        });

    // Готов с фазой геймфлоу (сырое имя из LCU — локализуется при рендере).
    public void ShowReadyPhase(string rawPhase) =>
        Dispatcher.InvokeAsync(() =>
        {
            _readyCustom = null;
            // Вышли из послематчевого экрана в меню — самое время показать, что
            // изменилось за игру.
            var left = AfterGamePhase(_phaseRaw) && !AfterGamePhase(rawPhase);
            _phaseRaw = rawPhase;
            _readyPhaseRaw = rawPhase;
            ShowReadyCore();

            if (left && _heldSession is { } held)
            {
                _heldSession = null;
                ShowSession(held);
            }
        });

    private void ShowReadyCore()
    {
        ApplyReadyText();
        DlBar.Visibility = Visibility.Collapsed;
        PulseAnim(false);
        LoadingInfo.Visibility = Visibility.Collapsed;
        ReadyScroll.Visibility = Visibility.Visible;
        UpdatePoolButtons();
        ShowIdle();
    }

    private void ApplyReadyText() =>
        ReadyStatusText.Text = _readyCustom
            ?? (_readyPhaseRaw is null
                ? Loc.T("status.readyIdle")
                : PhaseDisplay(_readyPhaseRaw));

    // Строка состояния для фазы геймфлоу. Сначала берём человеческое описание
    // (phaseState.* — «что сейчас происходит и чего ждать от программы»),
    // и только если его нет — сухое имя фазы, как раньше.
    private static string PhaseDisplay(string phase)
    {
        var lower = phase.ToLowerInvariant();

        var stateKey = "phaseState." + lower;
        var state = Loc.T(stateKey);
        if (state != stateKey) return state;

        var key = "phase." + lower;
        var t = Loc.T(key);
        return Loc.T("status.readyPhase", t == key ? phase : t);
    }

    // Прогресс загрузки со строкой состояния и полосой (обновление/база).
    public void ShowProgress(string text, double fraction) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = text;
            DlBar.Visibility    = Visibility.Visible;
            DlBarFill.Width     = 260.0 * Math.Clamp(fraction, 0.0, 1.0);
            PulseAnim(true);   // скользящий блик — бар «живой» даже на плато %
            SetLoadingMode();
            ShowIdle();
        });

    /// <summary>
    /// Прогресс фонового обновления базы — тонкой полосой в подвале, у версии.
    ///
    /// Отличие от <see cref="ShowProgress"/> в том, чего тут НЕТ: экрана
    /// загрузки. Пока качается новая база, программа работает на прежней —
    /// драфт разбирается, рекомендации выдаются. Полоса внизу только говорит,
    /// сколько осталось.
    /// </summary>
    /// <summary>
    /// Прогресс фоновой загрузки — тонкой полосой в подвале, у версии.
    ///
    /// Отличие от <see cref="ShowProgress"/> в том, чего тут НЕТ: экрана
    /// загрузки. Пока качается новая база или новая версия, программа работает —
    /// драфт разбирается, рекомендации выдаются. Полоса внизу только говорит,
    /// сколько осталось.
    /// </summary>
    /// <param name="owner">Чья полоса: «app» — новая версия, иначе база. Полосы
    /// РАЗНЫЕ, а не одна на двоих: сторожа ходят с разной частотой (раз в час и
    /// раз в три), рано или поздно совпадут, и одна полоса на двоих показывала
    /// бы то одно, то другое.</param>
    /// <param name="text">Подробности для подсказки: что именно и с какой
    /// скоростью. В строку они не лезут, а при наведении пригодятся.</param>
    public void ShowSideProgress(string text, double fraction, string owner = "db") =>
        Dispatcher.InvokeAsync(() =>
        {
            var app  = owner == "app";
            var box  = app ? SideDlApp     : SideDlDb;
            var pct  = app ? SideDlAppPct  : SideDlDbPct;
            var fill = app ? SideDlAppFillT : SideDlDbFillT;

            var part = Math.Clamp(fraction, 0.0, 1.0);
            box.Visibility = Visibility.Visible;
            box.ToolTip    = text;
            pct.Text       = $"{part * 100:0}%";
            fill.ScaleX    = part;
        });

    /// Убрать свою полосу: скачали, бросили или нечего было качать. Чужую не
    /// трогаем — она может догорать.
    public void HideSideProgress(string owner = "db") =>
        Dispatcher.InvokeAsync(() =>
            (owner == "app" ? SideDlApp : SideDlDb).Visibility = Visibility.Collapsed);

    // Неопределённый прогресс: фоновая работа без процентов (распаковка/проверка).
    // Полоса полная, но блик продолжает бежать — видно, что процесс идёт.
    public void ShowProgressBusy(string text) =>
        Dispatcher.InvokeAsync(() =>
        {
            IdleStatusText.Text = text;
            DlBar.Visibility    = Visibility.Visible;
            DlBarFill.Width     = 260.0;
            PulseAnim(true);
            SetLoadingMode();
            ShowIdle();
        });

    // Управление бегущим бликом на полосе прогресса. Часы блика
    // останавливаются явно (Loops): до 26.11 он снимался BeginAnimation(…, null),
    // и после загрузки окно рисовало кадры до сборки мусора. Частота — полная:
    // блик едет, а не мигает, на 24 кадрах он шёл бы рывками, и живёт он
    // только пока идёт загрузка.
    private bool _pulseOn;

    private void PulseAnim(bool on)
    {
        if (on == _pulseOn) return;
        _pulseOn = on;
        if (on)
        {
            DlBarPulse.Visibility = Visibility.Visible;
            Loops.Start(DlBarPulseT, System.Windows.Media.TranslateTransform.XProperty,
                new DoubleAnimation(-70, 260, new Duration(TimeSpan.FromSeconds(1.1))));
        }
        else
        {
            Loops.Stop(DlBarPulseT, System.Windows.Media.TranslateTransform.XProperty);
            DlBarPulse.Visibility = Visibility.Collapsed;
        }
    }

    // ── Карусель советов по пику (поле фиксированного размера, смена раз в 40 с) ──
    // Переключение на стадию загрузки/ожидания — прячем готовность.
    private void SetLoadingMode()
    {
        LoadingInfo.Visibility = Visibility.Visible;
        ReadyScroll.Visibility = Visibility.Collapsed;
        // _tipTimer?.Stop(); // карусель советов (см. блок ниже)
    }

    /* ── Карусель советов: временно скрыта — на её месте BETA-плашка. ─────────
       Вернуть: раскомментировать этот блок и Border с TipText в XAML,
       StartTips() в ShowReadyCore, _tipTimer?.Stop() в SetLoadingMode,
       а в OnLanguageChanged — сброс _tipIdx = -1 и StartTips() при видимом ReadyInfo.

    // Тексты — из локализации (assets/i18n/{lang}.json, ключ "tips").
    private static string[] Tips => Loc.TArray("tips");

    private int _tipIdx = -1;
    private System.Windows.Threading.DispatcherTimer? _tipTimer;

    private void StartTips()
    {
        if (_tipTimer == null)
        {
            _tipTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(40) };
            _tipTimer.Tick += (_, _) => NextTip();
        }
        if (_tipIdx < 0 && Tips.Length > 0)
        {
            // случайный стартовый совет — чтобы не всегда первый
            _tipIdx = new Random().Next(Tips.Length) - 1;
            NextTip();
        }
        _tipTimer.Start();
    }

    private void NextTip()
    {
        var tips = Tips;
        if (tips.Length == 0) return;
        _tipIdx = (_tipIdx + 1) % tips.Length;
        TipText.Text = tips[_tipIdx];
        // мягкое проявление при смене
        TipText.BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0.0, 1.0,
                new Duration(TimeSpan.FromMilliseconds(350))));
    }
    ────────────────────────────────────────────────────────────────────────── */

    // Экран ожидания: маленькое окно, спиннер и крупная подпись стадии.
    private void ShowIdle()
    {
        // Запоминаем размер полного режима перед сворачиванием в ожидание.
        if (IdlePanel.Visibility != Visibility.Visible && _isFullMode)
        {
            _savedFullW = Width;
            _savedFullH = Height;
        }

        IdlePanel.Visibility     = Visibility.Visible;
        CompactScroll.Visibility = Visibility.Collapsed;
        FullView.Visibility      = Visibility.Collapsed;

        SizeToContent = SizeToContent.Height; // высота по контенту
        MinWidth  = 0;
        MinHeight = 0;
        Width     = Scaled(IdleW);
        LimitHeightToClient();   // но не выше окна клиента — остальное прокруткой

        // Драфт закончился — панель профиля возвращается к краю клиента, даже
        // если в драфте окно стояло поверх него по своей раскладке. Флаг снимаем
        // здесь, а не по событию конца драфта: события геймфлоу и чемп-селекта
        // приходят в разном порядке, и панель успевала показаться на старом месте.
        //
        // Но ТОЛЬКО когда драфта уже нет. Сюда же приходят из середины драфта:
        // подбор бывает пустым (роль ещё не раскрыта, фаза банов), и тогда
        // показывается экран ожидания. Снимая флаги здесь без разбора, мы на
        // следующем же чужом пике раскладывали окно заново — человек отодвинул
        // его под себя, а оно прыгало обратно. У кого роль известна с первой
        // секунды, этого не видно совсем: пустого подбора у него не бывает.
        if (_lastDraft is null)
        {
            _placementApplied = false;

            // Раз раскладку драфта выбирает программа, она же отвечает и за
            // возврат: снимаем «окно двигали руками», иначе одно давнее
            // перетаскивание отключало привязку навсегда и панель оставалась
            // там, где стоял драфт. При раскладке «сбоку» положение пользователя
            // не трогаем — там он сам хозяин окна.
            if (AppSettings.Current.DraftPlacement != "right") _userMoved = false;
        }

        if (_inTray) return; // во время игры окно скрыто в трее
        Show();
        AnchorIfNotMoved();
        KeepReachable();
        ApplyScale();
    }
}
