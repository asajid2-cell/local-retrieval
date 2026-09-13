# launch_muxd.ps1 - the SINGLE launch entrypoint for muxd, wrapped so MUX_HOST_TOKEN is injected
# from the keysafe DPAPI vault instead of living plaintext in muxd.env.
#
# Every relaunch path funnels through the MuxdSessionHost scheduled task (restart_muxd.ps1 and
# watch_muxd.ps1 both call Start-ScheduledTask MuxdSessionHost), and that task runs this file via
# launch_muxd.vbs (hidden). So wrapping here covers boot, manual restart, and watchdog recovery.
#
# keysafe 'run' sets MUX_HOST_TOKEN on its own process; the child inherits it. pythonw is a
# GUI-subsystem exe that PowerShell would NOT wait on, which would make the scheduled task flip to
# 'Ready' while muxd runs orphaned -- so Start-Process -Wait keeps the whole chain attached and the
# task 'Running', exactly as the old direct-pythonw action behaved.
[CmdletBinding()]
param(
    [string]$Profile = $(if ($env:MUXD_PROFILE) { $env:MUXD_PROFILE } else { "production" }),
    [string]$RuntimeRoot = ""
)
$ErrorActionPreference = "Stop"
$root = if ($RuntimeRoot) { [IO.Path]::GetFullPath($RuntimeRoot) } else { Split-Path -Parent $PSScriptRoot }
$env:MUXD_PROFILE = $Profile
if ($Profile -ne "production") {
    $env:MUXD_RUNTIME_ROOT = $root
    $env:MUXD_ENV_FILE = Join-Path $root "muxd.env"
}
$profileArgs = @("--profile", $Profile)
$python = if ($Profile -eq "production") { "C:\Python311\pythonw.exe" } else { "C:\Python311\python.exe" }
$entry = Join-Path $root "muxd.py"
if (-not (Test-Path -LiteralPath $entry)) { throw "muxd entrypoint not found: $entry" }
# Profile-scoped launches do not consult production keysafe/task/path resources. The profile
# contract resolves MUXD_TOKEN_SOURCE and refuses missing isolation before the child starts.
if ($Profile -eq "production") {
    $quotedPython = $python.Replace("'", "''")
    $quotedArguments = ('"' + $entry + '" --profile production').Replace("'", "''")
    $child = "Start-Process -Wait -FilePath '$quotedPython' -ArgumentList '$quotedArguments'"
    $keysafe = Join-Path $env:USERPROFILE ".claude\skills\keysafe\scripts\keysafe.ps1"
    & $keysafe run mux-host-token -EnvVar MUX_HOST_TOKEN -Command $child
    exit $LASTEXITCODE
}
& $python $entry @profileArgs
exit $LASTEXITCODE
