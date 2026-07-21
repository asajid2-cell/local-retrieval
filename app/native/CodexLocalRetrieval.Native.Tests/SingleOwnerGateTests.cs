using System;
using System.Collections.Generic;
using System.Linq;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// The app must NEVER start a 2nd process for a session that's already live (two writers on one
// transcript => Claude silently drops writes => lost work). ResumeInTerminal gates on
// RunningSessions.IsSessionLive right before it spawns. These lock in the gate's decision logic and
// prove it detects a genuinely-live session on this machine.
[TestClass]
public class SingleOwnerGateTests
{
    [TestMethod]
    public void AnyLive_TrueWhenSessionIdIsLive()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "aaaa", "bbbb" };
        Assert.IsTrue(RunningSessions.AnyLive(new[] { "bbbb" }, live));
    }

    [TestMethod]
    public void AnyLive_TrueWhenAnAliasIsLive()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fork-child-123" };
        // a chat whose OWN id is idle but whose fork/lineage alias is live must still be caught
        Assert.IsTrue(RunningSessions.AnyLive(new[] { "parent-id", "fork-child-123" }, live));
    }

    [TestMethod]
    public void AnyLive_FalseWhenNothingOverlaps()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "xxxx" };
        Assert.IsFalse(RunningSessions.AnyLive(new[] { "yyyy", "zzzz" }, live));
    }

    [TestMethod]
    public void AnyLive_IgnoresNullAndEmptyIds()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
        Assert.IsFalse(RunningSessions.AnyLive(new string?[] { null, "" }, live));
    }

    [TestMethod]
    public void AnyLive_IsCaseInsensitive()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ABCD-1234" };
        Assert.IsTrue(RunningSessions.AnyLive(new[] { "abcd-1234" }, live));
    }

    [TestMethod]
    public void OpenHandleScan_FailsClosedWhenAgentPidCannotBeInspected()
    {
        Assert.IsFalse(OpenHandles.TryOpenTranscriptSessionIds(new[] { int.MaxValue }, out var ids, out var detail));
        Assert.AreEqual(0, ids.Count);
        StringAssert.Contains(detail, "refusing to risk a second writer");
    }

    [TestMethod]
    public void IsSessionLive_DetectsARealLiveSessionOnThisMachine()
    {
        const string bogusId = "00000000-0000-0000-0000-000000000000";
        if (!RunningSessions.TryAllLiveSessionIds(out var live, out var detail))
        {
            Assert.IsTrue(RunningSessions.IsSessionLive(bogusId),
                "when live-owner verification is uncertain, the launch gate must fail closed");
            Assert.Inconclusive("live session scan is unverified on this machine: " + detail);
        }

        if (live.Count == 0)
            Assert.Inconclusive("no live claude/codex sessions to test against right now");
        var realId = live.First();
        Assert.IsTrue(RunningSessions.IsSessionLive(realId), $"a genuinely live session ({realId}) must be detected");
        Assert.IsFalse(RunningSessions.IsSessionLive(bogusId),
            "a bogus id must NOT be reported live when live-owner verification is complete");
    }
}
