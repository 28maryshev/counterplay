#!/bin/bash
# Отправляет рабочий журнал и справочник на сервер коллектора: bash ops/journal-push.sh
#
# Журнал (папка journal/) и справочник (папка handbook/) живут на машине
# разработки и намеренно не попадают в git — репозиторий публичный. Значит до
# отправки они существуют в одном экземпляре и сгорят вместе с ноутбуком. Здесь
# они кладутся рядом с бэкапом коллектора, откуда суточная задача забирает
# ~/journal в R2 вместе с базами.
#
# Кладём в ~/journal, а НЕ внутрь counterplay-collector: тот каталог целиком
# уходит в docker build context, и журнал запёкся бы в образ.
set -euo pipefail

HOST="${COLLECTOR_SSH:-collector}"
DIR=$(cd "$(dirname "$0")/.." && pwd)
SRC="$DIR/journal"
BOOK="$DIR/handbook"

[ -d "$SRC" ] || { echo "нет каталога $SRC — нечего отправлять"; exit 1; }
n=$(ls "$SRC"/*.md 2>/dev/null | wc -l)
[ "$n" -gt 0 ] || { echo "в $SRC нет записей"; exit 1; }

# Справочник обязан сходиться с кодом. Расхождения не останавливают отправку —
# копия неполного справочника лучше, чем никакой, — но печатаются, чтобы их
# поправили, а не забыли.
if [ -f "$BOOK/check.py" ]; then
  # На Windows «python3» бывает заглушкой магазина, которая ничего не запускает:
  # берём первый интерпретатор, который правда отвечает.
  PY=""
  for cand in python python3; do
    if command -v "$cand" >/dev/null 2>&1 &&
       "$cand" -c 'import sys; sys.exit(sys.version_info < (3, 10))' >/dev/null 2>&1; then
      PY="$cand"; break
    fi
  done
  if [ -n "$PY" ]; then
    "$PY" "$BOOK/check.py" || echo "внимание: справочник разошёлся с кодом — см. выше"
  else
    echo "внимание: нет Python 3.10+ — проверку справочника пропускаю"
  fi
fi

ssh "$HOST" 'mkdir -p ~/journal'
scp -q "$SRC"/*.md "$HOST:journal/"
echo "журнал отправлен на $HOST: $n файлов"

# Справочник — целиком и с заменой: удалённая здесь страница не должна жить на
# сервере вечно. Распаковка во временную папку и подмена одним mv, чтобы
# оборванная передача не оставила вместо справочника пустоту.
if [ -d "$BOOK" ]; then
  m=$(find "$BOOK" -type f | wc -l)
  tar czf - -C "$DIR" handbook | ssh "$HOST" 'set -e; cd ~/journal
    rm -rf .handbook.new && mkdir .handbook.new && tar xzf - -C .handbook.new
    rm -rf handbook && mv .handbook.new/handbook handbook && rmdir .handbook.new'
  echo "справочник отправлен на $HOST: $m файлов"
fi

echo "в хранилище уедет со следующим суточным бэкапом (или сразу: ssh $HOST '. ~/.backup.env && ~/backup-collector.sh')"
