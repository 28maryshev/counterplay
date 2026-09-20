#!/bin/bash
# Снять с коллектора выжимку для локальных расчётов.
#
# Считать находки на самом коллекторе нельзя: база там 1.7 ГБ, памяти на машине
# 911 МБ, и рядом идёт сбор — ничего не кэшируется, каждый запрос уходит на диск.
# Один такой прогон полз больше часа, а раскладка сборок на пары чуть не уронила
# сбор, съев четверть всей памяти. Те же данные, снятые сюда, считаются секунды.
#
#   bash ops/pull-stats.sh              последние два патча
#   bash ops/pull-stats.sh 16.16 16.17 16.18
#
# Кладёт файл в ./data-slice.db (он в .gitignore) и больше ничего не делает.
set -euo pipefail

HOST="${COLLECTOR_SSH:-collector}"
CONTAINER="${COLLECTOR_CONTAINER:-counterplay-collector}"
OUT="${OUT:-data-slice.db}"

if [ $# -gt 0 ]; then
  PATCHES=("$@")
else
  # Последние два патча из самой базы — чтобы не держать список в двух местах.
  mapfile -t PATCHES < <(ssh "$HOST" "docker exec $CONTAINER python3 -c \"
import sqlite3
db = sqlite3.connect('file:/app/data/data.db?mode=ro', uri=True)
ps = [r[0] for r in db.execute('SELECT DISTINCT patch FROM base_wr')
      if r[0] and r[0][0].isdigit()]
ps.sort(key=lambda s: tuple(int(x) for x in s.split('.')))
print('\n'.join(ps[-2:]))\"")
fi

echo "патчи: ${PATCHES[*]}"

# Python собирает выжимку ВНУТРИ контейнера: только там лежит рабочая база.
# Переливаем через ATTACH — строки перекладывает сама СУБД, память не растёт.
REMOTE_PY=$(cat <<'PY'
import sqlite3, sys
OUT = '/app/data/publtmp/slice.db'
PATCHES = tuple(sys.argv[1:])
TABLES = ('base_wr', 'keystone_wr', 'rune_page', 'keystone_matchup',
          'item_wr', 'item_build', 'spell_wr', 'champion_damage')
src = sqlite3.connect('file:/app/data/data.db?mode=ro', uri=True)
src.execute('ATTACH DATABASE ? AS out', (OUT,))
ph = ','.join('?' * len(PATCHES))
for t in TABLES:
    src.execute('DROP TABLE IF EXISTS out.%s' % t)
    src.execute('CREATE TABLE out.%s AS SELECT * FROM main.%s WHERE patch IN (%s)'
                % (t, t, ph), PATCHES)
    n = src.execute('SELECT COUNT(*) FROM out.%s' % t).fetchone()[0]
    print('  %-18s %s' % (t, f'{n:,}'.replace(',', ' ')), flush=True)
# Без индексов локальный прогон будет таким же медленным, как на сервере.
for name, cols in (('build', 'item_build(champion_id, role, patch)'),
                   ('ks', 'keystone_wr(champion_id, role, patch)'),
                   ('page', 'rune_page(champion_id, role, keystone, patch)'),
                   ('ksm', 'keystone_matchup(champion_id, role, patch)'),
                   ('item', 'item_wr(champion_id, role, patch)'),
                   ('spell', 'spell_wr(champion_id, role, patch)')):
    src.execute('CREATE INDEX out.ix_%s ON %s' % (name, cols))
src.commit()
src.close()
PY
)

echo "собираю выжимку на коллекторе…"
ssh "$HOST" "docker exec -i $CONTAINER python3 - ${PATCHES[*]} <<'EOF'
$REMOTE_PY
EOF"

echo "качаю…"
ssh "$HOST" "gzip -c ~/counterplay-collector/data/publtmp/slice.db" > "$OUT.gz"
gzip -df "$OUT.gz"

# Прибираем за собой: файл на сервере весит сотни мегабайт, а место там нужно
# сбору — по DiskLow он останавливается.
ssh "$HOST" "docker exec $CONTAINER rm -f /app/data/publtmp/slice.db"

echo "готово: $OUT ($(du -h "$OUT" | cut -f1))"
echo "дальше: python pipeline/findings.py --db $OUT"
