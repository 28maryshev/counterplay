using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

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

    // Появляется снизу и проявляется; уходит быстрее, чем приходит.
    private void ShowPoolHint()
    {
        PoolHintCard.Visibility = Visibility.Visible;
        PoolHintCard.IsHitTestVisible = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PoolHintCard.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        PoolHintMove.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void HidePoolHint()
    {
        if (PoolHintCard.Visibility != Visibility.Visible) return;
        PoolHintCard.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
        // Пока гасла, могла прийти новая подсказка — её не прячем.
        fade.Completed += (_, _) => { if (_poolHint is null) PoolHintCard.Visibility = Visibility.Collapsed; };
        PoolHintCard.BeginAnimation(OpacityProperty, fade);
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

    private void PoolHintLater_Click(object sender, RoutedEventArgs e)
    {
        if (_poolHint is not { } s) return;
        Log.Write("подсказка пула отложена");
        _poolHintLater.Add(s.Key);
        _poolHint = null;
        HidePoolHint();
    }
}
