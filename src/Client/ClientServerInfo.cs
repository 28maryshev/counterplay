using System.Text.Json;

namespace Counterplay;

/// <summary>
/// На каком сервере клиент League и какая у него версия игры — для отчёта о
/// подключении (`Program.ClientReport`).
///
/// Зачем: по способу подключения (lockfile или командная строка) сервер
/// только угадывался. Игроки из Гонконга подключались так же, как китайские,
/// а значит, сидели на сервере Tencent, а не Riot. Но доказать это было нечем,
/// и насколько патч у Tencent отстаёт от нашей базы, было не узнать
/// (журнал 2026-11-25).
///
/// Всё это строки самого клиента, не игрока: регион (`EUW`, `TENCENT`…),
/// платформа (`EUW1`, `HN1`…) и номер версии игры.
/// </summary>
public sealed record ClientServerInfo(string? Region, string? Platform, string? Game)
{
    public static readonly ClientServerInfo None = new(null, null, null);

    /// Три запроса к клиенту. Не ответил какой-то — это поле пустое, остальные
    /// отдаём: старый клиент или клиент Tencent может не знать одного из адресов.
    public static async Task<ClientServerInfo> ReadAsync(LcuHttpClient http, CancellationToken ct)
    {
        var region = Code(ParseRegion(await TryGetAsync(http, "/riotclient/region-locale", ct)));
        var platform = Code(ParseString(await TryGetAsync(
            http, "/lol-platform-config/v1/namespaces/LoginDataPacket/platformId", ct)));
        var game = Version(ParseString(await TryGetAsync(http, "/lol-patch/v1/game-version", ct)));
        return new ClientServerInfo(region, platform, game);
    }

    private static async Task<string?> TryGetAsync(LcuHttpClient http, string path, CancellationToken ct)
    {
        try
        {
            var (s, body) = await http.GetAsync(path, ct);
            return s == 200 ? body : null;
        }
        catch { return null; }
    }

    /// `{"locale":"zh_CN","region":"TENCENT",…}` → `TENCENT`.
    public static string? ParseRegion(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("region", out var r)
                   && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// Клиент отдаёт платформу и версию строкой JSON: `"EUW1"` → `EUW1`.
    /// Ошибка клиента приходит объектом (`{"errorCode":…}`) — это не значение.
    public static string? ParseString(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// Регион и платформа: только латиница, цифры и `_`, до 16 знаков. Что-то
    /// другое — не шлём вовсе: в отчёт не должно уйти что попало.
    public static string? Code(string? s) =>
        s is { Length: > 0 and <= 16 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? s : null;

    /// Версия игры: цифры и точки, до 32 знаков (`16.23.725.1234`).
    public static string? Version(string? s) =>
        s is { Length: > 0 and <= 32 } && s.All(c => char.IsAsciiDigit(c) || c == '.') && char.IsAsciiDigit(s[0])
            ? s
            : null;

    public override string ToString() =>
        $"сервер {Region ?? "?"}/{Platform ?? "?"}, игра {Game ?? "?"}";
}
