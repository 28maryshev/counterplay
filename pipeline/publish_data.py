"""
publish_data.py — выкладывает базы в GitHub release `data`.

Программа читает из базы ТОЛЬКО движок пиков/банов (base_wr, matchup, synergy,
botlane_matchup, champion_bans). Руны/сборки берутся с сайта (/api/stats), а
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
import json
import os
import sqlite3
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

import requests

from freshness import all_patches

REPO = os.environ.get('GITHUB_REPO', '28maryshev/counterplay')
API = 'https://api.github.com'
UPLOADS = 'https://uploads.github.com'
TAG = 'data'

# Таблицы, которые реально читает движок программы (RecommendationEngine.cs).
ENGINE_TABLES = ['base_wr', 'matchup', 'synergy', 'botlane_matchup', 'champion_bans']
BUCKETS = ['silver', 'gold', 'emerald', 'master']
KEEP_PATCHES = 5     # сколько последних патчей класть (движок берёт из них 3 свежих)
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


def ensure_release(s: requests.Session) -> int:
    r = s.get(f'{API}/repos/{REPO}/releases/tags/{TAG}')
    if r.status_code == 200:
        return r.json()['id']
    r = s.post(f'{API}/repos/{REPO}/releases', json={
        'tag_name': TAG, 'name': 'Data', 'body': 'Central database, updated each patch'})
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
                   headers={'Content-Type': 'application/octet-stream'}, data=f)
    r.raise_for_status()


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

        # Побакетные тонкие базы.
        bucket_files = {}
        for b in BUCKETS:
            bf = tmp / f'data-{b}.db'
            build_slim(full, bf, patches, bucket=b, matches=matches)
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
        upload_asset(s, rid, slim, 'data.db')
        for b, bf in bucket_files.items():
            upload_asset(s, rid, bf, f'data-{b}.db')
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
