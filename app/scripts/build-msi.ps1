#requires -version 5
<#
  Build MUX.msi - a proper per-user installer (no admin, no code signing).

  Builds the Release output, stages a clean copy (no publish subfolder, no .pdb), and runs the
  WiX toolset to produce dist\MUX.msi. Double-clicking that .msi installs the app
  to %LOCALAPPDATA%\Programs\MUX, adds Start Menu + Desktop shortcuts, and an
  Add/Remove Programs entry.

  Requires the WiX v5 tool:  dotnet tool install --global wix --version 5.0.2
  (v6/v7 add an "Open Source Maintenance Fee" EULA prompt; v5 builds with no extra steps.)
#>
[CmdletBinding()]
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'

$repo     = Split-Path -Parent $PSScriptRoot
$proj     = Join-Path $repo 'native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj'
$tfm      = 'net8.0-windows10.0.26100.0'
$rid      = 'win-x64'
$buildDir = Join-Path $repo "native\CodexLocalRetrieval.Native\bin\Release\$tfm\$rid"
$stageDir = Join-Path $env:TEMP 'clr-msi-stage'
$wxs      = Join-Path $repo 'installer\CodexLocalRetrieval.wxs'
$outDir   = Join-Path $repo 'dist'
$msi      = Join-Path $outDir 'MUX.msi'

$wix = if (Get-Command wix -ErrorAction SilentlyContinue) { 'wix' } else { Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe' }
if (-not (Test-Path $wix) -and $wix -ne 'wix') { throw "WiX not found. Install it: dotnet tool install --global wix" }

Write-Host "Building Release..." -ForegroundColor Cyan
& dotnet build $proj -c Release -r $rid --nologo -v m
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
if (-not (Test-Path (Join-Path $buildDir 'CodexLocalRetrieval.Native.pri'))) { throw 'App .pri missing from build output.' }

Write-Host "Staging files..." -ForegroundColor Cyan
if (Test-Path $stageDir) { Remove-Item $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Get-ChildItem -LiteralPath $buildDir -Exclude 'publish','*.pdb' | Copy-Item -Destination $stageDir -Recurse -Force

Write-Host "Building MSI ($Version)..." -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
& $wix build $wxs -d BuildDir="$stageDir" -d Version=$Version -o $msi
$code = $LASTEXITCODE
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
if ($code -ne 0) { throw "wix build failed (exit $code)" }

Write-Host ""
Write-Host "Built: $msi" -ForegroundColor Green
Write-Host "Install: double-click the .msi (per-user, no admin)."
Write-Host "Uninstall: Settings > Apps, or Add/Remove Programs ('MUX')."
