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
MIN_ALT_SHARE = 10.0    # % игр у «другого варианта», иначе это удел одиночек

# Чемпиона, которого играют двумя разными сборками, сравнивать сам с собой
# нельзя. Град клинков на Шако — руна под AD, Магическая комета — под AP: это не
# два варианта одного, а два разных Шако, да ещё и разные группы игроков.
# Отличаем по тому, чем чемпион наносит урон: если ни физический, ни магический
# не преобладает — популяция расщеплена, и сравнивать в ней нечего.
#   Шако (лес)   физ 36% / маг 49%  → расщеплён, пропускаем
#   Ли Син (лес) физ 78% / маг 13%  → одна сборка
#   Зерат (мид)  физ  2% / маг 96%  → одна сборка
SPLIT_LOW, SPLIT_HIGH = 25.0, 75.0   # доля физического урона среди phys+magic

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
    """Имена чемпионов, предметов и рун — на двух языках.

    Русский нужен на экране: искалку читает человек, и «предмет 6610» ему ничего
    не говорит. Английский нужен для поста: в канале #meta-radar бот говорит
    по-английски, и один русский пост среди них выглядел бы чужим.
    """
    if CACHE.exists():
        try:
            cached = json.loads(CACHE.read_text(encoding='utf-8'))
            if 'ru' in cached and 'en' in cached:
                return cached
        except Exception:
            pass

    def get(url):
        with urllib.request.urlopen(url, timeout=20) as r:
            return json.load(r)

    out = {}
    try:
        ver = get('https://ddragon.leagueoflegends.com/api/versions.json')[0]
        for lang, locale in (('ru', 'ru_RU'), ('en', 'en_US')):
            base = f'https://ddragon.leagueoflegends.com/cdn/{ver}/data/{locale}'
            names = {'champions': {}, 'items': {}, 'runes': {}}
            for c in get(f'{base}/champion.json')['data'].values():
                names['champions'][c['key']] = c['name']
            for k, v in get(f'{base}/item.json')['data'].items():
                names['items'][k] = v['name']
            for tree in get(f'{base}/runesReforged.json'):
                for slot in tree['slots']:
                    for r in slot['runes']:
                        names['runes'][str(r['id'])] = r['name']
            out[lang] = names
        CACHE.write_text(json.dumps(out, ensure_ascii=False), encoding='utf-8')
    except Exception as e:
        print(f'имена не загрузились ({e}) — покажу идентификаторы', flush=True)
        out.setdefault('ru', {'champions': {}, 'items': {}, 'runes': {}})
        out.setdefault('en', out['ru'])
    return out


ROLES = {
    'ru': {'top': 'топ', 'jungle': 'лес', 'mid': 'мид', 'adc': 'адк', 'support': 'саппорт'},
    'en': {'top': 'top', 'jungle': 'jungle', 'mid': 'mid', 'adc': 'ADC', 'support': 'support'},
}


class Names:
    def __init__(self, d, lang='ru'):
        self.lang = lang
        self.d = d[lang]

    def champ(self, cid):
        return self.d['champions'].get(str(cid), f'#{cid}')

    def item(self, iid):
        return self.d['items'].get(str(iid), f'#{iid}')

    def rune(self, rid):
        return self.d['runes'].get(str(rid), f'#{rid}')

    def who(self, cid, role):
        return f'{self.champ(cid)} ({ROLES[self.lang].get(role, role)})'


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

        # Чем чемпион бьёт: по этому видно, играют ли его одной сборкой.
        # Чистый урон не учитываем — он одинаков при любой сборке.
        self.damage = {}
        try:
            for cid, role, p, m in db.execute(
                    f'SELECT champion_id, role, SUM(phys), SUM(magic) FROM champion_damage '
                    f'WHERE {where} AND patch=? GROUP BY 1,2', (*args, self.b)):
                if (p or 0) + (m or 0) > 0:
                    self.damage[(cid, role)] = 100.0 * p / (p + m)
        except sqlite3.OperationalError:
            pass   # старая выжимка без champion_damage — работаем без проверки

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

    def one_build(self, cid, role) -> bool:
        """Играют ли этого чемпиона одной сборкой.

        Нет данных об уроне — считаем, что да: пропустить настоящую находку
        хуже, чем показать её с оговоркой. Зато там, где расщепление ВИДНО,
        сравнения не делаем вовсе.
        """
        share = self.damage.get((cid, role))
        if share is None:
            return True
        return share <= SPLIT_LOW or share >= SPLIT_HIGH

    def pairs(self):
        """Все связки чемпион+роль, живые в обоих патчах."""
        out = set()
        for (cid, role, patch) in self.parsed:
            if patch == self.b and (cid, role, self.a) in self.parsed:
                out.add((cid, role))
        return sorted(out)


# ── Находки ─────────────────────────────────────────────────────────────────

def share_shifts(d: Data, table: str, nms: dict, kind: str) -> list[dict]:
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
        def line(n, lang):
            what = n.item(thing) if table == 'items' else n.rune(thing)
            if lang == 'ru':
                return (f'{n.who(cid, role)} · {what}: собирают {sa:.0f}% → {sb:.0f}% игр '
                        f'(винрейт {wr(ga, wa):.1f}% → {wr(gb, wb):.1f}%, '
                        f'{num(ga)} и {num(gb)} игр)')
            return (f'{n.who(cid, role)} · {what}: built in {sa:.0f}% → {sb:.0f}% of games '
                    f'(win rate {wr(ga, wa):.1f}% → {wr(gb, wb):.1f}%, '
                    f'{num(ga)} and {num(gb)} games)')

        out.append({
            'kind': kind, 'champion': cid, 'role': role, 'id': thing,
            'share_from': round(sa, 1), 'share_to': round(sb, 1),
            'wr_from': round(wr(ga, wa), 1), 'wr_to': round(wr(gb, wb), 1),
            'games_from': ga, 'games_to': gb, 'z': round(z, 1),
            'text': line(nms['ru'], 'ru'), 'text_en': line(nms['en'], 'en'),
        })
    return out


def build_edges(d: Data, nms: dict) -> list[dict]:
    """Сборки, которые заметно обгоняют или отстают от своего же уровня.

    Сравниваем ТОЛЬКО с играми той же длины — иначе меряли бы, кто дольше прожил.
    """
    out = []
    for (cid, role, ids, patch), (g, w) in d.builds.items():
        if patch != d.b or len(ids) < MIN_BUILD_ITEMS or g < MIN_GAMES:
            continue
        # У расщеплённого чемпиона «прочие сборки той же длины» — это сборки
        # другого билда, и разница между ними ничего не говорит о предметах.
        if not d.one_build(cid, role):
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
        def line(n, lang):
            items = ' + '.join(n.item(i) for i in ids)
            if lang == 'ru':
                return (f'{n.who(cid, role)} · {items}: {wr(g, w):.1f}% против '
                        f'{wr(rest_g, rest_w):.1f}% у прочих сборок той же длины '
                        f'({delta:+.1f}, {num(g)} игр)')
            return (f'{n.who(cid, role)} · {items}: {wr(g, w):.1f}% vs '
                    f'{wr(rest_g, rest_w):.1f}% for other builds of the same length '
                    f'({delta:+.1f}, {num(g)} games)')

        out.append({
            'kind': 'build', 'champion': cid, 'role': role, 'items': list(ids),
            'wr': round(wr(g, w), 1), 'level': round(wr(rest_g, rest_w), 1),
            'delta': round(delta, 1), 'games': g, 'share': round(100.0 * g / total, 1),
            'z': round(z, 1),
            'text': line(nms['ru'], 'ru'), 'text_en': line(nms['en'], 'en'),
        })
    return out


def popular_worse(d: Data, nms: dict) -> list[dict]:
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
        # Чемпион, которого играют двумя сборками, сравнивается сам с собой
        # некорректно: разойдутся не руны, а сборки и сами игроки.
        if not d.one_build(cid, role):
            continue
        rows.sort(key=lambda r: -r[1])
        top_ks, top_g, top_w = rows[0]
        total = d.parsed.get((cid, role, d.b), 0) or 1
        # Вариант должен быть реальным выбором, а не уделом одиночек: у редкой
        # руны играют те, кто её специально изучил, и винрейт это подхватывает.
        alts = [r for r in rows[1:] if 100.0 * r[1] / total >= MIN_ALT_SHARE]
        if not alts:
            continue
        best = max(alts, key=lambda r: wr(r[1], r[2]))
        ks, g, w = best
        delta = wr(g, w) - wr(top_g, top_w)
        if delta < MIN_WR_MOVE:
            continue
        z = z_prop(top_g, top_w, g, w)
        if z < MIN_Z:
            continue
        def line(n, lang):
            share = 100.0 * top_g / total
            if lang == 'ru':
                return (f'{n.who(cid, role)} · чаще всего берут {n.rune(top_ks)} '
                        f'({share:.0f}% игр, винрейт {wr(top_g, top_w):.1f}%), '
                        f'а {n.rune(ks)} даёт {wr(g, w):.1f}% ({delta:+.1f}, {num(g)} игр)')
            return (f'{n.who(cid, role)} · most take {n.rune(top_ks)} '
                    f'({share:.0f}% of games, {wr(top_g, top_w):.1f}%), '
                    f'while {n.rune(ks)} gives {wr(g, w):.1f}% ({delta:+.1f}, {num(g)} games)')

        out.append({
            'kind': 'popular_worse', 'champion': cid, 'role': role,
            'popular': top_ks, 'better': ks,
            'wr_popular': round(wr(top_g, top_w), 1), 'wr_better': round(wr(g, w), 1),
            'delta': round(delta, 1), 'games_popular': top_g, 'games_better': g,
            'z': round(z, 1),
            'text': line(nms['ru'], 'ru'), 'text_en': line(nms['en'], 'en'),
        })
    return out


def coherent(shifts: list[dict], nms: dict, what: str) -> list[dict]:
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
        moves = [abs(s['share_to'] - s['share_from']) for s in group]

        def line(n, lang):
            name = n.item(thing) if what == 'items' else n.rune(thing)
            who = ', '.join(n.champ(s['champion']) for s in group[:6])
            more = (' и др.' if lang == 'ru' else ' and more') if len(group) > 6 else ''
            if lang == 'ru':
                verb = 'стали брать чаще' if direction > 0 else 'уходят'
                return (f'{name}: {verb} сразу у {len(group)} связок — {who}{more} '
                        f'(сдвиг {min(moves):.0f}–{max(moves):.0f} пунктов)')
            verb = 'picked up' if direction > 0 else 'dropped'
            return (f'{name}: {verb} across {len(group)} champion-role pairs at once — '
                    f'{who}{more} (shift of {min(moves):.0f}–{max(moves):.0f} points)')

        out.append({
            'kind': 'coherent', 'id': thing, 'what': what,
            'direction': 'up' if direction > 0 else 'down',
            'champions': [s['champion'] for s in group],
            'move_min': round(min(moves), 1), 'move_max': round(max(moves), 1),
            'text': line(nms['ru'], 'ru'), 'text_en': line(nms['en'], 'en'),
        })
    return out


# ── Вывод ───────────────────────────────────────────────────────────────────

# ── Пост в Discord ──────────────────────────────────────────────────────────

# Цвета как у остальных постов бота (bot/lib/embeds.js).
COLOR_BLUE = 0x2E86C1
FOOTER = 'From the Counterplay database • counterplays.com'


def post_text(found: dict, patch_to: str) -> str | None:
    """Готовый текст поста: только то, что интересно читателю.

    Берём два самых понятных вида находок — смену меты, повторившуюся у
    нескольких чемпионов, и случаи, когда популярный выбор проигрывает другому.
    Остальное (отдельные сдвиги, сборки) остаётся материалом для разбора, но в
    коротком посте только шумит.
    """
    parts = []

    meta = (found.get('coherent_items') or [])[:2] + (found.get('coherent_runes') or [])[:1]
    if meta:
        parts.append('**The meta moved:**')
        parts += [f'• {r["text_en"]}' for r in meta]

    worse = (found.get('popular_worse') or [])[:3]
    if worse:
        if parts:
            parts.append('')
        parts.append('**The popular choice is not the best one:**')
        parts += [f'• {r["text_en"]}' for r in worse]

    if not parts:
        return None

    parts.append('')
    parts.append('_Measured from collected matches. This is what happened, not why: '
                 'players moved, and this is what the results did._')
    return '\n'.join(parts)


def send_post(webhook: str, text: str, patch_to: str, display_patch: str) -> None:
    """Отправка веб-хуком: искалка работает на локальной машине, а бот живёт на
    сервере — достучаться до канала иначе нечем."""
    import urllib.error

    payload = {
        'embeds': [{
            'title': f'📡 META RADAR — Patch {display_patch}',
            'description': text[:4000],
            'color': COLOR_BLUE,
            'footer': {'text': FOOTER},
        }]
    }
    req = urllib.request.Request(
        webhook, data=json.dumps(payload).encode('utf-8'),
        headers={'Content-Type': 'application/json'}, method='POST')
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            print(f'отправлено, код {r.status}')
    except urllib.error.HTTPError as e:
        raise SystemExit(f'Discord ответил {e.code}: {e.read()[:200].decode("utf-8", "replace")}')


def display_patch(patch: str) -> str:
    """Наружу патч называется так, как его видит игрок в клиенте: Riot ведёт две
    линейки со сдвигом ровно в десять мажорных версий (16.18 → 26.18)."""
    try:
        major, minor = patch.split('.')[:2]
        return f'{int(major) + 10}.{minor}'
    except Exception:
        return patch


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
    # Пост в Discord не отправляется сам: сначала показываем, что уйдёт, и
    # только по отдельной команде шлём. Находка — материал, а не готовый текст,
    # и прочитать её глазами перед публикацией обязательно.
    ap.add_argument('--preview', action='store_true', help='показать текст поста, не отправляя')
    ap.add_argument('--post', action='store_true', help='отправить пост в Discord')
    ap.add_argument('--webhook', default=os.environ.get('META_RADAR_WEBHOOK', ''),
                    help='веб-хук канала (или переменная META_RADAR_WEBHOOK)')
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

    raw = load_names()
    nms = {'ru': Names(raw, 'ru'), 'en': Names(raw, 'en')}
    d = Data(db, args.bucket, (pa, pb))

    item_shifts = share_shifts(d, 'items', nms, 'item')
    rune_shifts = share_shifts(d, 'runes', nms, 'rune')
    found = {
        'coherent_items': coherent(item_shifts, nms, 'items'),
        'coherent_runes': coherent(rune_shifts, nms, 'runes'),
        'popular_worse': sorted(popular_worse(d, nms), key=lambda r: -r['delta']),
        'items': sorted(item_shifts, key=lambda r: -abs(r['share_to'] - r['share_from'])),
        'runes': sorted(rune_shifts, key=lambda r: -abs(r['share_to'] - r['share_from'])),
        'builds': sorted(build_edges(d, nms), key=lambda r: -abs(r['delta'])),
    }

    if args.json:
        print(json.dumps({'from': pa, 'to': pb, 'bucket': args.bucket,
                          'findings': found}, ensure_ascii=False, indent=1))
        return

    if args.preview or args.post:
        text = post_text(found, pb)
        if not text:
            print('Постить нечего: ни одна находка не прошла пороги.')
            return
        shown = display_patch(pb)
        print(f'📡 META RADAR — Patch {shown}')
        print(text)
        if args.post:
            if not args.webhook:
                raise SystemExit('нет веб-хука: --webhook <url> или META_RADAR_WEBHOOK')
            print()
            send_post(args.webhook, text, pb, shown)
        else:
            print()
            print('(это предпросмотр — отправить: тот же запуск с --post)')
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
