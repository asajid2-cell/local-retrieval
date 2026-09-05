using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The C1 tiered force-clear and the reclaim flow that consumes it.
//
// The tiers are the whole safety story, so they are tested against the real claim files (temp roots, GUID
// directories) rather than a mock: a claim's OwnerPid is the APP that reserved the launch, and the
// `cmd /k -> claude` tree it started outlives that app. Every "may we delete this reservation?" answer below
// is one side of that asymmetry.
[TestClass]
public class SessionReclaimTests
{
    private readonly List<string> _dirs = new();
    private readonly List<Process> _children = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        foreach (var dir in _dirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    // ---- tier 1: expired, and the [F#2]-amended dead-owner rule -------------------------------------

    [TestMethod]
    public void ExpiredClaim_ClearsFreely()
    {
        var root = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-expired", ownerPid: 4242, now.AddMinutes(-5), now.AddMinutes(-3));

        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence { Now = now, IsProcessAlive = _ => true },
            out var detail);

        Assert.AreEqual(ReclaimClearOutcome.Cleared, outcome, detail);
        Assert.IsFalse(File.Exists(claim.Path));
    }

    // Unexpired + owner dead, and the wrapper the owner record ties to this claim is ALSO confirmed dead.
    // That is the only shape in which a dead owner clears a live reservation.
    [TestMethod]
    public void UnexpiredDeadOwner_WithConfirmedDeadWrapper_Clears()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-dead-wrapper", ownerPid: 4242, now, now.AddMinutes(2));
        WriteOwnerRecord(records, "session-dead-wrapper", wrapperPid: 5150, wrapperStart: now.AddMinutes(-1));

        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence
            {
                Now = now,
                RecordOptions = RecordOptions(records),
                IsProcessAlive = _ => false,                    // the app that reserved it is gone
                AliveIdentity = _ => (false, default),          // ...and so is the wrapper it started
            },
            out var detail);

        Assert.AreEqual(ReclaimClearOutcome.Cleared, outcome, detail);
        Assert.IsFalse(File.Exists(claim.Path));
    }

    // [F#2] The app died inside the grace window but its `cmd /k -> claude` tree is still alive and the agent
    // may be seconds from appearing. Clearing here reopens the exact double-writer race the claim exists for.
    [TestMethod]
    public void UnexpiredDeadOwner_WithLiveWrapper_IsRefused()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var wrapperStart = now.AddMinutes(-1);
        var claim = WriteClaim(root, "session-live-wrapper", ownerPid: 4242, now, now.AddMinutes(2));
        WriteOwnerRecord(records, "session-live-wrapper", wrapperPid: 5150, wrapperStart: wrapperStart);

        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence
            {
                Now = now,
                RecordOptions = RecordOptions(records),
                IsProcessAlive = _ => false,
                AliveIdentity = _ => (true, wrapperStart.UtcDateTime),   // same pid, same identity: still ours
            },
            out var detail);

        Assert.AreEqual(ReclaimClearOutcome.RefusedAliveOwner, outcome, detail);
        Assert.IsTrue(File.Exists(claim.Path));
        StringAssert.Contains(detail, "confirmed-dead");
    }

    // [F#2] No record at all is the same uncertainty: there is nothing to prove the writer never appeared.
    [TestMethod]
    public void UnexpiredDeadOwner_WithNoOwnerRecord_IsRefused()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-no-record", ownerPid: 4242, now, now.AddMinutes(2));

        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence { Now = now, RecordOptions = RecordOptions(records), IsProcessAlive = _ => false },
            out var detail);

        Assert.AreEqual(ReclaimClearOutcome.RefusedAliveOwner, outcome, detail);
        Assert.IsTrue(File.Exists(claim.Path));
    }

    // ---- tier 2: unexpired + alive owner ------------------------------------------------------------

    [TestMethod]
    public void UnexpiredAliveOwner_IsRefused_ThenClearsOnTiedKill_AndOnWaitedOutGrace()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-alive-owner", ownerPid: 4242, now, now.AddMinutes(2));
        var alive = new ReclaimEvidence { Now = now, RecordOptions = RecordOptions(records), IsProcessAlive = _ => true };

        Assert.AreEqual(
            ReclaimClearOutcome.RefusedAliveOwner,
            SessionLaunchClaims.TryReclaimClear(claim, alive, out var refusedDetail),
            "an unexpired claim with a live owner is the double-writer case; it must refuse by default");
        StringAssert.Contains(refusedDetail, "alive, verified");
        Assert.IsTrue(File.Exists(claim.Path));

        // (a) a process concretely tied to THIS claim was killed and confirmed exited.
        var tiedKill = alive with
        {
            ConfirmedExitedPids = new[] { new ReclaimKilledPid(9001, null, new[] { "session-alive-owner" }) }
        };
        Assert.AreEqual(
            ReclaimClearOutcome.Cleared,
            SessionLaunchClaims.TryReclaimClear(claim, tiedKill, out var killDetail),
            killDetail);
        Assert.IsFalse(File.Exists(claim.Path));

        // (b) the grace was waited out — same claim, rewritten, same live owner.
        var again = WriteClaim(root, "session-alive-owner", ownerPid: 4242, now, now.AddMinutes(2));
        Assert.AreEqual(
            ReclaimClearOutcome.Cleared,
            SessionLaunchClaims.TryReclaimClear(claim: again, alive with { GraceWaitedOut = true }, out var waitedDetail),
            waitedDetail);
        Assert.IsFalse(File.Exists(again.Path));
    }

    // An unrelated kill is not evidence. This is what keeps tier 2 from degrading into "we killed SOMETHING,
    // so clear everything".
    [TestMethod]
    public void UnexpiredAliveOwner_WithUntiedKill_IsStillRefused()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-untied", ownerPid: 4242, now, now.AddMinutes(2));

        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence
            {
                Now = now,
                RecordOptions = RecordOptions(records),
                IsProcessAlive = _ => true,
                ConfirmedExitedPids = new[] { new ReclaimKilledPid(9001, null, new[] { "some-other-session" }) },
            },
            out var detail);

        Assert.AreEqual(ReclaimClearOutcome.RefusedAliveOwner, outcome, detail);
        Assert.IsTrue(File.Exists(claim.Path));
    }

    // [F#1] THE non-negotiable. The retired override at MainPage.Integrity.cs:166 force-cleared claims owned by
    // this app's own pid — an unexpired, alive-owner tier-2 clear in disguise, and the most common reclaim
    // trigger. Own-app claims classify exactly like anyone else's.
    [TestMethod]
    public void OwnAppClaim_Unexpired_WithThisProcessAlive_IsRefusedLikeAnyOther()
    {
        var root = TempDir();
        var records = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-own-app", ownerPid: Environment.ProcessId, now, now.AddMinutes(2));

        // No IsProcessAlive seam: the REAL liveness probe runs against the real, running test process.
        var outcome = SessionLaunchClaims.TryReclaimClear(
            claim,
            new ReclaimEvidence { Now = now, RecordOptions = RecordOptions(records) },
            out var detail);

        Assert.AreEqual(
            ReclaimClearOutcome.RefusedAliveOwner,
            outcome,
            "a claim owned by this app's own live pid must refuse exactly like any other alive owner");
        StringAssert.Contains(detail, Environment.ProcessId.ToString());
        Assert.IsTrue(File.Exists(claim.Path));
    }

    // ---- tier 3: a launcher is mid-acquire ----------------------------------------------------------

    // TryAcquire keeps the claim stream open with FileShare.Read for the WHOLE acquire, so the quarantine move
    // hits a real sharing violation. That is a legitimate launch in flight, NOT an error to force past.
    [TestMethod]
    public void ClaimHeldOpenByALauncher_ReportsLaunchInFlight_AndLeavesTheFileIntact()
    {
        var dir = TempDir();
        var now = DateTimeOffset.UtcNow;
        var options = new SessionLaunchClaims.Options(RootDirectory: dir, Now: now);

        Assert.IsTrue(
            SessionLaunchClaims.TryAcquire("session-in-flight", null, "test", out var held, out var detail, _ => false, options),
            detail);
        using (held)   // the launcher is STILL holding the stream
        {
            var claim = SessionLaunchClaims.ReadClaimsForSession("session-in-flight", options: options).Single();

            // Grace waited out, so the tiers permit the attempt — the move itself is what refuses.
            var outcome = SessionLaunchClaims.TryReclaimClear(
                claim,
                new ReclaimEvidence { Now = now, GraceWaitedOut = true },
                out var clearDetail);

            Assert.AreEqual(ReclaimClearOutcome.LaunchInFlight, outcome, clearDetail);
            StringAssert.Contains(clearDetail, "already in flight");
            Assert.IsTrue(File.Exists(claim.Path), "an in-flight launch's reservation must survive untouched");
        }
    }

    // ---- explicit launch lease helper -----------------------------------------------------------------

    // Explicit Resume/Continue owns new-session creation. Only `isSessionLive` is forced; the file race is
    // never bypassed, so a concurrent launcher that got there first still wins.
    [TestMethod]
    public void ReclaimRelaunch_LosingTheCreateNewRace_ReportsInsteadOfDoubleLaunching()
    {
        var dir = TempDir();
        var events = TempDir();
        var options = new SessionLaunchClaims.Options(RootDirectory: dir);

        // Somebody else's launch is already reserved and holding the file.
        Assert.IsTrue(
            SessionLaunchClaims.TryAcquire("session-race", null, "other launcher", out var other, out var detail, _ => false, options),
            detail);
        using (other)
        {
            var request = Request("session-race");
            var acquired = SessionReclaim.TryAcquireReclaimLease(
                request,
                new SessionLaunchGovernorOptions(
                    ClaimOptions: options,
                    EventOptions: new SessionEventLedger.Options(RootDirectory: events)),
                out var lease,
                out var raceDetail,
                out var lostRace);

            Assert.IsFalse(acquired, "the CreateNew race must not be bypassed by reclaim");
            Assert.IsNull(lease);
            Assert.IsTrue(lostRace);
            StringAssert.Contains(raceDetail, SessionReclaim.LostRaceDetail);
        }

        // ...and with the race free, the same forced-liveness acquire succeeds.
        Assert.IsTrue(SessionReclaim.TryAcquireReclaimLease(
            Request("session-race"),
            new SessionLaunchGovernorOptions(
                ClaimOptions: options,
                EventOptions: new SessionEventLedger.Options(RootDirectory: events)),
            out var freshLease,
            out var freshDetail,
            out _), freshDetail);
        freshLease!.Dispose();
    }

    // ---- wait/re-read behavior ------------------------------------------------------------------------

    [TestMethod]
    public async Task Reclaim_WaitUsesInjectedClock_RereadsClaim_AndClearsOriginalOnly()
    {
        var root = TempDir();
        var now = DateTimeOffset.UtcNow;
        var current = now;
        var claim = WriteClaim(root, "session-wait", ownerPid: 4242, now, now.AddSeconds(2));
        var replacement = false;
        var delays = 0;
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { "session-wait" },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: root, Now: now),
            Now = now,
            Clock = () => current,
            Delay = (span, _) =>
            {
                current += span;
                if (!replacement && current >= now.AddSeconds(1))
                {
                    replacement = true;
                    File.WriteAllText(claim.Path, JsonSerializer.Serialize(new
                    {
                        SessionId = "session-wait",
                        CandidateIds = new[] { "session-wait" },
                        OwnerPid = 5151,
                        OwnerProcess = "replacement",
                        CreatedUtc = now.AddSeconds(1),
                        ExpiresUtc = now.AddSeconds(3),
                        Reason = "replacement"
                    }));
                }
                delays++;
                return Task.CompletedTask;
            },
            OnRefusal = _ => Task.FromResult(ReclaimRefusalChoice.WaitForExpiry),
            Kill = _ => new RunningSessions.KillResult(true, "no owners", Array.Empty<ReclaimKilledPid>()),
        });

        Assert.IsGreaterThan(0, delays);
        Assert.AreEqual(ReclaimClearOutcome.Failed, report.Claims.Single().Outcome);
        StringAssert.Contains(report.Claims.Single().Detail, "replaced");
        Assert.IsTrue(File.Exists(claim.Path), "a replacement reservation must not be cleared by stale grace evidence");
    }

    [TestMethod]
    public async Task Reclaim_CancellationDuringWaitCompletesAndPreservesClaim()
    {
        var root = TempDir();
        var now = DateTimeOffset.UtcNow;
        var claim = WriteClaim(root, "session-cancel-wait", ownerPid: 4242, now, now.AddMinutes(2));
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportTask = SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { "session-cancel-wait" },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: root, Now: now),
            Now = now,
            OnRefusal = _ => Task.FromResult(ReclaimRefusalChoice.WaitForExpiry),
            Cancellation = cancellation.Token,
            Delay = async (_, token) =>
            {
                waiting.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            Kill = _ => new RunningSessions.KillResult(true, "no owners", Array.Empty<ReclaimKilledPid>()),
        });

        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        try
        {
            await reportTask;
            Assert.Fail("reclaim should propagate cancellation from the wait");
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        Assert.IsTrue(File.Exists(claim.Path));
    }

    // ---- mux custody --------------------------------------------------------------------------------

    [TestMethod]
    public async Task Reclaim_KillsLiveMuxBeforeProcessCleanup_AndDoesNotLaunch()
    {
        var order = new List<string>();
        var claimRoot = TempDir();
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { "session-mux-reclaim" },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            KillMux = () =>
            {
                order.Add("mux-kill");
                return Task.FromResult(new ReclaimMuxResult(true, true, true, "killed canonical mux"));
            },
            Kill = _ =>
            {
                order.Add("process-kill");
                return new RunningSessions.KillResult(true, "no process owner", Array.Empty<ReclaimKilledPid>());
            },
        });

        Assert.IsTrue(report.MuxOk, report.MuxDetail);
        Assert.IsFalse(report.Relaunched);
        CollectionAssert.AreEqual(new[] { "mux-kill", "process-kill" }, order);
        StringAssert.Contains(report.Headline, "ready to continue");
    }

    [TestMethod]
    public async Task Reclaim_WhenNothingExists_ReportsNoOpInsteadOfClaimingOwnersStopped()
    {
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { "session-already-clear" },
            Kill = _ => new RunningSessions.KillResult(true, "already gone", Array.Empty<ReclaimKilledPid>()),
        });

        Assert.IsFalse(report.Changed);
        StringAssert.Contains(report.Headline, "Nothing to reclaim");
        StringAssert.Contains(report.Headline, "no owner");
        Assert.DoesNotContain("owners stopped", report.Headline);
    }

    [TestMethod]
    public async Task Reclaim_RefusesCleanupWhenLiveMuxKillFails_AndPreservesOwnership()
    {
        var processCleanup = 0;
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { "session-mux-kill-fails" },
            KillMux = () => Task.FromResult(new ReclaimMuxResult(true, true, false, "muxd kill was not verified")),
            Kill = _ =>
            {
                Interlocked.Increment(ref processCleanup);
                return new RunningSessions.KillResult(true, "must not run", Array.Empty<ReclaimKilledPid>());
            },
        });

        Assert.IsFalse(report.MuxOk);
        StringAssert.Contains(report.MuxDetail, "not verified");
        Assert.IsFalse(report.Relaunched);
        Assert.AreEqual(0, processCleanup, "failed mux takeover must preserve process/claim custody");
        StringAssert.Contains(report.Headline, "mux");
    }

    [TestMethod]
    public void PruneStaleMuxCurrent_DropsConfirmedAbsentTabs_KeepsLiveOnes_AndDoesNothingWhenMuxdCannotAnswer()
    {
        Dictionary<string, MuxTabRecord> Build() => new()
        {
            ["gone-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = "sid-a" } },
            ["live-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = "sid-b" } },
            ["other-chat"] = new MuxTabRecord { Current = new MuxTabChat { Id = "someone-else" } },
        };

        var history = Build();
        var pruned = SessionReclaim.PruneStaleMuxCurrent(history, new[] { "sid-a", "sid-b" }, new[] { "live-tab" });

        CollectionAssert.AreEquivalent(new[] { "gone-tab" }, pruned.ToArray());
        Assert.IsNull(history["gone-tab"].Current);
        Assert.IsNotNull(history["live-tab"].Current, "a live mux tab is not ours to clear");
        Assert.IsNotNull(history["other-chat"].Current, "another chat's custody is untouched");

        // muxd unreachable: "we couldn't ask" is never "the tab is gone".
        var unaskable = Build();
        Assert.IsEmpty(SessionReclaim.PruneStaleMuxCurrent(unaskable, new[] { "sid-a", "sid-b" }, null));
        Assert.IsNotNull(unaskable["gone-tab"].Current);
    }


    // ---- integration: a real process tree, killed, cleared, and pruned -------------------------------

    // Reclaim stops at cleanup. A later explicit Resume owns any new terminal/session creation.


    // The throwaway-session bar: a real cmd.exe tree holding a real transcript open, a real owner record and a
    // real unexpired claim tied to it, run through the REAL reclaim core (no user chats anywhere). The claim's
    // owner is this live test process, so nothing but the tier-2 kill evidence can clear it — which is exactly
    // the [F#1] shape the retired own-pid override used to short-circuit.
    [TestMethod]
    public async Task ReclaimCore_KillsTheTree_ClearsViaTierTwoEvidence_PrunesMux_AndDoesNotLaunch()
    {
        RequireWindows();
        var claimRoot = TempDir();
        var recordRoot = TempDir();
        var now = DateTimeOffset.UtcNow;

        var (sid, child) = StartChildHoldingTranscript();
        var childStart = child.StartTime.ToUniversalTime();
        WriteOwnerRecord(recordRoot, sid, child.Id, new DateTimeOffset(childStart, TimeSpan.Zero));
        var claim = WriteClaim(claimRoot, sid, ownerPid: Environment.ProcessId, now, now.AddMinutes(2));

        var history = new Dictionary<string, MuxTabRecord>
        {
            ["stale-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = sid } },
        };

        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { sid },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            RecordOptions = RecordOptions(recordRoot),
            // muxd confirms the tab is absent (empty live list).
            PruneMuxCurrent = ids => SessionReclaim.PruneStaleMuxCurrent(history, ids, Array.Empty<string>()),
        });

        // 1. the tree actually died, checked by identity rather than by bare pid.
        Assert.IsTrue(report.KillOk, "the recorded wrapper should have been killed: " + report.KillDetail);
        Assert.IsTrue(child.WaitForExit(20_000), "the child tree did not exit");
        Assert.IsTrue(
            RunningSessions.HasExitedByIdentity(child.Id, childStart),
            "the killed process must be gone BY IDENTITY, not merely absent from a pid check");
        Assert.IsTrue(
            report.ConfirmedExited.Any(p => p.Pid == child.Id),
            "the killed wrapper must appear as confirmed-exited evidence");

        // 2. the claim cleared, and only tier-2 evidence could have done it.
        var claimResult = report.Claims.Single();
        Assert.AreEqual(ReclaimClearOutcome.Cleared, claimResult.Outcome, claimResult.Detail);
        StringAssert.Contains(claimResult.Detail, "confirmed exited");

        // 3. stale mux custody pruned.
        CollectionAssert.AreEquivalent(new[] { "stale-tab" }, report.PrunedMuxTabs.ToArray());
        Assert.IsNull(history["stale-tab"].Current);

        // 4. reclaim is cleanup-only: it leaves no new reservation or terminal behind.
        Assert.IsFalse(report.Relaunched);
        Assert.IsFalse(report.LostLaunchRace);
        Assert.IsFalse(File.Exists(claim.Path), "cleanup must remove the old reservation without creating a replacement");
        Assert.IsEmpty(SessionLaunchClaims.ReadClaimsForSession(
            sid,
            options: new SessionLaunchClaims.Options(RootDirectory: claimRoot)));
        StringAssert.Contains(report.Headline, "ready to continue");
    }

    // ---- the reclaim-strand repro: a clear that FAILS must be reported, not swallowed ----------------

    // A real user hit this: reclaim cleared the danger (killed the owner) but its claim-file cleanup FAILED with
    // a file-in-use error, so the reservation stayed on disk. The old headline collapsed that to the generic
    // "A launch reservation could not be cleared; nothing was relaunched." — the operator never saw WHY. Here we
    // force a genuine Failed clear (an expired claim whose quarantine delete throws because the file is
    // read-only) and assert the report surfaces the actual cleanup detail. Then we assert the post-state — the
    // reservation still present while overall severity has dropped to warn — still leaves Reclaim available.
    [TestMethod]
    public async Task Reclaim_WhenAClaimClearFails_SurfacesTheFailureDetail_AndKeepsReclaimAvailable()
    {
        RequireWindows();
        var claimRoot = TempDir();
        var now = DateTimeOffset.UtcNow;
        const string sid = "session-clear-fails";

        // An expired reservation (tier 1 clears it freely) whose file we mark read-only, so the quarantine move
        // succeeds but the follow-up delete throws — the generic-catch Failed path with a real cleanup message.
        var claim = WriteClaim(claimRoot, sid, ownerPid: 4242, now.AddMinutes(-5), now.AddMinutes(-3));
        File.SetAttributes(claim.Path, File.GetAttributes(claim.Path) | FileAttributes.ReadOnly);

        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { sid },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            Now = now,
            // Hermetic: no real owner to hunt; the clear failure is the whole point.
            Kill = _ => new RunningSessions.KillResult(true, "no owners", Array.Empty<ReclaimKilledPid>()),
        });

        // Reclaim is cleanup-only even when the reservation cleanup itself fails.
        Assert.IsFalse(report.Relaunched);

        // The clear failed and reported the actual reason...
        var failed = report.Claims.Single();
        Assert.AreEqual(ReclaimClearOutcome.Failed, failed.Outcome, failed.Detail);
        StringAssert.Contains(failed.Detail, "claim cleanup failed", failed.Detail);

        // ...and that reason now reaches the headline instead of the old generic line.
        StringAssert.Contains(report.Headline, failed.Detail);
        Assert.AreNotEqual(
            "A launch reservation could not be cleared; nothing was relaunched.",
            report.Headline,
            "the failing reason must be surfaced, not collapsed to the generic line");

        // Nothing was launched over an unresolved reservation.
        Assert.IsFalse(report.Relaunched);
        Assert.IsTrue(report.AnyClaimBlocking);

        // Post-state: the danger is gone but the reservation is still on disk, so severity is only "warn". The
        // Reclaim affordance must survive that drop — the exact state where the button used to disappear.
        var postState = new SessionIntegritySummary
        {
            Severity = "warn",
            LaunchClaims = new[]
            {
                new SessionIntegrityClaim(sid, new[] { sid }, 0, "seed", "", "test", Expired: true, failed.Path),
            },
        };
        Assert.IsTrue(SessionIntegrity.ReclaimAvailable(postState),
            "a still-present reservation must keep Reclaim available even after severity drops to warn");
    }

    // ---- H1 + [F#5] ---------------------------------------------------------------------------------

    // Governed mux creates refuse (and say so in the ledger) when the lease cannot be taken...
    [TestMethod]
    public void GovernedMuxCreate_RefusesAndRecordsWhenTheLeaseCannotBeAcquired()
    {
        var claimRoot = TempDir();
        var eventRoot = TempDir();
        var options = new SessionLaunchClaims.Options(RootDirectory: claimRoot);
        var governor = new SessionLaunchGovernor(new SessionLaunchGovernorOptions(
            ClaimOptions: options,
            EventOptions: new SessionEventLedger.Options(RootDirectory: eventRoot),
            IsSessionLive: _ => false));

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-mux", null, "someone else", out var blocker, out var d, _ => false, options), d);
        using (blocker)
        {
            Assert.IsFalse(
                governor.TryAcquire(MuxRequest("session-mux"), out var lease, out var detail),
                "an ungoverned mux create is exactly the check-then-spawn race the claim system exists to close");
            Assert.IsNull(lease);
            StringAssert.Contains(detail, "launch already pending");
        }

        var events = SessionEventLedger.ReadRecent(200, new SessionEventLedger.Options(RootDirectory: eventRoot));
        Assert.IsTrue(
            events.Any(e => e.Kind == "mux.refused.claim"),
            "a refused mux lease must reach the ledger under its own kind");
    }

    // ...and [F#5]: without this, governing the handoff paths turns a working /tomux into a two-minute refusal,
    // because the previous app launch's own retained claim blocks it.
    [TestMethod]
    public void VerifiedKill_ClearsTheTiedRetainedClaim_SoAHandoffProceedsInsideTheRetentionWindow()
    {
        var claimRoot = TempDir();
        var recordRoot = TempDir();
        var eventRoot = TempDir();
        var options = new SessionLaunchClaims.Options(RootDirectory: claimRoot);
        var governor = new SessionLaunchGovernor(new SessionLaunchGovernorOptions(
            ClaimOptions: options,
            EventOptions: new SessionEventLedger.Options(RootDirectory: eventRoot),
            IsSessionLive: _ => false));

        // A previous app launch: claim retained for the grace window, owner record naming its wrapper.
        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-handoff", null, "previous launch", out var prior, out var d, _ => false, options), d);
        prior!.RetainUntilExpiry();
        prior.Dispose();
        WriteOwnerRecord(recordRoot, "session-handoff", wrapperPid: 7777, wrapperStart: DateTimeOffset.UtcNow.AddMinutes(-1));

        // Inside the retention window the handoff is refused by our OWN leftover reservation.
        Assert.IsFalse(governor.TryAcquire(MuxRequest("session-handoff"), out _, out var blockedDetail));
        StringAssert.Contains(blockedDetail, "launch already pending");

        // The handoff verified pid 7777 out, which is the tier-2 evidence tied to that claim.
        var cleared = SessionReclaim.ClearClaimsTiedToVerifiedKills(
            "session-handoff",
            null,
            new[] { 7777 },
            options,
            RecordOptions(recordRoot));
        Assert.AreEqual(ReclaimClearOutcome.Cleared, cleared.Single().Outcome, cleared.Single().Detail);

        // ...so the handoff now proceeds instead of waiting out two minutes.
        Assert.IsTrue(governor.TryAcquire(MuxRequest("session-handoff"), out var lease, out var afterDetail), afterDetail);
        lease!.Dispose();
    }

    // An UNVERIFIED pid is not evidence: nothing may clear a live reservation on the strength of a kill we did
    // not watch complete.
    [TestMethod]
    public void ClearClaimsTiedToVerifiedKills_WithNoPids_ClearsNothing()
    {
        var claimRoot = TempDir();
        var options = new SessionLaunchClaims.Options(RootDirectory: claimRoot);
        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-unverified", null, "prior", out var prior, out var d, _ => false, options), d);
        prior!.RetainUntilExpiry();
        prior.Dispose();
        var path = SessionLaunchClaims.ReadClaimsForSession("session-unverified", options: options).Single().Path;

        Assert.IsEmpty(SessionReclaim.ClearClaimsTiedToVerifiedKills("session-unverified", null, Array.Empty<int>(), options));
        Assert.IsTrue(File.Exists(path));
    }

    // ---- source-level wiring (ArchitectureLaunchSurfaceTests style) ----------------------------------

    // Muxd is the sole reservation authority for mux-hosted writers. The GUI must not acquire the native
    // claim first because muxd uses the same claim root and correctly sees that foreign claim as pending.
    [TestMethod]
    public void MuxHostedCreation_DelegatesReservationToMuxd_AndNeverUsesNativeGovernor()
    {
        var root = FindRepoRoot();
        var remote = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"));
        var integrity = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Integrity.cs"));

        var muxStart = remote.IndexOf("private async Task<(bool ok, string detail)> StartMuxHeadlessCommandFromIntentAsync", StringComparison.Ordinal);
        var muxEnd = remote.IndexOf("private async Task<CodexLocalRetrieval.Core.Models.AgentCommandResult> HandleMirrorLocalAsync", muxStart, StringComparison.Ordinal);
        var muxHelper = remote[muxStart..muxEnd];
        StringAssert.Contains(muxHelper, "CreateLocalMuxdSessionAsync(");
        Assert.DoesNotContain("GovernedCreateLocalMuxdSessionAsync(", muxHelper);
        Assert.DoesNotContain("SessionLaunchGovernor", muxHelper);
        Assert.DoesNotContain("SessionLaunchClaims", muxHelper);

        // The local GUI resume and /tomux handoff also delegate the create reservation to muxd.
        Assert.AreEqual(0, CountOccurrences(remote, "await GovernedCreateLocalMuxdSessionAsync("),
            "mux-hosted GUI creation must not pre-claim through the native governor");
        Assert.AreEqual(3, CountOccurrences(remote, "await CreateLocalMuxdSessionAsync("),
            "local resume, intent-fenced remote start, and /tomux must call muxd directly");
        StringAssert.Contains(remote, "muxd owns reservation");
        StringAssert.Contains(remote, "create_intent lock and claim");

        // The native governor remains the authority for non-mux app-owned launches.
        var sessions = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Sessions.cs"));
        var startChat = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.StartChat.cs"));
        StringAssert.Contains(sessions, "_launchGovernor.TryAcquire");
        StringAssert.Contains(startChat, "_launchGovernor.BeginFresh");
        StringAssert.Contains(startChat, "Process.Start(process)");
        StringAssert.Contains(sessions, "LaunchResumeWrapper(session, launchModeOverride)");
        Assert.IsFalse(sessions.Contains("CreateLocalMuxdSessionAsync(", StringComparison.Ordinal));
        Assert.IsFalse(startChat.Contains("CreateLocalMuxdSessionAsync(", StringComparison.Ordinal));

        // Native terminal launches remain governor-owned; changing muxd creation must not weaken that path.
        StringAssert.Contains(integrity, "private static bool CanReclaim(");
        Assert.DoesNotContain("Environment.ProcessId", integrity,
            "Reclaim must not special-case claims owned by this app's own pid.");
        Assert.DoesNotContain("isProcessAlive: ownerAlive", integrity);
        Assert.DoesNotContain("_ => false", integrity,
            "no forced-dead owner probe may be reintroduced on the reclaim path.");

        StringAssert.Contains(integrity, "await Task.Run(() => SessionReclaim.ExecuteAsync(options))");
        StringAssert.Contains(integrity, "DispatcherQueue.TryEnqueue");
        var riskyStart = integrity.IndexOf("private void SetRiskySessionActionsEnabled", StringComparison.Ordinal);
        var riskyEnd = integrity.IndexOf("private string IntegrityKey", riskyStart, StringComparison.Ordinal);
        var risky = integrity[riskyStart..riskyEnd];
        Assert.DoesNotContain("Reclaim", risky, "the Reclaim affordance must never be disabled by uncertainty");
    }

    // ---- harness -------------------------------------------------------------------------------------

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("reclaim's kill and identity checks are Windows-only.");
    }

    private static SessionLaunchRequest Request(string sid) => new(
        sid,
        null,
        "claude",
        "test",
        "reclaim relaunch",
        "resume.refused.claim",
        "resume.started.terminal",
        "resume.failed.terminal");

    private static SessionLaunchRequest MuxRequest(string sid) => new(
        sid,
        null,
        "claude",
        "test",
        "mux create",
        "mux.refused.claim",
        "mux.started.local",
        "mux.failed");

    private static SessionOwnerRecords.Options RecordOptions(string root) => new(RootDirectory: root);

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-reclaim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    // Claims are written through the real acquire (so the file lands on the real name derivation) and then
    // rewritten with the owner/expiry shape under test — the same approach the existing claim tests use.
    private static SessionLaunchClaims.LaunchClaimInfo WriteClaim(
        string root,
        string sessionId,
        int ownerPid,
        DateTimeOffset createdUtc,
        DateTimeOffset expiresUtc)
    {
        var options = new SessionLaunchClaims.Options(RootDirectory: root, Now: createdUtc);
        if (!SessionLaunchClaims.TryAcquire(sessionId, null, "test", out var claim, out var detail, _ => false, options))
            throw new InvalidOperationException("could not seed a claim: " + detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();

        var path = SessionLaunchClaims.ReadClaimsForSession(sessionId, options: options).Single().Path;
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            SessionId = sessionId,
            CandidateIds = new[] { sessionId },
            OwnerPid = ownerPid,
            OwnerProcess = "seed",
            CreatedUtc = createdUtc,
            ExpiresUtc = expiresUtc,
            Reason = "test",
        }));
        return SessionLaunchClaims.ReadClaimsForSession(sessionId, options: options).Single();
    }

    private static void WriteOwnerRecord(string root, string sessionId, int wrapperPid, DateTimeOffset wrapperStart)
    {
        if (!SessionOwnerRecords.TryWrite(
                sessionId, null, wrapperPid, wrapperStart, "terminal", out var detail,
                options: new SessionOwnerRecords.Options(RootDirectory: root)))
            throw new InvalidOperationException("could not seed an owner record: " + detail);
    }

    // Same shape as ProcessOpenFilesTests: a real child holding a real transcript open under a throwaway
    // ~/.claude/projects folder. No user chat is ever touched.
    private (string SessionId, Process Child) StartChildHoldingTranscript()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects",
            "clr-reclaim-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        _dirs.Add(folder);

        var sid = Guid.NewGuid().ToString();
        var path = Path.Combine(folder, sid + ".jsonl");
        File.WriteAllText(path, "{\"type\":\"user\"}\n");

        var script = $"""
            $fs = [System.IO.File]::Open('{path}', 'Open', 'Read', 'ReadWrite')
            [Console]::Out.WriteLine('ready')
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
        }) ?? throw new InvalidOperationException("could not start the transcript-holding child");
        _children.Add(child);

        if (!child.StandardOutput.ReadLineAsync().Wait(20_000))
            throw new TimeoutException("the child never reported holding the transcript open");
        return (sid, child);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }
}
