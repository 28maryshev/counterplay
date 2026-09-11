#!/bin/bash
# Бэкап коллектора и Discord-бота на сторону — в Cloudflare R2.
#
# Самое ценное здесь — data.db. Таблицы рун, предметов, кейстоунов и спеллов
# живут ТОЛЬКО в ней: в публикуемой на GitHub базе их нет, там лишь то, что
# нужно движку пиков. Потеряли её вчера — панель рун пришлось собирать заново.
#
# Запуск: cron раз в сутки (см. ops/README.md). Вручную — просто sh этот файл.
set -euo pipefail

BUCKET="${R2_BUCKET:-counterplay-backups}"
COL_DIR="$HOME/counterplay-collector"
BOT_DIR="$HOME/counterplay-bot"
KEEP_DAILY="${KEEP_DAILY:-5}"      # база большая — суточных копий держим меньше
KEEP_WEEKLY="${KEEP_WEEKLY:-4}"
LOG="$HOME/backup.log"

DAY=$(date -u +%F)
WEEK=$(date -u +%G-W%V)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

log() { echo "$(date -u +'%F %T') $*" | tee -a "$LOG"; }
fail() { log "ОШИБКА: $*"; notify "❌ Бэкап коллектора не сделан: $*"; exit 1; }

notify() {
  [ -n "${DISCORD_WEBHOOK:-}" ] || return 0
  curl -s -o /dev/null -H 'Content-Type: application/json' \
    -d "$(python3 -c 'import json,sys; print(json.dumps({"content": sys.argv[1]}))' "$1")" \
    "$DISCORD_WEBHOOK" || true
}

exec 9>"$HOME/.backup-collector.lock"
flock -n 9 || { log "уже выполняется — выхожу"; exit 0; }

log "── бэкап коллектора $DAY ──"

# Снимок живой базы. Просто скопировать файл нельзя: коллектор пишет в неё
# прямо сейчас, и копия получится с полузаписанной транзакцией. sqlite умеет
# согласованный снимок сам — он же сливает журнал WAL.
snapshot() {  # $1 — исходная база, $2 — куда положить снимок
  python3 - "$1" "$2" <<'PY'
import sqlite3, sys
src = sqlite3.connect('file:%s?mode=ro' % sys.argv[1], uri=True)
dst = sqlite3.connect(sys.argv[2])
with dst:
    src.backup(dst)
# Быстрая проверка целостности: битый снимок в хранилище не нужен.
ok = dst.execute('PRAGMA quick_check').fetchone()[0]
dst.close(); src.close()
if ok != 'ok':
    sys.exit('снимок повреждён: %s' % ok)
PY
}

snapshot "$COL_DIR/data/data.db" "$TMP/data.db" || fail "не снялся снимок базы коллектора"
gzip -6 "$TMP/data.db"   # -9 на 250 МБ греет процессор минутами ради процентов

if [ -f "$BOT_DIR/data/bot.db" ]; then
  snapshot "$BOT_DIR/data/bot.db" "$TMP/bot.db" || fail "не снялся снимок базы бота"
  gzip -9 "$TMP/bot.db"
fi

# Настройки обеих служб — в одном зашифрованном архиве.
if [ -n "${BACKUP_PASSPHRASE:-}" ]; then
  tar czf "$TMP/env.tar.gz" -C "$HOME" \
    counterplay-collector/.env counterplay-bot/.env 2>/dev/null || true
  openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt \
    -in "$TMP/env.tar.gz" -out "$TMP/env.enc" -pass env:BACKUP_PASSPHRASE \
    || fail "не зашифровались настройки"
  rm -f "$TMP/env.tar.gz"
else
  log "внимание: BACKUP_PASSPHRASE не задан — настройки НЕ выгружаются"
fi

for f in "$TMP"/*; do
  rclone copyto "$f" "r2:$BUCKET/collector/daily/$DAY/$(basename "$f")" \
    || fail "не загрузилось: $(basename "$f")"
done
if [ "$(date -u +%u)" = "7" ]; then
  rclone copy "r2:$BUCKET/collector/daily/$DAY" "r2:$BUCKET/collector/weekly/$WEEK" || true
fi

for f in "$TMP"/*; do
  n=$(basename "$f")
  local_size=$(stat -c %s "$f")
  remote_size=$(rclone size --json "r2:$BUCKET/collector/daily/$DAY/$n" 2>/dev/null \
                | python3 -c 'import json,sys; print(json.load(sys.stdin)["bytes"])' 2>/dev/null || echo 0)
  [ "$local_size" = "$remote_size" ] || fail "$n загрузился не целиком ($remote_size из $local_size)"
  log "  $n — $((local_size/1048576)) МБ, сверено"
done

prune() {
  rclone lsf --dirs-only "r2:$BUCKET/$1" 2>/dev/null | sort | head -n -"$2" | while read -r d; do
    log "  удаляю старую копию $1$d"
    rclone purge "r2:$BUCKET/$1$d" || true
  done
}
prune "collector/daily/"  "$KEEP_DAILY"
prune "collector/weekly/" "$KEEP_WEEKLY"

total=$(rclone size --json "r2:$BUCKET" 2>/dev/null \
        | python3 -c 'import json,sys; print(round(json.load(sys.stdin)["bytes"]/1048576,1))' 2>/dev/null || echo '?')
log "готово. занято в хранилище: $total МБ"
notify "💾 Бэкап коллектора за $DAY готов (в хранилище $total МБ)"
