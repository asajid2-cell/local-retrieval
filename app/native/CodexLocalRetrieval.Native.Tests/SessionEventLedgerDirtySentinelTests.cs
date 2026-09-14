using System.Security.Cryptography;
using System.Text;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// Regression for silent, permanent history loss in the sidecar index. A backfill holds the index mutex for
// the whole sweep and stamps .complete only when it finishes. An append that loses the bounded-timeout race
// for that same mutex lands its event bytes in a file the sweep had already passed; if the append merely
// DELETED the (not-yet-existing) marker, the sweep then stamped .complete over an index that never saw those
// bytes. ReadForSession trusted the marker and returned nothing, forever. The fix is a sticky .dirty sentinel
// that survives the stamp, so the next read re-sweeps and picks the event up.
[TestClass]
public sealed class SessionEventLedgerDirtySentinelTests
{
    [TestMethod]
    [Timeout(60_000)]
    public void AppendThatLosesTheIndexMutexRaceIsNotHiddenByASweepCompletingMarker()
    {
        using var dir = NewTempDir();
        // Short enough that losing the index mutex is cheap, long enough that the (free) events-file mutex is
        // always acquired. Both waits share EffectiveLockTimeout; only the index one is contended here.
        var options = Options(dir.Path) with { LockTimeout = TimeSpan.FromMilliseconds(50) };
        var indexDir = SessionEventLedger.IndexDirectory(dir.Path);
        Directory.CreateDirectory(indexDir);

        // A backfill owns the index mutex for the duration of its sweep. Its .complete stamp comes at the END,
        // so while it runs there is no marker for the append's invalidation to delete.
        using var ready = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Factory.StartNew(() =>
        {
            using var mutex = new Mutex(false, MutexName(indexDir));
            Assert.IsTrue(mutex.WaitOne(TimeSpan.FromSeconds(1)));
            try
            {
                ready.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
            finally { mutex.ReleaseMutex(); }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(5)), "index mutex holder did not start");
            // The append succeeds (its event bytes are durable) but cannot write its sidecar entry.
            Assert.IsTrue(SessionEventLedger.TryAppend(
                SessionEventLedger.Create("after.sweep.passed", "landed behind the sweep", "sweep-victim"),
                out var detail, options), detail);

            // The sweep finishes: it never scanned this event's bytes, yet stamps completeness anyway.
            File.WriteAllText(
                Path.Combine(indexDir, ".complete"),
                DateTimeOffset.UtcNow.UtcDateTime.ToString("O") + "\n");
        }
        finally
        {
            release.Set();
            holder.Wait(TimeSpan.FromSeconds(2));
        }

        // The event must still be readable: the sentinel leaves the completed index untrusted, so the read
        // re-sweeps the ledger and finds the bytes the marker alone would have hidden.
        var found = SessionEventLedger.ReadForSession("sweep-victim", max: 10, options: options);

        Assert.HasCount(1, found);
        Assert.AreEqual("after.sweep.passed", found[0].Kind);
    }

    private static SessionEventLedger.Options Options(string root)
        => new(root, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

    private static string MutexName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return @"Local\CodexLocalRetrieval.SessionEventLedger." + hash[..32];
    }

    private static TempDir NewTempDir()
        => new(Path.Combine(Path.GetTempPath(), "clr-ledger-dirty-" + Guid.NewGuid().ToString("N")));

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
