using System.Diagnostics;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The search box fires TextChanged on every keystroke, and each one used to cost a full filter pass.
// TrailingDebouncer is what makes typing cheap: a burst collapses into one invocation carrying the last
// value. These prove the three properties the UI actually leans on — collapse, it still fires after a
// quiet gap (not just once ever), and a disposed page never fires a stale callback.
[TestClass]
public sealed class DebounceTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);

    [TestMethod]
    public void RapidPostsCollapseToOneInvocationCarryingTheLastValue()
    {
        var invocations = new List<string>();
        var fired = new ManualResetEventSlim(false);
        using var debouncer = new TrailingDebouncer<string>(
            value => { lock (invocations) { invocations.Add(value); } fired.Set(); },
            Delay);

        for (var i = 0; i < 10; i++) debouncer.Post("q" + i);

        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)), "debounced callback never fired");
        // Give any extra (wrongly scheduled) invocation room to show up before we count.
        Thread.Sleep(400);
        lock (invocations)
        {
            Assert.AreEqual(1, invocations.Count, "10 posts inside the window must collapse to ONE call");
            Assert.AreEqual("q9", invocations[0], "the surviving call must carry the LAST posted value");
        }
    }

    [TestMethod]
    public void PostAfterAQuietPeriodFiresAgainWithin400Ms()
    {
        var invocations = new List<string>();
        var fired = new ManualResetEventSlim(false);
        using var debouncer = new TrailingDebouncer<string>(
            value => { lock (invocations) { invocations.Add(value); } fired.Set(); },
            Delay);

        debouncer.Post("first");
        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)), "first burst never fired");
        fired.Reset();

        // Quiet gap: the debouncer must re-arm, not latch after one shot.
        Thread.Sleep(300);

        var clock = Stopwatch.StartNew();
        debouncer.Post("second");
        Assert.IsTrue(fired.Wait(TimeSpan.FromMilliseconds(400)),
            "a post after a quiet period must fire within 400 ms");
        clock.Stop();

        lock (invocations)
        {
            Assert.AreEqual(2, invocations.Count);
            Assert.AreEqual("second", invocations[1]);
        }
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromMilliseconds(400),
            $"trailing fire took {clock.ElapsedMilliseconds} ms, expected < 400 ms");
    }

    [TestMethod]
    public void DisposeCancelsThePendingInvocation()
    {
        var invocations = 0;
        var debouncer = new TrailingDebouncer<string>(_ => Interlocked.Increment(ref invocations), Delay);

        debouncer.Post("doomed");
        debouncer.Dispose();

        Thread.Sleep(500);
        Assert.AreEqual(0, Volatile.Read(ref invocations), "disposal must drop the pending callback");

        // And a post to a disposed debouncer is a no-op, not a throw.
        debouncer.Post("also doomed");
        Thread.Sleep(300);
        Assert.AreEqual(0, Volatile.Read(ref invocations));
    }

    [TestMethod]
    public void CallbackRunsThroughTheInjectedPostSoTheUiThreadStaysInCharge()
    {
        var posted = 0;
        var fired = new ManualResetEventSlim(false);
        using var debouncer = new TrailingDebouncer<string>(
            _ => fired.Set(),
            Delay,
            run => { Interlocked.Increment(ref posted); run(); });

        debouncer.Post("x");

        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, Volatile.Read(ref posted), "the injected dispatcher must be the only path to the callback");
    }
}
