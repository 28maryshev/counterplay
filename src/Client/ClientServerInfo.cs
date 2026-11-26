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

    /// Запросы к клиенту. Не ответил какой-то — это поле пустое, остальные
    /// отдаём: старый клиент или клиент Tencent может не знать одного из адресов.
    ///
    /// Платформа: `LoginDataPacket/platformId` нынешний клиент не знает (404 —
    /// так 1.4.1 и слал её пустой), поэтому берём её из `/lol-chat/v1/me`, а не
    /// ответил чат — из окружения клиента Riot (`euw1` → `EUW1`). Из обоих
    /// ответов читаем одно это поле: остальное там — ник и номера игрока.
    public static async Task<ClientServerInfo> ReadAsync(LcuHttpClient http, CancellationToken ct)
    {
        var region = Code(ParseField(await TryGetAsync(http, "/riotclient/region-locale", ct), "region"));
        var platform = Code(ParseField(await TryGetAsync(http, "/lol-chat/v1/me", ct), "platformId"))
            ?? Code(ParseField(await TryGetAsync(http, "/riotclient/v1/crash-reporting/environment", ct),
                               "environment")?.ToUpperInvariant());
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

    /// Одно строковое поле объекта: `{"locale":"zh_CN","region":"TENCENT",…}`,
    /// `region` → `TENCENT`. Нет поля, не объект, не JSON — пусто.
    public static string? ParseField(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var r)
                   && r.ValueKind == JsonValueKind.String
                ? r.GetString()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// Версию клиент отдаёт строкой JSON. Ошибка клиента приходит объектом
    /// (`{"errorCode":…}`) — это не значение.
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

    /// Версия игры: цифры и точки, с цифры, до 32 знаков. Клиент дописывает к
    /// номеру ветку сборки — `16.23.8418428+branch.releases-16-23…vanguard`;
    /// хвост после `+` отрезаем (с ним 1.4.1 слала версию пустой), номер
    /// остаётся: `16.23.8418428`.
    public static string? Version(string? s)
    {
        if (s is null) return null;
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s is { Length: > 0 and <= 32 } && char.IsAsciiDigit(s[0])
               && s.All(c => char.IsAsciiDigit(c) || c == '.')
            ? s
            : null;
    }

    public override string ToString() =>
        $"сервер {Region ?? "?"}/{Platform ?? "?"}, игра {Game ?? "?"}";
}
