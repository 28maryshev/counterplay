# -*- coding: utf-8 -*-
"""
RegionTiers — тир-лист сайта по серверам и отдельным тирам (журнал 2026-10-28).

До 28.10 сбор писал матч только в бакет, а внутри бакета шёл по дивизионам
подряд: emerald был почти чистым Emerald, Diamond попадал только на LAN/LAS,
Iron — никуда. Emerald на больших серверах не кончался, и Silver/Gold там не
собирались вовсе. Теперь сбор пишет сервер и тир матча (region_wr/region_bans),
тиры бакета идут вперемешку, бакеты — кругами с нормой, а выгрузка отдаёт
ячейки «сервер × бакет/тир».

Пять частей:
  1. перенос: таблицы создаются из drafts ровно один раз и совпадают с тем, что
     записал бы сбор; курсор бакета, стоявший на втором тире, переезжает на тир;
  2. запись: новый матч ложится с сервером и тиром, баны — тоже;
  3. обход: игроки emerald идут 2:1 (Emerald:Diamond), Diamond — с позиции
     старого курсора; у апекса тир у каждого свой;
  4. круги: при бесконечном Emerald сбор всё равно доходит до Gold и Silver, а
     Emerald остаётся главным; Silver/Bronze/Iron — 2:2:1;
  5. выгрузка: бакеты всех серверов — те же, что выдавала прежняя выгрузка
     (тир-лист программы); тиры и серверы — только от порога матчей.

Работает на временной базе с настоящей схемой (collect.init_db), Riot подменён.
Рабочих баз не трогает. Секунд двадцать.

    python tests/RegionTiers/check.py        # 0 — всё хорошо, 1 — нет
"""
import io
import json
import os
import random
import shutil
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-regiontiers-'))
sys.path.insert(0, str(ROOT / 'pipeline'))
import collect                       # noqa: E402
import export_tiers                  # noqa: E402

collect.RATE_DELAY = 0.0001                       # 0 делит на ноль в строке лога
collect.load_completed_items = lambda: set()      # без Data Dragon
failures: list[str] = []
POS = {v: k for k, v in collect.POSITION_MAP.items()}
POOL = {r: [i * 10 + k for k in range(1, 4)] for i, r in enumerate(collect.POSITION_MAP.values(), 1)}
ALL_CHAMPS = [c for pool in POOL.values() for c in pool]


def check(ok: bool, msg: str):
    print(('  ok  ' if ok else '  FAIL ') + msg)
    if not ok:
        failures.append(msg)


def fake_match(mid: str, rnd: random.Random) -> dict:
    """Ранкед-матч с десятью игроками на своих линиях и банами."""
    win_team = rnd.choice((100, 200))
    parts = []
    for role, pool in POOL.items():
        a, b = rnd.sample(pool, 2)
        for team, champ in ((100, a), (200, b)):
            parts.append({'teamId': team, 'teamPosition': POS[role],
                          'championId': champ, 'win': team == win_team})
    bans = [{'bans': [{'championId': rnd.choice(ALL_CHAMPS)}]} for _ in (100, 200)]
    return {'metadata': {'matchId': mid},
            'info': {'queueId': 420, 'gameVersion': '16.21.555.1',
                     'participants': parts, 'teams': bans}}


def rows(con, sql, *args):
    return con.execute(sql, args).fetchall()


# ─────────────────────────── 1. перенос ───────────────────────────
print('1. перенос из drafts')
DB1 = str(TMP / 'one.db')
con = collect.init_db(DB1)
rnd = random.Random(7)
n_old = {'EUW1': 600, 'KR': 200}
for plat, n in n_old.items():
    for i in range(n):
        collect.process_match(con, fake_match(f'{plat}_{i}', rnd), 'emerald')
con.commit()
# Так выглядела бы запись сбора «с самого начала» — с ней и сверим перенос.
want = sorted(rows(con, "SELECT champion_id, role, region, tier_bucket, tier, patch, games, wins "
                        "FROM region_wr"))
# Теперь база как до 28.10: таблиц нет, курсор emerald стоит на Diamond.
con.execute('DROP TABLE region_wr')
con.execute('DROP TABLE region_bans')
con.execute("INSERT INTO collect_cursor VALUES ('euw1', 'emerald', 'DIAMOND', 'II', 37, 0)")
con.execute("INSERT INTO collect_cursor VALUES ('euw1', 'gold', 'GOLD', 'III', 5, 0)")
con.commit()
con.close()

con = collect.init_db(DB1)
got = sorted(rows(con, "SELECT champion_id, role, region, tier_bucket, tier, patch, games, wins "
                       "FROM region_wr"))
check(got == want and len(got) > 0,
      f'перенос совпал с записью сбора ({len(got)} строк, tier пустой)')
cur = dict(((r, b), (t, d, p)) for r, b, t, d, p in
           rows(con, 'SELECT region, bucket, tier, division, page FROM collect_cursor'))
check(cur.get(('euw1', 'diamond')) == ('DIAMOND', 'II', 37) and ('euw1', 'emerald') not in cur,
      'курсор emerald на Diamond переехал на тир diamond')
check(cur.get(('euw1', 'gold')) == ('GOLD', 'III', 5), 'курсор на своём тире остался на месте')
con.close()
con = collect.init_db(DB1)          # повторный запуск не переносит второй раз
again = sorted(rows(con, "SELECT champion_id, role, region, tier_bucket, tier, patch, games, wins "
                         "FROM region_wr"))
check(again == got, 'повторный запуск не задвоил перенос')

# ─────────────────────────── 2. запись ───────────────────────────
print('2. запись нового матча')
n_new = {'diamond': 450, 'emerald': 150}
k = 10_000
for tier, n in n_new.items():
    for _ in range(n):
        k += 1
        collect.process_match(con, fake_match(f'EUW1_{k}', rnd), 'emerald', tier)
con.commit()
g = dict(rows(con, "SELECT tier, SUM(games) FROM region_wr WHERE region='euw1' GROUP BY tier"))
check(g.get('diamond') == 4500 and g.get('emerald') == 1500 and g.get('') == 6000,
      f"region_wr euw1 по тирам: {g}")
b = dict(rows(con, "SELECT tier, SUM(bans) FROM region_bans WHERE region='euw1' GROUP BY tier"))
check(b.get('diamond') == 900 and b.get('emerald') == 300 and '' not in b,
      f'region_bans euw1 по тирам: {b}')


# ─────────────────────────── 3. обход ───────────────────────────
class League:
    def __init__(self, pages):
        self.pages = pages

    def entries(self, platform, queue, tier, division, page=1):
        if page > self.pages.get(tier, 3):
            return []
        return [{'puuid': f'{platform}:{tier}:{division}:{page}:{i}'} for i in range(5)]

    def _apex(self, platform, name, n):
        return {'entries': [{'puuid': f'{platform}:{name}:{i}'} for i in range(n)]}

    def challenger_by_queue(self, platform, queue):
        return self._apex(platform, 'CH', 4)

    def grandmaster_by_queue(self, platform, queue):
        return self._apex(platform, 'GM', 4)

    def masters_by_queue(self, platform, queue):
        return self._apex(platform, 'M', 6)


class Match:
    def __init__(self):
        self.n = 0
        self.rnd = random.Random(3)

    def matchlist_by_puuid(self, regional, puuid, **kw):
        self.n += 1
        return [f'{puuid.split(":")[0].upper()}_{900_000 + self.n}']

    def by_id(self, regional, mid):
        return fake_match(mid, self.rnd)


class Watcher:
    def __init__(self, pages):
        self.league = League(pages)
        self.match = Match()
        self.summoner = None


print('3. обход игроков')
w = Watcher({'DIAMOND': 40})
it = collect.iter_players(w, 'euw1', 'emerald', set(), con)
first = [next(it) for _ in range(9)]
tiers = [t for _, t in first]
check(tiers == ['emerald', 'emerald', 'diamond'] * 3, f'emerald вперемешку 2:1: {tiers}')
dia = [p for p, t in first if t == 'diamond']
check(dia[0] == 'euw1:DIAMOND:II:37:0', f'Diamond начал с позиции курсора: {dia[0]}')
apex = [t for _, t in collect.iter_players(w, 'euw1', 'master', set(), None)]
check(apex == ['challenger'] * 4 + ['grandmaster'] * 4 + ['master'] * 6,
      f'апекс: тир у каждого свой ({len(apex)} игроков)')
con.close()

# ─────────────────────────── 4. круги ───────────────────────────
print('4. круги бакетов')
DB2 = str(TMP / 'two.db')
endless = Watcher({'EMERALD': 10 ** 9, 'DIAMOND': 10 ** 9, 'GOLD': 1000, 'PLATINUM': 1000,
                   'SILVER': 1000, 'BRONZE': 1000, 'IRON': 1000})
collect.LolWatcher = lambda key, timeout=None: endless
collect.ROUND_QUOTA = {'emerald': 30, 'gold': 10, 'silver': 10}
log = io.StringIO()
_stdout, sys.stdout = sys.stdout, log           # сбор печатает строку на игрока
try:
    collect.run_continuous('RGAPI-test', DB2, ['euw1'], collect.BUCKET_PRIORITY, 30, 200)
finally:
    sys.stdout = _stdout
con = sqlite3.connect(DB2)
per = dict(rows(con, 'SELECT tier_bucket, COUNT(*) FROM drafts GROUP BY 1'))
print('     матчей по бакетам:', per)
check(per.get('master') == 14, 'master — до вычерпания (14 игроков)')
check(per.get('gold', 0) >= 10 and per.get('silver', 0) >= 10,
      'при бесконечном Emerald сбор дошёл до Gold и Silver')
check(per.get('emerald', 0) >= 2 * max(per.get('gold', 0), per.get('silver', 0)),
      'Emerald остаётся главным бакетом')
t = dict(rows(con, "SELECT tier, SUM(games) / 10 FROM region_wr WHERE tier_bucket='silver' GROUP BY 1"))
check(t.get('iron', 0) > 0 and t.get('silver') == t.get('bronze') == 2 * t.get('iron', 0),
      f'silver-бакет: Silver/Bronze/Iron 2:2:1 — {t}')
con.close()

# ─────────────────────────── 5. выгрузка ───────────────────────────
print('5. выгрузка')
OLD, NEW, REG = TMP / 'old', TMP / 'new', TMP / 'regions'
for d in (OLD, NEW):
    d.mkdir()
old_src = subprocess.run(['git', 'show', 'c94b8c0:pipeline/export_tiers.py'], cwd=ROOT,
                         capture_output=True, encoding='utf-8').stdout
(TMP / 'export_tiers_old.py').write_text(old_src, encoding='utf-8')
subprocess.run([sys.executable, str(TMP / 'export_tiers_old.py'), '--db', DB1, '--out', str(OLD)],
               env={**os.environ, 'PYTHONPATH': str(ROOT / 'pipeline'), 'PYTHONUTF8': '1'},
               check=True, capture_output=True)
export_tiers.MIN_CELL_MATCHES = 300
export_tiers.MIN_BAN_MATCHES = 100
sys.argv = ['export_tiers.py', '--db', DB1, '--out', str(NEW), '--regions-out', str(REG)]
out = io.StringIO()
_stdout, sys.stdout = sys.stdout, out
try:
    export_tiers.main()
finally:
    sys.stdout = _stdout
old = json.loads((OLD / 'tiers.json').read_text(encoding='utf-8'))
new = json.loads((NEW / 'tiers.json').read_text(encoding='utf-8'))
check(new['buckets'] == old['buckets'] and new['buckets']['emerald']['roles']['top'],
      'бакеты всех серверов — как у прежней выгрузки (тир-лист программы)')
check(list(new['tiers']) == ['diamond'], f"тиры всех серверов от порога: {list(new['tiers'])}")
check(new['tiers']['diamond']['matches'] == 450, 'тир diamond — 450 матчей, только новые')
euw = json.loads((REG / 'euw1.json').read_text(encoding='utf-8'))
kr = json.loads((REG / 'kr.json').read_text(encoding='utf-8'))
check(euw['buckets'].get('emerald', {}).get('matches') == 1200,
      'EUW × emerald: перенесённые 600 + новые 600')
check(list(euw['tiers']) == ['diamond'], f"EUW: тиры от порога — {list(euw['tiers'])}")
check(kr['buckets'] == {} and kr['tiers'] == {}, 'KR (200 матчей) — ни одной ячейки, сайт возьмёт ближайшую')
# Баны сервера: новых матчей 600 ≥ MIN_BAN_MATCHES — бан-рейт свой, по новым
# матчам (переносу банов неоткуда взяться), растянутый на все 1200 матчей ячейки.
con = sqlite3.connect(DB1)
fresh = dict(rows(con, "SELECT champion_id, SUM(bans) FROM region_bans "
                       "WHERE region='euw1' AND tier_bucket='emerald' GROUP BY 1"))
bans = euw['buckets']['emerald']['bans']
check(bool(bans) and all(abs(v - round(fresh[int(c)] / 600 * 1200, 1)) < 0.11 for c, v in bans.items()),
      'EUW × emerald: бан-рейт свой, по новым матчам')
dia = euw['tiers']['diamond']
check(all(v == fresh_d for c, v in dia['bans'].items()
          for fresh_d in [rows(con, "SELECT SUM(bans) FROM region_bans WHERE region='euw1' "
                                    "AND tier='diamond' AND champion_id=?", int(c))[0][0]]),
      'EUW × diamond: баны как есть')
con.close()

shutil.rmtree(TMP, ignore_errors=True)
print()
if failures:
    print(f'ПРОВАЛОВ: {len(failures)}')
    for f in failures:
        print('  —', f)
    sys.exit(1)
print('всё сходится')
