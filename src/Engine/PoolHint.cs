namespace Counterplay;

/// Почему подсказка предлагает сменить режим пула.
public enum PoolHintCase
{
    /// В лобби напарник — включить дуо-пул.
    WithMate,
    /// Играю один, а включён дуо — выключить его.
    Alone,
    /// В пати кто-то есть, но не тот, под кого собран включённый дуо-пул.
    MateAway,
}

/// <summary>
/// Что предложить: режим и пул (<c>Id</c> null — выбрать из меню, пулов
/// несколько и звезды нет), о ком речь (<c>Who</c>, может быть пустым) и как
/// называется предлагаемый пул (<c>Target</c>, пусто — у обычного режима).
/// <c>Key</c> — для «Не сейчас»: то же предложение при том же составе и
/// очереди больше не всплывает.
/// </summary>
public sealed record PoolSuggestion(PoolHintCase Case, PoolKind Kind, string? Id,
                                    string Who, string Target, string Key);

/// <summary>
/// Подсказка режима пула в лобби: с напарником — дуо-пул, одному — без него.
///
/// Режим запоминается по очереди, а не по составу: в соло/дуо один и тот же
/// человек играет то вдвоём, то один. Включённый дуо-пул без напарника молчит
/// (пару не с кем строить), а забытый личный пул при напарнике в лобби лишает
/// подсказок для пары. Переключать молча не стали: режим — выбор игрока, и
/// играть с другом без дуо-пула тоже законно. Поэтому — подсказка, а решает он.
/// </summary>
public static class PoolHint
{
    /// <summary>
    /// Что предложить при таком составе пати, или null — всё и так как надо.
    /// <paramref name="mates"/> — сопартийцы puuid → ник, <paramref name="size"/>
    /// — сколько их (у кого-то puuid может не прийти).
    /// </summary>
    public static PoolSuggestion? Decide(AccountPools a, string queue, bool known, int size,
                                         IReadOnlyDictionary<string, string> mates)
    {
        // Состав неизвестен — советовать не по чему. В ARAM пулы не работают:
        // чемпиона там не выбирают.
        if (!known || queue == "aram") return null;

        var active = a.ActiveKind == PoolKind.Duo
            ? a.DuoPools.FirstOrDefault(d => d.Id == a.ActiveId) : null;
        bool InParty(DuoPool d) => d.FriendPuuid.Length > 0 && mates.ContainsKey(d.FriendPuuid);
        string Key(PoolKind kind, string? id) =>
            $"{queue}|{size}|{string.Join(",", mates.Keys.Order(StringComparer.OrdinalIgnoreCase))}|{kind}|{id}";

        // 1. В лобби человек, под которого собран дуо-пул.
        var owned = a.DuoPools.Where(InParty).ToList();
        if (active is not null && owned.Contains(active)) return null;
        if (owned.Count > 0)
        {
            var d = Pick(owned, a.FavDuoId);
            var mate = d ?? owned[0];
            var who = mates.GetValueOrDefault(mate.FriendPuuid, "");
            if (who.Length == 0) who = mate.FriendNick;
            return new(PoolHintCase.WithMate, PoolKind.Duo, d?.Id, who, d?.FriendName ?? "",
                       Key(PoolKind.Duo, d?.Id));
        }

        // 2. Вдвоём, а дуо-пул ещё ни за кем не закреплён: напарником станет
        //    единственный человек из пати (Party.MateChampion, шаг 2).
        if (size == 1)
        {
            if (active is not null && active.FriendPuuid.Length == 0) return null;
            var free = a.DuoPools.Where(d => d.FriendPuuid.Length == 0).ToList();
            if (active is null && free.Count > 0)
            {
                var d = Pick(free, a.FavDuoId);
                var who = mates.Values.FirstOrDefault(n => n.Length > 0) ?? "";
                return new(PoolHintCase.WithMate, PoolKind.Duo, d?.Id, who, d?.FriendName ?? "",
                           Key(PoolKind.Duo, d?.Id));
            }
        }

        // 3. Дуо включён, а напарника по нему в лобби нет — пару строить не с
        //    кем. Пул без хозяина в большой пати тоже работает (берётся
        //    человек из пати), поэтому его не трогаем.
        if (active is null) return null;
        if (active.FriendPuuid.Length == 0 && size > 0) return null;

        var why = size == 0 ? PoolHintCase.Alone : PoolHintCase.MateAway;
        var away = why == PoolHintCase.MateAway ? active.FriendNick : "";
        if (a.Pools.Count == 0)
            return new(why, PoolKind.Normal, null, away, "", Key(PoolKind.Normal, null));
        var p = a.Pools.FirstOrDefault(x => x.Id == a.FavPoolId) ?? (a.Pools.Count == 1 ? a.Pools[0] : null);
        return new(why, PoolKind.Pool, p?.Id, away, p?.Name ?? "", Key(PoolKind.Pool, p?.Id));
    }

    /// Какой из подходящих дуо-пулов: со звездой, единственный — или никакой
    /// (тогда кнопка откроет меню, как кнопка «Дуо»).
    private static DuoPool? Pick(List<DuoPool> pools, string? fav) =>
        pools.FirstOrDefault(d => d.Id == fav) ?? (pools.Count == 1 ? pools[0] : null);
}
