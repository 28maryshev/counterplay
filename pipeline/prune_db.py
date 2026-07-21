# -*- coding: utf-8 -*-
"""
prune_db.py — гигиена серверной базы. Держим только последние патчи, чтобы база
не росла бесконечно. Запускать раз в сутки (cron на хосте коллектора).

Что чистим:
  • все таблицы с колонкой patch → оставляем последние KEEP_PATCHES патчей;
  • drafts (тяжёлая, только для калибровки) → последние DRAFT_PATCHES патчей.

НЕ трогаем «длинный хвост» (пары с 1–2 играми): на живой базе они ещё набирают
игры со следующими матчами. Хвост режется только в ПУБЛИКУЕМОЙ копии (publish_data).

VACUUM не делаем: коллектор пишет постоянно, а VACUUM берёт эксклюзивную блокировку.
Удалённые страницы переиспользуются под новые вставки — файл не раздувается.
Разовый VACUUM (сжать файл) — вручную при остановленном коллекторе.

Запуск:  DB_PATH=data/data.db python3 prune_db.py
"""
import os
import sqlite3
from pathlib import Path

DB = os.environ.get('DB_PATH', str(Path(__file__).with_name('data.db')))
KEEP_PATCHES = 6      # последние N патчей во всех таблицах
DRAFT_PATCHES = 2     # для drafts — плотнее (она самая тяжёлая)


def pk(p):
    try:
        return tuple(int(x) for x in p.split('.'))
    except Exception:
        return (0,)


def main():
    con = sqlite3.connect(DB, timeout=120)
    con.execute('PRAGMA busy_timeout=120000')
    patches = sorted({r[0] for r in con.execute('SELECT DISTINCT patch FROM base_wr') if r[0]},
                     key=pk, reverse=True)
    if len(patches) <= KEEP_PATCHES:
        print(f'патчей {len(patches)} <= {KEEP_PATCHES} — чистить нечего')
        con.close()
        return
    keep = patches[:KEEP_PATCHES]
    draft_keep = patches[:DRAFT_PATCHES]
    print(f'патчи: {patches} | держим {keep} | drafts {draft_keep}')

    total = 0
    for (t,) in con.execute("SELECT name FROM sqlite_master WHERE type='table'").fetchall():
        cols = [c[1] for c in con.execute(f'PRAGMA table_info({t})')]
        if 'patch' not in cols:
            continue
        kp = draft_keep if t == 'drafts' else keep
        ph = ','.join('?' * len(kp))
        cur = con.execute(f'DELETE FROM {t} WHERE patch NOT IN ({ph})', kp)
        if cur.rowcount:
            print(f'  {t}: -{cur.rowcount:,}')
            total += cur.rowcount
    con.commit()
    # Свернуть WAL обратно в базу и обрезать журнал — иначе он раздувается на
    # сотни МБ (большие DELETE + одновременные читатели).
    con.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    con.close()
    print(f'удалено строк: {total:,}')


if __name__ == '__main__':
    main()
