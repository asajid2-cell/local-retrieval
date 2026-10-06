using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveRuntimeTests
{
    [TestMethod]
    public async Task WatcherRefresh_MakesNewRolloutVisibleToWarmRuntime_AndPreservesOrganization()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        var existing = fixture.Archive.Store.Sessions["existing-1"];
        existing.CustomTitle = "Keep this name";
        existing.Pinned = true;
        await fixture.Archive.SaveAsync();

        var path = fixture.WriteRollout("rollout-new.jsonl", "new-1", "new work");
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("new-1"));

        var count = await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        Assert.AreEqual(2, count);
        Assert.AreEqual("Keep this name", fixture.Archive.Store.Sessions["existing-1"].CustomTitle);
        Assert.IsTrue(fixture.Archive.Store.Sessions["existing-1"].Pinned);
        Assert.IsTrue(fixture.Archive.Store.FileStamps.ContainsKey(path));
    }

    [TestMethod]
    public async Task DeferredMergeSave_StaysOffDiskUntilFlush()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        await fixture.Archive.FlushDeferredSaveAsync();

        fixture.WriteRollout("rollout-deferred.jsonl", "deferred-1", "deferred work");
        // Wait on the dirty flag, not the session: the merge publishes the session into memory before it
        // reaches its save step, so a session-only wait can read HasDeferredSave before the merge sets it.
        await WaitUntilAsync(() => fixture.Archive.HasDeferredSave);

        Assert.IsTrue(fixture.Archive.Store.Sessions.ContainsKey("deferred-1"));
        Assert.IsFalse(File.ReadAllText(fixture.StorePath).Contains("deferred-1"),
            "the background merge must not rewrite the store inside the coalesce window");

        await fixture.Archive.FlushDeferredSaveAsync();
        Assert.IsFalse(fixture.Archive.HasDeferredSave);
        Assert.IsTrue(File.ReadAllText(fixture.StorePath).Contains("deferred-1"),
            "flush must commit the deferred merge");
    }

    // The crash gate: a hard kill between appends drops the deferred save, and the restart must recover
    // the content by re-parsing the changed file (the stamps revert with the data they describe).
    [TestMethod]
    public async Task DeferredSaveDroppedOnKill_IsRecoveredByReparse()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        await fixture.Archive.FlushDeferredSaveAsync();
        fixture.WriteRollout("rollout-killed.jsonl", "killed-1", "appended then killed");
        // Wait for the merge to finish its save step, so the assertion below is about a completed merge.
        await WaitUntilAsync(() => fixture.Archive.HasDeferredSave);

        // No FlushDeferredSaveAsync: this is the state a hard kill leaves -- memory ahead of disk.
        Assert.IsTrue(fixture.Archive.Store.Sessions.ContainsKey("killed-1"));
        Assert.IsFalse(File.ReadAllText(fixture.StorePath).Contains("killed-1"));

        var restarted = new ArchiveService(
            storePath: fixture.StorePath,
            codexSessionsRoot: fixture.Root,
            sourceOverride: new[]
            {
                new CodexLocalRetrieval.Core.Models.SessionSource { Tool = "codex", Root = fixture.Root },
            });
        await restarted.LoadAsync();
        var scan = await restarted.ScanDiskAsync();
        await restarted.MergeScanAsync(scan);
        Assert.IsTrue(restarted.Store.Sessions.ContainsKey("killed-1"),
            "a dropped deferred save must be recovered by re-parsing the changed file");
    }

    [TestMethod]
    public async Task WatcherRefresh_DoesNotStampPartialRollout_AndConvergesAfterCompletion()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        var path = Path.Combine(fixture.Root, "rollout-partial.jsonl");
        await File.WriteAllTextAsync(path,
            "{\"timestamp\":\"2026-08-27T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"partial-1\",\"cwd\":\"z:/proj\"}}\n"
            + "{\"timestamp\":\"2026-08-27T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"fin");
        await WaitUntilAsync(() => !fixture.Archive.Store.FileStamps.ContainsKey(path));

        await File.AppendAllTextAsync(path, "ished\"}}\n");
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("partial-1"));
        await WaitUntilAsync(() => fixture.Archive.Store.FileStamps.ContainsKey(path));
        Assert.AreEqual(1, fixture.Archive.Store.Sessions["partial-1"].UserMessageCount);
        Assert.AreEqual("finished", fixture.Archive.Store.Sessions["partial-1"].LastUserMessage);

        // The completed file was stamped only after the retry parsed the completed tail.
    }

    [TestMethod]
    public async Task UseAsync_DoesNotRescanWarmRuntimeUntilWatcherMarksRefresh()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        fixture.WriteRollout("rollout-late.jsonl", "late-1", "late work");

        await Task.Delay(100);
        Assert.IsFalse(fixture.Archive.Store.Sessions.ContainsKey("late-1"), "a warm read must not perform a recursive scan on every request");

        fixture.Runtime.MarkRefreshPending();
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("late-1"));
    }

    [TestMethod]
    public async Task UseAsync_SerializesEveryArchiveReaderAndWriter()
    {
        using var store = new TempStore();
        var archive = new ArchiveService(storePath: store.Path);
        var runtime = new ArchiveRuntime(archive, syncOnLoad: false);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = runtime.UseAsync(async (_, _) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        });
        await firstEntered.Task;

        var second = runtime.UseAsync((_, _) =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        });

        await Task.Delay(100);
        Assert.IsFalse(secondEntered, "a second archive operation entered while the first still owned the runtime");

        releaseFirst.SetResult();
        Assert.AreEqual(1, await first);
        Assert.AreEqual(2, await second);
    }

    [TestMethod]
    public async Task WarmRuntime_ObservesExternalStoreCommitWithoutTranscriptRescan()
    {
        using var store = new TempStore();
        ArchiveService Create() => new(storePath: store.Path,
            sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>(), enableTranscriptSearchIndex: false);
        var writer = Create();
        await writer.LoadAsync();
        writer.Store.Sessions["shared"] = new() { Id = "shared", Tool = "claude", Title = "Original" };
        await writer.SaveAsync();
        var runtime = new ArchiveRuntime(Create(), syncOnLoad: false);
        try
        {
            Assert.IsFalse(await runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions["shared"].Pinned)));
            writer.Store.Sessions["shared"].Pinned = true;
            writer.Store.Sessions["shared"].CustomTitle = "GUI committed title";
            await writer.SaveAsync();
            await runtime.UseAsync((archive, _) =>
            {
                Assert.IsTrue(archive.Store.Sessions["shared"].Pinned, "completed external favorite must be visible on the next read");
                Assert.AreEqual("GUI committed title", archive.Store.Sessions["shared"].CustomTitle);
                return Task.FromResult(true);
            });
        }
        finally { runtime.Dispose(); }
    }

    [TestMethod]
    public async Task UseAsync_DoesNotQueueBehindTheWatcherDiskWalk()
    {
        // The regression this guards: the watcher sync used to run its ENTIRE disk walk inside the same
        // gate as every remote read, so a scan that took 22s made an unrelated read wait 22s. The walk is
        // off-thread safe and belongs outside the gate; only the merge needs it.
        using var fixture = new RuntimeFixture();
        var runtime = new ArchiveRuntime(fixture.Archive, syncOnLoad: true);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Warm it with no hook installed: the cold path runs its own sync, and parking that one would
            // just stall the load rather than test anything.
            await runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));

            var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.SyncScanHook = async _ =>
            {
                scanEntered.TrySetResult();
                await releaseScan.Task;
            };

            runtime.MarkRefreshPending();
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The scan is parked mid-walk, and the pending flag it consumed is already clear, so this read
            // has no sync of its own to run. It must complete now: if the walk held the archive gate, it
            // would block until the scan is released and the timeout below would fire instead.
            var read = runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(read, finished, "a remote read queued behind the watcher's disk walk");

            releaseScan.SetResult();
            Assert.AreEqual(1, await read);
        }
        finally
        {
            releaseScan.TrySetResult();
            runtime.Dispose();
        }
    }

    [TestMethod]
    public async Task UseAsync_DoesNotRunTheWatchersScanOnTheRequestThread()
    {
        // The regression this guards: a warm read that happened to win the watcher's pending flag paid the
        // entire disk walk inline (measured refresh=28339ms on the live bridge). The watcher's refresh is
        // background work, so the read must hand it to the worker and answer from the store it already has.
        using var fixture = new RuntimeFixture();
        var runtime = new ArchiveRuntime(fixture.Archive, syncOnLoad: true);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));

            var scanEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.SyncScanHook = async _ =>
            {
                scanEntered.TrySetResult();
                await releaseScan.Task;
            };

            runtime.MarkRefreshPending();
            await scanEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The worker is parked mid-walk, so it cannot consume a fresh flag: SchedulePendingRefresh is a
            // no-op while _refreshWorkerActive is set. Marking again therefore leaves the flag SET when the
            // read arrives -- which is the race that used to hand the walk to the request thread.
            runtime.MarkRefreshPending();

            // The read must answer from the store it has, not wait on the walk the worker is holding.
            var read = runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)));
            Assert.AreSame(read, finished, "a warm read ran the watcher's disk walk on the request thread");
            Assert.AreEqual(1, await read);

            // And the merge it deferred still lands, so deferring did not drop the refresh.
            releaseScan.SetResult();
            await WaitUntilAsync(() => fixture.Archive.Store.FileStamps.Count > 0);
        }
        finally
        {
            releaseScan.TrySetResult();
            runtime.Dispose();
        }
    }

    [TestMethod]
    public async Task ReconciliationBeat_ForcesAFullScanWithoutAWatcherEvent()
    {
        // The watcher's dirty set narrows every ordinary sync, so a file it never reported would only be
        // found by the reconciliation walk. This proves that beat actually fires and drives a scan on its
        // own -- the interval is a test override; production uses 30 minutes.
        using var fixture = new RuntimeFixture();
        var runtime = new ArchiveRuntime(fixture.Archive, syncOnLoad: true)
        {
            ReconcileIntervalOverride = TimeSpan.FromMilliseconds(200),
        };
        var scanSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            // Warm first: the cold load runs its own refresh before the hook is installed, so the scan
            // below can only be the timer's.
            await runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
            runtime.SyncScanHook = _ =>
            {
                scanSeen.TrySetResult();
                return Task.CompletedTask;
            };

            await scanSeen.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            runtime.Dispose();
        }
    }

    [TestMethod]
    public async Task TryUnloadIfIdleAsync_WaitsForActiveArchiveOperation()
    {
        using var store = new TempStore();
        var archive = new ArchiveService(storePath: store.Path);
        var runtime = new ArchiveRuntime(archive, syncOnLoad: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var active = runtime.UseAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        await entered.Task;

        var unload = runtime.TryUnloadIfIdleAsync(TimeSpan.Zero);
        await Task.Delay(100);
        Assert.IsFalse(unload.IsCompleted, "idle unload raced an active archive reader");
        Assert.IsTrue(runtime.IsLoaded);

        release.SetResult();
        await active;
        Assert.IsTrue(await unload);
        Assert.IsFalse(runtime.IsLoaded);
        Assert.AreEqual(-1, runtime.SessionCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("condition did not become true before the 10 second timeout");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clr-archive-runtime-watch-" + Guid.NewGuid().ToString("N"));

        public RuntimeFixture()
        {
            Directory.CreateDirectory(_directory);
            Root = System.IO.Path.Combine(_directory, "sessions");
            Directory.CreateDirectory(Root);
            StorePath = System.IO.Path.Combine(_directory, "store.json");
            File.WriteAllText(StorePath, "{\"sessions\":{},\"settings\":{\"sources\":[{\"tool\":\"codex\",\"root\":\"" + Root.Replace("\\", "\\\\") + "\"}]},\"collections\":{}}");
            Archive = new ArchiveService(
                storePath: StorePath,
                codexSessionsRoot: Root,
                sourceOverride: new[]
                {
                    new CodexLocalRetrieval.Core.Models.SessionSource { Tool = "codex", Root = Root },
                });
            Archive.Store.Settings.Sources.Clear();
            Archive.Store.Settings.Sources.Add(new CodexLocalRetrieval.Core.Models.SessionSource { Tool = "codex", Root = Root });
            Archive.Store.Settings.BundledHistoryAbsorbed = true;
            WriteRollout("rollout-existing.jsonl", "existing-1", "existing work");
            Runtime = new ArchiveRuntime(Archive, syncOnLoad: true);
        }

        public string Root { get; }
        public string StorePath { get; }
        public ArchiveService Archive { get; }
        public ArchiveRuntime Runtime { get; }

        public string WriteRollout(string fileName, string id, string userText)
        {
            var path = System.IO.Path.Combine(Root, fileName);
            File.WriteAllLines(path, new[]
            {
                "{\"timestamp\":\"2026-08-27T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"z:/proj\"}}",
                "{\"timestamp\":\"2026-08-27T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"" + userText + "\"}}",
                "{\"timestamp\":\"2026-08-27T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"on it\"}}",
            });
            return path;
        }

        public void Dispose()
        {
            Runtime.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clr-archive-runtime-" + Guid.NewGuid().ToString("N"));

        public TempStore()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "store.json");
            File.WriteAllText(Path, """{"sessions":{},"settings":{},"collections":{}}""");
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch { }
        }
    }
}
