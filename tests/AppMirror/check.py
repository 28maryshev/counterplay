# -*- coding: utf-8 -*-
"""
AppMirror — зеркало обновлений программы в R2 (pipeline/mirror_app.py).

Повод (журнал 2026-11-08): в Китае GitHub режут, игроки оттуда не могли ни
толком скачать установщик, ни обновиться. Зеркало кладёт фид Velopack,
пакеты и установщик в папку app/ бакета базы.

GitHub и R2 подменены: сеть не нужна, ключи не нужны, настоящий бакет не
трогается.

Что меряет:
  1. пустое зеркало догоняется: пакеты из фида, установщик выпуска, фид —
     ПОСЛЕДНИМ (пока он старый, программа не пойдёт за файлами, которых нет);
     установщик отдаётся как attachment;
  2. повторный запуск при совпавшем фиде ничего не заливает;
  3. новый выпуск: уже залитый пакет того же размера не перезаливается,
     пакеты старше трёх последних версий убираются;
  4. пакет не сошёлся по SHA256 с фидом — фид в зеркале остаётся прежним;
  5. фид называет пакет, которого нет в теге latest, — фид прежний;
  6. --check: совпадает — 0, отстало — 1.

    python tests/AppMirror/check.py        # 0 — всё хорошо, 1 — нет
"""
import hashlib
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
os.environ.update(GITHUB_TOKEN='test', R2_DATA_ENDPOINT='https://acc.r2.example',
                  R2_DATA_BUCKET='b', R2_DATA_KEY_ID='k', R2_DATA_SECRET='s')
sys.path.insert(0, str(ROOT / 'pipeline'))
import mirror_app as m      # noqa: E402

sys.stdout.reconfigure(encoding='utf-8')
failures: list[str] = []


def check(ok: bool, what: str):
    print(('  ок   ' if ok else '  FAIL ') + what)
    if not ok:
        failures.append(what)


# ── поддельный GitHub ─────────────────────────────────────────────────

class Resp:
    def __init__(self, status=200, data=None, body=b''):
        self.status_code, self._data, self.content = status, data, body

    def json(self):
        return self._data

    def raise_for_status(self):
        if self.status_code >= 400:
            raise RuntimeError(f'HTTP {self.status_code}')

    def iter_content(self, n):
        for i in range(0, len(self.content), n):
            yield self.content[i:i + n]


class FakeGithub:
    """releases: тег → {имя файла: байты}."""

    def __init__(self):
        self.releases: dict[str, dict[str, bytes]] = {}
        self.ids: dict[int, tuple[str, str]] = {}

    def get(self, url, params=None, headers=None, stream=False, timeout=None):
        tail = url.split(f'/repos/{m.REPO}/', 1)[1]
        if tail.startswith('releases/tags/'):
            tag = tail.split('/')[-1]
            if tag not in self.releases:
                return Resp(404)
            return Resp(data={'id': list(self.releases).index(tag) + 1})
        if tail.startswith('releases/assets/'):
            tag, name = self.ids[int(tail.split('/')[-1])]
            return Resp(body=self.releases[tag][name])
        if tail.endswith('/assets'):
            rid = int(tail.split('/')[1])
            tag = list(self.releases)[rid - 1]
            if params['page'] > 1:
                return Resp(data=[])
            out = []
            for name, body in self.releases[tag].items():
                aid = hash((tag, name)) & 0xFFFFFF
                self.ids[aid] = (tag, name)
                out.append({'id': aid, 'name': name, 'size': len(body)})
            return Resp(data=out)
        raise AssertionError(url)


gh = FakeGithub()
m.session = lambda: gh


# ── поддельный R2 ─────────────────────────────────────────────────────

store: dict[str, bytes] = {}
meta: dict[str, dict] = {}
puts: list[str] = []


def _put(path, name, content_type='application/octet-stream', cache_seconds=300, disposition=None):
    store[name] = Path(path).read_bytes()
    meta[name] = {'type': content_type, 'cache': cache_seconds, 'disposition': disposition}
    puts.append(name)


m.r2.put = lambda path, name, content_type='application/octet-stream', cache_seconds=300: \
    _put(path, name, content_type, cache_seconds)
m.r2.put_file = _put
m.r2.get = lambda name: store.get(name)
m.r2.head = lambda name: len(store[name]) if name in store else None
m.r2.list_keys = lambda prefix: [k for k in store if k.startswith(prefix)]
m.r2.delete = lambda name: store.pop(name, None)
m.requests.get = lambda url, params=None, timeout=None: (
    Resp(body=store[url.split(m.PUBLIC + '/', 1)[1]]) if url.split(m.PUBLIC + '/', 1)[1] in store
    else Resp(404))


def release(v: str, prev: str | None, bad_sha: bool = False, missing: str | None = None):
    """Выпуск v: релиз vX с установщиком и тег latest с фидом и пакетами."""
    full = f'full {v}'.encode() * 50
    delta = f'delta {v}'.encode() * 5
    assets = [('Full', f'Counterplay-{v}-full.nupkg', full)]
    if prev:
        assets.append(('Delta', f'Counterplay-{v}-delta.nupkg', delta))
    feed = {'Assets': [{
        'PackageId': 'Counterplay', 'Version': v, 'Type': t, 'FileName': n,
        'SHA1': '', 'SHA256': ('00' * 32 if bad_sha else hashlib.sha256(b).hexdigest().upper()),
        'Size': len(b)} for t, n, b in assets]}
    latest = {n: b for _, n, b in assets if n != missing}
    latest['releases.win.json'] = json.dumps(feed).encode()
    latest['RELEASES'] = '\n'.join(n for _, n, _ in assets).encode()
    gh.releases['latest'] = latest
    gh.releases[f'v{v}'] = {m.SETUP: f'setup {v}'.encode() * 40}


def feed_in_mirror() -> str | None:
    raw = store.get('app/releases.win.json')
    return m.feed_version(json.loads(raw)) if raw else None


# ── 1. пустое зеркало ─────────────────────────────────────────────────
print('пустое зеркало:')
release('1.3.84', '1.3.83')
code = m.sync(force=False)
check(code == 0, f'догнано, код 0 ({code})')
check(feed_in_mirror() == '1.3.84', f'фид в зеркале — 1.3.84 ({feed_in_mirror()})')
check(puts[-1] == 'app/releases.win.json', f'фид залит последним ({puts[-1]})')
check({'app/Counterplay-1.3.84-full.nupkg', 'app/Counterplay-1.3.84-delta.nupkg'} <= set(store),
      'пакеты из фида на месте')
check(store.get('app/' + m.SETUP) == gh.releases['v1.3.84'][m.SETUP], 'установщик из релиза v1.3.84')
check((meta.get('app/' + m.SETUP) or {}).get('disposition', '').startswith('attachment'),
      'установщик отдаётся как attachment — браузер его скачивает, а не открывает')
check(meta['app/releases.win.json']['cache'] == m.CACHE_FEED, 'фид на краю — минуту')

# ── 2. повтор ─────────────────────────────────────────────────────────
print('повторный запуск:')
puts.clear()
code = m.sync(force=False)
check(code == 0 and not puts, f'ничего не заливается ({puts})')
check(m.check() == 0, '--check: совпадает — 0')

# ── 3. новые выпуски и уборка ─────────────────────────────────────────
print('новые выпуски:')
for v, prev in (('1.3.85', '1.3.84'), ('1.3.86', '1.3.85')):
    release(v, prev)
    m.sync(force=False)
# Пакет 1.3.87 уже лежит в зеркале (например, прошлая попытка залила пакеты,
# но упала до фида) — второй раз его не льём.
release('1.3.87', '1.3.86')
store['app/Counterplay-1.3.87-full.nupkg'] = gh.releases['latest']['Counterplay-1.3.87-full.nupkg']
puts.clear()
m.sync(force=False)
check('app/Counterplay-1.3.87-full.nupkg' not in puts, 'залитый пакет того же размера не перезаливается')
check(feed_in_mirror() == '1.3.87', f'фид — 1.3.87 ({feed_in_mirror()})')
left = sorted({k.split('-')[1] for k in store if k.endswith('.nupkg')})
check(left == ['1.3.85', '1.3.86', '1.3.87'], f'остались пакеты трёх последних версий ({left})')

# ── 4. SHA256 не сошёлся ──────────────────────────────────────────────
print('пакет не сошёлся с фидом:')
release('1.3.88', '1.3.87', bad_sha=True)
try:
    code = m.sync(force=False)
except Exception as e:
    code = f'{type(e).__name__}: {e}'
check('SHA256' in str(code), f'остановился на сверке ({code})')
check(feed_in_mirror() == '1.3.87', f'фид в зеркале прежний ({feed_in_mirror()})')

# ── 5. фид называет то, чего нет ──────────────────────────────────────
print('фид называет пакет, которого нет в latest:')
release('1.3.89', '1.3.88', missing='Counterplay-1.3.89-delta.nupkg')
try:
    code = m.sync(force=False)
except Exception as e:
    code = f'{type(e).__name__}: {e}'
check('нет' in str(code), f'остановился ({code})')
check(feed_in_mirror() == '1.3.87', f'фид в зеркале прежний ({feed_in_mirror()})')

# ── 6. --check на отставшем ───────────────────────────────────────────
print('сверка:')
check(m.check() == 1, '--check: отстало — 1')

print('\nВСЁ В ПОРЯДКЕ' if not failures else f'\nНЕ ПРОШЛО: {len(failures)}')
sys.exit(1 if failures else 0)
