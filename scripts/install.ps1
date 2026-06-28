#requires -version 5
<#
  Proper per-user install for Codex Local Retrieval.

  Builds a Release build and copies it to %LOCALAPPDATA%\Programs\CodexLocalRetrieval, then
  creates Start Menu + Desktop shortcuts that point THERE. The app is launched from a stable
  install location - never from the throwaway bin/dist build output.

  Note: we install the Release BUILD output, not `dotnet publish` output. Unpackaged WinUI 3
  apps lose their compiled XAML (.xbf) + app .pri during publish and crash with "Cannot locate
  resource from 'ms-appx:///MainWindow.xaml'"; the build output keeps the working layout.

  This is a framework-dependent install: it needs the .NET 8 Desktop Runtime (which you already
  have if you can build the app). The Windows App SDK runtime is bundled in the output.

  Usage:
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1
    powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -NoDesktopShortcut
#>
[CmdletBinding()]
param(
    [switch]$NoDesktopShortcut
)
$ErrorActionPreference = 'Stop'

$repo       = Split-Path -Parent $PSScriptRoot
$proj       = Join-Path $repo 'native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj'
$tfm        = 'net8.0-windows10.0.26100.0'
$rid        = 'win-x64'
$exeName    = 'CodexLocalRetrieval.Native.exe'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CodexLocalRetrieval'
$buildDir   = Join-Path $repo "native\CodexLocalRetrieval.Native\bin\Release\$tfm\$rid"
$dataDir    = Join-Path $env:LOCALAPPDATA 'CodexLocalRetrieval'

Write-Host "Building Codex Local Retrieval (Release)..." -ForegroundColor Cyan
Get-Process -Name CodexLocalRetrieval.Native -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
& dotnet build $proj -c Release -r $rid --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

$builtExe = Join-Path $buildDir $exeName
if (-not (Test-Path $builtExe)) { throw "Built exe not found: $builtExe" }
if (-not (Test-Path (Join-Path $buildDir 'CodexLocalRetrieval.Native.pri'))) { throw "App .pri missing from build output - the app would crash on XAML load." }

Write-Host "Installing to $installDir ..." -ForegroundColor Cyan
Start-Sleep -Milliseconds 400
if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
# Copy the build output, skipping the (broken) publish subfolder and debug symbols.
Get-ChildItem -LiteralPath $buildDir -Exclude 'publish','*.pdb' | Copy-Item -Destination $installDir -Recurse -Force

# A small marker so you can tell what's installed.
@{ installedAt = (Get-Date).ToString('o') } | ConvertTo-Json | Set-Content (Join-Path $installDir 'install.json') -Encoding utf8

$target = Join-Path $installDir $exeName
$ws = New-Object -ComObject WScript.Shell
function New-AppShortcut([string]$lnkPath) {
    $sc = $ws.CreateShortcut($lnkPath)
    $sc.TargetPath       = $target
    $sc.WorkingDirectory = $installDir
    $sc.IconLocation     = "$target,0"
    $sc.Description       = 'Codex Local Retrieval - your local Claude/Codex chat hub'
    $sc.Save()
}
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Local Retrieval.lnk'
New-AppShortcut $startMenu
if (-not $NoDesktopShortcut) {
    New-AppShortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Local Retrieval.lnk')
}

Write-Host ""
Write-Host "Installed: $target" -ForegroundColor Green
Write-Host "Shortcuts: Start Menu$(if (-not $NoDesktopShortcut) { ' + Desktop' })."
Write-Host "Pin it: press Start, type 'Codex Local Retrieval', right-click -> Pin to Start / Pin to taskbar."
Write-Host "Your data stays in: $dataDir"
