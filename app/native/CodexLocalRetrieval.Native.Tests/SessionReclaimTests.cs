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

    // ---- relaunch: only the liveness gate is forced --------------------------------------------------

    // The relaunch acquires through the NORMAL FileMode.CreateNew path. Only `isSessionLive` is forced (the
    // kills were identity-verified); the file race is never bypassed, so a concurrent launcher that got there
    // first still wins and reclaim reports it instead of starting a second writer.
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

    // ---- mux custody --------------------------------------------------------------------------------

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

    // ---- integration: a real process tree, killed, cleared, pruned, relaunched -----------------------

    // The throwaway-session bar: a real cmd.exe tree holding a real transcript open, a real owner record and a
    // real unexpired claim tied to it, run through the REAL reclaim core (no user chats anywhere). The claim's
    // owner is this live test process, so nothing but the tier-2 kill evidence can clear it — which is exactly
    // the [F#1] shape the retired own-pid override used to short-circuit.
    [TestMethod]
    public async Task ReclaimCore_KillsTheTree_ClearsViaTierTwoEvidence_PrunesMux_AndRelaunches()
    {
        RequireWindows();
        var claimRoot = TempDir();
        var recordRoot = TempDir();
        var eventRoot = TempDir();
        var now = DateTimeOffset.UtcNow;

        var (sid, child) = StartChildHoldingTranscript();
        var childStart = child.StartTime.ToUniversalTime();
        WriteOwnerRecord(recordRoot, sid, child.Id, new DateTimeOffset(childStart, TimeSpan.Zero));
        var claim = WriteClaim(claimRoot, sid, ownerPid: Environment.ProcessId, now, now.AddMinutes(2));

        var history = new Dictionary<string, MuxTabRecord>
        {
            ["stale-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = sid } },
        };

        var launched = 0;
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { sid },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            RecordOptions = RecordOptions(recordRoot),
            // muxd confirms the tab is absent (empty live list).
            PruneMuxCurrent = ids => SessionReclaim.PruneStaleMuxCurrent(history, ids, Array.Empty<string>()),
            LaunchRequest = Request(sid),
            GovernorOptions = new SessionLaunchGovernorOptions(
                ClaimOptions: new SessionLaunchClaims.Options(RootDirectory: claimRoot),
                EventOptions: new SessionEventLedger.Options(RootDirectory: eventRoot)),
            Launch = () =>
            {
                Interlocked.Increment(ref launched);
                return Task.FromResult((true, "relaunched (seam)"));
            },
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

        // 4. relaunched through a FRESH CreateNew reservation — a different claim file from the one cleared.
        Assert.IsTrue(report.Relaunched, report.RelaunchDetail);
        Assert.IsFalse(report.LostLaunchRace);
        Assert.AreEqual(1, launched);
        Assert.IsTrue(File.Exists(claim.Path), "the relaunch must hold its own reservation");
        var relaunchClaim = SessionLaunchClaims
            .ReadClaimsForSession(sid, options: new SessionLaunchClaims.Options(RootDirectory: claimRoot))
            .Single();
        Assert.AreEqual(Environment.ProcessId, relaunchClaim.OwnerPid);
        Assert.IsGreaterThan(claim.CreatedUtc, relaunchClaim.CreatedUtc, "the reservation must be a new one, not the cleared one");
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

        var launched = 0;
        var report = await SessionReclaim.ExecuteAsync(new ReclaimOptions
        {
            CandidateIds = new[] { sid },
            ClaimOptions = new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            Now = now,
            // Hermetic: no real owner to hunt; the clear failure is the whole point.
            Kill = _ => new RunningSessions.KillResult(true, "no owners", Array.Empty<ReclaimKilledPid>()),
            LaunchRequest = Request(sid),
            Launch = () => { Interlocked.Increment(ref launched); return Task.FromResult((true, "must not run")); },
        });

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

        // Nothing was relaunched over an unresolved reservation.
        Assert.AreEqual(0, launched, "a blocking claim must not relaunch");
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

    // H1: the lease has to be taken BEFORE the muxd create on all three writer-creation paths, and [F#1]: the
    // own-pid clearing override must be gone and stay gone.
    [TestMethod]
    public void MuxWriterCreationPaths_AreGoverned_AndNoOwnPidOverrideSurvives()
    {
        var root = FindRepoRoot();
        var remote = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"));
        var integrity = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Integrity.cs"));

        // The governed wrapper acquires the lease first and marks the outcome on the muxd answer.
        var wrapperStart = remote.IndexOf("private async Task<(bool ok, string detail)> GovernedCreateLocalMuxdSessionAsync", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, wrapperStart, "the governed mux-create wrapper is missing");
        var wrapperEnd = remote.IndexOf("private static void ClearClaimsAfterVerifiedKill", wrapperStart, StringComparison.Ordinal);
        var wrapper = remote[wrapperStart..wrapperEnd];
        var acquire = wrapper.IndexOf("_launchGovernor.TryAcquire", StringComparison.Ordinal);
        var create = wrapper.IndexOf("await CreateLocalMuxdSessionAsync", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, acquire);
        Assert.IsTrue(create > acquire, "the governor lease must be acquired BEFORE the muxd create, not after");
        StringAssert.Contains(wrapper, "MarkStarted");
        StringAssert.Contains(wrapper, "MarkFailed");

        // All three writer-creation paths go through it; nothing calls the raw create except the wrapper.
        Assert.AreEqual(
            3,
            CountOccurrences(remote, "await GovernedCreateLocalMuxdSessionAsync("),
            "StartRemoteSession, StartMuxHeadlessFromIntentAsync and HandleToMuxAsync must all be governed");
        Assert.AreEqual(
            1,
            CountOccurrences(remote, "await CreateLocalMuxdSessionAsync("),
            "the ungoverned muxd create must only be reachable through the governed wrapper");

        // [F#5] the handoff paths clear the tied claim on verified-kill evidence before taking the lease.
        StringAssert.Contains(remote, "ClearClaimsTiedToVerifiedKills");
        Assert.IsGreaterThanOrEqualTo(
            3,
            CountOccurrences(remote, "ClearClaimsAfterVerifiedKill("),
            "the guard kill and both MuxIdentityTransfer handoffs must release the claim they verified out");

        // [F#1] NON-NEGOTIABLE: no own-pid special case anywhere in the reclaim surface.
        Assert.DoesNotContain("Environment.ProcessId", integrity,
            "Reclaim must not special-case claims owned by this app's own pid.");
        Assert.DoesNotContain("isProcessAlive: ownerAlive", integrity);
        Assert.DoesNotContain("_ => false", integrity,
            "no forced-dead owner probe may be reintroduced on the reclaim path.");

        // M7 + the oracle rule: Reclaim shows on ANY danger blocker and never gates itself on the scan.
        StringAssert.Contains(integrity, "private static bool CanReclaim(");
        Assert.DoesNotContain("RunningSessions.TryScan", integrity);
        Assert.DoesNotContain("RunningSessions.TryAllLiveSessionIds", integrity);
        Assert.DoesNotContain("TryLiveSessionPids", integrity);

        // M3: the flow runs off the UI thread behind a sequence guard.
        StringAssert.Contains(integrity, "await Task.Run(() => SessionReclaim.ExecuteAsync(options))");
        StringAssert.Contains(integrity, "DispatcherQueue.TryEnqueue");

        // Start-class buttons may be disabled on uncertainty; Reclaim/Kill never are.
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
