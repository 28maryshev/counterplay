# -*- coding: utf-8 -*-
"""
Выгрузка данных для страниц контрпиков и драфт-инструмента сайта.

Из data.db собирает базу, матчапы, синергию и кросс-ролевые матчапы по бакету
emerald и пишет stats.json + champions.json в папку сайта. Данные вшиваются в
сборку сайта, поэтому после выгрузки сайт пересобирается (это делает
publish_site.sh).

Окно патчей и правила — те же, что у движка программы (RecommendationEngine):
два последних ГОТОВЫХ патча (удержание — freshness.pick_patches) с весами
1.0 и 0.6, а пары, сыгранные один раз, не берутся — так же их выбрасывает
публикуемая база программы (publish_data.PRUNE_MIN). Тогда драфт-инструмент
сайта считает по тем же числам, что и программа.

Файлов два, оба — игры и победы, взвешенные по свежести патча:

stats.json (--out, вшивается в сборку сайта: страницы контрпиков, llms.txt):
    base           «чемпион|роль»                    → [игры, победы]
    matchup        «чемпион|роль|враг той же роли»    → [игры, победы]
    synergy        «чемпион|роль|союзник»             → [игры, победы], обе стороны
                   пары и все роли союзника вместе

engine.json (--engine-out, читается сайтом на лету — драфт-инструмент): всё то
же плюс
    synergyByRole  «чемпион|роль|союзник|его роль»    → [игры, победы], обе стороны
    cross          «чемпион|роль|враг|его роль»       → [игры, победы] — матчап
                   против врага другой роли (бот 2 на 2, лес против линий и т. д.)
    crossGames     сколько всего игр в кросс-матчапах окна: движок включает
                   слагаемое «бот 2 на 2» только при достаточном объёме

Почему два файла. На живой базе engine.json весит больше 20 МБ, и вшивать его
в сборку значит утяжелить её вчетверо на маленьком сервере. Страницам хватает
stats.json, а драфт-инструмент читает большой файл из тома с рунами, как
сервер читает сами руны, — и обновляется без пересборки сайта.

Запуск:  py -3.12 pipeline\\export_draft.py
         py -3.12 pipeline\\export_draft.py --tier gold --out D:\\site\\data\\draft
         py -3.12 pipeline\\export_draft.py --engine-out D:\\site\\stats\\draft
"""
import argparse
import io
import json
import os
import sqlite3
import urllib.request

from freshness import pick_patches

# Как в движке программы: PATCH_WINDOW = 2, вес патча PW — 1.0 и 0.6.
PATCH_WINDOW = 2
PATCH_WEIGHTS = (1.0, 0.6)
# Пары с одной игрой публикуемая база программы не везёт (PRUNE_MIN) — не берём
# их и здесь, иначе сайт и программа считали бы по разным числам.
MIN_PAIR_GAMES = 2


def main():
    ap = argparse.ArgumentParser(description="Выгрузка данных драфта для сайта")
    ap.add_argument('--db', default='pipeline/data.db')
    ap.add_argument('--tier', default='emerald')
    ap.add_argument('--out', default='C:/Indexcounterplay/data/draft')
    ap.add_argument('--engine-out', default=None,
                    help='куда положить engine.json (не задано — не писать)')
    args = ap.parse_args()

    con = sqlite3.connect(f'file:{args.db}?mode=ro', uri=True)
    # Удержание: новейший патч не берём, пока не набрал данных (см. freshness.py).
    patches = pick_patches(con, PATCH_WINDOW)
    w_of = {p: PATCH_WEIGHTS[i] for i, p in enumerate(patches)}
    ph = ','.join('?' * len(patches))
    print('патчи:', patches, '| веса:', [w_of[p] for p in patches])

    def weighted(sql, params):
        """Суммирует игры и победы с весом патча; ключ — строка через '|'."""
        acc = {}
        for row in con.execute(sql, params):
            *keys, patch, g, w = row
            k = '|'.join(str(x) for x in keys)
            wt = w_of[patch]
            a = acc.setdefault(k, [0.0, 0.0])
            a[0] += g * wt
            a[1] += w * wt
        return acc

    def rounded(acc):
        # два знака — компактнее JSON, точности хватает
        return {k: [round(v[0], 2), round(v[1], 2)] for k, v in acc.items()}

    base = weighted(f"""SELECT champion_id, role, patch, SUM(games), SUM(wins)
                        FROM base_wr WHERE tier_bucket=? AND patch IN ({ph})
                        GROUP BY champion_id, role, patch""", [args.tier, *patches])
    print('base:', len(base))

    pair = [args.tier, *patches, MIN_PAIR_GAMES]
    mu = weighted(f"""SELECT champion_id, role, vs_champion_id, patch, SUM(games), SUM(wins)
                      FROM matchup WHERE tier_bucket=? AND patch IN ({ph}) AND games>=?
                      GROUP BY champion_id, role, vs_champion_id, patch""", pair)
    print('matchup:', len(mu))

    cross = weighted(f"""SELECT champion_id, role, vs_champion_id, vs_role, patch, SUM(games), SUM(wins)
                         FROM botlane_matchup WHERE tier_bucket=? AND patch IN ({ph}) AND games>=?
                         GROUP BY champion_id, role, vs_champion_id, vs_role, patch""", pair)
    cross_games = con.execute(
        f"SELECT COALESCE(SUM(games),0) FROM botlane_matchup WHERE tier_bucket=? AND patch IN ({ph})",
        [args.tier, *patches]).fetchone()[0]
    print('cross:', len(cross), '| игр:', cross_games)

    # Синергия симметрична, а записана одной строкой на сторону — складываем обе
    # стороны пары, как движок. Два вида: по роли союзника и по всем его ролям
    # (запасной, когда по нужной паре ролей своих игр нет).
    syn, syn_role = {}, {}

    def add(acc, k, g, w):
        a = acc.setdefault(k, [0.0, 0.0])
        a[0] += g
        a[1] += w

    for cid, role, ally, arole, patch, g, w in con.execute(
            f"""SELECT champion_id, role, ally_id, ally_role, patch, SUM(games), SUM(wins)
                FROM synergy WHERE tier_bucket=? AND patch IN ({ph}) AND games>=?
                GROUP BY champion_id, role, ally_id, ally_role, patch""", pair):
        gw, ww = g * w_of[patch], w * w_of[patch]
        add(syn, f'{cid}|{role}|{ally}', gw, ww)              # моя сторона
        add(syn, f'{ally}|{arole}|{cid}', gw, ww)             # сторона союзника
        add(syn_role, f'{cid}|{role}|{ally}|{arole}', gw, ww)
        add(syn_role, f'{ally}|{arole}|{cid}|{role}', gw, ww)
    print('synergy:', len(syn), '| по ролям:', len(syn_role))

    from datetime import date
    head = {'tier': args.tier, 'patches': patches,
            'updated': date.today().isoformat()}  # dateModified/lastmod для SEO
    pages = {'base': rounded(base), 'matchup': rounded(mu), 'synergy': rounded(syn)}
    with io.open(f'{args.out}/stats.json', 'w', encoding='utf-8') as f:
        json.dump({**head, **pages}, f, separators=(',', ':'))
    print('stats.json записан')
    if args.engine_out:
        os.makedirs(args.engine_out, exist_ok=True)
        with io.open(os.path.join(args.engine_out, 'engine.json'), 'w', encoding='utf-8') as f:
            json.dump({**head, **pages, 'synergyByRole': rounded(syn_role),
                       'cross': rounded(cross), 'crossGames': cross_games},
                      f, separators=(',', ':'))
        print('engine.json записан')
    # Данные бандлятся в сборку сайта, поэтому порядок такой: сначала пересборка,
    # потом пинг IndexNow (Bing переобойдёт страницы за минуты, а на его индекс
    # опирается поиск ChatGPT).
    print('Дальше: пересобрать сайт, затем в папке сайта — npm run indexnow')

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
