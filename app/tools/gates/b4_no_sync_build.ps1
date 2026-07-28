# b4_no_sync_build.ps1 — the integrity oracle may not run on the UI thread.
#
# SessionIntegrity.Build walks the store, the process table and the claim files (including the 3x150ms
# registry retry in RunningSessions). Called inline from RenderIntegrity it froze the UI. It is now deferred:
# every occurrence in MainPage.Integrity.cs must be a lambda BODY handed to StaleGuardedRefresher, whose
# dispatcher is Task.Run. A bare call — `x = SessionIntegrity.Build(...)`, or `SessionIntegrity.Build(...)`
# as a statement — runs on whatever thread reached that line, which on this file is always the UI thread.
#
# Exit 1 on any bare call. Run from the app/ directory.

$ErrorActionPreference = 'Stop'

$target = Join-Path $PSScriptRoot '..\..\native\CodexLocalRetrieval.Native\MainPage.Integrity.cs'
if (-not (Test-Path $target)) {
    Write-Host "b4_no_sync_build: FAIL - target not found: $target"
    exit 1
}
$target = (Resolve-Path $target).Path

$hits = @(Select-String -Path $target -Pattern 'SessionIntegrity\.Build\(' -AllMatches)
if ($hits.Count -eq 0) {
    Write-Host "b4_no_sync_build: FAIL - no SessionIntegrity.Build( call found at all; the integrity panel is not being built."
    exit 1
}

# Deferred form: the call is the body of a lambda (`() => SessionIntegrity.Build(...)`), so reaching this
# line only CREATES the delegate. Anything else is invoked right there, on the caller's thread.
$bare = @($hits | Where-Object { $_.Line -notmatch '=>\s*SessionIntegrity\.Build\(' })
if ($bare.Count -gt 0) {
    Write-Host "b4_no_sync_build: FAIL - $($bare.Count) synchronous SessionIntegrity.Build( call(s) on the UI thread:"
    foreach ($b in $bare) {
        Write-Host ("  {0}:{1}: {2}" -f (Split-Path $b.Path -Leaf), $b.LineNumber, $b.Line.Trim())
    }
    Write-Host "  Hand the build to StaleGuardedRefresher as a lambda instead: _integrity.RefreshAsync(key, () => SessionIntegrity.Build(store, session), force)"
    exit 1
}

# A deferred lambda is only off-thread if something dispatches it. Prove the file routes it through the
# Task.Run-backed refresher rather than invoking the delegate itself.
$dispatched = @(Select-String -Path $target -Pattern '_integrity\.RefreshAsync\(')
if ($dispatched.Count -eq 0) {
    Write-Host "b4_no_sync_build: FAIL - the deferred build is never handed to StaleGuardedRefresher (_integrity.RefreshAsync); nothing takes it off the UI thread."
    exit 1
}

# And the panel must be paintable from cache, not only after a build completes.
$cached = @(Select-String -Path $target -Pattern '_integrity\.CurrentFor\(')
if ($cached.Count -eq 0) {
    Write-Host "b4_no_sync_build: FAIL - no cached-panel-first render (_integrity.CurrentFor); the UI still has nothing to show until the oracle returns."
    exit 1
}

Write-Host "b4_no_sync_build: PASS - $($hits.Count) SessionIntegrity.Build( call(s), all deferred; dispatched via StaleGuardedRefresher; cached-panel-first render present."
exit 0
