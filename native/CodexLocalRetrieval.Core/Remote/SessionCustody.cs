using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record CustodyOverviewCheck(string Name, string Severity, string Summary);

public sealed record CustodyOverviewItem(
    string SessionId,
    string Title,
    string Tool,
    string Workspace,
    string Severity,
    string Reason,
    string NextAction,
    IReadOnlyList<string> Signals);

public sealed record CustodyOverview
{
    public string Severity { get; init; } = "ok";
    public string Headline { get; init; } = "";
    public string GeneratedAt { get; init; } = "";
    public bool LiveVerified { get; init; }
    public string LiveStatus { get; init; } = "";
    public int TotalSessions { get; init; }
    public int DangerCount { get; init; }
    public int WarnCount { get; init; }
    public int LiveOwnerCount { get; init; }
    public int ActiveClaimCount { get; init; }
    public int ExpiredClaimCount { get; init; }
    public int MissingSourceCount { get; init; }
    public int CurrentMuxCount { get; init; }
    public int PendingFilingCount { get; init; }
    public int RecentIncidentCount { get; init; }
    public IReadOnlyList<CustodyOverviewCheck> Checks { get; init; } = Array.Empty<CustodyOverviewCheck>();
    public IReadOnlyList<CustodyOverviewItem> Items { get; init; } = Array.Empty<CustodyOverviewItem>();
}

public static class SessionCustody
{
    public sealed record Options(
        DateTimeOffset? Now = null,
        string? EventRootDirectory = null,
        string? ClaimRootDirectory = null,
        Func<(bool Verified, HashSet<string> LiveIds, string Detail)>? LiveIdsProvider = null,
        Func<string, bool>? FileExists = null,
        int MaxItems = 120,
        int RecentEventLimit = 300)
    {
        public DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;
    }

    public static CustodyOverview BuildOverview(AppStoreData store, Options? options = null)
    {
        options ??= new Options();
        var now = options.EffectiveNow;
        var checks = new List<CustodyOverviewCheck>();
        var items = new List<CustodyOverviewItem>();
        var fileExists = options.FileExists ?? File.Exists;

        var live = (options.LiveIdsProvider ?? DefaultLiveIds)();
        checks.Add(live.Verified
            ? new CustodyOverviewCheck("Live-owner scan", "ok", "Live agent owners were verified.")
            : new CustodyOverviewCheck("Live-owner scan", "danger", "Live agent owners could not be verified; session launches must fail closed."));

        var recentEvents = SessionEventLedger
            .ReadRecent(Math.Clamp(options.RecentEventLimit, 1, 2000), new SessionEventLedger.Options(options.EventRootDirectory, now))
            .Where(IsIncident)
            .ToList();
        var eventsById = GroupEventsById(recentEvents);

        var pendingByWorkspace = store.PendingNewChats
            .GroupBy(p => PendingKey(p.Tool, p.Cwd), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var claimOptions = new SessionLaunchClaims.Options(options.ClaimRootDirectory, Now: now);
        var liveOwnerCount = 0;
        var activeClaimCount = 0;
        var expiredClaimCount = 0;
        var missingSourceCount = 0;
        var currentMuxCount = 0;
        var pendingFilingCount = 0;
        var recentIncidentCount = 0;

        foreach (var session in store.Sessions.Values.OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal))
        {
            var ids = CandidateIds(session);
            if (ids.Count == 0) continue;
            var signals = new List<string>();
            var severity = "ok";
            var reason = "";
            var nextAction = "";

            var liveMatches = live.Verified
                ? live.LiveIds.Where(id => ids.Contains(id)).ToList()
                : [];
            if (liveMatches.Count > 0)
            {
                liveOwnerCount++;
                Danger("live owner", "This chat already has a live owner.", "Attach to the existing owner instead of starting another writer.");
            }

            var claims = SessionLaunchClaims.ReadClaimsForSession(session.Id, session.Aliases, claimOptions);
            var activeClaims = claims.Count(c => !c.IsExpired(now));
            var expiredClaims = claims.Count - activeClaims;
            if (activeClaims > 0)
            {
                activeClaimCount += activeClaims;
                Danger("active launch claim", "A launch reservation is still active.", "Wait for the reservation to expire or attach to the started session.");
            }
            else if (expiredClaims > 0)
            {
                expiredClaimCount += expiredClaims;
                Warn("expired launch claim", "Expired launch reservation files are still present.", "Review the stale claim before retrying a launch.");
            }

            if (!SourceExists(session.SourcePath, fileExists))
            {
                missingSourceCount++;
                Danger("missing source", "The source transcript or rollout file is missing.", "Recover the source file or treat this archive entry as stale.");
            }

            var mux = MuxSignal(store, ids);
            if (mux.Current)
            {
                currentMuxCount++;
                Danger("current mux owner", "This chat is already current in a mux tab.", "Open the mux tab instead of starting another writer.");
            }
            else if (mux.History)
            {
                signals.Add("mux history");
            }

            var pendingKey = PendingKey(session.Tool, session.Workspace);
            if (pendingKey.Length > 1 && pendingByWorkspace.TryGetValue(pendingKey, out var pending) && pending.Count > 0)
            {
                pendingFilingCount += pending.Count;
                Warn("pending filing", "A new-chat filing intent is waiting in this workspace.", "Run Sync sessions to reconcile the pending filing.");
            }

            var incidentMatches = ids
                .Where(eventsById.ContainsKey)
                .SelectMany(id => eventsById[id])
                .GroupBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            if (incidentMatches.Count > 0)
            {
                recentIncidentCount += incidentMatches.Count;
                var hasError = incidentMatches.Any(e => string.Equals(e.Severity, "error", StringComparison.OrdinalIgnoreCase));
                if (hasError) Danger("recent failure", "Recent ownership events include a failed operation.", "Open the session integrity details before retrying.");
                else Warn("recent refusal", "Recent ownership events include a refused operation.", "Review the refusal and use the safe recovery route.");
            }

            if (severity == "ok" || items.Count >= Math.Max(1, options.MaxItems)) continue;
            items.Add(new CustodyOverviewItem(
                session.Id,
                CleanLabel(session.DisplayTitle, 140),
                CleanLabel(session.Tool, 24),
                WorkspaceLabel(session.WorkspaceName, session.Workspace),
                severity,
                reason,
                nextAction,
                signals.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));

            void Danger(string signal, string itemReason, string action)
            {
                signals.Add(signal);
                if (severity != "danger")
                {
                    severity = "danger";
                    reason = itemReason;
                    nextAction = action;
                }
            }

            void Warn(string signal, string itemReason, string action)
            {
                signals.Add(signal);
                if (severity == "ok")
                {
                    severity = "warn";
                    reason = itemReason;
                    nextAction = action;
                }
            }
        }

        if (liveOwnerCount > 0) checks.Add(new CustodyOverviewCheck("Live owners", "danger", liveOwnerCount + " chat" + Plural(liveOwnerCount) + " already have a live owner."));
        if (activeClaimCount > 0) checks.Add(new CustodyOverviewCheck("Launch claims", "danger", activeClaimCount + " active launch reservation" + Plural(activeClaimCount) + " found."));
        if (currentMuxCount > 0) checks.Add(new CustodyOverviewCheck("Mux custody", "danger", currentMuxCount + " chat" + Plural(currentMuxCount) + " are current in mux tabs."));
        if (missingSourceCount > 0) checks.Add(new CustodyOverviewCheck("Source files", "danger", missingSourceCount + " archived chat" + Plural(missingSourceCount) + " are missing source files."));
        if (expiredClaimCount > 0) checks.Add(new CustodyOverviewCheck("Expired claims", "warn", expiredClaimCount + " expired launch reservation" + Plural(expiredClaimCount) + " remain on disk."));
        if (pendingFilingCount > 0) checks.Add(new CustodyOverviewCheck("Pending filings", "warn", pendingFilingCount + " pending new-chat filing intent" + Plural(pendingFilingCount) + " found."));
        if (recentIncidentCount > 0) checks.Add(new CustodyOverviewCheck("Recent events", "warn", recentIncidentCount + " recent refused or failed ownership event" + Plural(recentIncidentCount) + " found."));

        var dangerCount = items.Count(i => i.Severity == "danger") + (live.Verified ? 0 : 1);
        var warnCount = items.Count(i => i.Severity == "warn");
        var severityOverall = dangerCount > 0 ? "danger" : warnCount > 0 ? "warn" : "ok";
        return new CustodyOverview
        {
            Severity = severityOverall,
            Headline = Headline(severityOverall, dangerCount, warnCount, live.Verified),
            GeneratedAt = now.UtcDateTime.ToString("O"),
            LiveVerified = live.Verified,
            LiveStatus = live.Verified ? "verified" : "unverified",
            TotalSessions = store.Sessions.Count,
            DangerCount = dangerCount,
            WarnCount = warnCount,
            LiveOwnerCount = liveOwnerCount,
            ActiveClaimCount = activeClaimCount,
            ExpiredClaimCount = expiredClaimCount,
            MissingSourceCount = missingSourceCount,
            CurrentMuxCount = currentMuxCount,
            PendingFilingCount = pendingFilingCount,
            RecentIncidentCount = recentIncidentCount,
            Checks = checks,
            Items = items
                .OrderBy(i => i.Severity == "danger" ? 0 : 1)
                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                .ToList()
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
        Add(session.Id);
        foreach (var alias in session.Aliases) Add(alias);
        return ids;

        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0) ids.Add(id);
        }
    }

    private static bool SourceExists(string? path, Func<string, bool> fileExists)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return false;
        try { return fileExists(path); }
        catch { return false; }
    }

    private static (bool Current, bool History) MuxSignal(AppStoreData store, HashSet<string> ids)
    {
        var history = false;
        foreach (var rec in store.MuxTabHistory.Values)
        {
            if (rec.Current is not null && ids.Contains(rec.Current.Id)) return (true, true);
            if (rec.History.Any(h => ids.Contains(h.Id))) history = true;
        }
        return (false, history);
    }

    private static Dictionary<string, List<SessionEvent>> GroupEventsById(IEnumerable<SessionEvent> events)
    {
        var map = new Dictionary<string, List<SessionEvent>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in events)
        {
            foreach (var id in EventIds(ev))
            {
                if (!map.TryGetValue(id, out var list)) map[id] = list = new List<SessionEvent>();
                list.Add(ev);
            }
        }
        return map;
    }

    private static IEnumerable<string> EventIds(SessionEvent ev)
    {
        void Add(string? id, List<string> output)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0 && !output.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) output.Add(id);
        }

        var ids = new List<string>();
        Add(ev.SessionId, ids);
        foreach (var id in ev.SessionIds ?? []) Add(id, ids);
        foreach (var value in ev.Details.Values)
        {
            Add(value, ids);
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Add(part, ids);
        }
        return ids;
    }

    private static bool IsIncident(SessionEvent ev)
        => string.Equals(ev.Severity, "warn", StringComparison.OrdinalIgnoreCase)
           || string.Equals(ev.Severity, "error", StringComparison.OrdinalIgnoreCase)
           || ev.Kind.Contains(".refused", StringComparison.OrdinalIgnoreCase)
           || ev.Kind.Contains(".failed", StringComparison.OrdinalIgnoreCase);

    private static string PendingKey(string? tool, string? workspace)
    {
        var path = NormalizePath(workspace);
        return (tool ?? "").Trim().ToLowerInvariant() + "|" + path.ToLowerInvariant();
    }

    private static string NormalizePath(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return "";
        try { path = Path.GetFullPath(path); } catch { }
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string WorkspaceLabel(string? workspaceName, string? workspace)
    {
        var label = CleanLabel(workspaceName, 80);
        if (label.Length > 0) return label;
        workspace = NormalizePath(workspace);
        if (workspace.Length == 0) return "";
        var name = Path.GetFileName(workspace);
        return CleanLabel(string.IsNullOrWhiteSpace(name) ? "workspace" : name, 80);
    }

    private static string CleanLabel(string? value, int max)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return "";
        value = SecretRedactor.Scrub(value);
        value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        while (value.Contains("  ", StringComparison.Ordinal)) value = value.Replace("  ", " ");
        if (value.Length > max) value = value[..max];
        return value.Trim();
    }

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Headline(string severity, int dangerCount, int warnCount, bool liveVerified)
    {
        if (!liveVerified) return "Live-owner verification is unavailable; unsafe launch actions must stay blocked.";
        if (severity == "danger") return dangerCount + " custody risk" + Plural(dangerCount) + " need action before launching.";
        if (severity == "warn") return warnCount + " custody warning" + Plural(warnCount) + " should be reviewed.";
        return "No custody risks detected.";
    }
}
