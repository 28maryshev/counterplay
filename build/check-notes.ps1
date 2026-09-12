# Проверка заметок к релизу: они пишутся для ИГРОКА, не для разработчика.
#
# Патчноут уходит в Discord и на страницу релиза — это лицо продукта. Внутренняя
# кухня (журналы, песочница, коллектор, схема базы, имена файлов) игроку ничего
# не говорит: он её не видит и проверить не может. Такие строки выглядят шумом и
# обесценивают соседние, настоящие.
#
# Скрипт зовётся из release.ps1 перед публикацией и РОНЯЕТ релиз, если находит
# внутреннюю лексику. Осознанное исключение — ключ -AllowInternal.
#
#   .\build\check-notes.ps1 -Text "..."            проверить текст
#   .\build\check-notes.ps1 -Path notes.md         проверить файл
param(
  [string]$Text = "",
  [string]$Path = "",
  [switch]$Quiet
)

# Кодировку указываем явно: Windows PowerShell читает файл без BOM как ANSI,
# и тире с кавычками превращаются в «вЂ”» — прямо в патчноуте у игрока.
if ($Path -and (Test-Path $Path)) { $Text = Get-Content -Raw -Encoding UTF8 $Path }
if (-not $Text) { return }

# Слова, которых в патчноуте быть не должно. Ключ — что увидит игрок: если он
# не может это заметить в программе, значит строка не про него.
$banned = @{
  # Границы слова обязательны: «login» — это не «log», «indexes your climb» —
  # не индекс таблицы. Ложное срабатывание хуже пропуска: на него перестают
  # смотреть и начинают звать -AllowInternal не глядя.
  '\blogs?\b|\blogging\b|\blog file\b'      = 'журнал/логи — игрок их не читает'
  '\bsandbox\b|\btest mode\b|\bdev mode\b'  = 'песочница и тестовый режим — не для игроков'
  '\bcollector\b|\bpipeline\b'              = 'сбор данных — внутренняя кухня'
  '\bschema\b|\bsqlite\b|\bSQL\b|\bvacuum\b|\bWAL\b' = 'устройство базы'
  '\browid\b|\bcolumns?\b|\bprimary key\b'  = 'устройство таблиц'
  '\brefactor(ed|ing)?\b|\bcode path\b'     = 'работа с кодом, а не с продуктом'
  '\bcommits?\b|\brepository\b|\brepo\b'    = 'история изменений'
  '\bmiddleware\b|\bcontainers?\b|\bdocker\b|\bnginx\b' = 'инфраструктура'
  '\bcloudflare\b|\bcache header\b|\bno-store\b' = 'хостинг и кеширование'
  '\bbusy_timeout\b|\bthresholds?\b'        = 'внутренние пороги'
  '\.cs\b|\.ts\b|\.py\b|\.ps1\b'            = 'имена файлов'
}


$found = @()
foreach ($rx in $banned.Keys) {
  foreach ($m in [regex]::Matches($Text, $rx, 'IgnoreCase')) {
    # «login» — не «log»: слово целиком, а не кусок другого.
    $found += [pscustomobject]@{ Word = $m.Value; Why = $banned[$rx] }
  }
}

if ($found.Count -eq 0) {
  if (-not $Quiet) { Write-Host "Заметки к релизу: внутренней лексики нет." -ForegroundColor DarkGreen }
  return
}

Write-Host ""
Write-Host "СТОП: в заметках к релизу внутренняя информация." -ForegroundColor Red
foreach ($f in ($found | Sort-Object Word -Unique)) {
  Write-Host ("  «{0}» — {1}" -f $f.Word, $f.Why) -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Патчноут читает игрок. Перепиши так, чтобы каждая строка говорила," -ForegroundColor Gray
Write-Host "что он УВИДИТ в программе, — либо убери её совсем." -ForegroundColor Gray
Write-Host "Осознанное исключение: release.ps1 -AllowInternal" -ForegroundColor DarkGray
throw "Заметки к релизу содержат внутреннюю информацию."
