<#
.SYNOPSIS
r.4.5 bounded verifier â€” Release builds + full filtered test suite green.
Contract: Failed==0 && Passed>=507, marker R45-GATE-OK on success.
#>
$ErrorActionPreference = "Continue"

# Locate repo root from $PSScriptRoot (app/tools/gates -> ../../.. -> repo root)
$root = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path

$slnPath    = Join-Path $root "app\CodexLocalRetrieval.sln"
$nativeProj = Join-Path $root "app\native\CodexLocalRetrieval.Native\CodexLocalRetrieval.Native.csproj"
$testsProj  = Join-Path $root "app\native\CodexLocalRetrieval.Native.Tests\CodexLocalRetrieval.Native.Tests.csproj"

$filter = "TestCategory!=RealStore&TestCategory!=LiveCodex&TestCategory!=Perf"

$failReason = @()
$failOutput = @()

function Fail-Step {
    param([string]$StepName, [string]$Output)
    $script:failReason += $StepName
    $script:failOutput += $Output
}

# Step (a): dotnet build app/CodexLocalRetrieval.sln -c Release -m:1 /p:UseSharedCompilation=false
Write-Host "[r45_verify] Step (a) dotnet build solution Release..."
$outA = & dotnet build $slnPath "-c" "Release" "-m:1" "/p:UseSharedCompilation=false" "-p:Platform=x64" 2>&1
$exitA = $LASTEXITCODE
$markerA = ($outA | Select-String -SimpleMatch "Build succeeded." -Quiet)
if ($exitA -ne 0 -or -not $markerA) {
    Fail-Step "a-sln-build" ($outA | Out-String)
    Write-Host "[r45_verify] Step (a) FAILED exit=$exitA marker=$markerA"
} else {
    Write-Host "[r45_verify] Step (a) PASS"
}

# Step (b): dotnet build Native.csproj -c Release -m:1 /p:UseSharedCompilation=false
# Required because tests csproj references Core/ProcessJobProbe/Server only, so the two
# WinUI invalidate call sites (MainPage.RunningChats.cs:37, MainPage.Remote.cs:1150) are
# compile-proven ONLY by this build.
if (-not $failReason) {
    Write-Host "[r45_verify] Step (b) dotnet build Native.csproj Release..."
    $outB = & dotnet build $nativeProj "-c" "Release" "-m:1" "/p:UseSharedCompilation=false" 2>&1
    $exitB = $LASTEXITCODE
    $markerB = ($outB | Select-String -SimpleMatch "Build succeeded." -Quiet)
    if ($exitB -ne 0 -or -not $markerB) {
        Fail-Step "b-native-build" ($outB | Out-String)
        Write-Host "[r45_verify] Step (b) FAILED exit=$exitB marker=$markerB"
    } else {
        Write-Host "[r45_verify] Step (b) PASS"
    }
}

# Step (c): dotnet test --no-build --filter (mirrors run_gates.ps1 test step pattern)
if (-not $failReason) {
    Write-Host "[r45_verify] Step (c) dotnet test (filter=$filter)..."
    $outC = & dotnet test $testsProj "-c" "Release" "--no-build" `
        "--logger" "console;verbosity=minimal" `
        "--filter" $filter `
        "--blame-hang" "--blame-hang-timeout" "3m" "--blame-hang-dump-type" "mini" 2>&1
    $exitC = $LASTEXITCODE

    # Parse the dotnet test summary line
    $summaryLine = ($outC | Select-String -Pattern "^\s*(Passed|Failed)!" | Select-Object -Last 1)
    Write-Host "[r45_verify] dotnet test exit=$exitC summary=$summaryLine"

    $failedCount = -1
    $passedCount = -1

    if ($summaryLine) {
        $txt = $summaryLine.Line
        if ($txt -match "Failed:\s*(\d+)")  { $failedCount = [int]$Matches[1] }
        if ($txt -match "Passed:\s*(\d+)")  { $passedCount = [int]$Matches[1] }
    }

    if ($failedCount -ne 0 -or $passedCount -lt 507) {
        Fail-Step "c-tests" ($outC | Out-String)
        Write-Host "[r45_verify] Step (c) FAILED Failed=$failedCount Passed=$passedCount (need Failed==0 && Passed>=507)"
    } else {
        Write-Host "[r45_verify] Step (c) PASS Failed=$failedCount Passed=$passedCount"
    }
}

# Verdict
if ($failReason) {
    Write-Host "R45-GATE-FAIL"
    Write-Host "Failing step(s): $($failReason -join ", ")"
    Write-Host "`n--- Failing step output tail ---"
    foreach ($out in $failOutput) {
        $lines = $out -split "`r?`n"
        $start = [Math]::Max(0, $lines.Count - 80)
        $lines[$start..($lines.Count - 1)] | Write-Host
    }
    exit 1
}

Write-Host "R45-GATE-OK"
exit 0
