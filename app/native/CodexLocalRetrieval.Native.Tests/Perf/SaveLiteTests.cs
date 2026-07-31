using System.Collections.Concurrent;
using System.Diagnostics;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests.Perf;

// r.4.9 -- SaveAsync off the caller's thread.
//
// Tagging, pinning or renaming a chat calls `await SaveAsync(); ReapplyList();` straight from a click
// handler. Nothing in ArchiveService's save chain uses ConfigureAwait(false), so in WinUI every
// continuation resumes on the captured context -- the dispatcher. Everything SaveAsync does between its
// awaits therefore runs on the UI thread, and on a 4000-chat store that was:
//
//   * File.ReadAllBytes of the whole 13.8 MB store, plus TWO JsonDocument.Parse passes over it, to
//     recover ONE integer (the generation);
//   * a WriteIndented re-serialize of the entire store;
//   * a full Deserialize<AppStoreData> of all 4000 sessions purely to validate -- result discarded.
//
// DispatcherPump below is not a proxy for that. It is a single-threaded SynchronizationContext, which is
// what a WinUI dispatcher is, so the busy time it records IS the stall the user feels when they click a
// tag. Counters carry the strict bounds; the wall number is recorded and bounded loosely, because it is
// the only figure here a busy machine can move.
[TestClass]
public sealed class SaveLiteTests
{
    private const int CorpusSessions = 4000;

    private sealed class DispatcherPump : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();
        private readonly Stopwatch _busy = new();

        public double BusyMs => _busy.Elapsed.TotalMilliseconds;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            _busy.Start();
            try { d(state); } finally { _busy.Stop(); }
        }

        // Pump continuations on THIS thread until the work completes, timing only the callbacks -- the
        // idle wait between them is the background I/O we deliberately moved off the dispatcher.
        public void RunUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (!_queue.TryTake(out var item, 25)) continue;
                _busy.Start();
                try { item.Work(item.State); } finally { _busy.Stop(); }
            }
            while (_queue.TryTake(out var drained, 0))
            {
                _busy.Start();
                try { drained.Work(drained.State); } finally { _busy.Stop(); }
            }
            task.GetAwaiter().GetResult();
        }
    }

    // One tag click: mutate, save, and measure what the dispatcher was busy doing.
    private static (double BusyMs, IReadOnlyDictionary<string, long> Counters) TagClick(ArchiveService svc)
    {
        var previous = SynchronizationContext.Current;
        var pump = new DispatcherPump();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            PerfCounters.Reset();

            // The synchronous PREFIX matters more than the continuations. _saveGate.WaitAsync() and
            // AcquireStoreLockAsync() both complete synchronously when uncontended, so `await` never
            // yields and everything up to the first real suspension runs straight-line on THIS thread --
            // the dispatcher. Timing only pumped callbacks measured 0.05 ms and asserted nothing.
            var inline = Stopwatch.StartNew();
            var task = svc.SaveAsync();
            inline.Stop();

            pump.RunUntil(task);
            return (inline.Elapsed.TotalMilliseconds + pump.BusyMs, PerfCounters.Snapshot());
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(300000)]
    public async Task TagClick_DoesNotDoTheStoreWorkOnTheDispatcher()
    {
        var svc = PerfCorpus.NewStoreService(out var storeDir);
        try
        {
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            PerfCorpus.FillSessions(svc, CorpusSessions);
            await svc.SaveAsync();          // establish the file and warm the JIT
            await svc.SaveAsync();

            var (busyMs, counters) = TagClick(svc);

            var bytesRead = counters["storeBytesRead"];
            var parses = counters["storeJsonParses"];
            var deserializes = counters["storeFullDeserializes"];
            var written = counters["storeBytesWritten"];

            PerfRecord.Measure("store.save.dispatcherBusyMs", busyMs, "ms");
            PerfRecord.Measure("store.save.bytesRead", bytesRead, "bytes");
            PerfRecord.Measure("store.save.jsonParses", parses, "count");
            PerfRecord.Measure("store.save.fullDeserializes", deserializes, "count");
            PerfRecord.Measure("store.save.bytesWritten", written, "bytes");

            // The three named wastes, each its own assertion so a failure names the one that came back.
            Assert.IsTrue(bytesRead <= 64 * 1024,
                $"the save path read {bytesRead:N0} bytes of the store to recover one integer; " +
                "a bounded header probe is all the generation check needs");
            Assert.AreEqual(0L, deserializes,
                $"the save path ran {deserializes} full store deserializations; validating the serialized " +
                "bytes must not mean rebuilding 4000 session objects and discarding them");
            Assert.IsTrue(parses <= 1,
                $"the save path walked the whole store as JSON {parses} times; one shape check is enough");

            // Compact output is both smaller and cheaper to produce than the indented form.
            Assert.IsTrue(written > 0, "the save must actually have serialized something");
            PerfRecord.Measure("store.save.bytesWrittenMB", written / 1048576.0, "MB");

            // The softest number here, so it carries the loosest bound -- but not a vacuous one: the
            // pre-fix path measured 190 ms on this same corpus, so 100 ms would have caught it while
            // still leaving ~4x headroom over the 26 ms it now costs.
            Assert.IsTrue(busyMs < 100.0,
                $"the dispatcher was busy {busyMs:F1} ms during one save; that is the stall the user " +
                "feels on a tag click, and it was 190 ms before this path was thinned");
        }
        finally { PerfCorpus.TryDeleteDirectory(storeDir); }
    }

    // Durability is the thing that must NOT change. A save still has to be readable, still has to carry
    // the advanced generation, and a fresh service must load exactly what was written.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(300000)]
    public async Task SaveStillRoundTrips_AndAdvancesTheGeneration()
    {
        var svc = PerfCorpus.NewStoreService(out var storeDir);
        try
        {
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            PerfCorpus.FillSessions(svc, 200);
            await svc.SaveAsync();
            var firstGeneration = svc.Store.Generation;

            var victim = svc.Store.Sessions.Values.First();
            victim.Tags.Add("zzsavelite");
            await svc.SaveAsync();

            Assert.IsTrue(svc.Store.Generation > firstGeneration,
                "each commit must advance the generation");

            var storePath = Path.Combine(storeDir, "app-store.json");
            var reloaded = new ArchiveService(
                storePath: storePath,
                codexSessionsRoot: Path.Combine(storeDir, "no-such-codex"),
                claudeSessionsRoot: Path.Combine(storeDir, "no-such-claude"));
            await reloaded.LoadAsync();

            Assert.AreEqual(200, reloaded.Store.Sessions.Count, "every chat must survive the round trip");
            Assert.AreEqual(svc.Store.Generation, reloaded.Store.Generation);
            Assert.IsTrue(reloaded.Store.Sessions[victim.Id].Tags.Contains("zzsavelite"),
                "the mutation that triggered the save must be in the file");
        }
        finally { PerfCorpus.TryDeleteDirectory(storeDir); }
    }

    // The generation check is what detects a second writer. Thinning HOW it is read must not weaken WHAT
    // it detects: a store changed underneath us still has to be refused.
    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(300000)]
    public async Task AConcurrentWriterIsStillDetected()
    {
        var svc = PerfCorpus.NewStoreService(out var storeDir);
        try
        {
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            PerfCorpus.FillSessions(svc, 50);
            await svc.SaveAsync();

            var storePath = Path.Combine(storeDir, "app-store.json");
            var other = new ArchiveService(
                storePath: storePath,
                codexSessionsRoot: Path.Combine(storeDir, "no-such-codex"),
                claudeSessionsRoot: Path.Combine(storeDir, "no-such-claude"));
            await other.LoadAsync();
            await other.SaveAsync();          // advances the on-disk generation behind svc's back

            await Assert.ThrowsExactlyAsync<Exception>(async () =>
            {
                try { await svc.SaveAsync(); }
                catch (Exception e) when (e.GetType().Name == "StoreGenerationConflictException")
                { throw new Exception("conflict detected", e); }
            }, "a save racing another writer must still be refused");
        }
        finally { PerfCorpus.TryDeleteDirectory(storeDir); }
    }
}
