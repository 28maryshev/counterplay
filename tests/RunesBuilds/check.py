# -*- coding: utf-8 -*-
"""
RunesBuilds — сборка в файле рун есть и тогда, когда игры дробятся по наборам
(журнал 2026-11-02).

Сборка-кандидат — ТОЧНЫЙ набор из четырёх и больше предметов, и ему нужно не
меньше 1% игр чемпиона. Набор «A,B,C,D» не засчитывает игр, где к нему
докупили пятый предмет, поэтому у чемпионов с разнообразными сборками ни один
набор порога не проходил: у Шена в поддержке лучший набирал 0.87%, у Пантеона
на миде 0.89%, и панель в программе стояла без сборки — так было у 156 связок
из 710. Теперь, если строгий отбор пуст, ядро собирается жадно по играм, где
оно куплено целиком (export_runes.greedy_core).

Три связки:
  1. раздробленная: ни одна строка не набирает MIN_BUILD — сборка всё равно
     есть, её ядро — четыре предмета, которые покупают вместе, игры — все игры
     с этим ядром, слотов шесть, обуви одна пара;
  2. с явной сборкой: строгий путь как был — сборки те же, что без запасного
     пути;
  3. крошечная: вместе не покупают ничего и на 25 игр — сборки нет, выдумывать
     её нельзя.

Временная база с настоящей схемой (collect.init_db). Рабочих баз не трогает.

    python tests/RunesBuilds/check.py        # 0 — всё хорошо, 1 — нет
"""
import shutil
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TMP = Path(tempfile.mkdtemp(prefix='cp-runesbuilds-'))
sys.path.insert(0, str(ROOT / 'pipeline'))
import collect                       # noqa: E402
import export_runes                  # noqa: E402

failures: list[str] = []
PATCH = '16.21'
TIER = 'emerald'
BOOTS = (3047, 3111)                 # обувь и в справочнике Riot, и в запасном списке


def check(ok: bool, msg: str):
    print(('  ok  ' if ok else '  FAIL ') + msg)
    if not ok:
        failures.append(msg)


def champ(con, cid: int, role: str, games: int, builds: list[tuple[list[int], int]]):
    """Связка с одним кейстоуном и одной страницей рун — чтобы дошло до сборок."""
    wins = games // 2
    con.execute('INSERT INTO base_wr VALUES (?,?,?,?,?,?)', (cid, role, TIER, PATCH, games, wins))
    con.execute('INSERT INTO keystone_wr VALUES (?,?,?,?,?,?,?)',
                (cid, role, 8437, TIER, PATCH, games, wins))
    con.execute('INSERT INTO rune_page VALUES (?,?,?,?,?,?,?,?,?,?,?)',
                (cid, role, 8437, 8400, 8300, '8437,8401,8473,8242|8304,8347',
                 '5008,5001,5001', TIER, PATCH, games, wins))
    for items, g in builds:
        con.execute('INSERT INTO item_build VALUES (?,?,?,?,?,?,?)',
                    (cid, role, ','.join(map(str, sorted(items))), TIER, PATCH, g, g // 2))


con = collect.init_db(str(TMP / 'data.db'))

# 1. Раздробленная (как Шен в поддержке): ядро покупают почти всегда, но с
#    разными докупками, и ни одна точная строка не дотягивает до 25 игр.
CORE = [3047, 3075, 3084, 3190]
EXTRAS = [3065, 3068, 3143, 3110, 3742, 6665, 3193, 2504]
split = [(CORE, 15)]
split += [(CORE + [e], 18) for e in EXTRAS]
split += [(CORE + [EXTRAS[i], EXTRAS[i + 1]], 12) for i in range(6)]
split += [([3047, 3068, 3143, 3110], 19),           # длинный, но без ядра
          ([3047, 3075, 3084], 300),                # обрывок ядра
          ([3047, 3075], 600), ([3047], 400)]
champ(con, 98, 'support', 2000, split)
core_games = 15 + 18 * len(EXTRAS) + 12 * 6

# 2. С явной сборкой: точный набор сам проходит пороги, а рядом его же с
#    докупкой — строгий путь должен взять точный набор и его игры.
CLEAR = [3047, 3071, 6333, 6692]
champ(con, 64, 'jungle', 2000, [(CLEAR, 300), (CLEAR + [3026], 100),
                                ([3111, 3071, 6610, 6692], 120), ([3047, 3071], 500)])

# 3. Крошечная: наборы разные, вместе ни на 25 игр.
tiny = [([1000 + i, 2000 + i, 3000 + i, 4000 + i], 9) for i in range(20)]
champ(con, 7, 'mid', 220, tiny)
con.commit()
con.close()

ro = export_runes.sqlite3.connect(f'file:{TMP / "data.db"}?mode=ro', uri=True)
ps = export_runes.patches(ro)

print('1. раздробленная связка')
d = export_runes.build_champ_role(ro, 98, 'support', ps)
b = (d or {}).get('builds') or []
check(len(b) == 1, f'сборка есть, когда ни один точный набор не прошёл пороги (сборок: {len(b)})')
if b:
    check(b[0]['core'] == sorted(CORE), f'ядро — то, что покупают вместе: {b[0]["core"]}')
    check(b[0]['games'] == core_games,
          f'игры — все игры, где ядро куплено целиком: {b[0]["games"]} (ждали {core_games})')
    check(len(b[0]['items']) == 6, f'шесть слотов: {b[0]["items"]}')
    check(sum(i in BOOTS for i in b[0]['items']) == 1, f'обувь одна: {b[0]["items"]}')
    check(abs(b[0]['wr'] - d['wr']) < 5, f'винрейт сборки рядом с чемпионом: {b[0]["wr"]} при {d["wr"]}')

print('2. связка с явной сборкой')
d = export_runes.build_champ_role(ro, 64, 'jungle', ps)
b = (d or {}).get('builds') or []
greedy = export_runes.greedy_core
export_runes.greedy_core = lambda *a: None          # строгий путь сам по себе
plain = (export_runes.build_champ_role(ro, 64, 'jungle', ps) or {}).get('builds') or []
export_runes.greedy_core = greedy
check(bool(b) and b == plain, f'строгий путь как был: {len(b)} сборок, без запасного пути {len(plain)}')

print('3. крошечная связка')
d = export_runes.build_champ_role(ro, 7, 'mid', ps)
check(d is not None, 'связка выгружается (руны есть)')
check(d is not None and d['builds'] == [], f'сборку не выдумываем: {d["builds"] if d else None}')

ro.close()
shutil.rmtree(TMP, ignore_errors=True)
print()
print('ВСЁ ХОРОШО' if not failures else f'ПРОВАЛЕНО: {len(failures)}')
sys.exit(1 if failures else 0)
