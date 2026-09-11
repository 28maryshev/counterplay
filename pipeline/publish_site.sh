#!/bin/bash
# Выкладка свежих данных на сайт — после каждой публикации базы.
#
# Раньше номер патча и тир-лист на сайте жили в файлах, которые я обновлял
# руками: собрал базу — не забудь пересобрать выгрузку, залить и пересобрать
# сайт. Не забыть не получилось: страницы месяц показывали прошлый патч.
# Теперь это делает сам коллектор, сразу после того как база опубликована.
#
#   выгрузки (тир-лист, контрпики, руны) → сервер сайта → пересборка → сброс кэша
#
# Запускается из collector_service после публикации; вручную — просто bash этот
# файл. Ошибки не должны ронять сбор: вызывающий код их глушит.
set -euo pipefail

SITE="${SITE_SSH:-ubuntu@13.49.125.246}"
REMOTE="counterplay-site"
# Скрипт работает НА ХОСТЕ, а не в контейнере: выгрузкам нужен только python3 со
# стандартной библиотекой, зато не нужно давать контейнеру ключ от сервера сайта.
DB="${DB_PATH:-$HOME/counterplay-collector/data/data.db}"
HERE=$(cd "$(dirname "$0")" && pwd)
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

say() { echo "$(date -u +'%F %T') site: $*"; }

say "готовлю выгрузки из $DB"
mkdir -p "$TMP/draft" "$TMP/stats"

# Тир-лист и контрпики — страницы сайта; руны и сборки — то, что панель в
# программе спрашивает у сайта.
python3 "$HERE/export_tiers.py" --db "$DB" --out "$TMP/draft"
python3 "$HERE/export_draft.py" --db "$DB" --out "$TMP/draft"
python3 "$HERE/export_runes.py" --db "$DB" --out "$TMP/stats"

say "выгружено: $(ls "$TMP/draft" | wc -l) файлов данных, $(find "$TMP/stats" -type f | wc -l) файлов рун"

# Патч в свежей выгрузке — по нему видно, что именно уехало.
PATCH=$(python3 -c "import json,sys; print(json.load(open('$TMP/draft/tiers.json'))['patch'])" 2>/dev/null || echo '?')

say "отправляю на $SITE"
scp -q -o StrictHostKeyChecking=no "$TMP/draft"/*.json "$SITE:$REMOTE/data/draft/"
tar czf - -C "$TMP/stats" . | ssh -o StrictHostKeyChecking=no "$SITE" "mkdir -p $REMOTE/stats && tar xzf - -C $REMOTE/stats"

# Пересборка нужна: данные страниц вшиты в сборку (так они отдаются мгновенно и
# без базы). Руны лежат томом и подхватываются сразу, но сборку это не ломает.
# Сборка идёт минут десять, и ssh-сессия за это время успевает оборваться — тогда
# команда на том конце умирает вместе с ней. Поэтому запускаем её отвязанно, а
# ждём по файлу-отметке.
say "пересобираю сайт (несколько минут)"
ssh -o StrictHostKeyChecking=no "$SITE" "rm -f ~/.site-rebuilt; cd $REMOTE && \
  nohup bash -c 'docker compose build web && docker compose up -d web && sh warmup.sh && touch ~/.site-rebuilt' \
  > ~/site-rebuild.log 2>&1 &" > /dev/null

for _ in $(seq 1 60); do
  sleep 20
  ssh -o StrictHostKeyChecking=no "$SITE" "test -f ~/.site-rebuilt" 2>/dev/null && break
done
ssh -o StrictHostKeyChecking=no "$SITE" "test -f ~/.site-rebuilt" \
  || { say "сборка не завершилась за 20 минут — смотри ~/site-rebuild.log на сервере сайта"; exit 1; }

# Без сброса кэша правки не доедут: страницы лежат на краю сети год.
say "сбрасываю кэш"
ssh -o StrictHostKeyChecking=no "$SITE" "cd $REMOTE && \
  TOKEN=\$(grep -E '^CF_PURGE_TOKEN=' .env | sed -E 's/^[^=]+=//; s/\"//g') && \
  ZONE=\$(grep -E '^CF_ZONE_ID=' .env | sed -E 's/^[^=]+=//; s/\"//g') && \
  curl -s -X POST \"https://api.cloudflare.com/client/v4/zones/\$ZONE/purge_cache\" \
    -H \"Authorization: Bearer \$TOKEN\" -H 'Content-Type: application/json' \
    -d '{\"purge_everything\":true}'" > /dev/null

say "готово, патч $PATCH"
echo "$PATCH"
