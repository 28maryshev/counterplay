namespace Counterplay;

/// <summary>
/// Союзник, который быстро перебирает чемпионов наведением. Каждое его
/// наведение пересчитывало подбор, и центр окна скакал вслед за ним: игрок не
/// успевал прочитать список, как тот менялся (просьба владельца, 14.11.2026).
///
/// Сменил наведение <see cref="Switches"/> раза, и между сменами каждый раз
/// меньше <see cref="FastGap"/>, — «мечется»: подбор держится на том, кто
/// стоял у него в слоте до этой смены (центр уже был посчитан под него и не
/// дёргается ещё раз), и его наведения больше не трогает. Не меняет наведение
/// <see cref="Calm"/> — подбор отпускается и считается под его последний
/// показанный пик. Лок отпускает сразу.
///
/// Пороги — под тайминги драфта: на пик в ранкеде около 30 секунд на ход, на
/// объявление — 15. Три смены по три секунды — треть хода на перебор; пять
/// секунд покоя — шестая часть хода: успеваем показать подбор под его выбор,
/// пока он ещё не залочил.
///
/// Только союзники: своё наведение игрок делает сам, а врагов клиент до лока
/// не показывает. Состояние — на один драфт (<see cref="Reset"/>).
/// </summary>
public sealed class AllyWaver
{
    /// Столько смен наведения подряд…
    public const int Switches = 3;
    /// …и каждая меньше чем через столько после предыдущей — «мечется». Три
    /// секунды на чемпиона — глянуть и передумать, а не выбрать (решение
    /// владельца; сначала была секунда — ловила только щелчки по сетке).
    public static readonly TimeSpan FastGap = TimeSpan.FromSeconds(3);
    /// Столько не меняет наведение — успокоился: подбор под его последнего.
    public static readonly TimeSpan Calm = TimeSpan.FromSeconds(5);
    /// Почти метания — три смены, каждая меньше чем через столько: пишем в
    /// журнал программы промежутки, чтобы порог было по чему подкрутить.
    private static readonly TimeSpan NearGap = TimeSpan.FromSeconds(5);

    private sealed class Slot
    {
        public int Hover;              // наведение сейчас (0 — никого)
        public int LastShown;          // последний показанный (не 0): смена — это «другой, чем он»
        public DateTime ChangedAt;     // когда наведение менялось в последний раз
        public readonly List<DateTime> SwitchTimes = [];   // последние смены
        public int? Held;              // мечется: на ком держим подбор; null — не держим
        public bool NearLogged;        // «почти метания» уже записаны в этом драфте
    }

    private readonly Dictionary<int, Slot> _slots = [];

    /// Новый драфт — с чистого листа.
    public void Reset() => _slots.Clear();

    /// <summary>
    /// Союзник залочил: чемпион в слоте есть, и его ход пика кончился. В свой
    /// ход наведённый стоит у клиента в <c>championId</c> ещё до лока — по
    /// одному <see cref="DraftPlayer.ChampionId"/> метания в его собственный ход
    /// сошли бы за лок, а перебирают чаще всего как раз тогда. Одно правило на
    /// слежку и на подбор.
    /// </summary>
    public static bool Locked(DraftState s, DraftPlayer p) =>
        p.ChampionId != 0 && !s.ActiveCells.Contains(p.CellId);

    /// <summary>
    /// Когда кто-то из тех, на ком держим подбор, успокоится, если больше не
    /// тронет наведение (UTC); MaxValue — никого не держим. Цикл драфта ставит
    /// на это время будильник сокета (<see cref="LcuEventSocket.WakeAt"/>):
    /// покой — это отсутствие событий, и сам клиент о нём не скажет.
    /// </summary>
    public DateTime NextCalm =>
        _slots.Values.Where(s => s.Held is not null)
              .Select(s => s.ChangedAt + Calm).DefaultIfEmpty(DateTime.MaxValue).Min();

    /// <summary>
    /// Учесть снимок драфта и сказать, на ком из союзников держим подбор:
    /// ячейка → чемпион, под которого он посчитан. null — ни на ком. Звать на
    /// каждом снимке, в том числе по будильнику: «успокоился» наступает без
    /// всякого события.
    /// </summary>
    public IReadOnlyDictionary<int, int>? Observe(DraftState s, DateTime now)
    {
        foreach (var p in s.MyTeam)
        {
            if (p.IsLocalPlayer) continue;
            if (Locked(s, p))
            {
                // Залочил — выбор сделан, считается он сам.
                if (_slots.Remove(p.CellId, out var done) && done.Held is not null)
                    Log.Write($"союзник {p.CellId} залочил {DataDragon.Name(p.ChampionId)} — подбор снова по нему");
                continue;
            }
            if (!_slots.TryGetValue(p.CellId, out var slot))
                _slots[p.CellId] = slot = new Slot { ChangedAt = now };
            Step(p.CellId, slot, p.EffectiveChampionId, now);
        }

        var res = _slots.Where(kv => kv.Value.Held is not null)
                        .ToDictionary(kv => kv.Key, kv => kv.Value.Held!.Value);
        return res.Count > 0 ? res : null;
    }

    private static void Step(int cell, Slot slot, int hover, DateTime now)
    {
        if (hover == slot.Hover)
        {
            if (slot.Held is not null && now - slot.ChangedAt >= Calm)
            {
                slot.Held = null;
                slot.SwitchTimes.Clear();
                Log.Write($"союзник {cell} успокоился" +
                          (hover != 0 ? $" на {DataDragon.Name(hover)}" : "") + " — подбор снова по нему");
            }
            return;
        }

        var before = slot.Hover;   // под него посчитан центр на прошлом снимке
        slot.Hover = hover;
        slot.ChangedAt = now;      // любое движение, пока держим, отодвигает покой
        // Снял наведение или вернулся к тому же через пустое — не смена.
        if (hover == 0 || hover == slot.LastShown) return;
        var first = slot.LastShown == 0;
        slot.LastShown = hover;
        if (first || slot.Held is not null) return;   // первый показ — не смена

        var times = slot.SwitchTimes;
        times.Add(now);
        if (times.Count > Switches) times.RemoveAt(0);
        if (times.Count < Switches) return;

        var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToList();
        string Gaps() => string.Join(" и ", gaps.Select(g => $"{g.TotalSeconds:0.0} с"));
        if (gaps.All(g => g < FastGap))
        {
            slot.Held = before;
            Log.Write($"союзник {cell} быстро перебирает (смены через {Gaps()}) — подбор держим" +
                      (before != 0 ? $" на {DataDragon.Name(before)}" : " без него") +
                      $", ждём {Calm.TotalSeconds:0} с покоя");
        }
        else if (!slot.NearLogged && gaps.All(g => g < NearGap))
        {
            slot.NearLogged = true;
            Log.Write($"союзник {cell}: три смены через {Gaps()} — не метания (порог {FastGap.TotalSeconds:0} с)");
        }
    }
}
