# Бэкапы и восстановление

10 сентября 2026 аккаунт AWS закрыли, и два сервера исчезли вместе с данными.
Копии были — но лежали на тех же машинах. Отсюда единственное правило этого
каталога: **копия ценна только там, где её не достанет та же авария**.

Всё уезжает в **Cloudflare R2** — другой провайдер, другой аккаунт, бесплатные
10 ГБ.

## Что и откуда копируется

| Что | Откуда | Почему это важно |
|---|---|---|
| `postgres.dump.gz` | сервер сайта | аналитика, комментарии к гайдам, установки — восстановить неоткуда |
| `stats.tar.gz` | сервер сайта | готовые JSON рун и сборок, которые отдаёт сайт |
| `data.db.gz` | сервер коллектора | **самое ценное**: таблицы рун, предметов, кейстоунов. В публикуемой на GitHub базе их нет |
| `bot.db.gz` | сервер коллектора | состояние Discord-бота |
| `env.enc` | оба | настройки с паролями, шифруются `BACKUP_PASSPHRASE` |

Не копируем то, что и так живёт в двух местах: исходный код (git + GitHub) и
публикуемую базу движка (релиз `data`).

Расписание: раз в сутки, по воскресеньям копия дублируется в недельную папку.
Храним 14 суточных и 8 недельных копий сайта, 5 и 4 — коллектора (его база
весит сотни мегабайт).

## Установка на сервере

```bash
scp ops/backup-site.sh ubuntu@СЕРВЕР:~/           # или backup-collector.sh
scp ops/setup-backup.sh ubuntu@СЕРВЕР:~/
ssh ubuntu@СЕРВЕР
R2_ACCOUNT_ID=... R2_ACCESS_KEY=... R2_SECRET_KEY=... \
BACKUP_PASSPHRASE=... DISCORD_WEBHOOK=... \
  bash setup-backup.sh site                       # или collector
```

Скрипт поставит rclone, пропишет доступ, заведёт задачу в cron и сразу сделает
первую копию. Если доступа к хранилищу нет — остановится до того, как заведёт
расписание, а не через месяц молчания.

## Как понять, что бэкапы живы

- в Discord каждый день падает «💾 Бэкап … готов» с занятым объёмом;
- на сервере: `tail -20 ~/backup.log`;
- список копий: `rclone lsf r2:counterplay-backups/collector/daily/`

**Молчание — тоже сигнал.** Если сообщений нет второй день, значит не работает
сам бэкап, а не «ничего не менялось».

## Восстановление с нуля

Дальше — на чистой Ubuntu 24.04 с docker.

### Сайт

```bash
# 1. Код (GitHub) и настройки (из копии)
rclone copy r2:counterplay-backups/site/daily/ПОСЛЕДНЯЯ_ДАТА ./restore
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 \
  -in restore/env.enc -out counterplay-site/.env -pass pass:ПАРОЛЬ

# 2. Поднять контейнеры (база создастся пустой)
cd counterplay-site && docker compose up -d --build

# 3. Залить данные
gunzip -c ../restore/postgres.dump.gz > /tmp/pg.dump
docker exec -i counterplay-site-db-1 pg_restore -U counterplay -d counterplay \
  --clean --if-exists < /tmp/pg.dump

# 4. Вернуть руны
tar xzf ../restore/stats.tar.gz -C .

# 5. Проверить и переключить домен
sh scripts/check-admin.sh          # админка закрыта?
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1/ -H 'Host: counterplays.com'
```

Затем в Cloudflare → DNS заменить A-запись на новый IP и сделать Purge Everything.

### Коллектор и бот

```bash
rclone copy r2:counterplay-backups/collector/daily/ПОСЛЕДНЯЯ_ДАТА ./restore
openssl enc -d -aes-256-cbc -pbkdf2 -iter 200000 \
  -in restore/env.enc -out env.tar.gz -pass pass:ПАРОЛЬ
tar xzf env.tar.gz -C ~

scp -r pipeline СЕРВЕР:counterplay-collector && scp -r bot СЕРВЕР:counterplay-bot
gunzip -c restore/data.db.gz > ~/counterplay-collector/data/data.db
gunzip -c restore/bot.db.gz  > ~/counterplay-bot/data/bot.db

cd ~/counterplay-collector && sudo docker compose up -d
cd ~/counterplay-bot && sudo docker compose run --rm --no-deps \
  --entrypoint node bot deploy-commands.js && sudo docker compose up -d
```

Дальше прислать свежий ключ Riot командой `/collect key value:RGAPI-…`.

### Если копии настроек нет

`env.enc` расшифровывается только паролем `BACKUP_PASSPHRASE`. Потеряли пароль —
данные всё равно восстановятся, а настройки придётся собрать заново: пароли базы
и админки задаются любые новые, SMTP-пароль перевыпускается в Gmail, а вот
**секрет телеметрии обязан совпасть с тем, что вшит в released-сборки** — он
лежит в `build/telemetry.secret` на рабочей машине.

## Чего эта схема НЕ закрывает

- Рабочая машина. Репозиторий сайта (`C:\Indexcounterplay`) живёт локально и на
  сервере; удалённого репозитория у него нет.
- Сами ключи от серверов (`~/.ssh/*.pem`) — их копия только у тебя.
