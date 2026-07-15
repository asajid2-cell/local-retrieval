using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record SessionIntegrityCheck(string Name, string Severity, string Summary);

public sealed record SessionIntegrityEvent(string Kind, string Severity, string Source, string At, string Summary);

public sealed record SessionIntegrityMuxTab(
    string Name,
    bool IsCurrent,
    string Kind,
    string Color,
    string LastSeen,
    int HistoryCount);

public sealed record SessionIntegrityPendingIntent(string Tool, string Workspace, string CollectionId, string CreatedAt);

public sealed record SessionIntegrityClaim(
    string SessionId,
    IReadOnlyList<string> CandidateIds,
    int OwnerPid,
    string OwnerProcess,
    string ExpiresAt,
    string Reason,
    bool Expired,
    string Path);

public sealed record SessionIntegritySummary
{
    public string SessionId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Tool { get; init; } = "";
    public string Severity { get; init; } = "unknown";
    public string Headline { get; init; } = "";
    public bool LiveVerified { get; init; }
    public string LiveDetail { get; init; } = "";
    public IReadOnlyList<string> LiveSessionIds { get; init; } = Array.Empty<string>();
    public bool SourceExists { get; init; }
    public string SourceStatus { get; init; } = "";
    public IReadOnlyList<string> Collections { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SessionIntegrityMuxTab> MuxTabs { get; init; } = Array.Empty<SessionIntegrityMuxTab>();
    public IReadOnlyList<SessionIntegrityClaim> LaunchClaims { get; init; } = Array.Empty<SessionIntegrityClaim>();
    public IReadOnlyList<SessionIntegrityPendingIntent> PendingIntents { get; init; } = Array.Empty<SessionIntegrityPendingIntent>();
    public IReadOnlyList<SessionIntegrityEvent> RecentEvents { get; init; } = Array.Empty<SessionIntegrityEvent>();
    public IReadOnlyList<SessionIntegrityCheck> Checks { get; init; } = Array.Empty<SessionIntegrityCheck>();
}

public static class SessionIntegrity
{
    public sealed record Options(
        DateTimeOffset? Now = null,
        string? EventRootDirectory = null,
        string? ClaimRootDirectory = null,
        Func<(bool Verified, HashSet<string> LiveIds, string Detail)>? LiveIdsProvider = null,
        Func<string, bool>? FileExists = null)
    {
        public DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;
    }

    public static SessionIntegritySummary Build(AppStoreData store, ArchiveSession session, Options? options = null)
    {
        options ??= new Options();
        var now = options.EffectiveNow;
        var ids = CandidateIds(session);
        var checks = new List<SessionIntegrityCheck>();

        var live = (options.LiveIdsProvider ?? DefaultLiveIds)();
        var liveMatches = live.LiveIds.Where(id => ids.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        if (!live.Verified)
            checks.Add(new SessionIntegrityCheck("Live owner", "danger", string.IsNullOrWhiteSpace(live.Detail) ? "Could not verify live agent owners." : live.Detail));
        else if (liveMatches.Count > 0)
            checks.Add(new SessionIntegrityCheck("Live owner", "danger", "This chat already has a live owner. Resume only through a handoff or attach path."));
        else
            checks.Add(new SessionIntegrityCheck("Live owner", "ok", "No live owner was detected for this chat."));

        var claimOptions = new SessionLaunchClaims.Options(options.ClaimRootDirectory, Now: now);
        var claims = SessionLaunchClaims.ReadClaimsForSession(session.Id, session.Aliases, claimOptions)
            .Select(c => new SessionIntegrityClaim(c.SessionId, c.CandidateIds, c.OwnerPid, c.OwnerProcess, c.ExpiresUtc.ToString("O"), c.Reason, c.IsExpired(now), c.Path))
            .ToList();
        var activeClaims = claims.Where(c => !c.Expired).ToList();
        if (activeClaims.Count > 0)
            checks.Add(new SessionIntegrityCheck("Launch claim", "danger", "A launch reservation is still active; a second writer must not be started."));
        else if (claims.Count > 0)
            checks.Add(new SessionIntegrityCheck("Launch claim", "warn", "Expired launch reservation files are still present."));
        else
            checks.Add(new SessionIntegrityCheck("Launch claim", "ok", "No launch reservation is blocking this chat."));

        var exists = SourceExists(session.SourcePath, options.FileExists ?? File.Exists);
        checks.Add(exists
            ? new SessionIntegrityCheck("Source file", "ok", "The source transcript or rollout file exists on this PC.")
            : new SessionIntegrityCheck("Source file", "danger", "The source transcript or rollout file is missing on this PC."));

        var collections = store.Collections.Values
            .Where(c => c.SessionIds.Any(id => ids.Contains(id)))
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        checks.Add(collections.Count > 0
            ? new SessionIntegrityCheck("Filing", "ok", "This chat is filed in " + collections.Count + " collection" + (collections.Count == 1 ? "." : "s."))
            : new SessionIntegrityCheck("Filing", "warn", "This chat is not filed in any collection."));

        var pending = store.PendingNewChats
            .Where(p => SameTool(p.Tool, session.Tool) && SamePath(p.Cwd, session.Workspace))
            .Select(p => new SessionIntegrityPendingIntent(p.Tool, WorkspaceLabel(p.Cwd), p.CollectionId, p.CreatedAt))
            .ToList();
        if (pending.Count > 0)
            checks.Add(new SessionIntegrityCheck("Pending filing", "warn", "There is a pending new-chat filing intent in this workspace."));

        var muxTabs = FindMuxTabs(store, ids);
        if (muxTabs.Any(t => t.IsCurrent))
            checks.Add(new SessionIntegrityCheck("Mux custody", "danger", "This chat is already current in a mux tab. Attach instead of starting another writer."));
        else if (muxTabs.Count > 0)
            checks.Add(new SessionIntegrityCheck("Mux custody", "ok", "This chat appears in mux tab history."));
        else
            checks.Add(new SessionIntegrityCheck("Mux custody", "ok", "No mux tab currently claims this chat."));

        var eventOptions = new SessionEventLedger.Options(options.EventRootDirectory, now);
        var recentEvents = SessionEventLedger.ReadForSession(session.Id, session.Aliases, 12, eventOptions)
            .Select(e => new SessionIntegrityEvent(e.Kind, e.Severity, e.Source, e.At, e.Summary))
            .ToList();
        var eventIncidents = recentEvents.Count(e => IsIncident(e.Kind, e.Severity));
        if (eventIncidents > 0)
            checks.Add(new SessionIntegrityCheck("Recent events", recentEvents.Any(e => string.Equals(e.Severity, "error", StringComparison.OrdinalIgnoreCase)) ? "danger" : "warn",
                eventIncidents + " recent ownership event" + (eventIncidents == 1 ? " needs" : "s need") + " review."));
        else
            checks.Add(new SessionIntegrityCheck("Recent events", "ok", recentEvents.Count == 0 ? "No session event ledger entries yet." : "No recent refused or failed ownership events."));

        var severity = checks.Any(c => c.Severity == "danger") ? "danger" : checks.Any(c => c.Severity == "warn") ? "warn" : "ok";
        return new SessionIntegritySummary
        {
            SessionId = session.Id,
            Title = session.DisplayTitle,
            Tool = session.Tool,
            Severity = severity,
            Headline = Headline(severity, checks),
            LiveVerified = live.Verified,
            LiveDetail = live.Detail,
            LiveSessionIds = liveMatches,
            SourceExists = exists,
            SourceStatus = exists ? "source present" : "source missing",
            Collections = collections,
            MuxTabs = muxTabs,
            LaunchClaims = claims,
            PendingIntents = pending,
            RecentEvents = recentEvents,
            Checks = checks
        };
    }

    private static (bool Verified, HashSet<string> LiveIds, string Detail) DefaultLiveIds()
    {
        var verified = RunningSessions.TryAllLiveSessionIds(out var live, out var detail);
        return (verified, live, detail);
    }

    private static HashSet<string> CandidateIds(ArchiveSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0) ids.Add(id);
        }
        Add(session.Id);
        foreach (var alias in session.Aliases) Add(alias);
        return ids;
    }

    private static bool SourceExists(string? path, Func<string, bool> fileExists)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return false;
        try { return fileExists(path); }
        catch { return false; }
    }

    private static List<SessionIntegrityMuxTab> FindMuxTabs(AppStoreData store, HashSet<string> ids)
    {
        var tabs = new List<SessionIntegrityMuxTab>();
        foreach (var kv in store.MuxTabHistory.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rec = kv.Value;
            var current = rec.Current is not null && ids.Contains(rec.Current.Id);
            var inHistory = rec.History.Any(h => ids.Contains(h.Id));
            if (!current && !inHistory) continue;
            store.MuxTabMeta.TryGetValue(kv.Key, out var meta);
            var lastSeen = current ? rec.Current?.At ?? "" : rec.History.FirstOrDefault(h => ids.Contains(h.Id))?.At ?? "";
            tabs.Add(new SessionIntegrityMuxTab(kv.Key, current, meta?.Kind ?? "", meta?.Color ?? "", lastSeen, rec.History.Count));
        }
        return tabs;
    }

    private static bool SameTool(string a, string b)
        => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
    {
        a = NormalizePath(a);
        b = NormalizePath(b);
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return "";
        try { path = Path.GetFullPath(path); } catch { }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string WorkspaceLabel(string? workspace)
    {
        workspace = NormalizePath(workspace);
        if (workspace.Length == 0) return "";
        var name = Path.GetFileName(workspace);
        return string.IsNullOrWhiteSpace(name) ? workspace : name;
    }

    private static bool IsIncident(string kind, string severity)
        => string.Equals(severity, "warn", StringComparison.OrdinalIgnoreCase)
           || string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase)
           || kind.Contains(".refused", StringComparison.OrdinalIgnoreCase)
           || kind.Contains(".failed", StringComparison.OrdinalIgnoreCase);

    private static string Headline(string severity, IReadOnlyList<SessionIntegrityCheck> checks)
    {
        if (severity == "danger") return checks.First(c => c.Severity == "danger").Summary;
        if (severity == "warn") return checks.First(c => c.Severity == "warn").Summary;
        return "No integrity risks detected for this chat.";
    }
}
