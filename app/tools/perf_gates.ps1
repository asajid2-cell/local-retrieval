# Perf gates -- builds Release, runs the TestCategory=Perf probes, and folds the numbers they emit into
# app/artifacts/perf/<tag>/results.json.
#
# Unlike tools/run_gates.ps1 this script is SELF-CONTAINED: it never touches the relay or muxd checkouts and
# runs no source-provenance gate. A perf baseline has to be reproducible from this repo alone, on any machine,
# with a dirty working tree -- otherwise nobody records one.
#
# Numbers travel test -> gates through CLR_PERF_RESULTS (see PerfRecord in Perf/PerfCorpus.cs): the tests
# append one JSON line per named measurement, this script points that variable at a per-tag file and folds it.
param(
    [Parameter(Mandatory = $true)][string]$Tag
)

$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $root 'native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj'
$artifactDir = Join-Path $root "artifacts\perf\$Tag"
$resultsPath = Join-Path $artifactDir 'results.json'
$measurementsPath = Join-Path $artifactDir 'measurements.jsonl'
New-Item -ItemType Directory -Force $artifactDir | Out-Null

# Start from an empty sink so a tag's numbers are always from this run, never accreted across runs.
if (Test-Path $measurementsPath) { Remove-Item $measurementsPath -Force }

$gates = [ordered]@{}
$hardFail = $false

function Run-Gate {
    param(
        [string]$Name,
        [scriptblock]$Command,
        [string]$PositiveMarker
    )
    $log = Join-Path $artifactDir ($Name + '.log')
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $Command *> $log
    $exit = $LASTEXITCODE
    $sw.Stop()
    $marker = $true
    if ($PositiveMarker) {
        $marker = [bool](Select-String -Path $log -SimpleMatch $PositiveMarker -Quiet)
    }
    $ok = ($exit -eq 0) -and $marker
    $gates[$Name] = [ordered]@{
        ok = $ok
        exit = $exit
        wallMs = [Math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        log = $log
    }
    if (-not $ok) { $script:hardFail = $true }
    $status = 'FAIL'; if ($ok) { $status = 'PASS' }
    Write-Host "[$status] $Name exit=$exit wall=$([Math]::Round($sw.Elapsed.TotalSeconds, 1))s log=$log"
}

Set-Location $root

Run-Gate 'build' {
    dotnet build native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj `
        -c Release -m:1 -p:UseSharedCompilation=false
} 'Build succeeded.'

if ($gates['build'].ok) {
    $previousSink = $env:CLR_PERF_RESULTS
    $env:CLR_PERF_RESULTS = $measurementsPath
    try {
        # --filter on the command line overrides default.runsettings' TestCaseFilter, so this really is
        # Perf-only. --blame-hang catches a probe that stops finishing, which is itself a perf signal.
        Run-Gate 'perf-probes' {
            dotnet test native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj `
                -c Release --no-build --logger 'console;verbosity=minimal' `
                --filter 'TestCategory=Perf' `
                --blame-hang --blame-hang-timeout 3m --blame-hang-dump-type mini
        } 'Passed!'
    }
    finally {
        $env:CLR_PERF_RESULTS = $previousSink
    }
}

# ---------------------------------------------------------------- fold measurements into results.json

$measurements = [ordered]@{}
$counters = [ordered]@{}
$malformed = 0
if (Test-Path $measurementsPath) {
    foreach ($line in (Get-Content $measurementsPath)) {
        if (-not $line.Trim()) { continue }
        try { $entry = $line | ConvertFrom-Json } catch { $malformed++; continue }
        if (-not $entry.name) { $malformed++; continue }
        $slot = [ordered]@{ value = $entry.value; unit = $entry.unit; at = $entry.at }
        if ($entry.kind -eq 'counter') { $counters[[string]$entry.name] = $slot }
        else { $measurements[[string]$entry.name] = $slot }
    }
}

$appHead = (& git -C $root log -1 --pretty='%H %s' 2>$null)
if (-not $appHead) { $appHead = 'unknown' }
$dotnetVersion = (& dotnet --version 2>$null)

# A baseline number without the machine that produced it is not comparable to anything.
$results = [ordered]@{
    tag = $Tag
    recordedAt = (Get-Date).ToUniversalTime().ToString('o')
    appHead = $appHead
    machine = [ordered]@{
        host = $env:COMPUTERNAME
        os = [Environment]::OSVersion.VersionString
        processors = [Environment]::ProcessorCount
        dotnet = "$dotnetVersion"
        configuration = 'Release'
    }
    gates = $gates
    measurements = $measurements
    counters = $counters
    measurementCount = @($measurements.Keys).Count
    malformedLines = $malformed
    measurementsFile = $measurementsPath
}

$results | ConvertTo-Json -Depth 8 | Out-File -FilePath $resultsPath -Encoding utf8
Write-Host "Perf results written to $resultsPath ($(@($measurements.Keys).Count) measurements, $(@($counters.Keys).Count) counters)"

if ($hardFail) {
    Write-Host 'RED - perf gates failed'
    exit 1
}
Write-Host 'GREEN - perf gates passed'
exit 0
