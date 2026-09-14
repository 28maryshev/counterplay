# -*- coding: utf-8 -*-
"""
ops_log.py — журнал эксплуатации коллектора.

Отдельная маленькая база рядом с рабочей. Почему не в data.db: там матчи, её
пересобирает VACUUM, чистит prune_db и переливают с сервера на сервер — ни одного
такого движения телеметрия бы не пережила. ops.db не трогает никто, кроме этого
модуля.

Пишем два вида строк:

    sample — снимок раз в несколько минут: состояние, счётчик матчей, вес базы,
             свободное место. По нему видно, ДВИГАЛСЯ ли сбор: подряд идущие
             снимки с одинаковым числом матчей в состоянии collecting — это и
             есть простой. Ровно так мы 14.09 задним числом восстановили, что
             коллектор встал 12.09 в 17:31, а заметили это только через сутки.
    event  — что случилось: старт, принятый ключ, протухший ключ, публикация,
             уборка, нехватка места.

Главное правило: телеметрия не имеет права ронять сбор. Здесь всё молча глотает
ошибки — потерянная строка журнала не стоит ни одного потерянного матча.

Читать написанное: `python ops_stats.py` (сводка) или SQL по events напрямую.
"""

import json
import os
import sqlite3
import threading
import time

_path = None
_lock = threading.Lock()
_ready = False

SCHEMA = """
CREATE TABLE IF NOT EXISTS events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    ts           INTEGER NOT NULL,       -- unix utc
    kind         TEXT    NOT NULL,       -- sample | start | key_* | publish* | prune | disk_low
    state        TEXT,                   -- состояние демона на тот момент
    matches      INTEGER,                -- снимок счётчиков: по ним считается движение
    db_mb        REAL,
    disk_free_gb REAL,
    detail       TEXT                    -- JSON с остальным (патч, версия, ошибка…)
);
CREATE INDEX IF NOT EXISTS events_ts   ON events(ts);
CREATE INDEX IF NOT EXISTS events_kind ON events(kind, ts);
"""

# Снимки старше этого не нужны: они про «двигалось ли», а не про историю решений.
# События (публикации, ключи, аварии) не трогаем — их мало и они ценны все.
SAMPLE_KEEP_DAYS = 180


def setup(db_path: str):
    """Путь к ops.db выводится из пути рабочей базы — они лежат рядом на томе."""
    global _path, _ready
    _path = os.path.join(os.path.dirname(os.path.abspath(db_path)), 'ops.db')
    _ready = False
    return _path


def _con():
    con = sqlite3.connect(_path, timeout=5)
    global _ready
    if not _ready:
        con.executescript(SCHEMA)
        con.commit()
        _ready = True
    return con


def record(kind: str, state=None, matches=None, db_mb=None, disk_free_gb=None, **detail):
    """Одна строка журнала. Ошибки глушим: сбор важнее записи о сборе."""
    if not _path:
        return
    try:
        with _lock:
            con = _con()
            try:
                con.execute(
                    'INSERT INTO events (ts, kind, state, matches, db_mb, disk_free_gb, detail)'
                    ' VALUES (?,?,?,?,?,?,?)',
                    (int(time.time()), kind, state, matches, db_mb, disk_free_gb,
                     json.dumps(detail, ensure_ascii=False) if detail else None))
                con.commit()
            finally:
                con.close()
    except Exception:
        pass


def prune(days: int = SAMPLE_KEEP_DAYS):
    """Чистит старые снимки. Зовётся редко — из той же уборки, что и база."""
    if not _path:
        return
    try:
        with _lock:
            con = _con()
            try:
                con.execute("DELETE FROM events WHERE kind='sample' AND ts < ?",
                            (int(time.time()) - days * 86400,))
                con.commit()
                con.execute('VACUUM')
            finally:
                con.close()
    except Exception:
        pass
