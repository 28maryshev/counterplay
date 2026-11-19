# -*- coding: utf-8 -*-
"""
RunesExportFast — выгрузка рун считает связки по выжимке, а не по рабочей базе.

Повод (владелец, 20.11): «сделай, чтоб выгрузка шла быстрее». Выгрузка 19.11
шла на коллекторе больше 2,5 часа и сделала четверть связок: база больше 4 ГБ
при 911 МБ памяти, строки одной связки лежат по всему файлу, и каждый запрос
доставал их с диска по одной (прочитано 11 ГБ, процессор на 2%). Теперь нужные
патчи сначала переливаются в маленькую базу — одним проходом по таблице, с
суммой по эло, — и запросы идут к ней.

Временная база с настоящей схемой (collect.init_db): три патча (старший в
выгрузку не попадает), три эло, несколько связок, строки вставлены вразнобой.
  1. по выжимке каждая связка выходит той же, что по рабочей базе;
  2. в выжимке только патчи выгрузки, эло сложены: строка на ключ и патч;
  3. рабочая таблица читается одним проходом, без индекса (с индексом SQLite
     снова идёт по строкам вразброс — ради этого всё и делалось);
  4. полный прогон кладёт в --out только файлы для сайта: выжимки там нет
     (папка целиком уезжает на сайт), временная папка убрана.
Рабочих баз не трогает.

    python tests/RunesExportFast/check.py        # 0 — всё хорошо, 1 — нет
"""
import random
import shutil
import sqlite3
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-runesfast-'))
sys.path.insert(0, str(ROOT / 'pipeline'))
sys.stdout.reconfigure(encoding='utf-8')
import collect                       # noqa: E402
import export_runes                  # noqa: E402
import export_snapshot               # noqa: E402

failures: list[str] = []
PATCHES = ['16.20', '16.21', '16.22']        # 16.20 — за окном выгрузки
TIERS = ['low', 'emerald', 'master']
CHAMPS = [(22, 'adc'), (22, 'support'), (98, 'top'), (64, 'jungle'), (7, 'mid')]
KS = [8008, 8005, 8021, 8437]
PAGES = ['{k},9111,9104,8014|8473,8242', '{k},9111,9105,8017|8345,8347',
         '{k},9101,9104,8299|8444,8242']
SHARDS = ['5005,5008,5011', '5005,5008,5013', '5008,5008,5001']
BUILDS = ['3006,3031,3046,3072', '3006,3031,3046,3072,3094', '3047,3075,3084,3190',
          '3031', '3006,3031']
SPELLS = ['4,7', '4,14', '4,12']


def check(ok: bool, msg: str):
    print(('  ok  ' if ok else '  FAIL ') + msg)
    if not ok:
        failures.append(msg)


rnd = random.Random(7)
rows = []
for patch in PATCHES:
    for tier in TIERS:
        for champ, role in CHAMPS:
            g = rnd.randint(300, 900)
            rows.append(('base_wr', (champ, role, tier, patch, g, g // 2)))
            for ks in KS:
                rows.append(('keystone_wr', (champ, role, ks, tier, patch,
                                             rnd.randint(20, 300), rnd.randint(10, 150))))
                for p in PAGES:
                    for s in SHARDS:
                        if rnd.random() < 0.7:
                            gg = rnd.randint(1, 120)
                            rows.append(('rune_page', (champ, role, ks, 8000, 8400, p.format(k=ks), s,
                                                       tier, patch, gg, rnd.randint(0, gg))))
                for vs in (1, 11, 55, 77):
                    gg = rnd.randint(1, 60)
                    rows.append(('keystone_matchup', (champ, role, vs, ks, tier, patch, gg, rnd.randint(0, gg))))
            for b in BUILDS:
                gg = rnd.randint(1, 200)
                rows.append(('item_build', (champ, role, b, tier, patch, gg, rnd.randint(0, gg))))
            for s in SPELLS:
                gg = rnd.randint(10, 300)
                rows.append(('spell_wr', (champ, role, s, tier, patch, gg, rnd.randint(0, gg))))
# Вразнобой — как в рабочей базе, где строки лежат в порядке вставки.
rnd.shuffle(rows)

con = collect.init_db(str(TMP / 'data.db'))
for table, vals in rows:
    con.execute(f'INSERT INTO {table} VALUES ({",".join("?" * len(vals))})', vals)
con.commit()
con.close()

ro = sqlite3.connect(f'file:{TMP / "data.db"}?mode=ro', uri=True)
ps = export_runes.patches(ro)
snap = export_snapshot.snapshot(str(TMP / 'data.db'), ps, TMP / 'snap.db', log=lambda s: None)

print('1. связки по выжимке — те же, что по рабочей базе')
same, total = 0, 0
for champ, role in CHAMPS:
    a = export_runes.build_champ_role(ro, champ, role, ps)
    b = export_runes.build_champ_role(snap, champ, role, ps)
    total += a is not None
    same += a is not None and a == b
check(total == len(CHAMPS) and same == total,
      f'совпало {same} из {total} (связок с данными {total} из {len(CHAMPS)})')

print('2. в выжимке — только патчи выгрузки, эло сложены')
for table, key in export_snapshot.SNAPSHOT.items():
    cols = ', '.join(key)
    got = snap.execute(f'SELECT COUNT(*), COUNT(DISTINCT patch) FROM {table}').fetchone()
    want = ro.execute(f'SELECT COUNT(*) FROM (SELECT DISTINCT {cols}, patch FROM {table} '
                      f'WHERE patch IN ({",".join("?" * len(ps))}))', ps).fetchone()[0]
    sums = snap.execute(f'SELECT SUM(games), SUM(wins) FROM {table}').fetchone()
    raw = ro.execute(f'SELECT SUM(games), SUM(wins) FROM {table} '
                     f'WHERE patch IN ({",".join("?" * len(ps))})', ps).fetchone()
    check(got == (want, len(ps)) and sums == raw,
          f'{table}: строк {got[0]} (ждали {want}), патчей {got[1]} из {ps}, игры {sums} = {raw}')

print('3. рабочая таблица читается подряд, без индекса')
ro.close()
snap.close()
plan_con = sqlite3.connect(str(TMP / 'plan.db'), uri=True)
plan_con.execute('ATTACH DATABASE ? AS src', (f'file:{TMP / "data.db"}?mode=ro',))
for table, key in export_snapshot.SNAPSHOT.items():
    plan_con.execute(f'CREATE TABLE main.{table} ({", ".join(key)}, patch, games, wins)')
    plan = ' | '.join(r[-1] for r in plan_con.execute(
        'EXPLAIN QUERY PLAN ' + export_snapshot.snapshot_sql(table, ps), ps))
    check('INDEX' not in plan.upper() and 'SCAN' in plan.upper(), f'{table}: {plan}')
plan_con.close()

print('4. полный прогон: в --out только файлы для сайта')
before = set(Path(tempfile.gettempdir()).glob('cp-runes-*'))
argv = sys.argv
sys.argv = ['export_runes.py', '--db', str(TMP / 'data.db'), '--out', str(TMP / 'out')]
try:
    export_runes.main()
finally:
    sys.argv = argv
files = sorted(p.relative_to(TMP / 'out').as_posix() for p in (TMP / 'out').rglob('*') if p.is_file())
stray = [f for f in files if not f.endswith('.json')]
left = set(Path(tempfile.gettempdir()).glob('cp-runes-*')) - before
check(not stray and len(files) == len(CHAMPS) + 1,
      f'файлов {len(files)} (ждали {len(CHAMPS)} связок и манифест), лишние: {stray or "нет"}')
check(not left, f'временная папка выжимки убрана: {[p.name for p in left] or "да"}')

shutil.rmtree(TMP, ignore_errors=True)
print()
print('ИТОГ: выгрузка считает по выжимке' if not failures else f'ИТОГ: провалено — {len(failures)}')
sys.exit(1 if failures else 0)
