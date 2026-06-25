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
  [switch]$Upload
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
    $v       = [version]$latest
    $Version = "$($v.Major).$($v.Minor).$($v.Build + 1)"
    Write-Host "Auto version: $Version (latest tag v$latest)" -ForegroundColor Cyan
  } else {
    $Version = "1.0.0"
    Write-Host "No tags found - using $Version" -ForegroundColor Cyan
  }
}

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

# 4. (optional) publish the app release to GitHub
if ($Upload) {
  if (-not $Token) { throw "Need -Token or GITHUB_TOKEN environment variable" }
  vpk upload github --repoUrl $repo --publish --releaseName "Counterplay v$Version" --tag "v$Version" --token $Token
}
