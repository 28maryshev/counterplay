namespace Counterplay;

/// <summary>
/// Окно и трей на драфте — по фазе таймера в событиях драфта.
///
/// Идёт драфт — окну можно на экран, а ручное скрытие крестиком снимается
/// один раз за драфт: закрыл окно раньше — на новом драфте оно всё равно
/// вернётся (закрыть его в течение драфта по-прежнему можно). За 5 секунд до
/// конца финализации окно уходит в трей: прозрачное окно поверх всех мешает
/// входу в игру.
///
/// Зовёт цикл событий клиента (<c>Program.RunSessionAsync</c>); отдельно от
/// него — чтобы проверка могла провести через это окно настоящую
/// последовательность фаз без подставного сокета.
/// </summary>
public sealed class DraftTray(OverlayWindow overlay, CancellationToken ct)
{
    private CancellationTokenSource? _hide;   // запланированное скрытие в трей
    private bool _unhidden;                   // ручное скрытие на этом драфте уже снято

    /// Событие драфта: фаза таймера (PLANNING, BAN_PICK, FINALIZATION…) и
    /// сколько в ней осталось.
    public void OnTimer(string phase, double leftMs)
    {
        // Вход в игру уже начат. Финализация кончилась, и клиент шлёт события
        // драфта с другой фазой (GAME_STARTING), а окна игры ещё нет. До
        // 1 ноября «не финализация» значило «драфт снова идёт»: снималось
        // «идёт игра», и слежение за клиентом на долю секунды показывало окно
        // на каждом входе в игру. Имя фазы не важно: отменяет вход только
        // конец драфта (Ended) — додж.
        if (_hide is not null) return;

        if (phase == "FINALIZATION")
        {
            _hide = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Math.Max(0, leftMs - 5000);
            var token = _hide.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                    overlay.SetGameActive(true); // вход в игру — подавляем авто-показ
                    if (!AppSettings.Current.KeepDuringGame) overlay.HideToTray();
                }
                catch (OperationCanceledException) { }
            }, token);
            return;
        }

        // Активный драфт — авто-показ оверлея разрешён, чтобы окно надёжно
        // всплыло на новом чемп-селекте после игры.
        overlay.SetGameActive(false);
        if (!_unhidden) { overlay.AllowAutoShow(); _unhidden = true; }
    }

    /// Драфт кончился — в игру, доджем или выходом в меню: запланированное
    /// скрытие больше не нужно, следующий драфт снова снимет ручное скрытие.
    public void Ended()
    {
        _hide?.Cancel();
        _hide = null;
        _unhidden = false;
    }
}
