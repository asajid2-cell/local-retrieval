[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$restartScript = Join-Path $PSScriptRoot "restart_muxd.ps1"
$watchdogScript = Join-Path $PSScriptRoot "watch_muxd.ps1"
$powerShell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$userId = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$principal = New-ScheduledTaskPrincipal `
    -UserId $userId `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -StartWhenAvailable

$restartAction = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$restartScript`""
Register-ScheduledTask `
    -TaskName "MuxdSessionHostRestart" `
    -Action $restartAction `
    -Principal $principal `
    -Settings $settings `
    -Description "Safely restart muxd after rechecking that no hosted session is active." `
    -Force | Out-Null

$watchdogAction = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$watchdogScript`""
$watchdogTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask `
    -TaskName "MuxdSessionHostWatchdog" `
    -Action $watchdogAction `
    -Trigger $watchdogTrigger `
    -Principal $principal `
    -Settings $settings `
    -Description "Restart muxd when its process is absent; never kill a live but unresponsive owner." `
    -Force | Out-Null

Write-Output "Installed MuxdSessionHostRestart and MuxdSessionHostWatchdog."
