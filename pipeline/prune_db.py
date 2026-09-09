# -*- coding: utf-8 -*-
"""
prune_db.py — гигиена серверной базы. Держим только то, что ещё может
понадобиться, чтобы база не росла бесконечно.

Что чистим:
  • таблицы с колонкой patch → последние KEEP_PATCHES патчей;
  • drafts (тяжёлая, только для калибровки) → последние DRAFT_PATCHES патчей;
  • seen_players → недавние: сбор смотрит окно в 3 дня, остальное мёртвый груз;
  • processed_matches → защита от повторного счёта матча. Сбор берёт матчи не
    старше COLLECT_DAYS (30), поэтому запись старше MATCH_KEEP_DAYS уже никогда
    не сработает.

НЕ трогаем «длинный хвост» (пары с 1–2 играми): на живой базе они ещё набирают
игры со следующими матчами. Хвост режется только в ПУБЛИКУЕМОЙ копии (publish_data).

Удаляем МАЛЕНЬКИМИ ПОРЦИЯМИ с коммитом после каждой. В режиме WAL писатель у
базы ровно один, и одна большая транзакция DELETE держит его столько, сколько
идёт сама, — сбор в это время не может записать ни строки и падает с «database
is locked». Окнами по WINDOW строк каждая транзакция живёт доли секунды, и
сбор спокойно вклинивается между ними.

VACUUM по умолчанию не делаем: он берёт эксклюзивную блокировку, а коллектор
пишет почти всегда. Но без него файл не отдаёт место обратно — удалённые
страницы лишь переиспользуются под новые вставки. Поэтому публикация зовёт нас
с PRUNE_VACUUM=1 в тот момент, когда сбор стоит: только там сжатие безопасно и
только там оно и нужно — публикация копирует базу целиком и требует свободного
места в полтора её размера.

Запуск:  DB_PATH=data/data.db python3 prune_db.py
         DB_PATH=data/data.db PRUNE_VACUUM=1 python3 prune_db.py   # со сжатием
"""
import os
import sqlite3
import time
from pathlib import Path

DB = os.environ.get('DB_PATH', str(Path(__file__).with_name('data.db')))
KEEP_PATCHES = 4      # публикация везёт 3 (движку хватает 2 + один на «удержание»)
DRAFT_PATCHES = 2     # для drafts — плотнее (она самая тяжёлая)
PLAYER_KEEP_DAYS = 7  # сбор смотрит 3 дня (collect.RECENT_PLAYER_DAYS)
MATCH_KEEP_DAYS = 90  # тройной запас к окну сбора COLLECT_DAYS=30
VACUUM = os.environ.get('PRUNE_VACUUM') == '1'
# Чистка processed_matches требует колонки ts, а её появление ЛОМАЕТ старый код
# сбора: он вставляет строку без имён колонок («VALUES (?)»), и лишняя колонка
# делает такую вставку недопустимой. Поэтому и миграция, и сама чистка включаются
# только флагом — его ставит collector_service, который обновляется вместе с
# collect.py. Cron на хосте флага не ставит и остаётся безопасным при любой
# версии контейнера.
MATCH_PRUNE = os.environ.get('PRUNE_MATCHES') == '1'
WINDOW = 50000        # сколько rowid просматриваем за одну транзакцию
PAUSE = 0.02          # пауза между порциями — окно для записи коллектора
RETRIES = 5           # попыток на окно, если база занята сбором
BUDGET_S = 1800       # дольше получаса не работаем: остаток дочистится в следующий раз


def pk(p):
    try:
        return tuple(int(x) for x in p.split('.'))
    except Exception:
        return (0,)


def size_mb() -> float:
    try:
        return os.path.getsize(DB) / 1048576
    except OSError:
        return 0.0


def ensure_schema(con):
    """Отметка времени у processed_matches и счётчик удалённых.

    Колонки ts у таблицы изначально не было — записи со старых сборов остаются
    с NULL. Их не удаляем (возраст неизвестен), а проставляем текущим временем:
    хуже, чем есть, не будет, а со следующего прохода они начнут стареть штатно.
    """
    if not MATCH_PRUNE:
        return
    cols = [c[1] for c in con.execute('PRAGMA table_info(processed_matches)')]
    if 'ts' not in cols:
        con.execute('ALTER TABLE processed_matches ADD COLUMN ts INTEGER')
        con.execute('CREATE INDEX IF NOT EXISTS idx_pm_ts ON processed_matches(ts)')
    con.execute("""CREATE TABLE IF NOT EXISTS prune_meta (
                     key TEXT PRIMARY KEY, value INTEGER NOT NULL DEFAULT 0)""")
    now = int(time.time())
    con.execute('UPDATE processed_matches SET ts=? WHERE ts IS NULL', (now,))


def delete_batched(con, table, where, params, deadline) -> int:
    """Удаляет порциями, отпуская базу между ними. Возвращает число строк.

    Идём ОКНАМИ ПО rowid, а не «первые N подходящих». Разница принципиальная:
    patch стоит последним в первичном ключе, поиск по нему индексом не
    поддержан, и запрос «дай 2000 строк со старым патчем» каждый раз сканирует
    таблицу с начала — а по мере удаления начало пустеет, и каждая следующая
    порция дороже предыдущей. Окно по rowid ограничивает работу заранее: один
    проход по таблице кусками предсказуемого размера.

    В WAL-режиме писатель у базы один, поэтому держать длинную транзакцию
    нельзя — сбор в это время не сможет записать ни строки и упадёт с
    «database is locked». Отсюда и коммит после каждого окна, и пауза.
    """
    top = con.execute(f'SELECT max(rowid) FROM {table}').fetchone()[0] or 0
    total, lo = 0, 0
    while lo <= top:
        if time.time() > deadline:
            print(f'  {table}: остановился по времени, удалено {total:,}', flush=True)
            return total
        hi = lo + WINDOW - 1
        # Коллектор пишет почти непрерывно, и окно записи иногда не достаётся с
        # первого раза. Это нормальная очередь, а не ошибка: ждём и пробуем
        # снова. Падать здесь нельзя — уборка бросила бы работу на середине, а
        # база продолжила бы расти.
        for attempt in range(RETRIES):
            try:
                cur = con.execute(
                    f'DELETE FROM {table} WHERE rowid BETWEEN ? AND ? AND {where}',
                    (lo, hi, *params))
                con.commit()
                break
            except sqlite3.OperationalError as e:
                if 'locked' not in str(e).lower():
                    raise
                time.sleep(2 * (attempt + 1))
        else:
            print(f'  {table}: база занята, отложил остаток до следующего раза '
                  f'(удалено {total:,})', flush=True)
            return total
        total += cur.rowcount
        lo = hi + 1
        time.sleep(PAUSE)
    return total


def bump(con, key, n):
    """Копим число удалённых матчей: счётчик собранного показывается людям и
    не должен пойти назад из-за уборки (см. collect.db_total)."""
    if n:
        con.execute("""INSERT INTO prune_meta (key, value) VALUES (?, ?)
                       ON CONFLICT(key) DO UPDATE SET value = value + ?""", (key, n, n))


def main():
    before = size_mb()
    deadline = time.time() + BUDGET_S
    con = sqlite3.connect(DB, timeout=120)
    # Ждём базу недолго: наша работа не срочная, а сбор ждать не должен.
    con.execute('PRAGMA busy_timeout=15000')
    ensure_schema(con)

    total = 0
    patches = sorted({r[0] for r in con.execute('SELECT DISTINCT patch FROM base_wr') if r[0]},
                     key=pk, reverse=True)
    if len(patches) > KEEP_PATCHES:
        keep = patches[:KEEP_PATCHES]
        draft_keep = patches[:DRAFT_PATCHES]
        print(f'патчи: {patches} | держим {keep} | drafts {draft_keep}', flush=True)
        for (t,) in con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall():
            cols = [c[1] for c in con.execute(f'PRAGMA table_info({t})')]
            if 'patch' not in cols:
                continue
            kp = draft_keep if t == 'drafts' else keep
            ph = ','.join('?' * len(kp))
            n = delete_batched(con, t, f'patch NOT IN ({ph})', kp, deadline)
            if n:
                print(f'  {t}: -{n:,}', flush=True)
                total += n
    else:
        print(f'патчей {len(patches)} <= {KEEP_PATCHES} — по патчам чистить нечего', flush=True)

    now = int(time.time())
    n = delete_batched(con, 'seen_players', 'ts < ?',
                       (now - PLAYER_KEEP_DAYS * 86400,), deadline)
    if n:
        print(f'  seen_players: -{n:,}', flush=True)
        total += n

    if MATCH_PRUNE:
        n = delete_batched(con, 'processed_matches', 'ts < ?',
                           (now - MATCH_KEEP_DAYS * 86400,), deadline)
        if n:
            print(f'  processed_matches: -{n:,}', flush=True)
            bump(con, 'matches_pruned', n)
            total += n

    con.commit()
    # Свернуть WAL обратно в базу: после множества удалений он разрастается на
    # сотни МБ. TRUNCATE (обрезать файл журнала) требует, чтобы больше никто не
    # читал базу, поэтому его зовём только вместе со сжатием — там сбор стоит.
    # В обычном проходе достаточно PASSIVE: он не мешает никому.
    con.execute(f"PRAGMA wal_checkpoint({'TRUNCATE' if VACUUM else 'PASSIVE'})")

    if VACUUM:
        print('сжимаю файл (VACUUM)…', flush=True)
        t0 = time.time()
        con.execute('VACUUM')
        print(f'  готово за {time.time() - t0:.0f} с', flush=True)
    con.close()

    after = size_mb()
    freed = before - after
    print(f'удалено строк: {total:,} · файл {before:,.0f} → {after:,.0f} МБ '
          f'({"освободилось " + format(freed, ",.0f") + " МБ" if freed > 1 else "без сжатия"})')


if __name__ == '__main__':
    main()
