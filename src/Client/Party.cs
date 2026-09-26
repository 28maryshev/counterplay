using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Кто со мной в пати — по ЧЕЛОВЕКУ, а не по чемпиону.
///
/// Дуо-пул раньше опознавал напарника так: союзник взял чемпиона из половины
/// друга — значит, это друг. У друга с широким пулом это ломалось: случайный
/// союзник брал оттуда чемпиона, и программа строила пару вокруг чужого
/// человека.
///
/// Состав пати клиент показывает в лобби. Запоминаем его и в драфте сверяем
/// союзников по summonerId (а если его нет — по puuid).
///
/// Сознательно НЕ забываем состав, когда лобби закрывается: оно исчезает как раз
/// при переходе в чемп-селект, то есть ровно тогда, когда напарник и нужен.
/// Состав заменяется следующим увиденным лобби.
/// </summary>
public static class Party
{
    private static readonly HashSet<long>   Ids    = [];
    private static readonly HashSet<string> Puuids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Лобби мы хотя бы раз видели. Отличает «в пати никого» (играю один — и
    /// напарника быть не должно) от «мы ничего не знаем» (программу запустили
    /// посреди драфта — тогда работает старое правило по чемпиону).
    /// </summary>
    public static bool Known { get; private set; }

    /// Ники сопартийцев — для журнала и показа в настройках.
    public static IReadOnlyList<string> Names { get; private set; } = [];

    /// <summary>Разобрать ответ <c>/lol-lobby/v2/lobby</c>. Себя исключаем.</summary>
    public static void Update(JsonElement lobby)
    {
        if (lobby.ValueKind != JsonValueKind.Object) return;
        if (!lobby.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array) return;

        // Себя узнаём по localMember: сравнивать с текущим призывателем не нужно,
        // клиент уже сказал, кто здесь мы.
        long   myId    = 0;
        string myPuuid = "";
        if (lobby.TryGetProperty("localMember", out var me) && me.ValueKind == JsonValueKind.Object)
        {
            myId    = Long(me, "summonerId");
            myPuuid = Str(me, "puuid");
        }

        var ids = new HashSet<long>();
        var puuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();
        foreach (var m in members.EnumerateArray())
        {
            var id    = Long(m, "summonerId");
            var puuid = Str(m, "puuid");
            if ((myId != 0 && id == myId) || (myPuuid.Length > 0 && puuid == myPuuid)) continue;
            if (id != 0) ids.Add(id);
            if (puuid.Length > 0) puuids.Add(puuid);
            var name = Str(m, "gameName");
            if (name.Length == 0) name = Str(m, "summonerName");
            var tag = Str(m, "tagLine");
            if (name.Length > 0) names.Add(tag.Length > 0 ? $"{name}#{tag}" : name);
        }

        var changed = !ids.SetEquals(Ids) || !puuids.SetEquals(Puuids) || !Known;
        Ids.Clear();    foreach (var x in ids) Ids.Add(x);
        Puuids.Clear(); foreach (var x in puuids) Puuids.Add(x);
        Names = names;
        Known = true;
        // Считаем по ИДЕНТИФИКАТОРАМ, а не по никам: клиент отдаёт ник не всегда,
        // и запись «играю один» появлялась при непустой пати — в разборе жалобы
        // это уводило в сторону.
        if (changed)
            Log.Write(Ids.Count == 0 && Puuids.Count == 0
                ? "пати: играю один"
                : $"пати: {(names.Count > 0 ? string.Join(", ", names) : "без имён")} "
                  + $"({Ids.Count} id, {Puuids.Count} puuid)");
    }

    /// Этот союзник — мой сопартиец?
    public static bool IsMate(DraftPlayer p) =>
        (p.SummonerId != 0 && Ids.Contains(p.SummonerId))
        || (p.Puuid.Length > 0 && Puuids.Contains(p.Puuid));

    /// <summary>
    /// Чемпион напарника по дуо-пулу — 0, если напарника в драфте нет.
    ///
    /// Ищем только ЧЕЛОВЕКА из пати: неважно, из пула он взял чемпиона или нет —
    /// играем-то мы всё равно с ним.
    ///
    /// Отката «а вдруг вон тот союзник с чемпионом из половины друга» больше нет.
    /// Он существовал на случай «программу запустили посреди драфта», но именно
    /// он и выдавал чужого человека за напарника: у друга широкий пул, кто-то
    /// взял оттуда чемпиона — и пара строилась вокруг постороннего. Промолчать
    /// честнее, чем угадать неверно; в драфт без сведений о пати мы теперь просто
    /// не предлагаем пару.
    /// </summary>
    public static int MateChampion(DraftState state, DuoPool? duo)
    {
        if (duo is null) return Logged(0, "");

        var allies = state.MyTeam.Where(p => !p.IsLocalPlayer && p.EffectiveChampionId != 0).ToList();

        // 1) У пула есть ХОЗЯИН второй половины — ищем в команде именно его.
        //
        // Это главный путь, и он сильнее пати. Играем впятером в нормале или
        // флексе: сопартийцев четверо, и по одной лишь принадлежности к пати
        // напарником стал бы первый попавшийся. По puuid же находится тот
        // самый человек, под которого пул и настроен, — и вся статистика
        // связки пишется на него.
        if (duo.FriendPuuid.Length > 0)
        {
            var owner = allies.FirstOrDefault(p =>
                p.Puuid.Length > 0 &&
                p.Puuid.Equals(duo.FriendPuuid, StringComparison.OrdinalIgnoreCase));
            if (owner is not null)
                return Logged(owner.EffectiveChampionId, $"хозяин половины пула, роль {Role(owner)}");

            // Хозяин известен, но его в команде нет — значит это просто другая
            // игра. Молчим: подставлять вместо него случайного союзника хуже,
            // чем не предлагать пару вовсе.
            if (Known) return Logged(0, "хозяина половины пула в команде нет");
        }

        // 2) Хозяин ещё не известен (пул собран руками, без обмена файлом) —
        //    работает прежнее правило: единственный человек из пати.
        var mate = allies.FirstOrDefault(IsMate);
        if (mate is not null)
        {
            // Заодно запоминаем, КТО этот напарник, — дальше пойдём по пункту 1.
            if (mate.Puuid.Length > 0 && duo.FriendPuuid != mate.Puuid)
            {
                duo.FriendPuuid = mate.Puuid;
                PoolStore.Persist();
                Log.Write($"дуо «{duo.FriendName}»: напарник опознан, связки считаем по нему");
            }
            return Logged(mate.EffectiveChampionId, $"напарник по пати, роль {Role(mate)}");
        }

        if (allies.Count == 0) return Logged(0, "");
        return Logged(0, Known ? "в команде никого из пати" : "состав пати неизвестен — пару не предлагаю");
    }

    private static string Role(DraftPlayer p) => p.Position.Length > 0 ? p.Position : "не раскрыта";

    // Пишем в журнал только при СМЕНЕ напарника: метод зовётся на каждый пересчёт
    // рекомендаций, а это десятки раз за драфт.
    private static int _lastLogged = -1;

    private static int Logged(int champId, string why)
    {
        if (champId == _lastLogged) return champId;
        _lastLogged = champId;
        if (why.Length > 0)
            Log.Write($"дуо: {(champId != 0 ? $"чемпион напарника {champId} — {why}" : why)}");
        return champId;
    }

    private static long Long(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
