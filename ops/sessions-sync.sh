#!/bin/bash
# Ярлык «Claude — переписка» на рабочем столе и в автозагрузке Windows:
# сверить разговоры Claude с сервером сбора в окне Git Bash.
#
#   bash ops/sessions-sync.sh             сверить (это и запускает ярлык)
#   bash ops/sessions-sync.sh --install   поставить ярлыки на этой машине
#
# Сверку делает brain-sync.sh sessions (в обе стороны, «новее» = «длиннее»).
# Здесь только окно вокруг неё. При входе в Windows сеть поднимается не сразу
# (21.11 первая сверка упала на «No route to host»), поэтому сначала до двух
# минут ждём сервер. Удачная сверка закрывает окно сама через 20 секунд,
# неудачная держит его до нажатия клавиши, чтобы было видно, что случилось.
set -uo pipefail

DIR=$(cd "$(dirname "$0")/.." && pwd)
HOST="${COLLECTOR_SSH:-collector}"
NAME="Claude — переписка"

install() {
  local gitbash win_dir
  gitbash=$(cygpath -w /)'git-bash.exe'
  [ -f "$(cygpath -u "$gitbash")" ] || { echo "не нашёл $gitbash"; return 1; }
  win_dir=$(cygpath -w "$DIR")
  # Ярлык зовёт git-bash.exe: он сам выставляет окружение Git Bash. Строку
  # для -c передаём через переменную окружения, чтобы не воевать с кавычками
  # PowerShell.
  CP_GITBASH="$gitbash" CP_DIR="$win_dir" CP_NAME="$NAME" \
  CP_CMD="exec bash $(cd "$DIR" && pwd)/ops/sessions-sync.sh" \
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command '
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $sh = New-Object -ComObject WScript.Shell
    foreach ($dir in @([Environment]::GetFolderPath("Desktop"), [Environment]::GetFolderPath("Startup"))) {
      $lnk = $sh.CreateShortcut((Join-Path $dir ($env:CP_NAME + ".lnk")))
      $lnk.TargetPath = $env:CP_GITBASH
      $lnk.Arguments = "-c `"" + $env:CP_CMD + "`""
      $lnk.WorkingDirectory = $env:CP_DIR
      $lnk.IconLocation = $env:CP_GITBASH + ",0"
      $lnk.Description = "Сверить переписку Claude с сервером сбора"
      $lnk.Save()
      "  " + $lnk.FullName
    }' || return 1
  echo "Готово: ярлык на рабочем столе и в автозагрузке. Убрать из автозагрузки —"
  echo "удалить его из папки shell:startup (Win+R → shell:startup)."
}

if [ "${1:-}" = "--install" ]; then
  install
  exit $?
fi

printf '\033]0;%s\007' "$NAME"
echo "== $NAME"

waited=0
until ssh -o ConnectTimeout=5 -o BatchMode=yes "$HOST" true 2>/dev/null; do
  if [ "$waited" -ge 120 ]; then
    echo "сервер сбора не отвечает две минуты — сеть? ключ? (bash ops/healthcheck.sh)"
    read -r -s -n 1 -p "Нажми любую клавишу, чтобы закрыть."
    exit 1
  fi
  [ "$waited" = 0 ] && echo "жду сервер сбора (до двух минут)…"
  sleep 5; waited=$((waited + 5))
done

bash "$DIR/ops/brain-sync.sh" sessions
rc=$?
echo
if [ "$rc" = 0 ]; then
  read -r -s -n 1 -t 20 -p "Готово. Окно закроется через 20 секунд (или по любой клавише)."
else
  read -r -s -n 1 -p "Сверка не прошла — см. выше. Нажми любую клавишу, чтобы закрыть."
fi
exit "$rc"
