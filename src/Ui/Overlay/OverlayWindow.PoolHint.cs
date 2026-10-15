using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Counterplay;

// Окно драфта — подсказка режима пула в лобби: с напарником — включить дуо-пул,
// одному — выключить его (на личный пул или обычный подбор). Решает PoolHint.
public partial class OverlayWindow
{
    /// Показанная подсказка; null — карточки нет.
    private PoolSuggestion? _poolHint;

    /// «Не сейчас»: ключи отклонённых подсказок. До конца работы программы —
    /// при другом составе или очереди ключ другой, и подсказка вернётся.
    private readonly HashSet<string> _poolHintLater = [];

    // Значки в заголовке: один человек и двое рядом (сетка 18×16). Ненулевое
    // правило: у двоих плечи заходят друг на друга и сливаются, а не вырезаются.
    private static readonly Geometry HintSolo = (Geometry)Geometry.Parse(
        "F1 M9,1.5 A3.25,3.25 0 1 1 8.99,1.5 Z M2.5,15 C2.5,11.2 5.4,9.4 9,9.4 C12.6,9.4 15.5,11.2 15.5,15 Z")
        .GetAsFrozen();
    private static readonly Geometry HintDuo = (Geometry)Geometry.Parse(
        "F1 M5.5,2 A2.9,2.9 0 1 1 5.49,2 Z M0.5,15 C0.5,11.6 2.8,9.6 5.5,9.6 C8.2,9.6 10.5,11.6 10.5,15 Z " +
        "M12.5,2 A2.9,2.9 0 1 1 12.49,2 Z M7.5,15 C7.5,11.6 9.8,9.6 12.5,9.6 C15.2,9.6 17.5,11.6 17.5,15 Z")
        .GetAsFrozen();

    /// Из конструктора: состав пати, язык и настройки меняют подсказку сами.
    private void InitPoolHint()
    {
        Party.Changed += OnPartyChanged;
        Loc.LanguageChanged += ApplyPoolHintText;
        AppSettings.Changed += OnPoolHintSettings;
        Closed += (_, _) =>
        {
            Party.Changed -= OnPartyChanged;
            Loc.LanguageChanged -= ApplyPoolHintText;
            AppSettings.Changed -= OnPoolHintSettings;
        };
    }

    private void OnPartyChanged() => Dispatcher.InvokeAsync(UpdatePoolHint);

    /// <summary>
    /// Песочница: «Поиск игры» — это новое лобби, отложенные «Не сейчас»
    /// снова в силе. Владелец 15 октября отложил карточку, а потом ждал её на
    /// поиске и не дождался: состав тот же. В бою так и задумано — отложил, и
    /// до смены состава или очереди она не пристаёт.
    /// </summary>
    public void ForgetPoolHintLater() => Dispatcher.InvokeAsync(() =>
    {
        _poolHintLater.Clear();
        UpdatePoolHint();
    });

    private void OnPoolHintSettings() => Dispatcher.InvokeAsync(UpdatePoolHint);

    /// <summary>
    /// Пересчитать подсказку. Зовётся из <see cref="UpdatePoolButtons"/> (смена
    /// режима, очереди, фазы, правка пулов) и при смене состава пати. Та же
    /// подсказка — ничего не трогаем, иначе карточка мигала бы на каждом
    /// событии лобби.
    /// </summary>
    private void UpdatePoolHint()
    {
        var s = CurrentPoolHint();
        if (s is not null && _poolHintLater.Contains(s.Key)) s = null;
        if (s == _poolHint) return;
        _poolHint = s;
        if (s is null) { HidePoolHint(); return; }
        Log.Write($"подсказка пула: {s.Case} → {s.Kind} {(s.Target.Length > 0 ? $"«{s.Target}»" : "")}".TrimEnd());
        ApplyPoolHintText();
        ShowPoolHint();
    }

    private PoolSuggestion? CurrentPoolHint()
    {
        // Кнопок пула нет (подбор выключен, блок скрыт настройкой) — и
        // подсказывать нечего: переключить было бы негде.
        if (PoolButtonsRow.Visibility != Visibility.Visible) return null;

        // Только в фазах лобби. Party нарочно не забывает состав, когда лобби
        // закрывается (он нужен в драфте), и в меню после игры подсказка
        // говорила бы о прошлом составе. Песочница фаз не знает: там лобби —
        // друг, выбранный в панели, а «играю один» — состав известный и пустой.
        if (!Party.Sandbox && _phaseRaw is not ("Lobby" or "Matchmaking" or "ReadyCheck")) return null;

        return PoolHint.Decide(PoolStore.Current(), PoolStore.Queue,
                               Party.Known || Party.Sandbox, Party.Size, Party.Members);
    }

    /// Тексты карточки — из подсказки и текущего языка.
    private void ApplyPoolHintText()
    {
        if (_poolHint is not { } s) return;
        var duo = s.Kind == PoolKind.Duo;
        PoolHintIcon.Data = duo ? HintDuo : HintSolo;
        PoolHintTitle.Text = s.Case switch
        {
            PoolHintCase.WithMate => s.Who.Length > 0 ? Loc.T("poolHint.withMate", s.Who) : Loc.T("poolHint.withMateAnon"),
            PoolHintCase.WithParty => Loc.T("poolHint.withParty"),
            PoolHintCase.MateAway => s.Who.Length > 0 ? Loc.T("poolHint.mateAway", s.Who) : Loc.T("poolHint.mateAwayAnon"),
            _                     => Loc.T("poolHint.alone"),
        };
        PoolHintTitle.ToolTip = PoolHintTitle.Text;
        PoolHintText.Text = s.Kind switch
        {
            PoolKind.Duo  => s.Target.Length > 0 ? Loc.T("poolHint.toDuo", s.Target) : Loc.T("poolHint.toDuoAny"),
            PoolKind.Pool => s.Target.Length > 0 ? Loc.T("poolHint.toPool", s.Target) : Loc.T("poolHint.toPoolAny"),
            _             => Loc.T("poolHint.toNormal"),
        };
        PoolHintApplyBtn.Content = Loc.T(duo ? "poolHint.turnOn" : "poolHint.switch");
    }

    /// <summary>
    /// Сколько карточка ждёт ответа. Кончилось — уходит, как по «Не сейчас»:
    /// иначе всплывала бы снова на каждом событии лобби. Владелец 15 октября:
    /// «через 15 сек скрывается, если ничего не нажать». Проверки укорачивают.
    /// </summary>
    internal static double PoolHintSeconds = 15;

    /// Отсчёт полосы ожидания: управляемый, чтобы держать его под курсором.
    private Storyboard? _poolHintClock;

    // Появляется снизу и проявляется, с бликом и вспышкой рамки — чтобы было
    // заметно; уходит быстрее, чем приходит.
    private void ShowPoolHint()
    {
        PoolHintCard.Visibility = Visibility.Visible;
        PoolHintCard.IsHitTestVisible = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PoolHintCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PoolHintMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        // Кнопки и полоса под карточкой гаснут: у неё углы скруглены, у них
        // нет, и уголки кнопок торчали из-под рамки.
        PoolUnder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        FlashPoolHint();
        StartPoolHintClock();
    }

    /// <summary>
    /// Блик — косая светлая полоса пробегает по карточке слева направо — и
    /// вспышка свечения рамки, которое потом оседает до обычного.
    /// </summary>
    private void FlashPoolHint()
    {
        // Карточка только что была скрыта и своей ширины ещё не знает — берём
        // ширину ячейки, в которой она встанет.
        var width = PoolArea.ActualWidth > 0 ? PoolArea.ActualWidth : 280;
        PoolHintShine.Opacity = 1;
        PoolHintShineMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-90, width + 30, TimeSpan.FromMilliseconds(750))
            {
                BeginTime = TimeSpan.FromMilliseconds(120),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        var glow = new DoubleAnimationUsingKeyFrames();
        glow.KeyFrames.Add(new LinearDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260))));
        glow.KeyFrames.Add(new EasingDoubleKeyFrame(0.55, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1100)),
                                                    new SineEase { EasingMode = EasingMode.EaseOut }));
        PoolHintGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glow);
    }

    /// Полоса ожидания — с начала. Новая подсказка ждёт свои полные 15 секунд.
    private void StartPoolHintClock()
    {
        _poolHintClock?.Stop(this);
        var bar = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(PoolHintSeconds));
        Storyboard.SetTarget(bar, PoolHintTimer);
        Storyboard.SetTargetProperty(bar, new PropertyPath("RenderTransform.ScaleX"));
        var clock = new Storyboard();
        clock.Children.Add(bar);
        var shown = _poolHint;
        // Сменилась или ушла сама — этот отсчёт уже ни при чём.
        clock.Completed += (_, _) =>
        {
            if (shown is not null && ReferenceEquals(_poolHint, shown)) DismissPoolHint("время вышло");
        };
        _poolHintClock = clock;
        clock.Begin(this, true);
        if (PoolHintCard.IsMouseOver) clock.Pause(this);
    }

    // Курсор над карточкой — её читают, отсчёт стоит.
    private void PoolHint_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) =>
        _poolHintClock?.Pause(this);

    private void PoolHint_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_poolHint is not null) _poolHintClock?.Resume(this);
    }

    // Скругление внутреннего слоя — по рамке (8 снаружи минус 1,5 толщины).
    private void PoolHintInner_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PoolHintInner.Clip = new RectangleGeometry(new Rect(e.NewSize), 6.5, 6.5);

    private void HidePoolHint()
    {
        if (PoolHintCard.Visibility != Visibility.Visible) return;
        // Пауза, а не стоп: стоп вернул бы полосу к полной на время угасания.
        _poolHintClock?.Pause(this);
        PoolHintCard.IsHitTestVisible = false;
        PoolUnder.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        // Пока гасла, могла прийти новая подсказка — её не прячем.
        fade.Completed += (_, _) => { if (_poolHint is null) PoolHintCard.Visibility = Visibility.Collapsed; };
        PoolHintCard.BeginAnimation(OpacityProperty, fade);
    }

    /// «Не сейчас» — кнопкой или по истечении времени: до смены состава или
    /// очереди эта подсказка не вернётся.
    private void DismissPoolHint(string why)
    {
        if (_poolHint is not { } s) return;
        Log.Write($"подсказка пула отложена: {why}");
        _poolHintLater.Add(s.Key);
        _poolHint = null;
        HidePoolHint();
    }

    /// Принять: включить предложенный режим, как это сделала бы его кнопка.
    private void PoolHintApply_Click(object sender, RoutedEventArgs e)
    {
        if (_poolHint is not { } s) return;
        Log.Write($"подсказка пула принята: {s.Kind}");
        _poolHint = null;
        HidePoolHint();
        if (s.Kind != PoolKind.Normal && s.Id is null)
        {
            // Подходящих пулов несколько и звезды нет — выбор за игроком.
            ShowPoolMenu(s.Kind, s.Kind == PoolKind.Pool ? PoolPoolBtn : PoolDuoBtn);
            return;
        }
        PoolStore.SetActive(s.Kind, s.Id);
        UpdatePoolButtons();
        RefreshPoolSlot();
    }

    private void PoolHintLater_Click(object sender, RoutedEventArgs e) => DismissPoolHint("«Не сейчас»");
}
