# -*- coding: utf-8 -*-
"""
Калибровка весов движка рекомендаций по реальным исходам матчей.

Использует таблицу drafts (пишется collect.py начиная с этой версии): по каждому
матчу считает те же факторы, что движок (база / прямая контра / кросс-лейн /
синергия — чистые дельты с темпером), берёт разность фич команд и обучает
логистическую регрессию «фичи → победа». Выученные коэффициенты — это и есть
оптимальные веса W_* (печатаются нормированными к W_BASE=1.0).

Запуск:  py -3.12 pipeline\\calibrate.py --db pipeline\\data.db --tier emerald

Честное предупреждение: агрегаты содержат те же игры, что и драфты (утечка),
поэтому веса слегка оптимистичны. Для порядка величин и соотношений — годится;
для строгой оценки собери драфты на новом патче и калибруйся по нему.
"""
import argparse
import json
import math
import random
import sqlite3
from collections import defaultdict

# Константы сглаживания — как в движке (RecommendationEngine.cs).
K, K_PAIR = 50.0, 20.0
BASE_CONF, MATCHUP_CONF, CONF_GAMES = 250.0, 50.0, 60.0

CROSS_ROLES = {
    'adc': ('support',), 'support': ('adc',),
    'jungle': ('top', 'mid', 'adc', 'support'),
    'top': ('jungle',), 'mid': ('jungle',),
}


def delta(g, w, k):
    return ((w + k / 2.0) / (g + k) - 0.5) * 100 if g > 0 else 0.0


def load(con, tier):
    base, matchup, syn, cross = {}, {}, defaultdict(lambda: [0, 0]), {}
    for c, r, g, w in con.execute(
            "SELECT champion_id, role, SUM(games), SUM(wins) FROM base_wr "
            "WHERE tier_bucket=? GROUP BY 1,2", (tier,)):
        base[(c, r)] = (g, w)
    for c, r, v, g, w in con.execute(
            "SELECT champion_id, role, vs_champion_id, SUM(games), SUM(wins) FROM matchup "
            "WHERE tier_bucket=? GROUP BY 1,2,3", (tier,)):
        matchup[(c, r, v)] = (g, w)
    # Синергия симметрична — суммируем обе стороны пары (как движок).
    for c, r, a, g, w in con.execute(
            "SELECT champion_id, role, ally_id, SUM(games), SUM(wins) FROM synergy "
            "WHERE tier_bucket=? GROUP BY 1,2,3", (tier,)):
        s = syn[(c, r, a)]; s[0] += g; s[1] += w
    for a, ar, c, g, w in con.execute(
            "SELECT champion_id, ally_role, ally_id, SUM(games), SUM(wins) FROM synergy "
            "WHERE tier_bucket=? GROUP BY 1,2,3", (tier,)):
        s = syn[(c, ar, a)]; s[0] += g; s[1] += w
    for c, r, v, vr, g, w in con.execute(
            "SELECT champion_id, role, vs_champion_id, vs_role, SUM(games), SUM(wins) "
            "FROM botlane_matchup WHERE tier_bucket=? GROUP BY 1,2,3,4", (tier,)):
        cross[(c, r, v, vr)] = (g, w)
    return base, matchup, syn, cross


def team_features(team, enemy, base, matchup, syn, cross):
    """Фичи команды: [base, direct, cross, synergy] — как факторы движка."""
    f_base = f_dir = f_cross = f_syn = 0.0
    for role, champ in team.items():
        bg, bw = base.get((champ, role), (0, 0))
        raw_base = delta(bg, bw, K)
        f_base += raw_base * (bg / (bg + BASE_CONF)) if bg else 0.0

        def pure(g, w):
            return (delta(g, w, K_PAIR) - raw_base) * (g / (g + MATCHUP_CONF)) if g > 0 else 0.0

        opp = enemy.get(role)
        if opp:
            g, w = matchup.get((champ, role, opp), (0, 0))
            f_dir += pure(g, w)

        cg = cw = 0
        for cr in CROSS_ROLES.get(role, ()):
            e = enemy.get(cr)
            if e:
                g, w = cross.get((champ, role, e, cr), (0, 0))
                cg += g; cw += w
        f_cross += pure(cg, cw)

        sg = sw = 0
        for arole, ally in team.items():
            if arole == role:
                continue
            g, w = syn.get((champ, role, ally), (0, 0))
            sg += g; sw += w
        if sg > 0:
            f_syn += (delta(sg, sw, K_PAIR) - raw_base) * (sg / (sg + CONF_GAMES))
    return [f_base, f_dir, f_cross, f_syn]


def logistic_fit(X, y, epochs=300, lr=0.05):
    n, d = len(X), len(X[0])
    wts = [0.0] * d
    for _ in range(epochs):
        grad = [0.0] * d
        for xi, yi in zip(X, y):
            z = sum(w * x for w, x in zip(wts, xi))
            p = 1.0 / (1.0 + math.exp(-max(-30, min(30, z))))
            e = p - yi
            for j in range(d):
                grad[j] += e * xi[j]
        for j in range(d):
            wts[j] -= lr * grad[j] / n
    return wts


def main():
    ap = argparse.ArgumentParser(description="Калибровка весов движка по драфтам")
    ap.add_argument('--db', default='pipeline/data.db')
    ap.add_argument('--tier', default='emerald')
    ap.add_argument('--min-drafts', type=int, default=2000)
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    try:
        drafts = con.execute(
            "SELECT win_team, slots FROM drafts WHERE tier_bucket=?", (args.tier,)).fetchall()
    except sqlite3.OperationalError:
        print("таблицы drafts ещё нет — прогони сбор обновлённым collect.py, драфты начнут копиться")
        return
    print(f"драфтов в базе ({args.tier}): {len(drafts)}")
    if len(drafts) < args.min_drafts:
        print(f"мало данных (<{args.min_drafts}) — собери матчи обновлённым collect.py и повтори")
        return

    print("загружаю агрегаты…")
    base, matchup, syn, cross = load(con, args.tier)

    X, y = [], []
    for win_team, slots_json in drafts:
        slots = json.loads(slots_json)
        teams = defaultdict(dict)
        for s in slots:
            teams[s['t']][s['r']] = s['c']
        if len(teams) != 2:
            continue
        (ta, ra), (tb, rb) = teams.items()
        fa = team_features(ra, rb, base, matchup, syn, cross)
        fb = team_features(rb, ra, base, matchup, syn, cross)
        X.append([a - b for a, b in zip(fa, fb)])
        y.append(1.0 if win_team == ta else 0.0)

    # Нормировка фич (иначе градиенту тяжело) + train/test 80/20.
    rnd = random.Random(42)
    idx = list(range(len(X))); rnd.shuffle(idx)
    cut = int(len(idx) * 0.8)
    scale = [max(1e-9, sum(abs(X[i][j]) for i in idx[:cut]) / cut) for j in range(4)]
    Xn = [[x / s for x, s in zip(row, scale)] for row in X]

    wts = logistic_fit([Xn[i] for i in idx[:cut]], [y[i] for i in idx[:cut]])
    # Возврат к исходному масштабу фич.
    wts = [w / s for w, s in zip(wts, scale)]

    correct = 0
    for i in idx[cut:]:
        z = sum(w * x for w, x in zip(wts, X[i]))
        correct += (z > 0) == (y[i] > 0.5)
    acc = correct / max(1, len(idx) - cut)

    names = ['W_BASE', 'W_DIRECT', 'W_CROSS', 'W_SYNERGY']
    print(f"\nточность предсказания исхода по драфту (holdout): {acc:.1%}")
    print("сырые коэффициенты:", {n: round(w, 4) for n, w in zip(names, wts)})
    if abs(wts[0]) > 1e-9:
        norm = [w / wts[0] for w in wts]
        print("нормировано к W_BASE=1.0 (это и есть веса движка):")
        for n, w in zip(names, norm):
            print(f"  {n:10s} = {w:5.2f}")
    print("\nвнимание: агрегаты включают эти же игры (утечка) — соотношения верны,"
          "\nабсолютную точность проверяй на драфтах нового патча.")


if __name__ == '__main__':
    main()
