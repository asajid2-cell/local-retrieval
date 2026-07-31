using System.Diagnostics;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// The burst scan cache inside RunningSessions.
//
// The cost these pin down: one remote refresh fires integrity + custody + claim checks back to back, and each
// used to pay for its own WMI world sweep (~100-300ms) plus its own walk of Claude's live-session registry.
// They ask the same question microseconds apart, so a short TTL collapses the burst into ONE sweep.
//
// WMI and Claude's registry are machine-global and cannot be arranged in-test without touching real user
// state, so both sweep sources are injected through RunningSessions.ScanSourceOverride (the same seam shape as
// KillSignals). Only the SCAN fake ticks PerfCounters.WmiSweep, mirroring the real code: TryScan is the WMI
// pass, TryClaudeLiveSessionIds is a file walk. An injected scan returning no sessions means no live pids,
// which makes the per-pid transcript probe a no-op — so these tests touch nothing on the machine.
[TestClass]
public class ScanCacheTests
{
    private long _scans;
    private long _registryReads;
    private volatile bool _scanFails;
    private volatile bool _registryFails;

    [TestInitialize]
    public void Init()
    {
        _scans = 0;
        _registryReads = 0;
        _scanFails = false;
        _registryFails = false;
        RunningSessions.ScanSourceOverride = new RunningSessions.ScanSources(Scan: FakeScan, ClaudeRegistry: FakeRegistry);
        RunningSessions.InvalidateScanCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        RunningSessions.ScanSourceOverride = null;
        RunningSessions.InvalidateScanCache();
    }

    // Stands in for TryScan: counts itself against the same counter the real WMI pass ticks, so a test asserting
    // "one sweep" is asserting on the number the perf gates record.
    private (bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail) FakeScan()
    {
        Interlocked.Increment(ref _scans);
        PerfCounters.WmiSweep();
        return _scanFails
            ? (false, new List<ArchiveService.RunningSessionInfo>(), "injected scan failure")
            : (true, new List<ArchiveService.RunningSessionInfo>(), "");
    }

    // Stands in for TryClaudeLiveSessionIds. Not a WMI pass, so it does NOT tick wmiSweeps — it gets its own
    // counter, and the tests assert both collapse together.
    private (bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail) FakeRegistry(HashSet<int>? livePids)
    {
        Interlocked.Increment(ref _registryReads);
        return _registryFails
            ? (false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new HashSet<int> { 4242 }, "injected registry failure")
            : (true, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new HashSet<int>(), "");
    }

    private static long Sweeps() => PerfCounters.Snapshot()["wmiSweeps"];

    // ---- 1. a burst shares one sweep ----------------------------------------------------------------

    // The whole point of the cache: the callers of one refresh cycle ask within milliseconds of each other, so
    // nine of these ten questions must be answered from the first one's sweep.
    [TestMethod]
    public void TenLivenessChecksInOneBurst_SweepOnce()
    {
        var before = Sweeps();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var detail), detail);
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), "the burst took " + sw.ElapsedMilliseconds + "ms, longer than the TTL it is meant to fit inside");
        Assert.AreEqual(1, Sweeps() - before, "ten liveness checks inside one TTL should cost exactly one WMI sweep");
        Assert.AreEqual(1, Interlocked.Read(ref _scans));
        Assert.AreEqual(1, Interlocked.Read(ref _registryReads), "the Claude registry walk must collapse with the sweep, not survive it");
    }

    // ---- 2. [F#3] the post-claim re-check bypasses the cache -----------------------------------------

    // Once the reservation is HELD the question changes from "who is running" to "did the world change in the
    // last few milliseconds", and a cached answer cannot tell you that by construction. So with an already-warm
    // cache, acquiring a claim must still cost a sweep: the pre-check rides the burst, the re-check does not.
    [TestMethod]
    public void PostClaimRecheck_BypassesTheCacheAndSweepsAgain()
    {
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var warmDetail), warmDetail);   // warm it
        var before = Sweeps();
        var scansBefore = Interlocked.Read(ref _scans);

        var root = Path.Combine(Path.GetTempPath(), "scan-cache-claim-" + Guid.NewGuid().ToString("N"));
        SessionLaunchClaim? claim = null;
        try
        {
            var ok = SessionLaunchClaims.TryAcquire(
                Guid.NewGuid().ToString(),
                aliases: null,
                reason: "scan cache test",
                out claim,
                out var detail,
                isSessionLive: null,   // null => the check falls through to RunningSessions, which is the point
                options: new SessionLaunchClaims.Options(RootDirectory: root));

            Assert.IsTrue(ok, detail);
            Assert.AreEqual(1, Sweeps() - before, "the post-claim re-check must bypass the warm cache and sweep for itself");
            Assert.AreEqual(1, Interlocked.Read(ref _scans) - scansBefore, "exactly one of the two liveness checks may bypass: the post-claim one");
        }
        finally
        {
            try { claim?.Dispose(); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    // ---- 3. [F#4] invalidation forces the next caller to re-sweep ------------------------------------

    // Every kill/create site calls this. If it did not actually drop the entry, the next liveness question would
    // report the process we just removed as still running for the rest of the TTL.
    [TestMethod]
    public void InvalidateScanCache_MakesTheNextCallSweep()
    {
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d1), d1);
        var afterWarm = Sweeps();

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d2), d2);
        Assert.AreEqual(0, Sweeps() - afterWarm, "precondition: a second call inside the TTL is served from cache");

        RunningSessions.InvalidateScanCache();

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d3), d3);
        Assert.AreEqual(1, Sweeps() - afterWarm, "after invalidation the next caller must re-sweep");
        Assert.AreEqual(2, Interlocked.Read(ref _scans));
    }

    // ---- 4. [F#8] a failed sweep is never cached -----------------------------------------------------

    // A transient WMI hiccup must not fail-close every caller for a whole TTL: each retries for itself, and the
    // first success is what becomes shared.
    [TestMethod]
    public void FailedScan_IsNeverCachedAndTheNextCallRetries()
    {
        _scanFails = true;

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var first), "the injected failure should surface: " + first);
        var afterFailure = Sweeps();
        Assert.AreEqual(1, Interlocked.Read(ref _scans));

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var second), "the injected failure should still surface: " + second);
        Assert.AreEqual(1, Sweeps() - afterFailure, "a failed sweep must not be served from cache — the next caller retries");

        _scanFails = false;
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var third), third);
        Assert.AreEqual(2, Sweeps() - afterFailure, "the recovery call sweeps for itself");

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var fourth), fourth);
        Assert.AreEqual(2, Sweeps() - afterFailure, "...and only a SUCCESSFUL sweep becomes the shared answer");
    }

    // Same rule one layer down: an unverifiable registry read means a live owner may be HIDDEN from the answer,
    // and re-serving that for a whole TTL would turn one write race into a burst-wide blind spot.
    [TestMethod]
    public void FailedRegistryRead_IsNeverCachedAndTheNextCallRetries()
    {
        _registryFails = true;

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var unverifiable, out var first), first);
        CollectionAssert.Contains(unverifiable.ToList(), 4242, "the blocking pid should be reported as unverifiable, not silently dropped");
        Assert.AreEqual(1, Interlocked.Read(ref _registryReads));

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var second), second);
        Assert.AreEqual(2, Interlocked.Read(ref _registryReads), "a failed registry read must not be cached");

        _registryFails = false;
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var third), third);
        Assert.AreEqual(3, Interlocked.Read(ref _registryReads));

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var fourth), fourth);
        Assert.AreEqual(3, Interlocked.Read(ref _registryReads), "only the clean read becomes the shared answer");
    }
}
