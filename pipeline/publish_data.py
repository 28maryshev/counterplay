"""
publish_data.py — выкладывает базы в GitHub release `data`.

Программа читает из базы ТОЛЬКО движок пиков/банов (base_wr, matchup, synergy,
botlane_matchup, champion_bans, champion_damage). Руны/сборки берутся с сайта (/api/stats), а
drafts/processed_matches и таблицы рун движку не нужны. Поэтому публикуем ТОНКУЮ
базу: только эти 5 таблиц, за последние KEEP_PATCHES патчей, без «длинного
хвоста» (пары с 1–2 играми никогда не показываются). Это режет ~930 МБ → ~150 МБ.

Плюс сплит по эло: на каждый бакет своя маленькая база (data-<bucket>.db, ~50–70
МБ) — новая программа качает только свой ранг. Общую тонкую data.db тоже кладём
(для старых версий и как фолбэк).

Ассеты:
    data.db              — тонкая, все бакеты (совместимость со старой программой)
    data-<bucket>.db     — тонкая, один бакет (новая программа по рангу)
    data-version.json    — {version, patch, updated, buckets:{b:{version,size_mb}}}

Запуск:  python pipeline/publish_data.py [--db pipeline/data.db]
Токен:   GITHUB_TOKEN (scope: Contents read/write). TMPDIR — на диске (не tmpfs)!
"""

import argparse
import hashlib
import gzip
import json
import os
import shutil
import sqlite3
import sys
import tempfile
import time
from datetime import datetime, timezone
from pathlib import Path

import requests

import r2
from freshness import all_patches

REPO = os.environ.get('GITHUB_REPO', '28maryshev/counterplay')
API = 'https://api.github.com'
UPLOADS = 'https://uploads.github.com'
TAG = 'data'

# Таймауты (соединение, тишина в сокете) и повторы. Без них requests ждёт вечно:
# залипшая заливка со стороны GitHub вешала публикацию НАСОВСЕМ, а с ней и весь
# цикл коллектора — база не выкладывалась, новый ключ не запрашивался, и понять
# это можно было, только зайдя на сервер. Таймаут считается по бездействию, а не
# по всей заливке, так что медленный, но живой канал не обрывается.
TIMEOUT = (15, 120)
UPLOAD_TIMEOUT = (15, 300)
RETRIES = 3

# Таблицы, которые реально читает движок программы (RecommendationEngine.cs).
ENGINE_TABLES = ['base_wr', 'matchup', 'synergy', 'botlane_matchup', 'champion_bans',
                 'champion_damage']
BUCKETS = ['silver', 'gold', 'emerald', 'master']
KEEP_PATCHES = 3     # движок считает по 2 свежим, третий нужен ему для «удержания»
                     # нового патча (RecommendationEngine.PatchReady). Больше класть
                     # незачем: замер показал, что четвёртый и пятый патчи — 38% веса
                     # файла, до которых расчёт не доходит.

# Парные таблицы, для которых кладём ещё и сводку по всем дивизионам.
PAIR_KEYS = {
    'matchup':         'champion_id, role, vs_champion_id',
    'synergy':         'champion_id, role, ally_id, ally_role',
    'botlane_matchup': 'champion_id, role, vs_champion_id, vs_role',
}
# «Длинный хвост»: пары с малым числом игр никогда не показываются — не кладём.
# base_wr и champion_bans оставляем полностью (знаменатели пик/бан-рейта).
PRUNE_MIN = {'matchup': 2, 'synergy': 2, 'botlane_matchup': 2}


class BuildTimeout(Exception):
    """Сборка не уложилась в отведённое время и прервана — чисто, без хвостов."""


def _ro_uri(path) -> str:
    """Адрес базы для ATTACH «только чтение».

    Через URI, потому что только так SQLite открывает присоединённую базу без права
    записи. as_uri() сам экранирует путь и годится и для Linux, и для Windows (там
    запускают ручную публикацию).
    """
    return Path(path).resolve().as_uri() + '?mode=ro'


def _step(what: str, t0: float):
    """Строка в лог о шаге сборки. 2 октября сборка шла 6,5 часа без единой
    строки, и понять, на чём она стоит, было нельзя — теперь видно каждый шаг."""
    print(f'[публикация] сборка: {what} ({time.monotonic() - t0:.0f} с)', flush=True)


def build_slim(full_path: Path, dest_path: Path, patches, bucket=None, matches=0,
               deadline: float | None = None):
    """Тонкая база: только ENGINE_TABLES, за patches, без длинного хвоста,
    опционально по одному бакету. processed_matches не кладём (движку не нужна) —
    но общее число матчей пишем в служебную db_meta, чтобы бот показывал счётчик
    без 37 МБ таблицы.

    Источник читается НАПРЯМУЮ, только на чтение, внутри одной транзакции. Раньше
    перед этим снималась полная копия базы — 3,3 ГБ прочитать и столько же
    записать, — и на машине с 256 МБ памяти контейнера это была большая часть
    всей публикации. Копия была нужна для согласованности, но её даёт и сама
    SQLite: в режиме WAL одна читающая транзакция видит один срез, даже если рядом
    пишут. А во время публикации сбор и так стоит.

    deadline — момент (time.monotonic), после которого сборка прерывается.
    Прервать её можно честно, в отличие от потока заливки: SQLite останавливает
    запрос по сигналу обработчика прогресса, и ничего не остаётся висеть.
    """
    if dest_path.exists():
        dest_path.unlink()
    # uri=True — иначе ATTACH 'file:...' создал бы файл с таким именем, а не открыл
    # базу. Собственный путь назначения без префикса file: остаётся обычным путём.
    dst = sqlite3.connect(str(dest_path), uri=True)
    # Соединение закрываем ВСЕГДА: прерванная сборка иначе держит файл открытым —
    # на Windows его не удалить, а на Linux удалённый, но открытый файл держит
    # место на диске, которого на коллекторе и так в обрез.
    try:
        if deadline is not None:
            # Возврат «правды» из обработчика обрывает текущий запрос. Зовётся раз в
            # сотню тысяч шагов виртуальной машины SQLite — на скорость не влияет.
            dst.set_progress_handler(lambda: time.monotonic() > deadline, 100_000)
        dst.execute('ATTACH DATABASE ? AS src', (_ro_uri(full_path),))
        # Явная транзакция: все чтения источника — из одного среза, а не из того, что
        # успело оказаться в нём к очередной таблице.
        dst.execute('BEGIN')
        ph = ','.join('?' * len(patches))
        for t in ENGINE_TABLES:
            row = dst.execute(
                "SELECT sql FROM src.sqlite_master WHERE type='table' AND name=?", (t,)).fetchone()
            if not row:
                continue
            dst.execute(row[0])  # тот же DDL
            conds = [f'patch IN ({ph})']
            params = list(patches)
            if bucket:
                conds.append('tier_bucket=?')
                params.append(bucket)
            if t in PRUNE_MIN:
                conds.append('games>=?')
                params.append(PRUNE_MIN[t])
            dst.execute(f"INSERT INTO {t} SELECT * FROM src.{t} WHERE {' AND '.join(conds)}", params)
        # Сводка пар по ВСЕМ дивизионам — приор для разреженных пар.
        #
        # Данные по парам «чемпион против чемпиона» тонкие: в своём бакете у половины
        # пар меньше 15 игр, и движок обрезает такую дельту до пятой части. Соседние
        # дивизионы про ту же пару кое-что знают, но в бакетной базе их просто нет.
        # Кладём их одной строкой на пару (tier_bucket='all', patch='all'): по патчам
        # разбивать смысла нет — это приор, а не источник меты, зато файл прибавляет
        # не 108%, а 43% от парных таблиц.
        #
        # Метки 'all' выбраны так, чтобы старые сборки программы этих строк не увидели:
        # каждый их запрос фильтрует и бакет, и патч по конкретным значениям.
        if bucket:
            for t, key in PAIR_KEYS.items():
                cols = [c[1] for c in dst.execute(f'PRAGMA table_info({t})')]
                if not cols:
                    continue
                sel = ', '.join("'all'" if c in ('tier_bucket', 'patch')
                                else f'SUM({c})' if c in ('games', 'wins') else c
                                for c in cols)
                conds = [f'patch IN ({ph})']
                params = list(patches)
                if t in PRUNE_MIN:
                    conds.append('games>=?')
                    params.append(PRUNE_MIN[t])
                dst.execute(f"INSERT INTO {t} ({', '.join(cols)}) SELECT {sel} FROM src.{t} "
                            f"WHERE {' AND '.join(conds)} GROUP BY {key}", params)

        # Служебные метаданные (число матчей, патчи, бакет).
        dst.execute('CREATE TABLE db_meta (key TEXT PRIMARY KEY, value TEXT)')
        dst.executemany('INSERT INTO db_meta VALUES (?,?)', [
            ('matches', str(matches)),
            ('patch', patches[0] if patches else '0.0'),
            ('patches', ','.join(patches)),
            ('bucket', bucket or 'all'),
        ])
        dst.commit()
        dst.execute('DETACH DATABASE src')
        dst.execute('VACUUM')
    finally:
        dst.close()


def _guarded(fn, deadline):
    """Вызов шага сборки с переводом «прервано по сроку» в BuildTimeout.

    SQLite сообщает об обрыве обычной OperationalError «interrupted»; отличаем её
    от настоящих ошибок по самому сроку, а не по тексту сообщения."""
    try:
        return fn()
    except sqlite3.OperationalError:
        if deadline is not None and time.monotonic() > deadline:
            raise BuildTimeout('сборка не уложилась в отведённое время — прервана')
        raise


def latest_patch(db: Path) -> str:
    con = sqlite3.connect(f'file:{db}?mode=ro', uri=True)
    try:
        ps = all_patches(con)
        return ps[0] if ps else '0.0'
    finally:
        con.close()


def sha16(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()[:16].upper()


def _session(token: str) -> requests.Session:
    s = requests.Session()
    s.headers.update({'Authorization': f'Bearer {token}',
                      'Accept': 'application/vnd.github+json'})
    return s


def _checked(r: requests.Response) -> requests.Response:
    r.raise_for_status()
    return r


def _soft(r: requests.Response) -> requests.Response:
    """Ответ как есть, но 5xx превращаем в исключение — их вызывающий разбирать
    не должен, их должен повторить _retry."""
    if r.status_code >= 500:
        r.raise_for_status()
    return r


def _retry(what: str, fn):
    """Повтор сетевой операции. Обрыв, таймаут и 5xx у GitHub — обычное дело на
    заливке сотен мегабайт; терять из-за них готовую базу и ждать следующего
    круга незачем. Ответы 4xx повторять бессмысленно — пробрасываем сразу."""
    last = ''
    for attempt in range(1, RETRIES + 1):
        try:
            return fn()
        except (requests.Timeout, requests.ConnectionError) as e:
            last = f'{type(e).__name__}: {str(e)[:120]}'
        except requests.HTTPError as e:
            code = e.response.status_code if e.response is not None else 0
            if code < 500:
                raise
            last = f'HTTP {code}'
        if attempt == RETRIES:
            raise RuntimeError(f'{what}: не вышло за {RETRIES} попытки ({last})')
        wait = 15 * attempt
        print(f'[публикация] {what} — {last}; повтор через {wait}s '
              f'(попытка {attempt} из {RETRIES})', flush=True)
        time.sleep(wait)


def ensure_release(s: requests.Session) -> int:
    r = _retry('чтение релиза',
               lambda: _soft(s.get(f'{API}/repos/{REPO}/releases/tags/{TAG}', timeout=TIMEOUT)))
    if r.status_code == 200:
        return r.json()['id']
    if r.status_code in (401, 403):
        raise RuntimeError(
            f'GitHub отказал ({r.status_code}) — проверь GITHUB_TOKEN '
            f'(протух или нет прав Contents: read/write): {r.text[:200]}')
    if r.status_code != 404:
        raise RuntimeError(f'GitHub releases/tags/{TAG} -> {r.status_code}: {r.text[:200]}')

    # Релиза нет — создаём. 422 здесь означает «уже существует» (гонка или тег
    # есть, а GET его не отдал): не падаем, а перечитываем релиз по тегу.
    r = _retry('создание релиза', lambda: _soft(s.post(f'{API}/repos/{REPO}/releases', json={
        'tag_name': TAG, 'name': 'Data', 'body': 'Central database, updated each patch'},
        timeout=TIMEOUT)))
    if r.status_code == 422:
        again = s.get(f'{API}/repos/{REPO}/releases/tags/{TAG}', timeout=TIMEOUT)
        if again.status_code == 200:
            return again.json()['id']
        raise RuntimeError(f'Релиз {TAG} не создать и не прочитать: {r.text[:200]}')
    r.raise_for_status()
    return r.json()['id']


def _asset_id(s: requests.Session, release_id: int, name: str):
    r = _checked(s.get(f'{API}/repos/{REPO}/releases/{release_id}/assets', timeout=TIMEOUT))
    for a in r.json():
        if a['name'] == name:
            return a['id']
    return None


def gzip_file(src: Path) -> Path:
    """Сжать рядом, вернуть путь к архиву.

    База сжимается в 3.5 раза (112 МБ изумруда -> 32 МБ), а маршрут до GitHub из
    России гуляет от 0.4 до 6 МБ/с: на плохой минуте это разница между
    полуминутой и тремя. Уровень 6 — обычный компромисс, 9 даёт единицы
    процентов за втрое большее время.

    mtime обнуляем: иначе каждый прогон давал бы новый архив при том же
    содержимом, и GitHub заливал бы 32 МБ впустую.
    """
    dst = src.with_suffix(src.suffix + '.gz')
    with open(src, 'rb') as fi, gzip.GzipFile(dst, 'wb', compresslevel=6, mtime=0) as fo:
        shutil.copyfileobj(fi, fo, 1024 * 1024)
    return dst


def upload_gz(s: requests.Session, release_id: int, path: Path, name: str):
    """Сжать, залить и СРАЗУ убрать архив.

    На сервере коллектора памяти 256 МБ и место на диске на счету, а во временной
    папке уже лежат тонкая база и четыре побакетных.
    """
    gz = gzip_file(path)
    try:
        upload_asset(s, release_id, gz, name + '.gz')
    finally:
        gz.unlink(missing_ok=True)


def publish_r2(slim: Path, bucket_files: dict, vfile: Path):
    """Выложить базы в R2. Не настроен — молча пропускаем.

    Сбой заливки НЕ роняет публикацию: в GitHub всё уже лежит, и программа
    откатится на него сама. Молчать при этом нельзя — пишем в вывод.
    """
    if not r2.configured():
        print('[R2] не настроен — пропускаю', flush=True)
        return
    try:
        files = [(slim, 'data.db')] + [(bf, f'data-{b}.db') for b, bf in bucket_files.items()]
        for path, name in files:
            gz = gzip_file(path)
            try:
                print(f'[R2] заливаю {name}.gz ({gz.stat().st_size / 1e6:.1f} МБ)…', flush=True)
                r2.put(gz, name + '.gz')
            finally:
                gz.unlink(missing_ok=True)   # место на диске на счету
        # Номер версии — последним, см. вызов.
        r2.put(vfile, 'data-version.json', 'application/json', cache_seconds=60)
        print('[R2] готово', flush=True)
    except Exception as e:
        print(f'[R2] НЕ ВЫШЛО: {type(e).__name__}: {e}', flush=True)


def upload_asset(s: requests.Session, release_id: int, path: Path, name: str):
    """Заливает ассет, снося прежний с тем же именем (--clobber у gh).

    Снос делается на КАЖДОЙ попытке: после оборванной заливки в релизе остаётся
    огрызок с этим же именем, и на повтор GitHub отвечает 422 «уже существует» —
    то есть первая же сетевая икота хоронила бы всю публикацию.
    """
    def send():
        aid = _asset_id(s, release_id, name)
        if aid is not None:
            _checked(s.delete(f'{API}/repos/{REPO}/releases/assets/{aid}', timeout=TIMEOUT))
        with open(path, 'rb') as f:
            return _checked(s.post(f'{UPLOADS}/repos/{REPO}/releases/{release_id}/assets',
                                   params={'name': name},
                                   headers={'Content-Type': 'application/octet-stream'},
                                   data=f, timeout=UPLOAD_TIMEOUT))

    mb = round(path.stat().st_size / 1e6, 1)
    print(f'[публикация] заливаю {name} ({mb} МБ)…', flush=True)
    _retry(f'заливка {name}', send)


def build(db_path: str, tmp: Path, build_timeout: float | None = None) -> dict:
    """ЛОКАЛЬНАЯ часть публикации: тонкие базы и манифест в папке tmp.

    Отделена от заливки, потому что у них разные беды. Заливка ходит в сеть и
    однажды повисла насовсем (14.09) — на неё нужен дедлайн. Сборка в сеть не
    ходит и не виснет, она бывает медленной: 2 октября шла 6,5 часа. Обрывать её
    дедлайном заливки значило бросать поток, который продолжает молотить диск
    фоном, — поэтому у сборки свой потолок, и прерывается она честно.
    """
    db = Path(db_path)
    if not db.exists():
        raise FileNotFoundError(f'База не найдена: {db}')

    t0 = time.monotonic()
    deadline = t0 + build_timeout if build_timeout else None

    src = sqlite3.connect(_ro_uri(db), uri=True)
    try:
        patches = all_patches(src)[:KEEP_PATCHES]
        matches = src.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]
    finally:
        src.close()
    patch = patches[0] if patches else '0.0'
    _step(f'патчи {", ".join(patches)}, матчей {matches}', t0)

    # Тонкая общая база (совместимость со старой программой) — прямо из рабочей.
    slim = tmp / 'data.db'
    _guarded(lambda: build_slim(db, slim, patches, bucket=None, matches=matches,
                                deadline=deadline), deadline)
    _step(f'тонкая общая готова, {slim.stat().st_size / 1e6:.0f} МБ', t0)

    # Побакетные тонкие базы — из УЖЕ отфильтрованной тонкой базы, а не из
    # рабочей. Строки те же (условия у бакета строго уже), но читать приходится
    # 0.3 ГБ вместо 3+ ГБ, и так четыре раза подряд.
    bucket_files = {}
    for b in BUCKETS:
        bf = tmp / f'data-{b}.db'
        _guarded(lambda bf=bf, b=b: build_slim(slim, bf, patches, bucket=b, matches=matches,
                                                 deadline=deadline), deadline)
        bucket_files[b] = bf
        _step(f'бакет {b} готов', t0)

    # Манифест.
    manifest = {
        'version': sha16(slim),
        'patch': patch,
        'updated': datetime.now(timezone.utc).isoformat(),
        'buckets': {b: {'version': sha16(bf),
                        'size_mb': round(bf.stat().st_size / 1e6, 1)}
                    for b, bf in bucket_files.items()}
    }
    vfile = tmp / 'data-version.json'
    vfile.write_text(json.dumps(manifest), encoding='utf-8')
    _step('манифест готов, дальше заливка', t0)
    return {'slim': slim, 'bucket_files': bucket_files, 'vfile': vfile, 'manifest': manifest}


def upload(built: dict, token: str) -> dict:
    """СЕТЕВАЯ часть публикации: залить собранное на GitHub и в R2."""
    slim, bucket_files, vfile = built['slim'], built['bucket_files'], built['vfile']
    manifest = dict(built['manifest'])

    s = _session(token)
    rid = ensure_release(s)
    # Обычные файлы обязательны: по ним качают ВЫПУЩЕННЫЕ до сжатия версии
    # программы. Сжатые кладём рядом — свежая программа берёт их, а если
    # почему-то не вышло, откатывается на обычные.
    upload_asset(s, rid, slim, 'data.db')
    upload_gz(s, rid, slim, 'data.db')
    for b, bf in bucket_files.items():
        upload_asset(s, rid, bf, f'data-{b}.db')
        upload_gz(s, rid, bf, f'data-{b}.db')
    upload_asset(s, rid, vfile, 'data-version.json')

    # То же самое — в своё хранилище на Cloudflare. Программа спрашивает
    # сперва его: GitHub из России отдаёт 0.11 МБ/с против 5–6 у Cloudflare,
    # и на плохой минуте кусок не успевал скачаться за отведённое время.
    #
    # GitHub остаётся: по нему качают версии программы, выпущенные до
    # переезда, и он же запасной путь, если R2 окажется недоступен.
    #
    # Номер версии заливаем ПОСЛЕДНИМ: пока он старый, программа не пойдёт
    # за файлами, которых ещё нет.
    publish_r2(slim, bucket_files, vfile)

    manifest['slim_mb'] = round(slim.stat().st_size / 1e6, 1)
    return manifest


def publish(db_path: str, token: str, run_upload=None, workdir: Path | None = None,
            build_timeout: float | None = None) -> dict:
    """Собрать и залить.

    run_upload(job) — чем запускать заливку; коллектор передаёт сюда дедлайн. Без
    него заливка идёт тут же, как при ручном запуске.

    Временную папку убирает ЗАЛИВКА, а не этот вызов. Если заливка не уложилась в
    дедлайн, поток её продолжает работать фоном — и снести папку у него из-под
    ног значило бы оборвать заливку посреди файлов. Хвосты прерванных заходов
    подбирает уборка коллектора (clear_publish_dir) перед следующим.
    """
    tmp = Path(tempfile.mkdtemp(prefix='pub-', dir=workdir))
    try:
        built = build(db_path, tmp, build_timeout)
    except BaseException:
        shutil.rmtree(tmp, ignore_errors=True)
        raise

    def job():
        try:
            return upload(built, token)
        finally:
            shutil.rmtree(tmp, ignore_errors=True)

    return (run_upload or (lambda fn: fn()))(job)


if __name__ == '__main__':
    p = argparse.ArgumentParser(description='Публикация тонких баз в GitHub release `data`')
    p.add_argument('--db', default=str(Path(__file__).with_name('data.db')))
    args = p.parse_args()

    tok = os.environ.get('GITHUB_TOKEN')
    if not tok:
        sys.exit('Нет GITHUB_TOKEN в окружении (scope: Contents read/write на репо).')

    info = publish(args.db, tok)
    sizes = ' '.join(f'{b}={info["buckets"][b]["size_mb"]}МБ' for b in BUCKETS)
    print(f'Опубликовано: patch={info["patch"]} version={info["version"]} '
          f'| slim={info["slim_mb"]}МБ | {sizes}')
