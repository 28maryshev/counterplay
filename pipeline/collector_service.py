"""
collector_service.py — демон сбора на сервере.

Работает бесконечно и ждёт ключ, который присылают из Discord (`/collect`).
Бот кладёт ключ в файл в общем томе — сервис его подхватывает, гонит сбор
параллельно по регионам и по завершении круга публикует базу в GitHub release
`data` (приложение обновится само по data-version.json).

Dev-ключ живёт 24 ч, поэтому истечение ключа — штатная ситуация, а не ошибка:
ловим 401/403, база уже сохранена, пишем в Discord «пришли новый ключ» и снова
ждём. Никакого простоя данных: собранное до момента протухания опубликовано.

Порядок здесь важнее, чем кажется: сначала просим ключ, потом публикуем. Пока
было наоборот, залипшая заливка (в requests не стоял таймаут) вешала весь цикл —
демон сутки стоял, не выложив базу и не сказав ни слова. Теперь у публикации
есть свой потолок времени, и цикл переживает её зависание.

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
    PUBLISH_TIMEOUT  потолок времени на публикацию, сек (по умолчанию 5400)
"""

import json
import os
import shutil
import sqlite3
import subprocess
import sys
import threading
import time
import traceback
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

import requests

sys.path.insert(0, str(Path(__file__).parent))
import collect  # noqa: E402  (наш модуль сбора)
from collect import KeyExpired, DiskLow  # noqa: E402
import publish_data  # noqa: E402
import ops_log  # noqa: E402  (журнал эксплуатации)

CONTROL = Path(os.environ.get('CONTROL_DIR', '/control'))
DB_PATH = os.environ.get('DB_PATH', str(Path(__file__).with_name('data.db')))
WEBHOOK = os.environ.get('DISCORD_WEBHOOK', '')
GH_TOKEN = os.environ.get('GITHUB_TOKEN', '')
DAYS = int(os.environ.get('COLLECT_DAYS', '30'))
KEY_FILE = CONTROL / 'key'
STATUS_FILE = CONTROL / 'status'
POLL = 15  # секунд между проверками файла ключа

# Свежий ключ Riot начинает работать не в ту же секунду, в которую его выдали:
# первые мгновения он ещё не разошёлся по их сети, и запрос получает тот же
# 401/403, что и по-настоящему истёкший. Отличить одно от другого можно по двум
# признакам сразу — круг не собрал НИ ОДНОГО матча, и ключ у нас считанные
# секунды. Такой ключ выбрасывать нельзя: человек присылает рабочий ключ, у него
# его тут же отнимают с просьбой прислать новый, он шлёт тот же самый — и только
# тогда всё идёт. Ровно это и было 20.09: принят в 09:09:11, «истёк» в 09:09:15.
KEY_WARMUP_SEC = 300      # столько после появления ключа отказ считаем «не прогрелся»
KEY_WARMUP_TRIES = 8      # попыток, прежде чем поверить в отказ
KEY_WARMUP_PAUSE = 25     # пауза между попытками
# Ниже этого срока жизни ключ считаем не своим полным: сутки Riot отсчитывает от
# выпуска, а не от того, когда ключ попал к нам. Два часа — с запасом: полный
# ключ живёт около суток, а остаток чужих суток обычно меряется минутами.
SHORT_KEY_SEC = 2 * 3600
# 401 НЕ РАВНО «ключ мёртв». Riot отдаёт его и на вполне живой ключ — на этом
# стоит ветка прогрева выше. 24.09 то же самое случилось уже в разгар сбора:
# ключ, выпущенный в 14:24 и действительный до следующего дня, получил 401 в
# 14:58, был выброшен как истёкший, и сбор встал на 22 часа. Поэтому прежде чем
# поверить в смерть ключа — переспрашиваем у Riot отдельным дешёвым запросом.
KEY_RECHECK_TRIES = 5
KEY_RECHECK_PAUSE = 60
KEY_PROBE_URL = 'https://euw1.api.riotgames.com/lol/status/v4/platform-data'
KEY_PROBE_TIMEOUT = 10
# Потолок времени на ЗАЛИВКУ. Это не «сколько она обычно идёт» (три минуты), а
# «когда считать, что она уже не закончится»: демон живёт на сервере без
# присмотра, и одна залипшая заливка однажды заморозила весь цикл — база не
# выложена, новый ключ не запрошен.
#
# Раньше этот потолок стоял на всю публикацию вместе со сборкой — и 2 октября
# оборвал медленную, но живую сборку через 90 минут. Поток при этом продолжил
# молотить диск фоном ещё пять часов, уже наперегонки со сбором. Теперь потолок
# только на сеть, а у сборки свой (BUILD_TIMEOUT).
PUBLISH_TIMEOUT = int(os.environ.get('PUBLISH_TIMEOUT', '5400'))

# Потолок времени на СБОРКУ тонких баз. Сборка локальная и в сеть не ходит —
# зависнуть ей не на чем, она бывает только медленной. Поэтому идёт она в
# основном потоке, сбор на это время стоит (иначе они делят один диск, и оба
# ползут), а этот потолок — страховка от совсем дурного случая: сбор не должен
# простоять полсуток. Сборка, в отличие от потока заливки, прерывается честно —
# SQLite обрывает запрос и ничего не оставляет висеть.
BUILD_TIMEOUT = int(os.environ.get('BUILD_TIMEOUT', '10800'))


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


def key_alive(key: str) -> bool | None:
    """Жив ли ключ — один дешёвый запрос к Riot, мимо всей логики сбора.

    True — Riot отвечает, ключ рабочий; False — отказ именно по ключу;
    None — спросить не вышло (сеть, таймаут), то есть вывода нет.

    Статус платформы выбран нарочно: он ничего не стоит по лимитам и не зависит
    от того, какой регион и бакет мы в этот момент вычерпывали.

    User-Agent обязателен. На запрос с подписью python-urllib край Riot отвечает
    403 независимо от ключа — проба на этом врала и объявляла живой ключ мёртвым.
    Про 403 поэтому и не делаем вывода: отказ по самому ключу — это 401
    (проверено на заведомо неверном ключе), а 403 приходит и от защиты края.
    """
    req = urllib.request.Request(KEY_PROBE_URL, headers={
        'X-Riot-Token': key, 'User-Agent': 'counterplay-collector'})
    try:
        with urllib.request.urlopen(req, timeout=KEY_PROBE_TIMEOUT) as r:
            return 200 <= r.status < 300
    except urllib.error.HTTPError as e:
        if e.code == 401:
            return False
        return None          # 403, 429, 5xx про сам ключ ничего не говорят
    except Exception:
        return None


def human_span(sec: float) -> str:
    """Срок по-человечески: «33 минуты», «22 ч 36 мин». Секунды не показываем —
    в сообщении о ключе важен порядок величины, а не точность."""
    m = int(sec // 60)
    if m < 60:
        return f'{m} мин'
    return f'{m // 60} ч {m % 60:02d} мин'


# Текущее состояние + heartbeat. Раньше статус писался только при смене фазы, а
# сбор идёт часами — счётчик в /collect status замерзал на числе, которое было
# на старте (совпадало с засеянным из релиза, потому и выглядело «опубликованным»).
# Теперь фоновый поток переписывает файл раз в HEARTBEAT секунд со свежим счётчиком.
HEARTBEAT = 30
# Как часто класть снимок в ops.db. Чаще незачем: снимок отвечает на вопрос
# «двигался ли сбор», а не «сколько ровно матчей было в 14:07».
SAMPLE_EVERY = 600
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
    return data


def set_status(**fields):
    with _state_lock:
        prev = _state.get('state')
        _state.clear()
        _state.update(fields)
    data = _write_status()
    # Смена фазы — событие, а не снимок: по этим строкам потом видно, когда
    # именно демон перешёл в публикацию, сколько простоял в ожидании ключа и так
    # далее. Одинаковые состояния подряд не пишем, иначе журнал заплывёт.
    if fields.get('state') != prev:
        ops_log.record('state', state=fields.get('state'), matches=data.get('matches'),
                       db_mb=data.get('db_mb'), disk_free_gb=data.get('disk_free_gb'))


def update_status(**fields):
    """Дописать поля статуса, НЕ трогая состояние демона.

    set_status заменяет статус целиком. Поздняя публикация доезжает, когда сбор
    уже идёт, и затёрла бы «collecting» своим «published» — бот и проверка
    хозяйства увидели бы демона не в той фазе."""
    with _state_lock:
        _state.update(fields)
    _write_status()


def _heartbeat_loop():
    last_sample = 0.0
    while True:
        time.sleep(HEARTBEAT)
        data = _write_status()
        now = time.monotonic()
        if now - last_sample >= SAMPLE_EVERY:
            last_sample = now
            ops_log.record('sample', state=data.get('state'), matches=data.get('matches'),
                           db_mb=data.get('db_mb'), disk_free_gb=data.get('disk_free_gb'),
                           this_key=data.get('this_key'))


def read_key() -> str | None:
    try:
        k = KEY_FILE.read_text(encoding='utf-8').strip()
        return k or None
    except FileNotFoundError:
        return None


def drop_key(used: str):
    """Убирает отработанный ключ — но только если это тот самый ключ.

    Публикация базы идёт минутами, и за это время бот может положить свежий
    ключ. Безусловный unlink стирал его молча: сбор не начинался, а человек
    видел лишь «пришли ключ» и не понимал, куда делся уже отправленный.
    """
    if read_key() == used:
        KEY_FILE.unlink(missing_ok=True)


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


# Ссылка на поток публикации: по ней видно, не завис ли прошлый заход.
_publishing: threading.Thread | None = None


def publish_dir() -> Path:
    """Рабочая папка публикации. Снимок (~1 ГБ) и тонкие базы строятся здесь, на
    ТОМЕ (диск), а не в /tmp контейнера — там tmpfs мал."""
    return Path(DB_PATH).parent / 'publtmp'


def clear_publish_dir():
    """Сносит остатки прошлых публикаций.

    Обычно tempfile убирает за собой сам, но прерванная публикация — упавшая,
    зависшая, убитая рестартом контейнера — оставляет там снимок базы и пять
    тонких: полтора гигабайта за раз. Два таких хвоста, и сбор встаёт по DiskLow,
    хотя удалить надо всего лишь мусор.
    """
    freed = 0
    try:
        for item in publish_dir().iterdir():
            if item.is_dir():
                freed += sum(f.stat().st_size for f in item.rglob('*') if f.is_file())
                shutil.rmtree(item, ignore_errors=True)
            else:
                freed += item.stat().st_size
                item.unlink(missing_ok=True)
    except FileNotFoundError:
        return
    except Exception as e:
        print(f'[уборка] остатки публикации не убрались: {e}', flush=True)
    if freed:
        print(f'[уборка] снесены остатки прошлой публикации: {freed / 1e6:.0f} МБ',
              flush=True)


def run_with_deadline(fn, seconds: int, on_late=None, on_late_error=None):
    """Выполняет fn в отдельном потоке и сдаётся, если тот не уложился в срок.

    Убить зависший поток в Python нельзя, но он демонский: цикл сбора поедет
    дальше, а собранное никуда не денется — выложим следующим кругом. Раньше на
    этом месте стоял обычный вызов, и одна залипшая заливка вешала демона насмерть.

    Сдавшись, мы не бросаем результат. Поток, не уложившийся в срок, часто всё же
    доделывает работу — так было 2 октября: дедлайн сработал, а база доехала до
    игроков через пять часов. Но сделала она это МОЛЧА: хвост публикации (объявить,
    записать, позвать обновление сайта) стоял после вызова, до которого дело уже
    не дошло. Теперь поздний исход передаётся в on_late / on_late_error.

    Кто «сдался», а кто «доделал», решается под замком: иначе поток мог закончить
    ровно между проверкой и пометкой, и результат не достался бы никому.
    """
    global _publishing
    box: dict = {}
    lock = threading.Lock()

    def runner():
        try:
            res, err = fn(), None
        except BaseException as e:     # пробросим в вызывающий поток как есть
            res, err = None, e
        with lock:
            box['done'], box['ok'], box['err'] = True, res, err
            late = box.get('abandoned', False)
        if not late:
            return
        # Вызывающий уже сдался — исход достаётся нам.
        try:
            if err is None:
                if on_late:
                    on_late(res)
            elif on_late_error:
                on_late_error(err)
        except Exception as e:
            print(f'[публикация] поздний хвост не отработал: {e}', flush=True)

    t = threading.Thread(target=runner, daemon=True, name='publish')
    _publishing = t
    t.start()
    t.join(seconds)
    with lock:
        if not box.get('done'):
            box['abandoned'] = True
            limit = f'{seconds // 60} мин' if seconds >= 60 else f'{seconds} с'
            raise TimeoutError(f'заливка идёт дольше {limit}')
    if box['err'] is not None:
        raise box['err']
    return box['ok']


def _announce_published(info: dict, session_total: int, started: float, late: bool = False,
                        why: str = 'round'):
    """Всё, что делается после удачной публикации: объявить, записать, позвать
    обновление сайта. Одно место на оба исхода — вовремя и с опозданием, — чтобы
    поздний успех не оказался беднее обычного, как 2 октября."""
    minutes = round((time.monotonic() - started) / 60)
    buckets = info.get('buckets', {})
    bsizes = ' · '.join(f'{b} {buckets[b]["size_mb"]}МБ' for b in buckets)
    head = (f'📦 База обновлена в проде с опозданием (публикация шла {minutes} мин)'
            if late else '📦 База обновлена в проде')
    notify(f'{head}: +{session_total} матчей {PUBLISH_WHY[why]} · '
           f'патч {info["patch"]} · версия `{info["version"]}` · '
           f'тонкая {info.get("slim_mb", "?")}МБ'
           + (f'\nПо эло: {bsizes}' if bsizes else ''))
    if late:
        # Сбор к этому времени уже идёт — фазу демона не трогаем.
        update_status(version=info['version'], patch=info['patch'])
    else:
        set_status(state='published', version=info['version'], patch=info['patch'])
    ops_log.record('publish', state=None if late else 'published',
                   matches=db_matches(), db_mb=db_size_mb(),
                   patch=info['patch'], version=info['version'],
                   slim_mb=info.get('slim_mb'),
                   buckets={b: buckets[b]['size_mb'] for b in buckets},
                   collected=session_total, minutes=minutes, late=late)
    request_site_update(info['patch'])


# Повод публикации — в словах сообщений. 5 октября на истёкшем ключе бот написал
# «Круг завершён», а через полтора часа «+N за круг» — и это прочли как второй
# круг, хотя то были начало и конец одной публикации.
PUBLISH_WHY = {
    'round': 'за круг',             # круг пройден, ключ жив и пойдёт дальше
    'key': 'за истёкший ключ',      # Riot отказал, ждём новый
    'disk': 'до остановки по диску',
}


def publish_db(session_total: int, why: str = 'round'):
    if not GH_TOKEN:
        notify(f'✅ Сбор {PUBLISH_WHY[why]}: +{session_total} матчей. '
               '(Автопубликация выключена — нет GITHUB_TOKEN.)')
        return
    if _publishing is not None and _publishing.is_alive():
        # Прошлая заливка ещё не отпустила. Второй заход заливал бы те же
        # имена ассетов вперемешку с ней — в релизе оказался бы манифест от
        # одной базы и файлы от другой.
        notify('⏭ Прошлая публикация ещё идёт — эту пропускаю, '
               'база выложится следующим кругом.')
        return

    started = time.monotonic()

    def late_ok(info):
        _announce_published(info, session_total, started, late=True, why=why)

    def late_err(e):
        notify(f'⚠️ Заливка, которая шла фоном, так и не прошла: `{e}`. '
               'База выложится следующим кругом.')
        ops_log.record('publish_failed', error=str(e)[:200], collected=session_total, late=True)

    try:
        set_status(state='publishing')
        # Сколько займёт — не обещаем: на этой машине сборка идёт то 6 минут, то
        # полтора часа, смотря по остатку разгона диска (журнал, 5 октября).
        # На истёкшем ключе молчим: о публикации уже сказано в просьбе о ключе.
        if why == 'round':
            notify(f'📦 Круг завершён (+{session_total}) — публикую базу; '
                   'сбор тем же ключом продолжится после неё.')
        elif why == 'disk':
            notify(f'📦 Кончается место на диске — публикую собранное '
                   f'(+{session_total}) и останавливаю сбор.')
        clear_publish_dir()            # хвосты прошлого захода — до, а не после
        tmp = publish_dir()
        tmp.mkdir(exist_ok=True)
        os.environ['TMPDIR'] = str(tmp)
        # Сборка — здесь, в основном потоке: сбор на это время стоит и диск не
        # делит. Заливка — под дедлайном, в своём потоке.
        info = publish_data.publish(
            DB_PATH, GH_TOKEN,
            run_upload=lambda job: run_with_deadline(job, PUBLISH_TIMEOUT,
                                                     on_late=late_ok, on_late_error=late_err),
            workdir=tmp, build_timeout=BUILD_TIMEOUT)
        _announce_published(info, session_total, started, why=why)
    except publish_data.BuildTimeout as e:
        notify(f'⚠️ Сбор прошёл (+{session_total}), но сборка базы не уложилась в '
               f'{BUILD_TIMEOUT // 3600} ч и прервана. Сбор продолжаю, база выложится '
               'следующим кругом.')
        ops_log.record('publish_failed', error=str(e)[:200], collected=session_total)
    except TimeoutError as e:
        # Заливка не уложилась в срок, но поток её жив и, скорее всего, доедет:
        # тогда о результате сообщит late_ok. Писать «упала» здесь — неправда,
        # именно так 2 октября выглядела публикация, которая на деле прошла.
        notify(f'⏳ Сбор прошёл (+{session_total}), но {e} — сбор продолжаю, '
               'заливка идёт фоном, о результате сообщу отдельно.')
        ops_log.record('publish_slow', error=str(e)[:200], collected=session_total)
    except Exception as e:
        notify(f'⚠️ Сбор прошёл (+{session_total}), но публикация упала: `{e}`')
        print(traceback.format_exc(), flush=True)
        ops_log.record('publish_failed', error=str(e)[:200], collected=session_total)
    finally:
        # Уборка идёт СРАЗУ ПОСЛЕ публикации — и по времени, и по смыслу. Сбор в
        # этот момент стоит, значит можно сжать файл: VACUUM берёт эксклюзивную
        # блокировку, посреди сбора его звать нельзя. А нужен он регулярно: без
        # него база пухнет, и каждая следующая сборка читает больше.
        # Не уложившаяся в срок заливка — исключение: её поток ещё жив, а
        # отложенная уборка случится после следующей публикации.
        if _publishing is None or not _publishing.is_alive():
            prune_db()


def request_site_update(patch: str):
    """Просит хост обновить данные сайта: тир-лист, контрпики и руны.

    Сам контейнер этого не делает — ему для этого понадобился бы ключ от сервера
    сайта. Вместо этого оставляем отметку в общем каталоге control, а сторож на
    хосте (site_watch.sh, раз в 5 минут) её видит и запускает выкладку. Раньше
    выгрузки обновлялись руками, и сайт месяц показывал прошлый патч."""
    try:
        (CONTROL / 'site-publish').write_text(patch, encoding='utf-8')
        print(f'сайт: отметка на обновление данных (патч {patch})', flush=True)
    except Exception as e:
        print(f'сайт: не удалось оставить отметку — {e}', flush=True)


def prune_db():
    """Чистка старых данных со сжатием файла. Ошибки глушим: не убравшаяся база
    мешает жить позже, а упавший сбор — прямо сейчас."""
    try:
        before = os.path.getsize(DB_PATH) / 1048576
        set_status(state='pruning')
        env = {**os.environ, 'DB_PATH': DB_PATH, 'PRUNE_VACUUM': '1', 'PRUNE_MATCHES': '1'}
        out = subprocess.run([sys.executable, str(Path(__file__).with_name('prune_db.py'))],
                             env=env, capture_output=True, text=True, timeout=3600)
        tail = (out.stdout or out.stderr or '').strip().splitlines()
        after = os.path.getsize(DB_PATH) / 1048576
        for line in tail:
            print(f'[уборка] {line}', flush=True)
        ops_log.record('prune', db_mb=round(after, 1),
                       before_mb=round(before, 1), after_mb=round(after, 1))
        ops_log.prune()          # заодно подрезаем старые снимки в ops.db
        if before - after > 50:
            free = disk()
            notify(f'🧹 Уборка базы: {before:,.0f} → {after:,.0f} МБ '
                   f'(освободилось {before - after:,.0f} МБ)'
                   + (f' · свободно на диске {free[0]:.1f} ГБ' if free else ''))
    except Exception as e:
        print(f'[уборка] не удалась: {e}', flush=True)


def main():
    CONTROL.mkdir(parents=True, exist_ok=True)
    # Предохранитель по диску. Порог — максимум из MIN_FREE_MB и 1.5× веса базы
    # (публикация делает полную копию), считается на лету внутри check_disk.
    collect.MIN_FREE_MB = int(os.environ.get('MIN_FREE_MB', '900'))
    collect._db_file = DB_PATH
    # Демон: статус остаётся свежим всё время сбора, а не только на переходах фаз.
    threading.Thread(target=_heartbeat_loop, daemon=True).start()
    collect.COMPLETED_ITEMS = collect.load_completed_items()
    ops_log.setup(DB_PATH)
    ops_log.record('start', matches=db_matches(), db_mb=db_size_mb())
    # Перезапуск посреди публикации оставляет на томе снимок базы и тонкие копии.
    clear_publish_dir()

    regions = collect._parse_list(os.environ.get('REGIONS', 'all'),
                                  collect.REGION_PRIORITY, '--region')
    buckets = collect._parse_list(os.environ.get('BUCKETS', 'all'),
                                  collect.BUCKET_PRIORITY, '--tier')
    print(f'Коллектор запущен. Регионы: {regions} | Бакеты: {buckets} | БД: {DB_PATH}',
          flush=True)
    notify('🚀 Коллектор запущен и ждёт ключ.')

    last_key = None
    key_at = 0.0    # когда этот ключ у нас появился — см. KEY_WARMUP_SEC
    warmups = 0     # сколько раз он ответил отказом, ещё не разойдясь по сети Riot
    rechecks = 0    # сколько раз переспросили Riot про отказ посреди сбора
    while True:
        key = wait_for_key()
        # base_matches — точка отсчёта для «+N за текущий ключ» в heartbeat.
        set_status(state='collecting', regions=regions, buckets=buckets,
                   base_matches=db_matches())
        if key != last_key:
            print('Ключ получен — старт сбора.', flush=True)
            notify('▶️ Ключ принят, сбор пошёл (регионы параллельно).')
            ops_log.record('key_accepted', matches=db_matches())
            last_key = key
            key_at = time.time()
            warmups = 0
            rechecks = 0
        else:
            print('Следующий круг тем же ключом.', flush=True)
        try:
            got = collect.run_continuous(key, DB_PATH, regions, buckets, DAYS, 0)
            rechecks = 0    # круг дошёл до конца — прошлые отказы были случайными
            # Круг пройден до конца. Ключ живёт сутки, а круг — часы: выбрасывать
            # ещё живой ключ и ждать человека значит стоять без дела полдня.
            # Публикуем и идём на следующий круг тем же ключом; уберёт его ветка
            # KeyExpired, когда Riot откажет.
            publish_db(got or 0)
            set_status(state='idle', collected=got or 0)
            if not got:
                # Свежих матчей не нашлось — не крутим круги вхолостую.
                time.sleep(600)
        except DiskLow as e:
            # Место кончилось: публикуем собранное (на это запаса хватает — порог
            # ×1.5 от базы именно для этого) и ждём, пока освободят. Ключ НЕ
            # трогаем: как только место появится, сбор продолжится сам.
            got = getattr(e, 'collected', 0) or 0
            publish_db(got, why='disk')
            notify(f'🛑 **Сбор остановлен: мало места на диске.**\n'
                   f'Свободно {e.free_mb} МБ, нужно ≥ {e.need_mb} МБ. '
                   f'За этот ключ собрано +{got}.\n{matches_line()}\n'
                   f'Собранное опубликовано. Освободи место — сбор продолжится сам.')
            set_status(state='disk_full', collected=got)
            ops_log.record('disk_low', collected=got, matches=db_matches(),
                           free_mb=e.free_mb, need_mb=e.need_mb)
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
            # Штатно: база уже сохранена внутри run_continuous.
            got = getattr(e, 'collected', 0) or 0

            # Ключ ещё не прогрелся, а не истёк: ни одного матча и на руках он
            # считанные минуты. Не выбрасываем — ждём и пробуем тем же ключом.
            if (got == 0 and warmups < KEY_WARMUP_TRIES
                    and time.time() - key_at < KEY_WARMUP_SEC):
                warmups += 1
                print(f'[{e.code}] Riot ещё не принимает ключ — '
                      f'попытка {warmups} из {KEY_WARMUP_TRIES}.', flush=True)
                ops_log.record('key_warmup', matches=db_matches(),
                               code=e.code, attempt=warmups)
                set_status(state='key_warmup', attempt=warmups)
                # Говорим один раз: молчать нельзя (человек ждёт сбора), но и
                # повторять на каждой попытке незачем.
                if warmups == 1:
                    notify('⏳ Riot пока не принимает этот ключ — свежий ключ '
                           'расходится по их сети не мгновенно. Подожду и '
                           'попробую ещё; присылать новый не нужно.')
                time.sleep(KEY_WARMUP_PAUSE)
                continue

            # Отказ пришёл в разгар сбора. Прежде чем выбрасывать ключ — спросим
            # у Riot напрямую, мёртв ли он. Этого шага не было 24.09, и живой
            # ключ с 22 часами впереди отправился в мусор после одного 401.
            alive = key_alive(key)
            if alive is not False and rechecks < KEY_RECHECK_TRIES:
                rechecks += 1
                why = 'ключ отвечает' if alive else 'спросить не вышло'
                print(f'[{e.code}] Отказ, но {why} — переспрошу тем же ключом, '
                      f'попытка {rechecks} из {KEY_RECHECK_TRIES}.', flush=True)
                ops_log.record('key_recheck', matches=db_matches(), code=e.code,
                               attempt=rechecks, alive=alive, collected=got)
                set_status(state='key_recheck', attempt=rechecks, collected=got)
                if rechecks == 1:
                    notify(f'⚠️ Riot отказал по ключу ({e.code}), но сам ключ '
                           f'{"ещё отвечает" if alive else "проверить не удалось"} — '
                           f'похоже на сбой, а не на конец суток. Собрано за него '
                           f'**+{got}**. Пробую тем же ключом, новый пока не нужен.')
                time.sleep(KEY_RECHECK_PAUSE)
                continue

            # Просьба о новом ключе идёт ПЕРЕД публикацией, а не после. Публикация
            # занимает минуты, и всё это время человек не знал, что от него ждут
            # ключ; а когда она однажды залипла на заливке, не узнал вовсе —
            # демон молча простоял сутки. Сбор без ключа всё равно не продолжить,
            # так что пусть новый ключ едет навстречу выкладке.
            #
            # Срок жизни в сообщении — чтобы короткий ключ было видно сразу:
            # сутки Riot считает от ВЫПУСКА, а портал показывает прежний ключ,
            # пока не нажать Regenerate, так что скопированный с экрана ключ
            # может принести лишь остаток чужих суток.
            lived = time.time() - key_at if key_at else 0.0
            drop_key(key)
            set_status(state='key_expired', collected=got, lived_min=round(lived / 60))
            ops_log.record('key_expired', collected=got, matches=db_matches(),
                           lived_min=round(lived / 60))
            notify(f'⌛ Ключ истёк — прожил {human_span(lived)}, '
                   f'собрано **+{got}** матчей.\n'
                   + (f'⚠️ Это мало: сутки ключа идут от его выпуска. Похоже, '
                      f'скопирован ключ, выпущенный раньше. Нажми на портале '
                      f'**Regenerate API Key** и пришли новый — тогда он '
                      f'отработает полные сутки.\n'
                      if lived < SHORT_KEY_SEC else '')
                   + f'{matches_line()}\nПришли новый: `/collect key:RGAPI-…` — '
                   f'собранное сейчас публикую.')
            publish_db(got, why='key')
        except Exception as e:
            print(traceback.format_exc(), flush=True)
            notify(f'❌ Сбор упал: `{type(e).__name__}: {e}`. Перезапущусь через минуту.')
            set_status(state='error', error=str(e))
            time.sleep(60)


if __name__ == '__main__':
    main()
