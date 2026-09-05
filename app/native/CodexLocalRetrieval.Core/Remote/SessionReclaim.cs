using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Remote;

// What a reclaim was allowed to do to one claim file.
//
//  * Cleared            - the tiers permitted it and the quarantine-move + identity re-read succeeded.
//  * RefusedAliveOwner  - tier 2: an unexpired claim whose owner is alive (or unknown), with no evidence that
//                         anything tied to it was killed and no grace waited out. This includes claims owned by
//                         THIS app's pid [F#1] - there is no own-pid escape hatch.
//  * LaunchInFlight     - the claim file is held open by a launcher mid-acquire (SessionLaunchClaims keeps the
//                         stream with FileShare.Read through the WHOLE acquire, so a sharing violation on the
//                         move is a positive signal, not an error). Abort this claim, never retry-force past it.
//  * Failed             - anything else (I/O, the claim changed under us).
public enum ReclaimClearOutcome
{
    Cleared,
    RefusedAliveOwner,
    LaunchInFlight,
    Failed,
}

// A process this reclaim killed AND watched exit by identity. `SessionIds` is what tied it to a session: the
// scan sids it was matched on, or - for a pid the caller itself resolved as this session's owner (the guard's
// kill, MuxIdentityTransfer's owner-exit verification) - the candidate id set the caller killed it for.
public sealed record ReclaimKilledPid(int Pid, DateTime? StartTimeUtc, IReadOnlyList<string> SessionIds);

// Everything the tier decision is allowed to look at, plus the seams tests need. Nothing here reaches for the
// global liveness oracle: reclaim has to work precisely when that oracle is the thing that is broken.
public sealed record ReclaimEvidence
{
    public IReadOnlyList<ReclaimKilledPid> ConfirmedExitedPids { get; init; } = Array.Empty<ReclaimKilledPid>();

    // Pre-read owner records for the claim's candidate ids. Null means "read them yourself from RecordOptions".
    public IReadOnlyList<SessionOwnerRecords.OwnerRecordInfo>? OwnerRecords { get; init; }
    public SessionOwnerRecords.Options? RecordOptions { get; init; }

    // The operator chose "wait for the reservation to expire" and the wait completed.
    public bool GraceWaitedOut { get; init; }

    public DateTimeOffset? Now { get; init; }
    public Func<int, bool>? IsProcessAlive { get; init; }
    public Func<int, (bool Alive, DateTime StartUtc)>? AliveIdentity { get; init; }

    public static readonly ReclaimEvidence None = new();

    internal DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;

    // "Alive" is exit-code defined [F#6], which is what ProcessOpenFiles.IsAlive implements.
    internal bool ProcessAlive(int pid)
        => IsProcessAlive is not null ? IsProcessAlive(pid) : ProcessOpenFiles.IsAlive(pid);

    internal (bool Alive, DateTime StartUtc) Identity(int pid)
    {
        if (AliveIdentity is not null) return AliveIdentity(pid);
        return (ProcessOpenFiles.TryGetAliveIdentity(pid, out var start), start);
    }

    internal IReadOnlyList<SessionOwnerRecords.OwnerRecordInfo> RecordsFor(IReadOnlyList<string> candidateIds)
    {
        if (OwnerRecords is not null) return OwnerRecords;
        if (candidateIds.Count == 0) return Array.Empty<SessionOwnerRecords.OwnerRecordInfo>();
        try
        {
            return SessionOwnerRecords.ReadRecordsForSession(candidateIds[0], candidateIds.Skip(1), RecordOptions);
        }
        catch { return Array.Empty<SessionOwnerRecords.OwnerRecordInfo>(); }
    }
}

// A tier-2 refusal, carried out to whoever has to explain it to the operator. `EvidenceLine` is deliberately
// the ONLY sanctioned wording for the refusal dialog: it must never name a pid the app did not verify.
public sealed record ReclaimRefusal(
    SessionLaunchClaims.LaunchClaimInfo Claim,
    string Detail,
    bool OwnerVerifiedAlive,
    int OwnerPid)
{
    public string EvidenceLine => OwnerVerifiedAlive
        ? $"pid {OwnerPid} alive (verified)"
        : "owner unknown - scan unverifiable";
}

public enum ReclaimRefusalChoice
{
    // Leave the claim alone and stop reclaiming (the safe default when the operator closes the dialog).
    Abort,
    // Wait the reservation out (<= 2 min), then re-run the tier check with graceWaitedOut: true.
    WaitForExpiry,
}

public sealed record ReclaimClaimResult(
    string Path,
    string SessionId,
    ReclaimClearOutcome Outcome,
    string Detail,
    bool Overridden = false);

public sealed record ReclaimMuxResult(
    bool ProbeOk,
    bool OwnerFound,
    bool TeardownVerified,
    string Detail);

public sealed record ReclaimReport(
    bool KillOk,
    string KillDetail,
    bool MuxOk,
    string MuxDetail,
    IReadOnlyList<ReclaimKilledPid> ConfirmedExited,
    IReadOnlyList<ReclaimClaimResult> Claims,
    IReadOnlyList<string> PrunedMuxTabs,
    bool Relaunched,
    bool LostLaunchRace,
    string RelaunchDetail,
    string Headline)
{
    public bool AnyClaimBlocking => Claims.Any(c => c.Outcome != ReclaimClearOutcome.Cleared);
    public bool LaunchInFlight => Claims.Any(c => c.Outcome == ReclaimClearOutcome.LaunchInFlight);
    public bool Changed => ConfirmedExited.Count > 0
                           || Claims.Any(c => c.Outcome == ReclaimClearOutcome.Cleared)
                           || PrunedMuxTabs.Count > 0
                           || MuxDetail.StartsWith("killed ", StringComparison.OrdinalIgnoreCase);
}

public sealed record ReclaimOptions
{
    public IReadOnlyList<string> CandidateIds { get; init; } = Array.Empty<string>();

    // The canonical local mux owner, when present. The callback must first establish ownership from the muxd
    // listing and then return a verified teardown result. A failed or unverifiable mux teardown aborts reclaim
    // before process/claim cleanup, because starting a terminal over that tab would create a second writer.
    public Func<Task<ReclaimMuxResult>>? KillMux { get; init; }

    // Best-effort. A kill failure NEVER aborts the flow - it only shapes what tier-2 evidence exists and what
    // the report says. Default: the alias-aware Core kill with its identity-checked exit verification.
    public Func<IReadOnlyList<string>, RunningSessions.KillResult>? Kill { get; init; }

    // Returns the mux tab names whose Current pointer was pruned. Null = the muxd probe could not answer, so
    // nothing is pruned (an unverifiable probe must never be read as "the mux session is gone").
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? PruneMuxCurrent { get; init; }

    // Launching is intentionally not part of reclaim. Explicit Resume/Continue owns new-session creation.
    public Func<Task<(bool Ok, string Detail)>>? Launch { get; init; }
    public SessionLaunchRequest? LaunchRequest { get; init; }
    public SessionLaunchGovernorOptions? GovernorOptions { get; init; }

    public Func<ReclaimRefusal, Task<ReclaimRefusalChoice>>? OnRefusal { get; init; }
    public Action<string>? Progress { get; init; }

    public SessionLaunchClaims.Options? ClaimOptions { get; init; }
    public SessionOwnerRecords.Options? RecordOptions { get; init; }

    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }
    // Tests and callers that need a progressing clock can provide one; production uses UTC now.
    public Func<DateTimeOffset>? Clock { get; init; }
    public CancellationToken Cancellation { get; init; }
    public DateTimeOffset? Now { get; init; }
}

// Reclaim = take control, for real. The old "Unblock safely" was gated on the very oracle whose failure
// produces BLOCKED, was alias-blind, ran up to ~24s of killing on the UI thread, hid itself unless the danger
// set was exactly {Live owner, Launch claim}, force-cleared the app's OWN unexpired claims, and never
// relaunched. This is the replacement, and none of it touches TryScan / TryLiveSessionPids /
// TryAllLiveSessionIds as a gate.
//
// The flow, in order: canonical mux teardown (when live, verified) -> process kill (best effort,
// identity-verified) -> tiered clear of EVERY claim across the full candidate set -> prune stale mux
// custody -> stop here; continuation is an explicit separate user action.
public static class SessionReclaim
{
    public const string LostRaceDetail = "another launcher got there first";

    public static async Task<ReclaimReport> ExecuteAsync(ReclaimOptions options)
    {
        var ids = Normalize(options.CandidateIds);
        var progress = options.Progress ?? (_ => { });
        var claimResults = new List<ReclaimClaimResult>();
        var pruned = Array.Empty<string>() as IReadOnlyList<string>;

        // ---- 1. canonical mux teardown ---------------------------------------------------------------
        // The mux listing is the authoritative owner check. If it says the canonical session is live, the
        // callback must kill that exact name and verify the killed response before anything else changes.
        var mux = await RunMuxKillAsync(options);
        if (!mux.ProbeOk || (mux.OwnerFound && !mux.TeardownVerified))
        {
            var refused = new ReclaimReport(
                KillOk: false,
                KillDetail: "not attempted: mux owner teardown did not complete safely",
                MuxOk: false,
                MuxDetail: mux.Detail,
                ConfirmedExited: Array.Empty<ReclaimKilledPid>(),
                Claims: Array.Empty<ReclaimClaimResult>(),
                PrunedMuxTabs: Array.Empty<string>(),
                Relaunched: false,
                LostLaunchRace: false,
                RelaunchDetail: "not relaunched: mux owner teardown failed or could not be verified",
                Headline: "");
            return refused with { Headline = Headline(refused) };
        }

        // ---- 2. process kill -------------------------------------------------------------------------
        progress("Stopping any owner of this chat...");
        var kill = RunKill(options, ids);

        // ---- 3/4. tiered clear over the FULL candidate set ------------------------------------------
        progress("Clearing launch reservations...");
        var claims = SessionLaunchClaims.ReadClaimsForSession(
            ids.Count > 0 ? ids[0] : null,
            ids.Skip(1),
            options.ClaimOptions);

        foreach (var originalClaim in claims)
        {
            var claim = originalClaim;
            var evidence = BuildEvidence(options, kill, graceWaitedOut: false);
            var outcome = SessionLaunchClaims.TryReclaimClear(claim, evidence, out var detail);

            if (outcome == ReclaimClearOutcome.RefusedAliveOwner && options.OnRefusal is not null)
            {
                var refusal = new ReclaimRefusal(claim, detail, OwnerVerifiedAlive(claim, evidence), claim.OwnerPid);
                var choice = await options.OnRefusal(refusal).ConfigureAwait(false);

                if (choice == ReclaimRefusalChoice.WaitForExpiry)
                {
                    await WaitOutGraceAsync(options, claim, progress).ConfigureAwait(false);
                    // The reservation may have expired, been replaced, or entered an in-flight acquire while
                    // the operator was waiting. Never apply grace evidence to the stale object we first read.
                    var current = SessionLaunchClaims.ReadClaimsForSession(
                        ids.Count > 0 ? ids[0] : null,
                        ids.Skip(1),
                        options.ClaimOptions)
                        .FirstOrDefault(c => string.Equals(c.Path, claim.Path, StringComparison.OrdinalIgnoreCase));
                    if (current is null)
                    {
                        outcome = ReclaimClearOutcome.Cleared;
                        detail = "launch claim was cleared while waiting";
                    }
                    else if (!SameClaimIdentity(claim, current))
                    {
                        outcome = ReclaimClearOutcome.Failed;
                        detail = "launch claim was replaced while waiting; refusing to clear the replacement reservation";
                    }
                    else
                    {
                        claim = current;
                        var waited = BuildEvidence(options, kill, graceWaitedOut: true);
                        outcome = SessionLaunchClaims.TryReclaimClear(claim, waited, out detail);
                    }
                }
            }

            claimResults.Add(new ReclaimClaimResult(claim.Path, claim.SessionId, outcome, detail));
            if (outcome == ReclaimClearOutcome.LaunchInFlight) break;   // never force past a live acquire
        }

        // ---- 5. stale mux custody -------------------------------------------------------------------
        if (options.PruneMuxCurrent is not null)
        {
            progress("Checking mux custody...");
            try { pruned = options.PruneMuxCurrent(ids) ?? Array.Empty<string>(); }
            catch { pruned = Array.Empty<string>(); }
        }

        var report = new ReclaimReport(
            KillOk: kill.Ok,
            KillDetail: kill.Detail,
            MuxOk: !mux.OwnerFound || mux.TeardownVerified,
            MuxDetail: mux.Detail,
            ConfirmedExited: kill.ConfirmedExited,
            Claims: claimResults,
            PrunedMuxTabs: pruned,
            Relaunched: false,
            LostLaunchRace: false,
            RelaunchDetail: "reclaim is cleanup-only; continuation was not requested",
            Headline: "");
        return report with { Headline = Headline(report) };
    }

    // Explicit launch leasing remains available to Resume/Continue. Reclaim does not call this helper.
    public static bool TryAcquireReclaimLease(
        SessionLaunchRequest request,
        SessionLaunchGovernorOptions? baseOptions,
        out SessionLaunchLease? lease,
        out string detail,
        out bool lostRace)
    {
        var options = (baseOptions ?? new SessionLaunchGovernorOptions()) with { IsSessionLive = _ => false };
        var governor = new SessionLaunchGovernor(options);
        var acquired = governor.TryAcquire(request, out lease, out detail);
        lostRace = !acquired && detail.Contains("launch already pending", StringComparison.OrdinalIgnoreCase);
        if (lostRace) detail = LostRaceDetail + " (" + detail + ")";
        return acquired;
    }

    // [F#5] An app-launched session retains its claim for ~2 minutes. Once the mux handoff paths are governed,
    // /tomux and the guard's kill-and-takeover inside that window would be refused by the PREVIOUS launch's own
    // leftover claim. A verified kill of a process tied to that claim is exactly the tier-2 evidence the reclaim
    // tiers already accept, so the handoff paths reuse it rather than growing a second, weaker force-clear.
    public static IReadOnlyList<ReclaimClaimResult> ClearClaimsTiedToVerifiedKills(
        string? sessionId,
        IEnumerable<string>? aliases,
        IEnumerable<int> verifiedExitedPids,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionOwnerRecords.Options? recordOptions = null)
    {
        var ids = Normalize(Candidates(sessionId, aliases));
        var pids = verifiedExitedPids?.Where(p => p > 0).Distinct().ToArray() ?? Array.Empty<int>();
        if (ids.Count == 0 || pids.Length == 0) return Array.Empty<ReclaimClaimResult>();

        // The caller resolved these pids AS this session's owners and then watched them exit, so the candidate
        // ids are what ties them - the same tie a scan-sid match would have produced.
        var evidence = new ReclaimEvidence
        {
            ConfirmedExitedPids = pids.Select(p => new ReclaimKilledPid(p, null, ids)).ToArray(),
            RecordOptions = recordOptions,
        };

        var results = new List<ReclaimClaimResult>();
        foreach (var claim in SessionLaunchClaims.ReadClaimsForSession(ids[0], ids.Skip(1), claimOptions))
        {
            var outcome = SessionLaunchClaims.TryReclaimClear(claim, evidence, out var detail);
            results.Add(new ReclaimClaimResult(claim.Path, claim.SessionId, outcome, detail));
        }
        return results;
    }

    // Stale mux custody: a Current pointer at one of our candidate ids whose muxd session is CONFIRMED absent.
    // `liveMuxNames` null means the probe could not answer - nothing is pruned, because "we couldn't ask muxd"
    // is not "the tab is gone". Live tabs are left strictly alone.
    public static IReadOnlyList<string> PruneStaleMuxCurrent(
        IDictionary<string, MuxTabRecord> muxTabHistory,
        IReadOnlyCollection<string> candidateIds,
        IReadOnlyCollection<string>? liveMuxNames)
    {
        if (muxTabHistory is null || liveMuxNames is null) return Array.Empty<string>();
        var ids = new HashSet<string>(candidateIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (ids.Count == 0) return Array.Empty<string>();
        var live = new HashSet<string>(liveMuxNames, StringComparer.OrdinalIgnoreCase);

        var pruned = new List<string>();
        foreach (var kv in muxTabHistory.ToList())
        {
            var current = kv.Value?.Current;
            if (current is null || string.IsNullOrWhiteSpace(current.Id) || !ids.Contains(current.Id)) continue;
            if (live.Contains(kv.Key)) continue;   // still there - not ours to touch
            kv.Value!.Current = null;
            pruned.Add(kv.Key);
        }
        return pruned;
    }

    // ---- internals ---------------------------------------------------------------------------------

    private static async Task<ReclaimMuxResult> RunMuxKillAsync(ReclaimOptions options)
    {
        if (options.KillMux is null) return new ReclaimMuxResult(true, false, true, "no local mux owner found");
        try
        {
            return await options.KillMux().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new ReclaimMuxResult(false, true, false, "mux teardown threw: " + ex.Message);
        }
    }

    private static RunningSessions.KillResult RunKill(ReclaimOptions options, IReadOnlyList<string> ids)
    {
        try
        {
            return options.Kill is not null
                ? options.Kill(ids)
                : RunningSessions.KillWithEvidence(ids, ownerRecords: options.RecordOptions);
        }
        catch (Exception ex)
        {
            return new RunningSessions.KillResult(false, "kill threw: " + ex.Message, Array.Empty<ReclaimKilledPid>());
        }
    }

    private static ReclaimEvidence BuildEvidence(
        ReclaimOptions options,
        RunningSessions.KillResult kill,
        bool graceWaitedOut)
        => new()
        {
            ConfirmedExitedPids = kill.ConfirmedExited,
            RecordOptions = options.RecordOptions,
            GraceWaitedOut = graceWaitedOut,
            Now = options.Now,
        };

    private static bool OwnerVerifiedAlive(SessionLaunchClaims.LaunchClaimInfo claim, ReclaimEvidence evidence)
    {
        if (claim.OwnerPid <= 0) return false;
        try { return evidence.ProcessAlive(claim.OwnerPid); }
        catch { return false; }
    }

    private static bool SameClaimIdentity(
        SessionLaunchClaims.LaunchClaimInfo expected,
        SessionLaunchClaims.LaunchClaimInfo actual)
        => string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
           && expected.OwnerPid == actual.OwnerPid
           && expected.CreatedUtc == actual.CreatedUtc
           && expected.ExpiresUtc == actual.ExpiresUtc
           && expected.CandidateIds.SequenceEqual(actual.CandidateIds, StringComparer.Ordinal);

    private static async Task WaitOutGraceAsync(
        ReclaimOptions options,
        SessionLaunchClaims.LaunchClaimInfo claim,
        Action<string> progress)
    {
        var delay = options.Delay ?? ((span, token) => Task.Delay(span, token));
        var clock = options.Clock ?? (() => DateTimeOffset.UtcNow);
        // Bounded by the claim's own TTL: the reservation is 2 minutes by construction, and a claim whose
        // ExpiresUtc is garbage must not turn this into an unbounded wait.
        var start = options.Now ?? clock();
        var deadline = claim.ExpiresUtc.AddSeconds(1);
        var cap = start.Add(SessionLaunchClaims.DefaultStartupGrace).AddSeconds(5);
        if (deadline > cap) deadline = cap;

        while (true)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var remaining = deadline - clock();
            if (remaining <= TimeSpan.Zero) return;
            progress($"Waiting for the reservation to expire ({(int)Math.Ceiling(remaining.TotalSeconds)}s)...");
            var step = remaining > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : remaining;
            await delay(step, options.Cancellation).ConfigureAwait(false);
        }
    }

    private static string Headline(ReclaimReport report)
    {
        if (!report.MuxOk)
            return "Reclaim stopped: " + report.MuxDetail;
        if (report.LaunchInFlight) return "A launch is already in flight for this chat; nothing was forced.";
        var refused = report.Claims.Count(c => c.Outcome == ReclaimClearOutcome.RefusedAliveOwner);
        if (refused > 0)
            return "A live launch reservation still holds this chat; continuation remains blocked.";
        var failedClaims = report.Claims.Where(c => c.Outcome == ReclaimClearOutcome.Failed).ToList();
        if (failedClaims.Count > 0)
        {
            var reason = failedClaims[0].Detail;
            if (failedClaims.Count > 1) reason += $" (+{failedClaims.Count - 1} more)";
            return "Reclaim completed with reservation cleanup failure (" + reason + ").";
        }
        if (!report.Changed)
            return report.KillOk
                ? "Nothing to reclaim: no owner, reservation, or mux custody was changed."
                : "Reclaim could not change ownership - " + report.KillDetail;
        if (report.ConfirmedExited.Count > 0)
            return "Reclaimed: stopped " + report.ConfirmedExited.Count + " owner process"
                   + (report.ConfirmedExited.Count == 1 ? "" : "es")
                   + "; cleanup completed and ready to continue.";
        return "Reclaimed: cleanup completed and ready to continue.";
    }

    private static List<string> Candidates(string? sessionId, IEnumerable<string>? aliases)
    {
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(sessionId)) ids.Add(sessionId!);
        if (aliases is not null) ids.AddRange(aliases);
        return ids;
    }

    private static List<string> Normalize(IEnumerable<string>? ids)
    {
        var result = new List<string>();
        foreach (var raw in ids ?? Array.Empty<string>())
        {
            var id = (raw ?? "").Trim();
            if (id.Length == 0) continue;
            if (!result.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
                result.Add(id);
        }
        return result;
    }
}
