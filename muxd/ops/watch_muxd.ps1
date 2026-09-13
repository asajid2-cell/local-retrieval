[CmdletBinding()]
param(
    [string]$Profile = $(if ($env:MUXD_PROFILE) { $env:MUXD_PROFILE } else { "production" }),
    [string]$RuntimeRoot = ""
)
$ErrorActionPreference = "Stop"
if ($Profile -ne "production" -and -not $RuntimeRoot) { throw "non-production watchdog requires -RuntimeRoot" }
$env:MUXD_PROFILE = $Profile
if ($RuntimeRoot) {
    $env:MUXD_RUNTIME_ROOT = [IO.Path]::GetFullPath($RuntimeRoot)
    $env:MUXD_ENV_FILE = Join-Path $env:MUXD_RUNTIME_ROOT "muxd.env"
}
$root = if ($RuntimeRoot) { $env:MUXD_RUNTIME_ROOT } else { Split-Path -Parent $PSScriptRoot }
$python = "C:\Python311\python.exe"
$profileRaw = & $python (Join-Path $root "profile.py") --json
if ($LASTEXITCODE -ne 0) { throw 'Profile validation failed; no process or task changes permitted' }
$profileJson = $profileRaw | ConvertFrom-Json
if ($null -eq $profileJson -or $profileJson.Name -cne $Profile) { throw 'Profile identity validation failed' }
$taskName = $profileJson.TaskName
$logPath = Join-Path $profileJson.StateRoot "muxd-watchdog.log"

function Write-WatchdogLog([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-MuxdProcesses {
    $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId, CommandLine)
    $payload = ConvertTo-Json -InputObject $processes -Compress
    $payload = [regex]::Replace($payload, '[^\x00-\x7F]', { param($match) '\u{0:x4}' -f [int][char]$match.Value })
    $matched = $payload | & $python (Join-Path $root "profile.py") --matching-pids
    if ($LASTEXITCODE -ne 0) { throw 'Profile process ownership validation failed' }
    $ids = $matched | ConvertFrom-Json
    @($processes | Where-Object { $_.ProcessId -in $ids })
}

$previousAutostart = $env:MUXCTL_AUTOSTART
try {
    $env:MUXCTL_AUTOSTART = "0"
    & $python (Join-Path $root "muxctl.py") status --profile $Profile *> $null
    if ($LASTEXITCODE -eq 0) {
        exit 0
    }
}
catch {
}
finally {
    $env:MUXCTL_AUTOSTART = $previousAutostart
}

$processes = @(Get-MuxdProcesses)
if ($processes.Count -gt 0) {
    Write-WatchdogLog "muxd is running but local control is unresponsive; preserving process custody"
    exit 1
}

Write-WatchdogLog "muxd process absent; requesting scheduled-task start"
Start-ScheduledTask -TaskName $taskName
$deadline = (Get-Date).AddSeconds(20)
do {
    Start-Sleep -Milliseconds 500
    $previousAutostart = $env:MUXCTL_AUTOSTART
    try {
        $env:MUXCTL_AUTOSTART = "0"
        & $python (Join-Path $root "muxctl.py") status --profile $Profile *> $null
        if ($LASTEXITCODE -eq 0) {
            $pidValue = [int](Get-MuxdProcesses | Select-Object -First 1 -ExpandProperty ProcessId)
            Write-WatchdogLog "muxd recovered; pid=$pidValue"
            exit 0
        }
    }
    catch {
    }
    finally {
        $env:MUXCTL_AUTOSTART = $previousAutostart
    }
} while ((Get-Date) -lt $deadline)

Write-WatchdogLog "muxd recovery start did not become healthy"
exit 1
