# Публикует статистику рун/билдов на сервер (раздаётся через counterplays.com).
#
#   .\build\publish-runes.ps1
#
# Выгружает JSON из pipeline\data.db и копирует в том сервера (~/counterplay-site/stats).
# Пересборка контейнера НЕ нужна: это том, а не образ.
#
# Приложение читает манифест и показывает панель рун только для тех связок
# чемпион+роль, что есть в выгрузке — фичи включаются сами по мере накопления данных.

param(
  [string]$Server = "3.68.217.116",
  [string]$Db     = "pipeline/data.db"
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not (Test-Path $Db)) { throw "База не найдена: $Db" }

# 1. Выгрузка в статику
$dist = "pipeline/dist/stats"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
py -3.12 pipeline/export_runes.py --db $Db --out $dist
if ($LASTEXITCODE -ne 0) { throw "экспорт не удался" }

$files = @(Get-ChildItem "$dist/v1" -Recurse -Filter *.json)
if ($files.Count -le 1) {
  Write-Host "Данных о рунах ещё нет — публиковать нечего. Запусти collect.py." -ForegroundColor Yellow
  exit 0
}

# 2. Копирование на сервер (в том, который смонтирован в контейнер)
Write-Host "Копирую $($files.Count) файлов на $Server..." -ForegroundColor Cyan
ssh $Server "mkdir -p ~/counterplay-site/stats"
scp -r "$dist/v1" "${Server}:~/counterplay-site/stats/"
if ($LASTEXITCODE -ne 0) { throw "копирование не удалось" }

# 3. Проверка живьём
$patch = (Get-Content "$dist/v1/manifest.json" | ConvertFrom-Json).patch
Write-Host "`nГотово. Патч $patch. Проверка:" -ForegroundColor Green
Write-Host "  https://counterplays.com/api/stats/v1/manifest.json"
