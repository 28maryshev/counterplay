using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterplay;

// Журнал игр — роли игр: по составу той же игры, по драфту, главная роль; роли союзников.
public static partial class SessionTracker
{
    private static readonly string[] Roles = ["top", "jungle", "mid", "adc", "support"];

    // Игра журнала на пути к роли (см. LaneGames).
    private sealed class Lane(GameLog g, string queue)
    {
        public GameLog G { get; } = g;
        public string Queue { get; } = queue;
        public Team? Team { get; set; }
        public string? Role { get; set; }
        public bool Sure { get; set; }    // состав решил уверенно
    }

    // Состав одной игры: союзники и моя роль, записанная в связке ("" — нет).
    private sealed record Team(IReadOnlyList<int> Allies, string Stored)
    {
        public List<int> Champs(int mine) => [mine, .. Allies.Where(a => a != mine)];

        // Перевес записанной в связке роли (см. BotLaneHint, GuessHint).
        public double Hint(string role) =>
            Stored.Length == 0 || role != Stored ? 1
            : Stored is "adc" or "support" ? BotLaneHint : GuessHint;
    }

    // Записанная в связке роль бывает двух сортов. Стрелка и саппорта размечал
    // сам клиент (бот-линию он метит верно; RolesOfTeam, шаг 1) — это
    // свидетельство, перевес большой. Топ, лес и мид угадывала жадная раскладка
    // по тому же составу — это не новое знание, а та же догадка, перевес
    // маленький: Шен владельца так попал в мид (Вел'Коз — «саппорт»), а был
    // саппортом. Ясный состав перебивает и то и другое.
    private const double BotLaneHint = 6, GuessHint = 1.5;

    // Состав решил уверенно, если лучшая раскладка с моей ролью вероятнее
    // лучшей с любой другой моей ролью хотя бы во столько раз.
    private const double UnsureRatio = 3;

    // Время в журнале — момент, когда программа УВИДЕЛА игру (её конец или
    // позже), в связке — начало игры. Берём ближайшую отметку не позже записи и
    // не раньше чем за RoleMarkWindow: игру, которую программа увидела через
    // сутки, честнее отдать следующему правилу.
    private const long RoleMarkWindow = 6 * 3600;

    // Состав игры по связкам: все союзники с ближайшей отметкой (одна игра —
    // одно время начала). Союзников меньше четырёх (связки с одной игрой
    // забываются) — состав из тех, кто есть.
    private static Team? TeamOf(List<(long Ts, int Ally, string Mine)>? marks, long ts)
    {
        if (marks is null) return null;
        long at = 0, bestLag = long.MaxValue;
        foreach (var (t, _, _) in marks)
        {
            var lag = ts - t;
            if (lag < -60 || lag > RoleMarkWindow || lag >= bestLag) continue;
            bestLag = lag; at = t;
        }
        if (bestLag == long.MaxValue) return null;
        var same = marks.Where(m => m.Ts == at).ToList();
        return new Team([.. same.Select(m => m.Ally).Distinct()],
                        same.Select(m => m.Mine).FirstOrDefault(m => m.Length > 0) ?? "");
    }

    /// <summary>
    /// Самая вероятная раскладка ролей команды: перебор всех (их не больше 120),
    /// вероятность — произведение долей ролей чемпионов. Жадная раскладка, как в
    /// <see cref="RolesOfTeam"/>, тут ошибалась: Мальфит забирал топ, и Зерат
    /// (мид 0,51) обходил Ёнэ (мид 0,47) — хотя Ёнэ в миде, а Зерат в саппорте
    /// вероятнее вместе. Ноль доли — не запрет, а очень мало (бывает и такое).
    /// </summary>
    internal static string[]? BestRoles(IReadOnlyList<int> champs, Func<int, string, double> roleShare,
                                        IReadOnlyList<string>? roles = null,
                                        IReadOnlyDictionary<string, double>? myRoles = null)
    {
        var pool = roles ?? Roles;   // свободные роли (бот-линию мог уже назвать клиент)
        if (champs.Count == 0 || champs.Count > pool.Count) return null;
        // myRoles — на каких ролях я играл этого чемпиона (я — первый в списке).
        var logs = champs.Select((c, k) => pool.Select(r =>
                             Math.Log(Math.Max(roleShare(c, r), 0.005))
                             + (k == 0 && myRoles is not null ? Math.Log(myRoles.GetValueOrDefault(r, 0.005)) : 0))
                         .ToArray())
                         .ToArray();
        int[]? best = null;
        double bestScore = double.NegativeInfinity;
        var pick = new int[champs.Count];
        var used = new bool[pool.Count];
        void Go(int k, double score)
        {
            if (k == champs.Count)
            {
                if (score > bestScore) { bestScore = score; best = (int[])pick.Clone(); }
                return;
            }
            for (int r = 0; r < pool.Count; r++)
            {
                if (used[r]) continue;
                used[r] = true; pick[k] = r;
                Go(k + 1, score + logs[k][r]);
                used[r] = false;
            }
        }
        Go(0, 0);
        return best?.Select(r => pool[r]).ToArray();
    }

    /// <summary>
    /// Моя роль в игре по её составу (я — первый в списке): перебор всех
    /// раскладок, вероятность — произведение долей ролей, моя роль ещё взвешена
    /// <paramref name="myWeight"/>. Уверенность — во сколько раз лучшая раскладка
    /// с этой моей ролью вероятнее лучшей с любой другой моей ролью.
    /// </summary>
    internal static (string Role, double Confidence)? MyRoleByTeam(
        IReadOnlyList<int> champs, Func<int, string, double> roleShare, Func<string, double>? myWeight = null)
    {
        if (champs.Count == 0 || champs.Count > Roles.Length) return null;
        var logs = champs.Select((c, k) => Roles.Select(r =>
                             Math.Log(Math.Max(roleShare(c, r), 0.005))
                             + (k == 0 && myWeight is not null ? Math.Log(Math.Max(myWeight(r), 1e-6)) : 0))
                         .ToArray())
                         .ToArray();
        var bestFor = Enumerable.Repeat(double.NegativeInfinity, Roles.Length).ToArray();
        var used = new bool[Roles.Length];
        int mine = -1;
        void Go(int k, double score)
        {
            if (k == champs.Count)
            {
                if (score > bestFor[mine]) bestFor[mine] = score;
                return;
            }
            for (int r = 0; r < Roles.Length; r++)
            {
                if (used[r]) continue;
                used[r] = true;
                if (k == 0) mine = r;
                Go(k + 1, score + logs[k][r]);
                used[r] = false;
            }
        }
        Go(0, 0);
        var order = Enumerable.Range(0, Roles.Length).OrderByDescending(r => bestFor[r]).ToList();
        var conf = Math.Exp(bestFor[order[0]] - bestFor[order[1]]);
        return (Roles[order[0]], conf);
    }

    // Главная роль чемпиона — где у него больше всего игр по базе: когда ни о
    // самой игре, ни о том, как я играю этого чемпиона, ничего не известно.
    private static string? PrimaryRole(int champ, Func<int, string, double>? roleShare) =>
        roleShare is null ? null
        : Roles.Select(r => (Role: r, Share: roleShare(champ, r)))
               .Where(x => x.Share > 0)
               .OrderByDescending(x => x.Share)
               .Select(x => x.Role)
               .FirstOrDefault();

    // ── Роль из драфта ───────────────────────────────────────────────────────

    // Последний драфт: на ком я стою и какую роль мне выдали. Живёт в памяти:
    // игра попадает в историю через минуты после драфта, а если программу
    // перезапустили посередине — роль возьмётся из подробностей игры.
    private static (int Champ, string Role, long At)? _draftRole;

    // Игра создаётся сразу после драфта, так что отметка драфта стоит в паре
    // минут до её начала. Шире — уже другая игра на том же чемпионе.
    private const long DraftRoleWindow = 15 * 60;

    /// <summary>
    /// Запомнить выданную роль: Program зовёт на каждом снимке драфта, где мой
    /// чемпион подтверждён. По ней новая игра получает роль точно, а не по
    /// подробностям игры, где топ и лес угадываются по доле ролей.
    /// </summary>
    public static void NoteDraftRole(int championId, string dbRole)
    {
        if (championId <= 0 || dbRole.Length == 0) return;
        _draftRole = (championId, dbRole, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static string? DraftRoleOf(HistEntry h)
    {
        if (_draftRole is not { } d || d.Champ != h.ChampionId || h.CreatedSec <= 0) return null;
        var lag = h.CreatedSec - d.At;
        return lag is >= -300 and <= DraftRoleWindow ? d.Role : null;
    }

    /// Союзник в прошлой игре: кто, на ком и НА КАКОЙ РОЛИ. Роль нужна связкам:
    /// дуо-пул собирают под конкретную пару линий, а без роли все совместные
    /// игры валились в одну кучу — мы вдвоём на боте и мы же в лесу с мидом.
    /// Пусто — роль установить не удалось.
    private sealed record Ally(string Puuid, int ChampionId, string Name, string Role);

    /// Роли своей пятёрки в прошлой игре: participantId → роль базы.
    ///
    /// Клиент отдаёт `timeline.lane`/`role`, но верить им можно не везде. Замер
    /// на живой истории (19 игр, 38 команд): все пять ролей различимы лишь в
    /// **16%** случаев — топ-лейнеров он сплошь метит джунглерами. Надёжна одна
    /// бот-линия: **66%**. Лучшего источника у клиента нет — `teamPosition`
    /// отсутствует, `/timeline` отдаёт 404, в `stats` роли тоже нет.
    ///
    /// Поэтому берём у него то, что он знает точно (кэрри и саппорт на боте), а
    /// остальные три роли достраиваем по доле роли чемпиона — той же
    /// механикой, что раскладывает роли вражеской команде в драфте.
    /// Публична ради проверки: путь «прошлая игра → роли» иначе достижим только
    /// через живой клиент и запись в файл игрока.
    public static Dictionary<int, string> RolesOfTeam(
        IReadOnlyList<(int Pid, int Champ, string Lane, string Role)> team,
        Func<int, string, double>? roleShare)
    {
        var res = new Dictionary<int, string>();

        // 1. Бот-линия — прямо из ответа клиента, но только если она читается
        //    однозначно: ровно один кэрри и ровно один саппорт.
        var carry = team.Where(t => t.Lane == "BOTTOM" && t.Role == "CARRY").ToList();
        var sup   = team.Where(t => t.Lane == "BOTTOM" && t.Role == "SUPPORT").ToList();
        if (carry.Count == 1) res[carry[0].Pid] = "adc";
        if (sup.Count == 1)   res[sup[0].Pid]   = "support";

        // 2. Остальные — самой вероятной раскладкой по доле роли (BestRoles).
        //    Жадно («сперва самые однозначные пары») ошибалось: Мальфит забирал
        //    топ, и Зерат обходил Ёнэ в миде, хотя вместе вероятнее наоборот.
        if (roleShare is null) return res;
        var free  = team.Where(t => !res.ContainsKey(t.Pid)).ToList();
        var roles = Roles.Where(r => !res.ContainsValue(r)).ToList();
        if (BestRoles(free.Select(t => t.Champ).ToList(), roleShare, roles) is { } best)
            for (int k = 0; k < free.Count; k++) res[free[k].Pid] = best[k];
        return res;
    }
}
