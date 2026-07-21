[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$python = "C:\Python311\python.exe"
$taskName = "MuxdSessionHost"
$logPath = Join-Path $root "muxd-watchdog.log"

function Write-WatchdogLog([string]$Message) {
    $line = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
}

function Get-MuxdProcesses {
    @(
        Get-CimInstance Win32_Process |
            Where-Object {
                $_.CommandLine -match "C:\\Users\\Ahmed\\muxd\\muxd\.py"
            }
    )
}

$previousAutostart = $env:MUXCTL_AUTOSTART
try {
    $env:MUXCTL_AUTOSTART = "0"
    & $python (Join-Path $root "muxctl.py") status *> $null
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
        & $python (Join-Path $root "muxctl.py") status *> $null
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
