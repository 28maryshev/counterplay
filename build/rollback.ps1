# Откат на предыдущую версию — одной командой.
#
#   .\build\rollback.ps1              откатить на версию перед выпущенной
#   .\build\rollback.ps1 -To 1.3.45   откатить на конкретную
#   .\build\rollback.ps1 -DryRun      только показать, что будет сделано
#
# Как это работает и почему именно так.
#
# Velopack ходит ТОЛЬКО ВПЕРЁД: у кого стоит 1.3.48, тот не вернётся на 1.3.47,
# сколько ни переключай ленту обновлений. Переключение ленты спасает лишь новые
# установки — те, кто уже обновился, остаются со сломанной версией.
#
# Поэтому откат делается пересборкой: берём ИСХОДНИКИ прежней версии по её тегу
# и выпускаем их под СЛЕДУЮЩИМ номером. Для программы это обычное обновление
# вперёд, а внутри — прежний код. Так откат доходит до всех.
#
# Рабочая копия при этом не трогается: сборка идёт в отдельном git worktree,
# который удаляется в конце. Можно откатываться, не отвлекаясь от текущей работы.

param(
  # Версия, на которую откатываемся. Пусто — та, что шла перед выпущенной.
  [string]$To = "",
  [string]$Token = $env:GITHUB_TOKEN,
  # Показать план и выйти, ничего не собирая и не заливая.
  [switch]$DryRun
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# ── Какие версии вообще выпущены ────────────────────────────────────────────
$tags = git ls-remote --tags origin 2>$null |
        Select-String -Pattern 'refs/tags/v(\d+\.\d+\.\d+)$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value } |
        Sort-Object { [version]$_ }
if (-not $tags) { throw "не нашёл ни одного тега вида vX.Y.Z — откатывать не с чего" }

$current = $tags | Select-Object -Last 1
if ($To) {
  if ($tags -notcontains $To) {
    throw "версия $To не выпускалась. Есть: $(($tags | Select-Object -Last 8) -join ', ')"
  }
  $target = $To
} else {
  $target = $tags | Select-Object -Last 2 | Select-Object -First 1
  if ($target -eq $current) { throw "выпущена всего одна версия ($current) — откатывать некуда" }
}

# Следующий номер — по тем же правилам, что и в release.ps1: патч двузначный.
$v   = [version]$current
$maj = $v.Major; $min = $v.Minor; $pat = $v.Build + 1
if ($pat -gt 99) { $pat = 0; $min++ }
if ($min -gt 9)  { $min = 0; $maj++ }
$next = "$maj.$min.$pat"

Write-Host ""
Write-Host "Откат: код версии $target выйдет как $next (сейчас у людей $current)" -ForegroundColor Cyan
Write-Host "  Velopack не ходит назад, поэтому старый код выпускается новым номером —" -ForegroundColor DarkGray
Write-Host "  для программы это обычное обновление, и оно дойдёт до всех." -ForegroundColor DarkGray
Write-Host ""

if ($DryRun) { Write-Host "-DryRun: остановился, ничего не делал." -ForegroundColor Yellow; return }

# ── Сборка из отдельной копии по тегу ───────────────────────────────────────
# Рабочую копию не трогаем: там может идти другая работа.
$work = Join-Path ([System.IO.Path]::GetTempPath()) "counterplay-rollback-$target"
# Прибираем за прошлым запуском силами самого git: удалять папку напрямую
# нельзя — в ней его служебные файлы, и обрывок остаётся в списке worktree.
git worktree remove --force $work 2>$null | Out-Null
git worktree prune 2>$null | Out-Null

Write-Host "Разворачиваю исходники v$target..." -ForegroundColor Cyan
git fetch --tags origin 2>$null | Out-Null
git worktree add --detach $work "v$target" 2>&1 | Out-Null
if (-not (Test-Path (Join-Path $work "Counterplay.csproj"))) {
  throw "не удалось развернуть v$target в $work"
}

try {
  # Пакеты прошлых версий нужны Velopack, чтобы построить разницу. Их держит
  # рабочая копия — переносим в новую, иначе выйдет только полный пакет.
  $relSrc = Join-Path $PWD "Releases"
  $relDst = Join-Path $work "Releases"
  if (Test-Path $relSrc) {
    New-Item -ItemType Directory -Force -Path $relDst | Out-Null
    Copy-Item (Join-Path $relSrc "*.nupkg") $relDst -Force -ErrorAction SilentlyContinue
  }

  # Заметки пишем сами: автоген собрал бы список коммитов, которых в этой
  # сборке НЕТ, — она собрана из прошлого состояния.
  $notes = "Rolled back to the build released as $target. " +
           "Everything published after it is not in this version."
  $notesFile = Join-Path ([System.IO.Path]::GetTempPath()) "cp-rollback-$next.txt"
  [System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding($false)))

  $args = @("-Version", $next, "-Upload", "-NotesPath", $notesFile)
  if ($Token) { $args += @("-Token", $Token) }
  & (Join-Path $work "build\release.ps1") @args
  if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "сборка/заливка не прошла" }

  Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

  # Собранные пакеты забираем обратно: следующий обычный релиз строит разницу
  # от последнего, а он остался в временной копии.
  if (Test-Path $relDst) {
    Copy-Item (Join-Path $relDst "*.nupkg") $relSrc -Force -ErrorAction SilentlyContinue
  }

  Write-Host ""
  Write-Host "Готово: $next = код версии $target, лента переключена на неё." -ForegroundColor Green
  Write-Host "Люди получат её обычным обновлением." -ForegroundColor Green
}
finally {
  git worktree remove --force $work 2>$null | Out-Null
  git worktree prune 2>$null | Out-Null
}
