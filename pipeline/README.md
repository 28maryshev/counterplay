# Коллектор статистики

Сбор матчей Riot MATCH-V5 → `data.db` → публикация в GitHub release `data`
(приложение обновляется само по `data-version.json`).

Два способа запуска — одинаковый код, разный сценарий:

| | Где | Когда |
|---|---|---|
| **CLI** | твоя машина | разовый сбор, отладка |
| **Сервис** | сервер, 24/7 | постоянный сбор, ключ шлёшь из Discord |

## Сбор параллелится по регионам

Riot считает лимит на пару **(ключ, хост роутинга)**, а не на IP. Поэтому:

- регионы (`euw1`/`na1`/`kr`) идут **в потоках одновременно** — каждый со своим
  лимитером, прирост почти линейный;
- регионы с общим хостом MATCH-V5 (`euw1`+`eun1` → `europe`) сами встают в
  очередь этого хоста, лимит не нарушается;
- **больше инстансов не ускорят** сбор: лимит на ключе, а не на машине. Реальный
  прирост даёт только production-ключ (30 000 req/10 мин против 100 req/2 мин).

## CLI

```bash
py -3.12 pipeline/collect.py                       # ключ спросит сам, соберёт всё
py -3.12 pipeline/collect.py --region euw1,na1,kr  # только эти регионы
py -3.12 pipeline/publish_data.py                  # выложить базу (нужен GITHUB_TOKEN)
```

## Сервис на сервере

Ставится рядом с ботом — они делят том `control`: бот кладёт туда ключ,
коллектор его подхватывает (в течение ~15 с).

```
~/counterplay-bot/        (бот)
~/counterplay-collector/  (этот каталог)
  ├── control/   ← общий с ботом: key, status
  └── data/      ← data.db (переживает пересборку)
```

### Развернуть

```bash
# 1. Залить код
scp -r pipeline SERVER:counterplay-collector
scp pipeline/.env SERVER:counterplay-collector/.env     # заполнить из .env.example

# 2. ВАЖНО: засеять уже собранную базу — иначе коллектор начнёт с нуля
#    и заново перемелет то, что уже есть.
ssh SERVER 'mkdir -p counterplay-collector/data counterplay-collector/control'
scp pipeline/data.db SERVER:counterplay-collector/data/data.db

# 3. Поднять
ssh SERVER 'cd counterplay-collector && docker compose up -d --build'
ssh SERVER 'docker logs -f counterplay-collector'

# 3. Перезалить бота — ему добавился том control и команда /collect
scp -r bot SERVER:counterplay-bot && ssh SERVER 'cd counterplay-bot && \
  node deploy-commands.js && docker compose up -d --build'
```

`deploy-commands.js` обязателен: без него Discord не покажет новую `/collect`.

### Работа

Все команды эфемерные (ключ не попадает в историю канала) и только для
`ADMIN_USER_IDS`:

- `/collect key value:RGAPI-…` — прислать свежий ключ, сбор стартует;
- `/collect status` — что делает коллектор сейчас;
- `/collect stop` — убрать ключ, сбор встанет после текущего матча.

Дальше всё само:

1. коллектор собирает параллельно по регионам;
2. **ключ протух (24 ч)** → база сохраняется, что успели — публикуется,
   в Discord приходит «⌛ Ключ истёк, пришли новый»;
3. присылаешь новый ключ → сбор продолжается с того же места
   (матчи и игроки дедуплицируются глобально);
4. круг пройден → база уезжает в release `data`, у пользователей обновляется.

### Память

Сбор I/O-bound (ждёт Riot API), RSS ~80–120 МБ. В compose стоит `mem_limit: 256m`,
чтобы коллектор не съел память у соседей по машине. Диск: база ~150 МБ и растёт.
