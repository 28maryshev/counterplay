# -*- coding: utf-8 -*-
"""
RuneMatchup — сборщик пишет мелкие руны против соперников на линии.

Повод (19.11.2026): владелец — «делай все пункты», среди них «собирать цифры
„руна против оппонента“, чтобы проверить правила подбора рун». Подбор в
программе ставит мелкие руны правилами по гайдам (против наскока — Костяная
броня, против поука — Второе дыхание); собиралось же «против соперника»
только для кейстоуна (keystone_matchup).

Меряет на временной базе с настоящей схемой (collect.init_db) и настоящим
разбором матча (collect.process_match): один ранкед-матч, у всех одна
страница рун (Темп: Триумф, Легенда: Алчность, Удар милосердия | Костяная
пластина, Неудержимость). Ждём:
  1. у каждого — кейстоун и пять мелких рун против соперника (кейстоун — с
     20.11: на боте против второго бот-лейнера его больше нигде нет, а
     владелец — «руны на боте должны учитывать пики двух врагов»);
  2. на топе, в лесу, миде — соперник той же роли;
  3. на боте — оба вражеских бот-лейнера: у стрелка и стрелок, и саппорт;
  4. без разбивки по эло (в ключе нет tier_bucket), победы — у выигравших;
  5. в базу игрокам таблица не уезжает (её читает только выгрузка рун).
Рабочих баз не трогает, сети не нужно.

    python tests/RuneMatchup/check.py        # 0 — всё хорошо, 1 — нет
"""
import os
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-runematchup-'))
os.environ.update(DB_PATH=str(TMP / 'none.db'), CONTROL_DIR=str(TMP / 'control'),
                  GITHUB_TOKEN='test', DISCORD_WEBHOOK='')
sys.path.insert(0, str(ROOT / 'pipeline'))
sys.stdout.reconfigure(encoding='utf-8')
import collect          # noqa: E402
import publish_data     # noqa: E402

failures = []
ROLES = ['TOP', 'JUNGLE', 'MIDDLE', 'BOTTOM', 'UTILITY']
RUNES = [8008, 9111, 9104, 8014, 8473, 8242]   # кейстоун Темп и пять мелких


def perks() -> dict:
    return {'statPerks': {'offense': 5005, 'flex': 5008, 'defense': 5011},
            'styles': [{'description': 'primaryStyle', 'style': 8000,
                        'selections': [{'perk': x} for x in (8008, 9111, 9104, 8014)]},
                       {'description': 'subStyle', 'style': 8400,
                        'selections': [{'perk': x} for x in (8473, 8242)]}]}


def match() -> dict:
    """Команда 100 — чемпионы 1..5 (выиграла), 200 — 11..15; роли по порядку."""
    parts = []
    for team, base, win in ((100, 1, True), (200, 11, False)):
        for i, pos in enumerate(ROLES):
            parts.append({'teamId': team, 'teamPosition': pos, 'championId': base + i, 'win': win,
                          'perks': perks()})
    return {'metadata': {'matchId': 'EUW1_1'},
            'info': {'queueId': collect.QUEUE_RANKED, 'gameVersion': '16.22.1.1',
                     'gameDuration': 30 * 60, 'participants': parts, 'teams': []}}


def check(what: str, ok: bool, detail: str):
    print(f"  [{'ок' if ok else 'ПЛОХО'}] {what}  — {detail}")
    if not ok:
        failures.append(what)


con = collect.init_db(str(TMP / 'data.db'))
collect.process_match(con, match(), 'emerald', 'EMERALD')
con.commit()

rows = con.execute("SELECT champion_id, role, vs_champion_id, vs_role, perk, games, wins "
                   "FROM rune_matchup").fetchall()
by = {}
for c, r, vs, vr, perk, g, w in rows:
    by.setdefault((c, r), {}).setdefault((vs, vr), set()).add(perk)

top = by.get((1, 'top'), {})
check("у каждого — кейстоун и пять мелких рун против соперника",
      top == {(11, 'top'): set(RUNES)}, f"топ 1: {top}")
check("топ, лес, мид — соперник той же роли",
      set(by.get((2, 'jungle'), {})) == {(12, 'jungle')} and set(by.get((13, 'mid'), {})) == {(3, 'mid')},
      f"лес 2: {sorted(by.get((2, 'jungle'), {}))}, мид 13: {sorted(by.get((13, 'mid'), {}))}")
check("бот — оба вражеских бот-лейнера",
      set(by.get((4, 'adc'), {})) == {(14, 'adc'), (15, 'support')}
      and set(by.get((15, 'support'), {})) == {(4, 'adc'), (5, 'support')},
      f"стрелок 4: {sorted(by.get((4, 'adc'), {}))}, саппорт 15: {sorted(by.get((15, 'support'), {}))}")
wins_ok = all(w == (1 if c < 10 else 0) and g == 1 for c, _, _, _, _, g, w in rows)
cols = [c[1] for c in con.execute('PRAGMA table_info(rune_matchup)')]
check("без разбивки по эло, победы — у выигравших",
      wins_ok and 'tier_bucket' not in cols and len(rows) == 10 * 6 + 4 * 6,
      f"строк {len(rows)} (ждали 84: 10 × 6 рун + у четверых бот-лейнеров второй соперник), колонки {cols}")
check("в базу игрокам не уезжает", 'rune_matchup' not in publish_data.ENGINE_TABLES,
      ", ".join(publish_data.ENGINE_TABLES))

print()
print("ИТОГ: руны против соперников пишутся" if not failures else f"ИТОГ: провалено — {len(failures)}")
sys.exit(1 if failures else 0)
