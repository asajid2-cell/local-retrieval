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
    public void Build_RunsOffTheCallersThread()
    {
        var refresher = new StaleGuardedRefresher<Box>();
        var callerThread = Environment.CurrentManagedThreadId;

        var task = refresher.RefreshAsync(
            "k",
            () => new Box("built") { ThreadId = Environment.CurrentManagedThreadId });
        Wait(task, "the default-dispatched build");
        var outcome = task.Result;

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
    public async Task RecomputedKey_DoesNotMakeACompletedRefreshLookUnverified()
    {
        var refresher = new StaleGuardedRefresher<Box>();

        var first = await refresher.RefreshAsync("session|before", () => new Box("verified"), force: true);
        Assert.IsTrue(first.IsCurrent);
        Assert.IsNotNull(refresher.CurrentFor("session|before"));

        // A sync changes the key before the caller consumes the result. This is the exact reclaim shape:
        // asking CurrentFor(the-new-key) must not be treated as proof that the completed refresh was stale.
        var currentKey = "session|after-sync";
        Assert.IsNull(refresher.CurrentFor(currentKey));
        Assert.AreEqual("verified", first.Value?.Value);
        Assert.IsTrue(first.IsCurrent);

    }

    [TestMethod]
    public async Task SupersededRefresh_IsNotConsumedAsPostState()
    {
        using var releaseOld = new ManualResetEventSlim(false);
        using var oldEntered = new ManualResetEventSlim(false);
        var refresher = new StaleGuardedRefresher<Box>();
        var old = refresher.RefreshAsync("session", () =>
        {
            oldEntered.Set();
            releaseOld.Wait(Patience);
            return new Box("old");
        }, force: true);
        Wait(oldEntered.WaitHandle, "the first build to start");
        var current = await refresher.RefreshAsync("session|new", () => new Box("current"), force: true);
        releaseOld.Set();
        await old;

        Assert.IsTrue(current.IsCurrent);
        Assert.IsFalse(old.Result.IsCurrent);
        Assert.AreEqual("current", current.Value?.Value);
    }

    [TestMethod]
    public async Task InvalidateThenSameKeyRefresh_StartsNewBuild_AndOldCompletionCannotClearIt()
    {
        var oldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource<Box>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNew = new TaskCompletionSource<Box>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresher = new StaleGuardedRefresher<Box>();

        var old = refresher.RefreshAsync("k", () =>
        {
            oldStarted.SetResult(true);
            return releaseOld.Task.GetAwaiter().GetResult();
        }, force: true);
        Assert.IsTrue(await Task.WhenAny(oldStarted.Task, Task.Delay(Patience)) == oldStarted.Task);

        refresher.Invalidate();
        var current = refresher.RefreshAsync("k", () =>
        {
            newStarted.SetResult(true);
            return releaseNew.Task.GetAwaiter().GetResult();
        }, force: true);

        Assert.IsTrue(
            await Task.WhenAny(newStarted.Task, Task.Delay(Patience)) == newStarted.Task,
            "invalidation must prevent coalescing onto the superseded same-key build");
        Assert.AreEqual(2, refresher.BuildCount);
        Assert.IsTrue(refresher.IsRefreshing, "the replacement build must still be in flight");

        releaseOld.SetResult(new Box("old"));
        var oldOutcome = await old;
        Assert.IsFalse(oldOutcome.IsCurrent);
        Assert.IsTrue(refresher.IsRefreshing, "the superseded old build cleared the replacement slot");
        Assert.IsNull(refresher.Current);

        releaseNew.SetResult(new Box("current"));
        var currentOutcome = await current;
        Assert.IsTrue(currentOutcome.IsCurrent);
        Assert.AreEqual("current", refresher.Current?.Value);
        Assert.IsFalse(refresher.IsRefreshing);
        Assert.AreEqual("old", oldOutcome.Value?.Value);
        Assert.AreEqual("current", currentOutcome.Value?.Value);
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
    public void ConcurrentRefreshes_LeaveOneConsistentPublishedAnswer_AndNoStuckInFlight()
    {
        const int Racers = 64;
        var refresher = new StaleGuardedRefresher<Box>(ttl: TimeSpan.Zero);
        var tasks = new List<Task<RefreshOutcome<Box>>>();
        for (var i = 0; i < Racers; i++)
        {
            var n = i;
            tasks.Add(Task.Run(() => refresher.RefreshAsync("key-" + n, () => new Box("v" + n), force: true)));
        }

        Wait(Task.WhenAll(tasks), $"{Racers} racing refreshes");
        var outcomes = tasks.Select(t => t.Result).ToList();

        // Deliberately NOT "exactly one publishes": these are 64 distinct keys, and nothing forces them to
        // overlap. If refresh i lands before refresh i+1 is even dispatched, i legitimately publishes and
        // reports IsCurrent — so anywhere from 1 to 64 outcomes may be current. Asserting 1 is a coin flip.
        // What must hold under EVERY interleaving is below.
        Assert.AreEqual(Racers, refresher.BuildCount, "distinct keys never coalesce — every refresh must build");
        Assert.IsTrue(outcomes.Any(o => o.IsCurrent), "the last refresh to claim a sequence must publish");
        Assert.IsFalse(refresher.IsRefreshing, "the in-flight slot leaked");

        // The published answer belongs to exactly one key, and it is that key's own value — a superseded
        // build must never publish another key's answer under the winner's name.
        Assert.IsNotNull(refresher.Current, "nothing was published at all");
        var currentKey = refresher.CurrentKey;
        Assert.IsTrue(
            currentKey.StartsWith("key-", StringComparison.Ordinal),
            $"the published key '{currentKey}' is not one of key-0..key-{Racers - 1}");
        var parsed = int.TryParse(currentKey.Substring(4), out var winner);
        Assert.IsTrue(
            parsed && winner >= 0 && winner < Racers,
            $"the published key '{currentKey}' is not one of key-0..key-{Racers - 1}");
        Assert.AreEqual("v" + winner, refresher.Current!.Value, "the published value belongs to another key");

        // Every outcome carries its own key's value, whether or not it won.
        foreach (var outcome in outcomes)
        {
            Assert.IsNotNull(outcome.Value, $"{outcome.Key} returned no value");
            Assert.AreEqual(
                "v" + outcome.Key.Substring(4),
                outcome.Value!.Value,
                $"{outcome.Key} was answered with another key's build");
        }

        var published = outcomes.Where(o => string.Equals(o.Key, currentKey, StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, published.Count, "the published key must belong to exactly one refresh");
        Assert.IsTrue(published[0].IsCurrent, "the refresh that owns the published value reported itself stale");
    }
}
