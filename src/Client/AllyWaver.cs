namespace Counterplay;

/// <summary>
/// Союзник, который быстро перебирает чемпионов наведением. Каждое его
/// наведение пересчитывало подбор, и центр окна скакал вслед за ним: игрок не
/// успевал прочитать список, как тот менялся (просьба владельца, 14.11.2026).
///
/// Сменил наведение <see cref="Switches"/> раза за <see cref="Window"/> —
/// «мечется»: с этого снимка подбор считает его по среднему из всех
/// чемпионов, которых он показал с начала метаний
/// (<c>RecommendationEngine.Waver.cs</c>), и перестаёт идти за каждым
/// наведением. Возврат к уже показанному чемпиону не меняет ничего, новый
/// добавляется в среднее. Кончается, когда союзник залочил или держит одно
/// наведение <see cref="Calm"/>: тогда снова считается его настоящий выбор.
///
/// Только союзники: своё наведение игрок делает сам, а врагов клиент до лока
/// не показывает. Состояние — на один драфт (<see cref="Reset"/>).
/// </summary>
public sealed class AllyWaver
{
    /// Столько смен наведения подряд…
    public const int Switches = 3;
    /// …за столько времени от первой смены до последней — «мечется». Три смены
    /// за 10 секунд — по пять секунд на чемпиона: столько уходит, чтобы глянуть
    /// и передумать, а не чтобы выбрать. Сколько он держал чемпиона до первой
    /// смены, не важно.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    /// Столько держит одно наведение — успокоился, снова считаем его выбор.
    public static readonly TimeSpan Calm = TimeSpan.FromSeconds(20);
    /// Больше стольких в среднее не берём (старые уходят): каждый показанный —
    /// ещё один полный пересчёт подбора.
    public const int MaxShown = 8;

    private sealed class Slot
    {
        public int Hover;            // наведение сейчас (0 — никого)
        public int LastShown;        // последний показанный (не 0): смена — это «другой, чем он»
        public DateTime ChangedAt;   // когда наведение менялось в последний раз
        // Последние показы по порядку: по ним видно, быстро ли он меняет.
        public readonly List<(int Champ, DateTime At)> Shows = [];
        public List<int>? Shown;     // мечется: кого показал с начала метаний; null — не мечется
    }

    private readonly Dictionary<int, Slot> _slots = [];

    /// Новый драфт — с чистого листа.
    public void Reset() => _slots.Clear();

    /// <summary>
    /// Союзник залочил: чемпион в слоте есть, и его ход пика кончился. В свой
    /// ход наведённый стоит у клиента в <c>championId</c> ещё до лока — по
    /// одному <see cref="DraftPlayer.ChampionId"/> метания в его собственный ход
    /// сошли бы за лок, а перебирают чаще всего как раз тогда. Одно правило на
    /// слежку и на подбор по среднему.
    /// </summary>
    public static bool Locked(DraftState s, DraftPlayer p) =>
        p.ChampionId != 0 && !s.ActiveCells.Contains(p.CellId);

    /// <summary>
    /// Учесть снимок драфта и сказать, кто из союзников сейчас мечется:
    /// ячейка → показанные им с начала метаний (по порядку показа). null —
    /// никто. Звать на каждом снимке, а не только на изменённых: «успокоился»
    /// наступает без всякого события.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>>? Observe(DraftState s, DateTime now)
    {
        foreach (var p in s.MyTeam)
        {
            if (p.IsLocalPlayer) continue;
            if (Locked(s, p))
            {
                // Залочил — выбор сделан, считается он сам.
                if (_slots.Remove(p.CellId, out var done) && done.Shown is not null)
                    Log.Write($"союзник {p.CellId} залочил {DataDragon.Name(p.ChampionId)} — подбор снова по нему");
                continue;
            }
            if (!_slots.TryGetValue(p.CellId, out var slot))
                _slots[p.CellId] = slot = new Slot { ChangedAt = now };
            Step(p.CellId, slot, p.EffectiveChampionId, now);
        }

        var res = _slots.Where(kv => kv.Value.Shown is not null)
                        .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value.Shown!.ToArray());
        return res.Count > 0 ? res : null;
    }

    private static void Step(int cell, Slot slot, int hover, DateTime now)
    {
        if (hover == slot.Hover)
        {
            if (slot.Shown is not null && now - slot.ChangedAt >= Calm)
            {
                slot.Shown = null;
                slot.Shows.Clear();
                if (slot.LastShown != 0) slot.Shows.Add((slot.LastShown, slot.ChangedAt));
                Log.Write($"союзник {cell} успокоился" +
                          (hover != 0 ? $" на {DataDragon.Name(hover)}" : "") + " — подбор снова по нему");
            }
            return;
        }

        slot.Hover = hover;
        slot.ChangedAt = now;
        // Снял наведение или вернулся к тому же через пустое — не смена.
        if (hover == 0 || hover == slot.LastShown) return;
        slot.LastShown = hover;

        if (slot.Shown is { } shown)
        {
            if (shown.Contains(hover)) return;   // вернулся к показанному — среднее то же
            shown.Add(hover);
            if (shown.Count > MaxShown) shown.RemoveAt(0);
            return;
        }

        slot.Shows.Add((hover, now));
        if (slot.Shows.Count > Switches + 1) slot.Shows.RemoveAt(0);
        // Смен — на одну меньше, чем показов: Shows[1] — первая из последних
        // Switches смен, эта — последняя. Уложились в окно — мечется. В среднее
        // идут все эти показы, и тот, с которого он начал, тоже.
        if (slot.Shows.Count == Switches + 1 && now - slot.Shows[1].At <= Window)
        {
            slot.Shown = [.. slot.Shows.Select(x => x.Champ).Distinct()];
            Log.Write($"союзник {cell} мечется: {string.Join(", ", slot.Shown.Select(DataDragon.Name))}" +
                      " — подбор по среднему, центр за ним не скачет");
        }
    }
}
