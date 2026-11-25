using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Сообщение в чат драфта — по кнопке фразы в окне драфта (OverlayWindow.Phrases).
///
/// Отправляет только то, что человек нажал сам: ни одного сообщения по таймеру
/// или событию. Чат драфта клиент держит отдельной беседой с типом
/// <c>championSelect</c>; её адрес меняется каждый драфт, поэтому ищем его при
/// каждой отправке, а не запоминаем — запрос лёгкий, а нажимают кнопку редко.
/// </summary>
public static class DraftChat
{
    /// Длина фразы, дальше клиент режет сам; больше и не нужно для чата драфта.
    public const int MaxLength = 150;

    /// <summary>
    /// Отправить текст в чат текущего драфта. Ответ — HTTP-код отправки:
    /// 404 — беседы драфта нет (драфт кончился или ещё не открыт), 0 — сорвалось.
    /// </summary>
    public static async Task<int> SendAsync(LcuHttpClient http, string text, CancellationToken ct)
    {
        var body = Clean(text);
        if (body.Length == 0) return 0;
        try
        {
            var (s, list) = await http.GetAsync("/lol-chat/v1/conversations", ct);
            var id = s is >= 200 and < 300 ? FindConversation(list) : null;
            if (id is null)
            {
                Log.Write($"чат драфта: беседы нет (беседы → {s}: {Kinds(list)})");
                return 404;
            }
            var (code, resp) = await http.PostAsync(
                $"/lol-chat/v1/conversations/{Uri.EscapeDataString(id)}/messages", Message(body), ct);
            Log.Write(code is >= 200 and < 300
                ? $"чат драфта: отправлено {body.Length} знаков"
                : $"чат драфта: {code}: {Trim(resp)}");
            return code;
        }
        catch (Exception ex) { Log.Write($"чат драфта: сорвалось — {ex.Message}"); return 0; }
    }

    /// Адрес беседы драфта из списка бесед клиента; нет такой — null.
    public static string? FindConversation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var c in doc.RootElement.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object
                    && c.TryGetProperty("type", out var t) && t.GetString() == "championSelect"
                    && c.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } s)
                    return s;
        }
        catch (JsonException) { }
        return null;
    }

    /// Тело запроса отправки — так же, как его шлёт сам клиент из поля ввода.
    public static string Message(string text) =>
        JsonSerializer.Serialize(new { body = text, type = "chat" });

    /// Одной строкой и не длиннее предела: перевод строки в чате драфта — это
    /// отправка, и фраза из поля в две строки ушла бы двумя сообщениями.
    public static string Clean(string? text)
    {
        var s = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > MaxLength ? s[..MaxLength] : s;
    }

    // Какие беседы были — для журнала, когда драфтовой среди них не нашлось.
    private static string Kinds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "не список";
            var kinds = doc.RootElement.EnumerateArray()
                .Select(c => c.TryGetProperty("type", out var t) ? t.GetString() : "?")
                .GroupBy(k => k).Select(g => $"{g.Key}×{g.Count()}");
            return string.Join(", ", kinds) is { Length: > 0 } k ? k : "пусто";
        }
        catch (JsonException) { return "не JSON"; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
