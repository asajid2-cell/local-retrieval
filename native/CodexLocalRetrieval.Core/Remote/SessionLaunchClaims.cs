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

        if (!TryCheckAnyLive(ids, isSessionLive, out live, out liveId, out detail))
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

    private static bool TryCheckAnyLive(
        IReadOnlyList<string> candidateIds,
        Func<string, bool>? isSessionLive,
        out bool live,
        out string liveId,
        out string detail)
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

        var verified = RunningSessions.TryAllLiveSessionIds(out var liveIds, out var verificationDetail);
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
