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
$ErrorActionPreference = "Stop"
$keysafe = Join-Path $env:USERPROFILE ".claude\skills\keysafe\scripts\keysafe.ps1"
$child = "Start-Process -Wait -FilePath 'C:\Python311\pythonw.exe' -ArgumentList 'C:\Users\Ahmed\muxd\muxd.py'"
& $keysafe run mux-host-token -EnvVar MUX_HOST_TOKEN -Command $child
exit $LASTEXITCODE
