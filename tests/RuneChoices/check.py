# -*- coding: utf-8 -*-
"""
RuneChoices — варианты рун в файле связки: только то, что на этой роли правда
берут, и доли мелких рун для подбора под состав (журнал 2026-11-19).

Повод (владелец, 19.11): у Эш стрелком первым вариантом стояла Хватка нежити —
117 игр из 268 тысяч, 0,04%. Данные и так по роли, но порога по доле у
кейстоуна не было: подходил любой от 30 игр, а в программе варианты идут по
винрейту, и редкий кейстоун с удачным винрейтом выходил наверх. Так было у 103
связок из 724.

  1. редкий кейстоун (меньше MIN_KEYSTONE_SHARE игр связки) в файл не попадает,
     даже с лучшим винрейтом; ходовые — на месте, доля «берут в N%» считается
     от всех игр связки, как раньше;
  2. у каждого кейстоуна — доли мелких рун: основного дерева от игр кейстоуна,
     вторичного — от игр с тем же вторичным деревом. По ним подбор рун в
     программе берёт только то, что на этом чемпионе играют. Осколков в долях
     нет: стойкость под контроль берут все;
  3. руны реже MIN_SHARE_OUT в долях не пишем: файл не раздувается.

Временная база с настоящей схемой (collect.init_db). Рабочих баз не трогает.

    python tests/RuneChoices/check.py        # 0 — всё хорошо, 1 — нет
"""
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-runechoices-'))
sys.path.insert(0, str(ROOT / 'pipeline'))
import collect                       # noqa: E402
import export_runes                  # noqa: E402

failures: list[str] = []
PATCH = '16.22'
TIER = 'emerald'
ASHE, ROLE = 22, 'adc'
LT, PTA, GRASP = 8008, 8005, 8437


def check(ok: bool, msg: str):
    print(('  ok  ' if ok else '  FAIL ') + msg)
    if not ok:
        failures.append(msg)


def page(con, ks, prim, sub, perks, shards, games, wins):
    con.execute('INSERT INTO rune_page VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                (ASHE, ROLE, ks, prim, sub, perks, shards, TIER, PATCH, games, wins))


con = collect.init_db(str(TMP / 'data.db'))
TOTAL = 10000
con.execute('INSERT INTO base_wr VALUES (?,?,?,?,?,?)', (ASHE, ROLE, TIER, PATCH, TOTAL, TOTAL // 2))

# Как у Эш на 16.21–16.22: Темп почти у всех, Натиск заметно реже, Хватку —
# 40 игр (0,4%) и 60% побед: по винрейту она первая.
for ks, g, w in ((LT, 8960, 4490), (PTA, 1000, 474), (GRASP, 40, 24)):
    con.execute('INSERT INTO keystone_wr VALUES (?,?,?,?,?,?,?)', (ASHE, ROLE, ks, TIER, PATCH, g, w))

# Страницы Темпа: третья руна Точности — Удар милосердия (6000) или Резня (2960);
# вторичное Решимость (Костяная броня / Второе дыхание + Неудержимость) или
# Вдохновение. Осколки третьей ячейки — здоровье или стойкость.
page(con, LT, 8000, 8400, '8008,9111,9104,8014|8473,8242', '5005,5008,5011', 4000, 2010)
page(con, LT, 8000, 8400, '8008,9111,9104,8017|8444,8242', '5005,5008,5013', 1960, 990)
page(con, LT, 8000, 8300, '8008,9111,9104,8014|8345,8347', '5005,5008,5011', 2000, 1000)
page(con, LT, 8000, 8300, '8008,9111,9104,8017|8345,8347', '5005,5008,5011', 990, 480)
page(con, LT, 8000, 8300, '8008,9101,9104,8299|8345,8347', '5005,5008,5011', 10, 5)   # экзотика
page(con, PTA, 8000, 8300, '8005,9111,9104,8014|8345,8347', '5005,5008,5011', 1000, 474)
page(con, GRASP, 8400, 8000, '8437,8446,8444,8451|9111,9104', '5008,5008,5011', 40, 24)
con.commit()
con.close()

ro = export_runes.sqlite3.connect(f'file:{TMP / "data.db"}?mode=ro', uri=True)
ps = export_runes.patches(ro)
d = export_runes.build_champ_role(ro, ASHE, ROLE, ps) or {}
ks = {k['id']: k for k in d.get('keystones', [])}

print('1. редкий кейстоун')
check(GRASP not in ks, f'Хватки (0,4% игр, 60% побед) в вариантах нет: {sorted(ks)}')
check(LT in ks and PTA in ks, f'Темп и Натиск на месте: {sorted(ks)}')
if PTA in ks:
    check(abs(ks[PTA]['pick'] - 10.0) < 0.1,
          f'«берут в N%» — от всех игр связки: Натиск {ks[PTA]["pick"]}% (ждали 10.0)')

print('2. доли мелких рун')
lt = ks.get(LT, {})
share = {int(k): v for k, v in (lt.get('share') or {}).items()}
sub = {int(s): {int(k): v for k, v in m.items()} for s, m in (lt.get('subShare') or {}).items()}
check(abs(share.get(8014, 0) - 67.0) < 0.2 and abs(share.get(8017, 0) - 33.0) < 0.2,
      f'Удар милосердия 67%, Резня 33% от игр Темпа: {share.get(8014)} / {share.get(8017)}')
check(8008 not in share, 'кейстоуна в долях нет — он и так известен')
res = sub.get(8400, {})
check(abs(res.get(8473, 0) - 67.1) < 0.2 and abs(res.get(8444, 0) - 32.9) < 0.2,
      f'вторичное Решимость: Костяная броня 67%, Второе дыхание 33% — от игр с Решимостью: {res}')
check(abs(sub.get(8300, {}).get(8345, 0) - 100.0) < 0.1,
      f'вторичное Вдохновение — от своих игр: {sub.get(8300)}')
check('shardShare' not in lt, 'долей осколков нет')

print('3. экзотика в доли не попадает')
check(8299 not in share and 9101 not in share,
      f'Последний рубеж и Поглощение жизни (0,1%) не пишем: {sorted(share)}')

ro.close()
shutil.rmtree(TMP, ignore_errors=True)
print()
print('ВСЁ ХОРОШО' if not failures else f'ПРОВАЛЕНО: {len(failures)}')
sys.exit(1 if failures else 0)
