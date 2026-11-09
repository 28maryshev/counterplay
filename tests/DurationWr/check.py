# -*- coding: utf-8 -*-
"""
DurationWr — сборщик пишет силу чемпиона по времени игры.

Повод (09.11.2026): владелец — «делай всё» по предложениям, среди них «сила
по времени игры из данных, а не меток». До этого ранняя и поздняя сила
держались на ручных метках aggressive/scale, и на миде не было ни одного
раннего чемпиона. Сборщик теперь пишет таблицу duration_wr: игры и победы
чемпиона на роли по отрезкам длительности матча (0 — до 25 минут, 1 — 25–35,
2 — дольше 35); ремейки (досрочная сдача) не пишет.

Меряет на временной базе с настоящей схемой (collect.init_db) и настоящим
разбором матча (collect.process_match): четыре синтетических ранкед-матча —
20, 30 и 40 минут и ремейк на 4-й минуте. Ждём: у каждого из десяти
чемпионов ровно по игре в отрезках 0, 1, 2, победы — у тех, кто выиграл, от
ремейка — ничего; таблица уезжает в базу игрокам (publish_data.ENGINE_TABLES).
Рабочих баз не трогает, сети не нужно.

    python tests/DurationWr/check.py        # 0 — всё хорошо, 1 — нет
"""
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-durationwr-'))
os.environ.update(DB_PATH=str(TMP / 'none.db'), CONTROL_DIR=str(TMP / 'control'),
                  GITHUB_TOKEN='test', DISCORD_WEBHOOK='')
sys.path.insert(0, str(ROOT / 'pipeline'))
sys.stdout.reconfigure(encoding='utf-8')
import collect          # noqa: E402
import publish_data     # noqa: E402

failures = []
ROLES = ['TOP', 'JUNGLE', 'MIDDLE', 'BOTTOM', 'UTILITY']


def match(mid: str, seconds: int, remake: bool = False) -> dict:
    """Десять участников: команда 100 — чемпионы 1..5 (выиграла), 200 — 11..15."""
    parts = []
    for team, base, win in ((100, 1, True), (200, 11, False)):
        for i, pos in enumerate(ROLES):
            parts.append({'teamId': team, 'teamPosition': pos, 'championId': base + i, 'win': win,
                          'gameEndedInEarlySurrender': remake})
    return {'metadata': {'matchId': f'EUW1_{mid}'},
            'info': {'queueId': collect.QUEUE_RANKED, 'gameVersion': '16.21.1.1',
                     'gameDuration': seconds, 'participants': parts, 'teams': []}}


def check(what: str, ok: bool, detail: str):
    print(f"  [{'ок' if ok else 'ПЛОХО'}] {what}  — {detail}")
    if not ok:
        failures.append(what)


con = collect.init_db(str(TMP / 'data.db'))
for mid, sec, rk in (('1', 20 * 60, False), ('2', 30 * 60, False), ('3', 40 * 60, False), ('4', 4 * 60, True)):
    collect.process_match(con, match(mid, sec, rk), 'emerald', 'EMERALD')
con.commit()

rows = con.execute("SELECT champion_id, role, bucket, games, wins FROM duration_wr").fetchall()
by = {}
for c, r, b, g, w in rows:
    by.setdefault(c, {})[b] = (g, w)
champs = sorted(by)
check("у всех десяти чемпионов есть строки", champs == list(range(1, 6)) + list(range(11, 16)),
      f"чемпионы {champs}")
bad = [c for c in champs if sorted(by[c]) != [0, 1, 2] or any(g != 1 for g, _ in by[c].values())]
check("по одной игре в отрезках «до 25», «25–35», «после 35»", not bad, "да" if not bad else f"не так у {bad}")
wins = [c for c in champs if all(w == (1 if c < 10 else 0) for _, w in by[c].values())]
check("победы — у выигравшей команды", len(wins) == 10, f"{len(wins)} из 10")
games = sum(g for *_, g, _w in rows)
check("ремейк не записан", games == 30, f"игр в таблице {games} (ждали 30: 3 матча × 10)")
check("таблица уезжает в базу игрокам", 'duration_wr' in publish_data.ENGINE_TABLES,
      ", ".join(publish_data.ENGINE_TABLES))

print()
print("ИТОГ: сила по времени пишется" if not failures else f"ИТОГ: провалено — {len(failures)}")
sys.exit(1 if failures else 0)
