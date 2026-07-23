using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteCommandProtocolTests
{
    [DataTestMethod]
    [DataRow("rename", "idempotent")]
    [DataRow("addtocollection", "idempotent")]
    [DataRow("fetchfile", "intent-fenced")]
    [DataRow("startmux", "intent-fenced")]
    [DataRow("transcript", "read-only")]
    [DataRow("kill", "refused")]
    public void IsReplaySafe_AcceptsOnlyDeclaredCommandPolicyPairs(string type, string policy)
    {
        Assert.IsTrue(RemoteCommandProtocol.IsReplaySafe(type, policy));
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe(type, "idempotent-but-wrong"));
    }

    [TestMethod]
    public void IsReplaySafe_RefusesUnknownCommandsAndMissingPolicies()
    {
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe("future-mutation", "idempotent"));
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe("rename", ""));
    }

    [TestMethod]
    public void AckSucceeded_RequiresAnExplicitSuccessfulJsonAcknowledgement()
    {
        Assert.IsTrue(RemoteCommandProtocol.AckSucceeded("""{"ok":true,"deduplicated":true}"""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded("""{"error":"lease expired"}"""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded(""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded("not json"));
    }

    // ---- intent envelope: which types need one -------------------------------------------------------

    [TestMethod]
    public void RequiresIntentEnvelope_CoversExactlyTheAtMostOnceCommands()
    {
        Assert.IsTrue(RemoteCommandProtocol.RequiresIntentEnvelope("fetchfile"));
        Assert.IsTrue(RemoteCommandProtocol.RequiresIntentEnvelope("StartMux"));   // case/space tolerant
        Assert.IsTrue(RemoteCommandProtocol.RequiresIntentEnvelope("  startmux "));
        foreach (var idempotent in new[] { "rename", "setapptitle", "addtocollection", "cleartabhistory", "settabcolor", "transcript", "kill", "", null })
            Assert.IsFalse(RemoteCommandProtocol.RequiresIntentEnvelope(idempotent), $"'{idempotent}' is not intent-fenced");
    }

    // The relay sanitises both ids to ^[A-Za-z0-9._-]{1,128}$ before queueing (relay/server.js
    // commandIntentId), so app-side well-formedness has to mirror that charset exactly: narrower would
    // refuse honest 24-char base64url lease tokens, wider would admit values the relay never issued.
    [TestMethod]
    public void IsWellFormedEnvelopeToken_MirrorsTheRelaySanitisedCharset()
    {
        Assert.IsTrue(RemoteCommandProtocol.IsWellFormedEnvelopeToken("Ab3_-.xyz"));
        Assert.IsTrue(RemoteCommandProtocol.IsWellFormedEnvelopeToken("kQ7fMz2pR8sT4vX1yB6nC0dE"));   // base64url lease
        Assert.IsTrue(RemoteCommandProtocol.IsWellFormedEnvelopeToken(new string('a', 128)));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken(new string('a', 129)));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken(null));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken(""));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken("   "));
        foreach (var bad in new[] { "has space", "semi;colon", "sl/ash", "new\nline", "quote\"d", "nul\0byte" })
            Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken(bad), $"'{bad}' must not pass");
    }

    [TestMethod]
    public void ValidateEnvelope_RequiresBothALeaseTokenAndAStableIntentId()
    {
        Assert.IsTrue(RemoteCommandProtocol.ValidateEnvelope("intent-1", "lease-1", out var okDetail));
        Assert.AreEqual("", okDetail);

        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope("intent-1", "", out var noLease));
        StringAssert.Contains(noLease, "lease token");
        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope("intent-1", "   ", out _));
        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope("intent-1", "bad token", out var badLease));
        StringAssert.Contains(badLease, "malformed");

        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope("", "lease-1", out var noIntent));
        StringAssert.Contains(noIntent, "intent id");
        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope(null, "lease-1", out _));
        Assert.IsFalse(RemoteCommandProtocol.ValidateEnvelope("bad intent", "lease-1", out var badIntent));
        StringAssert.Contains(badIntent, "malformed");
    }

    // A correct replayPolicy alone must NOT open an intent-fenced command; the DTOs default both ids to
    // "", which is exactly what an old relay (or a replayed body) delivers.
    [DataTestMethod]
    [DataRow("fetchfile")]
    [DataRow("startmux")]
    public void TryAdmit_RefusesIntentFencedCommandsWithADefaultedEnvelope(string type)
    {
        Assert.IsFalse(RemoteCommandProtocol.TryAdmit(type, "intent-fenced", "", "", out var blank));
        Assert.AreNotEqual("", blank);
        Assert.IsFalse(RemoteCommandProtocol.TryAdmit(type, "intent-fenced", "intent-1", "", out _));
        Assert.IsFalse(RemoteCommandProtocol.TryAdmit(type, "intent-fenced", "", "lease-1", out _));
        Assert.IsTrue(RemoteCommandProtocol.TryAdmit(type, "intent-fenced", "intent-1", "lease-1", out _));

        // the replay gate still runs first, and its message is the one the poller acks
        Assert.IsFalse(RemoteCommandProtocol.TryAdmit(type, "idempotent", "intent-1", "lease-1", out var policy));
        StringAssert.Contains(policy, "replay policy");
    }

    [TestMethod]
    public void TryAdmit_LeavesNonFencedCommandsUngatedByTheEnvelope()
    {
        Assert.IsTrue(RemoteCommandProtocol.TryAdmit("rename", "idempotent", "", "", out var detail));
        Assert.AreEqual("", detail);
        Assert.IsTrue(RemoteCommandProtocol.TryAdmit("transcript", "read-only", null, null, out _));
    }

    // ---- intent ledger: the whole per-command gate both pollers run ---------------------------------

    [TestMethod]
    public void Ledger_AdmitsAValidEnvelopeExactlyOnceAndReplaysTheRecordedAck()
    {
        var ledger = new RemoteCommandProtocol.IntentLedger();

        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("fetchfile", "intent-fenced", "intent-1", "lease-1", out _));
        ledger.Record("intent-1", true, "downloaded to PC");

        // redelivery of the SAME intent (relay retry with a fresh lease) must not download twice
        var second = ledger.Admit("fetchfile", "intent-fenced", "intent-1", "lease-2", out var replay);
        Assert.AreEqual(RemoteCommandAdmission.Duplicate, second);
        Assert.IsTrue(replay.ok);
        Assert.AreEqual("downloaded to PC", replay.detail);

        // a different intent is fresh work
        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("fetchfile", "intent-fenced", "intent-2", "lease-3", out _));
    }

    [TestMethod]
    public void Ledger_RefusesABrokenEnvelopeWithoutClaimingTheIntent()
    {
        var ledger = new RemoteCommandProtocol.IntentLedger();

        Assert.AreEqual(
            RemoteCommandAdmission.Refused,
            ledger.Admit("startmux", "intent-fenced", "intent-1", "", out var refused));
        Assert.IsFalse(refused.ok);
        StringAssert.Contains(refused.detail, "lease token");

        // the refusal must not have consumed the intent — the honest redelivery still executes
        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("startmux", "intent-fenced", "intent-1", "lease-1", out _));
    }

    [TestMethod]
    public void Ledger_TreatsAnInFlightIntentAsADuplicateUntilItIsRecorded()
    {
        var ledger = new RemoteCommandProtocol.IntentLedger();
        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("startmux", "intent-fenced", "intent-1", "lease-1", out _));

        var concurrent = ledger.Admit("startmux", "intent-fenced", "intent-1", "lease-2", out var busy);
        Assert.AreEqual(RemoteCommandAdmission.Duplicate, concurrent);
        Assert.IsFalse(busy.ok, "an unfinished intent must never ack success");

        ledger.Record("intent-1", true, "started");
        Assert.AreEqual(RemoteCommandAdmission.Duplicate, ledger.Admit("startmux", "intent-fenced", "intent-1", "lease-3", out var done));
        Assert.IsTrue(done.ok);
        Assert.AreEqual("started", done.detail);
    }

    [TestMethod]
    public void Ledger_DoesNotDedupNonFencedCommandsAndStaysBounded()
    {
        var ledger = new RemoteCommandProtocol.IntentLedger(capacity: 4);

        // idempotent commands may legitimately be redelivered and re-run
        for (var i = 0; i < 3; i++)
        {
            Assert.AreEqual(
                RemoteCommandAdmission.Execute,
                ledger.Admit("rename", "idempotent", "intent-r", "lease-1", out _));
            ledger.Record("intent-r", true, "renamed");
        }

        // the fenced ledger evicts oldest-first, so a long-lived poller cannot grow without limit
        for (var i = 0; i < 6; i++)
        {
            Assert.AreEqual(
                RemoteCommandAdmission.Execute,
                ledger.Admit("fetchfile", "intent-fenced", $"intent-{i}", "lease-1", out _));
            ledger.Record($"intent-{i}", true, "downloaded to PC");
        }
        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("fetchfile", "intent-fenced", "intent-0", "lease-1", out _),
            "the oldest intents are evicted once the bound is reached");
        Assert.AreEqual(
            RemoteCommandAdmission.Duplicate,
            ledger.Admit("fetchfile", "intent-fenced", "intent-5", "lease-1", out _),
            "recent intents must survive eviction");
    }

    [TestMethod]
    public void Ledger_RecordOnlyMarksIntentsItActuallyAdmitted()
    {
        var ledger = new RemoteCommandProtocol.IntentLedger();
        ledger.Record("never-admitted", true, "should not stick");
        ledger.Record("", true, "ignored");
        ledger.Record(null, true, "ignored");

        Assert.AreEqual(
            RemoteCommandAdmission.Execute,
            ledger.Admit("fetchfile", "intent-fenced", "never-admitted", "lease-1", out _));
    }
}
