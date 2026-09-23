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


def snapshot(db_path: Path, dest: Path):
    """Консистентная копия базы: sqlite backup читает даже открытую базу и сливает WAL."""
    src = sqlite3.connect(f'file:{db_path}?mode=ro', uri=True)
    dst = sqlite3.connect(dest)
    with dst:
        src.backup(dst)
    dst.close()
    src.close()


def build_slim(full_path: Path, dest_path: Path, patches, bucket=None, matches=0):
    """Тонкая база: только ENGINE_TABLES, за patches, без длинного хвоста,
    опционально по одному бакету. Источник — консистентный снапшот (без писателя).
    processed_matches не кладём (движку не нужна) — но общее число матчей пишем в
    служебную db_meta, чтобы бот показывал счётчик без 37 МБ таблицы."""
    if dest_path.exists():
        dest_path.unlink()
    dst = sqlite3.connect(str(dest_path))
    dst.execute(f"ATTACH DATABASE '{full_path}' AS src")
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
    dst.close()


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
    папке уже лежат полный снапшот, тонкая база и четыре побакетных.
    """
    gz = gzip_file(path)
    try:
        upload_asset(s, release_id, gz, name + '.gz')
    finally:
        gz.unlink(missing_ok=True)


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


def publish(db_path: str, token: str) -> dict:
    db = Path(db_path)
    if not db.exists():
        raise FileNotFoundError(f'База не найдена: {db}')

    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        full = tmp / 'full.db'
        snapshot(db, full)                       # консистентный снапшот-источник
        fcon = sqlite3.connect(f'file:{full}?mode=ro', uri=True)
        patches = all_patches(fcon)[:KEEP_PATCHES]
        patch = patches[0] if patches else '0.0'
        matches = fcon.execute('SELECT COUNT(*) FROM processed_matches').fetchone()[0]
        fcon.close()

        # Тонкая общая база (совместимость со старой программой).
        slim = tmp / 'data.db'
        build_slim(full, slim, patches, bucket=None, matches=matches)

        # Побакетные тонкие базы — из УЖЕ отфильтрованной тонкой базы, а не из
        # полного снапшота. Строки те же (условия у бакета строго уже), но читать
        # приходится 0.5 ГБ вместо 4+ ГБ, и так четыре раза подряд: именно эти
        # четыре прохода по полной базе растягивали публикацию на часы — при 256 МБ
        # памяти контейнера кэш не держит и половины файла.
        bucket_files = {}
        for b in BUCKETS:
            bf = tmp / f'data-{b}.db'
            build_slim(slim, bf, patches, bucket=b, matches=matches)
            bucket_files[b] = bf

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

        # Заливка.
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

        manifest['slim_mb'] = round(slim.stat().st_size / 1e6, 1)
    return manifest


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
