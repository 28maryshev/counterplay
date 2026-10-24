using System.Text.Json.Nodes;

namespace Counterplay;

// Синхронизация — чей пароль: под его хозяином едет всё, под другим аккаунтом
// этого компьютера — только журнал игр вошедшего.
public static partial class SyncClient
{
    /// <summary>
    /// Чей пароль на этом компьютере (<see cref="SyncPassword.Owner"/>).
    ///
    /// Строка на сервере — от пароля и вошедшего аккаунта, а везла она файлы
    /// целиком: пулы и настройки всех аккаунтов компьютера, «побеждает свежий».
    /// Владелец ввёл свой пароль на чужом компьютере, и строка аккаунта хозяйки
    /// стала общей для двух компьютеров разных людей: его файл пулов ждал её
    /// компьютер, чтобы заменить её пулы, а её правка пула заменила бы его
    /// раздел у него — и, переписанная «сейчас», уехала бы и в его строку (24.10).
    /// Поэтому пулы и настройки едут только под хозяином пароля, а под другими
    /// аккаунтами — журнал игр одного вошедшего.
    ///
    /// Не запомнено (пароль задан до входа в клиент или до 1.3.74) — решаем
    /// сейчас и запоминаем: аккаунт, у которого в журнале больше всего игр. У
    /// игрока со смурфом это основной; на чужом компьютере, где владелец однажды
    /// ввёл пароль, — тоже владелец: его журнал приехал туда синхронизацией
    /// целиком. Журнала нет — хозяин тот, кто вошёл. Решили не так — пароль
    /// вводится заново под нужным аккаунтом.
    /// </summary>
    private static string OwnerOf(string puuid)
    {
        if (SyncPassword.Owner is { } known) return known;
        var owner = SessionTracker.BusiestAccount() ?? puuid;
        SyncPassword.SetOwner(owner);
        Log.Write(owner == puuid
            ? "синхронизация: пароль — этого аккаунта"
            : "синхронизация: пароль — другого аккаунта, под этим едет только его журнал игр");
        return owner;
    }

    /// <summary>
    /// Снимок этого компьютера или копия с сервера — только журнал аккаунта
    /// <paramref name="puuid"/>: без пулов и настроек, в журнале одна его запись.
    /// Журнал не разобрался — не везём ничего.
    /// </summary>
    private static JsonObject JournalOnly(JsonObject data, string puuid)
    {
        var res = new JsonObject();
        if (data["session.json"] is not JsonObject f || (string?)f["body"] is not { } body) return res;
        try
        {
            var accounts = new JsonObject();
            if (JsonNode.Parse(body)?["Accounts"]?[puuid] is { } acc) accounts[puuid] = acc.DeepClone();
            res["session.json"] = new JsonObject
            {
                ["at"] = f["at"]?.DeepClone(),
                ["body"] = new JsonObject { ["Accounts"] = accounts }.ToJsonString()
            };
        }
        catch { /* журнал не разобрался */ }
        return res;
    }

    /// <summary>
    /// Свой журнал целиком, где запись аккаунта <paramref name="puuid"/> заменена
    /// пришедшей (<paramref name="body"/> — из <see cref="JournalOnly"/>). null —
    /// менять нечего; свой журнал не разобрался — тоже null: его не трогаем.
    /// </summary>
    private static string? WithJournal(string? local, string body, string puuid)
    {
        try
        {
            if (JsonNode.Parse(body)?["Accounts"]?[puuid] is not { } acc) return null;
            var root = string.IsNullOrEmpty(local) ? new JsonObject() : JsonNode.Parse(local)!.AsObject();
            if (root["Accounts"] is not JsonObject accounts) root["Accounts"] = accounts = new JsonObject();
            if (JsonNode.DeepEquals(accounts[puuid], acc)) return null;
            accounts[puuid] = acc.DeepClone();
            return root.ToJsonString();
        }
        catch { return null; }
    }
}
