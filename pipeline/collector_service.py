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
import sqlite3
import sys
import time
import traceback
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))
import collect  # noqa: E402  (наш модуль сбора)
from collect import KeyExpired  # noqa: E402
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


def matches_line() -> str:
    n = db_matches()
    return f'В базе: **{n:,}** матчей.'.replace(',', ' ') if n is not None else ''


def set_status(**fields):
    fields['at'] = datetime.now(timezone.utc).isoformat()
    fields.setdefault('matches', db_matches())
    try:
        STATUS_FILE.write_text(json.dumps(fields, ensure_ascii=False), encoding='utf-8')
    except Exception:
        pass


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
        set_status(state='collecting', regions=regions, buckets=buckets)
        print('Ключ получен — старт сбора.', flush=True)
        notify('▶️ Ключ принят, сбор пошёл (регионы параллельно).')
        try:
            got = collect.run_continuous(key, DB_PATH, regions, buckets, DAYS, 0)
            # Круг пройден до конца — публикуем и ждём следующий ключ/запуск.
            publish_db(got or 0)
            KEY_FILE.unlink(missing_ok=True)
            set_status(state='idle', collected=got or 0)
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
