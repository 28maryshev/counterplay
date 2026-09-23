#!/bin/bash
# Проверка всего хозяйства одной командой:  bash ops/healthcheck.sh
#
# Запускается с машины разработки: сама ходит по ssh на оба сервера, дёргает сайт
# снаружи, читает релиз на GitHub и хранилище копий. Ничего не меняет — только
# смотрит и считает проблемы.
#
# Что проверяем и почему именно это: каждая строка здесь появилась после того,
# как соответствующая вещь однажды сломалась молча. Свежесть статуса — потому что
# коллектор простоял 39 часов, выглядя живым. Дата последней копии — потому что
# уборка копий когда-то пропускалась ровно в дни падений. Адрес в ssh-конфиге —
# потому что сервер один раз «исчез», а на деле переехал.
#
# Коды выхода: 0 — всё хорошо, 1 — есть предупреждения, 2 — есть поломки.
set -uo pipefail

COLLECTOR="${COLLECTOR_SSH:-collector}"
SITE="${SITE_SSH:-cp-site}"
SITE_URL="${SITE_URL:-https://counterplays.com}"
REPO="${GITHUB_REPO:-28maryshev/counterplay}"
DIR=$(cd "$(dirname "$0")/.." && pwd)

WARN=0; BAD=0
ok()   { echo "  [ ok ] $*"; }
warn() { echo "  [ ?? ] $*"; WARN=$((WARN + 1)); }
bad()  { echo "  [FAIL] $*"; BAD=$((BAD + 1)); }
head2() { echo; echo "$*"; }

# Значение по ключу из ответа сервера (формат key=value).
val() { printf '%s\n' "$1" | sed -n "s/^$2=//p" | head -1; }

now=$(date -u +%s)
echo "Counterplay — проверка хозяйства, $(date -u +'%F %H:%M') UTC"

# ─────────────────────────── сервер коллектора ───────────────────────────
head2 "СЕРВЕР КОЛЛЕКТОРА ($COLLECTOR)"
R=$(ssh -o BatchMode=yes -o ConnectTimeout=15 "$COLLECTOR" 'bash -s' <<'REMOTE' 2>/dev/null
cd "$HOME" || exit 1
echo "up=1"
echo "collector_state=$(docker inspect -f '{{.State.Status}}' counterplay-collector 2>/dev/null)"
echo "collector_since=$(docker inspect -f '{{.State.StartedAt}}' counterplay-collector 2>/dev/null)"
echo "bot_state=$(docker inspect -f '{{.State.Status}}' counterplay-bot 2>/dev/null)"
echo "disk_free_gb=$(df -BG --output=avail "$HOME" | tail -1 | tr -dc '0-9')"
echo "status_json=$(tr -d '\n' < counterplay-collector/control/status 2>/dev/null)"
echo "status_age=$(( $(date -u +%s) - $(stat -c %Y counterplay-collector/control/status 2>/dev/null || echo 0) ))"
echo "db_age=$(( $(date -u +%s) - $(stat -c %Y counterplay-collector/data/data.db 2>/dev/null || echo 0) ))"
echo "key_present=$([ -f counterplay-collector/control/key ] && echo 1 || echo 0)"
echo "ops_db=$([ -f counterplay-collector/data/ops.db ] && echo 1 || echo 0)"
echo "journal_files=$(ls journal/*.md 2>/dev/null | wc -l)"
echo "cron_backup=$(crontab -l 2>/dev/null | grep -c backup-collector.sh)"
echo "cron_watch=$(crontab -l 2>/dev/null | grep -c site_watch.sh)"
echo "cron_cleanup=$(crontab -l 2>/dev/null | grep -c cleanup.sh)"
echo "backup_last=$(grep 'готово. занято' backup.log 2>/dev/null | tail -1 | cut -c1-10)"
# Сторож патчей: что он помнит. Цикл патча начинается с него, и молчащий сторож
# внешне неотличим от «патча не было».
echo "patch_seen=$(docker exec counterplay-bot node -e '
  const { kvGet } = require("/app/db/botDb");
  process.stdout.write([kvGet("patchwatch_last_official"), kvGet("patchwatch_last_ready")].join("/"));
' 2>/dev/null)"
# Простой: последние снимки журнала эксплуатации. Если счётчик не двигался, а
# состояние collecting — сбор встал, сколько бы бодро ни выглядел статус.
python3 - <<'PY' 2>/dev/null
import sqlite3, time
try:
    con = sqlite3.connect('file:%s/counterplay-collector/data/ops.db?mode=ro' % __import__('os').environ['HOME'], uri=True)
    rows = con.execute("SELECT ts, state, matches FROM events WHERE kind='sample' ORDER BY ts DESC LIMIT 12").fetchall()
    if len(rows) >= 2:
        newest, oldest = rows[0], rows[-1]
        moved = (newest[2] or 0) - (oldest[2] or 0)
        print('sample_span=%d' % (newest[0] - oldest[0]))
        print('sample_moved=%d' % moved)
        print('sample_state=%s' % (newest[1] or ''))
    print('sample_count=%d' % len(rows))
except Exception:
    print('sample_count=0')
PY
REMOTE
)

if [ -z "$R" ]; then
  bad "не отвечает по ssh — проверь адрес и ключ в ~/.ssh/config (сервер уже один раз переезжал)"
else
  ok "доступен по ssh"
  [ "$(val "$R" collector_state)" = running ] && ok "контейнер коллектора работает" \
    || bad "контейнер коллектора не запущен: $(val "$R" collector_state)"
  [ "$(val "$R" bot_state)" = running ] && ok "контейнер бота работает" \
    || bad "контейнер бота не запущен: $(val "$R" bot_state)"

  age=$(val "$R" status_age)
  if [ "${age:-99999}" -lt 180 ]; then ok "статус свежий (${age} с назад)"
  else bad "статус не обновлялся ${age} с — демон не дышит"; fi

  st=$(printf '%s' "$(val "$R" status_json)" | sed -n 's/.*"state": *"\([a-z_]*\)".*/\1/p')
  [ -n "$st" ] && ok "состояние: $st" || warn "состояние не прочиталось"

  span=$(val "$R" sample_span); moved=$(val "$R" sample_moved); sstate=$(val "$R" sample_state)
  sc=$(val "$R" sample_count)
  if [ "${sc:-0}" -lt 2 ]; then
    warn "снимков пока ${sc:-0} — движение сбора можно будет оценить через ~20 минут"
  elif [ "$sstate" = collecting ] && [ "${span:-0}" -gt 1800 ] && [ "${moved:-1}" -le 0 ]; then
    bad "СБОР СТОИТ: за $(( span / 60 )) мин ни одного нового матча, а состояние collecting"
  elif [ "$sstate" = collecting ]; then
    ok "сбор движется: +${moved} матчей за $(( ${span:-0} / 60 )) мин"
  else
    ok "сбора нет по делу (состояние $sstate)"
  fi

  [ "$(val "$R" key_present)" = 1 ] && ok "ключ Riot на месте" \
    || { [ "$st" = waiting_key ] && warn "ключа нет — демон ждёт новый (/collect key:…)" \
         || ok "ключа нет (и не нужен: $st)"; }

  # Сторож патчей. Номер у Riot берём сами — так проверка не зависит от бота.
  seen=$(val "$R" patch_seen)
  live=$(curl -fsS --max-time 10 https://ddragon.leagueoflegends.com/api/versions.json 2>/dev/null \
         | sed -n 's/^\[\"\([0-9]*\.[0-9]*\)\..*/\1/p' | head -1)
  seen_official=${seen%%/*}
  if [ -z "$seen_official" ] || [ "$seen_official" = null ]; then
    warn "сторож патчей ничего не помнит — первый прогон ещё не случился"
  elif [ -z "$live" ]; then
    warn "Data Dragon не ответил — номер патча не проверить"
  elif [ "$seen_official" = "$live" ]; then
    ok "патч: у Riot $live, сторож знает (готов: ${seen#*/})"
  else
    warn "патч: у Riot $live, а сторож помнит $seen_official — объявление в течение часа (:45 UTC)"
  fi

  free=$(val "$R" disk_free_gb)
  if   [ "${free:-0}" -lt 3 ]; then bad "диск: свободно ${free} ГБ — публикация не влезет"
  elif [ "${free:-0}" -lt 6 ]; then warn "диск: свободно ${free} ГБ, пора прибраться"
  else ok "диск: свободно ${free} ГБ"; fi

  dba=$(val "$R" db_age)
  if [ "$st" = collecting ] && [ "${dba:-0}" -gt 3600 ]; then
    bad "база не менялась $(( dba / 60 )) мин при активном сборе"
  else ok "база писалась $(( ${dba:-0} / 60 )) мин назад"; fi

  [ "$(val "$R" ops_db)" = 1 ] && ok "журнал эксплуатации ведётся" || warn "нет ops.db"

  jf=$(val "$R" journal_files)
  lf=$(ls "$DIR"/journal/*.md 2>/dev/null | wc -l)
  if [ "${jf:-0}" -lt "${lf:-0}" ]; then
    warn "журнал на сервере отстал: там ${jf}, здесь ${lf} — нужен ops/journal-push.sh"
  else ok "журнал на сервере: ${jf} записей"; fi

  for c in backup watch cleanup; do
    [ "$(val "$R" "cron_$c")" -ge 1 ] 2>/dev/null && ok "задача в cron: $c" || warn "нет задачи в cron: $c"
  done

  bl=$(val "$R" backup_last)
  today=$(date -u +%F); yday=$(date -u -d yesterday +%F 2>/dev/null || echo '')
  if [ "$bl" = "$today" ] || { [ -n "$yday" ] && [ "$bl" = "$yday" ]; }; then
    ok "копия делалась: $bl"
  else bad "последняя удачная копия: ${bl:-нет записи} — бэкап не отрабатывает"; fi
fi

# ───────────────────────────── сервер сайта ─────────────────────────────
head2 "СЕРВЕР САЙТА ($SITE)"
S=$(ssh -o BatchMode=yes -o ConnectTimeout=15 "$SITE" 'bash -s' <<'REMOTE' 2>/dev/null
cd "$HOME" || exit 1
echo "up=1"
echo "web=$(docker inspect -f '{{.State.Status}}' counterplay-site-web-1 2>/dev/null)"
echo "db=$(docker inspect -f '{{.State.Status}}' counterplay-site-db-1 2>/dev/null)"
echo "disk_free_gb=$(df -BG --output=avail "$HOME" | tail -1 | tr -dc '0-9')"
echo "build_cache_gb=$(docker system df 2>/dev/null | awk '/Build Cache/ {gsub(/GB/,"",$4); print int($4)}')"
echo "cron_backup=$(crontab -l 2>/dev/null | grep -c backup-site.sh)"
echo "cron_cleanup=$(crontab -l 2>/dev/null | grep -c cleanup.sh)"
echo "backup_last=$(grep 'готово. занято' backup.log 2>/dev/null | tail -1 | cut -c1-10)"
echo "draft_age=$(( $(date -u +%s) - $(stat -c %Y counterplay-site/data/draft/tiers.json 2>/dev/null || echo 0) ))"
# Свой публичный адрес сервер называет сам: в репозитории его нет и быть не
# должно, а для проверки «снаружи не пускает» он нужен.
TOK=$(curl -s -m 5 -X PUT "http://169.254.169.254/latest/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 60" 2>/dev/null)
echo "public_ip=$(curl -s -m 5 -H "X-aws-ec2-metadata-token: $TOK" http://169.254.169.254/latest/meta-data/public-ipv4 2>/dev/null)"
REMOTE
)

if [ -z "$S" ]; then
  bad "не отвечает по ssh"
else
  ok "доступен по ssh"
  [ "$(val "$S" web)" = running ] && ok "контейнер сайта работает" || bad "сайт не запущен: $(val "$S" web)"
  [ "$(val "$S" db)" = running ] && ok "postgres работает" || bad "postgres не запущен: $(val "$S" db)"

  free=$(val "$S" disk_free_gb); cache=$(val "$S" build_cache_gb)
  if   [ "${free:-0}" -lt 3 ]; then bad "диск: свободно ${free} ГБ"
  elif [ "${free:-0}" -lt 6 ]; then warn "диск: свободно ${free} ГБ (кэш сборки ${cache:-?} ГБ — гонять cleanup.sh)"
  else ok "диск: свободно ${free} ГБ"; fi

  da=$(val "$S" draft_age)
  if [ "${da:-999999}" -gt 604800 ]; then warn "выгрузки сайта старше недели ($(( da / 86400 )) дн)"
  else ok "выгрузки сайта обновлялись $(( ${da:-0} / 3600 )) ч назад"; fi

  [ "$(val "$S" cron_backup)" -ge 1 ] 2>/dev/null && ok "задача в cron: backup" || warn "нет задачи в cron: backup"
  [ "$(val "$S" cron_cleanup)" -ge 1 ] 2>/dev/null && ok "задача в cron: cleanup" || warn "нет задачи в cron: cleanup"

  bl=$(val "$S" backup_last); today=$(date -u +%F); yday=$(date -u -d yesterday +%F 2>/dev/null || echo '')
  if [ "$bl" = "$today" ] || { [ -n "$yday" ] && [ "$bl" = "$yday" ]; }; then ok "копия делалась: $bl"
  else bad "последняя удачная копия: ${bl:-нет записи}"; fi
fi

# ─────────────────────── связь между серверами ───────────────────────
# Эти проверки — про дороги, которые рвутся МОЛЧА. Коллектор заливает свежие
# данные на сайт по ssh; оборвётся эта дорога (сменился адрес, ужали правила в
# группе безопасности) — никто не заметит, просто сайт начнёт тихо отставать от
# базы. Именно так уже было: страницы месяц показывали прошлый патч.
head2 "СВЯЗЬ МЕЖДУ СЕРВЕРАМИ"

site_ip=$(val "$S" public_ip)

# Коллектор → сайт. Ходит туда `publish_site.sh` после каждой публикации базы.
link=$(ssh -o BatchMode=yes -o ConnectTimeout=20 "$COLLECTOR"   "ssh -o BatchMode=yes -o ConnectTimeout=15 -o StrictHostKeyChecking=no    \$(grep -E '^SITE_SSH=' ~/counterplay-collector/.env | sed -E 's/^[^=]+=//; s/\"//g')    'echo alive' 2>/dev/null" 2>/dev/null)
if [ "$link" = alive ]; then
  ok "коллектор достаёт до сайта по ssh (этим путём едут данные)"
else
  bad "КОЛЛЕКТОР НЕ ДОСТАЁТ ДО САЙТА — выкладка данных встанет молча"
fi

# Адрес сайта известен публично (лежал в истории репозитория), поэтому прямой
# вход должен быть закрыт: иначе Cloudflare обходится вместе с кэшем и защитой.
if [ -n "$site_ip" ]; then
  dcode=$(curl -s -o /dev/null -m 12 -w '%{http_code}' -H "Host: ${SITE_URL#https://}"           "http://$site_ip/" 2>/dev/null)
  if [ "$dcode" = 000 ]; then
    ok "прямой вход на сайт закрыт (только Cloudflare)"
  else
    bad "САЙТ ОТВЕЧАЕТ НАПРЯМУЮ ($dcode) — Cloudflare обходится, проверь группу безопасности"
  fi
else
  warn "не узнал публичный адрес сайта — проверку прямого входа пропускаю"
fi

# А вот это обратная сторона той же медали: закрыли так, что и Cloudflare не
# проходит. Запрос с меткой гарантированно уходит мимо кэша, до самого сервера.
ocode=$(curl -sL -o /dev/null -m 25 -w '%{http_code}' "$SITE_URL/?hc=$$" 2>/dev/null)
[ "$ocode" = 200 ] && ok "Cloudflare достаёт до сервера (запрос мимо кэша)"                    || bad "CLOUDFLARE НЕ ДОСТАЁТ ДО СЕРВЕРА ($ocode) — сверь правила с cloudflare.com/ips-v4"

# ───────────────────────────── сайт снаружи ─────────────────────────────
head2 "САЙТ СНАРУЖИ ($SITE_URL)"
code=$(curl -sL -o /dev/null -w '%{http_code}' --max-time 25 "$SITE_URL" 2>/dev/null)
[ "$code" = 200 ] && ok "главная отвечает 200 (после перехода на язык)"   || bad "главная отвечает $code"
acode=$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$SITE_URL/admin" 2>/dev/null)
case "$acode" in
  401|403|404|302|307) ok "админка закрыта снаружи ($acode)" ;;
  200) bad "АДМИНКА ОТКРЫТА снаружи — это уже случалось из-за кэша" ;;
  *) warn "админка отвечает $acode" ;;
esac

# ──────────────────────── база движка в релизе ────────────────────────
head2 "БАЗА ДВИЖКА (релиз data)"
man=$(curl -sL --max-time 25 "https://github.com/$REPO/releases/download/data/data-version.json" 2>/dev/null)
if [ -z "$man" ]; then
  bad "манифест не скачивается — программа не увидит обновление данных"
else
  patch=$(printf '%s' "$man" | sed -n 's/.*"patch": *"\([^"]*\)".*/\1/p')
  upd=$(printf '%s' "$man" | sed -n 's/.*"updated": *"\([^"]*\)".*/\1/p')
  ts=$(date -u -d "$upd" +%s 2>/dev/null || echo 0)
  age=$(( (now - ts) / 3600 ))
  if   [ "$ts" = 0 ];     then warn "дата публикации не разобралась: $upd"
  elif [ "$age" -gt 72 ]; then bad "база не публиковалась $age ч (патч $patch)"
  elif [ "$age" -gt 36 ]; then warn "база публиковалась $age ч назад (патч $patch)"
  else ok "база свежая: патч $patch, $age ч назад"; fi
fi

# ───────────────────────────── копии в R2 ─────────────────────────────
head2 "ХРАНИЛИЩЕ КОПИЙ (R2)"
B=$(ssh -o BatchMode=yes -o ConnectTimeout=15 "$COLLECTOR" '. ~/.backup.env 2>/dev/null; B=${R2_BUCKET:-counterplay-backups}
echo "used_mb=$(rclone size --json "r2:$B" 2>/dev/null | python3 -c "import json,sys; print(int(json.load(sys.stdin)[\"bytes\"]/1048576))" 2>/dev/null || echo 0)"
echo "col_last=$(rclone lsf --dirs-only "r2:$B/collector/daily/" 2>/dev/null | sort | tail -1 | tr -d /)"
echo "site_last=$(rclone lsf --dirs-only "r2:$B/site/daily/" 2>/dev/null | sort | tail -1 | tr -d /)"
echo "col_files=$(rclone lsf "r2:$B/collector/daily/$(rclone lsf --dirs-only "r2:$B/collector/daily/" 2>/dev/null | sort | tail -1)" 2>/dev/null | tr "\n" " ")"' 2>/dev/null)

if [ -z "$B" ]; then
  warn "не удалось опросить хранилище"
else
  used=$(val "$B" used_mb); pct=$(( ${used:-0} * 100 / 10240 ))
  if   [ "$pct" -gt 85 ]; then bad "занято ${used} МБ (${pct}% от 10 ГБ)"
  elif [ "$pct" -gt 70 ]; then warn "занято ${used} МБ (${pct}% от 10 ГБ)"
  else ok "занято ${used} МБ (${pct}% от 10 ГБ)"; fi
  today=$(date -u +%F); yday=$(date -u -d yesterday +%F 2>/dev/null || echo '')
  for who in col site; do
    d=$(val "$B" "${who}_last")
    if [ "$d" = "$today" ] || { [ -n "$yday" ] && [ "$d" = "$yday" ]; }; then ok "свежая копия ($who): $d"
    else bad "копия ($who) от ${d:-никогда} — старше суток"; fi
  done
  f=$(val "$B" col_files)
  for want in data.db.gz ops.db.gz journal.enc env.enc; do
    case "$f" in *"$want"*) ok "в копии есть $want" ;; *) warn "в копии нет $want" ;; esac
  done
fi

# ──────────────────────── локальная машина ────────────────────────
head2 "ЛОКАЛЬНАЯ МАШИНА"
for repo in "$DIR" "${SITE_REPO:-/c/Indexcounterplay}"; do
  [ -d "$repo/.git" ] || { warn "нет репозитория: $repo"; continue; }
  name=$(basename "$repo")
  dirty=$(git -C "$repo" status --porcelain | wc -l)
  [ "$dirty" -eq 0 ] && ok "$name: рабочее дерево чистое" || warn "$name: не закоммичено файлов: $dirty"
  if git -C "$repo" remote | grep -q .; then
    ahead=$(git -C "$repo" rev-list --count '@{u}..HEAD' 2>/dev/null || echo '?')
    [ "$ahead" = 0 ] && ok "$name: всё запушено" || warn "$name: не отправлено коммитов: $ahead"
  else
    ok "$name: без удалённого репозитория (выкладка через deploy.sh)"
  fi
done
n=$(ls "$DIR"/journal/*.md 2>/dev/null | wc -l)
[ "$n" -gt 0 ] && ok "журнал на месте: $n записей" || bad "нет записей журнала"
for f in "$HOME/.ssh/config" "$HOME/.claude/projects/c--Counterplay/memory/MEMORY.md"; do
  [ -f "$f" ] && ok "на месте: $(basename "$f")" || warn "нет файла: $f"
done

# ───────────────────────────── итог ─────────────────────────────
echo
if   [ "$BAD" -gt 0 ];  then echo "ИТОГ: поломок $BAD, предупреждений $WARN"; exit 2
elif [ "$WARN" -gt 0 ]; then echo "ИТОГ: всё работает, предупреждений $WARN"; exit 1
else echo "ИТОГ: всё в порядке"; exit 0; fi
