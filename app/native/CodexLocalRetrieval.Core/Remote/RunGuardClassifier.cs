using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexLocalRetrieval.Core.Remote;

// What the pre-launch guard actually decided. The guard used to return a bare bool, so "the scan failed",
// "the scan timed out", "you cancelled", and "the kill didn't work" all came back as `false` — and both
// callers then wrote `*.refused.running` ("the session already had a live owner") into the session ledger.
// For the two scan failures that is a LIE: nothing was ever confirmed live. This enum is the fix.
public enum RunGuardOutcome
{
    // Nothing was running, or the user chose Kill and every kill step succeeded. Launch.
    Proceed,
    // A live owner WAS confirmed and shown, and the user declined to take it over.
    Cancelled,
    // A live owner WAS confirmed, the user chose Kill, and a kill/delete step failed — it is STILL there.
    Live,
    // The scan could not answer the question (unverifiable pids, a scan-level failure, or the 6s timeout).
    // Fail closed: no dialog, no launch — and never recorded as a confirmed live owner.
    Unverifiable,
}

// The takeover dialog's answer, as the classifier sees it. The UI keeps its own RunGuard enum for the
// ContentDialog result; this is the Core-side mirror so the decision logic is testable without a XamlRoot.
public enum RunGuardChoice { Cancel, Kill }

public readonly record struct RunGuardDecision(RunGuardOutcome Outcome, string Detail);

// The pre-launch guard's decision, extracted from the UI partial class so it can be tested. The guard itself
// (MainPage.RunningChats.cs) does the I/O — mux probe, scan, dialog, kills — and delegates every branch here.
//
// The contract is staged, because "we never even showed the dialog" is part of what callers must be able to
// prove: ClassifyScan decides first and, when it decides, the dialog is unreachable; NeedsTakeoverPrompt then
// says whether there is anything to prompt about. Passing a dialog choice the guard could not have collected
// is a programming error, not a fallback — Classify throws rather than quietly inventing an outcome.
public static class RunGuardClassifier
{
    public const string TimedOutDetail =
        "live-session verification timed out; refusing to risk a second writer";

    // Stage 1 — is the scan trustworthy at all? A non-null result is decided BEFORE the dialog exists.
    public static RunGuardDecision? ClassifyScan(
        bool scanOk,
        bool timedOut,
        IReadOnlyCollection<int>? unverifiablePids,
        string? scanDetail)
    {
        if (timedOut)
            return new RunGuardDecision(
                RunGuardOutcome.Unverifiable,
                string.IsNullOrWhiteSpace(scanDetail) ? TimedOutDetail : scanDetail!);

        if (scanOk) return null;

        // After the per-pid rewrite a false here means either "these specific pids blocked us" or a
        // scan-level failure; the scan's own detail says which. Append the pids only if it didn't.
        var detail = string.IsNullOrWhiteSpace(scanDetail)
            ? "live-session verification failed"
            : scanDetail!;
        var pids = unverifiablePids?.Where(p => p > 0).Distinct().OrderBy(p => p).ToArray() ?? Array.Empty<int>();
        if (pids.Length > 0 && !pids.All(p => detail.Contains("pid " + p, StringComparison.Ordinal)))
            detail += " (unverifiable pids: " + string.Join(", ", pids) + ")";

        return new RunGuardDecision(RunGuardOutcome.Unverifiable, detail);
    }

    // Stage 2 — given a trustworthy scan, is there anything to take over? If a multiplex is up, the local
    // pids ARE its agent rather than a second copy, so either signal alone is enough to prompt.
    public static bool NeedsTakeoverPrompt(bool muxUp, IReadOnlyCollection<int>? localPids)
        => muxUp || (localPids?.Count(p => p > 0) ?? 0) > 0;

    // Stage 3 — the whole decision. `dialogChoice` MUST be null exactly when the guard could not have shown
    // the dialog (unverifiable scan, or nothing running) and non-null otherwise.
    public static RunGuardDecision Classify(
        bool scanOk,
        bool timedOut,
        IReadOnlyCollection<int>? unverifiablePids,
        string? scanDetail,
        bool muxUp,
        IReadOnlyCollection<int>? localPids,
        RunGuardChoice? dialogChoice,
        bool killOk,
        string? killDetail)
    {
        var scan = ClassifyScan(scanOk, timedOut, unverifiablePids, scanDetail);
        if (scan is not null)
        {
            if (dialogChoice is not null)
                throw new ArgumentException(
                    "the takeover dialog must never be shown on an unverifiable scan", nameof(dialogChoice));
            return scan.Value;
        }

        if (!NeedsTakeoverPrompt(muxUp, localPids))
        {
            if (dialogChoice is not null)
                throw new ArgumentException(
                    "the takeover dialog must not be shown when nothing is running", nameof(dialogChoice));
            return new RunGuardDecision(RunGuardOutcome.Proceed, "");
        }

        if (dialogChoice is null)
            throw new ArgumentException(
                "a confirmed live owner requires the takeover dialog's answer", nameof(dialogChoice));

        if (dialogChoice == RunGuardChoice.Cancel)
            return new RunGuardDecision(RunGuardOutcome.Cancelled, "");

        // Kill chosen. Success is the only path back to Proceed — a failed kill leaves the owner running,
        // which is a CONFIRMED live owner, not a cancellation.
        return killOk
            ? new RunGuardDecision(RunGuardOutcome.Proceed, "")
            : new RunGuardDecision(
                RunGuardOutcome.Live,
                string.IsNullOrWhiteSpace(killDetail) ? "could not stop the running copy" : killDetail!);
    }
}
