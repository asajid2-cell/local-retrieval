# Keeps the MUX remote archive server (CodexLocalRetrieval.Server, port 8765) alive ON ITS OWN.
#
# harmonizerlabs.cc/multiplex/pc/api/discovery and the /remote site both reach this PC through the
# reverse tunnel, and nginx proxies that mount to 127.0.0.1:8765. When the server process is gone the
# ssh forward is STILL up (ssh -N does not care whether its upstream answers), so the VPS port accepts
# every connection and then drops it -- the browser gets 502 and the site reads as "PC archive
# unavailable" even though the relay, nginx and the tunnel are all healthy.
#
# Historically the only things that started the server again were the one-shot logon task and the
# desktop app's "Start server" button, so an unattended crash left the site down until somebody opened
# the app -- the site silently depended on the GUI. This watchdog removes that dependency: it restarts
# the server when it is absent, and it never fights a deliberate stop.
[CmdletBinding()]
param(
    [int]$Port = 8765,
    [string]$RemoteDir = (Join-Path $env:LOCALAPPDATA 'CodexArchiveRemote'),
    # A cold start makes the server busy for MINUTES, not seconds: measured 2026-09-26, one startup scan
    # took 166 s (`[perf] scan files=3 ... ms=166510`) and requests sat behind a 5 s gate, so /healthz
    # times out while the server is perfectly alive. The gate is therefore a long, unambiguous window --
    # ten straight minutes of not answering -- because a merely busy server must never be killed and
    # restarted into another long sync. The failure that actually happens (the process is GONE, see the
    # WER access violations) is handled by the absent path below, which starts immediately.
    [int]$UnhealthyRunsBeforeRestart = 10,
    # Long enough to outlast that same cold sync: a 45 s wait reported "did not become healthy" for a
    # server that was merely loading, which reads as a failure in Task Scheduler's LastTaskResult.
    [int]$StartDeadlineSeconds = 180,
    [int]$MinSecondsBetweenStarts = 45
)
$ErrorActionPreference = 'Continue'

$launcher    = Join-Path $RemoteDir 'run-remote.ps1'
$logPath     = Join-Path $RemoteDir 'watchdog.log'
$statePath   = Join-Path $RemoteDir 'watchdog.state.json'
$pauseMarker = Join-Path $RemoteDir 'watchdog.pause'
$serverProc  = 'CodexLocalRetrieval.Server'

function Write-WatchdogLog([string]$Message) {
    try {
        if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).Length -gt 1MB) {
            Move-Item -LiteralPath $logPath -Destination "$logPath.1" -Force -ErrorAction SilentlyContinue
        }
        Add-Content -LiteralPath $logPath -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $Message) -Encoding UTF8
    } catch {}
}

function Test-ServerHealthy {
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$Port/healthz" -TimeoutSec 12
        return ($health.ok -eq $true -and $health.service -eq 'codex-local-retrieval')
    } catch { return $false }
}

function Get-ServerProcess {
    @(Get-Process -Name $serverProc -ErrorAction SilentlyContinue)
}

function Read-State {
    try {
        $raw = Get-Content -LiteralPath $statePath -Raw -ErrorAction Stop | ConvertFrom-Json
        return @{ unhealthyRuns = [int]$raw.unhealthyRuns; lastStartUtc = [string]$raw.lastStartUtc }
    } catch { return @{ unhealthyRuns = 0; lastStartUtc = '' } }
}

function Write-State([int]$UnhealthyRuns, [string]$LastStartUtc) {
    try {
        @{ unhealthyRuns = $UnhealthyRuns; lastStartUtc = $LastStartUtc } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath $statePath -Encoding UTF8
    } catch {}
}

# "The user stopped it" is a real intent and outranks the watchdog: the app's Stop button reclaims RAM
# on purpose. The marker records that intent. It goes stale once the machine has rebooted, which is
# exactly the "it will start again at next login" promise the Stop button makes.
function Test-PauseActive {
    if (-not (Test-Path -LiteralPath $pauseMarker)) { return $false }
    $bootTime = (Get-Date).AddMilliseconds(-[System.Environment]::TickCount)
    if ((Get-Item -LiteralPath $pauseMarker).LastWriteTime -lt $bootTime) {
        Remove-Item -LiteralPath $pauseMarker -Force -ErrorAction SilentlyContinue
        Write-WatchdogLog 'cleared a stop marker left over from before the last boot; the server is wanted again'
        return $false
    }
    return $true
}

function Start-Server {
    Start-Process -FilePath 'powershell.exe' `
        -ArgumentList @('-NoProfile', '-WindowStyle', 'Hidden', '-ExecutionPolicy', 'Bypass', '-File', $launcher) `
        -WindowStyle Hidden
}

$state = Read-State

if (Test-ServerHealthy) {
    if ($state.unhealthyRuns -ne 0) { Write-State 0 $state.lastStartUtc }
    exit 0
}

if (-not (Test-Path -LiteralPath $launcher)) {
    Write-WatchdogLog "launcher missing: $launcher"
    exit 1
}

if (Test-PauseActive) {
    Write-WatchdogLog 'server is stopped and the stop was deliberate; leaving it alone'
    exit 0
}

$processes = Get-ServerProcess
$unhealthyRuns = $state.unhealthyRuns + 1

if ($processes.Count -gt 0) {
    # A live process that cannot answer /healthz still HOLDS the port, so the tunnel keeps forwarding
    # into a dead server and the site stays 502. Unlike muxd (where killing loses live sessions), there
    # is nothing to preserve here -- but a cold archive load makes a LIVE server stop answering for
    # minutes, so the bar is ten consecutive minutes of silence, never one slow probe.
    if ($unhealthyRuns -lt $UnhealthyRunsBeforeRestart) {
        Write-State $unhealthyRuns $state.lastStartUtc
        Write-WatchdogLog ("server pid $($processes[0].Id) is not answering /healthz (run $unhealthyRuns of $UnhealthyRunsBeforeRestart); not restarting yet")
        exit 0
    }
    Write-WatchdogLog "server pid $($processes[0].Id) stayed unhealthy for $unhealthyRuns runs; restarting it"
    foreach ($process in $processes) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        try { $process.WaitForExit(8000) | Out-Null } catch {}
    }
} else {
    Write-WatchdogLog "server process is absent; starting it"
}

if ($state.lastStartUtc) {
    $since = (Get-Date) - [datetime]::Parse($state.lastStartUtc, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    if ($since.TotalSeconds -lt $MinSecondsBetweenStarts) {
        Write-WatchdogLog ("started {0:N0}s ago; waiting before trying again" -f $since.TotalSeconds)
        Write-State $unhealthyRuns $state.lastStartUtc
        exit 0
    }
}

$startedAt = (Get-Date).ToString('o')
Write-State $unhealthyRuns $startedAt
Start-Server

$deadline = (Get-Date).AddSeconds($StartDeadlineSeconds)
do {
    Start-Sleep -Milliseconds 750
    if (Test-ServerHealthy) {
        Write-State 0 $startedAt
        Write-WatchdogLog 'server recovered and is answering /healthz'
        exit 0
    }
} while ((Get-Date) -lt $deadline)

Write-WatchdogLog "server did not become healthy within $StartDeadlineSeconds s of starting"
exit 1
