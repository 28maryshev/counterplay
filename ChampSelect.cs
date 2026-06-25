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
    bool ExposedToCounter);        // я пикаю раньше кого-то из врагов (риск контрпика)

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

        var exposed = ComputeExposed(session, localCell);

        return new DraftState(myTeam, theirTeam, myBans, theirBans, me, pos, opp, exposed);
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
