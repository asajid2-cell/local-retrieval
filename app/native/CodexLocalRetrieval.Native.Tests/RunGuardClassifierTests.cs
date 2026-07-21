using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The pre-launch guard used to answer "no" with a bare bool, so a failed scan and a confirmed live owner
// were indistinguishable — and the callers wrote "already had a live owner" into the ledger for both.
// These pin the four-way outcome that makes the refusal honest.
[TestClass]
public sealed class RunGuardClassifierTests
{
    [TestMethod]
    public void ScanFailure_IsUnverifiable_AndNamesTheBlockingPids()
    {
        var decision = RunGuardClassifier.ClassifyScan(
            scanOk: false,
            timedOut: false,
            unverifiablePids: new[] { 4242 },
            scanDetail: "handle enumeration failed");

        Assert.IsNotNull(decision);
        Assert.AreEqual(RunGuardOutcome.Unverifiable, decision!.Value.Outcome);
        Assert.Contains("4242", decision.Value.Detail,
            "an unverifiable refusal must say WHICH pid blocked it, not just that something did.");
    }

    [TestMethod]
    public void ScanFailure_DoesNotDuplicatePidsTheScanAlreadyNamed()
    {
        var decision = RunGuardClassifier.ClassifyScan(
            scanOk: false,
            timedOut: false,
            unverifiablePids: new[] { 4242 },
            scanDetail: "could not verify transcript ownership for pid 4242 (access denied)");

        Assert.IsNotNull(decision);
        Assert.DoesNotContain("unverifiable pids:", decision!.Value.Detail);
    }

    [TestMethod]
    public void ScanTimeout_IsUnverifiable_NeverCancelledOrLive()
    {
        var decision = RunGuardClassifier.ClassifyScan(true, timedOut: true, null, null);

        Assert.IsNotNull(decision);
        Assert.AreEqual(RunGuardOutcome.Unverifiable, decision!.Value.Outcome);
        Assert.AreEqual(RunGuardClassifier.TimedOutDetail, decision.Value.Detail);
    }

    [TestMethod]
    public void HealthyScan_LeavesTheDecisionToTheLiveOwnerCheck()
        => Assert.IsNull(RunGuardClassifier.ClassifyScan(true, false, System.Array.Empty<int>(), ""));

    // The dialog is UNREACHABLE on an unverifiable scan — fail closed means we never even offer the
    // takeover, because there is no confirmed owner to take over from. Encoded as a contract violation
    // so a future caller that reorders the stages fails loudly instead of proceeding.
    [TestMethod]
    public void UnverifiableScan_NeverReachesTheTakeoverDialog()
    {
        Assert.AreEqual(
            RunGuardOutcome.Unverifiable,
            Classify(scanOk: false, timedOut: false, dialogChoice: null).Outcome);

        Assert.ThrowsExactly<System.ArgumentException>(() =>
            Classify(scanOk: false, timedOut: false, dialogChoice: RunGuardChoice.Kill));
        Assert.ThrowsExactly<System.ArgumentException>(() =>
            Classify(scanOk: true, timedOut: true, dialogChoice: RunGuardChoice.Cancel));
    }

    [TestMethod]
    public void NothingRunning_Proceeds_WithoutADialog()
    {
        Assert.IsFalse(RunGuardClassifier.NeedsTakeoverPrompt(muxUp: false, localPids: System.Array.Empty<int>()));
        Assert.AreEqual(
            RunGuardOutcome.Proceed,
            RunGuardClassifier.Classify(true, false, null, null, false, System.Array.Empty<int>(), null, true, null).Outcome);
    }

    [TestMethod]
    public void ConfirmedOwner_AndCancel_IsCancelled()
        => Assert.AreEqual(
            RunGuardOutcome.Cancelled,
            Classify(scanOk: true, timedOut: false, dialogChoice: RunGuardChoice.Cancel).Outcome);

    [TestMethod]
    public void ConfirmedOwner_KillAllSucceed_Proceeds()
        => Assert.AreEqual(
            RunGuardOutcome.Proceed,
            Classify(scanOk: true, timedOut: false, dialogChoice: RunGuardChoice.Kill, killOk: true).Outcome);

    // A kill that fails leaves the owner running. That is a CONFIRMED live owner — the one case where
    // "already running" is a true statement — and it must not be reported as a user cancellation.
    [TestMethod]
    public void ConfirmedOwner_KillFails_IsLive_CarryingTheKillFailure()
    {
        var decision = Classify(
            scanOk: true,
            timedOut: false,
            dialogChoice: RunGuardChoice.Kill,
            killOk: false,
            killDetail: "Could not kill the local running agent: access denied");

        Assert.AreEqual(RunGuardOutcome.Live, decision.Outcome);
        Assert.Contains("access denied", decision.Detail);
    }

    [TestMethod]
    public void MuxUpAlone_StillCountsAsAnOwnerToTakeOver()
    {
        Assert.IsTrue(RunGuardClassifier.NeedsTakeoverPrompt(muxUp: true, localPids: System.Array.Empty<int>()));
        Assert.AreEqual(
            RunGuardOutcome.Cancelled,
            RunGuardClassifier.Classify(
                true, false, null, null, muxUp: true, localPids: System.Array.Empty<int>(),
                RunGuardChoice.Cancel, true, null).Outcome);
    }

    private static RunGuardDecision Classify(
        bool scanOk,
        bool timedOut,
        RunGuardChoice? dialogChoice,
        bool killOk = true,
        string? killDetail = null)
        => RunGuardClassifier.Classify(
            scanOk,
            timedOut,
            unverifiablePids: new[] { 4242 },
            scanDetail: "handle enumeration failed",
            muxUp: false,
            localPids: new[] { 1234 },
            dialogChoice,
            killOk,
            killDetail);
}
