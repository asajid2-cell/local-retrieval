param(
    [string]$Tag = 'r46'
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$gateScript = Join-Path $PSScriptRoot 'perf_gates.ps1'
$resultsPath = Join-Path $root "artifacts\perf\$Tag\results.json"

function Fail-Assertion {
    param([string]$Name, [string]$Detail)
    Write-Error "ASSERTION FAILED: $Name - $Detail"
    exit 1
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $gateScript -Tag $Tag
$gateExit = $LASTEXITCODE
if ($gateExit -ne 0) {
    Fail-Assertion 'perf-gates-child' "perf_gates.ps1 exited with code $gateExit."
}

if (-not (Test-Path -LiteralPath $resultsPath)) {
    Fail-Assertion 'results-artifact' "missing $resultsPath."
}

$results = Get-Content -LiteralPath $resultsPath -Raw | ConvertFrom-Json

function Get-PropertyValue {
    param(
        [object]$Object,
        [string]$PropertyName,
        [string]$AssertionName
    )
    if ($null -eq $Object) {
        Fail-Assertion $AssertionName "parent object is absent."
    }
    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        Fail-Assertion $AssertionName "property '$PropertyName' is absent."
    }
    return $property.Value
}

function Get-Measurement {
    param([string]$Name)
    $entry = Get-PropertyValue $results.measurements $Name "measurement-$Name"
    return Get-PropertyValue $entry 'value' "measurement-$Name-value"
}

function Get-Counter {
    param([string]$Name)
    $entry = Get-PropertyValue $results.counters $Name "counter-$Name"
    return Get-PropertyValue $entry 'value' "counter-$Name-value"
}

$quietMs = [double](Get-Measurement 'ledger.readForSession.quiet.ms')
$quietBytes = [double](Get-Measurement 'ledger.readForSession.quiet.bytesRead')
$backfillMs = [double](Get-Measurement 'ledger.readForSession.backfill.ms')
$backfillBytes = [double](Get-Measurement 'ledger.readForSession.backfill.bytesRead')
$ledgerBytesRead = [double](Get-Counter 'ledger.readForSession.ledgerBytesRead')

if (-not ($quietMs -gt 0 -and $quietMs -lt 100)) {
    Fail-Assertion 'quiet-ms-bounds' "value was $quietMs; expected > 0 and < 100."
}
if (-not ($quietBytes -gt 0 -and $quietBytes -lt 262144)) {
    Fail-Assertion 'quiet-bytes-bounds' "value was $quietBytes; expected > 0 and < 262144."
}
if (-not ($backfillBytes -ge (10 * $quietBytes))) {
    Fail-Assertion 'backfill-byte-ratio' "backfill bytes $backfillBytes was less than 10 times quiet bytes $quietBytes."
}
if (-not ($ledgerBytesRead -gt 0)) {
    Fail-Assertion 'ledger-bytes-counter-positive' "value was $ledgerBytesRead; expected > 0."
}

$ratio = $backfillBytes / $quietBytes
Write-Host "ledger.readForSession.quiet.ms = $quietMs"
Write-Host "ledger.readForSession.quiet.bytesRead = $quietBytes"
Write-Host "ledger.readForSession.backfill.ms = $backfillMs"
Write-Host "ledger.readForSession.backfill.bytesRead = $backfillBytes"
Write-Host "ledger.readForSession.ledgerBytesRead = $ledgerBytesRead"
Write-Host "backfill/quiet byte ratio = $ratio"

$baselinePath = Join-Path $root 'artifacts\perf\p0\results.json'
if (-not (Test-Path -LiteralPath $baselinePath)) {
    Write-Host "Baseline artifact $baselinePath is absent; no comparison available."
}
else {
    $baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
    $baselineMeasurements = if ($null -ne $baseline) { $baseline.PSObject.Properties['measurements'] } else { $null }
    $baselineNames = @(
        'ledger.readForSession.quiet.ms',
        'ledger.readForSession.quiet.bytesRead',
        'ledger.readForSession.backfill.ms',
        'ledger.readForSession.backfill.bytesRead'
    )
    $comparable = $true
    foreach ($name in $baselineNames) {
        if ($null -eq $baselineMeasurements -or $null -eq $baselineMeasurements.Value.PSObject.Properties[$name]) {
            $comparable = $false
            break
        }
    }
    if (-not $comparable) {
        Write-Host "Baseline artifact $baselinePath exists, but comparable ledger keys are absent."
    }
    else {
        Write-Host "Baseline comparison ($baselinePath):"
        foreach ($name in $baselineNames) {
            $current = Get-Measurement $name
            $baselineEntry = $baselineMeasurements.Value.PSObject.Properties[$name].Value
            $baselineValue = $baselineEntry.PSObject.Properties['value'].Value
            Write-Host "$name = $current; baseline = $baselineValue"
        }
    }
}

exit 0
