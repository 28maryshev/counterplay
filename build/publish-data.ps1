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

# Version = latest patch present in the DB (version-sorted).
$ver = (python -c "import sqlite3; c=sqlite3.connect(r'$Db'); ps=[r[0] for r in c.execute('SELECT DISTINCT patch FROM base_wr') if r[0] and r[0][0].isdigit()]; ps.sort(key=lambda s: tuple(int(x) for x in s.split('.'))); print(ps[-1] if ps else '0.0')").Trim()
Write-Host "Data version (latest patch): $ver"

# Manifest (UTF-8 without BOM so JsonDocument parses cleanly).
$manifest = [ordered]@{ version = $ver; updated = (Get-Date).ToUniversalTime().ToString("o") } | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $PWD "data-version.json"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

# Ensure the 'data' release exists (rolling assets, separate from app releases).
gh release view data 1>$null 2>$null
if (-not $?) { gh release create data --title "Data" --notes "Central database, updated each patch" }

gh release upload data $Db "data-version.json" --clobber
Write-Host "Uploaded data.db + data-version.json to 'data' release." -ForegroundColor Green
