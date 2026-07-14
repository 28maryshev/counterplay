# -*- coding: utf-8 -*-
"""
export_runes.py — готовит статику рун/билдов для сервера.

Ответ по паре «чемпион+роль» одинаков для всех пользователей и меняется раз в
патч, поэтому это не API к базе, а НАБОР ГОТОВЫХ JSON: сервер их просто отдаёт,
Cloudflare кэширует на краю сети (мгновенно в любой точке мира), базы на сервере
не нужно вовсе.

    py -3.12 pipeline\\export_runes.py                 # в pipeline\\dist\\stats
    py -3.12 pipeline\\export_runes.py --out C:\\tmp\\stats

Фичи включаются САМИ по мере накопления данных: связка чемпион+роль попадает в
выгрузку только когда набралось MIN_GAMES игр. Чего нет в манифесте — того нет и
в интерфейсе программы.
"""

import argparse
import json
import os
import sqlite3
from collections import defaultdict
from pathlib import Path

# ── Пороги: ниже них данные — шум, и мы их не публикуем ────────────────────
MIN_GAMES        = 200   # игр на чемпиона+роль, чтобы вообще показывать руны
MIN_KEYSTONE     = 30    # игр на кейстоун, чтобы он попал в варианты
MIN_PAGE         = 15    # игр на конкретную страницу рун
MIN_VS_GAMES     = 40    # игр в матчапе, чтобы показывать поправку на оппонента
MIN_BUILD        = 25    # игр на итоговый билд
MIN_ITEM         = 40    # игр на отдельный предмет
K                = 50.0  # сглаживание Лапласа (как в движке пиков)
PATCH_WINDOW     = 2     # сколько последних патчей агрегируем

ROLES = ['top', 'jungle', 'mid', 'adc', 'support']


def wr(games: int, wins: int) -> float:
    """Сглаженный винрейт в процентах: мало игр → ближе к 50%."""
    return 100.0 * (wins + K / 2) / (games + K)


def patches(con: sqlite3.Connection) -> list[str]:
    """Последние содержательные патчи (мусорные, <5% объёма, отбрасываем).
    patch — TEXT, поэтому сортируем ЧИСЛЕННО: «16.9» < «16.13»."""
    rows = [(p, g) for p, g in con.execute(
        "SELECT patch, SUM(games) FROM base_wr GROUP BY patch") if p and p[0].isdigit()]
    if not rows:
        return []
    top = max(g for _, g in rows)
    good = [p for p, g in rows if g >= top * 0.05]
    good.sort(key=lambda s: tuple(int(x) for x in s.split('.')))
    return good[-PATCH_WINDOW:]


def q(con, sql, params):
    return con.execute(sql, params).fetchall()


def build_champ_role(con, champ: int, role: str, ps: list[str]) -> dict | None:
    """JSON одной связки чемпион+роль. None — данных мало, не публикуем."""
    ph = ','.join('?' * len(ps))

    total = q(con, f"""SELECT SUM(games), SUM(wins) FROM base_wr
                       WHERE champion_id=? AND role=? AND patch IN ({ph})""",
              [champ, role, *ps])[0]
    games, wins = total[0] or 0, total[1] or 0
    if games < MIN_GAMES:
        return None

    # ── Кейстоуны: основа выбора (самая плотная выборка) ──────────────────
    ks_rows = q(con, f"""SELECT keystone, SUM(games) g, SUM(wins) w FROM keystone_wr
                         WHERE champion_id=? AND role=? AND patch IN ({ph})
                         GROUP BY keystone HAVING g >= ?""",
                [champ, role, *ps, MIN_KEYSTONE])
    if not ks_rows:
        return None

    ks_games = sum(g for _, g, _ in ks_rows)
    keystones = []
    for ks, g, w in sorted(ks_rows, key=lambda r: -r[1]):
        # Лучшая страница внутри этого кейстоуна: по сглаженному винрейту,
        # но только среди достаточно ходовых (иначе всплывёт случайная).
        pages = q(con, f"""SELECT primary_style, sub_style, perks, shards,
                                  SUM(games) g, SUM(wins) w
                           FROM rune_page
                           WHERE champion_id=? AND role=? AND keystone=? AND patch IN ({ph})
                           GROUP BY primary_style, sub_style, perks, shards
                           HAVING g >= ?""",
                  [champ, role, ks, *ps, MIN_PAGE])
        if not pages:
            continue
        best = max(pages, key=lambda r: wr(r[4], r[5]))
        prim, sub, perks, shards, pg, pw = best
        main, secondary = perks.split('|')

        keystones.append({
            'id': ks,
            'games': g,
            'wr': round(wr(g, w), 1),
            'pick': round(100.0 * g / ks_games, 1),
            'page': {
                'primary': prim,
                'sub': sub,
                'perks': [int(x) for x in main.split(',')],
                'secondary': [int(x) for x in secondary.split(',')],
                'shards': [int(x) for x in shards.split(',')],
                'games': pg,
                'wr': round(wr(pg, pw), 1),
            },
        })
    if not keystones:
        return None

    # ── Поправка на оппонента ─────────────────────────────────────────────
    # Пары матчапов тонкие (медиана ~60 игр на патч), поэтому НЕ заменяем базу,
    # а даём дельту: насколько кейстоун лучше/хуже обычного против этого врага.
    # Темпер по объёму: мало игр → дельта стремится к нулю.
    vs_raw = q(con, f"""SELECT vs_champion_id, keystone, SUM(games) g, SUM(wins) w
                        FROM keystone_matchup
                        WHERE champion_id=? AND role=? AND patch IN ({ph})
                        GROUP BY vs_champion_id, keystone""",
               [champ, role, *ps])
    base_wr_by_ks = {k['id']: wr(k['games'], round(k['games'] * k['wr'] / 100)) for k in keystones}

    by_opp = defaultdict(list)
    for opp, ks, g, w in vs_raw:
        by_opp[opp].append((ks, g, w))

    vs = {}
    for opp, rows in by_opp.items():
        opp_games = sum(g for _, g, _ in rows)
        if opp_games < MIN_VS_GAMES:
            continue
        deltas = []
        for ks, g, w in rows:
            if ks not in base_wr_by_ks or g < 5:
                continue
            temper = g / (g + K)                      # мало игр → доверия мало
            delta = (wr(g, w) - base_wr_by_ks[ks]) * temper
            deltas.append({'id': ks, 'games': g, 'delta': round(delta, 1)})
        if deltas:
            vs[str(opp)] = {'games': opp_games, 'keystones': deltas}

    # ── Предметы ──────────────────────────────────────────────────────────
    items = [
        {'id': i, 'games': g, 'wr': round(wr(g, w), 1)}
        for i, g, w in q(con, f"""SELECT item_id, SUM(games) g, SUM(wins) w FROM item_wr
                                  WHERE champion_id=? AND role=? AND patch IN ({ph})
                                  GROUP BY item_id HAVING g >= ?
                                  ORDER BY g DESC LIMIT 20""",
                         [champ, role, *ps, MIN_ITEM])
    ]
    # Сборки. Группировка идёт по ТОЧНОМУ набору предметов, а полные шестислотовые
    # сборки редки и разнообразны — самыми частыми наборами оказываются короткие
    # (люди заканчивают игру, не дособрав). Поэтому каждую сборку добиваем до 6
    # слотов самыми ходовыми предметами чемпиона, которых в ней ещё нет: в панели
    # и в игровом магазине должен быть полный список, а не три иконки.
    popular = [i['id'] for i in items]   # уже отсортированы по числу игр

    # Кандидаты: сглаженный винрейт уже учитывает объём (мало игр → ближе к 50%),
    # поэтому сортировка по нему — это и есть баланс «сила + популярность».
    cands = []
    for b, g, w in q(con, f"""SELECT items, SUM(games) g, SUM(wins) w FROM item_build
                              WHERE champion_id=? AND role=? AND patch IN ({ph})
                              GROUP BY items HAVING g >= ?
                              ORDER BY g DESC LIMIT 40""",
                     [champ, role, *ps, MIN_BUILD]):
        cands.append((set(int(x) for x in b.split(',')), g, w))
    cands.sort(key=lambda c: wr(c[1], c[2]), reverse=True)

    # ВАРИАНТЫ ДОЛЖНЫ ОТЛИЧАТЬСЯ. Наборы группируются точным составом, поэтому
    # три лучших по винрейту легко окажутся почти одинаковыми — показывать такое
    # бессмысленно.
    #
    # Порог — по симметрической разности множеств. Замена ОДНОГО предмета даёт
    # разность 2 (один ушёл, один пришёл), поэтому 2 мало: нужно 4, то есть
    # минимум две замены. Проверено на синтетике: с порогом 2 в тройку попадали
    # сборки, отличавшиеся одним предметом.
    MIN_DIFF = 4
    chosen = []
    for core, g, w in cands:
        if len(chosen) >= 3:
            break
        if all(len(core ^ prev) >= MIN_DIFF for prev, _, _ in chosen):
            chosen.append((core, g, w))

    # Если непохожих не набралось (узкий чемпион) — добираем самыми ходовыми.
    for c in cands:
        if len(chosen) >= 3:
            break
        if c[0] not in [x[0] for x in chosen]:
            chosen.append(c)

    builds = []
    for core, g, w in chosen:
        ids = sorted(core)
        for extra in popular:                              # добивка до шести слотов
            if len(ids) >= 6:
                break
            if extra not in ids:
                ids.append(extra)
        builds.append({
            'items': ids,
            'core': sorted(core),   # что реально играли вместе (у него и винрейт)
            'games': g,
            'wr': round(wr(g, w), 1),
        })

    spells = [
        {'spells': [int(x) for x in s.split(',')], 'games': g, 'wr': round(wr(g, w), 1)}
        for s, g, w in q(con, f"""SELECT spells, SUM(games) g, SUM(wins) w FROM spell_wr
                                  WHERE champion_id=? AND role=? AND patch IN ({ph})
                                  GROUP BY spells HAVING g >= ?
                                  ORDER BY g DESC LIMIT 3""",
                         [champ, role, *ps, MIN_BUILD])
    ]

    return {
        'champion': champ,
        'role': role,
        'patches': ps,
        'games': games,
        'wr': round(wr(games, wins), 1),
        'keystones': keystones,
        'vs': vs,
        'items': items,
        'builds': builds,
        'spells': spells,
    }


def main():
    ap = argparse.ArgumentParser(description='Экспорт рун/билдов в статику для сервера')
    ap.add_argument('--db',  default=str(Path(__file__).with_name('data.db')))
    ap.add_argument('--out', default=str(Path(__file__).parent / 'dist' / 'stats'))
    args = ap.parse_args()

    con = sqlite3.connect(f'file:{args.db}?mode=ro', uri=True)
    ps = patches(con)
    if not ps:
        raise SystemExit('в базе нет патчей')
    patch = ps[-1]
    print(f'патчи в выгрузке: {", ".join(ps)}')

    out = Path(args.out) / 'v1' / patch
    out.mkdir(parents=True, exist_ok=True)

    champs = [r[0] for r in con.execute(
        'SELECT DISTINCT champion_id FROM base_wr ORDER BY champion_id')]

    manifest_entries = []
    written = 0
    for champ in champs:
        for role in ROLES:
            data = build_champ_role(con, champ, role, ps)
            if not data:
                continue
            (out / f'{champ}-{role}.json').write_text(
                json.dumps(data, separators=(',', ':')), encoding='utf-8')
            manifest_entries.append(f'{champ}-{role}')
            written += 1

    manifest = {
        'version': 1,
        'patch': patch,
        'patches': ps,
        'available': manifest_entries,   # чего тут нет — того нет и в интерфейсе
        'thresholds': {'minGames': MIN_GAMES, 'minVsGames': MIN_VS_GAMES},
    }
    (Path(args.out) / 'v1' / 'manifest.json').write_text(
        json.dumps(manifest, separators=(',', ':')), encoding='utf-8')

    size = sum(f.stat().st_size for f in out.glob('*.json')) / 1e6
    print(f'выгружено связок чемпион+роль: {written} ({size:.1f} МБ)')
    print(f'манифест: {Path(args.out) / "v1" / "manifest.json"}')
    if written == 0:
        print('\n[!] Пусто: данных о рунах ещё нет. Запусти сбор (collect.py) —')
        print('    фичи включатся сами, как только наберётся выборка.')


if __name__ == '__main__':
    main()
