using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Опрос на плашке сообщений: голос и память о нём.
///
/// Вопрос и варианты приходят с сайта вместе с остальными сообщениями
/// (<see cref="Notice"/>). Здесь — что выбрал этот компьютер и сколько всего
/// голосов у каждого варианта. Голос один на установку (номер — тот же, что у
/// телеметрии) и окончательный: переголосовать нельзя, после ответа плашка
/// показывает итог.
///
/// Память о голосе — <c>notice-votes.json</c>: вариант и счёт на момент
/// голоса. Сайт и сам помнит голос, но выдача сообщений у него одна на всех
/// (кэш на минуту), и «я уже ответил» в ней не узнать.
/// </summary>
public static class NoticePoll
{
    private const string VoteUrl = "https://counterplays.com/api/notice/vote";

    private static string FilePath => Path.Combine(AppPaths.Root, "notice-votes.json");

    /// Что запомнили о голосе: выбранный вариант и счёт, который вернул сайт.
    public sealed class Vote
    {
        public string Mine { get; set; } = "";
        public Dictionary<string, int> Counts { get; set; } = new();
    }

    /// Чем кончилась отправка: принят, опрос уже закрыт, не дошло.
    public enum Outcome { Ok, Closed, Failed }

    private static Dictionary<string, Vote>? _votes;

    private static string _votesPath = "";

    // Голоса песочницы — только в памяти и отдельно от настоящих: вышел из
    // песочницы — и опрос снова ждёт настоящего ответа.
    private static readonly Dictionary<string, Vote> SandboxVotes = new();

    private static Dictionary<string, Vote> Votes()
    {
        if (Sandbox.Active) return SandboxVotes;

        // Папку подменяют проверки — память читается заново под новый путь.
        var path = FilePath;
        if (_votes is not null && _votesPath == path) return _votes;
        _votesPath = path;
        _votes = new();
        try
        {
            if (File.Exists(path))
                _votes = JsonSerializer.Deserialize<Dictionary<string, Vote>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { Log.Write($"опрос: память о голосах не прочиталась — {ex.Message}"); }
        return _votes;
    }

    /// Каким вариантом ответил этот компьютер; null — ещё не отвечал.
    public static string? Mine(Notice.Item item) =>
        Votes().TryGetValue(item.Id, out var v) && v.Mine.Length > 0 ? v.Mine : null;

    /// <summary>
    /// Счёт для показа.
    ///
    /// Источников два: ответ сайта на свой голос (свежий в ту секунду) и
    /// выдача сообщений (минута кэша, спрашиваем раз в полчаса). Сразу после
    /// голоса выдача ещё может не знать о нём — и свой вариант стоял бы с
    /// нулём. Голоса только прибавляются, поэтому свежее тот счёт, где их
    /// больше, — считая лишь варианты, которые у опроса есть сейчас.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Counts(Notice.Item item)
    {
        if (item.Poll is null || !Votes().TryGetValue(item.Id, out var v)) return item.Counts;

        int Sum(IReadOnlyDictionary<string, int> c) =>
            item.Poll.Sum(o => c.TryGetValue(o.Id, out var n) ? n : 0);
        return Sum(v.Counts) > Sum(item.Counts) ? v.Counts : item.Counts;
    }

    /// <summary>
    /// Отправить голос. Сайт отвечает счётом — его и запоминаем вместе с
    /// выбором. Если сайт уже знает голос этой установки (память о нём
    /// пропала), он вернёт прежний вариант, и запомнится именно он.
    ///
    /// Песочница и проверки не голосуют по-настоящему: голос прибавляется к
    /// известному счёту на месте. Проверки пишут память в свою временную
    /// папку, песочница не пишет ничего.
    /// </summary>
    public static async Task<Outcome> VoteAsync(Notice.Item item, string optionId)
    {
        if (item.Poll is null || item.Poll.All(o => o.Id != optionId)) return Outcome.Failed;

        if (Sandbox.Active || Notice.Frozen)
        {
            var counts = new Dictionary<string, int>(Counts(item));
            counts[optionId] = counts.GetValueOrDefault(optionId) + 1;
            Remember(item.Id, optionId, counts);
            return Outcome.Ok;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            if (Telemetry.Secret.Length > 0)
                http.DefaultRequestHeaders.Add("x-telemetry-secret", Telemetry.Secret);
            var body = JsonSerializer.Serialize(new
            {
                installId = Telemetry.DeviceId(), notice = item.Id, option = optionId,
            });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var r = await http.PostAsync(VoteUrl, content);

            // 410 — опрос выключили или он истёк, пока висел на плашке.
            if (r.StatusCode == HttpStatusCode.Gone)
            {
                Log.Write($"опрос {item.Id}: уже закрыт");
                return Outcome.Closed;
            }
            if (!r.IsSuccessStatusCode)
            {
                Log.Write($"опрос {item.Id}: голос не принят — {(int)r.StatusCode}");
                return Outcome.Failed;
            }

            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var mine = root.TryGetProperty("mine", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? optionId : optionId;
            Remember(item.Id, mine, Notice.ReadCounts(root));
            Log.Write($"опрос {item.Id}: голос за {mine}");
            return Outcome.Ok;
        }
        catch (Exception ex)
        {
            Log.Write($"опрос {item.Id}: голос не отправился — {ex.Message}");
            return Outcome.Failed;
        }
    }

    private static void Remember(string noticeId, string mine, Dictionary<string, int> counts)
    {
        var votes = Votes();
        votes[noticeId] = new Vote { Mine = mine, Counts = counts };
        if (Sandbox.Active) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(votes));
        }
        catch (Exception ex) { Log.Write($"опрос: память о голосе не записалась — {ex.Message}"); }
    }
}
