#!/bin/bash
# Разовая установка бэкапов на сервере.
#
# Ставит rclone, прописывает доступ к Cloudflare R2, заводит суточную задачу
# и сразу делает первую копию — чтобы не выяснять через месяц, что ничего не
# работало.
#
# Запуск (переменные передаются окружением, в историю команд не попадают):
#   R2_ACCOUNT_ID=... R2_ACCESS_KEY=... R2_SECRET_KEY=... \
#   BACKUP_PASSPHRASE=... DISCORD_WEBHOOK=... \
#   bash setup-backup.sh site        # на сервере сайта
#   bash setup-backup.sh collector   # на сервере коллектора
set -euo pipefail

ROLE="${1:-}"
case "$ROLE" in
  site|collector) ;;
  *) echo "укажи роль: site или collector"; exit 1 ;;
esac

: "${R2_ACCOUNT_ID:?нужен R2_ACCOUNT_ID}"
: "${R2_ACCESS_KEY:?нужен R2_ACCESS_KEY}"
: "${R2_SECRET_KEY:?нужен R2_SECRET_KEY}"
BUCKET="${R2_BUCKET:-counterplay-backups}"

# 1. rclone — клиент к хранилищу.
if ! command -v rclone > /dev/null; then
  echo "ставлю rclone…"
  sudo apt-get update -qq && sudo apt-get install -y -qq rclone
fi

# 2. Доступ. Файл читается только владельцем: в нём ключи от хранилища.
mkdir -p "$HOME/.config/rclone"
cat > "$HOME/.config/rclone/rclone.conf" <<CONF
[r2]
type = s3
provider = Cloudflare
access_key_id = $R2_ACCESS_KEY
secret_access_key = $R2_SECRET_KEY
endpoint = https://$R2_ACCOUNT_ID.r2.cloudflarestorage.com
acl = private
# R2 не считает контрольную сумму по-амазоновски: без этого большие файлы
# загружаются с предупреждениями.
no_check_bucket = false
CONF
chmod 600 "$HOME/.config/rclone/rclone.conf"

# 3. Окружение самого бэкапа (пароль шифрования, вебхук) — отдельным файлом.
cat > "$HOME/.backup.env" <<ENV
R2_BUCKET=$BUCKET
BACKUP_PASSPHRASE=${BACKUP_PASSPHRASE:-}
DISCORD_WEBHOOK=${DISCORD_WEBHOOK:-}
ENV
chmod 600 "$HOME/.backup.env"

# 4. Проверяем доступ до того, как заводить расписание.
echo "проверяю доступ к хранилищу…"
rclone lsd "r2:$BUCKET" > /dev/null || { echo "нет доступа к бакету $BUCKET"; exit 1; }
echo "доступ есть."

# 5. Суточная задача. Сайт и коллектор в разное время: они делят интернет-канал
#    только в момент выгрузки, но спокойнее, когда не одновременно.
SCRIPT="$HOME/backup-$ROLE.sh"
[ -f "$SCRIPT" ] || { echo "нет $SCRIPT — сначала залей скрипт бэкапа"; exit 1; }
chmod +x "$SCRIPT"
MIN=$([ "$ROLE" = site ] && echo "17 3" || echo "47 3")
LINE="$MIN * * * . \$HOME/.backup.env && $SCRIPT >> \$HOME/backup.log 2>&1"
( crontab -l 2>/dev/null | grep -v "backup-$ROLE.sh"; echo "$LINE" ) | crontab -
echo "расписание:"; crontab -l | tail -2

# 6. Первая копия — прямо сейчас.
echo "делаю первую копию…"
set -a; . "$HOME/.backup.env"; set +a
"$SCRIPT"
