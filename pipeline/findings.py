# -*- coding: utf-8 -*-
"""
findings.py — что в данных изменилось настолько, что об этом стоит говорить.

Ищет по локальной выжимке (см. `ops/pull-stats.sh`) сдвиги между двумя патчами и
перекосы внутри одного: предмет стали брать иначе, руна ушла в другую, популярный
выбор проигрывает менее популярному. Каждая находка выходит с числами и объёмом
выборки — материал для поста, а не готовый пост.

Запуск:
    bash ops/pull-stats.sh
    python pipeline/findings.py --db data-slice.db
    python pipeline/findings.py --db data-slice.db --json > findings.json

── Три правила, без которых находки будут красивыми и неверными ──────────────

1. ВИНРЕЙТ СБОРКИ МЕРИТ ДЛИНУ ИГРЫ. У Бель'Вет набор из одного предмета даёт 25%
   побед, из шести — 71%: проигрывающий сдаётся, не дособрав. Поэтому сборка
   сравнивается только с играми ТОЙ ЖЕ длины, и никогда — с общим винрейтом.
   Отдельный предмет — с тем уровнем, на котором он обычно встречается.

2. ДОЛЮ СЧИТАЕМ ОТ РАЗОБРАННЫХ ИГР, а не от всех. Предметы и руны разбирались не
   с самого начала: у патча 16.16 покрытие 16%, у 16.17 — 43%, у 16.18 — 100%.
   Делить на все матчи значит выдавать рост покрытия за рост популярности —
   именно так «предметы» однажды прыгнули с 40% до 93% у десятка не связанных
   между собой чемпионов. Знаменатель даёт кейстоун: он ровно один на игру.

3. ПОРОГ ЗНАЧИМОСТИ, И НЕ ОДИН. Мало проверить z-оценку: при десятках тысяч игр
   значимым становится сдвиг в полпроцента, который никому не интересен. Поэтому
   требуем и статистическую уверенность, и заметный размер эффекта. При пороге в
   200 игр вылезали выдуманные «+15 пунктов винрейта»; при 3000 разницы садятся
   на честные ±1–3.

Чего скрипт НЕ делает и делать не должен: не пишет причинность. «Стали собирать —
и вот что стало с результатом», а не «предмет поднял винрейт». Из наблюдений
причина не достаётся, и в посте её быть не может.
"""

import argparse
import json
import os
import sqlite3
import sys
import urllib.request
from collections import defaultdict
from pathlib import Path

# Консоль Windows по умолчанию не умеет ни стрелок, ни русских кавычек: вывод
# падал на первой же строке. Просим UTF-8 явно.
for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

# ── Пороги ──────────────────────────────────────────────────────────────────
K = 50.0            # сглаживание Лапласа: мало игр → ближе к 50%
MIN_Z = 3.0         # статистическая уверенность (примерно 3 сигмы)
MIN_GAMES = 2000    # игр на связку, иначе разговаривать не о чем
MIN_SHARE_MOVE = 5.0    # пунктов доли, иначе сдвиг незаметен игроку
MIN_WR_MOVE = 2.0       # пунктов винрейта
MIN_BUILD_ITEMS = 4     # набор короче — обрывок игры, а не сборка
MIN_BUILD_SHARE = 1.0   # % игр чемпиона
COHERENT_MIN = 4        # у скольких чемпионов сдвиг должен повториться

CACHE = Path(__file__).with_name('.ddragon-names.json')


def wr(games: int, wins: int) -> float:
    return 100.0 * (wins + K / 2) / (games + K)


def num(n: int) -> str:
    """Число с пробелами между тысячами.

    Раньше разделитель ставился заменой запятых во всей готовой фразе — и
    вместе с ними пропадали запятые самого предложения.
    """
    return f'{n:,}'.replace(',', ' ')


def z_prop(n1: int, k1: int, n2: int, k2: int) -> float:
    """Насколько уверенно две доли различаются, в сигмах.

    Обычная проверка разности долей. Ноль игр с любой стороны — уверенности нет.
    """
    if n1 <= 0 or n2 <= 0:
        return 0.0
    p1, p2 = k1 / n1, k2 / n2
    var = p1 * (1 - p1) / n1 + p2 * (1 - p2) / n2
    if var <= 0:
        return 0.0
    return (p2 - p1) / (var ** 0.5)


# ── Имена ───────────────────────────────────────────────────────────────────

def load_names() -> dict:
    """Имена чемпионов, предметов и рун. Кладём рядом со скриптом: сеть нужна
    один раз, а без имён находки нечитаемы («предмет 6610» никому ничего не
    говорит)."""
    if CACHE.exists():
        try:
            return json.loads(CACHE.read_text(encoding='utf-8'))
        except Exception:
            pass

    def get(url):
        with urllib.request.urlopen(url, timeout=20) as r:
            return json.load(r)

    names = {'champions': {}, 'items': {}, 'runes': {}}
    try:
        ver = get('https://ddragon.leagueoflegends.com/api/versions.json')[0]
        base = f'https://ddragon.leagueoflegends.com/cdn/{ver}/data/ru_RU'
        for c in get(f'{base}/champion.json')['data'].values():
            names['champions'][c['key']] = c['name']
        for k, v in get(f'{base}/item.json')['data'].items():
            names['items'][k] = v['name']
        for tree in get(f'{base}/runesReforged.json'):
            for slot in tree['slots']:
                for r in slot['runes']:
                    names['runes'][str(r['id'])] = r['name']
        CACHE.write_text(json.dumps(names, ensure_ascii=False), encoding='utf-8')
    except Exception as e:
        print(f'имена не загрузились ({e}) — покажу идентификаторы', flush=True)
    return names


ROLE_RU = {'top': 'топ', 'jungle': 'лес', 'mid': 'мид', 'adc': 'адк', 'support': 'саппорт'}


class Names:
    def __init__(self, d):
        self.d = d

    def champ(self, cid):
        return self.d['champions'].get(str(cid), f'#{cid}')

    def item(self, iid):
        return self.d['items'].get(str(iid), f'#{iid}')

    def rune(self, rid):
        return self.d['runes'].get(str(rid), f'#{rid}')

    def who(self, cid, role):
        return f'{self.champ(cid)} ({ROLE_RU.get(role, role)})'


# ── Данные ──────────────────────────────────────────────────────────────────

class Data:
    """Всё, что нужно искалке, одним куском в памяти.

    На локальной машине это десятки мегабайт и секунды — ровно поэтому расчёты
    и переехали сюда с коллектора.
    """

    def __init__(self, db: sqlite3.Connection, bucket: str, patches: tuple[str, str]):
        self.a, self.b = patches
        where = 'tier_bucket=?' if bucket != 'all' else '1=1'
        args = (bucket,) if bucket != 'all' else ()

        # Разобранные игры: кейстоун ровно один на игру, это и есть знаменатель.
        self.parsed = defaultdict(int)
        for cid, role, patch, g in db.execute(
                f'SELECT champion_id, role, patch, SUM(games) FROM keystone_wr '
                f'WHERE {where} GROUP BY 1,2,3', args):
            self.parsed[(cid, role, patch)] += g

        self.base = {}
        for cid, role, patch, g, w in db.execute(
                f'SELECT champion_id, role, patch, SUM(games), SUM(wins) FROM base_wr '
                f'WHERE {where} GROUP BY 1,2,3', args):
            self.base[(cid, role, patch)] = (g, w)

        self.items = defaultdict(lambda: [0, 0])
        for cid, role, item, patch, g, w in db.execute(
                f'SELECT champion_id, role, item_id, patch, SUM(games), SUM(wins) '
                f'FROM item_wr WHERE {where} GROUP BY 1,2,3,4', args):
            cell = self.items[(cid, role, item, patch)]
            cell[0] += g
            cell[1] += w

        self.keystones = defaultdict(lambda: [0, 0])
        for cid, role, ks, patch, g, w in db.execute(
                f'SELECT champion_id, role, keystone, patch, SUM(games), SUM(wins) '
                f'FROM keystone_wr WHERE {where} GROUP BY 1,2,3,4', args):
            cell = self.keystones[(cid, role, ks, patch)]
            cell[0] += g
            cell[1] += w

        # Сборки: нужны и сами наборы, и «обычный» винрейт для каждой длины.
        self.builds = defaultdict(lambda: [0, 0])
        self.strata = defaultdict(lambda: [0, 0])
        self.builds_total = defaultdict(int)
        for cid, role, its, patch, g, w in db.execute(
                f'SELECT champion_id, role, items, patch, SUM(games), SUM(wins) '
                f'FROM item_build WHERE {where} GROUP BY 1,2,3,4', args):
            ids = tuple(int(x) for x in its.split(',') if x)
            if not ids:
                continue
            cell = self.builds[(cid, role, ids, patch)]
            cell[0] += g
            cell[1] += w
            st = self.strata[(cid, role, len(ids), patch)]
            st[0] += g
            st[1] += w
            self.builds_total[(cid, role, patch)] += g

    def pairs(self):
        """Все связки чемпион+роль, живые в обоих патчах."""
        out = set()
        for (cid, role, patch) in self.parsed:
            if patch == self.b and (cid, role, self.a) in self.parsed:
                out.add((cid, role))
        return sorted(out)


# ── Находки ─────────────────────────────────────────────────────────────────

def share_shifts(d: Data, table: str, nm: Names, kind: str) -> list[dict]:
    """Что стали брать заметно чаще или реже — предметы или руны."""
    src = d.items if table == 'items' else d.keystones
    out = []
    for key, cell in src.items():
        cid, role, thing, patch = key
        if patch != d.b:
            continue
        prev = src.get((cid, role, thing, d.a))
        if not prev:
            continue
        gb, wb = cell
        ga, wa = prev
        if ga < MIN_GAMES or gb < MIN_GAMES:
            continue
        pa, pb = d.parsed.get((cid, role, d.a), 0), d.parsed.get((cid, role, d.b), 0)
        if not pa or not pb:
            continue
        sa, sb = 100.0 * ga / pa, 100.0 * gb / pb
        if abs(sb - sa) < MIN_SHARE_MOVE:
            continue
        z = z_prop(pa, ga, pb, gb)
        if abs(z) < MIN_Z:
            continue
        name = nm.item(thing) if table == 'items' else nm.rune(thing)
        out.append({
            'kind': kind, 'champion': cid, 'role': role, 'id': thing,
            'share_from': round(sa, 1), 'share_to': round(sb, 1),
            'wr_from': round(wr(ga, wa), 1), 'wr_to': round(wr(gb, wb), 1),
            'games_from': ga, 'games_to': gb, 'z': round(z, 1),
            'text': (f'{nm.who(cid, role)} · {name}: собирают '
                     f'{sa:.0f}% → {sb:.0f}% игр '
                     f'(винрейт {wr(ga, wa):.1f}% → {wr(gb, wb):.1f}%, '
                     f'{num(ga)} и {num(gb)} игр)'),
        })
    return out


def build_edges(d: Data, nm: Names) -> list[dict]:
    """Сборки, которые заметно обгоняют или отстают от своего же уровня.

    Сравниваем ТОЛЬКО с играми той же длины — иначе меряли бы, кто дольше прожил.
    """
    out = []
    for (cid, role, ids, patch), (g, w) in d.builds.items():
        if patch != d.b or len(ids) < MIN_BUILD_ITEMS or g < MIN_GAMES:
            continue
        total = d.builds_total.get((cid, role, patch), 0)
        if not total or 100.0 * g / total < MIN_BUILD_SHARE:
            continue
        sg, sw = d.strata.get((cid, role, len(ids), patch), (0, 0))
        if sg <= g:
            continue
        # Сравниваем с уровнем БЕЗ самой сборки: иначе она сравнивалась бы с собой.
        rest_g, rest_w = sg - g, sw - w
        if rest_g < MIN_GAMES:
            continue
        delta = wr(g, w) - wr(rest_g, rest_w)
        if abs(delta) < MIN_WR_MOVE:
            continue
        z = z_prop(rest_g, rest_w, g, w)
        if abs(z) < MIN_Z:
            continue
        out.append({
            'kind': 'build', 'champion': cid, 'role': role, 'items': list(ids),
            'wr': round(wr(g, w), 1), 'level': round(wr(rest_g, rest_w), 1),
            'delta': round(delta, 1), 'games': g, 'share': round(100.0 * g / total, 1),
            'z': round(z, 1),
            'text': (f'{nm.who(cid, role)} · {" + ".join(nm.item(i) for i in ids)}: '
                     f'{wr(g, w):.1f}% против {wr(rest_g, rest_w):.1f}% у прочих сборок '
                     f'той же длины ({delta:+.1f}, {num(g)} игр)'),
        })
    return out


def popular_worse(d: Data, nm: Names) -> list[dict]:
    """Самый ходовой выбор руны заметно хуже другого — самая полезная находка.

    Руны берём, а не сборки: кейстоун выбирают ДО игры, достраивать его не надо,
    и винрейт по нему ничем не искажён.
    """
    by_pair = defaultdict(list)
    for (cid, role, ks, patch), (g, w) in d.keystones.items():
        if patch == d.b and g >= MIN_GAMES:
            by_pair[(cid, role)].append((ks, g, w))

    out = []
    for (cid, role), rows in by_pair.items():
        if len(rows) < 2:
            continue
        rows.sort(key=lambda r: -r[1])
        top_ks, top_g, top_w = rows[0]
        best = max(rows[1:], key=lambda r: wr(r[1], r[2]))
        ks, g, w = best
        delta = wr(g, w) - wr(top_g, top_w)
        if delta < MIN_WR_MOVE:
            continue
        z = z_prop(top_g, top_w, g, w)
        if z < MIN_Z:
            continue
        total = d.parsed.get((cid, role, d.b), 0) or 1
        out.append({
            'kind': 'popular_worse', 'champion': cid, 'role': role,
            'popular': top_ks, 'better': ks,
            'wr_popular': round(wr(top_g, top_w), 1), 'wr_better': round(wr(g, w), 1),
            'delta': round(delta, 1), 'games_popular': top_g, 'games_better': g,
            'z': round(z, 1),
            'text': (f'{nm.who(cid, role)} · чаще всего берут {nm.rune(top_ks)} '
                     f'({100.0 * top_g / total:.0f}% игр, винрейт {wr(top_g, top_w):.1f}%), '
                     f'а {nm.rune(ks)} даёт {wr(g, w):.1f}% '
                     f'({delta:+.1f}, {num(g)} игр)'),
        })
    return out


def coherent(shifts: list[dict], nm: Names, what: str) -> list[dict]:
    """Одно и то же случилось у многих чемпионов разом.

    Самый надёжный вид находки: у одного чемпиона сдвиг может быть случайностью,
    у шести одновременно — уже нет. И для поста это лучший материал: не курьёз, а
    смена меты.
    """
    by_thing = defaultdict(list)
    for s in shifts:
        direction = 1 if s['share_to'] > s['share_from'] else -1
        by_thing[(s['id'], direction)].append(s)

    out = []
    for (thing, direction), group in by_thing.items():
        if len(group) < COHERENT_MIN:
            continue
        group.sort(key=lambda s: -abs(s['share_to'] - s['share_from']))
        name = nm.item(thing) if what == 'items' else nm.rune(thing)
        who = ', '.join(nm.champ(s['champion']) for s in group[:6])
        moves = [abs(s['share_to'] - s['share_from']) for s in group]
        out.append({
            'kind': 'coherent', 'id': thing, 'what': what,
            'direction': 'up' if direction > 0 else 'down',
            'champions': [s['champion'] for s in group],
            'move_min': round(min(moves), 1), 'move_max': round(max(moves), 1),
            'text': (f'{name}: {"стали брать чаще" if direction > 0 else "уходят"} '
                     f'сразу у {len(group)} связок — {who}'
                     f'{" и др." if len(group) > 6 else ""} '
                     f'(сдвиг {min(moves):.0f}–{max(moves):.0f} пунктов)'),
        })
    return out


# ── Вывод ───────────────────────────────────────────────────────────────────

def report(found: dict, limit: int) -> None:
    order = [
        ('coherent_items', 'СМЕНА МЕТЫ ПО ПРЕДМЕТАМ — повторилось у многих'),
        ('coherent_runes', 'СМЕНА МЕТЫ ПО РУНАМ — повторилось у многих'),
        ('popular_worse', 'ПОПУЛЯРНЫЙ ВЫБОР ХУЖЕ ДРУГОГО'),
        ('items', 'ОТДЕЛЬНЫЕ ПРЕДМЕТЫ: стали брать иначе'),
        ('runes', 'ОТДЕЛЬНЫЕ РУНЫ: стали брать иначе'),
        ('builds', 'СБОРКИ ВЫШЕ И НИЖЕ СВОЕГО УРОВНЯ'),
    ]
    for key, title in order:
        rows = found.get(key) or []
        if not rows:
            continue
        print()
        print(title)
        print('─' * len(title))
        for r in rows[:limit]:
            print('  • ' + r['text'])
        if len(rows) > limit:
            print(f'  …ещё {len(rows) - limit}')


def main():
    ap = argparse.ArgumentParser(description='Находки в данных для поста')
    ap.add_argument('--db', default='data-slice.db', help='локальная выжимка (ops/pull-stats.sh)')
    ap.add_argument('--bucket', default='emerald', help='эло или all')
    ap.add_argument('--from', dest='pa', default=None, help='патч «до»')
    ap.add_argument('--to', dest='pb', default=None, help='патч «после»')
    ap.add_argument('--limit', type=int, default=8, help='сколько показывать в разделе')
    ap.add_argument('--json', action='store_true', help='машинный вывод')
    args = ap.parse_args()

    if not os.path.exists(args.db):
        raise SystemExit(f'нет файла {args.db} — сними выжимку: bash ops/pull-stats.sh')

    db = sqlite3.connect(f'file:{args.db}?mode=ro', uri=True)
    ps = sorted({r[0] for r in db.execute('SELECT DISTINCT patch FROM base_wr') if r[0]},
                key=lambda s: tuple(int(x) for x in s.split('.')))
    pa = args.pa or (ps[-2] if len(ps) > 1 else ps[-1])
    pb = args.pb or ps[-1]
    if pa == pb:
        raise SystemExit(f'нужны два разных патча, в выжимке только {pb}')

    nm = Names(load_names())
    d = Data(db, args.bucket, (pa, pb))

    item_shifts = share_shifts(d, 'items', nm, 'item')
    rune_shifts = share_shifts(d, 'runes', nm, 'rune')
    found = {
        'coherent_items': coherent(item_shifts, nm, 'items'),
        'coherent_runes': coherent(rune_shifts, nm, 'runes'),
        'popular_worse': sorted(popular_worse(d, nm), key=lambda r: -r['delta']),
        'items': sorted(item_shifts, key=lambda r: -abs(r['share_to'] - r['share_from'])),
        'runes': sorted(rune_shifts, key=lambda r: -abs(r['share_to'] - r['share_from'])),
        'builds': sorted(build_edges(d, nm), key=lambda r: -abs(r['delta'])),
    }

    if args.json:
        print(json.dumps({'from': pa, 'to': pb, 'bucket': args.bucket,
                          'findings': found}, ensure_ascii=False, indent=1))
        return

    print(f'Патчи {pa} → {pb}, эло {args.bucket}. '
          f'Связок чемпион+роль в обоих: {len(d.pairs())}')
    print('Пороги: z ≥ %.0f, игр ≥ %s, сдвиг доли ≥ %.0f п., винрейта ≥ %.0f п.'
          % (MIN_Z, num(MIN_GAMES), MIN_SHARE_MOVE, MIN_WR_MOVE))
    report(found, args.limit)
    print()
    print('Числа измеренные. Причинность из них не следует: «стали собирать — '
          'и вот что стало», а не «предмет поднял винрейт».')


if __name__ == '__main__':
    main()
