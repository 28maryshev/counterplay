# ТЗ: Discord-бот Counterplay (адаптировано под реальную инфраструктуру, 2026-07-13)

Самодостаточное техническое задание. Реализовать бота, задеплоить на сервер,
подключить к Discord-серверу Counterplay (инвайт: https://discord.gg/mhv3tGXYBT).
Язык общения бота с пользователями — **английский**. Комментарии в коде —
английские или русские, идентификаторы — английские.

> Отличия от первоначального черновика сведены в §0 — прочитать первым.

---

## 0. Что здесь адаптировано под реальность (сводка)

1. **Схема базы — другая.** Реальные таблицы: `base_wr`, `matchup`, `synergy`,
   `botlane_matchup`, `drafts`, `processed_matches` (см. §3.1 — это точная схема,
   сверять ничего не нужно). Никаких `champion_stats/matchup_stats/synergy_stats`.
2. **Чемпионы — числовые id** (Riot championId), не имена. Маппинг id→имя даёт
   Data Dragon; готовый словарь также лежит в `pipeline/champion_map.json`
   (`{"266":"Aatrox", ...}` — id → английский идентификатор DDragon).
3. **Нет колонок winrate/pickrate.** Везде `games`/`wins`; winrate = wins/games,
   pickrate вычисляется (см. §3.3).
4. **Есть разрез по эло** — `tier_bucket` (`master`/`emerald`/`gold`, позже
   `silver`). Бот по умолчанию агрегирует SUM по всем бакетам.
5. **`patch` — TEXT, сортировать численно!** `MAX(patch)` вернёт «16.9» вместо
   «16.13». Плюс в базе есть мусорные старые патчи с копеечными объёмами —
   фильтровать по объёму (§3.4).
6. **База НЕ живёт на сервере.** Она собирается на ПК владельца и публикуется
   снапшотом на GitHub Releases (тег `data`, ассеты `data.db` +
   `data-version.json`). Приложение скачивает её оттуда — бот делает так же (§10.2).
7. **На VPS нет node/pm2 — есть только Docker** (Compose: web + db сайта).
   Деплой бота — третьим compose-сервисом, `node:20-alpine`,
   `restart: unless-stopped`. RAM на сервере впритык (2 GB, свободно ~0.5 GB) —
   бот обязан быть лёгким (§10.1).
8. **Формула скоринга обновлена** под текущий движок приложения: чистые
   контр-дельты относительно собственной базы, темперирование по объёму,
   пер-ролевые веса прямой контры, окно из 3 патчей с весами 1.0/0.7/0.45 (§5.2).
9. **Пороги по games откалиброваны** под реальные объёмы: на пиковом патче
   (16.13) ~110 тыс. матчей; медианный чемпион-роль ~450 игр, топ ~15 тыс.;
   медианная пара матчапа ~60 игр, топ ~1.4 тыс. (§4.1, §6.2).
10. Числа в примерах поправлены (не бывает «105k games» у одного чемпиона —
    у нас 15k максимум).

---

## 1. Цель и контекст

**Counterplay** — десктопное оверлей-приложение для League of Legends
(counterplays.com): в реальном времени во время драфта рекомендует пики на основе
собственной базы статистики (винрейты, контр-матчапы, синергия), собираемой через
Riot MATCH-V5 (`pipeline/collect.py`) и агрегируемой по патчу/роли/эло.

**Бот** — контент-машина Discord-сервера Counterplay. Он читает **тот же снапшот
базы**, что скачивает приложение, и превращает его в ежедневный контент +
интерактивные функции. Бот — часть воронки: мобильные пользователи получают
ценность в Discord сразу, десктопную программу скачивают позже.

Четыре функции:
1. **Meta Radar** — ежедневный автопост аномалии меты в #meta-radar.
2. **Draft Duels** — ежедневная драфт-викторина с голосованием кнопками,
   вечерним вскрытием ответа, очками и недельным лидербордом в #draft-duels.
3. **Lab Verifier** — автопроверка находок пользователей в форум-канале
   #submit-finds по данным базы; подтверждённые кросспостятся в #hall-of-fame.
4. **Pick Coach** — слэш-команды `/pool`, `/counter`, `/matchup` (эфемерные,
   работают в любом канале и DM).

Каналы #meta-radar, #draft-duels, #submit-finds (форум), #hall-of-fame нужно
**создать на сервере** (сейчас их нет — сервер только что развёрнут). #bot-commands
опционален: ответы Pick Coach эфемерные и не мусорят.

---

## 2. Стек и общие требования

- **Node.js 20+**, **discord.js v14** (актуальную минорную версию сверить с докой).
- **better-sqlite3** для чтения снапшота основной базы и ведения собственной.
- **node-cron** для расписаний. Все cron — в **UTC**.
- Конфигурация — через `.env` (dotenv). Токен и ID каналов в репозиторий не коммитить.
- Снапшот основной базы — **строго read-only** для бота (`{ readonly: true }`).
  Все записи бота (дуэли, голоса, очки, лог радара) — в отдельный файл `bot.db`.
- Логирование: консоль (её собирает `docker logs`) + файл `logs/bot.log`,
  уровни info/warn/error. Логировать каждый автопост, каждую ошибку запроса к
  базе, каждый interaction-фейл, каждое обновление снапшота данных.
- Устойчивость: любая ошибка в одной функции не должна ронять процесс. Все
  cron-джобы и обработчики — в try/catch с логом. Если данных для поста нет —
  пост пропустить и залогировать warn, но не падать.
- Rate limits Discord: не постить чаще необходимого; discord.js сам ретраит 429.
- Riot API бот НЕ использует (ключ не нужен): только статические файлы Data
  Dragon и наш снапшот базы.

### 2.1 Структура проекта

```
counterplay-bot/
├── index.js                 # клиент, загрузка команд/событий, запуск cron
├── deploy-commands.js       # регистрация слэш-команд (отдельный запуск)
├── config.js                # чтение .env, валидация обязательных переменных
├── db/
│   ├── mainDb.js            # подключение к снапшоту (read-only) + горячая подмена (§10.2)
│   ├── botDb.js             # bot.db: схема + миграция при старте
│   └── dataLayer.js         # ЕДИНСТВЕННОЕ место с SQL к основной базе (§3)
├── lib/
│   ├── champions.js         # id ↔ имя (Data Dragon), алиасы, нормализация
│   ├── scoring.js           # движок скоринга для дуэлей (§5.2)
│   ├── embeds.js            # фабрики embed'ов (стиль в §2.3)
│   ├── dataSync.js          # скачивание data.db с GitHub Releases (§10.2)
│   └── logger.js
├── commands/
│   ├── pool.js
│   ├── counter.js
│   ├── matchup.js
│   └── admin.js             # ручные триггеры (§8)
├── cron/
│   ├── metaRadar.js         # ежедневно 10:00 UTC
│   ├── duelPost.js          # ежедневно 12:00 UTC
│   ├── duelReveal.js        # ежедневно 22:00 UTC
│   └── weeklyBoard.js       # воскресенье 20:00 UTC
├── events/
│   ├── interactionCreate.js # команды + кнопки
│   └── threadCreate.js      # Lab Verifier
├── data/                    # сюда скачивается data.db (volume в Docker)
├── logs/
├── Dockerfile               # node:20-alpine
├── docker-compose.yml       # сервис bot (или блок для добавления в компоуз сайта)
├── .env.example
└── package.json
```

### 2.2 `.env` (шаблон — положить как `.env.example`)

```
DISCORD_TOKEN=
CLIENT_ID=              # application id
GUILD_ID=               # id сервера
CH_META_RADAR=          # id канала #meta-radar
CH_DRAFT_DUELS=         # id канала #draft-duels
CH_SUBMIT_FINDS=        # id форум-канала #submit-finds
CH_HALL_OF_FAME=        # id канала #hall-of-fame
DATA_DB_URL=https://github.com/28maryshev/counterplay/releases/download/data/data.db
DATA_VERSION_URL=https://github.com/28maryshev/counterplay/releases/download/data/data-version.json
DATA_DIR=./data         # локальный кэш снапшота (data.db + data-version.json)
BOT_DB_PATH=./bot.db
ADMIN_USER_IDS=         # id админов через запятую (для /admin)
SITE_URL=https://counterplays.com
```

ID каналов владелец получает так: Discord → Настройки → Расширенные → Режим
разработчика → ПКМ по каналу → «Копировать ID». В README бота это описать.

### 2.3 Единый стиль сообщений

- Все посты — **embed** (не plain text), кроме реплик в тредах Lab Verifier.
- Цвета: зелёный `0x3FB55C` (позитив/sleeper/confirmed), красный `0xC0413B`
  (trap/not confirmed), синий `0x2E86C1` (counter/инфо), золото `0xC8AA6E`
  (дуэли, лидерборд, hall of fame).
- Иконки чемпионов: `https://ddragon.leagueoflegends.com/cdn/<ver>/img/champion/<Id>.png`
  (thumbnail embed'а), где `<Id>` — английский идентификатор из champion.json
  (`Aatrox`, `KSante`, `MonkeyKing`…). Версию DDragon тянуть при старте из
  `https://ddragon.leagueoflegends.com/api/versions.json` и кэшировать.
- Футер каждого автопоста: `From the Counterplay database • counterplays.com`
- Никаких упоминаний @everyone/@here.

---

## 3. Слой данных (dataLayer.js)

### 3.1 РЕАЛЬНАЯ схема основной базы (проверена 2026-07-13, сверять не нужно)

```sql
-- обработанные матчи (для дедупа в пайплайне; боту — только COUNT для /admin status)
processed_matches(match_id TEXT PRIMARY KEY)

-- базовый винрейт чемпиона на роли
base_wr(champion_id INTEGER, role TEXT, tier_bucket TEXT, patch TEXT,
        games INTEGER, wins INTEGER,
        PRIMARY KEY (champion_id, role, tier_bucket, patch))

-- матчап на одной линии (обе стороны пишутся зеркально)
matchup(champion_id INTEGER, role TEXT, vs_champion_id INTEGER,
        tier_bucket TEXT, patch TEXT, games INTEGER, wins INTEGER,
        PRIMARY KEY (champion_id, role, vs_champion_id, tier_bucket, patch))

-- синергия с союзником другой роли
synergy(champion_id INTEGER, role TEXT, ally_id INTEGER, ally_role TEXT,
        tier_bucket TEXT, patch TEXT, games INTEGER, wins INTEGER,
        PRIMARY KEY (champion_id, role, ally_id, ally_role, tier_bucket, patch))

-- кросс-ролевые матчапы (бот 2v2, джангл↔линии) — для бота v1 НЕ используется
botlane_matchup(champion_id INTEGER, role TEXT, vs_champion_id INTEGER,
                vs_role TEXT, tier_bucket TEXT, patch TEXT,
                games INTEGER, wins INTEGER, PRIMARY KEY (...))

-- полные драфты (сырьё калибровки) — для бота НЕ используется
drafts(match_id TEXT PRIMARY KEY, tier_bucket TEXT, patch TEXT,
       win_team INTEGER, slots TEXT)
```

Факты:
- `champion_id` — числовой Riot id. Имя/иконка — через Data Dragon
  (`champion.json`: у каждого чемпиона `key` = id строкой, `id` = англ.
  идентификатор, `name` = отображаемое имя). Дубликат-словарь id→идентификатор
  есть в `pipeline/champion_map.json`.
- Роли: `top | jungle | mid | adc | support` — как в черновике, маппинг не нужен.
- `tier_bucket`: сейчас в базе `master` (~104k матчей), `emerald` (~68k),
  `gold` (~49k); `silver` появится. **По умолчанию агрегировать SUM по всем.**
- `winrate = 100.0*wins/games`, отдельной колонки нет.
- **pickrate вычисляется**: игр у чемпиона-роли / всего матчей патча, где
  `всего матчей = SUM(base_wr.games за патч) / 10` (в каждом матче 10 участников).
- Объёмы (патч 16.13, ~110k матчей — для калибровки порогов): пар чемпион-роль с
  ≥30 играми — 588; медиана ~450 игр, p90 ~5 600, максимум ~15 200 (пикрейт
  ~13.8%). Пары матчапов: медиана ~60 игр, p90 ~200, максимум ~1 350.
  База растёт непрерывно (collect.py), поэтому пороги в §4/§6 со временем можно
  ужесточать.

### 3.2 Обязательный интерфейс dataLayer

Все функции работают с id чемпионов (число); конверсию имя↔id делает
`lib/champions.js`. Все агрегаты — SUM по tier_bucket.

```js
getCurrentPatch()                        // строка последнего патча (§3.4!)
getPreviousPatch()                       // предыдущий содержательный патч
getTotalMatches(patch)                   // SUM(base_wr.games)/10 по патчу
getChampionStats(patch, {role})          // [{championId, role, games, wins, winrate, pickrate}]
getChampionStat(patch, championId, role) // одна строка или null
getMatchups(patch, championId, role)     // все матчапы чемпиона
getMatchup(patch, aId, bId, role)        // {games, wins, winrate} a против b, или null
getCountersAgainst(patch, enemyId, role, {minGames}) // кто бьёт enemy, DESC по adjWR
getSynergies(patch, championId)          // [{allyId, allyRole, games, wins}]
getTopPopular(patch, role, n)            // топ-n по games (== по pickrate)
getPatchWindow()                         // последние 3 патча + веса [1.0, 0.7, 0.45] (§3.4)
```

Для скоринга дуэлей и Pick Coach запросы делать по **окну из 3 патчей** с
весами (как движок приложения): в SQL —
`SUM(games * w)`, `SUM(wins * w)`, где
`w = CASE patch WHEN @p1 THEN 1.0 WHEN @p2 THEN 0.7 ELSE 0.45 END`.
Для Meta Radar и Lab Verifier — только текущий патч (утверждения «на этом патче»).

### 3.3 Сглаживание малых выборок (то же, что в приложении)

- Сглаженный WR: `adj = (wins + k/2) / (games + k)`, `k = 50`.
- Темперирование дельт по объёму: вклад умножается на `games/(games + conf)`
  (conf: база 250, матчап 50, синергия 60) — см. §5.2.
- Строки с `games < 50` в аномалиях и вердиктах не участвуют (кроме явного
  статуса «small sample» в Lab Verifier).

### 3.4 Выбор патчей — ОБЯЗАТЕЛЬНО числно и с фильтром объёма

`patch` — TEXT («16.9» > «16.13» лексикографически!). Правило:
1. Взять `SELECT patch, SUM(games) FROM base_wr GROUP BY patch`.
2. Оставить патчи с объёмом ≥ 5% от максимального (отсекает мусор вроде
   14.22/15.5 с единичными матчами из длинных историй).
3. Отсортировать по числовым компонентам (`'16.13' → [16,13]`).
4. `getCurrentPatch()` = последний; `getPreviousPatch()` = предпоследний;
   `getPatchWindow()` = последние 3 (если меньше — сколько есть).

---

## 4. Функция 1: Meta Radar

**Cron:** ежедневно `0 10 * * *` (UTC) → пост в `CH_META_RADAR`.

### 4.1 Типы аномалий и критерии (на текущем патче; adjWR — §3.3)

| Тип | Критерий | Цвет |
|---|---|---|
| `sleeper` | adjWR ≥ 52% AND pickrate < 3% AND games ≥ 400 | зелёный |
| `rising` | adjWR(тек. патч) − adjWR(пред. патч) ≥ +1.5 п.п., games ≥ 400 на обоих | зелёный |
| `trap` | pickrate ≥ 7% AND adjWR < 48.5% AND games ≥ 1000 | красный |
| `counter_surprise` | матчап: adjWR ≥ 56% против оппонента с pickrate ≥ 5%, при этом pickrate самого контрпика < 2%, games матчапа ≥ 100 | синий |

Пороги откалиброваны под ~110k матчей на патч (§3.1); при росте базы можно поднять.

**Волюм-гвард:** если у текущего патча < 20k матчей (первые дни патча) —
использовать предыдущий патч как «текущий» для radar (кроме режима Seismograph).

### 4.2 Алгоритм

1. Определить тип на сегодня **ротацией** по дню: хранить в `bot.db` таблицу
   `radar_log(date, type, champion_id, role)`; следующий тип = следующий по циклу
   sleeper → rising → trap → counter_surprise после последнего опубликованного.
2. Получить кандидатов по критерию типа. **Исключить** чемпионов, публиковавшихся
   в радаре за последние 14 дней (по radar_log).
3. Если по типу кандидатов нет — перейти к следующему типу по циклу (максимум
   полный круг; если пусто везде — warn в лог, поста нет).
4. Выбрать топ-1 кандидата (по величине аномалии: разрыв WR / дельта / глубина трапа).
5. Сформировать embed:
   - Title: `📡 META RADAR — Patch <patch>` + строка типа
     (`🔥 SLEEPER OP: <Champ> <Role>` / `📈 RISING` / `⚠️ TRAP PICK` / `🧊 HIDDEN COUNTER`)
   - Description: 2–4 строки конкретики с цифрами (WR, pickrate, games; для counter —
     против кого и WR матчапа; для rising — дельта к прошлому патчу).
   - Thumbnail: иконка чемпиона.
   - Footer стандартный.
   Тексты генерировать шаблонно из данных (без LLM), короткими фразами.
6. Записать пост в `radar_log`.

### 4.3 Режим «Seismograph» (день патча)

Если `getCurrentPatch()` изменился со вчерашнего значения (хранить последнее
виденное в bot.db) — вместо обычной аномалии в этот день и на следующий постить:
- Title: `🌋 SEISMOGRAPH — Patch <new> vs <old>`
- Топ-5 чемпионов по |дельте adjWR| между патчами (games ≥ 200 на обоих —
  на свежем патче данных ещё мало), формат: `Champ (Role): 49.1% → 52.3% (+3.2)`.
- Через 2 дня вернуться к обычной ротации.

---

## 5. Функция 2: Draft Duels

### 5.1 Схема в bot.db

```sql
CREATE TABLE IF NOT EXISTS duels (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  date TEXT UNIQUE,             -- YYYY-MM-DD (UTC)
  role TEXT,
  ally_json TEXT,               -- {"top":86,"jungle":254,...} champion_id, без слота игрока
  enemy_json TEXT,              -- все 5 врагов (по ролям, id)
  options_json TEXT,            -- [25,117,12,111] id в порядке A-D
  correct INTEGER,              -- champion_id правильного ответа
  explanation_json TEXT,        -- разбивка скоринга топ-1 и runner-up (для reveal)
  message_id TEXT,              -- id сообщения с задачей
  revealed INTEGER DEFAULT 0
);

CREATE TABLE IF NOT EXISTS duel_votes (
  duel_id INTEGER,
  user_id TEXT,
  choice TEXT,                  -- 'A'|'B'|'C'|'D'
  voted_at TEXT,
  PRIMARY KEY (duel_id, user_id)
);

CREATE TABLE IF NOT EXISTS duel_scores (
  user_id TEXT,
  week TEXT,                    -- ISO-неделя, напр. '2026-W29'
  points INTEGER DEFAULT 0,
  correct INTEGER DEFAULT 0,
  total INTEGER DEFAULT 0,
  PRIMARY KEY (user_id, week)
);
```

### 5.2 Движок скоринга (lib/scoring.js) — актуальная формула приложения

Данные брать через окно 3 патчей с весами (§3.2), SUM по бакетам.
`delta(g, w, k) = ((w + k/2)/(g + k) − 0.5) * 100` — сглаженная дельта в п.п.

```
для кандидата C на роль R при известных врагах E[] (по ролям) и союзниках A[]:

rawBase = delta(bg, bw, 50)                        // база без темпера
base    = rawBase * bg/(bg + 250)                  // темперированная база

// прямой оппонент (враг на роли R); нет строки или games<50 → 0
direct  = (delta(mg, mw, 50) − rawBase) * mg/(mg + 50)   // ЧИСТАЯ контра

// остальные известные враги — то же самое, усреднить
others  = avg( (delta(g_e, w_e, 50) − rawBase) * g_e/(g_e + 50) )

// союзники — по synergy, усреднить; нет данных → 0
syn     = avg( (delta(g_a, w_a, 50) − rawBase) * g_a/(g_a + 60) )

W_DIRECT(R): top 2.5 · jungle 2.2 · mid 2.0 · support 2.0 · adc 1.8

score = 1.0*base + W_DIRECT(R)*direct + 0.8*others + 1.2*syn
```

Ключевое отличие от старого черновика: контра — это `матчап МИНУС собственная
база` (иначе просто сильные мета-чемпионы всплывали бы как «контрпики»), и все
вклады темперируются объёмом выборки.

### 5.3 Генерация задачи — cron `0 12 * * *` (UTC)

1. Роль игрока: ротация top→jungle→mid→adc→support по дням.
2. Союзники: 4 чемпиона на остальные роли, случайно из `getTopPopular(patch, role, 15)`
   каждой роли. Враги: 5 аналогично. Без дубликатов между всеми десятью.
3. Кандидаты: пул роли игрока = `getTopPopular(patch, role, 25)` минус занятые.
   Прогнать всех через scoring → отсортировать.
4. Контроль качества: если `score(top1) − score(top2) < 3` — перегенерировать драфт
   (до 10 попыток; дальше публиковать как есть).
5. Варианты: top1 (correct) + два из мест 2–6 + один из мест 15–25 («ловушка»).
   Перемешать → буквы A–D.
6. Embed задачи:
   - Title: `🧩 DRAFT DUEL — <дата>`
   - Поля: Your role; Your team (роль: чемпион, 4 строки); Enemy team (5 строк);
     `Your lane opponent is **X**`.
   - 4 варианта строкой: `🅰 Morgana  🅱 Lulu  🅲 Alistar  🅳 Nautilus`
   - Внизу: `Vote below! Answer drops at 22:00 UTC`
7. Кнопки (ActionRow, 4 ButtonBuilder): label `A`/`B`/`C`/`D`,
   customId `duel:<duelId>:<letter>`, style Secondary.
8. Обработка нажатия (events/interactionCreate.js):
   - upsert в duel_votes (голос можно менять до reveal);
   - ответ ephemeral: `Vote recorded: **C — Alistar**. You can change it until the reveal.`;
   - если дуэль уже revealed — ephemeral `Voting is closed for this duel.`

### 5.4 Вскрытие — cron `0 22 * * *` (UTC)

1. Взять сегодняшнюю дуэль (revealed=0). Пометить revealed=1 (голосование закрыто).
2. Подсчитать распределение голосов по вариантам (в %).
3. Начислить очки за неделю (`duel_scores`, week = ISO-неделя даты дуэли):
   correct → +3 и correct+1; runner-up (2-е место по score) → +1; всем — total+1.
4. Embed ответа:
   - Title: `🧩 DUEL — ANSWER`
   - `The engine says: **<letter> <Champion>** (score +<X>)`
   - Why: 3–4 строки из разбивки (direct-матчап с цифрой, синергия, база).
   - Runner-up: имя + score + одна строка.
   - Vote split: `A 12% · B 48% · C 31% · D 9%` + сколько всего голосов.
   - Reply на сообщение задачи (message reference).

### 5.5 Недельный лидерборд — cron `0 20 * * 0` (воскресенье, UTC)

Embed в #draft-duels: топ-10 за текущую ISO-неделю
(`points DESC, correct DESC`): `#1 @user — 18 pts (6/7 correct)`.
Упоминания юзеров — через `<@id>`, но с `allowedMentions: { parse: [] }`
(показ имени без пинга). Внизу: `New week starts now. Play daily to climb!`

---

## 6. Функция 3: Lab Verifier

**Событие:** `threadCreate` в форум-канале `CH_SUBMIT_FINDS`
(в discord.js пост форума = новый thread; стартовое сообщение — fetch первого
сообщения треда). Требуется privileged intent **Message Content** (§9).

### 6.1 Парсинг поста

1. Текст = заголовок треда + стартовое сообщение.
2. Распознавание чемпионов: справочник из Data Dragon (`lib/champions.js`,
   матчить и по `name`, и по `id`-идентификатору) + таблица алиасов (минимум:
   MF→Miss Fortune, Kaisa/Kai sa→Kai'Sa, ASol→Aurelion Sol, J4→Jarvan IV,
   TF→Twisted Fate, Yi→Master Yi, Kass→Kassadin, Cait→Caitlyn, Naut→Nautilus,
   Sera→Seraphine, Mundo→Dr. Mundo, Ori→Orianna, WW→Warwick, Wukong→MonkeyKing).
   Матчинг без регистра, без апострофов/пробелов/точек.
3. Роль: искать ключевые слова top/jungle/jgl/jg/mid/bot/adc/support/supp/sup.
   Если роли нет — взять роль с максимумом games у этого чемпиона на текущем патче.
4. Паттерн матчапа: `X vs Y | X into Y | X against Y` → режим matchup.

### 6.2 Вердикты (adjWR — §3.3; текущий патч)

Одиночный чемпион (champion+role):
- ✅ `CONFIRMED` — adjWR ≥ 51% и games ≥ 300
- ⚠️ `SMALL SAMPLE` — adjWR ≥ 51% и 50 ≤ games < 300
- ❌ `NOT CONFIRMED` — adjWR < 51% (games ≥ 50)
- ❓ `NOT ENOUGH DATA` — games < 50 или чемпион не распознан

Матчап (a vs b): те же пороги по adjWR матчапа, для CONFIRMED games ≥ 100
(медианная пара в базе ~60 игр — 100+ уже верхний квартиль).

Ответ бота — сообщение в тред: вердикт + цифры
(`Seraphine ADC — 54.1% WR over 1,240 games, patch 16.13`) + одна строка пояснения.
Если распознать не удалось: вежливое `Couldn't identify the champion — try
"Champion + role", e.g. "Seraphine ADC".`

### 6.3 Кросспост в Hall of Fame

При `CONFIRMED`: embed в `CH_HALL_OF_FAME`:
- Title: `🏆 VERIFIED FIND`
- `<Champion> (<Role>) — discovered by @author`
- Цифры + ссылка на тред. Автору в тред: `Verified! Posted to #hall-of-fame 🏆`.
Дедупликация: не кросспостить, если тот же champion+role уже в hall of fame в этом
патче (таблица `hof_log(patch, champion_id, role, thread_id)` в bot.db).

---

## 7. Функция 4: Pick Coach (слэш-команды)

Все ответы — **ephemeral** embed (видит только автор). Работают в любом канале и в DM.
Для параметров-чемпионов сделать **autocomplete** по справочнику имён.
Данные — окно 3 патчей (§3.2), в футере указывать патчи окна.

### `/pool role:<role> champions:<строка через запятую>` (до 5 чемпионов)
1. Для каждого чемпиона: топ-3 хардкаунтера (adjWR ≤ 46%, games ≥ 80) и
   топ-3 лучших матчапа (adjWR ≥ 54%, games ≥ 80).
2. **Ban suggestions**: оппоненты с avg adjWR < 47% против ≥ 2 чемпионов пула.
3. **Pocket pick**: из топ-20 популярных роли выбрать чемпиона, который лучше всего
   бьёт (avg adjWR) найденных хардкаунтеров пула — «добавь в пул, чтобы закрыть дыры».
Если по порогу 80 игр пусто — повторить с 50 и пометить строки `(small sample)`.

### `/counter champion:<name> [role]`
Топ-5 контрпиков против чемпиона: `getCountersAgainst` (games ≥ 80), с WR и games.
Ранжировать по чистой контр-дельте (adjWR матчапа − adjWR базы контрпика), как в
приложении, — а не по сырому WR.

### `/matchup a:<name> b:<name> [role]`
WR обеих сторон, games, вердикт-строка
(`Morgana is favored into Leona (56.3% over 320 games)`).

Ошибки (нет данных/чемпион не найден) — вежливый ephemeral ответ с подсказкой формата.

---

## 8. Админ-команды (`/admin`, доступ только ADMIN_USER_IDS)

- `/admin radar-now` — немедленно выполнить пост Meta Radar (для теста).
- `/admin duel-now` — немедленно опубликовать дуэль.
- `/admin reveal-now` — немедленно вскрыть текущую дуэль.
- `/admin board-now` — опубликовать лидерборд.
- `/admin data-sync` — принудительно проверить/скачать свежий снапшот базы (§10.2).
- `/admin status` — ephemeral: текущий патч, версия снапшота (hash из
  data-version.json) и когда скачан, кол-во матчей в базе (processed_matches),
  дата последнего радара, id активной дуэли, аптайм.
Проверку прав делать по user id; для остальных — ephemeral `No access`.

---

## 9. Настройка Discord Developer Portal (описать в README)

1. discord.com/developers → New Application → Bot.
2. **Privileged Gateway Intents:** включить **Message Content Intent**
   (нужен Lab Verifier). Server Members не нужен.
3. Intents в коде: `Guilds`, `GuildMessages`, `MessageContent`.
4. Invite URL (OAuth2 → URL Generator): scopes `bot`, `applications.commands`;
   permissions: View Channels, Send Messages, Send Messages in Threads,
   Embed Links, Attach Files, Read Message History, Use External Emojis.
   Права Administrator НЕ давать.
5. После инвайта: у бота должно быть право писать в read-only каналах
   (#meta-radar, #draft-duels, #hall-of-fame) — на сервере это решается
   per-channel разрешением для роли бота (описать шаг в README: Настройки
   канала → Права доступа → добавить роль бота → разрешить Send Messages).
6. Регистрация слэш-команд: `node deploy-commands.js` — guild-команды на
   GUILD_ID (мгновенно применяются). Глобальные не использовать.

---

## 10. Деплой (РЕАЛЬНАЯ инфраструктура)

### 10.1 Сервер

- VPS: AWS Lightsail Ubuntu, IP 3.68.217.116, **2 GB RAM (свободно ~0.5 GB)**.
- На хосте НЕТ node и pm2. Есть **Docker + Compose** (там же крутится сайт:
  сервисы web + db в `~/counterplay-site`).
- Бот деплоится контейнером: `~/counterplay-bot` со своим `docker-compose.yml`
  (сервис `bot`, образ на базе `node:20-alpine`, `restart: unless-stopped` —
  переживает ребут, т.к. Docker-демон в автозапуске).
- Volumes: `./data` (снапшот базы), `./bot.db`, `./logs`, `.env`.
- Память: держать процесс скромным (без лишних кэшей); ориентир < 150 MB RSS.
- Логи: `docker logs counterplay-bot` + файл `logs/bot.log`. Ротация файла —
  простая встроенная (переименование при > 5 MB) или logrotate на хосте.

### 10.2 Данные: снапшот с GitHub Releases (lib/dataSync.js)

Основная база живёт на ПК владельца; после сессии сбора он публикует снапшот
скриптом `build/publish-data.ps1` → GitHub release с тегом `data`
(ассеты `data.db` и `data-version.json`; `version` = hash содержимого).
Приложение обновляется по этому же механизму. Бот делает то же самое:

1. При старте: если в `DATA_DIR` нет `data.db` — скачать оба файла, иначе открыть кэш.
2. Каждый час (node-cron): GET `DATA_VERSION_URL`; если `version` отличается от
   локального — скачать `data.db` во временный файл, затем: закрыть текущее
   better-sqlite3 соединение → атомарно заменить файл (rename) → открыть заново
   (`{ readonly: true }`). Лог info с новым патчем/хэшем.
3. Ошибки сети при проверке/скачивании — warn в лог, продолжаем на старом кэше.
   Если кэша нет вовсе (первый запуск без сети) — бот работает, но dataLayer
   возвращает «нет данных», команды отвечают `Data temporarily unavailable`.
4. GitHub отдаёт ассеты через redirect — HTTP-клиент должен ходить по 302.

### 10.3 Порядок деплоя

1. Локально собрать и проверить (можно и на Windows — better-sqlite3 ставится).
2. `scp -r` папку бота (без node_modules) в `~/counterplay-bot`, `.env` — отдельно.
3. `ssh 3.68.217.116 'cd counterplay-bot && docker compose up -d --build'`.
4. `node deploy-commands.js` — можно выполнить один раз локально (регистрирует
   команды через REST, серверу не нужен) или внутри контейнера.
5. Прогнать чеклист §11 админ-командами.

---

## 11. Критерии приёмки (чеклист)

- [ ] `node deploy-commands.js` регистрирует `/pool`, `/counter`, `/matchup`, `/admin`.
- [ ] Бот стартует, скачивает/открывает снапшот, пишет в лог текущий патч (16.x,
      НЕ «16.9» при наличии 16.13 — числовая сортировка работает).
- [ ] `/admin status` показывает патч, hash снапшота, кол-во матчей.
- [ ] `/admin radar-now` публикует корректный embed в #meta-radar с реальными цифрами;
      повторный вызов не публикует того же чемпиона (ротация типов работает).
- [ ] `/admin duel-now` публикует дуэль с 4 рабочими кнопками; голос записывается,
      повторный клик меняет голос; ephemeral-подтверждение приходит.
- [ ] `/admin reveal-now` вскрывает: правильный ответ, проценты голосов, очки
      начислены в duel_scores; кнопки после вскрытия отвечают «closed».
- [ ] Пост в #submit-finds вида «Seraphine ADC is broken» получает ответ-вердикт
      с цифрами; CONFIRMED-находка появляется в #hall-of-fame с ником автора.
- [ ] `/pool role:support champions:Morgana, Lulu` возвращает каунтеры/баны/pocket pick.
- [ ] `/admin data-sync` при новой публикации базы подхватывает её без рестарта.
- [ ] Недоступность снапшота (битый DATA_DB_URL) не роняет процесс: работа
      продолжается на кэше; без кэша — команды отвечают `Data temporarily
      unavailable`, cron логирует ошибку.
- [ ] Контейнер переживает рестарт сервера (`restart: unless-stopped`).

---

## 12. Порядок реализации (инкрементально)

1. Каркас: config, логгер, dataSync (скачивание снапшота), подключение баз,
   `lib/champions.js`, `/admin status`.
2. dataLayer по схеме §3.1 (она уже сверена с реальной базой — просто реализовать),
   включая числовой выбор патчей и окно 3 патчей.
3. Pick Coach (`/counter` → `/matchup` → `/pool`) — быстрые победы, проверяют dataLayer.
4. Meta Radar + `/admin radar-now`.
5. Draft Duels (scoring → генерация → кнопки → reveal → очки → лидерборд).
6. Lab Verifier + hall of fame.
7. Деплой на VPS (Docker), прогон чеклиста §11.

На каждом шаге — рабочее состояние; не переходить дальше, пока шаг не проверен
админ-командой или реальным сообщением на сервере.
