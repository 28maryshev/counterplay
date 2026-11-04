#!/bin/bash
# Снять с коллектора выжимку для калибровки веса контры (pipeline/calibrate.py).
#
# Калибровке нужны агрегаты (base_wr, matchup, botlane_matchup) за два патча и
# драфты последнего — ОДНИМ снимком: она вычитает каждый матч из агрегатов, и
# драфт, которого в агрегатах ещё нет, вычитался бы из чужих игр. Поэтому всё
# копируется одной транзакцией чтения (база в WAL — сбор при этом не стоит).
# Копирует сама СУБД через ATTACH: память на сервере не растёт.
#
#   bash ops/pull-calib.sh                    изумруд, последние два патча
#   bash ops/pull-calib.sh gold 16.20 16.21   эло и патчи (прошлый, текущий)
#
# Кладёт ./calib-slice.db (*.db в .gitignore), ~300 МБ на изумруде, и больше
# ничего не делает. Дальше: python pipeline/calibrate.py --db calib-slice.db
set -euo pipefail

HOST="${COLLECTOR_SSH:-collector}"
CONTAINER="${COLLECTOR_CONTAINER:-counterplay-collector}"
OUT="${OUT:-calib-slice.db}"
TIER="${1:-emerald}"

if [ $# -ge 3 ]; then
  PREV="$2"; CUR="$3"
else
  mapfile -t PS < <(ssh "$HOST" "docker exec $CONTAINER python3 -c \"
import sqlite3
db = sqlite3.connect('file:/app/data/data.db?mode=ro', uri=True)
ps = [r[0] for r in db.execute('SELECT DISTINCT patch FROM base_wr')
      if r[0] and r[0][0].isdigit()]
ps.sort(key=lambda s: tuple(int(x) for x in s.split('.')))
print('\n'.join(ps[-2:]))\"")
  PREV="${PS[0]}"; CUR="${PS[1]}"
fi
echo "эло $TIER, агрегаты $PREV + $CUR, драфты $CUR"

REMOTE_PY=$(cat <<'PY'
import sqlite3, os, sys
OUT = '/app/data/publtmp/calib.db'
TIER, PREV, CUR = sys.argv[1:4]
if os.path.exists(OUT): os.remove(OUT)
src = sqlite3.connect('file:/app/data/data.db?mode=ro', uri=True, isolation_level=None)
src.execute('ATTACH DATABASE ? AS out', (OUT,))
src.execute('BEGIN')   # один снимок: агрегаты и драфты из одних и тех же матчей
for t in ('base_wr', 'matchup', 'botlane_matchup'):
    src.execute('CREATE TABLE out.%s AS SELECT * FROM main.%s WHERE tier_bucket=? AND patch IN (?, ?)'
                % (t, t), (TIER, PREV, CUR))
src.execute('CREATE TABLE out.drafts AS SELECT * FROM main.drafts WHERE tier_bucket=? AND patch=?', (TIER, CUR))
for t in ('base_wr', 'matchup', 'botlane_matchup', 'drafts'):
    n = src.execute('SELECT COUNT(*) FROM out.%s' % t).fetchone()[0]
    print('  %-16s %s' % (t, f'{n:,}'.replace(',', ' ')), flush=True)
src.execute('COMMIT')
PY
)

echo "собираю выжимку на коллекторе…"
ssh "$HOST" "docker exec -i $CONTAINER python3 - $TIER $PREV $CUR <<'EOF'
$REMOTE_PY
EOF"

echo "качаю…"
ssh "$HOST" "gzip -c ~/counterplay-collector/data/publtmp/calib.db" > "$OUT.gz"
gzip -df "$OUT.gz"

# Прибираем за собой: место на сервере нужно сбору — по DiskLow он встаёт.
ssh "$HOST" "docker exec $CONTAINER rm -f /app/data/publtmp/calib.db"

echo "готово: $OUT ($(du -h "$OUT" | cut -f1))"
echo "дальше: python pipeline/calibrate.py --db $OUT --tier $TIER"
