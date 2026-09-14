#!/bin/bash
# Отправляет рабочий журнал на сервер коллектора: bash ops/journal-push.sh
#
# Журнал (папка journal/) живёт на машине разработки и намеренно не попадает в
# git — репозиторий публичный. Значит до отправки он существует в одном
# экземпляре, и сгорит вместе с ноутбуком. Здесь он кладётся рядом с бэкапом
# коллектора, откуда суточная задача забирает его в R2 вместе с базами.
#
# Кладём в ~/journal, а НЕ внутрь counterplay-collector: тот каталог целиком
# уходит в docker build context, и журнал запёкся бы в образ.
set -euo pipefail

HOST="${COLLECTOR_SSH:-collector}"
DIR=$(cd "$(dirname "$0")/.." && pwd)
SRC="$DIR/journal"

[ -d "$SRC" ] || { echo "нет каталога $SRC — нечего отправлять"; exit 1; }
n=$(ls "$SRC"/*.md 2>/dev/null | wc -l)
[ "$n" -gt 0 ] || { echo "в $SRC нет записей"; exit 1; }

ssh "$HOST" 'mkdir -p ~/journal'
scp -q "$SRC"/*.md "$HOST:journal/"
echo "журнал отправлен на $HOST: $n файлов"
echo "в хранилище уедет со следующим суточным бэкапом (или сразу: ssh $HOST '. ~/.backup.env && ~/backup-collector.sh')"
