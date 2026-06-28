#requires -version 5
<#
  Uninstall Codex Local Retrieval: removes the installed app and its shortcuts.
  Your chats / collections / backups in %LOCALAPPDATA%\CodexLocalRetrieval are KEPT.
  Pass -PurgeData to also delete that data.
#>
[CmdletBinding()]
param([switch]$PurgeData)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\CodexLocalRetrieval'
$dataDir    = Join-Path $env:LOCALAPPDATA 'CodexLocalRetrieval'

Get-Process -Name CodexLocalRetrieval.Native -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

if (Test-Path $installDir) { Remove-Item $installDir -Recurse -Force; Write-Host "Removed $installDir" } else { Write-Host "App was not installed." }

foreach ($lnk in @(
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Codex Local Retrieval.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Codex Local Retrieval.lnk')
)) { if (Test-Path $lnk) { Remove-Item $lnk -Force; Write-Host "Removed shortcut $lnk" } }

if ($PurgeData) {
    if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force; Write-Host "Purged data $dataDir" }
} else {
    Write-Host "Kept your data in $dataDir (use -PurgeData to delete it too)."
}
