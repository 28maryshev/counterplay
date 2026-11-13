# -*- coding: utf-8 -*-
"""
SessionsCase — сверка переписки Claude не путается в регистре имени папки.

    python tests/SessionsCase/check.py

Повод (14.11.2026): Claude Code называет папку разговоров по пути проекта, а
диск разные запуски пишут по-разному — «C--Counterplay» на одной машине,
«c--Counterplay» на другой. Windows считает это одной папкой, сервер сбора —
двумя. `brain-sync.sh` сравнивал имена с учётом регистра, поэтому при каждой
сверке (раз в полчаса) скачивал «чужую» папку целиком, около 500 МБ, и мог
затереть дописанный здесь разговор её старой копией. На сервере лежали две
копии всей переписки.

Проверка достаёт функции переписки из ops/brain-sync.sh и гоняет их в bash на
готовых списках и во временной папке:
- sessions_plan сравнивает имена без учёта регистра папки проекта: одинаковые
  копии не едут никуда, своя длинная — только на сервер, а из двух серверных
  копий одного разговора скачивается одна, самая длинная;
- sessions_pack отправляет папку проекта строчными, остальное имя не трогает
  (в именах tool-results есть заглавные, их Claude Code ищет как записал);
- sessions_sync ими и пользуется, а перед сверкой сводит серверные папки
  (sessions_fold — она выполняется на сервере, под Linux; здесь, на
  нечувствительной к регистру файловой системе, её не прогнать, поэтому только
  наличие).

Ничего не меняет, сети не нужно, пара секунд. 0 — всё в порядке, 1 — нашлось.
"""
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'ops' / 'brain-sync.sh'
LF = chr(10)  # файлы для bash — без возврата каретки, иначе он входит в имя

bad: list[str] = []
passed = 0


def ok(cond: bool, what: str) -> None:
    global passed
    if cond:
        passed += 1
    else:
        bad.append(what)


def bash_exe() -> str:
    """bash из Git: просто «bash» на Windows бывает и WSL."""
    try:
        exec_path = subprocess.run(['git', '--exec-path'], capture_output=True, text=True,
                                   check=True).stdout.strip()
        cand = Path(exec_path).parents[2] / 'bin' / 'bash.exe'
        if cand.exists():
            return str(cand)
    except Exception:
        pass
    return shutil.which('bash') or 'bash'


def function(src: str, name: str) -> str | None:
    m = re.search(rf'^{name}\(\) \{{.*?^\}}', src, re.S | re.M)
    return m.group(0) if m else None


def run(funcs: str, body: str, *args: str) -> str:
    out = subprocess.run([bash_exe(), '-c', f'set -euo pipefail{LF}{funcs}{LF}{body}', '_', *args],
                         capture_output=True, text=True, encoding='utf-8')
    if out.returncode != 0:
        raise RuntimeError(out.stderr.strip() or f'код {out.returncode}')
    return out.stdout


def write(p: Path, rows: list[str]) -> None:
    p.write_bytes(''.join(r + LF for r in rows).encode('utf-8'))


def lines_of(s: str) -> list[str]:
    return [l for l in s.splitlines() if l.strip()]


def check_plan(func: str, tmp: Path) -> None:
    # Здесь папка с заглавной, на сервере — строчными, как её отправляет сверка.
    local = {
        'C--Counterplay/same.jsonl': 100,          # одинаковые копии
        'C--Counterplay/grown.jsonl': 120,         # здесь дописан
        'C--Counterplay/behind.jsonl': 50,         # на сервере длиннее
        'C--Counterplay/s/tool-results/toolu_AbC.json': 5,  # только здесь
    }
    remote = {
        'c--counterplay/same.jsonl': 100,
        'c--counterplay/grown.jsonl': 100,
        'c--counterplay/behind.jsonl': 80,
        'C--Counterplay/behind.jsonl': 60,         # старая копия другим регистром
        'c--counterplay/new.jsonl': 10,            # только на сервере, две копии
        'c--Counterplay/new.jsonl': 10,
    }
    write(tmp / 'local', [f'{s}\t{p}' for p, s in local.items()])
    write(tmp / 'remote', [f'{s}\t{p}' for p, s in remote.items()])
    run(func, 'sessions_plan "$1/local" "$1/remote" "$1/up" "$1/down"', tmp.as_posix())
    up = lines_of((tmp / 'up').read_text(encoding='utf-8'))
    down = lines_of((tmp / 'down').read_text(encoding='utf-8'))
    low = lambda xs: sorted(x.lower() for x in xs)
    named = lambda xs, n: [x for x in xs if x.lower().endswith(n)]

    ok(not named(down, 'same.jsonl'),
       f'sessions_plan: одинаковая копия под другим регистром папки скачивается заново '
       f'(в загрузке: {named(down, "same.jsonl")}) — так уходило ~500 МБ на каждой сверке')
    ok(not named(down, 'grown.jsonl'),
       f'sessions_plan: дописанный здесь разговор забирается с сервера старой копией '
       f'(в загрузке: {named(down, "grown.jsonl")}) — хвост затрётся')
    ok(low(up) == low(['C--Counterplay/grown.jsonl', 'C--Counterplay/s/tool-results/toolu_AbC.json']),
       f'sessions_plan: к отправке ждал grown.jsonl и toolu_AbC.json, вышло {up}')
    ok(named(down, 'behind.jsonl') == ['c--counterplay/behind.jsonl'],
       f'sessions_plan: из двух серверных копий behind.jsonl (80 и 60 байт) ждал одну длинную, '
       f'вышло {named(down, "behind.jsonl")} — две копии легли бы здесь в один файл '
       f'в случайном порядке')
    ok(len(named(down, 'new.jsonl')) == 1,
       f'sessions_plan: new.jsonl лежит на сервере в двух регистрах — ждал одну загрузку, '
       f'вышло {named(down, "new.jsonl")}')
    ok(len(down) == 2, f'sessions_plan: к загрузке ждал behind и new, вышло {down}')


def check_pack(func: str, tmp: Path) -> None:
    src = tmp / 'projects'
    (src / 'C--Proj' / 'x' / 'tool-results').mkdir(parents=True)
    (src / 'C--Proj' / 'y.jsonl').write_bytes(b'1')
    (src / 'C--Proj' / 'x' / 'tool-results' / 'toolu_AbC.json').write_bytes(b'2')
    write(tmp / 'list', ['C--Proj/y.jsonl', 'C--Proj/x/tool-results/toolu_AbC.json'])
    names = sorted(lines_of(run(func, 'sessions_pack "$1" "$2" | tar tzf -',
                                src.as_posix(), (tmp / 'list').as_posix())))
    ok(names == ['c--proj/x/tool-results/toolu_AbC.json', 'c--proj/y.jsonl'],
       f'sessions_pack: ждал папку проекта строчными и прочее как есть, в архиве {names}')


def main() -> int:
    src = SCRIPT.read_text(encoding='utf-8')
    funcs = {n: function(src, n) for n in ('sessions_plan', 'sessions_pack', 'sessions_fold',
                                           'sessions_sync')}
    for n, f in funcs.items():
        ok(f is not None, f'в ops/brain-sync.sh нет функции {n}()')

    if funcs['sessions_plan']:
        with tempfile.TemporaryDirectory(prefix='sessions-case-') as d:
            check_plan(funcs['sessions_plan'], Path(d))
    if funcs['sessions_pack']:
        with tempfile.TemporaryDirectory(prefix='sessions-case-') as d:
            check_pack(funcs['sessions_pack'], Path(d))
    sync = funcs['sessions_sync'] or ''
    for n in ('sessions_plan', 'sessions_pack', 'sessions_fold'):
        ok(n in sync, f'sessions_sync() не зовёт {n} — сверка идёт мимо проверенной функции')

    print(f'SessionsCase: пунктов пройдено {passed}, провалено {len(bad)}')
    if bad:
        print(f'\n  ПРОВАЛ ({len(bad)}):')
        for b in bad:
            print('    ' + b)
        return 1
    print('ВСЁ В ПОРЯДКЕ')
    return 0


if __name__ == '__main__':
    sys.exit(main())
