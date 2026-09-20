#!/bin/bash
# Отправить пост в #meta-radar от имени бота. Текст приходит на stdin.
#
#   python pipeline/findings.py --db data-slice.db --post
#   echo "текст" | bash ops/post-radar.sh
#
# Почему через сервер, а не напрямую: токен бота живёт в его `.env` на сервере и
# оттуда не уезжает. Мы посылаем туда только текст, а Discord дёргает уже сам
# сервер. Веб-хук для этого заводить не нужно — бот там и так пишет.
#
# Текст едет в base64: так не надо воевать с кавычками, переносами строк и
# кириллицей на трёх уровнях экранирования разом.
set -euo pipefail

HOST="${COLLECTOR_SSH:-collector}"
TITLE="${RADAR_TITLE:-📡 META RADAR}"
COLOR="${RADAR_COLOR:-3049409}"      # 0x2E86C1 — синий, как у остальных постов

TEXT=$(cat)
if [ -z "${TEXT// }" ]; then
  echo "нечего отправлять: на входе пусто" >&2
  exit 1
fi

B64=$(printf '%s' "$TEXT" | base64 | tr -d '\n')
T64=$(printf '%s' "$TITLE" | base64 | tr -d '\n')

REMOTE=$(cat <<'PY'
import base64, json, os, re, sys, urllib.request, urllib.error

text = base64.b64decode(sys.argv[1]).decode('utf-8')
title = base64.b64decode(sys.argv[2]).decode('utf-8')
color = int(sys.argv[3])

env = {}
with open(os.path.expanduser('~/counterplay-bot/.env'), encoding='utf-8') as f:
    for line in f:
        m = re.match(r'^([A-Z_]+)=(.*)$', line.strip())
        if m:
            env[m.group(1)] = m.group(2).strip().strip('"').strip("'")

token = env.get('DISCORD_TOKEN')
# В .env канал может быть записан с комментарием в той же строке — берём первое
# число, иначе Discord отвечает «Unknown Channel» на мусорный идентификатор.
raw = env.get('CH_META_RADAR', '')
m = re.search(r'\d{15,}', raw)
if not token or not m:
    sys.exit('в .env бота нет DISCORD_TOKEN или CH_META_RADAR')
channel = m.group(0)

body = json.dumps({'embeds': [{
    'title': title,
    'description': text[:4000],
    'color': color,
    'footer': {'text': 'From the Counterplay database • counterplays.com'},
}]}).encode('utf-8')

req = urllib.request.Request(
    f'https://discord.com/api/v10/channels/{channel}/messages',
    data=body, method='POST',
    headers={'Authorization': f'Bot {token}',
             'Content-Type': 'application/json',
             'User-Agent': 'counterplay-radar/1.0'})
try:
    with urllib.request.urlopen(req, timeout=20) as r:
        print(f'отправлено в канал {channel}, код {r.status}')
except urllib.error.HTTPError as e:
    sys.exit(f'Discord ответил {e.code}: {e.read()[:300].decode("utf-8", "replace")}')
PY
)

ssh "$HOST" "python3 - '$B64' '$T64' '$COLOR' <<'EOF'
$REMOTE
EOF"
