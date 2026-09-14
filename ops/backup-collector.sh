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
# Рабочий журнал приезжает сюда с машины разработки (ops/journal-push.sh). В git
# его нет намеренно, так что до отправки он существует в одном экземпляре.
JOURNAL_DIR="$HOME/journal"
KEEP_DAILY="${KEEP_DAILY:-5}"      # база большая — суточных копий держим меньше
KEEP_WEEKLY="${KEEP_WEEKLY:-4}"
# Бесплатный тариф R2 — 10 ГБ на всё. Счёт копий (KEEP_*) сам по себе места не
# гарантирует: база коллектора растёт, и девять копий по 140 МБ однажды станут
# девятью по 800. Поэтому есть второй предохранитель — по занятому объёму.
R2_LIMIT_MB="${R2_LIMIT_MB:-10240}"
R2_SOFT_PCT="${R2_SOFT_PCT:-75}"   # выше этой доли начинаем срезать лишние копии
MIN_DAILY="${MIN_DAILY:-2}"        # ниже не опускаемся никогда: остаться без
MIN_WEEKLY="${MIN_WEEKLY:-2}"      # копий хуже, чем с тесным хранилищем
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

# Чистка старых копий висит на выходе из скрипта, а не последней строкой удачного
# пути. Раньше было наоборот, и любой сбой выгрузки — а он-то и случается, когда
# на диске тесно, — уносил с собой и уборку: копии копились неделями, пока место
# не кончалось совсем.
prune() {  # $1 — каталог в бакете, $2 — сколько копий оставить
  rclone lsf --dirs-only "r2:$BUCKET/$1" 2>/dev/null | sort | head -n -"$2" | while read -r d; do
    log "  удаляю старую копию $1$d"
    rclone purge "r2:$BUCKET/$1$d" || true
  done || true
}
bucket_mb() {
  rclone size --json "r2:$BUCKET" 2>/dev/null | python3 -c 'import json,sys; print(int(json.load(sys.stdin)["bytes"]/1048576))' 2>/dev/null || echo 0
}

# Удаляет самую старую копию в каталоге, если их больше минимума. Возвращает 1,
# когда резать уже нечего — по этому признаку сторож понимает, что упёрся.
drop_oldest() {  # $1 — каталог, $2 — сколько копий оставить в любом случае
  dirs=$(rclone lsf --dirs-only "r2:$BUCKET/$1" 2>/dev/null | sort)
  n=$(printf '%s' "$dirs" | grep -c . || true)
  if [ "${n:-0}" -le "$2" ]; then return 1; fi
  oldest=$(printf '%s' "$dirs" | head -1)
  log "  тесно в хранилище — удаляю $1$oldest"
  rclone purge "r2:$BUCKET/$1$oldest" || return 1
}

# Второй предохранитель: держит бакет в пределах бесплатных 10 ГБ, даже когда
# счёт копий формально соблюдён. Сначала срезает суточные (их много), потом
# недельные; до последних двух и тех и других дело не доходит никогда.
enforce_quota() {
  soft=$(( R2_LIMIT_MB * R2_SOFT_PCT / 100 ))
  used=$(bucket_mb)
  if [ "$used" -le "$soft" ]; then return 0; fi
  notify "⚠️ Хранилище копий занято на $(( used * 100 / R2_LIMIT_MB ))% ($used МБ из $R2_LIMIT_MB МБ). Срезаю самые старые копии."
  while [ "$used" -gt "$soft" ]; do
    if ! drop_oldest "collector/daily/" "$MIN_DAILY"; then
      if ! drop_oldest "collector/weekly/" "$MIN_WEEKLY"; then
        notify "🛑 В хранилище $used МБ, а резать больше нечего: остались последние $MIN_DAILY суточных и $MIN_WEEKLY недельных копий. Дальше — руками."
        return 1
      fi
    fi
    used=$(bucket_mb)
  done
  log "  после подрезки по объёму: $used МБ"
}

PRUNED=0
prune_all() {
  if [ "$PRUNED" = 1 ]; then return 0; fi
  PRUNED=1
  prune "collector/daily/"  "$KEEP_DAILY"
  prune "collector/weekly/" "$KEEP_WEEKLY"
  enforce_quota || true
}
trap 'rc=$?; prune_all; rm -rf "$TMP"; exit $rc' EXIT

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

# Журнал эксплуатации: простои, ключи, публикации, уборки. Файл крошечный, а
# восстановить его неоткуда — это единственная история того, как система себя
# вела. Именно по такой истории 14.09 удалось понять, что коллектор встал за
# сутки до того, как это заметили.
if [ -f "$COL_DIR/data/ops.db" ]; then
  snapshot "$COL_DIR/data/ops.db" "$TMP/ops.db" || fail "не снялся снимок журнала эксплуатации"
  gzip -9 "$TMP/ops.db"
fi

# Рабочий журнал — прозой, про решения и причины. Шифруем: внутри внутренняя
# кухня, а бакет хоть и закрытый, но лишний слой здесь ничего не стоит.
if [ -d "$JOURNAL_DIR" ]; then
  tar czf "$TMP/journal.tar.gz" -C "$HOME" journal || fail "не собрался архив рабочего журнала"
  if [ -n "${BACKUP_PASSPHRASE:-}" ]; then
    openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -in "$TMP/journal.tar.gz" -out "$TMP/journal.enc" -pass env:BACKUP_PASSPHRASE || fail "не зашифровался рабочий журнал"
    rm -f "$TMP/journal.tar.gz"
  fi
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

prune_all    # обычный путь: сразу после выгрузки, до подсчёта объёма

total=$(bucket_mb)
pct=$(( total * 100 / R2_LIMIT_MB ))
log "готово. занято в хранилище: $total МБ из $R2_LIMIT_MB ($pct%)"
notify "💾 Бэкап коллектора за $DAY готов. Хранилище: $total МБ из $(( R2_LIMIT_MB / 1024 )) ГБ ($pct%)."
