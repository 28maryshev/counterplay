# -*- coding: utf-8 -*-
"""
export_tiers.py — данные тир-листа сайта по ВСЕМ эло-бакетам (Silver..Master).

Сайт-тир-лист должен ПОБИТОВО совпадать с тир-листом программы, поэтому здесь
выгружаем ровно те входные данные, из которых движок (RecommendationEngine.cs,
метод TierList) считает meta-score:
  meta = ( Wilson_LB*100 - 50 ) + W_PICK*пикрейт% + W_BAN*банрейт%
Грейд S..D — по рангу внутри роли. Формулу и константы держим синхронно с C#.

На бакет отдаём:
  matches            — число матчей (SUM(games*вес)/10) — знаменатель бан-рейта;
  roles[role]        — [[champ_id, games, wins], ...] взвешенные, уже отфильтрованы
                       порогом TIER_MIN_GAMES и долей роли (как HAVING в движке);
  bans[champ_id]     — взвешенные баны чемпиона (числитель бан-рейта).
Винрейт, пикрейт, Уилсон и грейды считает уже сайт — из этих сырых чисел.

    py -3.12 pipeline\\export_tiers.py --db pipeline\\data.db
"""
import argparse
import io
import json
import sqlite3
from datetime import date

from freshness import pick_patches

PATCH_WEIGHTS = (1.0, 0.7, 0.45)          # свежий → старый (PW в движке)
BUCKETS = ['silver', 'gold', 'emerald', 'master']
ROLES = ['top', 'jungle', 'mid', 'adc', 'support']
TIER_MIN_GAMES = 250                       # как TIER_MIN_GAMES в движке (взвеш. игры)
MIN_ROLE_SHARE = 0.005                     # как MIN_ROLE_SHARE в движке


def patch_key(p):
    return tuple(int(x) for x in p.split('.'))


def main():
    ap = argparse.ArgumentParser(description='Многобакетные данные тир-листа для сайта')
    ap.add_argument('--db', default='pipeline/data.db')
    ap.add_argument('--out', default='C:/Indexcounterplay/data/draft')
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    # Удержание: новейший патч не берём, пока не набрал данных (см. freshness.py).
    patches = pick_patches(con, 3)
    w_of = {p: PATCH_WEIGHTS[i] for i, p in enumerate(patches)}
    ph = ','.join('?' * len(patches))
    print('патчи:', patches, '| веса:', [w_of[p] for p in patches])

    def has_bans():
        return con.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name='champion_bans'"
        ).fetchone() is not None
    bans_available = has_bans()

    out_buckets = {}
    for b in BUCKETS:
        # Взвешенные games/wins по (роль, чемпион) + общий объём (для бан-рейта).
        acc = {}            # (role, cid) → [g, w]
        total_games = 0.0   # все роли/чемпионы — знаменатель матчей
        for cid, role, patch, g, w in con.execute(
                f"""SELECT champion_id, role, patch, SUM(games), SUM(wins) FROM base_wr
                    WHERE tier_bucket=? AND patch IN ({ph})
                    GROUP BY champion_id, role, patch""", [b, *patches]):
            wt = w_of[patch]
            total_games += g * wt
            if role not in ROLES:
                continue
            a = acc.setdefault((role, cid), [0.0, 0.0])
            a[0] += g * wt
            a[1] += w * wt
        matches = total_games / 10.0        # по 10 участников на матч

        # Баны по чемпиону (взвешенно).
        bans = {}
        if bans_available:
            for cid, patch, cnt in con.execute(
                    f"""SELECT champion_id, patch, SUM(bans) FROM champion_bans
                        WHERE tier_bucket=? AND patch IN ({ph})
                        GROUP BY champion_id, patch""", [b, *patches]):
                bans[cid] = bans.get(cid, 0.0) + cnt * w_of[patch]

        # Фильтр как HAVING в движке: g >= TIER_MIN_GAMES и g >= доля*полный_объём_роли.
        role_total_all = {r: 0.0 for r in ROLES}
        for (role, _cid), (g, _w) in acc.items():
            role_total_all[role] += g

        roles_out = {}
        used_ids = set()
        for r in ROLES:
            floor = max(TIER_MIN_GAMES, MIN_ROLE_SHARE * role_total_all[r])
            lst = [[cid, round(g, 1), round(w, 1)]
                   for (role, cid), (g, w) in acc.items()
                   if role == r and g >= floor]
            roles_out[r] = lst
            used_ids.update(cid for cid, _g, _w in lst)

        out_buckets[b] = {
            'matches': round(matches, 1),
            'roles': roles_out,
            'bans': {str(cid): round(bans.get(cid, 0.0), 1)
                     for cid in used_ids if bans.get(cid, 0.0) > 0},
        }

    with io.open(f'{args.out}/tiers.json', 'w', encoding='utf-8') as f:
        json.dump({'patch': patches[0], 'patches': patches,
                   'updated': date.today().isoformat(), 'buckets': out_buckets},
                  f, separators=(',', ':'))
    print('tiers.json:', {b: sum(len(v) for v in out_buckets[b]['roles'].values())
                          for b in BUCKETS}, '| bans:', bans_available)


if __name__ == '__main__':
    main()
