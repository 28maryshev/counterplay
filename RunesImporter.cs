using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Counterplay;

/// <summary>
/// Импорт рун и набора предметов прямо в клиент LoL (через LCU).
///
/// Руны: у клиента лимит страниц — если места нет, удаляем СВОЮ прошлую
/// страницу (с нашей меткой), чужие не трогаем.
/// Предметы: набор появляется в магазине во время игры (вкладка «Наборы»).
/// </summary>
public static class RunesImporter
{
    // По этой метке узнаём свои страницы (и переиспользуем их вместо засорения).
    private const string PageMark = "Counterplay";

    /// <summary>Применить страницу рун. true — получилось.</summary>
    public static async Task<bool> ApplyRunesAsync(
        LcuHttpClient http, RunePage page, string championName, CancellationToken ct)
    {
        try
        {
            // Лимит страниц: LCU не создаст новую, если исчерпан ownedPageCount.
            // Именно на этом всё и падало в первый раз — у человека все слоты
            // заняты стандартными страницами Riot, а наших («Counterplay») ещё нет.
            int owned = 2;
            var (inv, invBody) = await http.GetAsync("/lol-perks/v1/inventory", ct);
            if (inv == 200)
            {
                using var idoc = JsonDocument.Parse(invBody);
                if (idoc.RootElement.TryGetProperty("ownedPageCount", out var oc)
                    && oc.ValueKind == JsonValueKind.Number)
                    owned = oc.GetInt32();
            }

            // Разбираем текущие страницы: свои удаляем сразу (чтобы не плодить),
            // чужие удаляемые запоминаем — понадобятся, если не хватит слота.
            var freeable = new List<(long Id, bool Current)>();
            var (ls, body) = await http.GetAsync("/lol-perks/v1/pages", ct);
            if (ls == 200)
            {
                using var doc = JsonDocument.Parse(body);
                foreach (var p in doc.RootElement.EnumerateArray())
                {
                    if (!p.TryGetProperty("id", out var idEl)) continue;
                    var id = idEl.GetInt64();
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var deletable = !p.TryGetProperty("isDeletable", out var d) || d.GetBoolean();
                    var current = p.TryGetProperty("current", out var c) && c.ValueKind == JsonValueKind.True;
                    if (!deletable) continue;

                    if (name.Contains(PageMark, StringComparison.Ordinal))
                        await http.DeleteAsync($"/lol-perks/v1/pages/{id}", ct);
                    else
                        freeable.Add((id, current));
                }
            }

            // Если слотов всё ещё нет — освобождаем один. Жертвуем активной
            // страницей (её мы и так сейчас заменим), иначе первой удаляемой.
            var remaining = await CountPagesAsync(http, ct);
            if (remaining >= owned && freeable.Count > 0)
            {
                var victim = freeable.FirstOrDefault(f => f.Current);
                if (victim.Id == 0) victim = freeable[0];
                await http.DeleteAsync($"/lol-perks/v1/pages/{victim.Id}", ct);
            }

            // Порядок важен: 4 основных (первый — кейстоун), 2 вторичных, 3 осколка.
            var selected = page.Perks.Concat(page.Secondary).Concat(page.Shards).ToArray();
            var payload = JsonSerializer.Serialize(new
            {
                // Подпись: персонаж — название кейстоуна — Counterplay.
                name = $"{championName} - {RuneIcons.NameOf(page.Perks[0])} - {PageMark}",
                primaryStyleId = page.Primary,
                subStyleId = page.Sub,
                selectedPerkIds = selected,
                current = true,   // сразу делаем активной
            });

            var (s, _) = await http.PostAsync("/lol-perks/v1/pages", payload, ct);
            return s is >= 200 and < 300;
        }
        catch { return false; }
    }

    /// <summary>
    /// Навести чемпиона (hover) в клиенте — обратимо, ничего не лочит.
    /// Возвращает HTTP-код (0 — исключение) для диагностики.
    /// </summary>
    public static async Task<int> HoverChampionAsync(
        LcuHttpClient http, int actionId, int championId, CancellationToken ct)
    {
        // id действия начинается с 0 (кастомки!) — «нет действия» это -1.
        if (actionId < 0 || championId <= 0) return 0;
        try
        {
            // championId без completed — только наведение (пик не завершаем).
            var payload = JsonSerializer.Serialize(new { championId });
            var (s, _) = await http.PatchAsync($"/lol-champ-select/v1/session/actions/{actionId}", payload, ct);
            return s;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Залочить наведённого чемпиона (необратимо). Только в свой ход.
    /// Пробуем оба известных способа: PATCH completed=true, затем POST /complete.
    /// </summary>
    public static async Task<int> LockChampionAsync(
        LcuHttpClient http, int actionId, int championId, CancellationToken ct)
    {
        // id действия начинается с 0 (кастомки!) — «нет действия» это -1.
        if (actionId < 0 || championId <= 0) return 0;
        try
        {
            // Сначала наводим (на случай если ещё не наведён).
            var hover = JsonSerializer.Serialize(new { championId });
            await http.PatchAsync($"/lol-champ-select/v1/session/actions/{actionId}", hover, ct);

            // Способ 1: PATCH completed=true (работает в большинстве версий клиента).
            var done = JsonSerializer.Serialize(new { championId, completed = true });
            var (s1, b1) = await http.PatchAsync($"/lol-champ-select/v1/session/actions/{actionId}", done, ct);
            if (s1 is >= 200 and < 300) return s1;
            Console.WriteLine($"[pick] PATCH completed=true → {s1}: {Trim(b1)}");

            // Способ 2: POST .../complete с championId в теле.
            var (s2, b2) = await http.PostAsync(
                $"/lol-champ-select/v1/session/actions/{actionId}/complete", hover, ct);
            if (s2 is < 200 or >= 300)
                Console.WriteLine($"[pick] POST /complete → {s2}: {Trim(b2)}");
            return s2;
        }
        catch (Exception ex) { Console.WriteLine($"[pick] lock exception: {ex.Message}"); return 0; }
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;

    /// <summary>
    /// Выставить саммонер-спеллы в champ select (вместе с рунами). Порядок
    /// подстраивается под привычку игрока: если у него Флеш сейчас стоит на
    /// втором слоте (клавиша F) — Флеш и останется на втором, и наоборот.
    /// </summary>
    public static async Task<bool> ApplySpellsAsync(
        LcuHttpClient http, IReadOnlyList<int> spells, CancellationToken ct)
    {
        if (spells.Count < 2 || spells[0] <= 0 || spells[1] <= 0) return false;
        try
        {
            int a = spells[0], b = spells[1];
            const int Flash = 4;

            // Читаем текущий выбор игрока — из него узнаём привычный слот Флеша.
            var (s, body) = await http.GetAsync("/lol-champ-select/v1/session/my-selection", ct);
            if (s == 200 && (a == Flash || b == Flash))
            {
                using var doc = JsonDocument.Parse(body);
                long cur1 = GetLong(doc.RootElement, "spell1Id");
                long cur2 = GetLong(doc.RootElement, "spell2Id");
                int other = a == Flash ? b : a;
                if (cur2 == Flash)      (a, b) = (other, Flash); // Флеш на F — остаётся справа
                else if (cur1 == Flash) (a, b) = (Flash, other); // Флеш на D — остаётся слева
            }

            var payload = JsonSerializer.Serialize(new { spell1Id = a, spell2Id = b });
            var (ps, _) = await http.PatchAsync("/lol-champ-select/v1/session/my-selection", payload, ct);
            return ps is >= 200 and < 300;
        }
        catch { return false; }
    }

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static async Task<int> CountPagesAsync(LcuHttpClient http, CancellationToken ct)
    {
        var (s, body) = await http.GetAsync("/lol-perks/v1/pages", ct);
        if (s != 200) return 0;
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.GetArrayLength(); }
        catch { return 0; }
    }

    // Стартовые предметы по роли. MATCH-V5 отдаёт только финальный инвентарь —
    // стартовые к концу игры проданы/выпиты, в статистике их нет. Это разумный
    // дефолт по роли (настоящие цифры — только из Timeline API).
    private static int[] StartItems(string role) => role switch
    {
        "adc"     => [1055, 2003],        // Меч Дорана + зелье
        "mid"     => [1056, 2003],        // Кольцо Дорана + зелье
        "top"     => [1054, 2003],        // Щит Дорана + зелье
        "support" => [3865, 2003],        // Мир-Атлас + зелье
        "jungle"  => [1101, 2003],        // талисман джангла + зелье
        _         => [2003],
    };

    /// <summary>
    /// Экспортировать набор предметов (виден в магазине в игре). Блоки:
    /// 1) стартовые предметы (по роли);
    /// 2) промежуточные — компоненты готового билда;
    /// 3) CORE — готовые предметы сборки;
    /// 4) ситуативные — что ещё часто берут на чемпионе.
    /// </summary>
    public static async Task<bool> ExportItemSetAsync(
        LcuHttpClient http, IReadOnlyList<int> core, IReadOnlyList<int> full,
        IReadOnlyList<int> situational, string role, int championId, string championName,
        CancellationToken ct)
    {
        try
        {
            var (ss, sbody) = await http.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (ss != 200) return false;
            using var sdoc = JsonDocument.Parse(sbody);
            var summonerId = sdoc.RootElement.GetProperty("summonerId").GetInt64();

            // Забираем существующие наборы, чтобы не затереть чужие.
            var (gs, gbody) = await http.GetAsync($"/lol-item-sets/v1/item-sets/{summonerId}/sets", ct);
            var existing = new List<JsonElement>();
            if (gs == 200)
            {
                using var gdoc = JsonDocument.Parse(gbody);
                if (gdoc.RootElement.TryGetProperty("itemSets", out var arr))
                    foreach (var set in arr.EnumerateArray())
                    {
                        var title = set.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                        if (!title.Contains(PageMark, StringComparison.Ordinal))
                            existing.Add(set.Clone());   // свои старые наборы заменяем
                    }
            }

            static object[] Items(IEnumerable<int> ids) =>
                ids.Select(i => (object)new { id = i.ToString(), count = 1 }).ToArray();

            object Block(string type, IEnumerable<int> ids) => new
            {
                type,
                showIfSummonerSpell = "",
                hideIfSummonerSpell = "",
                items = Items(ids),
            };

            // Промежуточные предметы — все компоненты готового билда (базовые →
            // продвинутые), кроме самих финальных предметов. Отдельным блоком.
            var fullSet = new HashSet<int>(full);
            var compSeq = new List<int>();
            var compSeen = new HashSet<int>();
            foreach (var item in full)
                foreach (var part in ItemIcons.WithComponents(item))
                    if (!fullSet.Contains(part) && compSeen.Add(part))
                        compSeq.Add(part);

            // CORE и ситуативные — только готовые предметы, без компонентов.
            var alt = situational.Distinct().ToList();

            var blocks = new List<object>
            {
                Block(Loc.T("runes.startBlock"), StartItems(role)),
            };
            if (compSeq.Count > 0) blocks.Add(Block(Loc.T("runes.compBlock"), compSeq));
            if (full.Count > 0)    blocks.Add(Block(Loc.T("runes.coreBlock"), full));
            if (alt.Count > 0)     blocks.Add(Block(Loc.T("runes.altBlock"), alt));

            var mySet = new
            {
                associatedChampions = new[] { championId },
                associatedMaps = new[] { 11 },
                title = $"{championName} - {PageMark}",
                type = "custom",
                map = "any",
                mode = "any",
                sortrank = 1,
                startedFrom = "blank",
                preferredItemSlots = Array.Empty<object>(),
                uid = Guid.NewGuid().ToString(),
                blocks = blocks.ToArray(),
            };

            var sets = new List<object>();
            foreach (var e in existing) sets.Add(JsonSerializer.Deserialize<object>(e.GetRawText())!);
            sets.Add(mySet);

            var payload = JsonSerializer.Serialize(new { accountId = 0, itemSets = sets, timestamp = 0 });
            var (ps, _) = await http.PutAsync($"/lol-item-sets/v1/item-sets/{summonerId}/sets", payload, ct);
            return ps is >= 200 and < 300;
        }
        catch { return false; }
    }
}
