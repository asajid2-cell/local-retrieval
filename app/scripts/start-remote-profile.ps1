param(
    [Parameter(Mandatory=$true)][string]$EnvFile,
    [Parameter(Mandatory=$true)][string]$ServerExe,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($EnvFile) -or -not (Test-Path -LiteralPath $EnvFile -PathType Leaf)) { throw 'Absolute profile env file required' }
if (-not [IO.Path]::IsPathRooted($ServerExe) -or -not (Test-Path -LiteralPath $ServerExe -PathType Leaf)) { throw 'Published server executable required' }
$profileSettings = @{}
foreach ($line in [IO.File]::ReadLines($EnvFile)) {
    $text = $line.Trim()
    if (-not $text -or $text.StartsWith('#')) { continue }
    $separator = $text.IndexOf('=')
    if ($separator -lt 1) { continue }
    $key = $text.Substring(0,$separator).Trim()
    $value = $text.Substring($separator+1).Trim()
    if ($profileSettings.ContainsKey($key)) { throw "Duplicate profile setting: $key" }
    $profileSettings[$key] = $value
    [Environment]::SetEnvironmentVariable($key,$value,'Process')
}
$env:MUXD_ENV_FILE = $EnvFile
if ($profileSettings.ContainsKey('CLR_REMOTE_BRIDGE') -and $profileSettings['CLR_REMOTE_BRIDGE'] -ne '1') { throw 'The isolated supervisor requires the remote bridge enabled' }
$env:CLR_REMOTE_BRIDGE = '1'
if ($env:MUXD_PROFILE -ne 'test') { throw 'This launcher requires MUXD_PROFILE=test' }
foreach ($key in @('MUXD_PROFILE','MUXD_STATE_ROOT','MUXD_CONTROL_PORT','MUXD_TASK_NAME','MUXD_PRINCIPAL_INSTANCE_ID','CLR_RELAY_PORT','CLR_REMOTE_PORT','CLR_REMOTE_STORE','CLR_REMOTE_TUNNEL_TARGET','CLR_REMOTE_BRIDGE_TARGET','CLR_REMOTE_BIND','CLR_REMOTE_SYNC','CLR_REMOTE_HLAUTH')) {
    if (-not $profileSettings.ContainsKey($key) -or -not $profileSettings[$key]) { throw "Missing explicit profile setting: $key" }
}
foreach ($key in @('CLR_REMOTE_TUNNEL_TARGET','CLR_REMOTE_BRIDGE_TARGET')) {
    if ($profileSettings[$key] -notmatch '^(?:[A-Za-z0-9_][A-Za-z0-9_.-]*@)?[A-Za-z0-9][A-Za-z0-9.-]*$') { throw "Invalid SSH destination: $key" }
}
$authKey = if ($env:CLR_REMOTE_HLAUTH -eq '1') { 'CLR_REMOTE_HLAUTH_BASE' } else { 'CLR_REMOTE_TOKEN' }
if (-not $profileSettings.ContainsKey($authKey) -or -not $profileSettings[$authKey]) { throw "Missing explicit authentication setting: $authKey" }
if ($env:CLR_REMOTE_BIND -ne '127.0.0.1' -or $env:CLR_REMOTE_SYNC -notin @('0','1')) { throw 'Test server requires loopback binding and explicit source sync 0 or 1' }
foreach ($key in @('MUXD_CONTROL_PORT','CLR_RELAY_PORT','CLR_REMOTE_PORT')) {
    $port = 0
    if (-not [int]::TryParse([Environment]::GetEnvironmentVariable($key),[ref]$port) -or $port -lt 1024 -or $port -gt 65535 -or $port -in @(7682,7699,8765)) { throw "Unsafe test port: $key" }
}
if ($env:MUXD_TASK_NAME -eq 'MuxdSessionHost') { throw 'Production task name refused' }
if ($env:MUXD_PRINCIPAL_INSTANCE_ID -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'Invalid profile principal instance identity' }
$ports = @($env:MUXD_CONTROL_PORT,$env:CLR_RELAY_PORT,$env:CLR_REMOTE_PORT) | ForEach-Object { [int]$_ }
if (@($ports | Select-Object -Unique).Count -ne 3) { throw 'Control, relay and archive ports must be distinct' }
if ($env:CLR_REMOTE_HLAUTH -eq '1' -and -not $env:CLR_REMOTE_HLAUTH_BASE) { throw 'SSO authentication base required' }
$stateRoot = [IO.Path]::GetFullPath($env:MUXD_STATE_ROOT).TrimEnd('\')
$productionRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'CodexArchiveRemote')).TrimEnd('\')
if (-not [IO.Path]::IsPathRooted($env:MUXD_STATE_ROOT) -or $stateRoot -eq $productionRoot -or $stateRoot.StartsWith($productionRoot+'\',[StringComparison]::OrdinalIgnoreCase) -or $productionRoot.StartsWith($stateRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'State root overlaps production' }
if (-not [IO.Path]::IsPathRooted($env:CLR_REMOTE_STORE) -or -not [IO.Path]::GetFullPath($env:CLR_REMOTE_STORE).StartsWith($stateRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive store must be inside isolated state root' }
if ($env:CLR_REMOTE_HLAUTH -ne '1' -and (-not $env:CLR_REMOTE_TOKEN -or $env:CLR_REMOTE_TOKEN.Length -lt 32)) { throw 'Remote authentication must be configured' }
foreach ($key in @('MUXD_PYTHON','MUXD_RUNTIME_ROOT')) {
    if (-not $profileSettings.ContainsKey($key) -or -not [IO.Path]::IsPathRooted($profileSettings[$key])) { throw "Missing explicit absolute runtime setting: $key" }
}
foreach ($file in @($env:MUXD_PYTHON,(Join-Path $env:MUXD_RUNTIME_ROOT 'muxd.py'),(Join-Path $PSScriptRoot 'remote-tunnel.ps1'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required runtime file missing: $file" }
}
if ($ValidateOnly) { Write-Output 'Test runtime launcher configuration valid'; return }
$hash = [Security.Cryptography.SHA256]::Create()
try { $identity = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($stateRoot.ToLowerInvariant()))).Replace('-','') }
finally { $hash.Dispose() }
$mutex = New-Object Threading.Mutex($false, ('Local\CodexArchiveRemote-test-' + $identity))
$owned = $false
try { $owned = $mutex.WaitOne(0) }
catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) { $mutex.Dispose(); throw 'An isolated supervisor already owns this state root' }
try {
    & $ServerExe '--supervise-profile' (Join-Path $PSScriptRoot 'remote-tunnel.ps1')
    if ($LASTEXITCODE -ne 0) { throw "Contained supervisor exited with code $LASTEXITCODE" }
} finally {
    try { if ($owned) { $mutex.ReleaseMutex() } }
    finally { $mutex.Dispose() }
}
