# Gate a3 - narrow render on filter.
#
# ApplyFilters() runs on the trailing edge of every search burst. It is allowed to touch the session
# list, the tag strip and (on a real selection change) the transcript pane. It is NOT allowed to rebuild
# the whole screen: RenderCurrent() fans out to whatever page is open, and on "Running" that means
# RenderRunningPage() - a host probe / SSH spawn per keystroke. This gate reads the ApplyFilters method
# body out of MainPage.Tags.cs and fails if either call reappears inside it.
#
# Exit 0 = clean. Exit 1 = a forbidden call is back (or the method/file moved and the gate went blind).
#
# ASCII-only on purpose: this file is run by Windows PowerShell 5.1 via -File, which decodes a BOM-less
# script with the ANSI codepage, so any non-ASCII byte here would corrupt the parse.

$ErrorActionPreference = 'Stop'

$appRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$target = Join-Path $appRoot 'native\CodexLocalRetrieval.Native\MainPage.Tags.cs'

if (-not (Test-Path -LiteralPath $target)) {
    Write-Host "[FAIL] a3_narrow_filter: target not found: $target"
    exit 1
}

$start = Select-String -LiteralPath $target -Pattern '^\s*private\s+void\s+ApplyFilters\s*\(\s*\)' |
    Select-Object -First 1
if (-not $start) {
    Write-Host "[FAIL] a3_narrow_filter: ApplyFilters not found in $target - the gate cannot assert anything."
    exit 1
}

# Brace-walk the method body so the assertion is scoped to ApplyFilters and nothing else in the file.
$lines = @(Get-Content -LiteralPath $target)
$depth = 0
$opened = $false
$body = New-Object System.Collections.Generic.List[string]
for ($i = $start.LineNumber - 1; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]
    $body.Add($line) | Out-Null
    $depth += ([regex]::Matches($line, '\{')).Count
    if ($depth -gt 0) { $opened = $true }
    $depth -= ([regex]::Matches($line, '\}')).Count
    if ($opened -and $depth -le 0) { break }
}

if (-not $opened) {
    Write-Host "[FAIL] a3_narrow_filter: could not delimit the ApplyFilters body in $target."
    exit 1
}

$hits = @($body | Select-String -Pattern 'RenderCurrent\(|RenderRunningPage\(')
if ($hits.Count -gt 0) {
    Write-Host "[FAIL] a3_narrow_filter: ApplyFilters rebuilds the whole screen. Forbidden call(s): $($hits.Count)"
    foreach ($hit in $hits) {
        Write-Host ("    line {0}: {1}" -f ($start.LineNumber - 1 + $hit.LineNumber), $hit.Line.Trim())
    }
    Write-Host "  Filtering must update the session list + tag strip only; re-render the transcript pane"
    Write-Host "  solely when the selection actually changes."
    exit 1
}

$bodyLines = $body.Count
Write-Host "[PASS] a3_narrow_filter: ApplyFilters body at ${target}:$($start.LineNumber) ($bodyLines lines) calls neither RenderCurrent( nor RenderRunningPage(."
exit 0
