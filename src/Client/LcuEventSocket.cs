using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// <summary>Разобранное событие OnJsonApiEvent.</summary>
public sealed record LcuEvent(string Uri, string EventType, JsonElement Data);

/// <summary>
/// WebSocket к LCU (протокол WAMP). Подписывается на все JSON-события
/// и отдаёт их потоком; фильтрация по uri — на стороне потребителя.
/// </summary>
public sealed class LcuEventSocket : IAsyncDisposable
{
    private readonly LcuCredentials _creds;
    private ClientWebSocket? _ws;

    public LcuEventSocket(LcuCredentials creds) => _creds = creds;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", _creds.AuthHeader);
        // Тот же самоподписанный серт LCU на localhost.
        _ws.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        // Keepalive-пинги: помогают ОС быстрее заметить мёртвый TCP. Основную
        // защиту от «полуоткрытого» сокета даёт таймаут приёма в ReadEventsAsync.
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        await _ws.ConnectAsync(_creds.WsUri, ct);

        // WAMP: [5, topic] = SUBSCRIBE. Подписываемся на все события, фильтруем по uri ниже.
        await SendAsync("[5,\"OnJsonApiEvent\"]", ct);
    }

    private Task SendAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return _ws!.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    // Тишина в сокете — ещё НЕ обрыв: сидя в лобби, клиент может не слать
    // событий сколько угодно (раньше это считалось смертью соединения, и
    // программа каждые полторы минуты переподключалась и лезла за базой).
    // По истечении этого срока пробуем ПИСАТЬ в сокет: живое соединение примет
    // отправку, мёртвое бросит исключение — вот это и есть обрыв.
    private const int RecvTimeoutSec = 60;

    public async IAsyncEnumerable<LcuEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (_ws!.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            sb.Clear();
            bool endOfMessage = false;
            bool silent = false;      // истёк таймаут приёма — сокет проверим отправкой
            while (!endOfMessage)
            {
                WebSocketReceiveResult result;
                try
                {
                    // Таймаут на каждый приём: linked-токен отменяется либо по ct
                    // (выход), либо по времени (мёртвый сокет). try без yield —
                    // корректно для итератора.
                    using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    recvCts.CancelAfter(TimeSpan.FromSeconds(RecvTimeoutSec));
                    result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), recvCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Не наша отмена, а таймаут приёма: событий давно не было.
                    // Само по себе это нормально (тихое лобби) — проверяем сокет
                    // отправкой ниже, а пока прерываем чтение этого сообщения.
                    silent = true;
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Close)
                    yield break;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                endOfMessage = result.EndOfMessage;
            }

            if (silent)
            {
                // Пере-подписка как пинг: для LCU повторный SUBSCRIBE безвреден,
                // а нам важен сам факт успешной записи в сокет.
                try { await SendAsync("[5,\"OnJsonApiEvent\"]", ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    throw new IOException("LCU socket is dead — reconnecting", e);
                }
                continue;
            }

            if (sb.Length == 0) continue;

            var text = sb.ToString();
            // Дешёвый префильтр: подписка идёт на ВСЕ события, но нам нужны
            // только две темы. При переходе в игру клиент шлёт лавину событий —
            // полный JSON-парсинг каждого зря грузит CPU и тормозит вычитку
            // сокета. Пропускаем нерелевантные ещё до парсинга по подстроке uri.
            if (!IsRelevant(text)) continue;

            if (TryParse(text) is { } ev) yield return ev;
        }
    }

    // URI, на которые реагирует потребитель (см. Program.RunSessionAsync).
    // current-summoner нужен, чтобы смену аккаунта в клиенте заметить сразу, а
    // не на следующем тике фонового обновления (до минуты ожидания).
    private static bool IsRelevant(string text) =>
        text.Contains("/lol-champ-select/v1/session",     StringComparison.Ordinal) ||
        text.Contains("/lol-gameflow/v1/session",         StringComparison.Ordinal) ||
        text.Contains("/lol-summoner/v1/current-summoner", StringComparison.Ordinal);

    // Формат события: [8, "OnJsonApiEvent", { data, eventType, uri }]
    private static LcuEvent? TryParse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3) return null;
            if (root[0].GetInt32() != 8) return null; // 8 = EVENT

            var payload = root[2];
            var uri  = payload.GetProperty("uri").GetString() ?? "";
            var type = payload.GetProperty("eventType").GetString() ?? "";
            var data = payload.GetProperty("data").Clone(); // переживёт Dispose() документа
            return new LcuEvent(uri, type, data);
        }
        catch
        {
            return null; // не та форма сообщения — игнорируем
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* ignore */ }
        }
        _ws?.Dispose();
    }
}
