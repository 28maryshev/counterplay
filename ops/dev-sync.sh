#!/bin/bash
# Ярлык «Counterplay — синхронизация» на рабочем столе и в автозагрузке
# Windows: подтянуть то, что сделано на другой машине, в окне Git Bash.
#
#   bash ops/dev-sync.sh             подтянуть (это и запускает ярлык)
#   bash ops/dev-sync.sh --install   поставить ярлыки на этой машине
#
# Два шага:
# - программа: git pull --ff-only с GitHub; если пришли новые коммиты —
#   сборка Debug, той же, что у `dotnet run -- test`, чтобы тестовая среда
#   (песочница) сразу открывалась на свежем коде. Запущенная из этой папки
#   программа держит файлы сборки — тогда сборку пропускаем, `dotnet run`
#   соберёт сам;
# - переписка Claude: brain-sync.sh sessions (в обе стороны, «новее» =
#   «длиннее»).
# При входе в Windows сеть поднимается не сразу (21.11 первая сверка упала на
# «No route to host»), поэтому сначала до двух минут ждём сервер сбора. Всё
# прошло — окно закрывается само через 20 секунд, что-то нет — держится до
# клавиши, чтобы было видно, что случилось.
#
# Тело — в функции main: git pull может переписать этот самый файл, пока он
# выполняется, а bash читает скрипт по ходу. Функцию он разбирает целиком
# заранее.
set -uo pipefail

DIR=$(cd "$(dirname "$0")/.." && pwd)
HOST="${COLLECTOR_SSH:-collector}"
NAME="Counterplay — синхронизация"
OLD_NAMES=("Claude — переписка")   # прежние имена ярлыка: --install их убирает

install() {
  local gitbash win_dir old
  gitbash=$(cygpath -w /)'git-bash.exe'
  [ -f "$(cygpath -u "$gitbash")" ] || { echo "не нашёл $gitbash"; return 1; }
  win_dir=$(cygpath -w "$DIR")
  old=$(IFS='|'; echo "${OLD_NAMES[*]}")
  # Ярлык зовёт git-bash.exe: он сам выставляет окружение Git Bash. Строки
  # передаём через переменные окружения, чтобы не воевать с кавычками
  # PowerShell.
  CP_GITBASH="$gitbash" CP_DIR="$win_dir" CP_NAME="$NAME" CP_OLD="$old" \
  CP_CMD="exec bash $DIR/ops/dev-sync.sh" \
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command '
    [Console]::OutputEncoding = [Text.Encoding]::UTF8
    $sh = New-Object -ComObject WScript.Shell
    foreach ($dir in @([Environment]::GetFolderPath("Desktop"), [Environment]::GetFolderPath("Startup"))) {
      foreach ($o in $env:CP_OLD.Split("|")) {
        $p = Join-Path $dir ($o + ".lnk")
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p; "  убран старый " + $p }
      }
      $lnk = $sh.CreateShortcut((Join-Path $dir ($env:CP_NAME + ".lnk")))
      $lnk.TargetPath = $env:CP_GITBASH
      $lnk.Arguments = "-c `"" + $env:CP_CMD + "`""
      $lnk.WorkingDirectory = $env:CP_DIR
      $lnk.IconLocation = $env:CP_GITBASH + ",0"
      $lnk.Description = "Подтянуть программу с GitHub и переписку Claude с сервера сбора"
      $lnk.Save()
      "  " + $lnk.FullName
    }' || return 1
  echo "Готово: ярлык на рабочем столе и в автозагрузке. Убрать из автозагрузки —"
  echo "удалить его из папки shell:startup (Win+R → shell:startup)."
}

# Запущена ли программа из папки сборки этого репозитория (песочница через
# dotnet run или exe напрямую): тогда её файлы заняты и сборка упадёт.
running_from_repo() {
  CP_BIN="$(cygpath -w "$DIR")\\bin\\" powershell.exe -NoProfile -Command '
    $p = Get-Process Counterplay -ErrorAction SilentlyContinue |
         Where-Object { $_.Path -and $_.Path.StartsWith($env:CP_BIN, [StringComparison]::OrdinalIgnoreCase) }
    if ($p) { exit 0 } else { exit 1 }'
}

program() {
  local br before after out n
  echo "== программа"
  br=$(git -C "$DIR" rev-parse --abbrev-ref HEAD 2>/dev/null)
  if [ "$br" != main ]; then
    echo "  ветка «$br», не main — не трогаю"
    return 1
  fi
  before=$(git -C "$DIR" rev-parse HEAD)
  if ! out=$(GIT_TERMINAL_PROMPT=0 git -C "$DIR" pull -q --ff-only 2>&1); then
    echo "  не обновилась:"
    printf '%s\n' "$out" | grep -v '^warning: in the working copy' | sed 's/^/    /'
    echo "  чаще всего мешают незакоммиченные правки в тех же файлах — git status"
    return 1
  fi
  after=$(git -C "$DIR" rev-parse HEAD)
  if [ "$before" = "$after" ]; then
    echo "  уже свежая: $(git -C "$DIR" log -1 --format='%h %s')"
    return 0
  fi
  n=$(git -C "$DIR" rev-list --count "$before..$after")
  echo "  обновлена, новых коммитов $n:"
  git -C "$DIR" log --format='    %h %s' "$before..$after" | head -10
  [ "$n" -gt 10 ] && echo "    …"

  if running_from_repo; then
    echo "  программа из этой папки сейчас запущена — сборку пропускаю,"
    echo "  dotnet run -- test соберёт сам при следующем запуске"
    return 0
  fi
  echo "  сборка для тестовой среды (Debug)…"
  if out=$(cd "$DIR" && dotnet build Counterplay.csproj -nologo -v q 2>&1); then
    echo "  собрана"
  else
    echo "  сборка не прошла:"
    printf '%s\n' "$out" | grep -E 'error|ошибк' | head -10 | sed 's/^/    /'
    return 1
  fi
}

wait_server() {
  local waited=0
  until ssh -o ConnectTimeout=5 -o BatchMode=yes "$HOST" true 2>/dev/null; do
    if [ "$waited" -ge 120 ]; then
      echo "сервер сбора не отвечает две минуты — сеть? ключ? (bash ops/healthcheck.sh)"
      return 1
    fi
    [ "$waited" = 0 ] && echo "жду сеть и сервер сбора (до двух минут)…"
    sleep 5; waited=$((waited + 5))
  done
}

main() {
  local fail=0
  if [ "${1:-}" = "--install" ]; then
    install
    return $?
  fi
  printf '\033]0;%s\007' "$NAME"
  echo "== $NAME"
  if ! wait_server; then
    read -r -s -n 1 -p "Нажми любую клавишу, чтобы закрыть."
    return 1
  fi
  program || fail=1
  echo "== переписка Claude"
  bash "$DIR/ops/brain-sync.sh" sessions | sed 's/^/  /'
  [ "${PIPESTATUS[0]}" = 0 ] || fail=1
  echo
  if [ "$fail" = 0 ]; then
    read -r -s -n 1 -t 20 -p "Готово. Окно закроется через 20 секунд (или по любой клавише)."
  else
    read -r -s -n 1 -p "Не всё прошло — см. выше. Нажми любую клавишу, чтобы закрыть."
  fi
  return "$fail"
}

main "$@"; exit $?
