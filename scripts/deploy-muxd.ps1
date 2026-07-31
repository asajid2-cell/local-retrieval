# Deploy muxd/ from the monorepo to the live runtime (C:\Users\Ahmed\muxd) and restart it.
# The scheduled task MuxdSessionHostRestart does the safe swap (preflight refuses if sessions are live).
$src = Join-Path $PSScriptRoot '..\muxd'
$dst = 'C:\Users\Ahmed\muxd'
$files = @('muxd.py','muxctl.py','host_input_intent.py')   # runtime code only — never state (live-tabs.json, logs, env)
foreach ($f in $files) { Copy-Item (Join-Path $src $f) (Join-Path $dst $f) -Force; Write-Host "deployed $f" }
foreach ($f in $files) {
  python -m py_compile (Join-Path $dst $f)
  if ($LASTEXITCODE -ne 0) { Write-Error "$f does not compile — NOT restarting"; exit 1 }
}
# PREFLIGHT-LOCAL-MODULE-MANIFEST — py_compile never resolves imports, so a deployed entrypoint can
# compile fine and still die at startup on a sibling module that was left out of $files. Parse the
# DEPLOYED sources and refuse if any local sibling module they import is missing from $dst.
# (Never `import muxd` here — muxd.py has module-level side effects.)
$preflight = @'
import ast, os, sys
src, dst = sys.argv[1], sys.argv[2]
missing = set()
for entry in ("muxd.py", "muxctl.py"):
    path = os.path.join(dst, entry)
    if not os.path.exists(path):
        missing.add(entry)
        continue
    with open(path, "r", encoding="utf-8") as fh:
        tree = ast.parse(fh.read(), filename=path)
    names = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            names.update(a.name.split(".")[0] for a in node.names)
        elif isinstance(node, ast.ImportFrom):
            if node.level == 0 and node.module:
                names.add(node.module.split(".")[0])
    for name in names:
        # local sibling module (exists in the repo next to the entrypoint), not stdlib/site-packages
        if os.path.exists(os.path.join(src, name + ".py")) and not os.path.exists(os.path.join(dst, name + ".py")):
            missing.add(name + ".py")
if missing:
    print("missing local modules in %s: %s" % (dst, ", ".join(sorted(missing))))
    sys.exit(1)
'@
$preflight | python - $src $dst
if ($LASTEXITCODE -ne 0) { Write-Error 'deployed runtime is missing local modules — NOT restarting'; exit 1 }
Start-ScheduledTask -TaskName MuxdSessionHostRestart
Write-Host 'restart requested via MuxdSessionHostRestart (check muxd-restart.log)'
