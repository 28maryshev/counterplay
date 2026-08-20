using System.Text.Json;

namespace Counterplay;

/// <summary>Игрок в драфте.</summary>
public sealed record DraftPlayer(
    int CellId,
    int ChampionId,    // 0 = ещё не залочен
    int PickIntentId,  // наведённый (ховер) чемпион; 0 = нет
    string Position,   // top/jungle/middle/bottom/utility, "" = роль не раскрыта
    bool IsLocalPlayer)
{
    // Чемпион для рекомендаций: залоченный, иначе наведённый (ховер).
    // Позволяет пересчитывать пик ещё на этапе наведения, до лока.
    public int EffectiveChampionId => ChampionId > 0 ? ChampionId : PickIntentId;
}

/// <summary>Снимок состояния champ select.</summary>
public sealed record DraftState(
    IReadOnlyList<DraftPlayer> MyTeam,
    IReadOnlyList<DraftPlayer> TheirTeam,
    IReadOnlyList<int> MyTeamBans,
    IReadOnlyList<int> TheirTeamBans,
    DraftPlayer? Me,
    string MyPosition,
    DraftPlayer? DirectOpponent,   // null, если роли врага скрыты (соло/дуо)
    bool ExposedToCounter,         // я пикаю раньше кого-то из врагов (риск контрпика)
    bool InBanPhase,               // сейчас идёт фаза банов
    IReadOnlyList<int> Bench,      // ARAM: чемпионы на скамейке (доступны для обмена)
    bool IsAram,                   // ARAM-режим (есть скамейка/реролл)
    int MyPickActionId,            // id действия моего пика (-1 = нет; id 0 валиден!)
    bool MyPickInProgress,         // мой ход пикать — можно лочить (иначе только hover)
    IReadOnlyList<int> ActiveCells,// cellId'ы, чей ход пикать прямо сейчас (мигание)
    int FirstPickCell,             // cellId первого пика в порядке драфта (-1 = неизвестно)
    int MyBanActionId,             // id действия моего бана (-1 = нет; для бана из оверлея)
    bool MyBanInProgress);         // мой ход банить прямо сейчас

public static class ChampSelectParser
{
    public static DraftState Parse(JsonElement session)
    {
        int localCell = GetInt(session, "localPlayerCellId", -1);

        var myTeam    = ParseTeam(session, "myTeam", localCell);
        var theirTeam = ParseTeam(session, "theirTeam", localCell);
        var (myBans, theirBans) = ParseBans(session);

        var me  = myTeam.FirstOrDefault(p => p.IsLocalPlayer);
        var pos = me?.Position ?? "";

        // Прямой оппонент — враг той же роли. В соло/дуо роли врага скрыты → null.
        DraftPlayer? opp = string.IsNullOrEmpty(pos)
            ? null
            : theirTeam.FirstOrDefault(p => p.Position == pos);

        var exposed  = ComputeExposed(session, localCell);
        var inBan    = ComputeInBanPhase(session);
        var (bench, isAram) = ParseBench(session);
        var (pickId, pickNow) = FindMyAction(session, localCell, "pick");
        var (banId,  banNow)  = FindMyAction(session, localCell, "ban");
        var (active, firstCell) = ParsePickTurns(session);

        return new DraftState(myTeam, theirTeam, myBans, theirBans, me, pos, opp, exposed, inBan,
                              bench, isAram, pickId, pickNow, active, firstCell, banId, banNow);
    }

    // Чей ход пикать прямо сейчас (in-progress pick) + кто пикает первым по
    // порядку очереди — для подсветки слота и бейджа «1st pick».
    private static (List<int> Active, int FirstCell) ParsePickTurns(JsonElement session)
    {
        var active = new List<int>();
        int first = -1;
        bool firstSeen = false;
        if (!session.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return (active, first);

        foreach (var group in actions.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in group.EnumerateArray())
            {
                if (GetStr(a, "type") != "pick") continue;
                int cell = GetInt(a, "actorCellId", -1);
                // Первый пик — строго ПЕРВОЕ действие пика в очереди. Если его
                // актор скрыт (у врагов в рейтинге cellId бывает -1) — бейдж не
                // показываем вовсе: раньше «первым» ошибочно становился первый
                // СОЮЗНИК, чей cellId был виден.
                if (!firstSeen) { firstSeen = true; first = cell; }
                if (cell >= 0 && IsTrue(a, "isInProgress") && !IsTrue(a, "completed"))
                    active.Add(cell);
            }
        }
        return (active, first < 0 ? -1 : first);
    }

    // Моё незавершённое действие типа type ("pick"/"ban"): id (для PATCH
    // hover/lock) и можно ли завершить его прямо сейчас (isInProgress == мой ход).
    // ВАЖНО: id действия в LCU начинается с 0 (в кастомках первый пик = 0),
    // поэтому «нет действия» — это -1, а не 0.
    private static (int Id, bool InProgress) FindMyAction(JsonElement session, int localCell, string type)
    {
        if (localCell < 0 || !session.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
            return (-1, false);

        foreach (var group in actions.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in group.EnumerateArray())
            {
                if (GetStr(a, "type") != type) continue;
                if (GetInt(a, "actorCellId", -1) != localCell) continue;
                if (IsTrue(a, "completed")) continue;         // уже завершён
                return (GetInt(a, "id", -1), IsTrue(a, "isInProgress"));
            }
        }
        return (-1, false);
    }

    // Скамейка ARAM: доступные для обмена чемпионы + флаг режима (benchEnabled).
    private static (List<int> Bench, bool IsAram) ParseBench(JsonElement s)
    {
        var bench = new List<int>();
        bool enabled = s.TryGetProperty("benchEnabled", out var be) && be.ValueKind == JsonValueKind.True;

        if (s.TryGetProperty("benchChampions", out var bc) && bc.ValueKind == JsonValueKind.Array)
            foreach (var e in bc.EnumerateArray())
            {
                int id = GetInt(e, "championId");
                if (id > 0) bench.Add(id);
            }
        if (bench.Count == 0 && s.TryGetProperty("benchChampionIds", out var bi) && bi.ValueKind == JsonValueKind.Array)
            foreach (var e in bi.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Number) bench.Add(e.GetInt32());

        return (bench, enabled);
    }

    // Идёт ли сейчас фаза банов: есть активное (in-progress) действие типа "ban".
    private static bool ComputeInBanPhase(JsonElement session)
    {
        // Фаза планирования (все объявляют намерения ДО таймера банов): пикнуть
        // ещё нельзя, а баны — следующий шаг. Советы по банам актуальны уже здесь,
        // по показанным чемпионам команды.
        if (session.TryGetProperty("timer", out var timer) &&
            GetStr(timer, "phase") == "PLANNING")
            return true;

        if (!session.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var group in actions.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Array) continue;
            foreach (var a in group.EnumerateArray())
                if (GetStr(a, "type") == "ban" && IsTrue(a, "isInProgress"))
                    return true;
        }
        return false;
    }

    // true, если мой пик ещё не залочен и после меня по порядку есть незавершённый
    // пик врага → меня могут контрпикнуть (актуально для драфт-режимов).
    private static bool ComputeExposed(JsonElement session, int localCell)
    {
        if (localCell < 0) return false;
        if (!session.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
            return false;

        // Плоский список действий в порядке очереди.
        var flat = new List<JsonElement>();
        foreach (var group in actions.EnumerateArray())
            if (group.ValueKind == JsonValueKind.Array)
                foreach (var a in group.EnumerateArray())
                    flat.Add(a);

        int myIdx = -1;
        for (int i = 0; i < flat.Count; i++)
        {
            if (GetStr(flat[i], "type") != "pick") continue;
            if (GetInt(flat[i], "actorCellId", -1) != localCell) continue;
            if (IsTrue(flat[i], "completed")) return false; // я уже залочился
            myIdx = i; break;
        }
        if (myIdx < 0) return false;

        for (int i = myIdx + 1; i < flat.Count; i++)
        {
            if (GetStr(flat[i], "type") != "pick") continue;
            if (!IsTrue(flat[i], "isAllyAction") && !IsTrue(flat[i], "completed"))
                return true; // враг пикает после меня и ещё не залочился
        }
        return false;
    }

    private static List<DraftPlayer> ParseTeam(JsonElement session, string key, int localCell)
    {
        var list = new List<DraftPlayer>();
        if (!session.TryGetProperty(key, out var team) || team.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var p in team.EnumerateArray())
        {
            int cell = GetInt(p, "cellId");
            list.Add(new DraftPlayer(
                CellId: cell,
                ChampionId: GetInt(p, "championId"),
                PickIntentId: GetInt(p, "championPickIntent"),
                Position: GetStr(p, "assignedPosition"),
                IsLocalPlayer: localCell >= 0 && cell == localCell));
        }
        return list;
    }

    private static (List<int> Mine, List<int> Theirs) ParseBans(JsonElement session)
    {
        var mine = new List<int>();
        var theirs = new List<int>();

        // Баны берём из actions — там они появляются по ходу драфта.
        if (session.TryGetProperty("actions", out var actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in actions.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in group.EnumerateArray())
                {
                    if (GetStr(a, "type") != "ban") continue;
                    if (!IsTrue(a, "completed")) continue;
                    int champ = GetInt(a, "championId");
                    if (champ <= 0) continue;
                    (IsTrue(a, "isAllyAction") ? mine : theirs).Add(champ);
                }
            }
        }
        return (mine, theirs);
    }

    private static int GetInt(JsonElement e, string name, int fallback = 0) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static string GetStr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool IsTrue(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
