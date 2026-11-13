# -*- coding: utf-8 -*-
"""
ShellTraps — ловушка выхода в функции shell-скрипта не держится за local.

    python tests/ShellTraps/check.py

Повод (13.11.2026): `brain-sync.sh secrets-import` расшифровывал архив ключей
во временную папку и ставил `trap 'rm -rf "$tmp"' EXIT`, где `tmp` объявлен
через `local`. Ловушка EXIT срабатывает при выходе из скрипта, когда функция
уже вернулась и её локальной переменной нет: под `set -u` — «tmp: unbound
variable», и папка с ключами в открытом виде остаётся во временной папке
Windows. Так же вёл себя `secrets-export`.

Проверка читает все *.sh под git: в теле функции не должно быть
`trap '…$имя…'` (одинарные кавычки — подстановка в момент срабатывания), если
`имя` объявлено в этой же функции через `local`. Правильно — подставить путь
сразу: `trap "rm -rf '$tmp'" EXIT`.

Ничего не меняет, сети не нужно, мгновенно. 0 — всё в порядке, 1 — нашлось.
"""
import re
import subprocess
import sys
from pathlib import Path

for stream in (sys.stdout, sys.stderr):
    try:
        stream.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

ROOT = Path(__file__).resolve().parents[2]

FUNC_START = re.compile(r'^([A-Za-z_][\w-]*)\s*\(\)\s*\{')
LOCAL = re.compile(r'^\s*local\s+(.*)$')
TRAP = re.compile(r"\btrap\s+'([^']*)'")
VAR = re.compile(r'\$\{?([A-Za-z_]\w*)')
# Значения в local (строки, $(…)) выкидываем, чтобы слова из них не сошли за имена.
NOISE = re.compile(r'"[^"]*"|\'[^\']*\'|\$\([^)]*\)')
NAME = re.compile(r'^([A-Za-z_]\w*)(=|$)')


def scripts() -> list[Path]:
    out = subprocess.run(['git', '-C', str(ROOT), 'ls-files', '*.sh'], capture_output=True,
                         text=True, encoding='utf-8', check=True).stdout
    return [ROOT / l.strip() for l in out.splitlines() if l.strip()]


def check(path: Path) -> tuple[list[str], int]:
    """Нарушения в одном скрипте и сколько ловушек внутри функций просмотрено."""
    bad, seen = [], 0
    func, locals_ = None, set()
    rel = path.relative_to(ROOT).as_posix()
    for n, line in enumerate(path.read_text(encoding='utf-8', errors='replace').splitlines(), 1):
        m = FUNC_START.match(line)
        if m:
            func, locals_ = m.group(1), set()
        elif func and line.startswith('}'):
            func = None
            continue
        if not func:
            continue
        lm = LOCAL.match(line)
        if lm:
            for tok in NOISE.sub('', lm.group(1).split(';')[0]).split():
                nm = NAME.match(tok)
                if nm:
                    locals_.add(nm.group(1))
        for tm in TRAP.finditer(line):
            seen += 1
            hit = sorted(set(VAR.findall(tm.group(1))) & locals_)
            if hit:
                bad.append(f"{rel}:{n} {func}(): trap '{tm.group(1)}' держится за local "
                           f"{', '.join(hit)} — к выходу из скрипта её уже нет "
                           f"(set -u: unbound variable, уборка не выполняется)")
    return bad, seen


def main() -> int:
    files = scripts()
    bad, seen = [], 0
    for p in files:
        b, s = check(p)
        bad += b
        seen += s
    print(f'ShellTraps: скриптов {len(files)}, ловушек в одинарных кавычках внутри функций {seen}')
    if not files or seen == 0:
        # Пустой прогон — сломался разбор, а не «всё хорошо».
        print('  ПРОВАЛ: не нашлось ни одной ловушки внутри функций — разбор сломан')
        return 1
    if bad:
        print(f'\n  ПРОВАЛ ({len(bad)}):')
        for b in bad:
            print('    ' + b)
        return 1
    print('ВСЁ В ПОРЯДКЕ')
    return 0


if __name__ == '__main__':
    sys.exit(main())
