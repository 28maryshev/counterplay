#!/bin/bash
# База знаний проекта на сервере: на любой машине тот же контекст, что и здесь.
#
#   bash ops/brain-sync.sh setup            первый раз на машине (на этой и на новой)
#   bash ops/brain-sync.sh pull             подтянуть с сервера (хук SessionStart)
#   bash ops/brain-sync.sh push [--every N] [--final]
#                                           отправить (хуки Stop и SessionEnd, journal-push.sh)
#   bash ops/brain-sync.sh sessions         переписка с Claude в обе стороны
#   bash ops/brain-sync.sh status           что где лежит и что не отправлено
#   bash ops/brain-sync.sh secrets-export   ключи и .env → зашифрованный архив на сервере
#   bash ops/brain-sync.sh secrets-import   … и обратно, на новой машине
#
# Что куда едет:
#   CLAUDE.md, ROADMAP.md, journal/, handbook/, seo/, память Claude
#   (.claude/memory), настройки Claude для проекта (.claude/settings.local.json),
#   свои скиллы и настройки Claude (.claude/user/ — копия ~/.claude/skills и
#   ~/.claude/settings.json), отладочные скрипты пайплайна
#       → git collector:journal/knowledge.git. Лежит внутри ~/journal, поэтому
#         ночной бэкап коллектора увозит его в R2 вместе с журналом, зашифрованным.
#   исходники сайта (../Indexcounterplay)  → collector:brain/site.git, удалённый «brain»
#   переписка (~/.claude/projects)         → collector:brain/sessions/
#
# В публичный репозиторий из этого не попадает ничего: всё перечисленное стоит
# в .gitignore, а git базы знаний живёт отдельно — каталог .claude/brain.git с
# рабочим деревом в корне проекта (сам .claude/ тоже в .gitignore).
#
# Хуки зовут скрипт молча: вывод pull попадает в контекст Claude, поэтому
# печатается только то, что пришло с другой машины, и то, что надо чинить.
# Остальное — в журнал ~/.claude/brain-sync/log.txt.
set -uo pipefail

HOST="${COLLECTOR_SSH:-collector}"
DIR=$(cd "$(dirname "$0")/.." && pwd)
WIN_DIR=$(cd "$DIR" && pwd -W 2>/dev/null || pwd)
SITE="${SITE_DIR:-$(dirname "$DIR")/Indexcounterplay}"
G="$DIR/.claude/brain.git"
R_KNOW="journal/knowledge.git"
R_SITE="brain/site.git"
R_SESS="brain/sessions"
R_SECRETS="brain/secrets.enc"
PROJECTS="$HOME/.claude/projects"
STATE="$HOME/.claude/brain-sync"
LOG="$STATE/log.txt"

SSH_OPTS="-o ConnectTimeout=10 -o BatchMode=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=4"
export GIT_SSH_COMMAND="ssh $SSH_OPTS"
# shellcheck disable=SC2086
rsh() { ssh $SSH_OPTS "$HOST" "$@"; }

# Что входит в базу знаний. Путь, которого на этой машине нет, пропускается и
# НЕ удаляется из базы: пропавшая папка памяти не должна стереть её у всех.
PATHS=(CLAUDE.md ROADMAP.md journal handbook seo .claude/memory .claude/settings.local.json
       .claude/user pipeline/check_db.py pipeline/diag.py pipeline/games_check.py
       pipeline/multicheck.py)

# Ключи и настройки служб для secrets-*. Ключа самого коллектора здесь нет:
# без него архив с сервера всё равно не скачать, его переносят руками.
SECRETS_HOME=(.ssh/config .ssh/counterplay.pem .ssh/bing-webmaster.key)
SECRETS_CP=(bot/.env build/telemetry.secret)
SECRETS_SITE=(.env ops/.cloudflare.token)

mkdir -p "$STATE"
if [ -f "$LOG" ] && [ "$(stat -c %s "$LOG" 2>/dev/null || echo 0)" -gt 1000000 ]; then
  tail -n 3000 "$LOG" > "$LOG.tmp" && mv "$LOG.tmp" "$LOG"
fi

QUIET=0
log()  { echo "$(date +'%F %T') $*" >> "$LOG"; }
say()  { log "$*"; [ "$QUIET" = 1 ] || echo "$*"; }
loud() { log "$*"; echo "$*"; }   # видно всегда: это надо чинить
die()  { loud "$*"; exit 1; }
b() { git --git-dir="$G" --work-tree="$DIR" "$@"; }

# Блокировка: хуки Stop идут после каждого ответа и могут наложиться друг на
# друга или на SessionStart соседнего окна. $1 — сколько секунд ждать.
LOCK="$STATE/lock"
lock() {
  local waited=0
  while ! mkdir "$LOCK" 2>/dev/null; do
    # Брошенная блокировка (процесс убили вместе с окном) — старше 15 минут.
    if [ -n "$(find "$LOCK" -maxdepth 0 -mmin +15 2>/dev/null)" ]; then
      rmdir "$LOCK" 2>/dev/null; continue
    fi
    [ "$waited" -ge "${1:-0}" ] && return 1
    sleep 2; waited=$((waited + 2))
  done
  trap 'rmdir "$LOCK" 2>/dev/null' EXIT
}

# Отметка «делали не раньше N секунд назад».
fresh() { [ -f "$1" ] && [ $(( $(date +%s) - $(stat -c %Y "$1") )) -lt "$2" ]; }

present_paths() {
  local p
  for p in "${PATHS[@]}"; do [ -e "$DIR/$p" ] && printf '%s\n' "$p"; done
}

# ── свои скиллы и настройки Claude: живут в ~/.claude, едут через .claude/user ──

user_in() {
  local u="$DIR/.claude/user" d
  mkdir -p "$u/skills"
  for d in "$HOME/.claude/skills"/*/; do
    [ -d "$d" ] || continue
    # synced — скиллы с claude.ai, их раздаёт сам Claude Code на каждой машине.
    case "$(basename "$d")" in synced|.trash) continue ;; esac
    cp -ru "$d" "$u/skills/" 2>>"$LOG"
  done
  [ -f "$HOME/.claude/settings.json" ] && cp -u "$HOME/.claude/settings.json" "$u/settings.json"
  return 0
}

user_out() {
  local u="$DIR/.claude/user"
  if [ -d "$u/skills" ]; then
    mkdir -p "$HOME/.claude/skills"
    cp -ru "$u/skills/." "$HOME/.claude/skills/" 2>>"$LOG"
  fi
  # Общие настройки Claude не перетираем: на машине они могут быть свои.
  if [ -f "$u/settings.json" ] && [ ! -f "$HOME/.claude/settings.json" ]; then
    cp "$u/settings.json" "$HOME/.claude/settings.json"
  fi
  return 0
}

# ── база знаний ──

commit_local() {
  local have=()
  mapfile -t have < <(present_paths)
  [ ${#have[@]} -gt 0 ] || return 0
  # -f обязателен: рабочее дерево общее с репозиторием программы, и его
  # .gitignore перечисляет ровно то, что мы сюда везём. Мусор отсекаем сами.
  b add -A -f -- "${have[@]}" ':(exclude,glob)**/__pycache__/**' ':(exclude,glob)**/*.pyc' 2>>"$LOG" ||
    { loud "база знаний: git add не прошёл — см. $LOG"; return 1; }
  b diff --cached --quiet 2>/dev/null && return 0
  b commit -q -m "sync: $(hostname) $(date +'%F %T')" >>"$LOG" 2>&1 ||
    { loud "база знаний: коммит не прошёл — см. $LOG"; return 1; }
}

# Свести локальное с серверным: закоммитить своё, подтянуть чужое поверх
# (rebase), отправить. В PULLED — файлы, пришедшие с сервера.
PULLED=""
sync_knowledge() {
  commit_local || return 1
  if ! b fetch -q origin 2>>"$LOG"; then
    say "база знаний: сервер недоступен — работаю с локальной копией"
    return 1
  fi
  local before
  before=$(b rev-parse HEAD)
  if b rev-parse -q --verify origin/main >/dev/null && ! b merge-base --is-ancestor origin/main HEAD; then
    if ! b rebase -q origin/main >>"$LOG" 2>&1; then
      b rebase --abort >/dev/null 2>&1
      loud "база знаний разошлась с сервером: одни и те же файлы правили на двух машинах." \
           "Слить руками: git --git-dir=.claude/brain.git rebase origin/main, поправить конфликт," \
           "git --git-dir=.claude/brain.git add <файл> и rebase --continue, затем bash ops/brain-sync.sh push"
      return 1
    fi
    PULLED=$(b diff --name-only "$before" HEAD)
  fi
  if ! b rev-parse -q --verify origin/main >/dev/null || ! b merge-base --is-ancestor HEAD origin/main; then
    b push -q origin HEAD:main 2>>"$LOG" || { loud "база знаний: не отправилась на сервер — см. $LOG"; return 1; }
    log "база знаний отправлена: $(b rev-parse --short HEAD)"
  fi
  return 0
}

# Код программы и сайта — только перемоткой вперёд: ничего не сливаем и не
# затираем; не вышло — сказать и оставить как есть.
code_pull() {
  local out br
  if [ "$(git -C "$DIR" rev-parse --abbrev-ref HEAD 2>/dev/null)" = main ]; then
    out=$(GIT_TERMINAL_PROMPT=0 git -C "$DIR" pull -q --ff-only 2>&1) ||
      loud "программа: git pull не прошёл (${out%%$'\n'*}) — разобраться руками"
  fi
  if [ -d "$SITE/.git" ] && git -C "$SITE" remote | grep -qx brain; then
    br=$(git -C "$SITE" rev-parse --abbrev-ref HEAD 2>/dev/null)
    out=$(git -C "$SITE" pull -q --ff-only brain "$br" 2>&1) ||
      loud "сайт: git pull с сервера не прошёл (${out%%$'\n'*}) — разобраться руками"
  fi
  return 0
}

site_push() {
  [ -d "$SITE/.git" ] && git -C "$SITE" remote | grep -qx brain || return 0
  git -C "$SITE" push -q brain --all 2>>"$LOG" ||
    loud "сайт: история не отправилась на сервер — см. $LOG (на другой машине были свои коммиты? git -C $SITE pull brain)"
}

# ── переписка ──
#
# Файлы разговоров только дописываются в конец, поэтому «новее» = «длиннее»:
# своё длиннее серверного — отправить, серверное длиннее — забрать. Память
# (memory/) сюда не входит, она едет через git выше.
sessions_sync() {
  local tmp remote_list local_list nu nd rc
  tmp=$(mktemp -d)
  mkdir -p "$PROJECTS"
  remote_list=$(rsh "mkdir -p ~/$R_SESS && chmod 700 ~/brain ~/$R_SESS && cd ~/$R_SESS && find . -type f -printf '%s\t%P\n'") ||
    { say "переписка: сервер недоступен"; rm -rf "$tmp"; return 1; }
  local_list=$(cd "$PROJECTS" && find . \( -name memory -o -name 'memory.pre-brain*' \) -prune -o -type f -printf '%s\t%P\n')
  printf '%s\n' "$remote_list" > "$tmp/remote"; printf '%s\n' "$local_list" > "$tmp/local"
  awk -F'\t' 'NR==FNR { if ($2 != "") r[$2] = $1; next }
              $2 != "" && (!($2 in r) || $1 + 0 > r[$2] + 0) { print $2 }' "$tmp/remote" "$tmp/local" > "$tmp/up"
  awk -F'\t' 'NR==FNR { if ($2 != "") l[$2] = $1; next }
              $2 != "" && (!($2 in l) || $1 + 0 > l[$2] + 0) { print $2 }' "$tmp/local" "$tmp/remote" > "$tmp/down"
  nu=$(grep -c . "$tmp/up"); nd=$(grep -c . "$tmp/down")
  if [ "$nu" -gt 0 ]; then
    # tar честно ругается, если файл дописывается прямо сейчас (это текущий
    # разговор), — недописанный хвост уедет в следующий раз.
    (cd "$PROJECTS" && tar czf - --warning=no-file-changed -T "$tmp/up" 2>>"$LOG") |
      rsh "tar xzf - -C ~/$R_SESS"
    rc=${PIPESTATUS[1]}
    [ "$rc" = 0 ] || { loud "переписка: отправка оборвалась — см. $LOG"; rm -rf "$tmp"; return 1; }
  fi
  if [ "$nd" -gt 0 ]; then
    rsh "cd ~/$R_SESS && tar czf - -T -" < "$tmp/down" | (cd "$PROJECTS" && tar xzf -)
    rc=$?
    [ "$rc" = 0 ] || { loud "переписка: загрузка с сервера оборвалась — см. $LOG"; rm -rf "$tmp"; return 1; }
  fi
  say "переписка: отправлено файлов $nu, получено $nd"
  rm -rf "$tmp"
  return 0
}

# ── память Claude ──
#
# Claude Code держит память в ~/.claude/projects/<проект>/memory. Сама папка
# переезжает в .claude/memory (оттуда она едет в базу знаний), а на старом
# месте встаёт связка (junction) на новое: Claude Code пишет по старому пути и
# попадает в новую папку. Можно вдобавок указать autoMemoryDirectory в
# .claude/settings.local.json — тогда она должна смотреть сюда же.
memory_setup() {
  local slug old new
  new="$DIR/.claude/memory"
  slug=$(printf '%s' "$WIN_DIR" | sed 's/[^A-Za-z0-9]/-/g')
  old="$PROJECTS/$slug/memory"
  mkdir -p "$new" "$PROJECTS/$slug"
  if [ -d "$old" ] && [ ! -L "$old" ]; then
    cp -rn "$old/." "$new/"            # то, что пришло с сервера, не затираем
    mv "$old" "$old.pre-brain-$(date +%Y%m%d-%H%M%S)"
    say "память: перенесена в .claude/memory, старая папка оставлена рядом с пометкой pre-brain"
  fi
  if [ ! -e "$old" ]; then
    MSYS2_ARG_CONV_EXCL='*' cmd /c mklink /J "$(cygpath -w "$old")" "$(cygpath -w "$new")" >/dev/null 2>>"$LOG" ||
      loud "память: не создалась связка $old → $new (см. $LOG); хватит и настройки autoMemoryDirectory"
  fi
  # В JSON путь записан с двойными обратными косыми: приводим к прямым и
  # сравниваем без учёта регистра (диск бывает и «c:», и «C:»).
  local v
  v=$(sed -n 's/.*"autoMemoryDirectory" *: *"\([^"]*\)".*/\1/p' "$DIR/.claude/settings.local.json" 2>/dev/null | head -1 |
      sed 's#\\\\#/#g; s#\\#/#g')
  # Настройки может и не быть: связки на старом месте хватает. Опасна только
  # настройка, указывающая в чужую папку.
  if [ -n "$v" ] && [ "${v,,}" != "${WIN_DIR,,}/.claude/memory" ]; then
    loud "память: autoMemoryDirectory ($v) указывает не в эту папку проекта ($WIN_DIR) — проект на всех машинах должен лежать по одному пути"
  fi
}

# ── команды ──

cmd_pull() {
  [ -d "$G" ] || { echo "база знаний на этой машине не настроена: bash ops/brain-sync.sh setup"; return 0; }
  lock 20 || { log "pull: занято другим запуском"; return 0; }
  user_in
  if sync_knowledge && [ -n "$PULLED" ]; then
    local n j
    n=$(printf '%s\n' "$PULLED" | grep -c .)
    j=$(printf '%s\n' "$PULLED" | grep -c '^journal/')
    echo "База знаний: с сервера пришли правки с другой машины — файлов $n, из них журнала $j."
    printf '%s\n' "$PULLED" | grep -qx 'CLAUDE.md' && echo "CLAUDE.md изменился — перечитать его."
    printf '%s\n' "$PULLED" | grep -q '^\.claude/memory/' && echo "Память обновилась — перечитать .claude/memory/MEMORY.md."
    log "подтянуто: $(printf '%s ' $PULLED)"
  fi
  user_out
  code_pull
  return 0
}

cmd_push() {
  local every=0 final=0
  while [ $# -gt 0 ]; do
    case "$1" in
      --every) every="$2"; shift 2 ;;
      --final) final=1; shift ;;
      *) die "push: не знаю ключа $1" ;;
    esac
  done
  [ -d "$G" ] || return 0
  # Из хуков — молча (Stop идёт после каждого ответа); руками и из
  # journal-push.sh — с отчётом и с ожиданием, если занято хуком.
  local wait=30
  if [ "$every" -gt 0 ] || [ "$final" = 1 ]; then QUIET=1; wait=0; fi
  [ "$final" = 1 ] && wait=60
  if [ "$every" -gt 0 ] && fresh "$STATE/pushed" "$every"; then return 0; fi
  lock "$wait" || { say "база знаний: занято другим запуском — отправит он"; return 0; }
  user_in
  sync_knowledge && touch "$STATE/pushed" && say "база знаний отправлена на сервер ($(b rev-parse --short HEAD))"
  site_push
  # Переписка тяжелее — не чаще раза в полчаса, и всегда в конце сессии.
  if [ "$final" = 1 ] || ! fresh "$STATE/sessions" 1800; then
    sessions_sync && touch "$STATE/sessions"
  fi
  return 0
}

cmd_sessions() {
  lock 60 || die "занято другим запуском — попробуй через минуту"
  sessions_sync && touch "$STATE/sessions"
}

cmd_setup() {
  echo "== сервер ($HOST)"
  rsh true 2>/dev/null || die "нет входа на $HOST по ssh. Сначала ключ коллектора в ~/.ssh и запись «Host $HOST» в ~/.ssh/config — см. «Новая машина» в journal/CHECKLIST.md (или у владельца)."
  rsh "set -e; mkdir -p ~/journal ~/brain ~/$R_SESS; chmod 700 ~/brain ~/$R_SESS
       [ -d ~/$R_KNOW ] || git init -q --bare -b main ~/$R_KNOW
       [ -d ~/$R_SITE ] || git init -q --bare -b master ~/$R_SITE" || die "не создались хранилища на сервере"
  echo "  хранилища на месте: ~/$R_KNOW, ~/$R_SITE, ~/$R_SESS"

  echo "== база знаний"
  lock 60 || die "занято другим запуском — попробуй через минуту"
  if [ ! -d "$G" ]; then
    git init -q --bare -b main "$G"
    b config core.bare false
    b config core.worktree "$WIN_DIR"
    b config core.autocrlf false
    b config status.showUntrackedFiles no
    b remote add origin "$HOST:$R_KNOW"
    printf '%s\n' '__pycache__/' '*.pyc' > "$G/info/exclude"
  fi
  # Автор коммитов — тот же, что у программы (там он задан в .git/config, а не
  # глобально); нет и там — имя машины.
  if ! b config user.email >/dev/null; then
    b config user.name "$(git -C "$DIR" config user.name || hostname)"
    b config user.email "$(git -C "$DIR" config user.email || echo "brain@$(hostname)")"
  fi
  b fetch -q origin 2>>"$LOG" || die "не достучался до ~/$R_KNOW — см. $LOG"
  if b rev-parse -q --verify origin/main >/dev/null; then
    if ! b rev-parse -q --verify HEAD >/dev/null; then
      # Новая машина. Что уже лежит на диске под теми же именами — сохранить.
      local bak f
      bak="$DIR/.claude/brain-before-setup-$(date +%Y%m%d-%H%M%S)"
      while read -r f; do
        if [ -e "$DIR/$f" ]; then mkdir -p "$bak/$(dirname "$f")"; cp -p "$DIR/$f" "$bak/$f"; fi
      done < <(b ls-tree -r --name-only origin/main)
      b checkout -q -f -B main origin/main || die "не выложилась база знаний — см. вывод выше"
      [ -d "$bak" ] && echo "  что лежало здесь раньше под теми же именами — в $bak"
      echo "  база знаний получена с сервера: $(b ls-files | wc -l) файлов"
    fi
    b branch -q -u origin/main main 2>/dev/null
    memory_setup
    user_in
    sync_knowledge || true
  else
    # Сервер пуст — эта машина первая.
    memory_setup
    user_in
    commit_local || die "не собрался первый коммит"
    b push -q -u origin main 2>>"$LOG" || die "не отправилось — см. $LOG"
    echo "  база знаний отправлена на сервер: $(b ls-files | wc -l) файлов"
  fi
  user_out

  echo "== сайт ($SITE)"
  if [ -d "$SITE/.git" ]; then
    git -C "$SITE" remote | grep -qx brain || git -C "$SITE" remote add brain "$HOST:$R_SITE"
    if rsh "git -C ~/$R_SITE rev-parse -q --verify HEAD" >/dev/null 2>&1; then
      git -C "$SITE" fetch -q brain && echo "  связан с сервером"
      git -C "$SITE" branch -q -u "brain/$(git -C "$SITE" rev-parse --abbrev-ref HEAD)" 2>/dev/null
    else
      git -C "$SITE" push -q -u brain --all 2>>"$LOG" && echo "  история сайта отправлена на сервер" ||
        loud "  сайт не отправился — см. $LOG"
    fi
  elif rsh "git -C ~/$R_SITE rev-parse -q --verify HEAD" >/dev/null 2>&1; then
    git clone -q -o brain "$HOST:$R_SITE" "$SITE" && echo "  склонирован в $SITE (дальше: cd $SITE && npm ci)"
  else
    echo "  ни здесь, ни на сервере сайта нет — пропускаю"
  fi

  echo "== переписка (первый раз может занять минуты)"
  sessions_sync && touch "$STATE/sessions"

  echo "== инструменты"
  local t
  for t in git ssh dotnet python node npm gh claude; do
    command -v "$t" >/dev/null 2>&1 && echo "  [ ok ] $t" || echo "  [нет ] $t"
  done
  gh auth status >/dev/null 2>&1 || echo "  gh: не выполнен вход — gh auth login"

  echo "== ключи и .env"
  local miss=0 p
  for p in "${SECRETS_HOME[@]}"; do [ -f "$HOME/$p" ] || { echo "  нет ~/$p"; miss=1; }; done
  for p in "${SECRETS_CP[@]}"; do [ -f "$DIR/$p" ] || { echo "  нет $p"; miss=1; }; done
  for p in "${SECRETS_SITE[@]}"; do [ -f "$SITE/$p" ] || { echo "  нет $SITE/$p"; miss=1; }; done
  ls "$HOME"/.ssh/counterplay-506019-*.json >/dev/null 2>&1 || { echo "  нет ключа Search Console (~/.ssh/counterplay-506019-*.json)"; miss=1; }
  if [ "$miss" = 1 ]; then
    if rsh "test -f ~/$R_SECRETS" 2>/dev/null; then
      echo "  → в обычном терминале: bash ops/brain-sync.sh secrets-import"
    else
      echo "  → архива ключей на сервере нет: на машине, где они есть, bash ops/brain-sync.sh secrets-export"
    fi
  else
    echo "  все на месте"
  fi
  echo
  echo "Готово. Хуки в .claude/settings.local.json дальше синхронизируют всё сами."
}

cmd_status() {
  [ -d "$G" ] || { echo "база знаний не настроена: bash ops/brain-sync.sh setup"; return 1; }
  local net=1 ahead behind dirty have=()
  b fetch -q origin 2>/dev/null || net=0
  mapfile -t have < <(present_paths)
  ahead=$(b rev-list --count origin/main..HEAD 2>/dev/null || echo '?')
  behind=$(b rev-list --count HEAD..origin/main 2>/dev/null || echo '?')
  dirty=$(b status --porcelain -- "${have[@]}" | wc -l)
  echo "база знаний: последний коммит $(b log -1 --format='%cd · %s' --date=format:'%F %H:%M')"
  [ "$net" = 1 ] || echo "  (сервер недоступен — сравниваю с тем, что знал раньше)"
  echo "  не отправлено коммитов: $ahead · не подтянуто: $behind · незакоммиченных файлов: $dirty"
  local p
  for p in "${PATHS[@]}"; do [ -e "$DIR/$p" ] || echo "  нет на этой машине: $p"; done
  [ -f "$STATE/pushed" ] && echo "  последняя отправка: $(date -r "$STATE/pushed" +'%F %H:%M')"
  if [ -d "$SITE/.git" ]; then
    if git -C "$SITE" remote | grep -qx brain; then
      echo "сайт: не отправлено коммитов $(git -C "$SITE" rev-list --count '@{u}..HEAD' 2>/dev/null || echo '?')"
    else
      echo "сайт: не связан с сервером — bash ops/brain-sync.sh setup"
    fi
  fi
  echo "переписка: здесь $(find "$PROJECTS" -name '*.jsonl' 2>/dev/null | wc -l) разговоров," \
       "на сервере $(rsh "find ~/$R_SESS -name '*.jsonl' | wc -l" 2>/dev/null || echo '?')"
  [ -f "$STATE/sessions" ] && echo "  последняя сверка: $(date -r "$STATE/sessions" +'%F %H:%M')"
  return 0
}

ask_pass() {  # $1 — подсказка; пароль в переменную PASS
  [ -t 0 ] || die "пароль спрашивается в терминале: запусти команду в обычном окне Git Bash"
  read -rs -p "$1" PASS; echo
  PASS=${PASS%$'\r'}   # консоль Windows бывает, что отдаёт и возврат каретки
}

cmd_secrets_export() {
  local tmp p n=0 PASS p2
  # Путь подставляется сразу: ловушка EXIT срабатывает после возврата из
  # функции, local tmp к тому времени уже нет — с '$tmp' папка с ключами
  # оставалась во временной (tests/ShellTraps).
  tmp=$(mktemp -d); trap "rm -rf '$tmp'" EXIT
  mkdir -p "$tmp/home" "$tmp/counterplay" "$tmp/site"
  put() {  # $1 — откуда, $2 — куда в архиве
    [ -f "$1" ] || { echo "  нет $1 — пропускаю"; return 0; }
    mkdir -p "$(dirname "$2")"; cp -p "$1" "$2"; n=$((n + 1))
  }
  for p in "${SECRETS_HOME[@]}"; do put "$HOME/$p" "$tmp/home/$p"; done
  for p in "$HOME"/.ssh/counterplay-506019-*.json; do put "$p" "$tmp/home/.ssh/$(basename "$p")"; done
  for p in "${SECRETS_CP[@]}"; do put "$DIR/$p" "$tmp/counterplay/$p"; done
  for p in "${SECRETS_SITE[@]}"; do put "$SITE/$p" "$tmp/site/$p"; done
  cygpath -w "$HOME" > "$tmp/home.path"
  [ "$n" -gt 0 ] || die "нечего упаковывать"
  # Ввод не виден, опечататься легко — три попытки, а не выход с первой.
  local try ok=0
  for try in 1 2 3; do
    ask_pass "Пароль для архива ключей (не короче 12 знаков, набор не виден): "
    if [ ${#PASS} -lt 12 ]; then echo "  короче 12 знаков (${#PASS}) — ещё раз"; continue; fi
    p2="$PASS"; ask_pass "Ещё раз тот же: "
    if [ "$PASS" = "$p2" ]; then ok=1; break; fi
    echo "  не совпали (знаков: ${#p2} и ${#PASS}) — ещё раз"
  done
  [ "$ok" = 1 ] || die "три попытки не вышли — запусти команду заново"
  tar czf - -C "$tmp" home counterplay site home.path |
    BRAIN_PASS="$PASS" openssl enc -aes-256-cbc -pbkdf2 -iter 200000 -salt -pass env:BRAIN_PASS |
    rsh "umask 077; cat > ~/$R_SECRETS.new && mv ~/$R_SECRETS.new ~/$R_SECRETS" ||
    die "архив не отправился"
  echo "отправлено файлов: $n → $HOST:~/$R_SECRETS (зашифровано; пароль нигде не хранится — запомни его)"
}

cmd_secrets_import() {
  local tmp PASS f rel dst old newhome have
  tmp=$(mktemp -d); trap "rm -rf '$tmp'" EXIT   # путь сразу — см. secrets-export
  rsh "test -f ~/$R_SECRETS" || die "на сервере нет архива ключей: сначала secrets-export там, где ключи есть"
  ask_pass "Пароль архива ключей: "
  rsh "cat ~/$R_SECRETS" |
    BRAIN_PASS="$PASS" openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 -pass env:BRAIN_PASS 2>/dev/null |
    tar xzf - -C "$tmp" 2>/dev/null || die "не расшифровалось — неверный пароль?"
  place() {  # $1 — корень в архиве, $2 — куда на этой машине
    [ -d "$tmp/$1" ] || return 0
    while read -r f; do
      rel=${f#"$tmp/$1/"}; dst="$2/$rel"
      [ "$1/$rel" = "home/.ssh/config" ] && continue   # конфиг ssh — ниже, отдельно
      if [ ! -e "$dst" ]; then
        mkdir -p "$(dirname "$dst")"; cp -p "$f" "$dst"; echo "  + $dst"
      elif ! cmp -s "$f" "$dst"; then
        cp -p "$f" "$dst.from-brain"; echo "  ≠ $dst уже есть и отличается — архивный рядом: $dst.from-brain"
      fi
    done < <(find "$tmp/$1" -type f)
  }
  place home "$HOME"
  place counterplay "$DIR"
  if [ -d "$SITE" ]; then place site "$SITE"; else echo "  нет $SITE — .env сайта пропущены (сначала setup)"; fi
  # ssh: недостающие блоки Host дописать, путь к ключам поправить на здешний дом.
  if [ -f "$tmp/home/.ssh/config" ]; then
    old=$(cat "$tmp/home.path" 2>/dev/null); newhome=$(cygpath -w "$HOME")
    mkdir -p "$HOME/.ssh"; touch "$HOME/.ssh/config"
    have=$(awk 'tolower($1) == "host" { for (i = 2; i <= NF; i++) print $i }' "$HOME/.ssh/config")
    HAVE="$have" O="$old" N="$newhome" awk '
      BEGIN { k = split(ENVIRON["HAVE"], a, "\n"); for (i = 1; i <= k; i++) h[a[i]] = 1 }
      tolower($1) == "host" { keep = !($2 in h) }
      keep {
        s = $0; out = ""; o = ENVIRON["O"]
        if (o != "") while ((i = index(s, o)) > 0) { out = out substr(s, 1, i - 1) ENVIRON["N"]; s = substr(s, i + length(o)) }
        print out s
      }' "$tmp/home/.ssh/config" > "$tmp/ssh.add"
    if [ -s "$tmp/ssh.add" ]; then
      { echo; cat "$tmp/ssh.add"; } >> "$HOME/.ssh/config"
      echo "  ~/.ssh/config: дописаны блоки $(awk 'tolower($1)=="host"{printf "%s ", $2}' "$tmp/ssh.add")"
    fi
  fi
  echo "готово. Проверка: bash ops/healthcheck.sh"
}

case "${1:-}" in
  setup) cmd_setup ;;
  pull) cmd_pull ;;
  push) shift; cmd_push "$@" ;;
  sessions) cmd_sessions ;;
  status) cmd_status ;;
  secrets-export) cmd_secrets_export ;;
  secrets-import) cmd_secrets_import ;;
  *) sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; exit 1 ;;
esac
