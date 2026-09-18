using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The command poll costs one whole ssh handshake per tick, and the queue it checks is almost always empty.
// These prove the three properties the poll actually leans on: it stays fast while anything is happening,
// it genuinely stretches out once the queue has proved quiet, and one real command re-arms full speed.
[TestClass]
public sealed class RemotePollBackoffTests
{
    private static readonly TimeSpan Active = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(15);

    private static RemotePollBackoff New(int threshold = 5)
        => new(Active, Idle, threshold);

    [TestMethod]
    public void StartsAtTheFastCadence()
    {
        var backoff = New();
        Assert.AreEqual(Active, backoff.Current);
        Assert.IsFalse(backoff.IsBackedOff);
    }

    [TestMethod]
    public void HoldsTheFastCadenceUntilTheQueueHasProvedQuiet()
    {
        // A couple of empty polls is the normal idle gap BETWEEN two taps; backing off there would make the
        // web feel laggy exactly when the user is still working.
        var backoff = New(threshold: 5);
        for (var i = 1; i < 5; i++)
        {
            var interval = backoff.OnEmptyPoll();
            Assert.AreEqual(Active, interval, $"empty poll #{i} is still inside the fast window");
            Assert.IsFalse(backoff.IsBackedOff, $"empty poll #{i} must not have backed off yet");
        }
    }

    [TestMethod]
    public void StretchesPastTheFastCadenceOnceTheThresholdIsCrossed()
    {
        var backoff = New(threshold: 5);
        for (var i = 0; i < 4; i++) backoff.OnEmptyPoll();

        var atThreshold = backoff.OnEmptyPoll();   // the 5th
        Assert.IsTrue(
            atThreshold > Active,
            $"the 5th consecutive empty poll must stretch the interval past {Active}, got {atThreshold}");
        Assert.IsTrue(backoff.IsBackedOff);
    }

    [TestMethod]
    public void SettlesExactlyOnTheIdleCadenceAndNeverOvershoots()
    {
        var backoff = New(threshold: 5);
        for (var i = 0; i < 200; i++) backoff.OnEmptyPoll();

        Assert.AreEqual(Idle, backoff.Current, "a long-idle poll must settle ON the idle cadence");
        // The cap has to be a clamp, not a coincidence of the doubling sequence landing on 15s.
        Assert.IsTrue(backoff.Current <= Idle, "the interval must never exceed the idle cadence");
    }

    [TestMethod]
    public void EveryIntervalItEverReturnsStaysInsideTheConfiguredBounds()
    {
        var backoff = New(threshold: 5);
        for (var i = 0; i < 100; i++)
        {
            var interval = backoff.OnEmptyPoll();
            Assert.IsTrue(
                interval >= Active && interval <= Idle,
                $"poll #{i} returned {interval}, outside [{Active}, {Idle}]");
        }
    }

    [TestMethod]
    public void ACommandSnapsAllTheWayBackToTheFastCadence()
    {
        var backoff = New(threshold: 5);
        for (var i = 0; i < 50; i++) backoff.OnEmptyPoll();
        Assert.AreEqual(Idle, backoff.Current, "precondition: must be fully backed off first");

        var afterWork = backoff.OnCommandsReceived();

        Assert.AreEqual(Active, afterWork, "one real command must restore the FAST cadence, not a middle step");
        Assert.IsFalse(backoff.IsBackedOff);
        Assert.AreEqual(0, backoff.ConsecutiveEmpty);
    }

    [TestMethod]
    public void AfterSnappingBackItMustEarnTheBackoffAgainFromScratch()
    {
        // The snap-back has to reset the COUNTER, not just the interval — otherwise the next single empty
        // poll would jump straight back to the idle cadence and the app would feel dead after one command.
        var backoff = New(threshold: 5);
        for (var i = 0; i < 50; i++) backoff.OnEmptyPoll();
        backoff.OnCommandsReceived();

        Assert.AreEqual(Active, backoff.OnEmptyPoll(), "the first empty poll after a command must still be fast");
        Assert.IsFalse(backoff.IsBackedOff, "one empty poll must not re-enter backoff");
    }

    [TestMethod]
    public void ResetBehavesLikeActivity()
    {
        var backoff = New(threshold: 5);
        for (var i = 0; i < 50; i++) backoff.OnEmptyPoll();

        backoff.Reset();

        Assert.AreEqual(Active, backoff.Current);
        Assert.AreEqual(0, backoff.ConsecutiveEmpty);
    }

    [TestMethod]
    public void TheDefaultsAreTheOnesTheAppShips()
    {
        // A backoff whose idle cadence drifts past the relay's command wait would turn "slow" into "dropped",
        // so the shipped numbers are themselves worth pinning.
        var backoff = new RemotePollBackoff();
        Assert.AreEqual(TimeSpan.FromSeconds(3), backoff.ActiveInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(15), backoff.IdleInterval);
        Assert.IsTrue(
            backoff.IdleInterval < TimeSpan.FromSeconds(30),
            "the idle cadence must stay well inside the relay's 30s wait for a command result");
    }

    [TestMethod]
    public void RejectsAConfigurationThatCouldNeverBackOff()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RemotePollBackoff(TimeSpan.Zero, Idle));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RemotePollBackoff(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new RemotePollBackoff(Active, Idle, 0));
    }

    // ---- wiring ----
    // A backoff nothing consults is just a well-tested unused class, and every assertion above would still
    // pass. These pin that both pollers actually route their cadence through it.

    [TestMethod]
    public void TheGuiCommandPollDrivesItsTimerFromTheBackoff()
    {
        var text = ReadProductionFile("CodexLocalRetrieval.Native", "MainPage.Remote.cs");

        Assert.Contains("RemotePollBackoff", text, "the GUI poll must own a backoff");
        Assert.Contains("OnEmptyPoll()", text, "an empty lease must be reported to the backoff");
        Assert.Contains("OnCommandsReceived()", text, "a delivered command must snap the cadence back");
        Assert.DoesNotContain(
            "_cmdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) }",
            text,
            "the command timer must start from the backoff's interval, not a hardcoded 3s");
    }

    [TestMethod]
    public void TheHeadlessBridgeSchedulesItsHeartbeatIndependentlyOfTheCommandPoll()
    {
        var text = ReadProductionFile("CodexLocalRetrieval.Core", Path.Combine("Remote", "RemoteBridge.cs"));

        Assert.Contains("RemotePollBackoff", text, "the headless drain must back off too");
        Assert.DoesNotContain(
            "tick % 3 == 0",
            text,
            "the running heartbeat must not be counted off the poll tick: once the drain backs off, a "
            + "tick-counted push stretches with it and the relay ages the projection out (45s) and calls "
            + "the agent stale (25s).");
        Assert.Contains("nextPushAt", text, "the heartbeat needs its own wall-clock deadline");
        Assert.Contains("nextPollAt", text, "the drain needs its own wall-clock deadline");
    }

    [TestMethod]
    public void TheHeartbeatStaysInsideTheRelaysFreshnessWindow()
    {
        // The relay calls an agent stale at 25s (AGENT_WORKING_FRESH_MS) and ages the whole projection out
        // at 45s (ARCHIVE_INDEX_LIVE_MS). The push cadence has to leave room for a missed beat.
        Assert.IsTrue(
            RemoteBridge.RunningPushInterval * 2 < TimeSpan.FromSeconds(25),
            $"two consecutive heartbeats at {RemoteBridge.RunningPushInterval} must still land inside the "
            + "relay's 25s agent-freshness window");
    }

    private static string ReadProductionFile(string project, string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
            dir = dir.Parent;
        Assert.IsNotNull(dir, "could not locate the solution root");

        var path = Path.Combine(dir.FullName, "native", project, relativePath);
        Assert.IsTrue(File.Exists(path), $"expected production file not found: {path}");
        return File.ReadAllText(path);
    }

    [TestMethod]
    public void CutsTheIdleHandshakeRateByAtLeastFourFold()
    {
        // The whole point, stated as the number that matters: over a quiet hour, how many ssh handshakes does
        // the poll cost versus the fixed 3s cadence it replaces?
        var backoff = New(threshold: 5);
        var hour = TimeSpan.FromHours(1);

        var elapsed = TimeSpan.Zero;
        var polls = 0;
        while (elapsed < hour)
        {
            elapsed += backoff.OnEmptyPoll();
            polls++;
        }

        var fixedPolls = (int)(hour.TotalSeconds / Active.TotalSeconds);   // 1200
        Assert.IsTrue(
            polls * 4 <= fixedPolls,
            $"an idle hour must cost at most a quarter of the fixed-cadence handshakes; "
            + $"got {polls} vs {fixedPolls}");
    }

    [TestMethod]
    public void TheIdleCadenceAndRampAreTheNumbersWeReport()
    {
        // Pins the exact shape the change is claimed to produce, so the reported figures cannot quietly
        // drift away from the shipped behaviour: four polls at 3s, one 6s, one 12s, then 15s forever.
        var backoff = New(threshold: 5);
        var ramp = new List<double>();
        for (var i = 0; i < 8; i++) ramp.Add(backoff.OnEmptyPoll().TotalSeconds);

        CollectionAssert.AreEqual(
            new List<double> { 3, 3, 3, 3, 6, 12, 15, 15 },
            ramp,
            "the idle ramp must be 3,3,3,3,6,12,15,15 seconds; got " + string.Join(",", ramp));

        // Steady state: 15s => 4 polls/min, versus 20/min at the fixed 3s cadence.
        Assert.AreEqual(4, (int)(60 / backoff.Current.TotalSeconds), "idle steady state must be 4 polls/min");
        Assert.AreEqual(20, (int)(60 / Active.TotalSeconds), "the cadence it replaces was 20 polls/min");
    }

    // ---- the muxd tab probe ----
    // A failed muxd probe is not a cheap failed probe: the app answers it by relaunching muxd's scheduled
    // task, a whole schtasks -> wscript -> powershell -> pythonw chain, and then waits 1.2s for the retry.
    // Against a muxd that is alive but no longer serving its control port that relaunch can NEVER succeed
    // (muxd's single-instance mutex makes the fresh launch log "refusing to start duplicate muxd" and exit),
    // so a fixed tick is a permanent process-spawn loop - observed at ~5 spawns/minute for three days.

    [TestMethod]
    public void TheMuxdProbeStaysFastWhileMuxdAnswers()
    {
        var backoff = MuxdProbeCadence.NewBackoff();

        Assert.AreEqual(TimeSpan.FromSeconds(5), MuxdProbeCadence.Healthy);
        Assert.AreEqual(MuxdProbeCadence.Healthy, backoff.Current, "a live control port must still be polled every 5s");
    }

    [TestMethod]
    public void TheMuxdProbeToleratesABlipBeforeStretching()
    {
        var backoff = MuxdProbeCadence.NewBackoff();

        for (var i = 1; i < MuxdProbeCadence.EmptyProbesBeforeBackoff; i++)
        {
            Assert.AreEqual(
                MuxdProbeCadence.Healthy,
                backoff.OnEmptyPoll(),
                $"failure #{i} is a blip (a muxd restart) and must not stretch the probe");
            Assert.IsFalse(backoff.IsBackedOff, $"failure #{i} must not have backed off yet");
        }
    }

    [TestMethod]
    public void TheMuxdProbeStretchesToTheUnreachableCeilingAndCutsTheSpawnRateSixtyFold()
    {
        var backoff = MuxdProbeCadence.NewBackoff();
        for (var i = 0; i < 100; i++) backoff.OnEmptyPoll();

        Assert.AreEqual(TimeSpan.FromMinutes(5), MuxdProbeCadence.Unreachable);
        Assert.AreEqual(MuxdProbeCadence.Unreachable, backoff.Current, "a long-unreachable muxd must settle on the ceiling");

        // The number that matters: relaunch-triggering probes per hour, versus the fixed 5s tick.
        var hour = TimeSpan.FromHours(1).TotalSeconds;
        Assert.AreEqual(720, (int)(hour / MuxdProbeCadence.Healthy.TotalSeconds), "the cadence this replaces was 12/min");
        Assert.AreEqual(
            12,
            (int)(hour / MuxdProbeCadence.Unreachable.TotalSeconds),
            "an unreachable muxd must cost ~12 probes/hour, a 60x cut");
    }

    [TestMethod]
    public void AMuxdThatAnswersAgainSnapsStraightBackToTheFastProbe()
    {
        var backoff = MuxdProbeCadence.NewBackoff();
        for (var i = 0; i < 50; i++) backoff.OnEmptyPoll();
        Assert.AreEqual(MuxdProbeCadence.Unreachable, backoff.Current, "precondition: fully stretched first");

        var afterAnswer = backoff.OnCommandsReceived();

        Assert.AreEqual(MuxdProbeCadence.Healthy, afterAnswer, "a recovered muxd must be noticed immediately");
        Assert.IsFalse(backoff.IsBackedOff);
    }

    [TestMethod]
    public void TheGuiMuxdProbeDrivesItsTimerFromTheBackoff()
    {
        // A backoff nothing consults is a well-tested unused class. This pins the wiring that makes the 5s
        // spawn loop impossible: the tab timer takes its interval from the cadence, and a failed muxd
        // request reports the failure to it.
        var text = ReadProductionFile("CodexLocalRetrieval.Native", "MainPage.Remote.cs");

        Assert.Contains("MuxdProbeCadence.NewBackoff()", text, "the tab probe must own a muxd backoff");
        Assert.Contains("_tabBackoff.OnEmptyPoll()", text, "a failed muxd request must stretch the probe");
        Assert.Contains("_tabBackoff.OnCommandsReceived()", text, "an answered muxd request must snap it back");
        Assert.DoesNotContain(
            "_tabTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) }",
            text,
            "the tab timer must start from the cadence's interval, not a hardcoded 5s");
    }
}
