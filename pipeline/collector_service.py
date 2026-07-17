"""
collector_service.py — демон сбора на сервере.

Работает бесконечно и ждёт ключ, который присылают из Discord (`/collect`).
Бот кладёт ключ в файл в общем томе — сервис его подхватывает, гонит сбор
параллельно по регионам и по завершении круга публикует базу в GitHub release
`data` (приложение обновится само по data-version.json).

Dev-ключ живёт 24 ч, поэтому истечение ключа — штатная ситуация, а не ошибка:
ловим 401/403, база уже сохранена, пишем в Discord «пришли новый ключ» и снова
ждём. Никакого простоя данных: собранное до момента протухания опубликовано.

Файлы в CONTROL_DIR (общий том с ботом):
    key      — Riot API-ключ (пишет бот; сервис удаляет после протухания)
    status   — JSON со статусом для `/collect status`

Переменные окружения:
    CONTROL_DIR      каталог обмена с ботом (по умолчанию /control)
    DB_PATH          путь к data.db
    DISCORD_WEBHOOK  webhook для уведомлений (протух ключ / круг завершён)
    GITHUB_TOKEN     для публикации базы (без него — только сбор)
    REGIONS/BUCKETS  что собирать (по умолчанию all)
    COLLECT_DAYS     окно матчей в днях (по умолчанию 30)
"""

import json
import os
import shutil
import sqlite3
import sys
import threading
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))
import collect  # noqa: E402  (наш модуль сбора)
from collect import KeyExpired, DiskLow  # noqa: E402
import publish_data  # noqa: E402

CONTROL = Path(os.environ.get('CONTROL_DIR', '/control'))
DB_PATH = os.environ.get('DB_PATH', str(Path(__file__).with_name('data.db')))
WEBHOOK = os.environ.get('DISCORD_WEBHOOK', '')
GH_TOKEN = os.environ.get('GITHUB_TOKEN', '')
DAYS = int(os.environ.get('COLLECT_DAYS', '30'))
KEY_FILE = CONTROL / 'key'
STATUS_FILE = CONTROL / 'status'
POLL = 15  # секунд между проверками файла ключа


def notify(text: str):
    """Сообщение в Discord. Молча игнорируем сбой — сбор важнее уведомления."""
    if not WEBHOOK:
        print(f'[notify] {text}', flush=True)
        return
    try:
        requests.post(WEBHOOK, json={'content': text[:1900]}, timeout=15)
    except Exception as e:
        print(f'[notify] не отправилось: {e}', flush=True)


def db_matches() -> int | None:
    """Матчей в базе. Идёт в уведомления — по нему видно, что сбор реально
    двигается, а не простаивает. Read-only, чтобы не мешать пишущему сбору."""
    try:
        con = sqlite3.connect(f'file:{DB_PATH}?mode=ro', uri=True)
        try:
            return con.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]
        finally:
            con.close()
    except Exception:
        return None


def db_size_mb() -> float | None:
    """Вес базы вместе с WAL/SHM — в WAL может лежать заметный кусок несброшенных
    данных, так что считать только сам .db файл было бы враньём."""
    try:
        total = 0
        for suffix in ('', '-wal', '-shm'):
            f = Path(str(DB_PATH) + suffix)
            if f.exists():
                total += f.stat().st_size
        return round(total / 1e6, 1)
    except Exception:
        return None


def disk() -> tuple[float, float] | None:
    """(свободно, всего) в ГБ на диске, где лежит база. Каталог примонтирован с
    хоста, поэтому цифры — настоящие серверные, а не контейнерные."""
    try:
        u = shutil.disk_usage(Path(DB_PATH).parent)
        return round(u.free / 1e9, 1), round(u.total / 1e9, 1)
    except Exception:
        return None


def matches_line() -> str:
    n = db_matches()
    return f'В базе: **{n:,}** матчей.'.replace(',', ' ') if n is not None else ''


# Текущее состояние + heartbeat. Раньше статус писался только при смене фазы, а
# сбор идёт часами — счётчик в /collect status замерзал на числе, которое было
# на старте (совпадало с засеянным из релиза, потому и выглядело «опубликованным»).
# Теперь фоновый поток переписывает файл раз в HEARTBEAT секунд со свежим счётчиком.
HEARTBEAT = 30
_state: dict = {'state': 'starting'}
_state_lock = threading.Lock()


def _write_status():
    with _state_lock:
        data = dict(_state)
        base = _state.get('base_matches')
    data['at'] = datetime.now(timezone.utc).isoformat()
    n = db_matches()                 # всегда живое число из рабочей базы
    data['matches'] = n
    data['db_mb'] = db_size_mb()
    d = disk()
    if d:
        data['disk_free_gb'], data['disk_total_gb'] = d
    # Прирост с момента старта текущего ключа — видно, идёт сбор или встал.
    if base is not None and n is not None:
        data['this_key'] = n - base
    try:
        STATUS_FILE.write_text(json.dumps(data, ensure_ascii=False), encoding='utf-8')
    except Exception:
        pass


def set_status(**fields):
    with _state_lock:
        _state.clear()
        _state.update(fields)
    _write_status()


def _heartbeat_loop():
    while True:
        time.sleep(HEARTBEAT)
        _write_status()


def read_key() -> str | None:
    try:
        k = KEY_FILE.read_text(encoding='utf-8').strip()
        return k or None
    except FileNotFoundError:
        return None


def wait_for_key() -> str:
    """Ждёт ключ от бота. Просит его один раз, потом молча поллит."""
    asked = False
    while True:
        key = read_key()
        if key:
            return key
        if not asked:
            set_status(state='waiting_key')
            notify('🔑 Нужен свежий Riot API-ключ — пришли его командой '
                   f'`/collect key:RGAPI-…` (ключ живёт 24 ч).\n{matches_line()}')
            asked = True
        time.sleep(POLL)


def publish_db(session_total: int):
    if not GH_TOKEN:
        notify(f'✅ Круг сбора завершён: +{session_total} матчей. '
               '(Автопубликация выключена — нет GITHUB_TOKEN.)')
        return
    try:
        set_status(state='publishing')
        info = publish_data.publish(DB_PATH, GH_TOKEN)
        notify(f'📦 База обновлена в проде: +{session_total} матчей за круг · '
               f'патч {info["patch"]} · версия `{info["version"]}` · {info["size_mb"]} МБ')
        set_status(state='published', version=info['version'], patch=info['patch'])
    except Exception as e:
        notify(f'⚠️ Сбор прошёл (+{session_total}), но публикация упала: `{e}`')
        print(traceback.format_exc(), flush=True)


def main():
    CONTROL.mkdir(parents=True, exist_ok=True)
    # Предохранитель по диску. Порог — максимум из MIN_FREE_MB и 1.5× веса базы
    # (публикация делает полную копию), считается на лету внутри check_disk.
    collect.MIN_FREE_MB = int(os.environ.get('MIN_FREE_MB', '900'))
    collect._db_file = DB_PATH
    # Демон: статус остаётся свежим всё время сбора, а не только на переходах фаз.
    threading.Thread(target=_heartbeat_loop, daemon=True).start()
    collect.COMPLETED_ITEMS = collect.load_completed_items()

    regions = collect._parse_list(os.environ.get('REGIONS', 'all'),
                                  collect.REGION_PRIORITY, '--region')
    buckets = collect._parse_list(os.environ.get('BUCKETS', 'all'),
                                  collect.BUCKET_PRIORITY, '--tier')
    print(f'Коллектор запущен. Регионы: {regions} | Бакеты: {buckets} | БД: {DB_PATH}',
          flush=True)
    notify('🚀 Коллектор запущен и ждёт ключ.')

    while True:
        key = wait_for_key()
        # base_matches — точка отсчёта для «+N за текущий ключ» в heartbeat.
        set_status(state='collecting', regions=regions, buckets=buckets,
                   base_matches=db_matches())
        print('Ключ получен — старт сбора.', flush=True)
        notify('▶️ Ключ принят, сбор пошёл (регионы параллельно).')
        try:
            got = collect.run_continuous(key, DB_PATH, regions, buckets, DAYS, 0)
            # Круг пройден до конца — публикуем и ждём следующий ключ/запуск.
            publish_db(got or 0)
            KEY_FILE.unlink(missing_ok=True)
            set_status(state='idle', collected=got or 0)
        except DiskLow as e:
            # Место кончилось: публикуем собранное (на это запаса хватает — порог
            # ×1.5 от базы именно для этого) и ждём, пока освободят. Ключ НЕ
            # трогаем: как только место появится, сбор продолжится сам.
            got = getattr(e, 'collected', 0) or 0
            publish_db(got)
            notify(f'🛑 **Сбор остановлен: мало места на диске.**\n'
                   f'Свободно {e.free_mb} МБ, нужно ≥ {e.need_mb} МБ. '
                   f'За этот ключ собрано +{got}.\n{matches_line()}\n'
                   f'Собранное опубликовано. Освободи место — сбор продолжится сам.')
            set_status(state='disk_full', collected=got)
            # Ждём места. Ключ на руках, так что как освободится — сразу в бой.
            while True:
                time.sleep(60)
                try:
                    collect.check_disk()
                    break           # место появилось
                except DiskLow:
                    continue
            notify(f'✅ Место освободилось — продолжаю сбор.\n{matches_line()}')
        except KeyExpired as e:
            # Штатно: база уже сохранена внутри run_continuous. Публикуем всё,
            # что успели, чистим ключ и ждём новый.
            KEY_FILE.unlink(missing_ok=True)
            got = getattr(e, 'collected', 0) or 0
            publish_db(got)
            notify(f'⌛ Ключ истёк — за этот ключ собрано **+{got}** матчей.\n'
                   f'{matches_line()}\nПришли новый: `/collect key:RGAPI-…`')
            set_status(state='key_expired', collected=got)
        except Exception as e:
            print(traceback.format_exc(), flush=True)
            notify(f'❌ Сбор упал: `{type(e).__name__}: {e}`. Перезапущусь через минуту.')
            set_status(state='error', error=str(e))
            time.sleep(60)


if __name__ == '__main__':
    main()
