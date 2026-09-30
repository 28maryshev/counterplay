using System.IO;
using System.Text.Json;

namespace Counterplay;

public static class DataDragon
{
    private sealed record ChampInfo(string Name, string DdId, string[] Tags, int Attack, int Magic,
                                    int AttackRange);

    private static Dictionary<int, ChampInfo>? _champions;
    private static string _version = "14.10.1";

    public static string Version => _version;

    // Справочник на диске: %APPDATA%\Counterplay\ddragon\champion-<локаль>.json
    // рядом с файлом версии, к которой он относится.
    private static string CacheDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterplay", "ddragon");
    private static string CachePath(string locale) => Path.Combine(CacheDir, $"champion-{locale}.json");
    private static string CacheVer(string locale)  => Path.Combine(CacheDir, $"champion-{locale}.ver");

    /// <summary>
    /// Взять справочник с диска. Ничего не ждёт и в сеть не ходит.
    ///
    /// Ради этого всё и заведено: раньше запуск упирался в два запроса к Data
    /// Dragon и до ответа не показывал ничего. Справочник меняется раз в патч —
    /// держать его в сети незачем.
    ///
    /// Вернёт false, если кэша нет (первый запуск) или он не читается.
    /// </summary>
    public static bool LoadFromCache(string locale)
    {
        try
        {
            if (!File.Exists(CachePath(locale)) || !File.Exists(CacheVer(locale))) return false;
            var ver = File.ReadAllText(CacheVer(locale)).Trim();
            if (ver.Length == 0) return false;

            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath(locale)));
            if (!doc.RootElement.TryGetProperty("data", out var data)) return false;

            _version = ver;
            Parse(data);
            Log.Write($"справочник чемпионов с диска: {_champions?.Count ?? 0}, патч {_version}");
            return _champions is { Count: > 0 };
        }
        catch { return false; }
    }

    /// Загружает имена/иконки чемпионов. locale — локаль Data Dragon (ru_RU, en_US…).
    public static async Task LoadAsync(string locale, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var vJson = await http.GetStringAsync(
                "https://ddragon.leagueoflegends.com/api/versions.json", ct);
            using var vDoc = JsonDocument.Parse(vJson);
            _version = vDoc.RootElement[0].GetString() ?? _version;

            // Локализованные имена чемпионов под выбранный язык интерфейса.
            var cJson = await http.GetStringAsync(
                $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/{locale}/champion.json", ct);
            using var cDoc = JsonDocument.Parse(cJson);
            var data = cDoc.RootElement.GetProperty("data");

            Parse(data);

            // Кладём на диск ровно то, что пришло: следующий запуск обойдётся
            // без сети. Не легло — не беда, просто сходим ещё раз.
            try
            {
                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(CachePath(locale), cJson, ct);
                await File.WriteAllTextAsync(CacheVer(locale), _version, ct);
            }
            catch { /* диск занят или полон — обойдёмся без кэша */ }

            Console.WriteLine($"Data Dragon загружен: {_champions?.Count ?? 0} чемпионов, патч {_version}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Data Dragon недоступен ({ex.Message}) — показываем ID.");
        }
    }

    /// Разбор раздела data из champion.json. Один на оба пути — сетевой и дисковый.
    private static void Parse(JsonElement data)
    {
        {
            _champions = [];
            foreach (var entry in data.EnumerateObject())
            {
                var val = entry.Value;
                if (val.TryGetProperty("key", out var keyEl) &&
                    val.TryGetProperty("name", out var nameEl) &&
                    int.TryParse(keyEl.GetString(), out var id))
                {
                    var tags = Array.Empty<string>();
                    if (val.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                        tags = tagsEl.EnumerateArray()
                                     .Where(x => x.ValueKind == JsonValueKind.String)
                                     .Select(x => x.GetString()!).ToArray();

                    int atk = 0, mag = 0;
                    if (val.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
                    {
                        if (infoEl.TryGetProperty("attack", out var aEl) && aEl.ValueKind == JsonValueKind.Number) atk = aEl.GetInt32();
                        if (infoEl.TryGetProperty("magic",  out var mEl) && mEl.ValueKind == JsonValueKind.Number) mag = mEl.GetInt32();
                    }

                    // Дальность автоатаки: у ближнего боя 125–175, у дальнего от 500.
                    // Нужна, чтобы не приписывать ближнему «бьёт с дистанции».
                    int range = 0;
                    if (val.TryGetProperty("stats", out var stEl) && stEl.ValueKind == JsonValueKind.Object
                        && stEl.TryGetProperty("attackrange", out var rEl) && rEl.ValueKind == JsonValueKind.Number)
                        range = (int)rEl.GetDouble();

                    _champions[id] = new ChampInfo(
                        Name: nameEl.GetString() ?? entry.Name,
                        DdId: entry.Name,
                        Tags: tags,
                        Attack: atk,
                        Magic: mag,
                        AttackRange: range);
                }
            }
        }
    }

    /// Русское имя чемпиона. Возвращает "id=N" если Data Dragon не загружен.
    public static string Name(int id)
    {
        if (id == 0) return "—";
        if (_champions is not null && _champions.TryGetValue(id, out var info)) return info.Name;
        return $"id={id}";
    }

    /// Классовые теги чемпиона из Data Dragon (Fighter/Tank/Mage/Assassin/Marksman/Support).
    /// Английский идентификатор чемпиона («Soraka», «JarvanIV») — по нему сверяются
    /// списки черт, которых нет в данных Riot (лечение, контроль, щиты).
    public static string DdId(int id) =>
        _champions is not null && _champions.TryGetValue(id, out var info) ? info.DdId : "";

    public static string[] ClassTags(int id) =>
        _champions is not null && _champions.TryGetValue(id, out var info) ? info.Tags : [];

    /// <summary>
    /// Бьёт ли чемпион с дистанции. Порог 300 лежит в пустоте между двумя
    /// группами: ближний бой это 125–175, дальний — от 500.
    ///
    /// Дальность берём базовую, какой её отдаёт Riot. У Кейл она вырастает по
    /// ходу игры, и по этому признаку она числится ближней — на ранней игре так
    /// и есть. Неизвестен чемпион — считаем ближним: приписать «бьёт с
    /// дистанции» тому, кто стоит в гуще, хуже, чем промолчать.
    /// </summary>
    public static bool IsRanged(int id) =>
        _champions is not null && _champions.TryGetValue(id, out var i) && i.AttackRange >= 300;

    /// <summary>
    /// Тип урона: маг, физик или «не берёмся судить».
    ///
    /// Основной признак — оценки Data Dragon (attack против magic). У семи
    /// чемпионов из 173 они РАВНЫ, и у четырёх из них там вообще нули: Riot не
    /// заполняет этот блок для новых чемпионов. Серафина — из таких, и баланс
    /// урона обходил её стороной: пятый маг в команде не получал штрафа за
    /// перекос, потому что магом его не считали.
    ///
    /// Для таких смотрим классы. Правило намеренно осторожное: «Mage без
    /// Marksman» — маг, «Marksman/Assassin/Fighter без Mage» — физик, а танк или
    /// саппорт без ясного класса так и остаются никем. Танк урона почти не
    /// носит, и записывать его в любую сторону значило бы врать балансу.
    /// </summary>
    private static int DamageKind(int id)   // +1 маг, -1 физик, 0 не знаем
    {
        if (_champions is null || !_champions.TryGetValue(id, out var info)) return 0;
        if (info.Magic > info.Attack) return  1;
        if (info.Attack > info.Magic) return -1;

        var tags = info.Tags;
        bool mage = tags.Contains("Mage"), shooter = tags.Contains("Marksman");
        if (mage && !shooter) return  1;
        if (shooter && !mage) return -1;
        if (!mage && (tags.Contains("Assassin") || tags.Contains("Fighter"))) return -1;
        return 0;   // «Mage,Marksman», чистый танк, саппорт без класса
    }

    /// Преимущественно магический урон.
    public static bool IsApChampion(int id) => DamageKind(id) > 0;

    /// Преимущественно физический урон.
    public static bool IsAdChampion(int id) => DamageKind(id) < 0;

    /// URL иконки для оверлея.
    public static string IconUrl(int id)
    {
        if (_champions is not null && _champions.TryGetValue(id, out var info))
            return $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{info.DdId}.png";
        return "";
    }

    /// Все пары id → URL иконки (для предзагрузки).
    public static IReadOnlyDictionary<int, string> GetAllIconUrls()
    {
        if (_champions is null) return new Dictionary<int, string>();
        return _champions.ToDictionary(
            kvp => kvp.Key,
            kvp => $"https://ddragon.leagueoflegends.com/cdn/{_version}/img/champion/{kvp.Value.DdId}.png");
    }
}
