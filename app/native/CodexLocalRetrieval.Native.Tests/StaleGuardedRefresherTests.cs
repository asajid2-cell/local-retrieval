using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

/// <summary>
/// The three invariants B4 leans on: the build never runs on the caller's thread, only the newest of two
/// overlapping refreshes publishes, and a force-refresh coalesces onto an in-flight build for the same key.
/// </summary>
[TestClass]
public sealed class StaleGuardedRefresherTests
{
    private sealed class Box
    {
        public Box(string value) => Value = value;
        public string Value { get; }
        public int ThreadId { get; init; }
    }

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static void Wait(Task task, string what)
    {
        if (!task.Wait(Patience)) Assert.Fail($"timed out waiting for {what}");
    }

    private static void Wait(WaitHandle handle, string what)
    {
        if (!handle.WaitOne(Patience)) Assert.Fail($"timed out waiting for {what}");
    }

    [TestMethod]
    public async Task Build_RunsOffTheCallersThread()
    {
        var refresher = new StaleGuardedRefresher<Box>();
        var callerThread = Environment.CurrentManagedThreadId;

        var outcome = await refresher.RefreshAsync(
            "k",
            () => new Box("built") { ThreadId = Environment.CurrentManagedThreadId });

        Assert.IsTrue(outcome.IsCurrent);
        Assert.IsNotNull(outcome.Value);
        Assert.AreNotEqual(
            callerThread,
            outcome.Value!.ThreadId,
            "the build ran on the calling thread — the whole point is that it does not");
        Assert.AreEqual("built", refresher.Current?.Value);
        Assert.AreEqual(1, refresher.BuildCount);
        Assert.IsFalse(refresher.IsRefreshing);
    }

    [TestMethod]
    public void Build_RunsOffTheCallersThread_ThroughTheInjectedDispatcher()
    {
        // The dispatcher is the injection seam: whatever it is, the refresher must hand the build to it and
        // never invoke it inline.
        var dispatcherThread = 0;
        var refresher = new StaleGuardedRefresher<Box>(
            dispatch: build => Task.Run(() =>
            {
                dispatcherThread = Environment.CurrentManagedThreadId;
                return build();
            }));

        var callerThread = Environment.CurrentManagedThreadId;
        var task = refresher.RefreshAsync("k", () => new Box("v") { ThreadId = Environment.CurrentManagedThreadId });
        Wait(task, "the dispatched build");

        Assert.AreNotEqual(callerThread, dispatcherThread);
        Assert.AreEqual(dispatcherThread, task.Result.Value!.ThreadId);
    }

    [TestMethod]
    public void OverlappingRefreshes_OnlyTheNewerPublishes_EvenWhenTheOlderFinishesLast()
    {
        using var releaseOld = new ManualResetEventSlim(false);
        using var oldEntered = new ManualResetEventSlim(false);
        var refresher = new StaleGuardedRefresher<Box>();

        var older = refresher.RefreshAsync("old-key", () =>
        {
            oldEntered.Set();
            releaseOld.Wait(Patience);
            return new Box("older");
        });
        Wait(oldEntered.WaitHandle, "the first build to start");

        // A different key, so this is a genuinely newer question — not a coalesce.
        var newer = refresher.RefreshAsync("new-key", () => new Box("newer"));
        Wait(newer, "the second build");

        Assert.IsTrue(newer.Result.IsCurrent);
        Assert.AreEqual("newer", refresher.Current?.Value);
        Assert.AreEqual("new-key", refresher.CurrentKey);

        // Now let the stale one land last. It must not overwrite the newer answer.
        releaseOld.Set();
        Wait(older, "the first build");

        Assert.IsFalse(older.Result.IsCurrent, "a superseded build must report itself stale");
        Assert.AreEqual("older", older.Result.Value?.Value, "the stale value is still returned, just not published");
        Assert.AreEqual("newer", refresher.Current?.Value, "the stale build overwrote the newer published value");
        Assert.AreEqual("new-key", refresher.CurrentKey);
        Assert.IsNull(refresher.CurrentFor("old-key"));
        Assert.AreEqual(2, refresher.BuildCount);
    }

    [TestMethod]
    public void ForceRefresh_CoalescesWithAnInFlightBuildForTheSameKey()
    {
        using var release = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var builds = 0;
        var refresher = new StaleGuardedRefresher<Box>();

        var first = refresher.RefreshAsync("k", () =>
        {
            Interlocked.Increment(ref builds);
            entered.Set();
            release.Wait(Patience);
            return new Box("once");
        });
        Wait(entered.WaitHandle, "the first build to start");
        Assert.IsTrue(refresher.IsRefreshing);

        var forced = refresher.RefreshAsync("k", () => new Box("second-build-that-must-not-happen"), force: true);
        var forcedAgain = refresher.RefreshAsync("k", () => new Box("third-build-that-must-not-happen"), force: true);

        Assert.AreSame(first, forced, "a force refresh for an in-flight key must ride the in-flight build");
        Assert.AreSame(first, forcedAgain);
        Assert.AreEqual(2, refresher.CoalescedCount);
        Assert.AreEqual(1, refresher.BuildCount);

        release.Set();
        Wait(first, "the coalesced build");

        Assert.AreEqual(1, Volatile.Read(ref builds), "the coalesced refreshes started extra builds");
        Assert.AreEqual("once", refresher.Current?.Value);
        Assert.AreEqual("once", forced.Result.Value?.Value);
        Assert.IsTrue(forced.Result.IsCurrent);
        Assert.IsFalse(refresher.IsRefreshing, "the in-flight slot was never cleared");
    }

    [TestMethod]
    public async Task NeedsRefresh_IsKeyedAndTtlBound()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var refresher = new StaleGuardedRefresher<Box>(ttl: TimeSpan.FromSeconds(5), clock: () => now);

        Assert.IsTrue(refresher.NeedsRefresh("k"), "nothing published yet");
        await refresher.RefreshAsync("k", () => new Box("v"));

        Assert.IsFalse(refresher.NeedsRefresh("k"));
        Assert.IsTrue(refresher.NeedsRefresh("other"), "a different subject is never served from another's cache");

        now = now.AddSeconds(6);
        Assert.IsTrue(refresher.NeedsRefresh("k"), "past the TTL");
    }

    [TestMethod]
    public async Task UnforcedRefresh_OnFreshCache_DoesNotBuild()
    {
        var now = DateTimeOffset.UtcNow;
        var refresher = new StaleGuardedRefresher<Box>(ttl: TimeSpan.FromSeconds(5), clock: () => now);
        await refresher.RefreshAsync("k", () => new Box("v"));

        var outcome = await refresher.RefreshAsync("k", () => new Box("rebuilt"));

        Assert.AreEqual(1, refresher.BuildCount);
        Assert.IsTrue(outcome.IsCurrent);
        Assert.AreEqual("v", outcome.Value?.Value);
    }

    [TestMethod]
    public async Task FailedBuild_ClearsTheCachedValue_AndReportsTheError()
    {
        var refresher = new StaleGuardedRefresher<Box>(ttl: TimeSpan.FromSeconds(5));
        await refresher.RefreshAsync("k", () => new Box("good"));
        Assert.IsNotNull(refresher.CurrentFor("k"));

        var outcome = await refresher.RefreshAsync("k", () => throw new InvalidOperationException("oracle down"), force: true);

        Assert.IsTrue(outcome.IsCurrent);
        Assert.IsInstanceOfType<InvalidOperationException>(outcome.Error);
        Assert.IsNull(refresher.Current, "a failed oracle must not leave a reassuring stale answer standing");
        Assert.IsNull(refresher.CurrentFor("k"));
        Assert.IsFalse(refresher.IsRefreshing);
    }

    [TestMethod]
    public void ConcurrentRefreshes_LeaveExactlyTheNewestPublished_AndNoStuckInFlight()
    {
        var refresher = new StaleGuardedRefresher<Box>(ttl: TimeSpan.Zero);
        var tasks = new List<Task<RefreshOutcome<Box>>>();
        for (var i = 0; i < 64; i++)
        {
            var n = i;
            tasks.Add(Task.Run(() => refresher.RefreshAsync("key-" + n, () => new Box("v" + n), force: true)).Unwrap());
        }

        Wait(Task.WhenAll(tasks), "64 racing refreshes");

        Assert.AreEqual(1, tasks.Count(t => t.Result.IsCurrent), "exactly one refresh may publish");
        Assert.IsFalse(refresher.IsRefreshing, "the in-flight slot leaked");
        Assert.IsNotNull(refresher.Current);
        var winner = tasks.Single(t => t.Result.IsCurrent);
        Assert.AreEqual(winner.Result.Key, refresher.CurrentKey);
        Assert.AreEqual(winner.Result.Value?.Value, refresher.Current?.Value);
    }
}
