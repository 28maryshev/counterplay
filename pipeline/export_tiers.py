# -*- coding: utf-8 -*-
"""
export_tiers.py — данные тир-листа сайта: эло-бакеты, отдельные тиры, серверы.

Сайт-тир-лист должен ПОБИТОВО совпадать с тир-листом программы, поэтому здесь
выгружаем ровно те входные данные, из которых движок (RecommendationEngine.cs,
метод TierList) считает силу:
  сила = (победы + 1000) / (игры + 2000)   — винрейт, усаженный к 50%
Грейд OP/S..C — по самой силе. Формулу и константы держим синхронно с C#.

Ячейка (один срез: бакет или тир, все серверы или один):
  matches            — число матчей (SUM(games*вес)/10) — знаменатель бан-рейта;
  roles[role]        — [[champ_id, games, wins], ...] взвешенные, уже отфильтрованы
                       порогом TIER_MIN_GAMES и долей роли (как в движке);
  totals[role]       — все игры роли ДО порога — знаменатель пикрейта;
  bans[champ_id]     — взвешенные баны чемпиона (числитель бан-рейта).
Винрейт, пикрейт, силу и грейды считает уже сайт — из этих сырых чисел.

Два вида файлов:
  tiers.json (в сборку сайта) — все серверы вместе:
    buckets[b] — base_wr + champion_bans, то же, что видит программа;
    tiers[t]   — отдельный тир из region_wr (тир пишется с 28.10).
  <regions-out>/<сервер>.json (в том статистики; сайт берёт при выборе сервера):
    buckets[b] — region_wr сервера по бакету, вместе с матчами до 28.10;
    tiers[t]   — region_wr сервера по тиру.
Ячейки тиров и серверов — только от MIN_CELL_MATCHES матчей: меньше — сайт
показывает ближайшую (бакет того же сервера, тот же тир всех серверов, бакет
всех) и пишет, что показано вместо выбранного.

    py -3.12 pipeline\\export_tiers.py --db pipeline\\data.db
"""
import argparse
import io
import json
import os
import sqlite3
from datetime import date

from freshness import pick_patches

PATCH_WEIGHTS = (1.0, 0.7, 0.45)          # свежий → старый (PW в движке)
BUCKETS = ['silver', 'gold', 'emerald', 'master']
ROLES = ['top', 'jungle', 'mid', 'adc', 'support']
TIER_MIN_GAMES = 250                       # как TIER_MIN_GAMES в движке
MIN_ROLE_SHARE = 0.005                     # мин. пик-рейт роли 0,5% (как TIER_MIN_SHARE)

# Тир → бакет (как TIER_BUCKETS в collect.py; сюда collect не импортировать:
# скрипт работает на хосте без riotwatcher).
BUCKET_OF = {
    'iron': 'silver', 'bronze': 'silver', 'silver': 'silver',
    'gold': 'gold', 'platinum': 'gold',
    'emerald': 'emerald', 'diamond': 'emerald',
    'master': 'master', 'grandmaster': 'master', 'challenger': 'master',
}
# Меньше — ячейку не выгружаем: при 10 000 матчей порог в 250 игр проходят
# чемпионы от 2,5% роли, это 15–20 на роль; ниже тир-лист пустеет на глазах.
MIN_CELL_MATCHES = 10000
# Банов по серверу до 28.10 нет. Пока новых матчей сервера в бакете меньше
# этого, бан-рейт ячейки берём общий по бакету.
MIN_BAN_MATCHES = 2000


def num(x):
    """Число для JSON: целые — без «.0» (так выгрузка короче)."""
    r = round(x, 1)
    return int(r) if r == int(r) else r


def make_cell(acc, total_games, bans):
    """acc: (роль, чемпион) → [игры, победы]; total_games — все игры всех ролей
    (знаменатель матчей); bans: чемпион → баны на тех же матчах."""
    # Фильтр как в движке: g >= TIER_MIN_GAMES и g >= доля*полный_объём_роли.
    role_total_all = {r: 0.0 for r in ROLES}
    for (role, _cid), (g, _w) in acc.items():
        role_total_all[role] += g

    roles_out = {}
    used_ids = set()
    for r in ROLES:
        floor = max(TIER_MIN_GAMES, MIN_ROLE_SHARE * role_total_all[r])
        lst = [[cid, num(g), num(w)]
               for (role, cid), (g, w) in acc.items()
               if role == r and g >= floor]
        roles_out[r] = lst
        used_ids.update(cid for cid, _g, _w in lst)

    return {
        'matches': num(total_games / 10.0),     # по 10 участников на матч
        'roles': roles_out,
        'totals': {r: num(role_total_all[r]) for r in ROLES},
        'bans': {str(cid): num(bans.get(cid, 0.0))
                 for cid in used_ids if bans.get(cid, 0.0) > 0},
    }


def main():
    ap = argparse.ArgumentParser(description='Данные тир-листа для сайта')
    ap.add_argument('--db', default='pipeline/data.db')
    ap.add_argument('--out', default='C:/Indexcounterplay/data/draft')
    ap.add_argument('--regions-out', default=None,
                    help='папка для <сервер>.json (на сервере — том статистики сайта)')
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    # Тир-лист считаем по ТЕКУЩЕМУ (готовому) патчу, без смеси 3 патчей — чтобы WR
    # совпадал с другими сайтами. Удержание: если новейший патч ещё сырой, берём
    # предыдущий полный (см. freshness.pick_patches).
    patches = pick_patches(con, 1)
    w_of = {p: PATCH_WEIGHTS[i] for i, p in enumerate(patches)}
    ph = ','.join('?' * len(patches))
    print('патчи:', patches, '| веса:', [w_of[p] for p in patches])

    def table_exists(name):
        return con.execute("SELECT name FROM sqlite_master WHERE type='table' AND name=?",
                           (name,)).fetchone() is not None

    def gather(table, where, params):
        """Взвешенные (роль, чемпион) → [игры, победы] и все игры всех ролей."""
        acc, total = {}, 0.0
        for cid, role, patch, g, w in con.execute(
                f"""SELECT champion_id, role, patch, SUM(games), SUM(wins) FROM {table}
                    WHERE {where} AND patch IN ({ph})
                    GROUP BY champion_id, role, patch""", [*params, *patches]):
            wt = w_of[patch]
            total += g * wt
            if role not in ROLES:
                continue
            a = acc.setdefault((role, cid), [0.0, 0.0])
            a[0] += g * wt
            a[1] += w * wt
        return acc, total

    def gather_bans(table, where, params):
        bans = {}
        for cid, patch, cnt in con.execute(
                f"""SELECT champion_id, patch, SUM(bans) FROM {table}
                    WHERE {where} AND patch IN ({ph})
                    GROUP BY champion_id, patch""", [*params, *patches]):
            bans[cid] = bans.get(cid, 0.0) + cnt * w_of[patch]
        return bans

    # Бакеты всех серверов — как в программе.
    bans_available = table_exists('champion_bans')
    out_buckets, bucket_ban_rate = {}, {}
    for b in BUCKETS:
        acc, total = gather('base_wr', 'tier_bucket=?', [b])
        bans = gather_bans('champion_bans', 'tier_bucket=?', [b]) if bans_available else {}
        out_buckets[b] = make_cell(acc, total, bans)
        m = total / 10.0
        bucket_ban_rate[b] = {cid: n / m for cid, n in bans.items()} if m else {}

    def tier_cell(where, params):
        """Ячейка из region_wr/region_bans — только тир, всё за новыми матчами."""
        acc, total = gather('region_wr', where, params)
        if total / 10.0 < MIN_CELL_MATCHES:
            return None
        return make_cell(acc, total, gather_bans('region_bans', where, params))

    def region_bucket_cell(region, b):
        """Сервер × бакет: игры — все (с перенесёнными из drafts), баны — только с
        28.10, поэтому бан-рейт по новым матчам сервера, а пока их мало — общий."""
        acc, total = gather('region_wr', 'region=? AND tier_bucket=?', [region, b])
        matches = total / 10.0
        if matches < MIN_CELL_MATCHES:
            return None
        _, fresh = gather('region_wr', "region=? AND tier_bucket=? AND tier<>''", [region, b])
        if fresh / 10.0 >= MIN_BAN_MATCHES:
            raw = gather_bans('region_bans', 'region=? AND tier_bucket=?', [region, b])
            rate = {cid: n / (fresh / 10.0) for cid, n in raw.items()}
        else:
            rate = bucket_ban_rate[b]
        return make_cell(acc, total, {cid: r * matches for cid, r in rate.items()})

    have_regions = table_exists('region_wr')
    out_tiers = {}
    if have_regions:
        for t in BUCKET_OF:
            c = tier_cell('tier=?', [t])
            if c:
                out_tiers[t] = c

    updated = date.today().isoformat()
    with io.open(f'{args.out}/tiers.json', 'w', encoding='utf-8') as f:
        json.dump({'patch': patches[0], 'patches': patches, 'updated': updated,
                   'buckets': out_buckets, 'tiers': out_tiers},
                  f, separators=(',', ':'))
    print('tiers.json:', {b: sum(len(v) for v in out_buckets[b]['roles'].values())
                          for b in BUCKETS}, '| тиры:', list(out_tiers), '| bans:', bans_available)

    if not (args.regions_out and have_regions):
        return
    os.makedirs(args.regions_out, exist_ok=True)
    regions = [r for (r,) in con.execute(
        f'SELECT DISTINCT region FROM region_wr WHERE patch IN ({ph}) ORDER BY region', patches)]
    for region in regions:
        rb = {}
        for b in BUCKETS:
            c = region_bucket_cell(region, b)
            if c:
                rb[b] = c
        rt = {}
        for t in BUCKET_OF:
            c = tier_cell('region=? AND tier=?', [region, t])
            if c:
                rt[t] = c
        with io.open(os.path.join(args.regions_out, f'{region}.json'), 'w', encoding='utf-8') as f:
            json.dump({'patch': patches[0], 'updated': updated, 'region': region,
                       'buckets': rb, 'tiers': rt}, f, separators=(',', ':'))
        print(f'  {region}: бакеты {list(rb)} | тиры {list(rt)}')


if __name__ == '__main__':
    main()
