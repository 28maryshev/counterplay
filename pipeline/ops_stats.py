# -*- coding: utf-8 -*-
"""
ops_stats.py — сводка по журналу эксплуатации (ops.db).

Отвечает на вопросы, ради которых журнал и заводили: сколько раз коллектор
вставал и на сколько, что происходило с ключами, когда и чем публиковались базы,
как рос файл и куда девалось место.

    python ops_stats.py                    # за последние 30 дней
    python ops_stats.py --days 7
    python ops_stats.py --db /app/data/data.db   # ops.db берётся рядом

Простоем считается отрезок, на котором демон был в состоянии collecting, а
счётчик матчей не двигался дольше STALL_MIN минут. Уменьшение счётчика простоем
не считается: это уборка удалила старые записи, а не сбор встал.
"""

import argparse
import json
import os
import sqlite3
import time
from datetime import datetime, timezone

STALL_MIN = 30          # минут неподвижности, с которых это уже простой


def ts_str(ts):
    return datetime.fromtimestamp(ts, timezone.utc).strftime('%d.%m %H:%M')


def dur(seconds):
    h, m = divmod(int(seconds) // 60, 60)
    if h >= 24:
        d, h = divmod(h, 24)
        return f'{d} д {h} ч'
    return f'{h} ч {m:02d} мин' if h else f'{m} мин'


def detail(row):
    try:
        return json.loads(row) if row else {}
    except Exception:
        return {}


def find_stalls(samples):
    """Отрезки неподвижности. Вход — снимки (ts, state, matches) по возрастанию."""
    stalls, start, prev = [], None, None
    for ts, state, matches in samples:
        moving = prev is None or matches is None or prev[2] is None or matches != prev[2]
        if state == 'collecting' and not moving:
            if start is None:
                start = prev[0]          # встало на прошлом снимке
        else:
            if start is not None:
                stalls.append((start, prev[0]))
                start = None
        prev = (ts, state, matches)
    if start is not None and prev is not None:
        stalls.append((start, prev[0]))
    return [(a, b) for a, b in stalls if b - a >= STALL_MIN * 60]


def main():
    ap = argparse.ArgumentParser(description='Сводка по журналу эксплуатации коллектора')
    ap.add_argument('--db', default=str(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'data.db')),
                    help='путь к рабочей базе (ops.db ищется рядом) или прямо к ops.db')
    ap.add_argument('--days', type=int, default=30)
    args = ap.parse_args()

    path = args.db if args.db.endswith('ops.db') else \
        os.path.join(os.path.dirname(os.path.abspath(args.db)), 'ops.db')
    if not os.path.exists(path):
        raise SystemExit(f'Журнала нет: {path}. Он появляется, когда демон отработает.')

    con = sqlite3.connect(f'file:{path}?mode=ro', uri=True)
    since = int(time.time()) - args.days * 86400
    rows = con.execute('SELECT ts, kind, state, matches, db_mb, disk_free_gb, detail'
                       ' FROM events WHERE ts >= ? ORDER BY ts', (since,)).fetchall()
    if not rows:
        raise SystemExit(f'За последние {args.days} дней записей нет.')

    print(f'\nЖурнал за {args.days} дней: {ts_str(rows[0][0])} — {ts_str(rows[-1][0])}, '
          f'{len(rows)} записей\n')

    samples = [(r[0], r[2], r[3]) for r in rows if r[1] == 'sample']

    # --- Простои ---
    stalls = find_stalls(samples)
    if not samples:
        print('Простои: снимков пока нет (демон не отработал ни одного окна).')
    elif not stalls:
        print(f'Простои: ни одного дольше {STALL_MIN} минут.')
    else:
        total = sum(b - a for a, b in stalls)
        span = samples[-1][0] - samples[0][0]
        print(f'Простои: {len(stalls)} шт, суммарно {dur(total)} '
              f'({total / span * 100:.1f}% наблюдаемого времени)')
        for a, b in sorted(stalls, key=lambda x: x[1] - x[0], reverse=True)[:10]:
            print(f'   {ts_str(a)} → {ts_str(b)}   {dur(b - a)}')
    print()

    # --- Ключи ---
    keys = [r for r in rows if r[1] in ('key_accepted', 'key_expired')]
    if keys:
        print('Ключи:')
        for r in keys:
            d = detail(r[6])
            if r[1] == 'key_accepted':
                print(f'   {ts_str(r[0])}  принят')
            else:
                print(f'   {ts_str(r[0])}  истёк, собрано за него +{d.get("collected", 0):,}'
                      .replace(',', ' '))
        print()

    # --- Публикации ---
    pubs = [r for r in rows if r[1] == 'publish']
    fails = [r for r in rows if r[1] == 'publish_failed']
    if pubs:
        print('Публикации:')
        for r in pubs:
            d = detail(r[6])
            print(f'   {ts_str(r[0])}  патч {d.get("patch", "?")}  '
                  f'тонкая {d.get("slim_mb", "?")} МБ  версия {d.get("version", "?")}'
                  f'  (+{d.get("collected", 0)} за круг)')
    if fails:
        print(f'   не удались: {len(fails)}')
        for r in fails:
            print(f'   {ts_str(r[0])}  ОШИБКА: {detail(r[6]).get("error", "?")}')
    if pubs or fails:
        print()

    # --- Уборки и место ---
    prunes = [r for r in rows if r[1] == 'prune']
    if prunes:
        freed = sum(detail(r[6]).get('before_mb', 0) - detail(r[6]).get('after_mb', 0)
                    for r in prunes)
        print(f'Уборок базы: {len(prunes)}, освобождено суммарно {freed:.0f} МБ')
    lows = [r for r in rows if r[1] == 'disk_low']
    if lows:
        print(f'Остановок из-за места: {len(lows)}')
        for r in lows:
            d = detail(r[6])
            print(f'   {ts_str(r[0])}  свободно {d.get("free_mb")} МБ, нужно {d.get("need_mb")}')
    if prunes or lows:
        print()

    # --- Рост ---
    with_m = [r for r in rows if r[3] is not None]
    if len(with_m) >= 2:
        first, last = with_m[0], with_m[-1]
        days = max((last[0] - first[0]) / 86400, 0.01)
        print(f'Матчей в базе: {first[3]:,} → {last[3]:,} '
              f'(+{last[3] - first[3]:,}, ~{(last[3] - first[3]) / days:,.0f} в сутки)'
              .replace(',', ' '))
    with_s = [r for r in rows if r[4] is not None]
    if len(with_s) >= 2:
        print(f'Файл базы: {with_s[0][4]:.0f} → {with_s[-1][4]:.0f} МБ')
    with_d = [r for r in rows if r[5] is not None]
    if with_d:
        print(f'Свободно на диске: {with_d[-1][5]:.1f} ГБ '
              f'(минимум за период — {min(r[5] for r in with_d):.1f})')
    print()

    # --- Фазы ---
    states = [r for r in rows if r[1] == 'state']
    if states:
        spent = {}
        for cur, nxt in zip(states, states[1:] + [(int(time.time()), None, None, None, None, None, None)]):
            spent[cur[2]] = spent.get(cur[2], 0) + (nxt[0] - cur[0])
        total = sum(spent.values()) or 1
        print('Время по фазам:')
        for st, sec in sorted(spent.items(), key=lambda x: -x[1]):
            print(f'   {st or "?":<14} {dur(sec):>12}  {sec / total * 100:4.1f}%')
        print()

    con.close()


if __name__ == '__main__':
    main()
