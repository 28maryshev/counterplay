# -*- coding: utf-8 -*-
"""
CollectorWal — после публикации журнал записи рабочей базы сворачивается.

Повод (журнал 2026-10-13): уборка после публикации сжимает базу VACUUM, а в
режиме WAL сжатая база ложится в журнал записи, и его надо свернуть обратно в
файл. Сворачивать можно, только когда базу никто не читает. Отметка для сайта
ставилась ДО уборки, сторож запускал выгрузки сайта (они читают живую базу
около часа) прямо посреди сжатия, и через раз журнал на 3,5 ГБ оставался лежать
до следующего ключа. Каждый, кто открывал базу, перечитывал его целиком — пять
минут на диске сервера, — и ночной бэкап и старт сбора падали с «database is
locked».

Три части, каждая про свою починку:
  1. порядок: отметка для сайта появляется только после конца уборки;
  2. терпение: читатель во время уборки (бэкап, выжимка) задерживает свёртку
     журнала, но не срывает её;
  3. сигнал: если журнал всё-таки не свернулся, демон пишет об этом в Discord.

Работает на временной базе с настоящей схемой (collect.init_db) и настоящей
уборкой (prune_db.py отдельным процессом, как на сервере). Рабочих баз не
трогает. Около полутора минут.

    python tests/CollectorWal/check.py        # 0 — всё хорошо, 1 — нет
"""
import os
import random
import shutil
import sqlite3
import string
import sys
import tempfile
import threading
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-collectorwal-'))
os.environ.update(DB_PATH=str(TMP / 'none.db'), CONTROL_DIR=str(TMP / 'control'),
                  GITHUB_TOKEN='test', DISCORD_WEBHOOK='',
                  PYTHONUTF8='1')        # уборка — отдельный процесс; на Windows иначе пишет в cp1251
sys.path.insert(0, str(ROOT / 'pipeline'))
import collect                       # noqa: E402
import collector_service as svc      # noqa: E402

(TMP / 'control').mkdir()
MARK = TMP / 'control' / 'site-publish'
WAL_OK_MB = 1.0                      # свёрнутый журнал — 0 байт; 1 МБ — с запасом
failures: list[str] = []


def make_db(name: str) -> str:
    """База с настоящей схемой: половина строк старше окна уборки, половина свежая
    — VACUUM переписывает десятки мегабайт, и журнал после него заметен."""
    path = str(TMP / name)
    con = collect.init_db(path)
    now = int(time.time())
    rnd = random.Random(13)
    def puuid():
        return ''.join(rnd.choices(string.ascii_letters + string.digits, k=78))
    con.executemany('INSERT INTO seen_players VALUES (?, ?)',
                    ((puuid(), now - (30 if i % 2 else 1) * 86400) for i in range(300_000)))
    con.executemany('INSERT INTO base_wr (champion_id, role, tier_bucket, patch, games, wins) '
                    'VALUES (?, ?, ?, ?, 10, 5)',
                    ((c, 'MIDDLE', 'gold', p) for c in range(1, 60)
                     for p in ('16.15', '16.16', '16.17', '16.18', '16.19', '16.20')))
    con.commit()
    con.execute('PRAGMA wal_checkpoint(TRUNCATE)')
    con.close()
    return path


def wal_mb(path: str) -> float:
    try:
        return os.path.getsize(path + '-wal') / 1048576
    except OSError:
        return 0.0


class Reader:
    """Держит срез базы, как долгая выгрузка или бэкап: открытая транзакция
    чтения, пока не отпустят или не выйдет время."""

    def __init__(self, path: str, hold: float):
        self.path, self.hold = path, hold
        self.release = threading.Event()
        self.started = threading.Event()
        self.thread = threading.Thread(target=self._run, daemon=True)

    def _run(self):
        con = sqlite3.connect(f'file:{self.path}?mode=ro', uri=True, timeout=60)
        try:
            con.execute('BEGIN')
            con.execute('SELECT count(*) FROM seen_players').fetchone()
            self.started.set()
            self.release.wait(self.hold)
            con.rollback()
        finally:
            con.close()

    def start(self):
        self.thread.start()
        self.started.wait(30)
        return self

    def stop(self):
        self.release.set()
        self.thread.join(60)


def use_db(path: str):
    svc.DB_PATH = path
    MARK.unlink(missing_ok=True)


# Публикация без сети: сборку и заливку подменяем, остальное — как в бою.
svc.publish_data.publish = lambda *a, **k: {'patch': '16.20', 'version': 'TEST',
                                            'slim_mb': 1, 'buckets': {}}
notices: list[str] = []
_notify = svc.notify
svc.notify = lambda text: (notices.append(text), _notify(text))


# ── 1. Отметка для сайта — после уборки ──────────────────────────────────────
# Сторож сайта на сервере срабатывает раз в 5 минут, здесь — сразу, как увидит
# отметку, и держит базу дольше, чем уборка готова ждать.
print('1. порядок: уборка, потом отметка для сайта')
db = make_db('order.db')
use_db(db)
os.environ['PRUNE_WAL_WAIT'] = '5'
state = {'pruned': False, 'mark_early': None, 'reader': None}
real_prune = svc.prune_db


def watched_prune():
    try:
        real_prune()
    finally:
        state['pruned'] = True


def site_watch():
    deadline = time.time() + 120
    while time.time() < deadline and not MARK.exists():
        time.sleep(0.02)
    if MARK.exists():
        state['mark_early'] = not state['pruned']
        state['reader'] = Reader(db, hold=90).start()


svc.prune_db = watched_prune
watcher = threading.Thread(target=site_watch, daemon=True)
watcher.start()
svc.publish_db(5, why='key')
watcher.join(130)
left = wal_mb(db)
svc.prune_db = real_prune
if state['reader']:
    state['reader'].stop()
print(f'   отметка появилась {"до конца уборки" if state["mark_early"] else "после уборки"}, '
      f'журнал после публикации {left:.1f} МБ')
if state['mark_early'] is None:
    failures.append('1: отметки для сайта нет совсем')
elif state['mark_early']:
    failures.append('1: отметка для сайта появилась до конца уборки — выгрузки сайта '
                    'читают базу во время сжатия и не дают свернуть журнал')
if left > WAL_OK_MB:
    failures.append(f'1: журнал после публикации не свёрнут: {left:.0f} МБ')


# ── 2. Читатель во время уборки задерживает свёртку, но не срывает её ────────
print('2. терпение: бэкап читает базу во время уборки 40 с')
db = make_db('patience.db')
use_db(db)
os.environ['PRUNE_WAL_WAIT'] = '120'
reader = Reader(db, hold=40).start()
t0 = time.time()
svc.prune_db()
took = time.time() - t0
left = wal_mb(db)
reader.stop()
print(f'   уборка шла {took:.0f} с, журнал после неё {left:.1f} МБ')
if left > WAL_OK_MB:
    failures.append(f'2: уборка сдалась, пока базу читали 40 с: журнал {left:.0f} МБ '
                    'остался несвёрнутым')


# ── 3. Не свернулся — демон говорит об этом ─────────────────────────────────
print('3. сигнал: читатель не уходит дольше, чем уборка готова ждать')
db = make_db('alarm.db')
use_db(db)
os.environ['PRUNE_WAL_WAIT'] = '5'
svc.WAL_ALARM_MB = 1
notices.clear()
reader = Reader(db, hold=120).start()
svc.prune_db()
left = wal_mb(db)
reader.stop()
said = [n for n in notices if 'журнал' in n.lower()]
print(f'   журнал {left:.1f} МБ, сообщений о нём: {len(said)}')
if left <= WAL_OK_MB:
    failures.append('3: журнал свернулся при живом читателе — проверка ничего не проверила')
elif not said:
    failures.append(f'3: журнал {left:.0f} МБ остался несвёрнутым, а демон промолчал')


shutil.rmtree(TMP, ignore_errors=True)
print()
if failures:
    print('НЕ ПРОШЛА:')
    for f in failures:
        print('  - ' + f)
    sys.exit(1)
print('ПРОШЛА: журнал после публикации сворачивается, а если нет — об этом слышно')
