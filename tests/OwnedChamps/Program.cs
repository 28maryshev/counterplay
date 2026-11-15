using System.IO;
using System.Text;
using System.Text.Json;
using Counterplay;

namespace Counterplay.Tests;

/// <summary>
/// Отметки «нет на аккаунте» в боевом драфте: список чемпионов аккаунта,
/// который клиент отдаёт не сразу.
///
/// 15.11 владелец: «перестал работать отсутствующий чемпион». Лог самого
/// клиента (LeagueClient-tracing.json) показал одно и то же в каждом запуске:
/// программа спрашивает список на ~9-й секунде и получает 404 «Champion data
/// has not yet been received», а список приходит событием на ~18-й. Программа
/// спрашивала один раз, событие отбрасывал фильтр сокета, и пустой ответ
/// значил «владение неизвестно» до конца сеанса.
///
/// Здесь та же последовательность на подставном клиенте: ответы на запрос —
/// очередь, событие — сырой текст, каким его шлёт клиент, через тот же фильтр
/// сокета, что в бою. Плюс пропущенное событие (выручает начало драфта) и
/// смена аккаунта.
/// </summary>
internal static class Program
{
    private const int Annie = 1, Ryze = 13, Garen = 86, Talon = 91, Hwei = 910;

    private static int _fails;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Log.FileDisabled = true;
        var ct = CancellationToken.None;

        // ── Сегодняшний запуск клиента ────────────────────────────────────
        {
            var c = new FakeClient();
            c.Answers.Enqueue([]);                     // 9-я секунда: 404
            await c.Owned.LoadAsync("подключение", ct);
            Check("подключение до загрузки клиента: отметок нет, но и не «известно пусто»",
                  c.Published.Count == 0 && !c.Owned.Known, c.Describe());

            // 18-я секунда: клиент присылает список событием.
            var raw = EventText("Update", [(Annie, true, false), (Ryze, false, true),
                                           (Talon, true, false), (Hwei, true, false)]);
            Check("событие со списком проходит фильтр сокета",
                  LcuEventSocket.IsRelevant(raw), "IsRelevant отбросил " + OwnedChamps.EventUri);
            var handled = c.Owned.TryHandle(ParseEvent(raw));
            Check("событие со списком разобрано: Райза (бесплатная ротация) нет, остальные есть",
                  handled && c.Last is { } l && l.SetEquals([Annie, Talon, Hwei]), c.Describe());

            // Драфт: список есть — клиент лишний раз не спрашиваем.
            var asked = c.Asked;
            await c.Owned.EnsureAsync(ct);
            Check("в драфте со списком на руках клиент не спрашиваем", c.Asked == asked,
                  $"запросов было {asked}, стало {c.Asked}");

            // Клиент на миг ответил ошибкой — список не пропадает.
            c.Answers.Enqueue([]);
            await c.Owned.LoadAsync("подключение", ct);
            c.Owned.TryHandle(ParseEvent(EventText("Delete", [])));
            Check("пустой ответ и Delete прежний список не затирают",
                  c.Last is { } l2 && l2.Count == 3 && c.Owned.Known, c.Describe());

            // Купил Райза: клиент шлёт список заново.
            c.Owned.TryHandle(ParseEvent(EventText("Update", [(Annie, true, false), (Ryze, true, false),
                                                             (Talon, true, false), (Hwei, true, false)])));
            Check("покупка чемпиона: новый список доезжает событием",
                  c.Last is { } l3 && l3.Contains(Ryze), c.Describe());

            // Чужие темы — не его.
            Check("события других тем не трогает",
                  !c.Owned.TryHandle(new LcuEvent("/lol-champ-select/v1/session", "Update", default)), "");
        }

        // ── Событие пропустили: выручает начало драфта ────────────────────
        {
            var c = new FakeClient(TimeSpan.Zero);
            c.Answers.Enqueue([]);
            await c.Owned.LoadAsync("подключение", ct);
            c.Answers.Enqueue([Annie, Talon]);
            await c.Owned.EnsureAsync(ct);
            Check("событие потерялось — список прочитан в начале драфта",
                  c.Last is { } l && l.SetEquals([Annie, Talon]), c.Describe());
        }

        // ── События драфта идут пачкой: запрос не на каждое ───────────────
        {
            var c = new FakeClient(TimeSpan.FromMinutes(1));
            c.Answers.Enqueue([]);
            await c.Owned.LoadAsync("подключение", ct);
            for (var i = 0; i < 5; i++) await c.Owned.EnsureAsync(ct);
            Check("пять событий драфта подряд без списка — ни одного лишнего запроса",
                  c.Asked == 1, $"запросов {c.Asked}");
        }

        // ── Смена аккаунта ────────────────────────────────────────────────
        {
            var c = new FakeClient();
            c.Answers.Enqueue([Annie, Talon, Hwei]);
            await c.Owned.LoadAsync("подключение", ct);
            c.Owned.Forget();
            Check("сменился аккаунт: чужой список снят сразу",
                  c.Last is { Count: 0 } && !c.Owned.Known, c.Describe());
            c.Answers.Enqueue([Garen]);
            await c.Owned.LoadAsync("вход в аккаунт", ct);
            Check("список нового аккаунта на месте", c.Last is { } l && l.SetEquals([Garen]), c.Describe());
        }

        // ── Сеанс в бою собран из этих деталей ────────────────────────────
        // Без вызова любая из них — снова «отметок нет весь сеанс». Читаем всю
        // папку Program, а не один файл.
        {
            var src = string.Concat(Directory.GetFiles(Path.Combine(Root(), "src", "App"), "Program*.cs")
                                             .Select(File.ReadAllText));
            foreach (var call in new[] { "new OwnedChamps(", "owned.TryHandle(ev)", "owned.EnsureAsync(",
                                         "owned.Forget()" })
                Check($"сеанс зовёт {call}", src.Contains(call), "нет в src/App/Program*.cs");
        }

        Console.WriteLine();
        Console.WriteLine(_fails == 0 ? "ИТОГ: отметки «нет на аккаунте» доезжают, даже если клиент только что запущен"
                                      : $"ИТОГ: провалено — {_fails}");
        return _fails == 0 ? 0 : 1;
    }

    /// Подставной клиент: ответы на запрос по очереди (пусто — 404), всё,
    /// что ушло в окно, — по порядку.
    private sealed class FakeClient
    {
        public readonly Queue<HashSet<int>> Answers = new();
        public readonly List<HashSet<int>> Published = [];
        public int Asked;
        public readonly OwnedChamps Owned;

        public FakeClient(TimeSpan? gap = null)
        {
            Owned = new OwnedChamps(_ =>
            {
                Asked++;
                return Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : new HashSet<int>());
            }, ids => Published.Add(ids)) { RetryGap = gap ?? TimeSpan.FromSeconds(10) };
        }

        public HashSet<int>? Last => Published.Count > 0 ? Published[^1] : null;

        public string Describe() => Published.Count == 0 ? "в окно ничего не ушло"
            : $"в окно ушло {Published.Count} раз, последний: [{string.Join(",", Last!.Order())}]";
    }

    /// Событие так, как его шлёт клиент: [8, "OnJsonApiEvent", {data, eventType, uri}],
    /// у чемпиона те же поля, что в настоящем ответе (id, ownership.owned, freeToPlay).
    private static string EventText(string type, (int Id, bool Owned, bool Free)[] champs)
    {
        var data = string.Join(",", champs.Select(c =>
            $"{{\"id\":{c.Id},\"active\":true,\"freeToPlay\":{(c.Free ? "true" : "false")}," +
            $"\"ownership\":{{\"loyaltyReward\":false,\"owned\":{(c.Owned ? "true" : "false")}," +
            "\"rental\":{\"endDate\":0,\"purchaseDate\":0,\"rented\":false,\"winCountRemaining\":0}}}"));
        return $"[8,\"OnJsonApiEvent\",{{\"data\":[{data}],\"eventType\":\"{type}\",\"uri\":\"{OwnedChamps.EventUri}\"}}]";
    }

    private static LcuEvent ParseEvent(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var p = doc.RootElement[2];
        return new LcuEvent(p.GetProperty("uri").GetString()!, p.GetProperty("eventType").GetString()!,
                            p.GetProperty("data").Clone());
    }

    private static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Counterplay.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("не нашёл корень репозитория");
    }

    private static void Check(string what, bool ok, string detail)
    {
        Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}{(ok || detail == "" ? "" : "  — " + detail)}");
        if (!ok) _fails++;
    }
}
