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

// Окно драфта — пик и бан прямо из окна: наведение в клиенте кликом и кнопки
// подтверждения.
public partial class OverlayWindow
{
    /// Навести чемпиона в клиенте (hover). championId → HTTP-код (0 — исключение).
    public Func<int, Task<int>>? HoverHandler { get; set; }

    /// Залочить наведённого чемпиона (необратимо). championId → HTTP-код.
    public Func<int, Task<int>>? LockHandler { get; set; }

    // ── Выбор чемпиона через интерфейс (hover кликом + кнопка лока) ───────────
    private int _pickHoverId;     // наведённый кликом чемпион (0 = нет)

    private bool _pickBusy;       // идёт запрос к клиенту

    private DateTime _pickMsgUntil; // до этого времени не затирать текст ошибки

    private async void RecCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        if (HoverHandler is null) return;   // не боевой режим/нет actionId
        e.Handled = true;

        _pickHoverId = champId;
        RenderCurrentState();          // перерисуем карточки — рамка выбранного золотится
        UpdatePickBar();               // и подсветим кнопку выбранным чемпионом
        var code = await HoverHandler(champId);   // навести в клиенте
        // Если клиент не принял ховер — сразу сообщаем на кнопке (с кодом).
        if (code is < 200 or >= 300)
        {
            PickText.Text = $"{Loc.T("pick.failed")} ({code})";
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(4);
        }
    }

    // Кнопка «Выбрать»: лочим наведённого чемпиона (необратимо, только в свой ход).
    private async void PickConfirm_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        // Кнопка на экране, а нажатие не делает ничего — разбирать такое вслепую
        // нельзя: причин у молчаливого выхода три, и снаружи они неразличимы.
        var champ = PickTarget;
        if (LockHandler is null || champ <= 0 || _pickBusy)
        {
            Log.Write($"пик: нажатие вхолостую (клиент={(LockHandler is null ? "нет" : "есть")}, " +
                      $"чемпион={champ}, занято={_pickBusy})");
            return;
        }
        if (_lastDraft is null || !_lastDraft.MyPickInProgress)
        {
            // Не мой ход — говорим об этом, а не молчим.
            PickText.Text = Loc.T("pick.failed");
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(3);
            Log.Write("пик: не мой ход");
            return;
        }

        _pickBusy = true;
        try
        {
            PickText.Text = Loc.T("pick.locking");
            _pickMsgUntil = DateTime.UtcNow.AddSeconds(2);
            var code = await LockHandler(champ);
            if (code is < 200 or >= 300)
            {
                // Ошибка «липнет» на 4 секунды — иначе её мгновенно затрёт
                // перерисовка от очередного события сессии.
                PickText.Text = $"{Loc.T("pick.failed")} ({code})";
                _pickMsgUntil = DateTime.UtcNow.AddSeconds(4);
                return;
            }
            // Успех: клиент сам обновит сессию, карточки исчезнут — прячем плашку.
            PickBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            // Через finally: сорвись запрос с исключением — флаг остался бы
            // поднятым, и кнопка молчала бы до перезапуска программы.
            _pickBusy = false;
        }
    }

    // Показ/скрытие плашки выбора: видна только когда мой ход пикать и есть
    // наведённый кликом чемпион. Иконку/имя берём из выбранного.
    /// <summary>
    /// Чемпион, которого подтвердит кнопка: выбранный кликом у нас, иначе тот,
    /// что игрок навёл в самом клиенте.
    ///
    /// Второе здесь не для полноты. Руны и сборка показываются уже по ховеру —
    /// хоть нашему, хоть клиентскому, — и когда чемпиона выбирали привычным
    /// способом, прямо в клиенте, получалось нелепое: подсказки на экране есть,
    /// а подтвердить их нечем, кнопки нет.
    /// </summary>
    private int PickTarget =>
        _pickHoverId > 0 ? _pickHoverId : _lastDraft?.Me?.EffectiveChampionId ?? 0;

    private void UpdatePickBar()
    {
        var draft = _lastDraft;
        var champ = PickTarget;
        bool canPick = HoverHandler != null && LockHandler != null
                       && draft is { MyPickInProgress: true } && champ > 0;
        if (!canPick) { PickBar.Visibility = Visibility.Collapsed; return; }

        PickIcon.Source = IconCache.Get(champ);
        // Пока показывается ошибка/статус — не затираем текст обычной подписью.
        if (DateTime.UtcNow >= _pickMsgUntil)
            PickText.Text = Loc.T("pick.confirm", DataDragon.Name(champ));
        PickBar.Visibility = Visibility.Visible;
    }

    // ── Бан через интерфейс (клик по карточке бана + кнопка «Забанить») ────────
    /// Навести бан в клиенте (обратимо). championId → HTTP-код.
    public Func<int, Task<int>>? BanHoverHandler { get; set; }

    /// Завершить бан (необратимо). championId → HTTP-код.
    public Func<int, Task<int>>? BanLockHandler  { get; set; }

    private int _banHoverId;

    private bool _banBusy;

    private DateTime _banMsgUntil;

    private async void BanCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        if (BanHoverHandler is null) return;
        // Его уже забанил союзник — второй раз клиент не даст.
        if (_lastDraft is { } now && !StillAvailable(now, champId)) { e.Handled = true; return; }
        e.Handled = true;
        _banHoverId = champId;
        RenderCurrentState();          // перерисуем баны — рамка выбранного краснеет
        UpdateBanBar();
        var code = await BanHoverHandler(champId);
        if (code is < 200 or >= 300)
        {
            BanText.Text = $"{Loc.T("ban.failed")} ({code})";
            _banMsgUntil = DateTime.UtcNow.AddSeconds(4);
        }
    }

    private async void BanConfirm_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (BanLockHandler is null || _banHoverId <= 0 || _banBusy)
        {
            Log.Write($"бан: нажатие вхолостую (клиент={(BanLockHandler is null ? "нет" : "есть")}, " +
                      $"чемпион={_banHoverId}, занято={_banBusy})");
            return;
        }
        if (_lastDraft is null || !_lastDraft.MyBanInProgress)
        {
            BanText.Text = Loc.T("ban.failed");
            _banMsgUntil = DateTime.UtcNow.AddSeconds(3);
            Log.Write("бан: не мой ход");
            return;
        }
        _banBusy = true;
        try
        {
            BanText.Text = Loc.T("ban.banning");
            _banMsgUntil = DateTime.UtcNow.AddSeconds(2);
            var code = await BanLockHandler(_banHoverId);
            if (code is < 200 or >= 300)
            {
                BanText.Text = $"{Loc.T("ban.failed")} ({code})";
                _banMsgUntil = DateTime.UtcNow.AddSeconds(4);
                return;
            }
            BanBar.Visibility = Visibility.Collapsed;
        }
        finally { _banBusy = false; }   // см. пик: иначе одна осечка глушит кнопку
    }

    // Ещё можно навести этого чемпиона? Забанённые и уже залоченные кем-то —
    // нельзя. Ховер сбрасываем ИМЕННО по этому признаку, а не по «нет в списке
    // рекомендаций»: выбор из тир-листа почти никогда не совпадает с топ-6
    // советов, и такой сброс гасил кнопку бана сразу после клика.
    private static bool StillAvailable(DraftState d, int champId) =>
        champId > 0
        && !d.MyTeamBans.Contains(champId)
        && !d.TheirTeamBans.Contains(champId)
        && d.MyTeam.Concat(d.TheirTeam).All(p => p.ChampionId != champId);

    private void UpdateBanBar()
    {
        var draft = _lastDraft;
        bool canBan = BanHoverHandler != null && BanLockHandler != null
                      && draft is { MyBanInProgress: true } && _banHoverId > 0;
        if (!canBan) { BanBar.Visibility = Visibility.Collapsed; return; }

        BanIcon.Source = IconCache.Get(_banHoverId);
        if (DateTime.UtcNow >= _banMsgUntil)
            BanText.Text = Loc.T("ban.confirm", DataDragon.Name(_banHoverId));
        BanBar.Visibility = Visibility.Visible;
    }

    // Клик по чемпиону в тир-листе: в банфазе — наводим бан, иначе — пик.
    // В фазе объявления банить ещё нельзя — клик показывает пик союзникам.
    // Переиспользуем те же обработчики, что и карточки рекомендаций.
    private void TierCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not int champId || champId <= 0) return;
        e.Handled = true;
        if (_lastDraft is { InBanPhase: true, InPlanning: false })
        {
            if (BanHoverHandler is null) return;
            _banHoverId = champId;
            RenderCurrentState();
            UpdateBanBar();
            _ = BanHoverHandler(champId);
        }
        else
        {
            if (HoverHandler is null) return;
            _pickHoverId = champId;
            RenderCurrentState();
            UpdatePickBar();
            _ = HoverHandler(champId);
        }
    }
}
