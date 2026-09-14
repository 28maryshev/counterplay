#!/bin/bash
# Уборка места на сервере:  bash cleanup.sh site|collector
#
# Копии уезжают в R2 и там подрезаются сами (см. backup-*.sh). Но место на самих
# машинах съедают не копии, а следы работы: слои docker после каждой пересборки,
# логи контейнеров, разросшийся ~/backup.log, а на коллекторе — оборванные
# публикации, каждая из которых оставляет на томе снимок базы под полтора
# гигабайта. Отсюда и «память забилась за неделю».
#
# Ставится в cron рядом с бэкапом (setup-backup.sh делает это сам) и молчит,
# пока всё в порядке: пишет в лог, а в Discord зовёт, только если места мало.
set -euo pipefail

ROLE="${1:-}"
case "$ROLE" in
  site|collector) ;;
  *) echo "укажи роль: site или collector"; exit 1 ;;
esac

LOG="$HOME/backup.log"
LOG_MAX_MB="${LOG_MAX_MB:-20}"          # лог больше этого — срезаем хвостом
LOG_KEEP_LINES="${LOG_KEEP_LINES:-2000}"
KEEP_IMAGES_H="${KEEP_IMAGES_H:-168}"   # слои docker старше недели не нужны
STALE_PUBLISH_MIN="${STALE_PUBLISH_MIN:-180}"  # дольше этого публикация не живёт
LOW_FREE_GB="${LOW_FREE_GB:-3}"         # ниже — зовём человека

log() { echo "$(date -u +'%F %T') $*" | tee -a "$LOG"; }

notify() {
  [ -n "${DISCORD_WEBHOOK:-}" ] || return 0
  curl -s -o /dev/null -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,sys; print(json.dumps({"content": sys.argv[1]}))' "$1")" \
    "$DISCORD_WEBHOOK" || true
}

free_gb() { df -BG --output=avail "$HOME" | tail -1 | tr -dc '0-9'; }

# Один экземпляр за раз: cron не должен наложиться на ручной запуск.
exec 9>"$HOME/.cleanup.lock"
flock -n 9 || { echo "уборка уже идёт — выхожу"; exit 0; }

before=$(free_gb)
log "── уборка места ($ROLE), свободно ${before} ГБ ──"

# 1. Логи. Режем на месте, а не переименовываем: тот же файл открыт на дозапись
#    у cron и у служб, и подсунутый новый inode они бы не заметили — писали бы в
#    удалённый файл, который так и остался бы занимать место.
trim_log() {
  [ -f "$1" ] || return 0
  mb=$(( $(stat -c %s "$1") / 1048576 ))
  [ "$mb" -ge "$LOG_MAX_MB" ] || return 0
  tmp=$(mktemp)
  tail -n "$LOG_KEEP_LINES" "$1" > "$tmp"
  cat "$tmp" > "$1"
  rm -f "$tmp"
  log "  лог $(basename "$1"): ${mb} МБ -> $(( $(stat -c %s "$1") / 1024 )) КБ"
}
trim_log "$LOG"
for f in "$HOME"/counterplay-*/logs/*.log; do
  [ -e "$f" ] && trim_log "$f"
done

# 2. Docker. Главный едок на маленьком VPS: каждая пересборка оставляет прошлый
#    образ и кэш слоёв. Неделю держим (есть на что откатиться), старше — сносим.
if command -v docker > /dev/null; then
  docker container prune -f --filter "until=24h" > /dev/null 2>&1 || true
  img=$(docker image prune -af --filter "until=${KEEP_IMAGES_H}h" 2>/dev/null | tail -1)
  bld=$(docker builder prune -af --filter "until=${KEEP_IMAGES_H}h" 2>/dev/null | tail -1)
  log "  docker образы: ${img:-нечего чистить}"
  log "  docker кэш сборки: ${bld:-нечего чистить}"
fi

# 3. Коллектор: хвосты оборванных публикаций. Идущую публикацию не трогаем —
#    у неё свой потолок (PUBLISH_TIMEOUT, 90 минут), так что всё старше трёх
#    часов уже точно мусор.
if [ "$ROLE" = collector ]; then
  d="$HOME/counterplay-collector/data/publtmp"
  if [ -d "$d" ]; then
    n=$(find "$d" -mindepth 1 -maxdepth 1 -mmin "+$STALE_PUBLISH_MIN" | wc -l)
    if [ "$n" -gt 0 ]; then
      find "$d" -mindepth 1 -maxdepth 1 -mmin "+$STALE_PUBLISH_MIN" -exec rm -rf {} + || true
      log "  снесены хвосты незавершённых публикаций: $n"
    fi
  fi
fi

# 4. Система. Только через sudo -n: в cron пароль спросить не у кого, и обычный
#    sudo просто повис бы до таймаута.
sudo -n journalctl --vacuum-size=200M > /dev/null 2>&1 || true
sudo -n apt-get clean > /dev/null 2>&1 || true

after=$(free_gb)
log "готово: было ${before} ГБ, стало ${after} ГБ"
if [ "$after" -lt "$LOW_FREE_GB" ]; then
  notify "⚠️ Мало места на сервере ($ROLE): свободно ${after} ГБ из $(df -BG --output=size "$HOME" | tail -1 | tr -dc '0-9') ГБ. Уборка своё сделала — дальше нужен человек."
fi
