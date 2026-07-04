# Keeps harmonizerlabs.cc/remote connected to this PC.
#
# nginx on the VPS proxies /remote/ to 127.0.0.1:8765. This script publishes
# this PC's local server there via reverse SSH and restarts the tunnel if ssh exits.
$ErrorActionPreference = 'SilentlyContinue'

$target = if ($env:CLR_REMOTE_TUNNEL_TARGET) { $env:CLR_REMOTE_TUNNEL_TARGET } else { 'harmonizer@192.168.1.142' }
$port = if ($env:CLR_REMOTE_PORT) { $env:CLR_REMOTE_PORT } else { '8765' }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$outLog = Join-Path $here 'tunnel.log'
$errLog = Join-Path $here 'tunnel.err.log'

function Write-TunnelLog([string]$message) {
    $stamp = (Get-Date).ToString('o')
    Add-Content -LiteralPath $outLog -Value "[$stamp] $message"
}

function Test-LocalServer {
    try { return (Invoke-RestMethod "http://127.0.0.1:$port/healthz" -TimeoutSec 2).ok -eq $true }
    catch { return $false }
}

function Invoke-RemoteCheck([string]$remoteCommand) {
    & ssh.exe -n -o BatchMode=yes -o ConnectTimeout=8 -o ConnectionAttempts=1 -o ServerAliveInterval=5 -o ServerAliveCountMax=1 $target $remoteCommand *> $null
    return $LASTEXITCODE -eq 0
}

function Test-RemoteTunnel {
    return Invoke-RemoteCheck "curl -fsS -m 3 http://127.0.0.1:$port/healthz >/dev/null"
}

function Clear-StaleRemoteListener {
    Write-TunnelLog "remote tunnel port is bound but unhealthy; clearing stale VPS listener on 127.0.0.1:$port"
    Invoke-RemoteCheck "sudo -n fuser -k $port/tcp >/dev/null 2>&1 || true" | Out-Null
    Start-Sleep -Seconds 1
}

Write-TunnelLog "supervisor starting for $target, remote 127.0.0.1:$port -> local 127.0.0.1:$port"

while ($true) {
    if (-not (Test-LocalServer)) {
        Write-TunnelLog "local server is not healthy; waiting"
        Start-Sleep -Seconds 5
        continue
    }

    if (Test-RemoteTunnel) {
        Write-TunnelLog "remote reverse tunnel already healthy"
        Start-Sleep -Seconds 15
        continue
    }
    Clear-StaleRemoteListener

    Write-TunnelLog "starting ssh reverse tunnel"
    & ssh.exe `
        -N `
        -R "127.0.0.1:$port`:127.0.0.1:$port" `
        -o ExitOnForwardFailure=yes `
        -o BatchMode=yes `
        -o ServerAliveInterval=30 `
        -o ServerAliveCountMax=3 `
        $target `
        >> $outLog 2>> $errLog

    $code = $LASTEXITCODE
    Write-TunnelLog "ssh exited with code $code; restarting soon"
    if ($code -eq 255 -and -not (Test-RemoteTunnel)) { Clear-StaleRemoteListener }
    Start-Sleep -Seconds 5
}
