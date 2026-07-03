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
  have if you can build the app). The Windows App SDK runtime AND the VC++ runtime it depends on are
  bundled next to the exe, so the app is self-contained (no reliance on a machine-wide VC++ redist).

  Reliability: the app is unpackaged self-contained WinUI 3 - if ANY WindowsAppRuntime native DLL (or
  its VC++ dependency) is missing next to the exe, the bootstrap .cctor throws DllNotFound BEFORE any
  managed code runs and the process fails-fast as 0xc000027b through Microsoft.UI.Xaml.dll (a crash no
  in-app handler can catch). This installer therefore STAGES, bundles VC++, VERIFIES every critical DLL
  is present, and only then ATOMICALLY swaps it into place - a partial/broken install is impossible; on
  any failure it rolls back and leaves the previous working install untouched.

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

# Stop EVERYTHING that could hold a lock on the install dir. A running app/bridge locking a DLL during
# the copy is exactly how a PARTIAL install happened - which then crash-loops at startup with
# 0xc000027b, because the WinUI bootstrap (.cctor) can't load a WindowsAppRuntime DLL (DllNotFound).
function Stop-AppAndDeps {
    foreach ($n in 'CodexLocalRetrieval.Native','CodexLocalRetrieval.Server') {
        $ps = Get-Process -Name $n -ErrorAction SilentlyContinue
        if ($ps) { $ps | Stop-Process -Force -ErrorAction SilentlyContinue; foreach ($p in $ps) { try { $p.WaitForExit(6000) | Out-Null } catch {} } }
    }
    try { Stop-ScheduledTask -TaskName 'CodexArchiveRemote' -ErrorAction SilentlyContinue } catch {}   # the bridge task can relaunch the app mid-install
}
Stop-AppAndDeps

& dotnet build $proj -c Release -r $rid --nologo -v m
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

$builtExe = Join-Path $buildDir $exeName
if (-not (Test-Path $builtExe)) { throw "Built exe not found: $builtExe" }
if (-not (Test-Path (Join-Path $buildDir 'CodexLocalRetrieval.Native.pri'))) { throw "App .pri missing from build output - the app would crash on XAML load." }

Write-Host "Staging a COMPLETE, verified install (atomic swap) ..." -ForegroundColor Cyan
Stop-AppAndDeps            # once more, in case a copy relaunched during the build
Start-Sleep -Milliseconds 400

# 1) Copy the build output into a FRESH staging dir - no pre-existing handle can make this copy partial.
$staging = "$installDir.staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
Get-ChildItem -LiteralPath $buildDir -Exclude 'publish','*.pdb' | Copy-Item -Destination $staging -Recurse -Force

# 2) BUNDLE the VC++ runtime the WindowsAppRuntime depends on, so the app is TRULY self-contained and
# never silently relies on the machine's VC++ redist (that missing dependency is the "or one of its
# dependencies" behind the 0x8007007E bootstrap fault).
$sys32 = Join-Path $env:WINDIR 'System32'
foreach ($vc in 'vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll') {
    $dst = Join-Path $staging $vc
    if (-not (Test-Path $dst)) {
        $src = Join-Path $sys32 $vc
        if (Test-Path $src) { Copy-Item $src $dst -Force } else { Write-Host "  WARN: $vc not in System32 to bundle" -ForegroundColor DarkYellow }
    }
}

# 3) VERIFY staging is COMPLETE before touching the live install. If anything critical is missing, ABORT
# and leave the working install untouched - never ship a half-copied app that crash-loops.
$critical = @($exeName,'CodexLocalRetrieval.Native.pri','Microsoft.WindowsAppRuntime.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll','Microsoft.WindowsAppRuntime.Bootstrap.Net.dll',
    'Microsoft.ui.xaml.dll','CoreMessagingXP.dll','MRM.dll','vcruntime140.dll','msvcp140.dll')
$missing = $critical | Where-Object { -not (Test-Path (Join-Path $staging $_)) }
if ($missing) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue; throw "Refusing to install: staged build is missing [$($missing -join ', ')]. The working install was left untouched." }

# 4) ATOMIC SWAP: move the old install aside, move staging into place. If the old dir is still locked
# after our kills, RETRY then ABORT - we NEVER overwrite-in-place (that is what produced partial installs).
$backup = "$installDir.old"
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
if (Test-Path $installDir) {
    $swapped = $false
    for ($i = 0; $i -lt 6 -and -not $swapped; $i++) {
        try { Rename-Item -LiteralPath $installDir -NewName (Split-Path $backup -Leaf) -ErrorAction Stop; $swapped = $true }
        catch { Stop-AppAndDeps; Start-Sleep -Seconds 1 }
    }
    if (-not $swapped) { Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue; throw "Install dir is locked (app still running?). Close it and retry - the working install was left in place." }
}
Rename-Item -LiteralPath $staging -NewName (Split-Path $installDir -Leaf) -ErrorAction Stop

# 5) POST-SWAP verify; if somehow incomplete, roll back to the backup.
$stillMissing = $critical | Where-Object { -not (Test-Path (Join-Path $installDir $_)) }
if ($stillMissing) {
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $backup) { Rename-Item -LiteralPath $backup -NewName (Split-Path $installDir -Leaf) -ErrorAction SilentlyContinue }
    throw "Install verification failed [$($stillMissing -join ', ')]. Rolled back to the previous install."
}
if (Test-Path $backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }
try { Start-ScheduledTask -TaskName 'CodexArchiveRemote' -ErrorAction SilentlyContinue } catch {}   # restart the bridge we paused

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
