namespace Counterplay;

/// <summary>
/// Как назвать напарника там, где речь о ЧЕЛОВЕКЕ, а не о наборе чемпионов.
///
/// <see cref="DuoPool.FriendName"/> для этого не годится: это подпись плитки,
/// и она нарочно склеена из названий обеих половин («supports + top»). На месте
/// имени такая склейка читается бессмыслицей — «связки с supports + top».
/// </summary>
public static class DuoNaming
{
    /// <summary>
    /// С кем игрок правда играл вдвоём: puuid → ник и число совместных игр,
    /// от частых к редким. Один источник на плитку и на редактор — иначе списки
    /// разъедутся, а человек там один и тот же.
    /// </summary>
    public static List<(string Puuid, string Name, int Games)> Mates()
    {
        // Игры вместе считаем ТОЛЬКО по соло/дуо. Связки копятся на всех
        // союзников подряд, и по всем очередям в списке оказывались все, с кем
        // вообще доводилось играть, — у владельца это 182 человека.
        var games = new Dictionary<string, (string Name, int Games)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in SessionTracker.TopPairs(take: 10_000, queues: new[] { "solo" }))
        {
            if (p.AllyPuuid.Length == 0) continue;
            var had = games.GetValueOrDefault(p.AllyPuuid);
            games[p.AllyPuuid] = (p.AllyName.Length > 0 ? p.AllyName : had.Name ?? "",
                                  had.Games + p.Games);
        }

        // Ник, под которым игрок сам держит пул этого человека. Лобби ников не
        // отдаёт, и до первой общей игры в соло/дуо назвать его больше нечем.
        var poolNick = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in PoolStore.Current().DuoPools)
            if (d.FriendPuuid.Length > 0 && d.FriendNick.Length > 0)
                poolNick.TryAdd(d.FriendPuuid, d.FriendNick);

        // Ник по порядку: лобби, связки соло/дуо, пул. Нет нигде — пустая
        // строка, а не null: меню и плашка мерят её длину. GetValueOrDefault
        // для незнакомого puuid отдаёт кортеж с null внутри — так меню
        // «сменить напарника» и роняло программу.
        string NameOf(string puuid, string lobby) =>
            lobby.Length > 0 ? lobby
            : games.GetValueOrDefault(puuid).Name is { Length: > 0 } fromGames ? fromGames
            : poolNick.GetValueOrDefault(puuid, "");

        var res  = new List<(string Puuid, string Name, int Games)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Главный источник — лобби соло/дуо: это ровно те, с кем ВСТАВАЛИ В
        // ОЧЕРЕДЬ. Человек попадает сюда, даже если совместных игр ещё нет.
        foreach (var (puuid, name, _) in SessionTracker.QueuedMates())
        {
            res.Add((puuid, NameOf(puuid, name), games.GetValueOrDefault(puuid).Games));
            seen.Add(puuid);
        }

        // Запас для тех, у кого лобби ещё не записалось: список лобби копится
        // только вперёд, а пул с другом у многих настроен давно. В соло/дуо
        // случайный союзник попадается один раз, напарник — помногу, поэтому
        // двух совместных игр уже достаточно, чтобы это была не случайность.
        foreach (var (puuid, v) in games)
            if (!seen.Contains(puuid) && v.Games >= 2)
                res.Add((puuid, NameOf(puuid, ""), v.Games));

        return res.OrderByDescending(x => x.Games).ToList();
    }

    /// <summary>
    /// Ник напарника или пусто, если назвать некого.
    ///
    /// По порядку: ник из файла, которым обменялись (приезжает рядом с puuid);
    /// ник из самих связок, где он лежит рядом с играми; иначе — пусто.
    ///
    /// Пусто возвращается и тогда, когда напарник вообще не опознан: в этом
    /// случае показаны связки СО ВСЕМИ, и называть кого-то одного нельзя.
    /// </summary>
    public static string PartnerNick(DuoPool? duo, IEnumerable<SessionTracker.PairStat> pairs)
    {
        if (duo is null || duo.FriendPuuid.Length == 0) return "";
        if (duo.FriendNick.Length > 0) return duo.FriendNick;
        return pairs.FirstOrDefault(p => p.AllyName.Length > 0)?.AllyName ?? "";
    }

    /// <summary>
    /// Имя для ПЛИТКИ пула. Правило мягче, чем у заголовка.
    ///
    /// Заголовок подписывает список связок, и назвать там человека можно только
    /// когда список по нему и отфильтрован, — иначе подпись обещает одного, а
    /// показаны связки со всеми. На плитке вопрос другой: «с кем этот пул». Ник
    /// из файла отвечает на него сам по себе, даже если вместе ещё не играли и
    /// puuid не подтверждён.
    /// </summary>
    public static string TileNick(DuoPool? duo, IEnumerable<SessionTracker.PairStat> pairs)
    {
        if (duo is null) return "";
        if (duo.FriendNick.Length > 0) return duo.FriendNick;
        return duo.FriendPuuid.Length > 0
            ? pairs.FirstOrDefault(p => p.AllyName.Length > 0)?.AllyName ?? ""
            : "";
    }

    /// <summary>
    /// Этот ник из лобби — тот, под кого собран пул?
    ///
    /// Пул без закреплённого напарника часто подписан его ником: в поле ника
    /// или прямо названием плитки. Во флексе сопартийцев до четырёх, и ник —
    /// единственное, по чему видно, кто из них «тот самый». Сверяем без тега,
    /// регистра и пробелов: в пуле их пишут как придётся. Совпадение только
    /// целиком — по куску короткий ник нашёлся бы в чём угодно.
    /// </summary>
    public static bool Matches(DuoPool d, string nick)
    {
        var k = NickKey(nick);
        return k.Length > 0 && (NickKey(d.FriendNick) == k || NickKey(d.FriendName) == k);
    }

    private static string NickKey(string s)
    {
        var tag = s.IndexOf('#');
        if (tag >= 0) s = s[..tag];
        return string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    }
}
