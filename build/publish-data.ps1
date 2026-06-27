# Publishes the central database to the GitHub 'data' release.
# Run after collect.py refreshes pipeline/data.db (once per patch).
#
#   .\build\publish-data.ps1
#
# The app checks data-version.json on each launch and re-downloads data.db
# when the version changes. Users get fresh meta without doing anything.

param([string]$Db = "pipeline/data.db")

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

if (-not (Test-Path $Db)) { throw "Database not found: $Db (run collect.py first)" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "gh CLI not found (winget install GitHub.cli; gh auth login)" }

# Консистентный снапшот: sqlite backup мержит WAL в один файл и читает БД даже
# если она ещё открыта (не ловит "файл занят"). Публикуем именно снапшот —
# иначе свежие записи из data.db-wal не попадут пользователям.
$snapDir = Join-Path $PWD "build\_publish"
New-Item -ItemType Directory -Force -Path $snapDir | Out-Null
$snap = Join-Path $snapDir "data.db"
if (Test-Path $snap) { Remove-Item $snap -Force }
python -c "import sqlite3,sys; s=sqlite3.connect(sys.argv[1]); d=sqlite3.connect(sys.argv[2]); s.backup(d); d.close(); s.close()" "$Db" "$snap"
if ($LASTEXITCODE -ne 0) { throw "snapshot (sqlite backup) failed" }

# Latest patch (for display/info) and content hash (for change detection) — из снапшота.
$patch = (python -c "import sqlite3,sys; c=sqlite3.connect(sys.argv[1]); ps=[r[0] for r in c.execute('SELECT DISTINCT patch FROM base_wr') if r[0] and r[0][0].isdigit()]; ps.sort(key=lambda s: tuple(int(x) for x in s.split('.'))); print(ps[-1] if ps else '0.0')" "$snap").Trim()
$hash  = (Get-FileHash $snap -Algorithm SHA256).Hash.Substring(0, 16)
Write-Host "Data: patch $patch, hash $hash"

# version = content hash -> users update on ANY change (new patch or just more games).
# Manifest is UTF-8 without BOM so JsonDocument parses cleanly.
$manifest = [ordered]@{ version = $hash; patch = $patch; updated = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $PWD "data-version.json"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# Ensure the 'data' release exists (rolling assets, separate from app releases).
# Use LASTEXITCODE (not $?) and avoid Stop terminating on gh's stderr.
$ErrorActionPreference = "Continue"
gh release view data 1>$null 2>$null
if ($LASTEXITCODE -ne 0) {
  gh release create data --title "Data" --notes "Central database, updated each patch"
}
gh release upload data $snap "data-version.json" --clobber
if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }
Remove-Item $snap -Force -ErrorAction SilentlyContinue
Write-Host "Uploaded data.db + data-version.json to 'data' release." -ForegroundColor Green
