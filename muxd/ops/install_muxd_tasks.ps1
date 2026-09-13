[CmdletBinding()]
param(
    [string]$Profile = $(if ($env:MUXD_PROFILE) { $env:MUXD_PROFILE } else { "production" }),
    [string]$RuntimeRoot = ""
)

$ErrorActionPreference = "Stop"
if ($Profile -ne "production" -and -not $RuntimeRoot) { throw "non-production task installation requires -RuntimeRoot" }
$env:MUXD_PROFILE = $Profile
if ($RuntimeRoot) {
    $env:MUXD_RUNTIME_ROOT = [IO.Path]::GetFullPath($RuntimeRoot)
    $env:MUXD_ENV_FILE = Join-Path $env:MUXD_RUNTIME_ROOT "muxd.env"
}
$launchScript = Join-Path $PSScriptRoot "launch_muxd.ps1"
$restartScript = Join-Path $PSScriptRoot "restart_muxd.ps1"
$watchdogScript = Join-Path $PSScriptRoot "watch_muxd.ps1"
$profileScript = Join-Path (Split-Path -Parent $PSScriptRoot) "profile.py"
$python = "C:\Python311\python.exe"
$profileRaw = & $python $profileScript --json
if ($LASTEXITCODE -ne 0) { throw 'Profile validation failed; no task changes permitted' }
$profileJson = $profileRaw | ConvertFrom-Json
if ($null -eq $profileJson -or $profileJson.Name -cne $Profile) { throw 'Profile identity validation failed' }
$hostTask = $profileJson.TaskName
$restartTask = $profileJson.RestartTaskName
$watchdogTask = $profileJson.WatchdogTaskName
$profileArgs = " -Profile `"$Profile`" -RuntimeRoot `"$RuntimeRoot`""
if ($Profile -eq "production") { $profileArgs = " -Profile production" }
$launchArgument = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$launchScript`"$profileArgs"
$restartArgument = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$restartScript`"$profileArgs"
$watchdogArgument = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$watchdogScript`"$profileArgs"
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

$launchAction = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument $launchArgument
Register-ScheduledTask `
    -TaskName $hostTask `
    -Action $launchAction `
    -Principal $principal `
    -Settings $settings `
    -Description "Run the profile-scoped muxd session host." `
    -Force | Out-Null

$restartAction = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument $restartArgument
Register-ScheduledTask `
    -TaskName $restartTask `
    -Action $restartAction `
    -Principal $principal `
    -Settings $settings `
    -Description "Safely restart muxd after rechecking that no hosted session is active." `
    -Force | Out-Null

$watchdogAction = New-ScheduledTaskAction `
    -Execute $powerShell `
    -Argument $watchdogArgument
$watchdogTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask `
    -TaskName $watchdogTask `
    -Action $watchdogAction `
    -Trigger $watchdogTrigger `
    -Principal $principal `
    -Settings $settings `
    -Description "Restart muxd when its process is absent; never kill a live but unresponsive owner." `
    -Force | Out-Null

Write-Output "Installed $hostTask, $restartTask, and $watchdogTask for profile $Profile."
