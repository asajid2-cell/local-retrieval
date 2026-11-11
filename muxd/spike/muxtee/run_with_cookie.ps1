# Run a node probe with MUX_COOKIE taken from the keysafe-injected env var.
# keysafe names the child env var KEYSAFE_<NAME-UPPER-WITH-UNDERSCORES>.
# Usage: keysafe.ps1 run hl-owner-session -Command "powershell -File run_with_cookie.ps1 -Script verify_deployed_threads.cjs"
param([Parameter(Mandatory=$true)][string]$Script)

$name = (Get-Item Env:KEYSAFE_HL_OWNER_SESSION -ErrorAction SilentlyContinue).Value
if (-not $name) {
  Write-Error 'KEYSAFE_HL_OWNER_SESSION was not injected; run this through keysafe run hl-owner-session'
  exit 2
}
$env:MUX_COOKIE = $name
node $Script
exit $LASTEXITCODE
