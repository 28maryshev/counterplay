# -*- coding: utf-8 -*-
"""Зеркало обновлений программы в R2: папка app/ открытого бакета базы.

Зачем: в материковом Китае GitHub режут. Игроки оттуда скачивали установщик,
и в установку превращалась одна загрузка из пяти (у остальных — две из
трёх). Поставившие сидели на версии установки: обновления с GitHub не
доходили (журнал 2026-11-08). Бакет базы за Cloudflare оттуда открывается —
база всегда качалась с него.

Что лежит в app/ (адрес https://data.counterplays.com/app/…):
  releases.win.json, RELEASES   фид Velopack — копия катящегося тега latest;
  Counterplay-<v>-full/delta.nupkg  пакеты, которые фид называет;
  Counterplay-win-Setup.exe     установщик выпуска из фида — его отдаёт сайт
                                посетителям из CN (/api/setup).

Программа спрашивает фид и здесь, и на GitHub, и берёт тот, где версия выше
(MirroredUpdateSource), так что отставшее зеркало обновлений не ломает, —
но китайским игрокам без него обновляться неоткуда.

Порядок как у базы: сначала пакеты и установщик, фид — ПОСЛЕДНИМ. Пока фид
старый, программа не пойдёт за файлами, которых ещё нет. Пакеты проверяются
по SHA256 из фида до заливки. Старые пакеты убираются: хранятся три
последние версии (кто сейчас качает предпоследнюю — докачает).

Запуск — release.ps1 после обновления фида на GitHub, руками — так же:
    docker exec counterplay-collector python mirror_app.py           # догнать
    docker exec counterplay-collector python mirror_app.py --check   # сверить
Коды выхода: 0 — зеркало совпадает с GitHub (или догнано), 1 — нет.

Нужны GITHUB_TOKEN и R2_DATA_* — оба есть в окружении контейнера сборщика.
Память: файлы идут потоком через диск (TMPDIR), а не целиком в память, —
полный пакет весит ~75 МБ, памяти на машине 256 МБ.
"""
import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
import time
from pathlib import Path

import requests

import r2

REPO = os.environ.get('GITHUB_REPO', '28maryshev/counterplay')
API = 'https://api.github.com'
PUBLIC = 'https://data.counterplays.com'
PREFIX = 'app/'
FEED = 'releases.win.json'
LEGACY_FEED = 'RELEASES'
SETUP = 'Counterplay-win-Setup.exe'
KEEP_VERSIONS = 3
PKG_RE = re.compile(r'^Counterplay-(\d+)\.(\d+)\.(\d+)-(full|delta)\.nupkg$')

# Сколько край Cloudflare держит файл. Пакеты под своими номерами не меняются;
# установщик и фид меняются с каждым выпуском.
CACHE_PKG = 86400
CACHE_SETUP = 300
CACHE_FEED = 60


def log(msg: str):
    print(msg, flush=True)


def session() -> requests.Session:
    tok = os.environ.get('GITHUB_TOKEN')
    if not tok:
        sys.exit('нет GITHUB_TOKEN в окружении')
    s = requests.Session()
    s.headers.update({'Authorization': f'Bearer {tok}',
                      'Accept': 'application/vnd.github+json',
                      'X-GitHub-Api-Version': '2022-11-28'})
    return s


def assets_of(s: requests.Session, tag: str) -> dict:
    """Файлы релиза по тегу: имя → описание (id, size). Нет релиза — пусто.

    Через API, а не по ссылке releases/download: та ведёт на адрес с кэшем, и
    сразу после замены файла там минутами лежит прежний (release.ps1 на этом
    уже спотыкался)."""
    r = s.get(f'{API}/repos/{REPO}/releases/tags/{tag}', timeout=30)
    if r.status_code == 404:
        return {}
    r.raise_for_status()
    rid = r.json()['id']
    out = {}
    for page in range(1, 6):
        r = s.get(f'{API}/repos/{REPO}/releases/{rid}/assets',
                  params={'per_page': 100, 'page': page}, timeout=30)
        r.raise_for_status()
        batch = r.json()
        if not batch:
            break
        out.update({a['name']: a for a in batch})
    return out


def download(s: requests.Session, asset: dict, dest: Path) -> Path:
    """Скачать файл релиза потоком на диск, сверить размер."""
    r = s.get(f'{API}/repos/{REPO}/releases/assets/{asset["id"]}',
              headers={'Accept': 'application/octet-stream'}, stream=True, timeout=300)
    r.raise_for_status()
    with open(dest, 'wb') as f:
        for chunk in r.iter_content(1 << 20):
            f.write(chunk)
    if dest.stat().st_size != asset['size']:
        raise RuntimeError(f'{asset["name"]}: скачалось {dest.stat().st_size} из {asset["size"]} байт')
    return dest


def sha256_of(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest().upper()


def feed_version(feed: dict) -> str:
    vs = [a.get('Version', '') for a in feed.get('Assets', [])]
    vs = [v for v in vs if re.fullmatch(r'\d+\.\d+\.\d+', v)]
    if not vs:
        raise RuntimeError('в фиде нет ни одной версии')
    return max(vs, key=lambda v: tuple(map(int, v.split('.'))))


def github_feed(s: requests.Session) -> tuple[dict, bytes, dict]:
    """Фид с GitHub: (описания файлов тега latest, байты фида, фид)."""
    latest = assets_of(s, 'latest')
    if FEED not in latest:
        raise RuntimeError(f'в теге latest нет {FEED}')
    with tempfile.TemporaryDirectory() as d:
        raw = download(s, latest[FEED], Path(d) / FEED).read_bytes()
    return latest, raw, json.loads(raw.decode('utf-8-sig'))


def check() -> int:
    s = session()
    _, gh_raw, gh = github_feed(s)
    mirror_raw = r2.get(PREFIX + FEED)
    gv = feed_version(gh)
    if mirror_raw is None:
        log(f'зеркало пустое, на GitHub {gv}')
        return 1
    mv = feed_version(json.loads(mirror_raw.decode('utf-8-sig')))
    if mirror_raw == gh_raw:
        log(f'зеркало совпадает с GitHub: {gv}')
        return 0
    log(f'зеркало отстало: в нём {mv}, на GitHub {gv}')
    return 1


def sync(force: bool) -> int:
    s = session()
    latest, gh_raw, gh = github_feed(s)
    version = feed_version(gh)

    if not force and r2.get(PREFIX + FEED) == gh_raw:
        log(f'зеркало совпадает с GitHub: {version} — делать нечего')
        return 0
    log(f'догоняю зеркало до {version}')

    with tempfile.TemporaryDirectory() as d:
        tmp = Path(d)

        # 1. Пакеты, которые называет фид. Уже залитые под тем же именем и
        # размером не трогаем: номер в имени, и содержимое у него одно.
        for a in gh.get('Assets', []):
            name = a['FileName']
            if name not in latest:
                raise RuntimeError(f'фид называет {name}, а в теге latest его нет')
            if r2.head(PREFIX + name) == a['Size']:
                log(f'  {name}: уже в зеркале')
                continue
            path = download(s, latest[name], tmp / name)
            got = sha256_of(path)
            if got != a['SHA256'].upper():
                raise RuntimeError(f'{name}: SHA256 {got} не совпал с фидом {a["SHA256"]}')
            r2.put_file(path, PREFIX + name, cache_seconds=CACHE_PKG)
            log(f'  {name}: залит ({a["Size"] / 1e6:.1f} МБ)')
            path.unlink()

        # 2. Установщик выпуска из фида — им сайт кормит посетителей из CN.
        setup = assets_of(s, f'v{version}').get(SETUP)
        if setup:
            path = download(s, setup, tmp / SETUP)
            r2.put_file(path, PREFIX + SETUP, cache_seconds=CACHE_SETUP,
                        disposition=f'attachment; filename="{SETUP}"')
            log(f'  {SETUP}: залит из v{version} ({setup["size"] / 1e6:.1f} МБ)')
            path.unlink()
        else:
            log(f'  ВНИМАНИЕ: в релизе v{version} нет {SETUP} — установщик в зеркале прежний')

        # 3. Фид — последним.
        if LEGACY_FEED in latest:
            path = download(s, latest[LEGACY_FEED], tmp / LEGACY_FEED)
            r2.put(path, PREFIX + LEGACY_FEED, 'text/plain', cache_seconds=CACHE_FEED)
        path = tmp / FEED
        path.write_bytes(gh_raw)
        r2.put(path, PREFIX + FEED, 'application/json', cache_seconds=CACHE_FEED)
        log(f'  {FEED}: залит — зеркало на {version}')

    # 4. Сверка через публичный адрес, как его видит программа. Хвост
    # ?t=… — мимо кэша края: ключ кэша включает строку запроса.
    r = requests.get(f'{PUBLIC}/{PREFIX}{FEED}', params={'t': int(time.time())}, timeout=30)
    if r.status_code != 200 or r.content != gh_raw:
        log(f'ВНИМАНИЕ: публичный адрес отдаёт не то (HTTP {r.status_code})')
        return 1
    log(f'сверено: {PUBLIC}/{PREFIX}{FEED} отдаёт {version}')

    # 5. Уборка: пакеты старше трёх последних версий. Сбой уборки зеркало не
    # портит — только место занимает.
    try:
        prune()
    except Exception as e:
        log(f'уборка не удалась: {type(e).__name__}: {e}')
    return 0


def prune():
    keys = r2.list_keys(PREFIX)
    pkgs = {}
    for k in keys:
        m = PKG_RE.match(k[len(PREFIX):])
        if m:
            pkgs.setdefault(tuple(map(int, m.groups()[:3])), []).append(k)
    keep = sorted(pkgs)[-KEEP_VERSIONS:]
    gone = [k for v, ks in pkgs.items() if v not in keep for k in ks]
    for k in gone:
        r2.delete(k)
    if gone:
        log(f'убрано старых пакетов: {len(gone)} (остались {", ".join(".".join(map(str, v)) for v in keep)})')


def main() -> int:
    ap = argparse.ArgumentParser(description='зеркало обновлений программы в R2')
    ap.add_argument('--check', action='store_true', help='только сверить с GitHub')
    ap.add_argument('--force', action='store_true', help='перезалить, даже если фид совпадает')
    args = ap.parse_args()
    if not r2.configured():
        log('R2 не настроен (R2_DATA_*) — зеркалить некуда')
        return 1
    try:
        return check() if args.check else sync(args.force)
    except Exception as e:
        log(f'НЕ ВЫШЛО: {type(e).__name__}: {e}')
        return 1


if __name__ == '__main__':
    sys.exit(main())
