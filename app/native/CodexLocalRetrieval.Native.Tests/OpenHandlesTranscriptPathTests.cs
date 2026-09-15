using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The transcript-path predicate is the ONLY identity signal a fresh codex tab has. MUX launches codex with
// CODEX_HOME=~/.codex-accounts/<account>, so its rollout lives under a home the canonical "\.codex\sessions\"
// test cannot see; a predicate that misses it leaves the tab identityPending forever. These are pure-string
// tests pinning the exact shapes measured from a live handle probe — including the over-match the broader,
// bare-".codex" test produced (a repair-backup session_index.jsonl is NOT a transcript).
[TestClass]
public class OpenHandlesTranscriptPathTests
{
    private const string AccountHomeRollout =
        @"C:\Users\Ahmed\.codex-accounts\freedock\sessions\2026\09\13\rollout-2026-09-13T17-24-03-01a09d15-c104-79a2-9333-10c8bbcdfe93.jsonl";

    // The measured bug: this exact path was held open by the live codex pid and the old predicate rejected it.
    [TestMethod]
    public void PerAccountCodexRollout_IsATranscript()
    {
        Assert.IsTrue(OpenHandles.IsTranscriptPath(AccountHomeRollout),
            "a per-account CODEX_HOME rollout is a transcript; missing it leaves fresh codex tabs unidentifiable");
    }

    // The old shape must not regress with the new anchor.
    [TestMethod]
    public void CanonicalCodexRollout_IsATranscript()
    {
        Assert.IsTrue(OpenHandles.IsTranscriptPath(
            @"C:\Users\Ahmed\.codex\sessions\2026\05\12\rollout-2026-05-12T07-34-58-abc.jsonl"));
    }

    [TestMethod]
    public void ClaudeProjectJsonl_IsATranscript()
    {
        Assert.IsTrue(OpenHandles.IsTranscriptPath(
            @"C:\Users\Ahmed\.claude\projects\C--Users-Ahmed\01a09d15-c104-79a2-9333-10c8bbcdfe93.jsonl"));
    }

    // The measured over-match guard: a bare ".codex" widening also matched this, which is not a transcript.
    [TestMethod]
    public void NonTranscriptJsonlUnderACodexHome_IsRejected()
    {
        Assert.IsFalse(OpenHandles.IsTranscriptPath(
            @"C:\Users\Ahmed\.codex\repair-backups\trim-rebuilt-session-index-20260521-162555\session_index.jsonl"),
            "a repair-backup index is not a session transcript, even though it sits under a codex home");
    }

    // A rollout name with a \sessions\ segment is the anchor; without the segment it must not match.
    [TestMethod]
    public void RolloutNameWithoutASessionsSegment_IsRejected()
    {
        Assert.IsFalse(OpenHandles.IsTranscriptPath(
            @"C:\Users\Ahmed\.codex\repair-backups\rollout-2026-09-13T17-24-03-01a09d15-c104-79a2-9333-10c8bbcdfe93.jsonl"));
    }

    [TestMethod]
    public void NonJsonlFile_IsRejected()
    {
        Assert.IsFalse(OpenHandles.IsTranscriptPath(
            @"C:\Users\Ahmed\.codex-accounts\freedock\sessions\2026\09\13\rollout-2026-09-13T17-24-03-01a09d15-c104-79a2-9333-10c8bbcdfe93.log"));
    }

    // The handle table returns native paths, but callers elsewhere may hand us forward slashes; the predicate
    // normalises '/' to '\' before matching, so both spellings must resolve identically.
    [TestMethod]
    public void ForwardSlashPath_IsNormalised()
    {
        Assert.IsTrue(OpenHandles.IsTranscriptPath(
            "C:/Users/Ahmed/.codex-accounts/freedock/sessions/2026/09/13/rollout-2026-09-13T17-24-03-01a09d15-c104-79a2-9333-10c8bbcdfe93.jsonl"));
    }

    // The uuid suffix is what the launcher keys identity on; the timestamp prefix must not leak into it.
    [TestMethod]
    public void SessionIdFromRolloutPath_IsTheTrailingUuid()
    {
        Assert.AreEqual(
            "01a09d15-c104-79a2-9333-10c8bbcdfe93",
            OpenHandles.SessionIdFromPath(AccountHomeRollout));
    }

    // A rollout filename without a trailing uuid cannot yield a uuid; it falls back to the stem rather than
    // inventing one. Documented so a future "improvement" to the regex is a deliberate change.
    [TestMethod]
    public void SessionIdFromRolloutWithoutUuid_FallsBackToTheFilenameStem()
    {
        Assert.AreEqual(
            "rollout-2026-05-12T07-34-58-abc",
            OpenHandles.SessionIdFromPath(@"C:\Users\Ahmed\.codex\sessions\2026\05\12\rollout-2026-05-12T07-34-58-abc.jsonl"));
    }
}
