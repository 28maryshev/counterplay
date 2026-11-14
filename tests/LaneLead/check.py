# -*- coding: utf-8 -*-
"""
LaneLead — сборщик пишет исход линии в паре.

Повод (14.11.2026): владелец выбрал пункт 3 разбора — «линия в паре у
сборщика и на ней слабая сторона как дыра состава с проверкой через
calibrate.py». MATCH-V5 уже отдаёт в participants[].challenges флаг
laningPhaseGoldExpAdvantage («выиграл линию с явным перевесом», у соперников
взаимоисключающий) и миньонов к 10-й минуте. Сборщик кладёт их в matchup
(lane_games, lane_won, lane_lost, cs10_diff) и в слоты драфта (l: 1/−1/0) —
калибровке для вычитания самого матча из агрегатов.

Меряет на временной базе с настоящей схемой (collect.init_db) и настоящим
разбором (collect.process_match):
  * топ синих выиграл линию, мид красных — свою, бот ровно, у лесника красных
    нет challenges — линия у него не пишется, а матчап пишется;
  * обе стороны пары: выигравшему won, проигравшему lost, разница CS со знаком;
  * ремейк: игра в matchup есть, линии нет;
  * слоты драфта: l у всех, кроме лесников;
  * старая база без колонок получает их при запуске, и новые матчи пишутся;
  * база игрокам (build_slim) колонок линии не несёт, остальное на месте.
Рабочих баз не трогает, сети не нужно.

    python tests/LaneLead/check.py        # 0 — всё хорошо, 1 — нет
"""
import json
import os
import sqlite3
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-lanelead-'))
os.environ.update(DB_PATH=str(TMP / 'none.db'), CONTROL_DIR=str(TMP / 'control'),
                  GITHUB_TOKEN='test', DISCORD_WEBHOOK='')
sys.path.insert(0, str(ROOT / 'pipeline'))
sys.stdout.reconfigure(encoding='utf-8')
import collect          # noqa: E402
import publish_data     # noqa: E402

failures = []
ROLES = ['TOP', 'JUNGLE', 'MIDDLE', 'BOTTOM', 'UTILITY']
# Флаг линии (синие, красные) и миньоны к 10-й (синие, красные) по ролям.
LANE = {'TOP': ((1, 0), (80, 62)), 'JUNGLE': ((0, None), (50, 44)), 'MIDDLE': ((0, 1), (70, 79)),
        'BOTTOM': ((0, 0), (65, 65)), 'UTILITY': ((0, 0), (10, 8))}


def match(mid: str, remake: bool = False) -> dict:
    """Синие (100) — чемпионы 1..5, выиграли; красные (200) — 11..15."""
    parts = []
    for team, base, win, side in ((100, 1, True, 0), (200, 11, False, 1)):
        for i, pos in enumerate(ROLES):
            flag, cs = LANE[pos][0][side], LANE[pos][1][side]
            ch = None if flag is None else {
                'laningPhaseGoldExpAdvantage': flag,
                'jungleCsBefore10Minutes' if pos == 'JUNGLE' else 'laneMinionsFirst10Minutes': cs}
            p = {'teamId': team, 'teamPosition': pos, 'championId': base + i, 'win': win,
                 'gameEndedInEarlySurrender': remake}
            if ch is not None:
                p['challenges'] = ch
            parts.append(p)
    return {'metadata': {'matchId': f'EUW1_{mid}'},
            'info': {'queueId': collect.QUEUE_RANKED, 'gameVersion': '16.22.1.1',
                     'gameDuration': 4 * 60 if remake else 30 * 60, 'participants': parts, 'teams': []}}


def check(what: str, ok: bool, detail: str):
    print(f"  [{'ок' if ok else 'ПЛОХО'}] {what}  — {detail}")
    if not ok:
        failures.append(what)


def pair(con, a, b):
    return con.execute("SELECT games, wins, lane_games, lane_won, lane_lost, cs10_diff FROM matchup "
                       "WHERE champion_id=? AND vs_champion_id=?", (a, b)).fetchone()


print('линия в паре:')
db = TMP / 'data.db'
con = collect.init_db(str(db))
collect.process_match(con, match('1'), 'emerald', 'EMERALD')
collect.process_match(con, match('2', remake=True), 'emerald', 'EMERALD')
con.commit()

top, top_r = pair(con, 1, 11), pair(con, 11, 1)
check("топ: выигравший — won, проигравший — lost, по одной игре линии (ремейк не в счёт)",
      top == (2, 2, 1, 1, 0, 18) and top_r == (2, 0, 1, 0, 1, -18), f"синий {top}, красный {top_r}")
mid = pair(con, 3, 13)
check("мид: проигравший синий — lost, CS −9", mid == (2, 2, 1, 0, 1, -9), f"{mid}")
bot = pair(con, 4, 14)
check("бот ровно: игра линии есть, ни won, ни lost", bot == (2, 2, 1, 0, 0, 0), f"{bot}")
jg = pair(con, 2, 12)
check("у лесника красных нет challenges: матчап пишется, линия — нет", jg == (2, 2, 0, 0, 0, 0), f"{jg}")

slots = json.loads(con.execute("SELECT slots FROM drafts WHERE match_id='EUW1_1'").fetchone()[0])
ls = {(s['t'], s['r']): s.get('l') for s in slots}
check("слоты драфта: исход линии у всех, кроме лесников",
      ls[(100, 'top')] == 1 and ls[(200, 'top')] == -1 and ls[(100, 'mid')] == -1 and ls[(200, 'mid')] == 1
      and ls[(100, 'adc')] == 0 and ls[(100, 'jungle')] is None and ls[(200, 'jungle')] is None,
      f"{ls}")
con.close()

print('\nстарая база:')
old = TMP / 'old.db'
o = sqlite3.connect(old)
o.execute("""CREATE TABLE matchup (champion_id INTEGER, role TEXT, vs_champion_id INTEGER, tier_bucket TEXT,
             patch TEXT, games INTEGER DEFAULT 0, wins INTEGER DEFAULT 0,
             PRIMARY KEY (champion_id, role, vs_champion_id, tier_bucket, patch))""")
o.execute("INSERT INTO matchup VALUES (1, 'top', 11, 'emerald', '16.22', 40, 22)")
o.commit()
o.close()
con = collect.init_db(str(old))
cols = [c[1] for c in con.execute('PRAGMA table_info(matchup)')]
check("колонки линии досозданы", all(c in cols for c in collect.LANE_COLS), ", ".join(cols))
collect.process_match(con, match('3'), 'emerald', 'EMERALD')
con.commit()
check("старая строка дописалась: 41 игра, линия — 1 из 1",
      pair(con, 1, 11) == (41, 23, 1, 1, 0, 18), f"{pair(con, 1, 11)}")
con.close()

print('\nбаза игрокам:')
slim = TMP / 'slim.db'
publish_data.build_slim(db, slim, ['16.22'], bucket='emerald', matches=2)
s = sqlite3.connect(slim)
scols = [c[1] for c in s.execute('PRAGMA table_info(matchup)')]
check("колонок линии в базе игрокам нет", not any(c in scols for c in collect.LANE_COLS), ", ".join(scols))
row = s.execute("SELECT games, wins FROM matchup WHERE champion_id=1 AND vs_champion_id=11 "
                "AND tier_bucket='emerald'").fetchone()
check("а матчапы на месте", row == (2, 2), f"{row}")
allrow = s.execute("SELECT games, wins FROM matchup WHERE champion_id=1 AND vs_champion_id=11 "
                   "AND tier_bucket='all'").fetchone()
check("и сводка по всем дивизионам", allrow == (2, 2), f"{allrow}")
s.close()

print('\nкалибровка (calibrate.py --lane) на синтетических матчах:')
# Восемь чемпионов на роль; у каждого линейного — своя хрупкость (шанс проиграть
# линию). Команда, у которой хрупки и топ, и бот («нет слабой стороны»), сверх
# того проигрывает: логит −0,4 (около −10 пп). Агрегаты — из тех же матчей,
# так что калибровка обязана вычесть сам матч, иначе обманется.
import random     # noqa: E402
import subprocess  # noqa: E402
rnd = random.Random(14)
CAL_ROLES = ['top', 'jungle', 'mid', 'adc', 'support']
champs = {r: [100 * i + k for k in range(8)] for i, r in enumerate(CAL_ROLES)}
frag = {c: rnd.uniform(0.04, 0.26) for r in ('top', 'mid', 'adc') for c in champs[r]}
mean = {r: sum(frag[c] for c in champs[r]) / 8 for r in ('top', 'mid', 'adc')}
sl = sqlite3.connect(TMP / 'slice.db')
sl.execute("CREATE TABLE base_wr (champion_id, role, tier_bucket, patch, games, wins)")
sl.execute("CREATE TABLE botlane_matchup (champion_id, role, vs_champion_id, vs_role, tier_bucket, patch, games, wins)")
sl.execute("CREATE TABLE drafts (match_id, tier_bucket, patch, win_team, slots)")
mu, base, cross = {}, {}, {}
for n in range(6000):
    A = {r: rnd.choice(champs[r]) for r in CAL_ROLES}
    B = {r: rnd.choice(champs[r]) for r in CAL_ROLES}
    lane, logit = {}, 0.0
    for r in ('top', 'mid', 'adc'):
        u = rnd.random()
        lane[r] = -1 if u < frag[A[r]] else 1 if u < frag[A[r]] + frag[B[r]] else 0
        logit += 0.15 * lane[r]
    gap = lambda T: frag[T['top']] > mean['top'] and frag[T['adc']] > mean['adc']  # noqa: E731
    logit += -0.4 * gap(A) + 0.4 * gap(B)
    wa = rnd.random() < 1 / (1 + 2.718281828 ** -logit)
    slots = []
    for team, T, O, won, sign in ((100, A, B, wa, 1), (200, B, A, not wa, -1)):
        for r in CAL_ROLES:
            g, w = base.get((T[r], r), (0, 0))
            base[(T[r], r)] = (g + 1, w + won)
            k = (T[r], r, O[r])
            g, w, lg, lw, ll = mu.get(k, (0, 0, 0, 0, 0))
            l = sign * lane[r] if r in lane else None
            mu[k] = (g + 1, w + won, lg + (l is not None), lw + (l == 1), ll + (l == -1))
            slots.append({'t': team, 'r': r, 'c': T[r], **({'l': l} if l is not None else {})})
            for vr in CAL_ROLES:   # пары разных ролей: без них признаки контр вырождены
                if vr != r:
                    g, w = cross.get((T[r], r, O[vr], vr), (0, 0))
                    cross[(T[r], r, O[vr], vr)] = (g + 1, w + won)
    sl.execute("INSERT INTO drafts VALUES (?, 'emerald', '16.22', ?, ?)", (f'S_{n}', 100 if wa else 200, json.dumps(slots)))
sl.execute("""CREATE TABLE matchup (champion_id, role, vs_champion_id, tier_bucket, patch, games, wins,
              lane_games, lane_won, lane_lost, cs10_diff)""")
sl.executemany("INSERT INTO matchup VALUES (?, ?, ?, 'emerald', '16.22', ?, ?, ?, ?, ?, 0)",
               [(*k, *v) for k, v in mu.items()])
sl.executemany("INSERT INTO botlane_matchup VALUES (?, ?, ?, ?, 'emerald', '16.22', ?, ?)",
               [(*k, *v) for k, v in cross.items()])
sl.executemany("INSERT INTO base_wr VALUES (?, ?, 'emerald', '16.22', ?, ?)", [(*k, *v) for k, v in base.items()])
sl.commit()
sl.close()


def calibrate(*extra):
    r = subprocess.run([sys.executable, str(ROOT / 'pipeline' / 'calibrate.py'), '--db', str(TMP / 'slice.db'),
                        '--min-drafts', '1000', *extra], capture_output=True, text=True, encoding='utf-8',
                       env={**os.environ, 'PYTHONIOENCODING': 'utf-8'})
    return r.stdout + r.stderr


out = calibrate('--lane')
gap_line = next((ln for ln in out.splitlines() if ln.startswith('нет слабой стороны')), '')
import re          # noqa: E402
m = re.search(r':\s+(-?\d+[.,]\d+) ± (\d+[.,]\d+)', gap_line)
est, err = (float(m.group(1).replace(',', '.')), float(m.group(2).replace(',', '.'))) if m else (0.0, 99.0)
check("«нет слабой стороны» найдено: заложено около −10 пп, оценка ниже −4 и дальше двух ошибок от нуля",
      est < -4 and abs(est) > 2 * err, gap_line.strip() or out[-400:])
plain = calibrate()
check("без --lane вывод прежний — про линию ни строки", 'слабой стороны' not in plain and 'линия (' not in plain,
      'есть строка про линию' if 'слабой стороны' in plain else 'да')
sl = sqlite3.connect(TMP / 'slice.db')
sl.execute("ALTER TABLE matchup RENAME TO matchup_lane")
sl.execute("CREATE TABLE matchup AS SELECT champion_id, role, vs_champion_id, tier_bucket, patch, games, wins "
           "FROM matchup_lane")
sl.commit()
sl.close()
check("в выжимке без колонок линии — «не мерю», а не падение", 'не мерю' in calibrate('--lane'), "")

print()
print("ИТОГ: линия в паре пишется" if not failures else f"ИТОГ: провалено — {len(failures)}")
sys.exit(1 if failures else 0)
