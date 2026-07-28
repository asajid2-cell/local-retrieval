using System.Diagnostics;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests.Perf;

// The campaign's BEFORE picture. These probes assert nothing — a regression threshold invented before a
// single number exists is a guess, and a wrong guess would make the suite red for reasons unrelated to the
// code. They record named numbers into results.json; later leaves in this prong assert against them.
//
// [Timeout(120000)] is the only failure mode: a probe that stops finishing is itself the signal.
[TestClass]
public sealed class BaselineProbeTests
{
    private const int CorpusSessions = 4000;
    private const long LargeTranscriptBytes = 32L * 1024 * 1024;

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

    // Search recomposes SearchText(session) per session PER TERM (ArchiveService.cs:726) — every keystroke
    // rebuilds one interpolated string per chat per word. This is the cost that has to come down.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public void Baseline_SearchPassOverFourThousandSessions()
    {
        var queries = new[] { "renderer parse", "archive store", "session commit", "projection gate", "ledger measure" };
        _ = _svc.Search("warm the jit");

        PerfCounters.Reset();
        var samples = new List<double>();
        var hits = 0;
        for (var round = 0; round < 5; round++)
        {
            foreach (var q in queries)
            {
                var sw = Stopwatch.StartNew();
                var results = _svc.Search(q);
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
                hits += results.Count;
            }
        }

        samples.Sort();
        PerfRecord.Measure("search.pass.sessions", CorpusSessions, "count");
        PerfRecord.Measure("search.pass.passes", samples.Count, "count");
        PerfRecord.Measure("search.pass.hits", hits, "count");
        PerfRecord.Measure("search.pass.ms.p50", samples[samples.Count / 2], "ms");
        PerfRecord.Measure("search.pass.ms.p90", samples[(int)(samples.Count * 0.9)], "ms");
        PerfRecord.Measure("search.pass.ms.max", samples[^1], "ms");
        PerfRecord.Measure("search.pass.ms.mean", samples.Average(), "ms");
        PerfRecord.Counters("search.pass");
    }

    // SaveAsync serializes the WHOLE store, deserializes it back to verify, then writes three copies
    // (ArchiveService.cs:589-604). Every persisted edit pays this, so its wall on a realistic store is the
    // ceiling on how responsive any save-on-change feature can be.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public async Task Baseline_SaveAsyncWallOnFourThousandSessionStore()
    {
        var storePath = Path.Combine(_storeDir, "app-store.json");

        PerfCounters.Reset();
        var sw = Stopwatch.StartNew();
        await _svc.SaveAsync();
        sw.Stop();

        var bytes = File.Exists(storePath) ? new FileInfo(storePath).Length : 0;
        var ms = sw.Elapsed.TotalMilliseconds;
        PerfRecord.Measure("store.saveAsync.sessions", CorpusSessions, "count");
        PerfRecord.Measure("store.saveAsync.ms", ms, "ms");
        PerfRecord.Measure("store.saveAsync.bytes", bytes, "bytes");
        PerfRecord.Measure("store.saveAsync.msPerThousandSessions", ms / (CorpusSessions / 1000.0), "ms");
        PerfRecord.Measure("store.saveAsync.mbPerSec", ms > 0 ? bytes / 1048576.0 / (ms / 1000.0) : 0, "MB/s");
        PerfRecord.Counters("store.saveAsync");
    }

    // ScanDiskAsync is the integrity-adjacent path: it re-reads and re-parses every transcript on disk to
    // decide what changed. Measured cold (IndexVersion mismatch => full rescan) over a corpus that includes
    // one 32 MB transcript, because the long-chat case is where this actually hurts.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(120000)]
    public async Task Baseline_IntegrityAdjacentDiskScanCost()
    {
        var corpus = PerfCorpus.EnsureTranscriptCorpus("scan", sessions: 240, turnsPerSession: 24, largeBytes: LargeTranscriptBytes);
        var corpusBytes = PerfCorpus.DirectoryBytes(corpus);
        var files = Directory.GetFiles(corpus, "*.jsonl").Length;

        var svc = PerfCorpus.NewStoreService(out var scanStoreDir, codexSessionsRoot: corpus);
        try
        {
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            svc.Store.Settings.Sources.Add(new SessionSource { Tool = "codex", Root = corpus, Enabled = true });

            PerfCounters.Reset();
            var sw = Stopwatch.StartNew();
            var scan = await svc.ScanDiskAsync();
            sw.Stop();

            var ms = sw.Elapsed.TotalMilliseconds;
            PerfRecord.Measure("scan.cold.files", files, "count");
            PerfRecord.Measure("scan.cold.bytesOnDisk", corpusBytes, "bytes");
            PerfRecord.Measure("scan.cold.largestTranscriptBytes", LargeTranscriptBytes, "bytes");
            PerfRecord.Measure("scan.cold.parsedSessions", scan.Disk.Count, "count");
            PerfRecord.Measure("scan.cold.ms", ms, "ms");
            PerfRecord.Measure("scan.cold.msPerFile", files > 0 ? ms / files : 0, "ms");
            PerfRecord.Measure("scan.cold.mbPerSec", ms > 0 ? corpusBytes / 1048576.0 / (ms / 1000.0) : 0, "MB/s");
            PerfRecord.Counters("scan.cold");
        }
        finally
        {
            PerfCorpus.TryDeleteDirectory(scanStoreDir);
        }
    }
}
