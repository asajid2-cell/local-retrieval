# Keeps harmonizerlabs.cc/remote connected to this PC.
#
# nginx on the VPS proxies /remote/ to 127.0.0.1:8765. This script publishes
# this PC's local server there via reverse SSH and restarts the tunnel if ssh exits.
$ErrorActionPreference = 'Stop'

$target = if ($env:CLR_REMOTE_TUNNEL_TARGET) { $env:CLR_REMOTE_TUNNEL_TARGET } else { 'harmonizer@192.168.1.142' }
$port = if ($env:CLR_REMOTE_PORT) { $env:CLR_REMOTE_PORT } else { '8765' }
$portNumber = 0
if (-not [int]::TryParse($port, [ref]$portNumber) -or $portNumber -lt 1024 -or $portNumber -gt 65535) { throw 'Invalid remote tunnel port' }
if ($target -notmatch '^(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9.-]*$') { throw 'Invalid SSH destination' }
$port = [string]$portNumber
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($env:MUXD_PROFILE -and $env:MUXD_PROFILE -ne 'production') {
    if ($env:MUXD_PROFILE -ne 'test' -or $portNumber -in @(7682,7699,8765) -or -not $env:MUXD_STATE_ROOT -or -not [IO.Path]::IsPathRooted($env:MUXD_STATE_ROOT)) { throw 'Isolated tunnel configuration required' }
    if (-not $env:CLR_REMOTE_TUNNEL_TARGET) { throw 'Isolated tunnel requires explicit SSH destination' }
    foreach ($reservedPort in @($env:MUXD_CONTROL_PORT, $env:CLR_RELAY_PORT)) {
        if ($reservedPort -and $portNumber -eq [int]$reservedPort) { throw 'Isolated tunnel port overlaps another profile service' }
    }
    if ($env:MUXD_PRINCIPAL_INSTANCE_ID -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'Isolated tunnel principal identity required' }
    $here = Join-Path $env:MUXD_STATE_ROOT 'logs'
    [IO.Directory]::CreateDirectory($here) | Out-Null
}
$outLog = Join-Path $here 'tunnel.log'
$errLog = Join-Path $here 'tunnel.err.log'
$ErrorActionPreference = 'Continue'

function Write-TunnelLog([string]$message) {
    $stamp = (Get-Date).ToString('o')
    Add-Content -LiteralPath $outLog -Value "[$stamp] $message"
}

function Test-LocalServer {
    try {
        $health = Invoke-RestMethod "http://127.0.0.1:$port/healthz" -TimeoutSec 2
        if ($health.ok -ne $true -or $health.service -ne 'codex-local-retrieval') { return $false }
        if ($env:MUXD_PROFILE -eq 'test') {
            return $health.profile -eq 'test' -and $health.principalInstanceId -eq $env:MUXD_PRINCIPAL_INSTANCE_ID
        }
        return $true
    }
    catch { return $false }
}

function Invoke-RemoteCheck([string]$remoteCommand) {
    & ssh.exe -n -o BatchMode=yes -o ConnectTimeout=8 -o ConnectionAttempts=1 -o ServerAliveInterval=5 -o ServerAliveCountMax=1 $target $remoteCommand *> $null
    return $LASTEXITCODE -eq 0
}

function Test-RemoteTunnel {
    if ($env:MUXD_PROFILE -eq 'test') {
        $identity = $env:MUXD_PRINCIPAL_INSTANCE_ID
        if ($identity -notmatch '^[A-Za-z0-9._-]{1,128}$') { return $false }
        $check = "import json,sys; h=json.load(sys.stdin); sys.exit(0 if h.get('ok') is True and h.get('service')=='codex-local-retrieval' and h.get('profile')=='test' and h.get('principalInstanceId')=='$identity' else 1)"
        return Invoke-RemoteCheck "curl -fsS -m 3 http://127.0.0.1:$port/healthz | python3 -c `"$check`""
    }
    return Invoke-RemoteCheck "curl -fsS -m 3 http://127.0.0.1:$port/healthz >/dev/null"
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

    Write-TunnelLog "starting ssh reverse tunnel"
    & ssh.exe `
        -N `
        -R "127.0.0.1:$port`:127.0.0.1:$port" `
        -o ExitOnForwardFailure=yes `
        -o BatchMode=yes `
        -o ConnectTimeout=8 `
        -o ConnectionAttempts=1 `
        -o ServerAliveInterval=30 `
        -o ServerAliveCountMax=3 `
        $target `
        >> $outLog 2>> $errLog

    $code = $LASTEXITCODE
    Write-TunnelLog "ssh exited with code $code; restarting soon"
    if ($code -eq 255) { Write-TunnelLog 'SSH refused the tunnel; leaving any existing remote listener untouched.' }
    Start-Sleep -Seconds 5
}
