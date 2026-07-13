# Counterplay Discord Bot

Контент-машина Discord-сервера Counterplay. Читает тот же снапшот базы статистики,
что и десктопное приложение (GitHub release `data`), и превращает его в:

- **Meta Radar** — ежедневная аномалия меты в #meta-radar (10:00 UTC);
  в день выхода патча — режим Seismograph (крупнейшие сдвиги WR).
- **Draft Duels** — ежедневная драфт-викторина в #draft-duels (12:00 UTC),
  вскрытие в 22:00 UTC, недельный лидерборд в воскресенье 20:00 UTC.
- **Lab Verifier** — автопроверка находок в форуме #submit-finds;
  подтверждённые — в #hall-of-fame.
- **Pick Coach** — `/pool`, `/counter`, `/matchup` (эфемерные, работают везде).
- `/admin` — ручные триггеры и статус (только для ADMIN_USER_IDS).

## Настройка Discord (один раз)

1. https://discord.com/developers → **New Application** → вкладка **Bot**.
2. **Privileged Gateway Intents**: включить **Message Content Intent**
   (нужен Lab Verifier). Server Members не нужен.
3. Скопировать **Bot Token** (`DISCORD_TOKEN`) и **Application ID** (`CLIENT_ID`).
4. **OAuth2 → URL Generator**: scopes `bot` + `applications.commands`;
   permissions: View Channels, Send Messages, Send Messages in Threads,
   Embed Links, Attach Files, Read Message History, Use External Emojis.
   Administrator НЕ давать. Открыть сгенерированный URL → пригласить на сервер.
5. Создать каналы: `#meta-radar`, `#draft-duels`, `#hall-of-fame` (обычные,
   для читателей read-only) и `#submit-finds` (**форум**). В каждом read-only
   канале: Настройки канала → Права доступа → добавить роль бота → разрешить
   **Send Messages** (в форуме — Send Messages in Threads).
6. Включить у себя Режим разработчика (Настройки → Расширенные) и скопировать
   ID сервера и каналов (ПКМ → «Копировать ID») в `.env`.

## Запуск

```bash
cp .env.example .env   # заполнить
npm ci
node deploy-commands.js   # регистрация слэш-команд (один раз и после изменений команд)
node index.js
```

При первом старте бот сам скачает снапшот базы (~140 MB) с GitHub Releases в
`./data/` и дальше раз в час проверяет обновления (по хэшу в data-version.json),
подменяя файл на лету. Если данных временно нет, команды вежливо отвечают
«Data temporarily unavailable», процесс не падает.

## Деплой на VPS (Docker)

На сервере Counterplay нет node/pm2 — бот живёт контейнером:

```bash
# локально: копируем без node_modules
scp -r bot 3.68.217.116:counterplay-bot && scp bot/.env 3.68.217.116:counterplay-bot/.env
ssh 3.68.217.116 'cd counterplay-bot && docker compose up -d --build'
ssh 3.68.217.116 'docker logs -f counterplay-bot'   # проверить старт
```

`restart: unless-stopped` — контейнер переживает ребут сервера.
`node deploy-commands.js` можно выполнять локально: он ходит только в Discord REST.

## Данные

- Снапшот: `DATA_DB_URL` (read-only, better-sqlite3). Все записи бота — в
  `./data/bot.db` (дуэли, голоса, очки, лог радара, hall of fame).
- Если каких-то данных нет (мало игр), функции деградируют мягко: пороги с
  фолбэком и пометкой *small sample*, синергия без данных = 0.
- Патчи сортируются численно, мусорные (< 5% объёма) отбрасываются; первые дни
  нового патча радар/верификатор работают по предыдущему патчу (volume guard).
