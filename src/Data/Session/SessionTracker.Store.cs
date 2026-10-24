using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — модель файла session.json: записи игр, очередей, аккаунтов и связок.
public static partial class SessionTracker
{
    // ── Журнал игр (персистентный) ─────────────────────────────────────────
    private sealed class GameLog
    {
        public long Ts { get; set; }            // unix seconds
        public int ChampionId { get; set; }
        public bool Win { get; set; }
        public int? Lp { get; set; }            // LP-дельта (только ранкед-очереди)
        public double Wr { get; set; }          // винрейт очереди на момент игры
        // Моя роль в этой игре (top/jungle/mid/adc/support). Пишется с 1.3.61:
        // у старых игр её нет, и роль достраивается по связкам (LaneChamps).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Role { get; set; }
        // Номер игры в клиенте и время её начала. Пишутся с 1.3.63: по ним
        // узнаётся та же игра, записанная двумя компьютерами, — время записи
        // (Ts) у них разное (см. SameGames).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Id { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public long? Start { get; set; }
    }

    private sealed class QueueLog
    {
        public int LastAbsLp { get; set; } = int.MinValue;
        public List<GameLog> Games { get; set; } = [];

        // Опорная точка графика для ранговых очередей: сезонный винрейт на
        // момент первого подключения. Клиент знает его сразу, поэтому линии не
        // нужно ждать пять игр — она начинается с реального значения игрока.
        public long AnchorTs { get; set; }
        public double AnchorWr { get; set; }
    }

    private sealed class RankedCache
    {
        public bool HasRank { get; set; }
        public string Tier { get; set; } = "";
        public string Div { get; set; } = "";
        public int Lp { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
    }

    /// <summary>
    /// Данные ОДНОГО аккаунта. Раньше всё лежало в общей куче, и при смене
    /// аккаунта: ник не обновлялся, а LP-дельта считалась между рангами разных
    /// аккаунтов (эмеральд → золото давало «−492 LP» за первую же игру).
    /// </summary>
    private sealed class Account
    {
        public string? Nick { get; set; }
        public string SelectedQueue { get; set; } = "solo";
        public bool QueueChosen { get; set; }
        public long LastGameId { get; set; }
        /// <summary>
        /// Номера уже засчитанных игр. Раньше хватало одного «последнего»
        /// номера: считалось, что новая игра — это та, у которой номер больше.
        /// Оказалось, номера НЕ растут со временем. 22.09 игра, начатая в 10:27,
        /// получила номер 7991423371, а предыдущая, в 09:51, — 7991430692:
        /// победа просто не засчиталась, и перезапуск не помогал, потому что
        /// отметка лежит на диске. Поэтому помним сами номера, а порядок берём
        /// по времени начала игры.
        /// </summary>
        public List<long> SeenGames { get; set; } = new();
        public Dictionary<string, QueueLog> Queues { get; set; } = new();
        // Последний успешный снимок рангов: LCU после рестарта/обновления может
        // не ответить — панель всё равно рендерится из кэша, а не пустой.
        public Dictionary<string, RankedCache> Ranked { get; set; } = new();

        /// <summary>
        /// Винрейт связки: сколько игр на КОНКРЕТНОЙ паре «мой чемпион + его
        /// чемпион» сыграно именно с этим человеком и сколько из них выиграно.
        ///
        /// Ключ — <c>puuid|мой чемпион|его чемпион</c>. Напарник опознаётся по
        /// puuid, а не по нику: ник меняется, puuid — нет.
        ///
        /// Копится только вперёд. История LCU отдаёт около двадцати последних
        /// игр и глубже не пускает (проверено: запросы с begIndex 100 и 200
        /// возвращают тот же список), так что задним числом взять неоткуда.
        /// </summary>
        public Dictionary<string, PairRec> Pairs { get; set; } = new();

        /// <summary>
        /// Игры, уже посчитанные в связки. Отдельно от <see cref="SeenGames"/>
        /// нарочно: связки появились позже, и у тех, кто играл с программой
        /// раньше, все игры в истории уже «виденные». Считая по SeenGames, мы
        /// не дали бы им ни одной связки — а пул с другом у них уже настроен.
        /// Свой список позволяет разово пройти по тому, что клиент ещё помнит.
        /// </summary>
        public List<long> PairGames { get; set; } = new();

        /// <summary>
        /// С кем ты СТОЯЛ В ОЧЕРЕДИ соло/дуо — puuid → ник и время последнего раза.
        ///
        /// Связки копятся на всех союзников подряд: в флексе и нормалах их
        /// четверо, и список «напарников» разрастался до всех, с кем вообще
        /// доводилось играть. Напарник по дуо-пулу — это человек, с которым
        /// ВСТАЁШЬ В ОЧЕРЕДЬ, и знает это только лобби.
        ///
        /// Пишем лишь из лобби соло/дуо (queueId 420): там в пати может быть
        /// ровно один человек, и это он и есть.
        /// </summary>
        public Dictionary<string, QueuedMate> Queued { get; set; } = new();

        /// <summary>
        /// Записи, доказанно чужие и убранные (см. <c>ProveForeignAsync</c>):
        /// игры другого аккаунта, легшие в этот журнал при смене аккаунта до
        /// 1.3.63. Слияние синхронизации убирает по этим отпечаткам те же записи,
        /// пришедшие с копии другого компьютера, — иначе они возвращались бы
        /// каждым проходом. Нет убранного — поля в файле нет.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ForeignMark>? Foreign { get; set; }
    }

    /// Отпечаток убранной чужой записи: где лежала и что в ней было. Время
    /// записи у пачки одно на все записи и своё у каждой пачки — с чемпионом и
    /// исходом его хватает, чтобы узнать ту же запись на любом компьютере.
    private sealed class ForeignMark
    {
        public string Queue { get; set; } = "";
        public long Ts { get; set; }
        public int ChampionId { get; set; }
        public bool Win { get; set; }
    }

    /// Человек, с которым стояли в очереди соло/дуо.
    public sealed class QueuedMate
    {
        public string Name { get; set; } = "";
        /// Unix-секунды последнего раза — по ним список стареет.
        public long Seen { get; set; }
    }

    /// <summary>
    /// Одна связка. Хранит не счёт, а ВРЕМЯ каждой совместной игры — отдельно
    /// выигранных и проигранных.
    ///
    /// Счётчиков было мало: из «11 игр, 8 побед» не вычесть прошлый месяц, а
    /// период в окне переключается. Два массива чисел в json занимают немного,
    /// и из них считается и общий счёт, и любое окно.
    /// </summary>
    private sealed class PairRec
    {

        public List<long> Won  { get; set; } = new();
        public List<long> Lost { get; set; } = new();
        /// Ник напарника, каким он был в последней совместной игре. Только для
        /// показа: опознаём человека по puuid, ник может смениться.
        public string Name { get; set; } = "";

        /// Сколько сыграно и выиграно не раньше указанного времени (0 — всё).
        public (int Games, int Wins) Count(long since)
        {
            var w = Won.Count(t => t >= since);
            var l = Lost.Count(t => t >= since);
            return (w + l, w);
        }

        /// Время последней совместной игры; 0 — игр нет.
        /// Наибольшая отметка, а не последняя в списке: по этому времени связки
        /// чистит и журнал, и синхронизация, и обязаны выбрать одни и те же.
        public long Last() => Math.Max(Won.Count > 0 ? Won.Max() : 0,
                                       Lost.Count > 0 ? Lost.Max() : 0);

        /// Связка живёт годами, а нужны из неё последние игры: держим последние
        /// PairKeep с каждой стороны, чтобы файл не рос без конца.
        public void Trim()
        {
            if (Won.Count  > PairKeep) Won.RemoveRange(0, Won.Count - PairKeep);
            if (Lost.Count > PairKeep) Lost.RemoveRange(0, Lost.Count - PairKeep);
        }
    }

    /// Сколько последних игр связки держать с каждой стороны (см.
    /// <c>PairRec.Trim</c>). Снаружи нужно слиянию синхронизации: оно
    /// складывает отметки двух компьютеров.
    internal const int PairKeep = 200;

    /// Одна связка наружу: с кем, на ком, с каким счётом.
    public sealed record PairStat(
        string AllyPuuid, string AllyName, string Queue,
        int MyChampionId, int AllyChampionId, int Games, int Wins,
        string MyRole = "", string AllyRole = "")
    {
        /// Роли известны у обеих сторон — значит по ним можно и отбирать.
        public bool HasRoles => MyRole.Length > 0 && AllyRole.Length > 0;

        public double WinRate => Games > 0 ? 100.0 * Wins / Games : 0;
    }

    private sealed class Store
    {
        // Ключ — puuid (стабилен и не меняется при переименовании).
        public Dictionary<string, Account> Accounts { get; set; } = new();
        // Кем играли в прошлый раз — чтобы панель не пустела до ответа LCU.
        public string? LastAccount { get; set; }

        // ── Наследие одноаккаунтного формата (мигрирует при первом запуске) ──
        public string? SelectedQueue { get; set; }
        public bool QueueChosen { get; set; }
        public string? Nick { get; set; }
        public long LastGameId { get; set; }
        public Dictionary<string, QueueLog>? Queues { get; set; }
        public Dictionary<string, RankedCache>? Ranked { get; set; }
        public List<LegacyGame>? Games { get; set; }
        public int LastAbsLp { get; set; } = int.MinValue;
    }

    private sealed class LegacyGame
    {
        public long Ts { get; set; }
        public int ChampionId { get; set; }
        public bool Win { get; set; }
        public int LpDelta { get; set; }
        public double Winrate { get; set; }
    }
}
