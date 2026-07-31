using System.Diagnostics;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests.Perf;

// The r.4.8 acceptance gate for the search hot path: one filter pass over 4000 sessions must cost
// < 25 ms and < 4 MB of allocation.
//
// The COUNTER assertions are the real contract. Wall and allocation bounds are kept at roughly 5x
// headroom over the measured post-fix numbers on purpose: this box runs a 5000-process table and
// several concurrent agents, so a tight wall bound would measure the host, not the code. A counter
// does not care how busy the machine is.
//
// Measured on master before the cache (BaselineProbeTests, same 4000-session corpus):
//   search pass p50 44.25 ms / p90 61.77 ms, ~5.2 MB allocated per query, 8000 compositions per
//   two-term query. After: see the recorded search.cached.* numbers below.
[TestClass]
public sealed class SearchHotPathTests
{
    private const int CorpusSessions = 4000;

    private static readonly string[] Queries =
    {
        "renderer parse", "archive store", "session commit", "projection gate", "ledger measure",
        "transcript buffer", "custody claim", "relay socket", "pty resize", "worktree baseline",
    };

    private static ArchiveService _svc = null!;
    private static string _storeDir = "";

    [ClassInitialize]
    public static void BuildCorpus(TestContext _)
    {
        _svc = PerfCorpus.NewStoreService(out _storeDir);
        _svc.Store.Settings.BundledHistoryAbsorbed = true;
        PerfCorpus.FillSessions(_svc, CorpusSessions);
    }

    [ClassCleanup]
    public static void DropCorpus() => PerfCorpus.TryDeleteDirectory(_storeDir);

    // (1) One pass over 4000 sessions: median wall and per-query allocation.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void SearchPass_OverFourThousandSessions_IsUnderWallAndAllocationBudget()
    {
        // Warm the cache and the JIT the same way the app does: the user has already typed something.
        foreach (var q in Queries) _ = _svc.Search(q);

        var samples = new List<double>();
        var allocations = new List<long>();
        foreach (var q in Queries)
        {
            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var results = _svc.Search(q);
            sw.Stop();
            var afterAlloc = GC.GetAllocatedBytesForCurrentThread();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            allocations.Add(afterAlloc - beforeAlloc);
            Assert.IsNotNull(results);
        }

        samples.Sort();
        allocations.Sort();
        var medianMs = samples[samples.Count / 2];
        var maxAlloc = allocations[^1];

        PerfRecord.Measure("search.cached.ms.p50", medianMs, "ms");
        PerfRecord.Measure("search.cached.ms.max", samples[^1], "ms");
        PerfRecord.Measure("search.cached.allocBytes.max", maxAlloc, "bytes");
        PerfRecord.Measure("search.cached.allocBytes.p50", allocations[allocations.Count / 2], "bytes");

        Assert.IsTrue(medianMs < 25.0,
            $"median search pass over {CorpusSessions} sessions was {medianMs:F2} ms, budget 25 ms " +
            "(pre-cache baseline was 44.25 ms)");
        Assert.IsTrue(maxAlloc < 4L * 1024 * 1024,
            $"worst-query allocation was {maxAlloc:N0} bytes, budget 4 MB");
    }

    // (2) The primary assertion: a repeated query over an unmutated store recomposes NOTHING.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void RepeatQuery_OverUnmutatedStore_ComposesNoSearchText()
    {
        _ = _svc.Search("renderer parse");   // first pass populates every session's cache

        PerfCounters.Reset();
        var before = PerfCounters.Snapshot()["searchTextCompositions"];
        _ = _svc.Search("renderer parse");
        _ = _svc.Search("archive store");
        var after = PerfCounters.Snapshot()["searchTextCompositions"];

        Assert.AreEqual(0L, after - before,
            "a search over an unmutated store must not recompose any session's search text; " +
            $"{after - before} compositions happened, meaning the cache is not being hit");
    }

    // (3) The negative control that keeps (2) honest: a mutation MUST invalidate, and exactly one
    //     session's text may be recomposed. Without this, a permanently-empty cache would also pass (2).
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void RetaggingOneSession_RecomposesExactlyThatSession_AndTheResultReflectsIt()
    {
        const string tag = "zzsearchhotpathtag";
        var victim = _svc.Store.Sessions.Values.First(s => !s.Archived);

        _ = _svc.Search(tag);   // populate every cache; nothing matches yet

        PerfCounters.Reset();
        victim.Tags.Add(tag);
        var hits = _svc.Search(tag);
        var composed = PerfCounters.Snapshot()["searchTextCompositions"];

        try
        {
            Assert.AreEqual(1L, composed,
                $"retagging one session must recompose exactly one search text, not {composed}");
            Assert.AreEqual(1, hits.Count, "the freshly tagged session must be the only match");
            Assert.AreEqual(victim.Id, hits[0].Id, "the match must be the session that was retagged");
        }
        finally
        {
            victim.Tags.Remove(tag);
        }
    }

    // The cache must never be able to serve a stale answer: mutate each contributing field in turn and
    // assert the recomposed text carries the new value. This is what makes the cache safe to trust.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(60000)]
    public void EveryContributingField_InvalidatesTheCache()
    {
        var s = new ArchiveSession();
        Assert.IsFalse(s.SearchText.Contains("zzprobe", StringComparison.Ordinal));

        void Check(string label, Action mutate)
        {
            mutate();
            Assert.IsTrue(s.SearchText.Contains("zzprobe", StringComparison.Ordinal),
                $"mutating {label} did not invalidate the cached search text");
            Assert.IsFalse(s.SearchText.Contains("zzprobe-stale", StringComparison.Ordinal));
        }

        Check("Id", () => s.Id = "zzprobe-id");
        s.Id = "";
        Check("Title", () => s.Title = "zzprobe-title");
        s.Title = "";
        Check("CustomTitle", () => s.CustomTitle = "zzprobe-custom");
        s.CustomTitle = "";
        Check("Text", () => s.Text = "zzprobe-text");
        s.Text = "";
        Check("SourcePath", () => s.SourcePath = "zzprobe-path");
        s.SourcePath = "";
        Check("Workspace", () => s.Workspace = "zzprobe-workspace");
        s.Workspace = "";
        Check("Tags.Add", () => s.Tags.Add("zzprobe-tag"));
        s.Tags.Clear();
        Check("SpecialPhrases.Add", () => s.SpecialPhrases.Add("zzprobe-phrase"));
        s.SpecialPhrases.Clear();
        Check("Tags replaced wholesale", () => s.Tags = new System.Collections.ObjectModel.ObservableCollection<string> { "zzprobe-new" });
        Check("Tags mutated after replacement", () => s.Tags.Add("zzprobe-after"));
    }
}
