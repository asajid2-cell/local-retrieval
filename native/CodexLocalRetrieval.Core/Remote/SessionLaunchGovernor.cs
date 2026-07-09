namespace CodexLocalRetrieval.Core.Remote;

public sealed record SessionLaunchRequest(
    string? SessionId,
    IEnumerable<string>? Aliases,
    string Tool,
    string Source,
    string Reason,
    string RefusedKind,
    string StartedKind,
    string FailedKind,
    string? Title = null,
    string? Workspace = null,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed record SessionLaunchGovernorOptions(
    SessionLaunchClaims.Options? ClaimOptions = null,
    SessionEventLedger.Options? EventOptions = null,
    Func<string, bool>? IsSessionLive = null,
    Action<string>? Log = null);

// One authority for app-owned writer launches. Callers still perform their own transport-specific
// start, but they do not create a Claude/Codex writer unless this governor issued the lease.
public sealed class SessionLaunchGovernor
{
    private readonly SessionLaunchClaims.Options? _claimOptions;
    private readonly SessionEventLedger.Options? _eventOptions;
    private readonly Func<string, bool>? _isSessionLive;
    private readonly Action<string>? _log;

    public SessionLaunchGovernor(SessionLaunchGovernorOptions? options = null)
    {
        _claimOptions = options?.ClaimOptions;
        _eventOptions = options?.EventOptions;
        _isSessionLive = options?.IsSessionLive;
        _log = options?.Log;
    }

    public bool TryAcquire(SessionLaunchRequest request, out SessionLaunchLease? lease, out string detail)
    {
        lease = null;
        if (!SessionLaunchClaims.TryAcquire(
                request.SessionId,
                request.Aliases,
                request.Reason,
                out var claim,
                out detail,
                _isSessionLive,
                _claimOptions))
        {
            RecordRefused(request, detail);
            return false;
        }

        lease = new SessionLaunchLease(this, request, claim);
        return true;
    }

    public SessionLaunchLease BeginFresh(SessionLaunchRequest request)
        => new(this, request, null);

    public bool TryAcquireForResumeCommand(
        string? commandLine,
        SessionLaunchRequest request,
        out string resumedSessionId,
        out SessionLaunchLease? lease,
        out string detail)
    {
        lease = null;
        if (!SessionLaunchClaims.TryAcquireForResumeCommand(
                commandLine,
                request.Reason,
                out resumedSessionId,
                out var claim,
                out detail,
                request.Aliases,
                _isSessionLive,
                _claimOptions))
        {
            var eventRequest = string.IsNullOrWhiteSpace(resumedSessionId)
                ? request
                : request with { SessionId = resumedSessionId };
            RecordRefused(eventRequest, detail);
            return false;
        }

        if (claim is not null)
        {
            var eventRequest = string.IsNullOrWhiteSpace(resumedSessionId)
                ? request
                : request with { SessionId = resumedSessionId };
            lease = new SessionLaunchLease(this, eventRequest, claim);
        }

        return true;
    }

    public bool TryAcquireRequiredResumeCommand(
        string? commandLine,
        SessionLaunchRequest request,
        out string resumedSessionId,
        out SessionLaunchLease? lease,
        out string detail)
    {
        if (!TryAcquireForResumeCommand(commandLine, request, out resumedSessionId, out lease, out detail))
            return false;
        if (!string.IsNullOrWhiteSpace(resumedSessionId) && lease is not null)
            return true;

        detail = "launch refused because the command does not resume a stored session";
        RecordRefused(request, detail);
        return false;
    }

    public void RecordRefused(SessionLaunchRequest request, string summary, IReadOnlyDictionary<string, string>? details = null)
        => Record(request, request.RefusedKind, summary, "warn", details);

    internal void RecordStarted(SessionLaunchRequest request, string? summary, IReadOnlyDictionary<string, string>? details)
        => Record(request, request.StartedKind, string.IsNullOrWhiteSpace(summary) ? "Started app-owned session writer." : summary!, "info", details);

    internal void RecordFailed(SessionLaunchRequest request, string? summary, IReadOnlyDictionary<string, string>? details)
        => Record(request, request.FailedKind, string.IsNullOrWhiteSpace(summary) ? "App-owned session writer failed to start." : summary!, "error", details);

    private void Record(SessionLaunchRequest request, string kind, string summary, string severity, IReadOnlyDictionary<string, string>? details)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            request.SessionId,
            request.Tool,
            request.Title,
            WorkspaceLabel(request.Workspace),
            source: request.Source,
            severity: severity,
            details: MergeDetails(request.Details, details),
            sessionIds: CandidateIds(request.SessionId, request.Aliases));
        SessionEventLedger.AppendBestEffort(ev, _log, _eventOptions);
    }

    private static Dictionary<string, string>? MergeDetails(IReadOnlyDictionary<string, string>? first, IReadOnlyDictionary<string, string>? second)
    {
        if ((first is null || first.Count == 0) && (second is null || second.Count == 0)) return null;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (first is not null)
            foreach (var kv in first) merged[kv.Key] = kv.Value;
        if (second is not null)
            foreach (var kv in second) merged[kv.Key] = kv.Value;
        return merged;
    }

    private static List<string> CandidateIds(string? sessionId, IEnumerable<string>? aliases)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) return;
            if (!ids.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
        }

        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        return ids;
    }

    private static string WorkspaceLabel(string? workspace)
    {
        workspace = (workspace ?? "").Trim();
        if (workspace.Length == 0) return "";
        try
        {
            var trimmed = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch { return ""; }
    }
}

public sealed class SessionLaunchLease : IDisposable
{
    private readonly object _gate = new();
    private readonly SessionLaunchGovernor _governor;
    private readonly SessionLaunchRequest _request;
    private readonly SessionLaunchClaim? _claim;
    private bool _retained;
    private bool _disposed;

    internal SessionLaunchLease(SessionLaunchGovernor governor, SessionLaunchRequest request, SessionLaunchClaim? claim)
    {
        _governor = governor;
        _request = request;
        _claim = claim;
    }

    public string? SessionId => _request.SessionId;

    public void MarkStarted(string? summary = null, IReadOnlyDictionary<string, string>? details = null, bool retainUntilExpiry = true)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (retainUntilExpiry && !_retained)
            {
                _claim?.RetainUntilExpiry();
                _retained = true;
            }
            _governor.RecordStarted(_request, summary, details);
        }
    }

    public void MarkFailed(string? summary = null, IReadOnlyDictionary<string, string>? details = null)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _governor.RecordFailed(_request, summary, details);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _claim?.Dispose();
        }
    }
}
