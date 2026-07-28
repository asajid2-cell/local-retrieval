using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// Cross-process reservation for the dangerous gap between "not live" and "writer visible".
// Live process detection is still the authority once the agent is running; this closes the
// startup race where two launchers both check "not live" before either process appears.
public static class SessionLaunchClaims
{
    public static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromMinutes(2);

    public sealed record LaunchClaimInfo(
        string SessionId,
        IReadOnlyList<string> CandidateIds,
        int OwnerPid,
        string OwnerProcess,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ExpiresUtc,
        string Reason,
        string Path)
    {
        public bool IsExpired(DateTimeOffset now) => ExpiresUtc <= now;
    }

    public sealed record Options(string? RootDirectory = null, TimeSpan? StaleAfter = null, DateTimeOffset? Now = null)
    {
        public string EffectiveRootDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RootDirectory)) return RootDirectory!;
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
                return Path.Combine(local, "CodexLocalRetrieval", "launch-claims");
            }
        }

        public TimeSpan EffectiveStaleAfter => StaleAfter is { } s && s > TimeSpan.Zero ? s : DefaultStartupGrace;
        public DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;
    }

    public static bool TryAcquire(
        string? sessionId,
        IEnumerable<string>? aliases,
        string reason,
        out SessionLaunchClaim? claim,
        out string detail,
        Func<string, bool>? isSessionLive = null,
        Options? options = null)
    {
        claim = null;
        detail = "";
        var ids = CandidateIds(sessionId, aliases);
        if (ids.Count == 0)
        {
            detail = "missing session id";
            return false;
        }

        options ??= new Options();
        var now = options.EffectiveNow;
        var ttl = options.EffectiveStaleAfter;
        var root = options.EffectiveRootDirectory;

        if (!TryCheckAnyLive(ids, isSessionLive, out var live, out var liveId, out detail))
            return false;
        if (live)
        {
            detail = $"session {liveId} is already running on this PC; refusing to start a second writer";
            return false;
        }

        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            detail = "couldn't create launch-claim directory: " + ex.Message;
            return false;
        }

        var targets = ids
            .Select(id => new ClaimTarget(id, Path.Combine(root, ClaimFileName(id))))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var target in targets)
        {
            if (!TryClearOrReportBlockingClaim(target, now, ttl, out detail))
                return false;
        }

        var held = new List<HeldClaimFile>();
        var expiresAt = now.Add(ttl);
        var metadata = new ClaimMetadata
        {
            SessionId = ids[0],
            CandidateIds = ids,
            OwnerPid = Environment.ProcessId,
            OwnerProcess = CurrentProcessName(),
            CreatedUtc = now,
            ExpiresUtc = expiresAt,
            Reason = string.IsNullOrWhiteSpace(reason) ? "session launch" : reason.Trim()
        };

        foreach (var target in targets)
        {
            try
            {
                var stream = new FileStream(target.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                WriteMetadata(stream, metadata);
                held.Add(new HeldClaimFile(target.Path, stream));
            }
            catch (IOException) when (File.Exists(target.Path))
            {
                ReleaseHeld(held, deleteFiles: true);
                detail = BlockingDetail(target.Id, ReadClaimMetadata(target.Path), now, ttl);
                return false;
            }
            catch (Exception ex)
            {
                ReleaseHeld(held, deleteFiles: true);
                detail = $"couldn't reserve launch for session {target.Id}: {ex.Message}";
                return false;
            }
        }

        if (!TryCheckAnyLive(ids, isSessionLive, out live, out liveId, out detail, bypassCache: true))
        {
            ReleaseHeld(held, deleteFiles: true);
            return false;
        }
        if (live)
        {
            ReleaseHeld(held, deleteFiles: true);
            detail = $"session {liveId} became live during launch reservation; refusing to start a second writer";
            return false;
        }

        claim = new SessionLaunchClaim(ids[0], ids, expiresAt, held);
        detail = "reserved";
        return true;
    }

    public static bool TryAcquireForResumeCommand(
        string? commandLine,
        string reason,
        out string sessionId,
        out SessionLaunchClaim? claim,
        out string detail,
        Func<string, bool>? isSessionLive = null,
        Options? options = null)
        => TryAcquireForResumeCommand(commandLine, reason, out sessionId, out claim, out detail, aliases: null, isSessionLive, options);

    public static bool TryAcquireForResumeCommand(
        string? commandLine,
        string reason,
        out string sessionId,
        out SessionLaunchClaim? claim,
        out string detail,
        IEnumerable<string>? aliases,
        Func<string, bool>? isSessionLive = null,
        Options? options = null)
    {
        if (!ArchiveService.TryParseSingleResumedSessionId(commandLine ?? "", out sessionId, out detail))
        {
            claim = null;
            return string.Equals(detail, "command does not resume a stored session", StringComparison.OrdinalIgnoreCase);
        }
        return TryAcquire(sessionId, aliases, reason, out claim, out detail, isSessionLive, options);
    }

    public static IReadOnlyList<LaunchClaimInfo> ReadClaimsForSession(string? sessionId, IEnumerable<string>? aliases = null, Options? options = null)
    {
        options ??= new Options();
        var root = options.EffectiveRootDirectory;
        var ids = CandidateIds(sessionId, aliases);
        if (ids.Count == 0 || !Directory.Exists(root)) return Array.Empty<LaunchClaimInfo>();

        var claims = new List<LaunchClaimInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var path = Path.Combine(root, ClaimFileName(id));
            if (!File.Exists(path) || !seen.Add(path)) continue;
            var metadata = ReadClaimMetadata(path);
            if (metadata is null)
            {
                claims.Add(new LaunchClaimInfo(id, new[] { id }, 0, "unknown", default, LastWriteExpiry(path, options.EffectiveStaleAfter), "", path));
                continue;
            }
            claims.Add(new LaunchClaimInfo(
                metadata.SessionId,
                metadata.CandidateIds,
                metadata.OwnerPid,
                metadata.OwnerProcess,
                metadata.CreatedUtc,
                metadata.ExpiresUtc,
                metadata.Reason,
                path));
        }
        return claims;
    }

    // Every claim clear in the app now goes through the C1 tiers. This wrapper is the janitor-ish entry point
    // (expired sweeps, opportunistic cleanup); it therefore gets [F#2] for free and can no longer silently
    // clear an unexpired dead-owner claim whose writer may still be seconds from appearing inside the grace.
    // Pass `evidence` when the caller has something concrete (a verified kill, a waited-out grace) to offer.
    public static bool TryClearAbandonedClaim(
        LaunchClaimInfo claim,
        out string detail,
        DateTimeOffset? now = null,
        Func<int, bool>? isProcessAlive = null,
        ReclaimEvidence? evidence = null)
    {
        var basis = evidence ?? ReclaimEvidence.None;
        if (now is not null) basis = basis with { Now = now };
        if (isProcessAlive is not null) basis = basis with { IsProcessAlive = isProcessAlive };
        return TryReclaimClear(claim, basis, out detail) == ReclaimClearOutcome.Cleared;
    }

    // ---- C1 tiered force-clear (NORMATIVE, plan §2.2) ------------------------------------------------
    //
    // Tier 1 - expired claims clear freely. An UNEXPIRED claim whose owner is dead clears only when the
    //          wrapper tied to it is ALSO confirmed dead by identity [F#2]: the claim's owner is the app that
    //          acquired it, and the `cmd /k -> claude` tree it launched outlives the app. App dead + wrapper
    //          alive (or no record at all) means the writer may be seconds from appearing - that is tier 2.
    // Tier 2 - unexpired + owner alive (or unknown) REFUSES, unless something concretely tied to this claim was
    //          killed and confirmed exited, or the grace was waited out. There is NO own-pid special case
    //          [F#1]: a claim owned by Environment.ProcessId with this app alive refuses like any other.
    // Tier 3 - a sharing violation on the quarantine move means a launcher is holding the claim stream open
    //          mid-acquire. That is a legitimate launch in flight: abort this claim, never retry-force past it.
    public static ReclaimClearOutcome TryReclaimClear(LaunchClaimInfo claim, ReclaimEvidence evidence, out string detail)
    {
        detail = "";
        if (claim is null || string.IsNullOrWhiteSpace(claim.Path))
        {
            detail = "launch claim path is missing";
            return ReclaimClearOutcome.Failed;
        }

        evidence ??= ReclaimEvidence.None;
        var now = evidence.EffectiveNow;

        if (claim.IsExpired(now))
            return Quarantine(claim, "expired claim cleared", out detail);

        // Owner state. A claim with no readable owner pid is UNKNOWN, not dead - it gets tier-2 treatment.
        var ownerKnown = claim.OwnerPid > 0;
        var ownerAlive = ownerKnown && SafeAlive(evidence, claim.OwnerPid);

        if (ownerKnown && !ownerAlive)
        {
            var (tied, wrapperDetail) = TiedWrappersConfirmedDead(claim, evidence);
            if (tied)
                return Quarantine(claim, "abandoned claim cleared (" + wrapperDetail + ")", out detail);
            // Fall through to tier 2: the writer may still be spawning behind a dead app.
        }

        // ---- tier 2 -----------------------------------------------------------------------------------
        if (evidence.GraceWaitedOut)
            return Quarantine(claim, "claim cleared after the reservation was waited out", out detail);

        var killedTie = TiedKilledPid(claim, evidence);
        if (killedTie is not null)
            return Quarantine(claim, "claim cleared: " + killedTie + " was killed and confirmed exited", out detail);

        detail = ownerAlive
            ? $"launch claim is still owned by {claim.OwnerProcess} pid {claim.OwnerPid} (alive, verified)"
            : ownerKnown
                ? $"launch claim owner {claim.OwnerProcess} pid {claim.OwnerPid} is gone, but nothing ties a confirmed-dead launch to it yet"
                : "launch claim owner is unknown - scan unverifiable";
        return ReclaimClearOutcome.RefusedAliveOwner;
    }

    // [F#2]. "Confirmed dead by identity" = the recorded wrapper pid is gone, or the pid is alive but its start
    // time no longer matches the record (so the pid was REUSED and our wrapper is dead). A record with a live
    // wrapper, or a record with no start time to identify a live pid by, is NOT confirmation.
    private static (bool Confirmed, string Detail) TiedWrappersConfirmedDead(LaunchClaimInfo claim, ReclaimEvidence evidence)
    {
        var records = evidence.RecordsFor(claim.CandidateIds)
            .Where(r => r.WrapperPid > 0 && Overlaps(r.CandidateIds, claim.CandidateIds))
            .ToList();
        if (records.Count == 0) return (false, "no owner record ties a launch to this claim");

        foreach (var record in records)
        {
            var (alive, start) = SafeIdentity(evidence, record.WrapperPid);
            if (!alive) continue;                       // gone -> dead
            if (record.WrapperStartTimeUtc is null)
                return (false, $"recorded wrapper pid {record.WrapperPid} is alive with no start time to identify it by");
            if (Math.Abs((start - record.WrapperStartTimeUtc.Value.UtcDateTime).TotalSeconds) <= 1)
                return (false, $"recorded wrapper pid {record.WrapperPid} is still alive");
            // identity mismatch -> the pid was reused; our wrapper is dead.
        }
        return (true, "the tied launch wrapper is confirmed dead");
    }

    // Tier-2 evidence: the recorded wrapper pid for this claim, or a killed pid whose matched session ids
    // intersect the claim's candidate ids.
    private static string? TiedKilledPid(LaunchClaimInfo claim, ReclaimEvidence evidence)
    {
        if (evidence.ConfirmedExitedPids.Count == 0) return null;
        var wrapperPids = evidence.RecordsFor(claim.CandidateIds)
            .Where(r => r.WrapperPid > 0 && Overlaps(r.CandidateIds, claim.CandidateIds))
            .Select(r => r.WrapperPid)
            .ToHashSet();

        foreach (var killed in evidence.ConfirmedExitedPids)
        {
            if (killed.Pid <= 0) continue;
            if (wrapperPids.Contains(killed.Pid)) return $"the recorded launch wrapper pid {killed.Pid}";
            if (Overlaps(killed.SessionIds, claim.CandidateIds)) return $"pid {killed.Pid}";
        }
        return null;
    }

    private static bool Overlaps(IReadOnlyCollection<string>? a, IReadOnlyCollection<string>? b)
    {
        if (a is null || b is null) return false;
        foreach (var left in a)
            foreach (var right in b)
                if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool SafeAlive(ReclaimEvidence evidence, int pid)
    {
        // An unanswerable liveness probe must read as "alive" here: this decides whether to force-clear a
        // reservation, so uncertainty has to fall on the refusing side.
        try { return evidence.ProcessAlive(pid); }
        catch { return true; }
    }

    private static (bool Alive, DateTime Start) SafeIdentity(ReclaimEvidence evidence, int pid)
    {
        try { return evidence.Identity(pid); }
        catch { return (true, default); }
    }

    // The move mechanics are unchanged - the tiers decide WHETHER to attempt this, never how it works.
    private static ReclaimClearOutcome Quarantine(LaunchClaimInfo claim, string successDetail, out string detail)
    {
        try
        {
            if (!File.Exists(claim.Path))
            {
                detail = "already gone";
                return ReclaimClearOutcome.Cleared;
            }
            var quarantine = claim.Path + ".clearing-" + Guid.NewGuid().ToString("N");
            File.Move(claim.Path, quarantine);
            var moved = ReadClaimMetadata(quarantine);
            if (!SameClaim(claim, moved))
            {
                try
                {
                    if (!File.Exists(claim.Path)) File.Move(quarantine, claim.Path);
                }
                catch { }
                detail = "launch claim changed during cleanup; refusing to delete it";
                return ReclaimClearOutcome.Failed;
            }
            File.Delete(quarantine);
            detail = successDetail;
            return ReclaimClearOutcome.Cleared;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            // TryAcquire holds the claim stream with FileShare.Read for the WHOLE acquire, so this is not an
            // error condition - it is a launcher mid-acquisition, and the claim file stays exactly as it is.
            detail = "a launch is already in flight for this chat; the reservation is held open by its launcher";
            return ReclaimClearOutcome.LaunchInFlight;
        }
        catch (Exception ex)
        {
            detail = "claim cleanup failed: " + ex.Message;
            return ReclaimClearOutcome.Failed;
        }
    }

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private static bool IsSharingViolation(IOException ex)
        => ex.HResult == ErrorSharingViolation || ex.HResult == ErrorLockViolation;

    private static bool SameClaim(LaunchClaimInfo expected, ClaimMetadata? actual)
        => actual is not null
           && string.Equals(expected.SessionId, actual.SessionId, StringComparison.Ordinal)
           && expected.OwnerPid == actual.OwnerPid
           && expected.CreatedUtc == actual.CreatedUtc
           && expected.ExpiresUtc == actual.ExpiresUtc
           && expected.CandidateIds.SequenceEqual(actual.CandidateIds, StringComparer.Ordinal);

    // [F#3] `bypassCache` is true for the POST-claim re-check only. The pre-check may ride the burst cache with
    // everyone else, but once the reservation is held the whole question is "did the world change in the last
    // few milliseconds" — and a cached answer is, by construction, unable to say.
    private static bool TryCheckAnyLive(
        IReadOnlyList<string> candidateIds,
        Func<string, bool>? isSessionLive,
        out bool live,
        out string liveId,
        out string detail,
        bool bypassCache = false)
    {
        live = false;
        detail = "";
        if (isSessionLive is not null)
        {
            foreach (var id in candidateIds)
            {
                bool isLive;
                try { isLive = isSessionLive(id); }
                catch (Exception ex)
                {
                    liveId = "";
                    detail = $"couldn't verify whether session {id} is live ({ex.Message}); refusing to risk a second writer";
                    return false;
                }
                if (isLive) { live = true; liveId = id; return true; }
            }
            liveId = "";
            return true;
        }

        var verified = RunningSessions.TryAllLiveSessionIds(out var liveIds, out var verificationDetail, bypassCache);
        foreach (var id in candidateIds)
            if (liveIds.Contains(id)) { live = true; liveId = id; return true; }
        if (!verified)
        {
            liveId = "";
            detail = verificationDetail;
            return false;
        }
        liveId = "";
        return true;
    }

    private static bool TryClearOrReportBlockingClaim(ClaimTarget target, DateTimeOffset now, TimeSpan ttl, out string detail)
    {
        detail = "";
        if (!File.Exists(target.Path)) return true;

        var metadata = ReadClaimMetadata(target.Path);
        var expiresAt = metadata?.ExpiresUtc ?? LastWriteExpiry(target.Path, ttl);
        if (expiresAt <= now)
        {
            try
            {
                File.Delete(target.Path);
                return true;
            }
            catch (Exception ex)
            {
                detail = $"expired launch claim for session {target.Id} could not be cleared ({ex.Message}); refusing to risk a second writer";
                return false;
            }
        }

        detail = BlockingDetail(target.Id, metadata, now, ttl);
        return false;
    }

    private static string BlockingDetail(string sessionId, ClaimMetadata? metadata, DateTimeOffset now, TimeSpan ttl)
    {
        var expiresAt = metadata?.ExpiresUtc ?? now.Add(ttl);
        var owner = metadata is null
            ? "unknown owner"
            : $"{metadata.OwnerProcess} pid {metadata.OwnerPid}";
        return $"launch already pending for session {sessionId} ({owner}, expires {expiresAt.LocalDateTime:G}); refusing to start another writer";
    }

    private static DateTimeOffset LastWriteExpiry(string path, TimeSpan ttl)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero).Add(ttl); }
        catch { return DateTimeOffset.UtcNow.Add(ttl); }
    }

    private static ClaimMetadata? ReadClaimMetadata(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ClaimMetadata>(fs);
        }
        catch { return null; }
    }

    private static void WriteMetadata(FileStream stream, ClaimMetadata metadata)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true });
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static List<string> CandidateIds(string? sessionId, IEnumerable<string>? aliases)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0 || id.Any(ch => !(ch <= 0x7f && (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))) return;
            if (!ids.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
                ids.Add(id);
        }

        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    private static string ClaimFileName(string id)
    {
        var cleaned = new string(id.Where(ch => ch <= 0x7f && (char.IsLetterOrDigit(ch) || ch is '-' or '_')).Take(36).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "session";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()))).ToLowerInvariant();
        return $"{cleaned}-{hash[..16]}.claim.json";
    }

    private static string CurrentProcessName()
    {
        try { return Process.GetCurrentProcess().ProcessName; }
        catch { return "unknown"; }
    }

    // (The old HasExited-based liveness helper is gone: every claim-clearing decision now routes through
    // ReclaimEvidence, whose default is the exit-code rule [F#6] in ProcessOpenFiles.IsAlive. A pid that is
    // merely openable — a zombie whose handle someone still holds — must never read as a live claim owner.)

    private static void ReleaseHeld(List<HeldClaimFile> held, bool deleteFiles)
    {
        foreach (var h in held)
        {
            try { h.Stream.Dispose(); } catch { }
            if (deleteFiles)
            {
                try { File.Delete(h.Path); } catch { }
            }
        }
        held.Clear();
    }

    private sealed record ClaimTarget(string Id, string Path);

    internal sealed record HeldClaimFile(string Path, FileStream Stream);

    private sealed class ClaimMetadata
    {
        public string SessionId { get; set; } = "";
        public List<string> CandidateIds { get; set; } = new();
        public int OwnerPid { get; set; }
        public string OwnerProcess { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public string Reason { get; set; } = "";
    }
}

public sealed class SessionLaunchClaim : IDisposable
{
    private readonly object _gate = new();
    private readonly List<SessionLaunchClaims.HeldClaimFile> _held;
    private bool _retained;
    private bool _disposed;

    internal SessionLaunchClaim(string sessionId, IReadOnlyList<string> candidateIds, DateTimeOffset expiresAt, List<SessionLaunchClaims.HeldClaimFile> held)
    {
        SessionId = sessionId;
        CandidateIds = candidateIds.ToArray();
        ExpiresAt = expiresAt;
        _held = held;
    }

    public string SessionId { get; }
    public IReadOnlyList<string> CandidateIds { get; }
    public DateTimeOffset ExpiresAt { get; }

    // Use after a terminal/mux launch succeeds but we do not own the child writer handle. The file is
    // intentionally left behind until the short startup grace expires; live-process detection then owns it.
    public void RetainUntilExpiry()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _retained = true;
            foreach (var h in _held)
            {
                try { h.Stream.Dispose(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var h in _held)
            {
                try { h.Stream.Dispose(); } catch { }
                if (!_retained)
                {
                    try { File.Delete(h.Path); } catch { }
                }
            }
            _held.Clear();
        }
    }
}
