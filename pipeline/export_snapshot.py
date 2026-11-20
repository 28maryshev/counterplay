# -*- coding: utf-8 -*-
"""
export_snapshot.py — выжимка рабочей базы для выгрузки рун: нужные патчи
одним проходом подряд.

На коллекторе база весит больше 4 ГБ при 911 МБ памяти, рядом пишет сбор, и в
кэш она не помещается. Ключ таблиц — «чемпион, роль, …, эло, патч», а строки
лежат в порядке вставки, вразброс по всему файлу: каждый запрос выгрузки рун
«связка + кейстоун» доставал свои строки с диска по одной. Выгрузка 19.11 за
2,5 часа прочитала 11 ГБ (два с половиной размера базы) отдельными чтениями по
4 КБ, сделала четверть связок, а процессор стоял на 2% (журнал 2026-11-20).

Поэтому сначала нужные патчи переливаются в маленькую базу рядом — подряд, без
индекса, с суммой по эло, — и все запросы связок идут уже к ней: там строки
одной связки лежат рядом. Таблицы и колонки те же (без эло), так что
export_runes.build_champ_role читает её так же, как рабочую базу. На
коллекторе 20.11: выжимка 158 с и 444 МБ вместо девяти часов разбросанного
чтения.
"""
import sqlite3
import time
from pathlib import Path

# Таблица → ключ без эло и патча — ровно то, по чему её читает export_runes.
SNAPSHOT = {
    'base_wr':          ('champion_id', 'role'),
    'keystone_wr':      ('champion_id', 'role', 'keystone'),
    'rune_page':        ('champion_id', 'role', 'keystone', 'primary_style', 'sub_style',
                         'perks', 'shards'),
    'keystone_matchup': ('champion_id', 'role', 'vs_champion_id', 'keystone'),
    'item_build':       ('champion_id', 'role', 'items'),
    'spell_wr':         ('champion_id', 'role', 'spells'),
    # Руны против соперников по линии: кейстоун против второго бот-лейнера
    # (vsDuo) и мелкие руны (perkVs).
    'rune_matchup':     ('champion_id', 'role', 'vs_champion_id', 'vs_role', 'perk'),
}
# Что ещё отсечь при переливе. В лесу правил по линии нет — его строки
# rune_matchup выгрузке не нужны (пятая часть таблицы).
SNAPSHOT_FILTER = {'rune_matchup': "role != 'jungle'"}
# По скольким колонкам ключа индекс выжимки (иначе по двум: чемпион и роль).
# Страницы рун читаются по кейстоуну.
SNAPSHOT_INDEX = {'rune_page': 3}


def snapshot_sql(table: str, ps: list[str]) -> str:
    """Перелив одной таблицы. NOT INDEXED — главное: без него SQLite берёт
    индекс ключа (он же даёт готовый порядок для GROUP BY) и снова читает
    строки вразброс. Полный проход по таблице читает файл подряд."""
    cols = ', '.join(SNAPSHOT[table])
    ph = ','.join('?' * len(ps))
    extra = f' AND {SNAPSHOT_FILTER[table]}' if table in SNAPSHOT_FILTER else ''
    return (f'INSERT INTO main.{table} SELECT {cols}, patch, SUM(games), SUM(wins) '
            f'FROM src.{table} NOT INDEXED WHERE patch IN ({ph}){extra} '
            f'GROUP BY {cols}, patch ORDER BY {cols}, patch')


def snapshot(db: str, ps: list[str], path: Path, log=print) -> sqlite3.Connection:
    """Маленькая база с патчами ps из db; возвращает соединение с ней."""
    con = sqlite3.connect(str(path), uri=True)
    # Выжимка одноразовая: журнал и сброс на диск ей не нужны. Память под
    # сортировку GROUP BY — 64 МБ, больше SQLite сам уносит во временные файлы.
    con.execute('PRAGMA journal_mode=OFF')
    con.execute('PRAGMA synchronous=OFF')
    con.execute('PRAGMA cache_size=-65536')
    con.execute('ATTACH DATABASE ? AS src', (f'file:{db}?mode=ro',))
    have = {r[0] for r in con.execute("SELECT name FROM src.sqlite_master WHERE type='table'")}
    for table, key in SNAPSHOT.items():
        t0 = time.monotonic()
        cols = ', '.join(key)
        con.execute(f'CREATE TABLE main.{table} ({cols}, patch, games, wins)')
        # rune_matchup собирается с 19.11: в базе старше её нет — пустая.
        if table in have:
            con.execute(snapshot_sql(table, ps), ps)
        idx = ', '.join(key[:SNAPSHOT_INDEX.get(table, 2)])
        con.execute(f'CREATE INDEX main.ix_{table} ON {table} ({idx})')
        con.commit()
        n = con.execute(f'SELECT COUNT(*) FROM main.{table}').fetchone()[0]
        log(f'  выжимка {table}: {n} строк, {time.monotonic() - t0:.0f} с')
    con.execute('DETACH DATABASE src')
    return con
