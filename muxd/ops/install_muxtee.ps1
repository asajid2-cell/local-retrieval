# install_muxtee.ps1 - place the muxtee helper where the PC can find it and the WT profile can start it.
#
# muxtee IS a terminal's child: a tab launches it, it owns its own inner ConPTY, and it mirrors the tab
# to muxd. Installing it is therefore just "put the three files in one directory and point a profile at
# the exe" - there is no service, no scheduled task, no token. That is the whole contrast with
# install_muxd_tasks.ps1, which registers tasks.
#
# Section 5 is the load-bearing detail: conpty.dll launches a console host per pty and finds it by
# scanning its OWN directory for `x64\OpenConsole.exe`, then `arm64\`, `x86\`, and only then the inbox
# `\conhost.exe`. Ship conpty.dll without the x64 subdir and every tab silently runs on the inbox host
# instead - so this script refuses to finish without both, and muxtee's log line reports which host it
# actually got (`ptyHost=`).
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$InstallDir = $(if ($env:MUXTEE_INSTALL_DIR) { $env:MUXTEE_INSTALL_DIR } else { Join-Path $env:USERPROFILE "muxtee" })
)

$ErrorActionPreference = "Stop"

# Default source: the Release publish output next to this repo's MuxTee project. A caller can point at
# a built tarball copy instead, which is what a box-to-box deploy would do.
if (-not $Source) {
    $repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # muxd/ops -> muxd -> repo
    $Source = Join-Path $repoRoot "app\native\MuxTee\bin\Release\net10.0-windows\win-x64\publish"
}
if (-not (Test-Path -LiteralPath $Source)) {
    throw "publish output not found: $Source. Run: dotnet publish -c Release -r win-x64 --self-contained false in app/native/MuxTee"
}

$files = @(
    "muxtee.exe",
    "muxtee.dll",
    "muxtee.deps.json",
    "muxtee.runtimeconfig.json",
    "conpty.dll",
    "x64\OpenConsole.exe"
)
foreach ($f in $files) {
    $p = Join-Path $Source $f
    if (-not (Test-Path -LiteralPath $p)) { throw "missing required artifact: $p" }
}

$full = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Path $full -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $full "x64") -Force | Out-Null

foreach ($f in $files) {
    $dest = Join-Path $full $f
    $destDir = Split-Path -Parent $dest
    New-Item -ItemType Directory -Path $destDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $Source $f) -Destination $dest -Force
}
# muxtee.pdb is optional; copy it when present so a local crash dump can be symbolised.
$pdb = Join-Path $Source "muxtee.pdb"
if (Test-Path -LiteralPath $pdb) { Copy-Item -LiteralPath $pdb -Destination (Join-Path $full "muxtee.pdb") -Force }

$exe = Join-Path $full "muxtee.exe"
Write-Output "Installed muxtee to $full"
Write-Output "  exe:  $exe"
Write-Output "  host: $(Join-Path $full 'x64\OpenConsole.exe')"

# Prove the runtime host is present and where conpty.dll looks for it. A missing host would still run,
# on the inbox conpty - the one thing section 5 says we must not accept - so this is a hard check.
if (-not (Test-Path -LiteralPath (Join-Path $full "x64\OpenConsole.exe"))) {
    throw "bundled pty host not staged; muxtee would fall back to inbox conhost.exe"
}

# Which shell the WT profile launches under muxtee. Prefer pwsh when installed, else Windows
# PowerShell 5.1 - the same choice the profile itself makes, so the printed line is copy-pasteable.
$pwshCmd = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if ($pwshCmd) { $shell = "pwsh.exe" } else { $shell = "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" }
Write-Output ""
Write-Output "Windows Terminal profile command line:"
Write-Output "  `"$exe`" -- $shell"
Write-Output ""
Write-Output "Escape hatches (both give a plain, unmuxed tab):"
Write-Output "  - set MUXTEE_DISABLE=1"
Write-Output "  - use a profile that launches the shell directly, e.g. 'PowerShell (no mux)'"
