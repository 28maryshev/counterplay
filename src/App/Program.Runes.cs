using Counterplay;

// Program — панель рун и сборки для залоченного чемпиона: показ, повтор, когда подвела сеть.
partial class Program
{
    // ── Руны и билд ────────────────────────────────────────────────────────
    // Панель показываем, когда мой чемпион залочен и по связке чемпион+роль
    // есть данные на сайте. Файл связки качается один раз за запуск, а не на
    // каждое событие драфта, поэтому сеть не мешает.
    private static string _runesShownFor = "";   // связка, для которой уже показали руны

    public static Task UpdateRunesAsync(OverlayWindow overlay, DraftState? draft, CancellationToken ct)
    {
        _runesDraft = draft;
        return UpdateRunesAsync(overlay, draft, ct, retry: false);
    }

    private static async Task UpdateRunesAsync(OverlayWindow overlay, DraftState? draft,
                                               CancellationToken ct, bool retry)
    {
        // Только залоченный чемпион, не наведённый. Раньше руны и сборка
        // появлялись уже по ховеру, и это сбивало: подсказки на экране, а пик
        // ещё не сделан — человек считал, что чемпион уже взят, и терял ход.
        // Кнопка подтверждения при этом работает по ховеру, как и раньше:
        // навёл — подтвердил — увидел сборку.
        var champ = draft?.Me?.ChampionId ?? 0;
        if (draft is null || champ == 0 || draft.IsAram)
        {
            _runesShownFor = "";
            overlay.HideRunes();
            return;
        }

        // Список связок не загрузился на запуске или ещё не успел — пик и есть
        // повод спросить его снова, не дожидаясь разогрева.
        await RunesClient.EnsureManifestAsync(ct);

        // Роль: из драфта, иначе основная роль чемпиона (custom games/блайнд не
        // раскрывают позицию — но руны показать всё равно нужно).
        var wanted = RecommendationEngine.LcuToDbRole(draft.MyPosition);
        var role = RunesClient.ResolveRole(champ, wanted);
        if (role is null || !RunesClient.Has(champ, role))
        {
            // «Не показывает руны» — жалоба, которую нельзя разобрать без этой
            // строки: причин три (не загрузился манифест, роль не раскрыта,
            // связки нет в данных), а внешне они выглядят одинаково — пустотой.
            if (_runesMissLogged != champ)
            {
                _runesMissLogged = champ;
                Log.Write($"руны не показаны: {DataDragon.Name(champ)}, роль «{wanted}» → " +
                          $"{(role is null ? "связка не найдена" : $"«{role}» нет в данных")}, " +
                          $"манифест {(RunesClient.ManifestLoaded ? "загружен" : "НЕ ЗАГРУЖЕН, пробую ещё")}");
            }
            if (!retry) overlay.HideRunes();
            // Список не загружен — это сеть, а не отсутствие данных: после пика
            // событий драфта может и не быть, поэтому повторяем сами.
            if (!RunesClient.ManifestLoaded) RetryRunesLater(overlay, draft, ct);
            return;
        }

        var opponent = draft.DirectOpponent?.EffectiveChampionId;
        var key = $"{champ}:{role}:{opponent ?? 0}";
        if (key == _runesShownFor) return;   // уже показано для этой связки
        _runesShownFor = key;

        var stats = await RunesClient.GetAsync(champ, role, ct);
        if (stats is null)
        {
            // Файл связки не пришёл. Связку не считаем показанной — иначе ни
            // повтор, ни следующее событие драфта её бы не спросили.
            _runesShownFor = "";
            if (_runesFailLogged != key)
            {
                _runesFailLogged = key;
                Log.Write($"руны: {DataDragon.Name(champ)} на роли «{role}» — данные не пришли, пробую ещё");
            }
            if (!retry) overlay.ShowRunes(null, champ, DataDragon.Name(champ), role, opponent);
            RetryRunesLater(overlay, draft, ct);
            return;
        }

        var late = _runesMissLogged == champ || _runesFailLogged == key;
        _runesMissLogged = 0;
        _runesFailLogged = "";
        Log.Write($"руны: {DataDragon.Name(champ)} на роли «{role}», игр {stats.Games:N0}" +
                  (late ? " — с повторной попытки" : ""));
        overlay.ShowRunes(stats, champ, DataDragon.Name(champ), role, opponent);
    }

    // Чемпион, по которому уже написали «рун нет», и связка, по которой
    // написали «не пришли», — чтобы не повторяться каждый цикл чемп-селекта.
    static int _runesMissLogged;
    static string _runesFailLogged = "";

    // ── Повтор, когда подвела сеть ──────────────────────────────────────────
    // Последний снимок драфта, отданный рунам; null — драфт кончился. Повтор
    // жив, пока это всё тот же снимок: пришло событие новее — повторяет уже
    // оно, драфт кончился — повторять нечего (иначе руны всплыли бы в лобби).
    private static DraftState? _runesDraft;
    private static readonly TimeSpan RunesRetryDelay = TimeSpan.FromSeconds(5);

    private static void RetryRunesLater(OverlayWindow overlay, DraftState draft, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(RunesRetryDelay, ct); }
            catch (OperationCanceledException) { return; }
            if (ReferenceEquals(_runesDraft, draft))
                await UpdateRunesAsync(overlay, draft, ct, retry: true);
        }, ct);

    /// Драфт кончился (или клиент отключился): повторы рун прекращаются.
    private static void RunesDraftEnded()
    {
        _runesDraft = null;
        _runesShownFor = "";
    }
}
