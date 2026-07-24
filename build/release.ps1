# Build Counterplay installer (Velopack) and optionally publish a GitHub release.
#
#   .\build\release.ps1 -Version 1.0.1
#   .\build\release.ps1 -Version 1.0.1 -Upload -Token <github_token>
#
# Output: .\Releases\Counterplay-win-Setup.exe and update packages.
# NOTE: data.db is NOT bundled. The app downloads it on first run from the
# latest release. So every release must include data.db as an asset (this
# script uploads it via gh when -Upload is used).

param(
  [string]$Version = "",                 # empty = auto-increment patch from latest tag
  [string]$Token   = $env:GITHUB_TOKEN,
  [switch]$Upload,
  # Only refresh the rolling 'latest' feed for an ALREADY published version —
  # no rebuild, no new release. Use when the feed step failed on its own.
  #   .\build\release.ps1 -Version 1.0.71 -Upload -FeedOnly
  [switch]$FeedOnly,
  # Release notes for the GitHub release (the Discord bot posts them as the
  # update description). Empty = auto-generate from commits since the last tag.
  #   .\build\release.ps1 -Upload -Notes "Rune & build panel, faster updates"
  [string]$Notes = "",
  # Prose "Highlights" block prepended to the notes — a human summary of a big
  # feature that the commit list can't convey. Also read from
  # build/RELEASE_HIGHLIGHTS.txt (gitignored) if that file exists (consumed once).
  [string]$Highlights = "",
  # Widen the changelog window: build notes from THIS tag..HEAD instead of just
  # the previous release. Use to fold a feature that shipped across several small
  # releases into ONE coherent, grouped changelog.
  #   .\build\release.ps1 -Upload -SinceTag v1.0.95
  [string]$SinceTag = ""
)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

$repo = "https://github.com/28maryshev/counterplay"

# Auto version: latest vX.Y.Z tag from origin, patch + 1.
if ([string]::IsNullOrWhiteSpace($Version)) {
  $tags = git ls-remote --tags origin 2>$null |
          Select-String -Pattern 'refs/tags/v(\d+\.\d+\.\d+)$' |
          ForEach-Object { $_.Matches[0].Groups[1].Value }
  if ($tags) {
    $latest  = $tags | Sort-Object { [version]$_ } | Select-Object -Last 1
    if ($FeedOnly) {
      # Фид наполняем для УЖЕ выпущенной версии — не для следующей.
      $Version = $latest
      Write-Host "Feed for the latest published release: v$Version" -ForegroundColor Cyan
    } else {
      $v       = [version]$latest
      $Version = "$($v.Major).$($v.Minor).$($v.Build + 1)"
      Write-Host "Auto version: $Version (latest tag v$latest)" -ForegroundColor Cyan
    }
  } else {
    $Version = "1.0.0"
    Write-Host "No tags found - using $Version" -ForegroundColor Cyan
  }
}

if (-not $FeedOnly) {
  # 1. Velopack CLI (vpk)
  if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host "Installing vpk (Velopack CLI)..."
    dotnet tool install -g vpk
    $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
  }

  # 2. Self-contained publish (bundles .NET runtime and native deps)
  $pub = "publish"
  if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
  dotnet publish Counterplay.csproj -c Release -r win-x64 --self-contained true -o $pub

  # 3. Build Setup.exe and update package (with logo icon if present)
  $iconArgs = @()
  if (Test-Path "assets\logo.ico") { $iconArgs = @("--icon", "assets\logo.ico") }
  vpk pack --packId Counterplay --packTitle Counterplay --packVersion $Version --packDir $pub --mainExe Counterplay.exe @iconArgs

  Write-Host ""
  Write-Host "Done: installer and release files are in .\Releases" -ForegroundColor Green
  Write-Host "Note: database is published separately via build\publish-data.ps1 (data release)." -ForegroundColor Yellow
}
else {
  Write-Host "Feed-only: refreshing the 'latest' feed for v$Version (no rebuild)" -ForegroundColor Cyan
}

# 4. (optional) publish the app release to GitHub
if ($Upload) {
  # No explicit token? Fall back to the token from an authenticated gh CLI, so
  # `.\release` just works after `gh auth login` without exporting a variable.
  if (-not $Token -and (Get-Command gh -ErrorAction SilentlyContinue)) {
    $Token = (gh auth token 2>$null | Select-Object -First 1)
  }
  if (-not $Token) {
    throw "No GitHub token: pass -Token, set GITHUB_TOKEN, or run 'gh auth login'"
  }
  if (-not $FeedOnly) {
    vpk upload github --repoUrl $repo --publish --releaseName "Counterplay v$Version" --tag "v$Version" --token $Token

    # Release notes = update description shown in Discord. If -Notes not given,
    # build a short changelog from commit subjects since the PREVIOUS release
    # tag. vpk just created v$Version on GitHub, so fetch tags first; the tag
    # right below this one marks where the previous release was cut.
    $env:GH_TOKEN = $Token
    if (-not $Notes) {
      git fetch --tags --force --quiet 2>$null
      $verTags = git tag --list "v*" |
                 Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
                 Sort-Object { [version]($_.TrimStart('v')) }
      $idx  = [array]::IndexOf([array]$verTags, "v$Version")
      $prev = if ($idx -gt 0) { $verTags[$idx - 1] }
              else { $verTags | Where-Object { $_ -ne "v$Version" } | Select-Object -Last 1 }

      # -SinceTag widens the window so a feature that shipped across several
      # small releases folds into ONE changelog for this one.
      $from  = if ($SinceTag) { $SinceTag } else { $prev }
      $range = if ($from) { "$from..HEAD" } else { "HEAD" }

      # In the Discord announcement players only care about the app itself.
      # Commits that touched ONLY internal files (test sandbox, data pipeline,
      # Discord bot, build scripts, docs) are left out of the release notes.
      $internal = '^(pipeline/|bot/|build/|docs/|\.claude/|\.github/|README|CLAUDE\.md|\.gitignore|TestMode\.cs|DraftTest\.cs)'

      # Group commits by FEATURE = the "Area:" prefix before the first colon
      # (e.g. "Duo pool", "Pool settings", "Damage mix"). A big feature made of
      # many commits collapses into one block instead of a wall of bullets.
      $groups  = [ordered]@{}
      $singles = @()
      foreach ($c in (git log $range --no-merges --pretty=format:'%H|%s' 2>$null)) {
        $h, $s = $c -split '\|', 2
        if (-not $s -or $s -match '^(Co-Authored-By|Merge )') { continue }
        $files = git diff-tree --no-commit-id --name-only -r $h 2>$null
        $userFacing = $files | Where-Object { $_ -notmatch $internal }
        if ($files -and -not $userFacing) { continue }   # только внутренняя кухня

        if ($s -match '^([A-Z][^:]{1,26}):\s*(.+)$') {
          $area = $matches[1].Trim(); $detail = $matches[2].Trim()
          if (-not $groups.Contains($area)) { $groups[$area] = @() }
          $groups[$area] += $detail
        } else {
          $singles += $s
        }
      }

      $lines = @()
      foreach ($area in $groups.Keys) {
        $details = @($groups[$area])
        if ($details.Count -ge 2) {          # большая фича → заголовок + детали
          $lines += "**$area**"
          foreach ($d in $details) { $lines += "  • $d" }
        } else {                             # один коммит области → плоской строкой
          $lines += "- ${area}: $($details[0])"
        }
      }
      foreach ($s in $singles) { $lines += "- $s" }

      $Notes = if ($lines) { ($lines -join "`n") } else { "Maintenance and fixes." }
      Write-Host "Changelog since $from ($($groups.Count) features, $($singles.Count) other)" -ForegroundColor Cyan
    }

    # Highlights — связное описание большой фичи (проза), которого нет в коммит-
    # логе. Из -Highlights или build/RELEASE_HIGHLIGHTS.txt (одноразово: файл
    # удаляется после успешной подстановки, чтобы не повторяться в след. релизе).
    $hlFile = Join-Path $PSScriptRoot "RELEASE_HIGHLIGHTS.txt"
    if (-not $Highlights -and (Test-Path $hlFile)) {
      $Highlights = (Get-Content $hlFile -Raw).Trim()
    }
    if ($Highlights) {
      $Notes = "✨ **Highlights**`n$Highlights`n`n**Changes**`n$Notes"
      if (Test-Path $hlFile) { Remove-Item $hlFile -Force }
    }
    $Notes | gh release edit "v$Version" --notes-file - 2>$null
    if ($LASTEXITCODE -ne 0) { Write-Host "warn: could not set release notes" -ForegroundColor Yellow }
    else { Write-Host "Release notes set for v$Version" -ForegroundColor Green }
  }

  # 5. Rolling 'latest' tag = the update feed the app actually reads.
  #
  # The app does NOT use the GitHub API: unauthenticated api.github.com allows
  # only 60 requests/hour PER IP, so users behind a shared IP (dorms, cafes,
  # carrier NAT) silently stop receiving updates. Direct release-file URLs are
  # served by GitHub's CDN and are not rate limited — but they need a stable
  # address, hence this rolling tag.
  #
  # gh writes progress to stderr, and PowerShell turns that into a terminating
  # error while $ErrorActionPreference = 'Stop'. So relax it here and check
  # $LASTEXITCODE explicitly instead.
  $ErrorActionPreference = "Continue"
  $env:GH_TOKEN = $Token

  gh release view latest 1>$null 2>$null
  if ($LASTEXITCODE -ne 0) {
    gh release create latest --title "Latest (update feed)" `
      --notes "Rolling feed the app reads for updates. Do not delete." 2>$null
    if ($LASTEXITCODE -ne 0) { throw "failed to create the 'latest' tag" }
  }

  # Take the feed vpk just PUBLISHED (it lists only this version), not the local
  # one in .\Releases — that one accumulates every version ever built and would
  # point at packages absent from the rolling tag.
  $tmp = Join-Path $PWD "build\_feed"
  if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $tmp | Out-Null
  foreach ($f in @("releases.win.json", "RELEASES")) {
    gh release download "v$Version" --pattern $f --dir $tmp --clobber 2>$null
  }

  $feed = @(Get-ChildItem $tmp -File)
  $feed += @(Get-ChildItem "Releases" -File | Where-Object { $_.Name -like "Counterplay-$Version-*.nupkg" })
  if ($feed.Count -lt 2) { throw "feed files for v$Version not found (is the release published?)" }

  gh release upload latest @($feed.FullName) --clobber 2>$null
  if ($LASTEXITCODE -ne 0) { throw "failed to update the 'latest' feed" }
  Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
  Write-Host "Update feed refreshed (tag 'latest'): $($feed.Name -join ', ')" -ForegroundColor Green
}
