# -*- coding: utf-8 -*-
"""
Выгрузка агрегатов для веб-драфта (counterplays.com/draft).

Из data.db собирает base/matchup/synergy за 3 последних патча с ВЗВЕШИВАНИЕМ ПО
СВЕЖЕСТИ (1.0 / 0.7 / 0.45 — как в движке программы) и пишет stats.json +
champions.json в папку сайта. После выгрузки сайт задеплоить (данные бандлятся).

Запуск:  py -3.12 pipeline\\export_draft.py
         py -3.12 pipeline\\export_draft.py --tier gold --out D:\\site\\data\\draft
"""
import argparse
import io
import json
import sqlite3
import urllib.request

PATCH_WEIGHTS = (1.0, 0.7, 0.45)   # свежий → старый (как PW в движке)


def patch_key(p):
    return tuple(int(x) for x in p.split('.'))


def main():
    ap = argparse.ArgumentParser(description="Выгрузка данных драфта для сайта")
    ap.add_argument('--db', default='pipeline/data.db')
    ap.add_argument('--tier', default='emerald')
    ap.add_argument('--out', default='C:/Indexcounterplay/data/draft')
    args = ap.parse_args()

    con = sqlite3.connect(args.db)
    patches = sorted((r[0] for r in con.execute('SELECT DISTINCT patch FROM base_wr')),
                     key=patch_key, reverse=True)[:3]
    w_of = {p: PATCH_WEIGHTS[i] for i, p in enumerate(patches)}
    ph = ','.join('?' * len(patches))
    print('патчи:', patches, '| веса:', [w_of[p] for p in patches])

    def weighted(sql, key_cols):
        """Суммирует games/wins с весом патча; ключ — строка через '|'. """
        acc = {}
        for row in con.execute(sql, [args.tier, *patches]):
            *keys, patch, g, w = row
            k = '|'.join(str(x) for x in keys)
            wt = w_of[patch]
            a = acc.setdefault(k, [0.0, 0.0])
            a[0] += g * wt
            a[1] += w * wt
        # округляем до 2 знаков — компактнее JSON, точности хватает
        return {k: [round(v[0], 2), round(v[1], 2)] for k, v in acc.items()}

    base = weighted(f"""SELECT champion_id, role, patch, SUM(games), SUM(wins)
                        FROM base_wr WHERE tier_bucket=? AND patch IN ({ph})
                        GROUP BY champion_id, role, patch""", 2)
    print('base:', len(base))

    mu = weighted(f"""SELECT champion_id, role, vs_champion_id, patch, SUM(games), SUM(wins)
                      FROM matchup WHERE tier_bucket=? AND patch IN ({ph})
                      GROUP BY champion_id, role, vs_champion_id, patch""", 3)
    print('matchup:', len(mu))

    # Синергия: маржинализуем роль союзника + обе стороны пары (как движок).
    syn = {}

    def add_syn(k, g, w):
        a = syn.setdefault(k, [0.0, 0.0])
        a[0] += g
        a[1] += w

    for cid, role, ally, patch, g, w in con.execute(
            f"""SELECT champion_id, role, ally_id, patch, SUM(games), SUM(wins)
                FROM synergy WHERE tier_bucket=? AND patch IN ({ph})
                GROUP BY champion_id, role, ally_id, patch""", [args.tier, *patches]):
        add_syn(f'{cid}|{role}|{ally}', g * w_of[patch], w * w_of[patch])
    for cid, arole, ally, patch, g, w in con.execute(
            f"""SELECT champion_id, ally_role, ally_id, patch, SUM(games), SUM(wins)
                FROM synergy WHERE tier_bucket=? AND patch IN ({ph})
                GROUP BY champion_id, ally_role, ally_id, patch""", [args.tier, *patches]):
        # обратная сторона: (ally_id на ally_role) в паре с champion_id
        add_syn(f'{ally}|{arole}|{cid}', g * w_of[patch], w * w_of[patch])
    syn = {k: [round(v[0], 2), round(v[1], 2)] for k, v in syn.items()}
    print('synergy:', len(syn))

    from datetime import date
    with io.open(f'{args.out}/stats.json', 'w', encoding='utf-8') as f:
        json.dump({'tier': args.tier, 'patches': patches,
                   'updated': date.today().isoformat(),  # dateModified/lastmod для SEO
                   'base': base, 'matchup': mu, 'synergy': syn}, f, separators=(',', ':'))
    print('stats.json записан')

    # champions.json: id → ключ/имена/роли/классы (Data Dragon en+ru).
    ver = json.load(urllib.request.urlopen(
        'https://ddragon.leagueoflegends.com/api/versions.json'))[0]

    def champs(lang):
        d = json.load(urllib.request.urlopen(
            f'https://ddragon.leagueoflegends.com/cdn/{ver}/data/{lang}/champion.json'))['data']
        return {int(v['key']): v for v in d.values()}

    en, ru = champs('en_US'), champs('ru_RU')
    roles = {}
    for cid, role, g in con.execute(
            f"""SELECT champion_id, role, SUM(games) FROM base_wr
                WHERE tier_bucket=? AND patch IN ({ph}) GROUP BY champion_id, role""",
            [args.tier, *patches]):
        roles.setdefault(cid, {})[role] = g
    out = {}
    for cid, e in en.items():
        out[cid] = {'key': e['id'], 'en': e['name'],
                    'ru': ru.get(cid, {}).get('name', e['name']),
                    'roles': roles.get(cid, {}), 'classes': e.get('tags', [])}
    with io.open(f'{args.out}/champions.json', 'w', encoding='utf-8') as f:
        json.dump(out, f, ensure_ascii=False, separators=(',', ':'))
    print(f'champions.json записан ({len(out)} чемпионов, ddragon {ver})')


if __name__ == '__main__':
    main()
