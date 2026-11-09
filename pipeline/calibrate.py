# -*- coding: utf-8 -*-
"""
Калибровка веса контры по ролям — по исходам настоящих матчей.

Вопрос: сколько настоящих пунктов победы несёт один пункт контры, который
показывает движок (матчап минус своя база, с доверием по объёму пары), — на
каждой линии и в каждой паре ролей. Цифра контры — правда пополам с шумом
малых выборок, и доля правды у ролей разная.

Как считается. По каждому драфту патча — разность команд по факторам:
  * сила — сумма TierPower пятёрки (текущий патч, усадка на 2000 игр);
  * чистая контра по каждой паре ролей (5 линий + 10 пар разных ролей):
    то, что движок видит у кандидатов против одного и того же соперника,
    k = d + t·база соперника, где d — дельта движка, t = g/(g+PAIR_CONF).
Доверие пары PAIR_CONF = 1000 игр подобрано по исходам перебором (50, 300,
1000, 3000, 8000 — отдельно для своей линии и для пар разных ролей): у 1000
правдоподобие исходов выше на 377 единиц логарифма, чем у прежних 50.
Логистическая регрессия «разность → победа»; коэффициент × 2 / 0,04 — доля
правды (пара входит в признак за обе команды; 0,04 логита — пункт победы у
50%).

Две ловушки, из-за которых прежняя версия врала:
  1. Утечка. Агрегаты собраны из тех же матчей — пара на десяти играх
     «предсказывает» исход, в котором сама участвовала. Здесь из каждого
     агрегата вычитается сам матч (leave-one-out), поэтому выжимка должна быть
     ОДНИМ снимком: агрегаты и драфты из одних и тех же игр
     (ops/pull-calib.sh снимает их одной транзакцией).
  2. Сила внутри контры. «Матчап минус своя база» несёт ещё и силу соперника,
     и регрессия делила её между «силой» и «контрой». Признак k её вычитает.

Ещё два признака — «против стиля врага» (трифекта архетипов и инструменты
против доминанты врага, веса W_TRIFECTA и W_STYLE). Метки чемпионов живут
только в программе, поэтому признаки по драфтам считает она:
`Counterplay.exe --style-features calib-slice.db` кладёт таблицу draft_style.
Нет таблицы — стиль просто не мерится.

Дыры состава (с 09.11.2026, RecommendationEngine.Needs.cs): та же команда
программы кладёт таблицу draft_gaps — по каждому виду дыры (фронт, отход,
инициация, защита керри, контроль, тип урона, урон по танкам, волны, ранняя
сила) 1/0 у полной пятёрки, разность команд. Коэффициент / 0,04 — пунктов
победы, которые команда теряет с открытой дырой; со знаком минус — цена её
закрытия, это и есть GapWeight движка (W_NEED = W_COUNTER_PP).

Запуск:
  bash ops/pull-calib.sh                    # → calib-slice.db
  Counterplay.exe --style-features calib-slice.db   # необязательно: стиль
  python pipeline/calibrate.py --db calib-slice.db

Чистый Python, без numpy: 600 тыс. драфтов — минуты три-четыре.
Константы сглаживания повторяют движок — при правке там сверить здесь.
"""
import argparse
import json
import math
import sqlite3
import sys
from array import array

K, K_PAIR, PAIR_CONF = 50.0, 20.0, 1000.0   # PAIR_CONF — доверие пары g/(g+1000), подобрано по исходам 05.11.2026
TIER_SHRINK = 2000.0           # TierPower: усадка к 50% на 2000 игр текущего патча
PREV_WEIGHT = 0.6              # вес прошлого патча в агрегатах (PW движка)
ROLES = ['top', 'jungle', 'mid', 'adc', 'support']
PAIRS = [(a, b) for i, a in enumerate(ROLES) for b in ROLES[i:]]
ROLE_RU = {'top': 'топ', 'jungle': 'лес', 'mid': 'мид', 'adc': 'АДК', 'support': 'саппорт'}


def delta(g, w, k):
    return ((w + k / 2.0) / (g + k) - 0.5) * 100 if g > 0 else 0.0


def patches(con, tier):
    ps = [r[0] for r in con.execute(
        "SELECT DISTINCT patch FROM drafts WHERE tier_bucket=?", (tier,)) if r[0] and r[0][0].isdigit()]
    ps.sort(key=lambda s: tuple(int(x) for x in s.split('.')))
    return ps


def load(con, tier, cur, prev):
    pw = f"CASE patch WHEN '{cur}' THEN 1.0 WHEN '{prev}' THEN {PREV_WEIGHT} ELSE 0 END"
    where = "tier_bucket=? AND patch IN (?, ?)"
    base = {(c, r): (g, w) for c, r, g, w in con.execute(
        f"SELECT champion_id, role, SUM(games*{pw}), SUM(wins*{pw}) FROM base_wr WHERE {where} GROUP BY 1,2",
        (tier, cur, prev))}
    power = {(c, r): (g, w) for c, r, g, w in con.execute(
        "SELECT champion_id, role, SUM(games), SUM(wins) FROM base_wr WHERE tier_bucket=? AND patch=? GROUP BY 1,2",
        (tier, cur))}
    pair = {}
    for c, r, v, g, w in con.execute(
            f"SELECT champion_id, role, vs_champion_id, SUM(games*{pw}), SUM(wins*{pw}) FROM matchup "
            f"WHERE {where} GROUP BY 1,2,3", (tier, cur, prev)):
        pair[(c, r, v, r)] = (g, w)
    for c, r, v, vr, g, w in con.execute(
            f"SELECT champion_id, role, vs_champion_id, vs_role, SUM(games*{pw}), SUM(wins*{pw}) "
            f"FROM botlane_matchup WHERE {where} GROUP BY 1,2,3,4", (tier, cur, prev)):
        pair[(c, r, v, vr)] = (g, w)
    return base, power, pair


def features(A, B, wa, base, power, pair):
    """Разность команд A и B; wa — победила ли A. Из каждого агрегата вычтен сам матч."""
    def raw_base(c, r, won):
        g, w = base.get((c, r), (0, 0))
        return delta(g - 1, w - won, K)

    def tier_power(c, r, won):
        g, w = power.get((c, r), (0, 0))
        return ((w - won) + TIER_SHRINK / 2) / ((g - 1) + TIER_SHRINK) * 100 - 50

    def k(c, r, o, vr, won):
        g, w = pair.get((c, r, o, vr), (0, 0))
        if g <= 1:
            return 0.0
        g -= 1; w -= won
        t = g / (g + PAIR_CONF)
        return (delta(g, w, K_PAIR) - raw_base(c, r, won) + raw_base(o, vr, 1 - won)) * t

    f = [sum(tier_power(A[r], r, wa) for r in ROLES) - sum(tier_power(B[r], r, 1 - wa) for r in ROLES)]
    for a, b in PAIRS:
        x = k(A[a], a, B[b], b, wa) - k(B[b], b, A[a], a, 1 - wa)
        if a != b:
            x += k(A[b], b, B[a], a, wa) - k(B[a], a, A[b], b, 1 - wa)
        f.append(x)
    return f


def solve(M, v):
    """Гаусс с выбором ведущего: M·x = v (M маленькая, ~17×17)."""
    n = len(v)
    a = [row[:] + [v[i]] for i, row in enumerate(M)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(a[r][col]))
        a[col], a[piv] = a[piv], a[col]
        for r in range(n):
            if r != col and a[r][col]:
                f = a[r][col] / a[col][col]
                for j in range(col, n + 1):
                    a[r][j] -= f * a[col][j]
    return [a[i][n] / a[i][i] for i in range(n)]


def inverse_diag(M):
    n = len(M)
    return [solve(M, [1.0 if j == i else 0.0 for j in range(n)])[i] for i in range(n)]


def logistic(X, y, d, iters=4):
    """IRLS (Ньютон). X — плоский массив по d признаков на матч (первый — единица)."""
    n = len(y)
    b = [0.0] * d
    H = None
    for it in range(iters):
        H = [[0.0] * d for _ in range(d)]
        g = [0.0] * d
        for i in range(n):
            row = X[i * d:(i + 1) * d]
            z = sum(bj * xj for bj, xj in zip(b, row))
            p = 1.0 / (1.0 + math.exp(-max(-30.0, min(30.0, z))))
            wgt = p * (1 - p); e = y[i] - p
            for j in range(d):
                xj = row[j]
                g[j] += e * xj
                hj = H[j]; wx = wgt * xj
                for m in range(j, d):
                    hj[m] += wx * row[m]
        for j in range(d):
            for m in range(j):
                H[j][m] = H[m][j]
        step = solve(H, g)
        b = [bj + sj for bj, sj in zip(b, step)]
        print(f"  шаг {it + 1}: наибольшая поправка {max(abs(s) for s in step):.2e}", flush=True)
        if max(abs(s) for s in step) < 1e-9:
            break
    se = [math.sqrt(max(v, 0.0)) for v in inverse_diag(H)]
    return b, se


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    ap = argparse.ArgumentParser(description="Доля правды в цифре контры по ролям — по исходам матчей")
    ap.add_argument('--db', default='calib-slice.db', help='выжимка ops/pull-calib.sh')
    ap.add_argument('--tier', default='emerald')
    ap.add_argument('--patch', help='патч драфтов (по умолчанию последний)')
    ap.add_argument('--prev', help='прошлый патч в агрегатах (по умолчанию предыдущий)')
    ap.add_argument('--points', type=float, default=3.7,
                    help='очков оценки за настоящий пункт контры (W_COUNTER_PP) — его калибровка сохраняет')
    ap.add_argument('--min-drafts', type=int, default=50000)
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    try:
        ps = patches(con, args.tier)
    except sqlite3.OperationalError:
        print("в выжимке нет таблицы drafts — сними её ops/pull-calib.sh")
        return
    if not ps:
        print(f"драфтов {args.tier} нет")
        return
    cur = args.patch or ps[-1]
    prev = args.prev or next((p for p in reversed([r[0] for r in con.execute(
        "SELECT DISTINCT patch FROM base_wr WHERE tier_bucket=?", (args.tier,))]) if p != cur), cur)
    print(f"драфты {args.tier} {cur}, агрегаты {cur}×1.0 + {prev}×{PREV_WEIGHT}")

    base, power, pair = load(con, args.tier, cur, prev)
    try:
        style = {m: (w, s) for m, w, s in con.execute("SELECT match_id, want, style FROM draft_style")}
    except sqlite3.OperationalError:
        style = {}
    print("стиль врага: " + (f"признаки по {len(style)} драфтам" if style else "таблицы draft_style нет — не мерю"))
    try:
        gcols = [r[1] for r in con.execute("PRAGMA table_info(draft_gaps)")][1:]
        gaps = {r[0]: tuple(r[1:]) for r in con.execute("SELECT * FROM draft_gaps")} if gcols else {}
    except sqlite3.OperationalError:
        gcols, gaps = [], {}
    print("дыры состава: " + (f"признаки по {len(gaps)} драфтам" if gaps else "таблицы draft_gaps нет — не мерю"))
    d = 1 + 1 + len(PAIRS) + (2 if style else 0) + len(gcols)
    X = array('d'); y = array('d')
    for i, (mid, win, slots) in enumerate(con.execute(
            "SELECT match_id, win_team, slots FROM drafts WHERE tier_bucket=? AND patch=?", (args.tier, cur))):
        t = {100: {}, 200: {}}
        for s in json.loads(slots):
            t[s['t']][s['r']] = s['c']
        if len(t[100]) != 5 or len(t[200]) != 5:
            continue
        wa = 1 if win == 100 else 0
        X.append(1.0); X.extend(features(t[100], t[200], wa, base, power, pair))
        if style:
            X.extend(style.get(mid, (0.0, 0.0)))
        if gcols:
            X.extend(gaps.get(mid, (0.0,) * len(gcols)))
        y.append(wa)
        if i % 100000 == 0:
            print(f"  драфтов разобрано: {i}", flush=True)
    if len(y) < args.min_drafts:
        print(f"мало драфтов ({len(y)} < {args.min_drafts}) — оценки будут шумнее самих различий")
        return
    print(f"матчей: {len(y)}; регрессия…")
    b, se = logistic(X, y, d)

    print(f"\nсила (TierPower): {b[1] / 0.04:.2f} пп победы на пункт (теория — около 1)")
    print("\nдоля правды в пункте контры (настоящих пп на показанный пп):")
    trust = {}
    for (a, bb), bj, sj in zip(PAIRS, b[2:], se[2:]):
        trust[(a, bb)] = 2 * bj / 0.04
        name = ROLE_RU[a] if a == bb else f"{ROLE_RU[a]} ↔ {ROLE_RU[bb]}"
        print(f"  {name:18} {2 * bj / 0.04:5.2f} ± {2 * sj / 0.04:.2f}")

    if style:
        sw, ss = b[2 + len(PAIRS)], b[3 + len(PAIRS)]
        ew, es = se[2 + len(PAIRS)], se[3 + len(PAIRS)]
        print("\nстиль врага (настоящих пп на единицу признака, сумма по пятёрке):")
        print(f"  трифекта (Want)     {sw / 0.04:6.3f} ± {ew / 0.04:.3f}")
        print(f"  инструменты (Style) {ss / 0.04:6.3f} ± {es / 0.04:.3f}")

    if gcols:
        g0 = 2 + len(PAIRS) + (2 if style else 0)
        print()
        print("дыры состава (пунктов победы, которые стоит открытая дыра; минус — команда теряет):")
        for i, c in enumerate(gcols):
            n = sum(1 for v in gaps.values() if v[i] != 0)
            print(f"  {c:10} {b[g0 + i] / 0.04:6.2f} ± {se[g0 + i] / 0.04:.2f}   (драфтов с разницей {n})")

    lanes = [trust[(r, r)] for r in ROLES]
    # Сохраняется цена настоящего пункта, а не средний вес: вес — на показанный
    # пункт, а при другом доверии пары (PAIR_CONF) показанный пункт другой.
    # 3.7 выбрано 04.11.2026 (при доверии 50 это давало прежний средний вес 2.1).
    g = args.points
    print(f"\nдля RecommendationEngine.Weights (W_COUNTER_PP = {g}, средний вес линии {g * sum(lanes) / len(lanes):.2f}):")
    for r in ROLES:
        print(f"  LaneTrust {r:8} {trust[(r, r)]:.2f}  → вес {g * trust[(r, r)]:.2f}")
    print(f"  бот 2 на 2 (АДК ↔ саппорт) {trust[('adc', 'support')]:.2f}  → вес {g * trust[('adc', 'support')]:.2f}")
    print("  CrossTrust (прочие враги, на каждого):")
    for a, bb in PAIRS:
        if a != bb and (a, bb) != ('adc', 'support'):
            print(f"    {a:8} ↔ {bb:8} {trust[(a, bb)]:.2f}  → вес {g * trust[(a, bb)]:.2f}")
    if style:
        print(f"  W_TRIFECTA = {g * sw / 0.04:.2f}   W_STYLE = {g * ss / 0.04:.2f}")
    if gcols:
        print("  GapWeight (цена закрытой дыры, настоящих пп; W_NEED = W_COUNTER_PP):")
        for i, c in enumerate(gcols):
            print(f"    {c:10} {-b[g0 + i] / 0.04:.2f}")


if __name__ == '__main__':
    main()
