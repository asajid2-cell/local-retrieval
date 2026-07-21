# Deploy muxd/ from the monorepo to the live runtime (C:\Users\Ahmed\muxd) and restart it.
# The scheduled task MuxdSessionHostRestart does the safe swap (preflight refuses if sessions are live).
$src = Join-Path $PSScriptRoot '..\muxd'
$dst = 'C:\Users\Ahmed\muxd'
$files = @('muxd.py','muxctl.py')   # runtime code only — never state (live-tabs.json, logs, env)
foreach ($f in $files) { Copy-Item (Join-Path $src $f) (Join-Path $dst $f) -Force; Write-Host "deployed $f" }
python -m py_compile (Join-Path $dst 'muxd.py'); if ($LASTEXITCODE -ne 0) { Write-Error 'muxd.py does not compile — NOT restarting'; exit 1 }
Start-ScheduledTask -TaskName MuxdSessionHostRestart
Write-Host 'restart requested via MuxdSessionHostRestart (check muxd-restart.log)'
