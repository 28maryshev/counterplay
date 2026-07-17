"""
publish_data.py — выкладывает data.db в GitHub release `data` (порт publish-data.ps1).

Нужен на сервере: там нет PowerShell и gh CLI, поэтому работаем через GitHub API
и токен из окружения. Логика та же, что у publish-data.ps1, чтобы приложение
обновлялось одинаково независимо от того, кто опубликовал — ты с машины или
коллектор с сервера:

  1. sqlite backup → консистентный снапшот (мержит WAL, не ловит «файл занят»);
  2. version = sha256 снапшота (первые 16) → любое изменение базы = новая версия;
  3. data-version.json + data.db заливаются в rolling-release `data` (assets
     перезаписываются).

Запуск:  python pipeline/publish_data.py [--db pipeline/data.db]
Токен:   переменная окружения GITHUB_TOKEN (scope: Contents read/write на репо).
"""

import argparse
import hashlib
import json
import os
import sqlite3
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import requests

REPO = os.environ.get('GITHUB_REPO', '28maryshev/counterplay')
API = 'https://api.github.com'
UPLOADS = 'https://uploads.github.com'
TAG = 'data'


def snapshot(db_path: Path, dest: Path):
    """Консистентная копия базы: sqlite backup читает даже открытую базу и
    сливает WAL — иначе свежие матчи не попали бы пользователям."""
    src = sqlite3.connect(f'file:{db_path}?mode=ro', uri=True)
    dst = sqlite3.connect(dest)
    with dst:
        src.backup(dst)
    dst.close()
    src.close()


def latest_patch(db: Path) -> str:
    con = sqlite3.connect(f'file:{db}?mode=ro', uri=True)
    try:
        ps = [r[0] for r in con.execute('SELECT DISTINCT patch FROM base_wr')
              if r[0] and r[0][0].isdigit()]
        ps.sort(key=lambda s: tuple(int(x) for x in s.split('.')))
        return ps[-1] if ps else '0.0'
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


def ensure_release(s: requests.Session) -> int:
    r = s.get(f'{API}/repos/{REPO}/releases/tags/{TAG}')
    if r.status_code == 200:
        return r.json()['id']
    r = s.post(f'{API}/repos/{REPO}/releases', json={
        'tag_name': TAG, 'name': 'Data',
        'body': 'Central database, updated each patch'})
    r.raise_for_status()
    return r.json()['id']


def upload_asset(s: requests.Session, release_id: int, path: Path, name: str):
    """Заливает ассет, снося прежний с тем же именем (--clobber у gh)."""
    r = s.get(f'{API}/repos/{REPO}/releases/{release_id}/assets')
    r.raise_for_status()
    for a in r.json():
        if a['name'] == name:
            s.delete(f'{API}/repos/{REPO}/releases/assets/{a["id"]}').raise_for_status()
    with open(path, 'rb') as f:
        r = s.post(f'{UPLOADS}/repos/{REPO}/releases/{release_id}/assets',
                   params={'name': name},
                   headers={'Content-Type': 'application/octet-stream'},
                   data=f)
    r.raise_for_status()


def publish(db_path: str, token: str) -> dict:
    db = Path(db_path)
    if not db.exists():
        raise FileNotFoundError(f'База не найдена: {db}')

    with tempfile.TemporaryDirectory() as tmp:
        snap = Path(tmp) / 'data.db'
        snapshot(db, snap)
        version = sha16(snap)
        patch = latest_patch(snap)
        manifest = {'version': version, 'patch': patch,
                    'updated': datetime.now(timezone.utc).isoformat()}
        vfile = Path(tmp) / 'data-version.json'
        vfile.write_text(json.dumps(manifest), encoding='utf-8')

        s = _session(token)
        rid = ensure_release(s)
        upload_asset(s, rid, snap, 'data.db')
        upload_asset(s, rid, vfile, 'data-version.json')
        manifest['size_mb'] = round(snap.stat().st_size / 1e6, 1)
    return manifest


if __name__ == '__main__':
    p = argparse.ArgumentParser(description='Публикация data.db в GitHub release `data`')
    p.add_argument('--db', default=str(Path(__file__).with_name('data.db')))
    args = p.parse_args()

    tok = os.environ.get('GITHUB_TOKEN')
    if not tok:
        sys.exit('Нет GITHUB_TOKEN в окружении (scope: Contents read/write на репо).')

    info = publish(args.db, tok)
    print(f'Опубликовано: version={info["version"]} patch={info["patch"]} '
          f'({info["size_mb"]} МБ)')
