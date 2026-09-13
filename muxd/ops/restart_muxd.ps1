[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Recovery,
    [string]$RecoveryToken,
    [int]$TimeoutSeconds = 30,
    [string]$Profile = $(if ($env:MUXD_PROFILE) { $env:MUXD_PROFILE } else { "production" }),
    [string]$RuntimeRoot = ""
)

$ErrorActionPreference = "Stop"
if ($Profile -ne "production" -and -not $RuntimeRoot) { throw "non-production restart requires -RuntimeRoot" }
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
$logPath = Join-Path $profileJson.StateRoot "muxd-restart.log"
$deployFence = Join-Path $profileJson.StateRoot "deploying.flag"

function Write-RestartLog([string]$Message) {
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

function Get-HostedSessions {
    $code = @"
import asyncio
import json
import sys
import os
sys.path.insert(0, os.environ['MUXD_PREFLIGHT_ROOT'])
import muxctl
async def preflight():
    await muxctl.fetch_info()
    return await muxctl.request_json({'t': 'ls'})
response = asyncio.run(preflight())
if not isinstance(response, dict) or not isinstance(response.get('list'), list):
    raise SystemExit('muxd preflight requires an explicit session list')
sessions = response['list']
if any(not isinstance(row, dict) or not isinstance(row.get('name'), str)
       or not row['name'] or not isinstance(row.get('alive'), bool) for row in sessions):
    raise SystemExit('muxd preflight received malformed session liveness')
print(json.dumps(sessions))
"@
    $previousAutostart = $env:MUXCTL_AUTOSTART
    $previousPreflightRoot = $env:MUXD_PREFLIGHT_ROOT
    try {
        $env:MUXCTL_AUTOSTART = "0"
        $env:MUXD_PREFLIGHT_ROOT = $root
        $raw = & $python -c $code 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "muxd preflight failed: $($raw -join ' ')"
        }
        $sessions = $raw | ConvertFrom-Json
        foreach ($session in $sessions) { $session }
    }
    finally {
        $env:MUXD_PREFLIGHT_ROOT = $previousPreflightRoot
        $env:MUXCTL_AUTOSTART = $previousAutostart
    }
}

function Wait-ForCondition([scriptblock]$Condition, [string]$Failure) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    throw $Failure
}

$existingProcesses = @(Get-MuxdProcesses)
$skipPreflight = $false
if ($Recovery) {
    if (-not $RecoveryToken -or -not (Test-Path -LiteralPath $deployFence)) {
        throw "recovery restart requires the active deployment token"
    }
    $fence = (Get-Content -LiteralPath $deployFence -Raw).Trim()
    if ($fence -ne "recovery:$RecoveryToken") {
        throw "recovery restart token does not match the active deployment"
    }
    $fenceAge = (Get-Date) - (Get-Item -LiteralPath $deployFence).LastWriteTime
    if ($fenceAge.TotalMinutes -gt 5) {
        throw "recovery restart token expired"
    }
    $skipPreflight = $true
}
if (-not $skipPreflight) {
    $sessions = @(Get-HostedSessions)
    $active = @($sessions | Where-Object { @($_.alive) -contains $true })
    if ($active.Count -gt 0) {
        $names = ($active | ForEach-Object { $_.name }) -join ", "
        throw "refusing muxd restart while active sessions exist: $names"
    }
} else {
    Write-RestartLog "recovery restart allowed under the active deployment fence"
}

Write-RestartLog "preflight passed; activeSessions=0 checkOnly=$CheckOnly recovery=$Recovery"
if ($CheckOnly) {
    return
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
foreach ($name in @("sessions.json", "live-tabs.json")) {
    $source = Join-Path $profileJson.StateRoot $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination "$source.deploy-$stamp.bak" -Force
    }
}

$oldProcesses = @(Get-MuxdProcesses)
$oldPids = @($oldProcesses | ForEach-Object { [int]$_.ProcessId })
$oldHostPids = @()
foreach ($pidValue in $oldPids) {
    $oldHostPids += @(
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$pidValue" |
            Where-Object {
                $_.Name -in @("conhost.exe", "OpenConsole.exe", "winpty-agent.exe")
            } |
            ForEach-Object { [int]$_.ProcessId }
    )
}

$restartStarted = $false
try {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    # Kill the matched muxd/conpty processes DIRECTLY. Stopping the task only ends the process it
    # launched; since muxd now runs under a launcher chain (wscript -> powershell -> keysafe ->
    # Start-Process pythonw) that injects MUX_HOST_TOKEN, the leaf pythonw is orphaned by a task-stop
    # rather than killed. Killing the pids we already resolved is correct regardless of how muxd was
    # launched, and unwinds the launcher chain as the -Wait parent sees its child exit.
    foreach ($pidValue in @($oldPids + $oldHostPids)) {
        Stop-Process -Id $pidValue -Force -ErrorAction SilentlyContinue
    }
    Wait-ForCondition {
        $remaining = @(
            $oldPids + $oldHostPids |
                Where-Object { Get-Process -Id $_ -ErrorAction SilentlyContinue }
        )
        $remaining.Count -eq 0
    } "old muxd process tree did not exit"

    Start-ScheduledTask -TaskName $taskName
    $restartStarted = $true
    Wait-ForCondition {
        $processes = @(Get-MuxdProcesses)
        if ($processes.Count -ne 1) {
            return $false
        }
        $previousAutostart = $env:MUXCTL_AUTOSTART
        try {
            $env:MUXCTL_AUTOSTART = "0"
            & $python (Join-Path $root "muxctl.py") status --profile $Profile *> $null
            return $LASTEXITCODE -eq 0
        }
        finally {
            $env:MUXCTL_AUTOSTART = $previousAutostart
        }
    } "replacement muxd did not become healthy"
    $newPid = [int](Get-MuxdProcesses | Select-Object -First 1 -ExpandProperty ProcessId)
    Write-RestartLog "restart complete; oldPids=$($oldPids -join ',') newPid=$newPid"
}
finally {
    if (@(Get-MuxdProcesses).Count -eq 0) {
        try {
            Start-ScheduledTask -TaskName $taskName
            Write-RestartLog "finally recovery start issued"
        }
        catch {
            Write-RestartLog "finally recovery start failed: $($_.Exception.Message)"
        }
    }
    elseif (-not $restartStarted) {
        Write-RestartLog "restart command ended with muxd already running"
    }
}
