# Deploy muxd/ from the monorepo to the live runtime (C:\Users\Ahmed\muxd) and restart it.
# The scheduled task MuxdSessionHostRestart does the safe swap (preflight refuses if sessions are live).
param(
  [ValidateSet('audit','enforce')]
  [string]$AuthzMode = $(if ($env:MUX_AUTHZ_MODE) { $env:MUX_AUTHZ_MODE } else { 'audit' })
)
$src = Join-Path $PSScriptRoot '..\muxd'
$relay = Join-Path $PSScriptRoot '..\relay'
$dst = 'C:\Users\Ahmed\muxd'
$files = @('muxd.py','muxctl.py','host_input_intent.py')   # runtime code only — never state (live-tabs.json, logs, env)

node -e "const c=require(process.argv[1]).createLeaseConduit();const f={t:'i',auth:{principalId:'p',keyId:'k',sig:'x',bodyB64:'eA=='}};const o=c.forwardSignedInput('s',JSON.stringify(f));if(!o||o.t!=='i'||!o.auth||o.auth.sig!=='x')process.exit(1)" (Join-Path $relay 'lease-conduit.js')
if ($LASTEXITCODE -ne 0) { Write-Error 'local relay cannot forward the inputDurable t:i contract - NOT deploying'; exit 1 }

if ($AuthzMode -eq 'enforce') {
  $principalStatus = @'
import sys
sys.path.insert(0, sys.argv[1])
import host_input_intent
try:
    endpoint = host_input_intent.load_principal_endpoint()
except Exception as error:
    print(error)
    raise SystemExit(1)
print(endpoint.summary())
raise SystemExit(0 if endpoint.provisioned() else 1)
'@
  $principalStatus | python - $src
  if ($LASTEXITCODE -ne 0) { Write-Error 'enforce deployment requires a DPAPI-protected provisioned principal - NOT deploying'; exit 1 }
}
$rollback = Join-Path $dst 'rollback\pre-input-durable'
New-Item -ItemType Directory -Path $rollback -Force | Out-Null
foreach ($f in $files) {
  $live = Join-Path $dst $f
  if (Test-Path -LiteralPath $live) { Copy-Item -LiteralPath $live -Destination (Join-Path $rollback $f) -Force }
}
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
