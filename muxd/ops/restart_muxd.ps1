[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$python = "C:\Python311\python.exe"
$taskName = "MuxdSessionHost"
$logPath = Join-Path $root "muxd-restart.log"

function Write-RestartLog([string]$Message) {
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

function Get-HostedSessions {
    $code = @"
import asyncio
import json
import sys
sys.path.insert(0, r'$root')
import muxctl
print(json.dumps(asyncio.run(muxctl.fetch_list())))
"@
    $previousAutostart = $env:MUXCTL_AUTOSTART
    try {
        $env:MUXCTL_AUTOSTART = "0"
        $raw = & $python -c $code 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "muxd preflight failed: $($raw -join ' ')"
        }
        @($raw | ConvertFrom-Json)
    }
    finally {
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

$sessions = @(Get-HostedSessions)
$active = @($sessions | Where-Object { @($_.alive) -contains $true })
if ($active.Count -gt 0) {
    $names = ($active | ForEach-Object { $_.name }) -join ", "
    throw "refusing muxd restart while active sessions exist: $names"
}

Write-RestartLog "preflight passed; activeSessions=0 checkOnly=$CheckOnly"
if ($CheckOnly) {
    return
}

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
foreach ($name in @("sessions.json", "live-tabs.json")) {
    $source = Join-Path $root $name
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
            & $python (Join-Path $root "muxctl.py") status *> $null
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
