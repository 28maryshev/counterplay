#!/bin/bash
# Выкладка коллектора одной командой:  bash ops/deploy-collector.sh <файлы>
#
#   bash ops/deploy-collector.sh                         — ничего не меняет:
#        показывает, какие файлы pipeline/ из git отличаются от серверных;
#   bash ops/deploy-collector.sh collector_service.py    — выкладывает названные
#        файлы (имена — внутри pipeline/, можно и с префиксом pipeline/);
#   --force — выложить, даже если демон публикует базу или файл не закоммичен.
#
# Шаги — в том порядке, в каком их легко забыть руками:
#   1. файл закоммичен — на сервер едет то, что есть в git, а не черновик;
#   2. демон не публикует и не убирает базу: перезапуск посреди публикации рвёт
#      заливку, и база ждёт следующего круга;
#   3. прежняя версия на сервере остаётся рядом как <файл>.bak-<дата>;
#   4. заливка scp и пересборка образа — код запечён в образ, простой restart
#      поднимает СТАРЫЙ код (2026-09-20). Скрипты хоста (publish_site.sh,
#      site_watch.sh, export*.py) работают без контейнера — для них пересборки нет;
#   5. сверка: sha256 файла внутри контейнера (или на хосте) совпадает с локальным,
#      контейнер работает, в логе — свежий старт демона.
#
# Коды выхода: 0 — выложено и сверено (или нечего сравнивать), 1 — остановился.
set -uo pipefail

COLLECTOR="${COLLECTOR_SSH:-collector}"
REMOTE_DIR="counterplay-collector"
CONTAINER="counterplay-collector"
DIR=$(cd "$(dirname "$0")/.." && pwd)
SSH="ssh -o BatchMode=yes -o ConnectTimeout=15 $COLLECTOR"
HOST_ONLY='^(publish_site\.sh|site_watch\.sh|export(_[a-z]+)?\.py)$'

force=0
files=()
for a in "$@"; do
  case "$a" in
    --force) force=1 ;;
    *) files+=("${a#pipeline/}") ;;
  esac
done

cd "$DIR" || exit 1

# ── без аргументов: только показать расхождения ──
if [ ${#files[@]} -eq 0 ]; then
  echo "── сравниваю pipeline/ (файлы из git) с сервером"
  # Сравниваем без \r: рабочая копия на Windows бывает с CRLF, и прежние выкладки
  # увезли файлы то так, то так — по содержимому они одинаковые.
  names=$(git ls-files pipeline | sed 's#^pipeline/##' | tr '\n' ' ')
  remote=$($SSH "cd $REMOTE_DIR && for f in $names; do [ -f \"\$f\" ] && echo \"\$(tr -d '\r' < \"\$f\" | sha256sum | cut -d' ' -f1) \$f\"; done; true") \
    || { echo "не достучался до $COLLECTOR"; exit 1; }
  diff=0
  for f in $names; do
    l=$(tr -d '\r' < "pipeline/$f" | sha256sum | cut -d' ' -f1)
    r=$(printf '%s\n' "$remote" | awk -v f="$f" '$2==f {print $1}')
    if [ -z "$r" ]; then echo "  нет на сервере: $f"; diff=1
    elif [ "$l" != "$r" ]; then echo "  отличается:     $f"; diff=1; fi
  done
  [ $diff -eq 0 ] && echo "  всё совпадает"
  echo "Выложить: bash ops/deploy-collector.sh <файл> [<файл> …]"
  exit 0
fi

# ── 1. файлы есть и закоммичены ──
rebuild=0
for f in "${files[@]}"; do
  [ -f "pipeline/$f" ] || { echo "нет такого файла: pipeline/$f"; exit 1; }
  if ! git diff --quiet HEAD -- "pipeline/$f" 2>/dev/null; then
    if [ $force -eq 1 ]; then echo "внимание: pipeline/$f не закоммичен — выкладываю (--force)"
    else echo "pipeline/$f не закоммичен — сначала коммит (или --force)"; exit 1; fi
  fi
  [[ "$f" =~ $HOST_ONLY ]] || rebuild=1
done

# ── 2. фаза демона ──
state=$($SSH "cat $REMOTE_DIR/control/status" 2>/dev/null | tr -d '\n' \
  | sed -n 's/.*"state": *"\([a-z_]*\)".*/\1/p')
[ -n "$state" ] || { echo "не прочитал статус демона на $COLLECTOR — не выкладываю"; exit 1; }
echo "── демон сейчас: $state"
if [ $rebuild -eq 1 ]; then
  case "$state" in
    publishing|pruning|key_expired)
      if [ $force -eq 1 ]; then echo "внимание: перезапуск посреди «$state» (--force)"
      else echo "демон в фазе «$state» — перезапуск оборвёт публикацию. Подожди конца или --force"; exit 1; fi ;;
  esac
  busy=$($SSH "ls -A $REMOTE_DIR/data/publtmp 2>/dev/null | head -3")
  [ -n "$busy" ] && echo "внимание: в data/publtmp что-то лежит (фоновая заливка или остатки): $busy"
fi

# ── 3. копия прежних версий ──
stamp=$(date -u +%Y%m%d-%H%M)
for f in "${files[@]}"; do
  $SSH "cd $REMOTE_DIR && if [ -f '$f' ]; then cp -p '$f' '$f.bak-$stamp'; echo '  копия: $f.bak-$stamp'; fi" \
    || { echo "не сделал копию $f — не выкладываю"; exit 1; }
done

# ── 4. заливка и пересборка ──
for f in "${files[@]}"; do
  scp -q -o BatchMode=yes "pipeline/$f" "$COLLECTOR:$REMOTE_DIR/$f" || { echo "scp $f не прошёл"; exit 1; }
  echo "  залит: $f"
done

if [ $rebuild -eq 1 ]; then
  echo "── пересборка образа и перезапуск"
  started=$(date -u +%Y-%m-%dT%H:%M:%SZ)
  $SSH "cd $REMOTE_DIR && docker compose build -q collector && docker compose up -d collector" 2>&1 | tail -5
  [ "${PIPESTATUS[0]}" -eq 0 ] || { echo "пересборка не прошла — на сервере новые файлы, старый контейнер; копии *.bak-$stamp"; exit 1; }
else
  echo "── только скрипты хоста — контейнер не трогаю"
fi

# ── 5. сверка ──
fail=0
for f in "${files[@]}"; do
  l=$(sha256sum "pipeline/$f" | cut -d' ' -f1)
  if [[ "$f" =~ $HOST_ONLY ]]; then
    r=$($SSH "sha256sum $REMOTE_DIR/$f" | cut -d' ' -f1); where="на хосте"
  else
    r=$($SSH "docker exec $CONTAINER sha256sum /app/$f" | cut -d' ' -f1); where="в контейнере"
  fi
  if [ "$l" = "$r" ]; then echo "  [ ok ] $f $where совпадает с локальным"
  else echo "  [FAIL] $f $where отличается от локального"; fail=1; fi
done

if [ $rebuild -eq 1 ]; then
  ok=0
  for _ in $(seq 1 12); do
    st=$($SSH "docker inspect -f '{{.State.Status}}' $CONTAINER" 2>/dev/null)
    log=$($SSH "docker logs --since $started $CONTAINER 2>&1 | grep -m1 'Коллектор запущен'" 2>/dev/null)
    if [ "$st" = running ] && [ -n "$log" ]; then ok=1; break; fi
    sleep 5
  done
  if [ $ok -eq 1 ]; then echo "  [ ok ] контейнер работает, демон стартовал"
  else echo "  [FAIL] за минуту демон не стартовал (состояние: ${st:-?}) — смотреть docker logs $CONTAINER"; fail=1; fi
  $SSH "docker logs --since $started $CONTAINER 2>&1 | tail -4" | sed 's/^/  │ /'
fi

[ $fail -eq 0 ] && echo "Выложено." || { echo "Выложено, но сверка не сошлась — см. выше. Откат: копии *.bak-$stamp"; exit 1; }
