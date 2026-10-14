# Проверка заметок к релизу: они пишутся для ИГРОКА, не для разработчика.
#
# Патчноут уходит в Discord и на страницу релиза — это лицо продукта. Внутренняя
# кухня (журналы, песочница, коллектор, схема базы, имена файлов) игроку ничего
# не говорит: он её не видит и проверить не может. Такие строки выглядят шумом и
# обесценивают соседние, настоящие.
#
# И патчноут — сводка, а не рассказ: что поменялось и на что обратить внимание,
# по короткой строке на изменение. Как загорается рамка и откуда въезжает бан,
# игрок увидит сам; абзац про каждую анимацию топит главное.
#
# И патчноут — по-английски, как все выпуски до 1.3.63: бот публикует его в
# Discord как есть, без перевода, и 1.3.63 ушёл туда по-русски.
#
# Скрипт зовётся из release.ps1 перед публикацией и РОНЯЕТ релиз, если находит
# внутреннюю лексику, слишком длинный текст или кириллицу. Осознанное исключение — ключ
# -AllowInternal.
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

# Длина. 1.3.40–1.3.50 были одной строкой в 10–20 слов, а 1.3.59 и 1.3.61
# вышли по 230–250 — абзацами про каждую вспышку и волну, и владелец попросил
# так не писать. Пункт длиннее 25 слов — это уже объяснение, а не строка.
$maxItemWords  = 25    # пункт — одно предложение
$maxTotalWords = 100   # весь патчноут — несколько пунктов

# Пункт — абзац или строка списка; перенос строки внутри абзаца пункт не рвёт
# (заметки бывают свёрстаны с жёсткими переносами).
$items = @()
foreach ($para in (($Text -replace "`r", '') -split "\n\s*\n")) {
  $cur = ''
  foreach ($line in ($para -split "\n")) {
    if ($line -match '^\s*([-*•]|\d+\.)\s') {
      if ($cur.Trim()) { $items += $cur.Trim() }
      $cur = $line
    } else { $cur += ' ' + $line }
  }
  if ($cur.Trim()) { $items += $cur.Trim() }
}

# Слово — кусок с буквой или цифрой: маркер списка и тире словами не считаются.
$total = 0
$long = @()
foreach ($it in $items) {
  $n = @($it -split '\s+' | Where-Object { $_ -match '\w' }).Count
  $total += $n
  if ($n -gt $maxItemWords) { $long += [pscustomobject]@{ N = $n; Text = $it } }
}
$tooLong = $total -gt $maxTotalWords -or $long.Count -gt 0

# Язык. Кириллица в английском патчноуте не нужна ни в одном слове, так что
# хватает её одной: ложных срабатываний здесь не бывает.
$cyrillic = @([regex]::Matches($Text, '\p{IsCyrillic}+\S*') | ForEach-Object { $_.Value })
$notEnglish = $cyrillic.Count -gt 0

if ($found.Count -eq 0 -and -not $tooLong -and -not $notEnglish) {
  if (-not $Quiet) { Write-Host "Заметки к релизу: внутренней лексики нет, длина $total слов." -ForegroundColor DarkGreen }
  return
}

Write-Host ""
if ($notEnglish) {
  Write-Host "СТОП: патчноут не на английском." -ForegroundColor Red
  $sample = ($cyrillic | Select-Object -First 5) -join ' '
  Write-Host "  кириллица: $sample" -ForegroundColor Yellow
  Write-Host ""
  Write-Host "Бот публикует заметки в Discord как есть, без перевода. Пиши по-английски," -ForegroundColor Gray
  Write-Host "как все прошлые выпуски." -ForegroundColor Gray
  Write-Host ""
}
if ($found.Count -gt 0) {
  Write-Host "СТОП: в заметках к релизу внутренняя информация." -ForegroundColor Red
  foreach ($f in ($found | Sort-Object Word -Unique)) {
    Write-Host ("  «{0}» — {1}" -f $f.Word, $f.Why) -ForegroundColor Yellow
  }
  Write-Host ""
  Write-Host "Патчноут читает игрок. Перепиши так, чтобы каждая строка говорила," -ForegroundColor Gray
  Write-Host "что он УВИДИТ в программе, — либо убери её совсем." -ForegroundColor Gray
  Write-Host ""
}
if ($tooLong) {
  Write-Host "СТОП: патчноут слишком длинный." -ForegroundColor Red
  if ($total -gt $maxTotalWords) {
    Write-Host ("  всего {0} слов, предел {1}" -f $total, $maxTotalWords) -ForegroundColor Yellow
  }
  foreach ($l in $long) {
    $head = if ($l.Text.Length -gt 60) { $l.Text.Substring(0, 60) + '…' } else { $l.Text }
    Write-Host ("  {0} слов в пункте, предел {1}: «{2}»" -f $l.N, $maxItemWords, $head) -ForegroundColor Yellow
  }
  Write-Host ""
  Write-Host "Пиши в общих словах: что поменялось и на что обратить внимание, одной" -ForegroundColor Gray
  Write-Host "строкой на изменение. Как это выглядит и анимируется — игрок увидит сам." -ForegroundColor Gray
  Write-Host ""
}
Write-Host "Осознанное исключение: release.ps1 -AllowInternal" -ForegroundColor DarkGray
throw "Заметки к релизу не прошли проверку."
