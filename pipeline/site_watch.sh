#!/bin/bash
# Сторож: коллектор опубликовал базу — значит сайту пора обновить данные.
#
# Контейнер не ходит наружу по ssh (ключ от сервера сайта ему ни к чему), он
# только оставляет отметку в общем каталоге control. Этот скрипт запускается по
# расписанию на хосте, видит отметку и запускает выкладку.
#
# Ставится в cron: */5 * * * * bash ~/counterplay-collector/site_watch.sh
set -uo pipefail

DIR="$HOME/counterplay-collector"
FLAG="$DIR/control/site-publish"
LOG="$HOME/site-publish.log"
LOCK="$HOME/.site-publish.lock"

[ -f "$FLAG" ] || exit 0

exec 9>"$LOCK"
flock -n 9 || exit 0          # предыдущая выкладка ещё идёт

WEBHOOK=$(grep -E '^DISCORD_WEBHOOK=' "$DIR/.env" 2>/dev/null | sed -E 's/^[^=]+=//')
notify() {
  [ -n "${WEBHOOK:-}" ] || return 0
  curl -s -o /dev/null -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,sys; print(json.dumps({"content": sys.argv[1]}))' "$1")" \
    "$WEBHOOK" || true
}

rm -f "$FLAG"                 # снимаем отметку сразу: повтор не нужен
echo "── $(date -u +'%F %T') выкладка данных сайта ──" >> "$LOG"

if PATCH=$(bash "$DIR/publish_site.sh" 2>>"$LOG" | tail -1); then
  echo "готово, патч $PATCH" >> "$LOG"
  notify "🌐 Сайт обновлён: тир-лист, контрпики и руны за патч $PATCH"
else
  echo "ОШИБКА — см. выше" >> "$LOG"
  notify "⚠️ Данные на сайт не уехали — смотри ~/site-publish.log на коллекторе"
fi
