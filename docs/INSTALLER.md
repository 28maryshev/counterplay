# Установщик и обновления Counterplay

Дистрибуция через **Velopack**: один `Setup.exe`, который ставит приложение со
всеми зависимостями (среда .NET вшита — ставить ничего отдельно не нужно), и
**автообновление из GitHub Releases при каждом запуске**.

## Что внутри

- **Зависимости**: публикуется self-contained (`--self-contained`), поэтому .NET 8
  и нативные библиотеки (SQLite) уже в комплекте. Пользователю ничего ставить не надо.
- **Обновления**: при каждом старте приложение проверяет последний релиз на
  `github.com/28maryshev/counterplay`. Если есть новее — тихо скачивает и
  перезапускается в новую версию ([Program.cs](../Program.cs) → `CheckForUpdatesAsync`).
  В dev-сборке (запуск через `dotnet run`) проверка пропускается.
- **База данных**: `data.db` (≈13 МБ) **не входит** в установщик и **обновляется
  централизованно**, отдельно от версий приложения. Лежит в GitHub-релизе с тегом
  `data` (ассеты `data.db` + `data-version.json`). Приложение при **каждом запуске**
  сверяет `data-version.json` с локальной версией и, если на сервере новее, тихо
  подкачивает свежую базу в `%APPDATA%\Counterplay\data.db` ([DataDb.cs](../DataDb.cs)).
  Пользователь ничего не делает — мета держится актуальной.

## Обновление данных под новый патч

```powershell
# 1. собрать матчи нового патча (нужен Riot API-ключ)
python pipeline\collect.py --key RGAPI-xxxx --tier emerald
# 2. выложить свежую базу в дата-релиз (нужен gh)
.\build\publish-data.ps1
```
`publish-data.ps1` считает версию = последний патч в БД, пишет `data-version.json`
и заливает оба ассета в релиз `data` (`--clobber`). У всех пользователей при
следующем запуске подтянется новая база. Версию приложения для этого менять не нужно.

## Как выпустить релиз

Нужен токен GitHub с правом на релизы (`repo`/`contents:write`).

```powershell
# собрать установщик локально (без публикации)
.\build\release.ps1 -Version 1.0.1

# собрать и сразу выложить релиз на GitHub (+ загрузить data.db ассетом)
.\build\release.ps1 -Version 1.0.1 -Upload -Token <github_token>
```

Скрипт:
1. ставит `vpk` (Velopack CLI), если его нет;
2. `dotnet publish` self-contained в `.\publish`;
3. `vpk pack` → `.\Releases\Counterplay-win-Setup.exe` + пакеты обновления;
4. при `-Upload` — `vpk upload github` (создаёт релиз с тегом `vX.Y.Z`) и через
   `gh` заливает `pipeline\data.db` ассетом в тот же релиз.

### Важно про data.db

Приложение качает базу по адресу
`https://github.com/28maryshev/counterplay/releases/latest/download/data.db`.
Поэтому в **каждом** релизе должен быть ассет `data.db`. При `-Upload` со
установленным `gh` это происходит автоматически; иначе добавь файл в релиз вручную.

## Обновление версии

Перед релизом подними `<Version>` в [Counterplay.csproj](../Counterplay.csproj)
(или передавай `-Version` в скрипт — он задаёт версию пакета Velopack).
Velopack сравнивает версии и обновляет только на бо́льшую.

## Где что лежит у пользователя

- Приложение: `%LOCALAPPDATA%\Counterplay` (Velopack, папка `current`).
- База: `%APPDATA%\Counterplay\data.db` (скачивается один раз).
- Ярлык в меню «Пуск» создаёт установщик.
