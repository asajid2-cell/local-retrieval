# Creates a double-clickable shortcut to the built app. Invoked from the .csproj
# as an AfterTargets="Build" step so the link always points at the freshest build.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Out
)
$ErrorActionPreference = 'Stop'
try {
    if (-not (Test-Path -LiteralPath $Exe)) {
        Write-Host "make-shortcut: app exe not found yet, skipping ($Exe)"
        exit 0
    }
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($Out)
    $sc.TargetPath       = $Exe
    $sc.WorkingDirectory = (Split-Path -Parent $Exe)
    $sc.IconLocation     = "$Exe,0"
    $sc.Description       = "CodexLocalRetrieval - Codex/Claude chat hub"
    $sc.Save()
    Write-Host "make-shortcut: wrote $Out"
}
catch {
    # Never fail the build over a convenience shortcut.
    Write-Host "make-shortcut: skipped ($($_.Exception.Message))"
}
exit 0
