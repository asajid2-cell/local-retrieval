using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record DiscoveryQuery(
    string? Q = null,
    string? Include = null,
    string? Exclude = null,
    string? Match = null,
    string? Agent = null,
    string? Date = null,
    int? MinUserMessages = null,
    bool? ShowHidden = null,
    string? Project = null,
    string? Sort = null,
    int? Offset = null,
    int? Limit = null);

public sealed record DiscoveryChatRow(
    string Id,
    string Title,
    string Tool,
    string WorkspaceLabel,
    string UpdatedAt,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Phrases,
    int UserMsgCount,
    bool Pinned,
    bool Resumable,
    string MuxName);

public sealed record DiscoveryPage(
    string Query,
    int Offset,
    int Limit,
    int Total,
    bool HasMore,
    IReadOnlyList<DiscoveryChatRow> Rows);

public sealed record DiscoveryFacet(string Value, int Count);
public sealed record DiscoveryProjectFacet(string Id, string Label, int Count);
public sealed record DiscoveryFacets(
    int Total,
    int Hidden,
    IReadOnlyList<DiscoveryFacet> Tags,
    IReadOnlyList<DiscoveryFacet> Phrases,
    IReadOnlyList<DiscoveryProjectFacet> Projects);

// HTTP-agnostic discovery surface. The server adapter binds query parameters onto DiscoveryQuery;
// all archive semantics and response shaping stay here so they can be tested without a web host.
public sealed class DiscoveryApi
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 100;

    private static readonly HashSet<int> AllowedMinimums = new() { 0, 2, 3, 5, 10, 25 };
    private static readonly HashSet<string> AllowedAgents = new(StringComparer.OrdinalIgnoreCase) { "codex", "claude" };
    private static readonly HashSet<string> AllowedDates = new(StringComparer.OrdinalIgnoreCase) { "today", "week", "month" };
    private static readonly HashSet<string> AllowedSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        "recent", "created-newest", "created-oldest", "last-user", "first-user",
    };

    private readonly ArchiveService _archive;
    private readonly Func<ArchiveSession, bool> _isResumable;

    public DiscoveryApi(ArchiveService archive, Func<ArchiveSession, bool>? isResumable = null)
    {
        _archive = archive;
        _isResumable = isResumable ?? archive.CanBuildTrustedResumeLaunch;
    }

    public DiscoveryPage Chats(DiscoveryQuery? query = null)
    {
        var normalized = Normalize(query);
        var sessions = Filter(normalized);
        var total = sessions.Count;
        var rows = sessions
            .Skip(normalized.Offset)
            .Take(normalized.Limit)
            .Select(session => ToRow(session, normalized.Sort))
            .ToList();

        return new DiscoveryPage(
            normalized.Q,
            normalized.Offset,
            normalized.Limit,
            total,
            normalized.Offset + rows.Count < total,
            rows);
    }

    public DiscoveryFacets Facets(DiscoveryQuery? query = null)
    {
        var normalized = Normalize(query);
        var sessions = Filter(normalized);
        var ids = new HashSet<string>(sessions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);

        var tags = CountValues(sessions.SelectMany(ArchiveService.UserTags));
        var phrases = CountValues(sessions.SelectMany(s => s.SpecialPhrases));
        var projects = _archive.Store.Collections.Values
            .Select(collection => new DiscoveryProjectFacet(
                collection.Id,
                collection.Name,
                collection.SessionIds.Count(ids.Contains)))
            .Where(project => project.Count > 0)
            .OrderByDescending(project => project.Count)
            .ThenBy(project => project.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DiscoveryFacets(
            sessions.Count,
            _archive.HiddenChatCount(),
            tags,
            phrases,
            projects);
    }

    private IReadOnlyList<ArchiveSession> Filter(NormalizedQuery query)
    {
        if (query.Project.Length > 0 && !_archive.Store.Collections.ContainsKey(query.Project))
            return Array.Empty<ArchiveSession>();

        var filter = new ChatFilter
        {
            Query = query.Q,
            IncludeTags = query.IncludeTags,
            ExcludeTags = query.ExcludeTags,
            MatchAllIncludes = query.MatchAll,
            CollectionId = query.Project,
            Tool = query.Agent,
            DateRange = query.Date,
            DateMode = query.Sort == "recent" ? "" : query.Sort,
            MinUserMessages = query.MinUserMessages,
            ShowHidden = query.ShowHidden,
        };

        var filtered = _archive.FilterChats(filter);
        return StableOrder(filtered, query);
    }

    private DiscoveryChatRow ToRow(ArchiveSession session, string sort) => new(
        session.Id,
        SecretRedactor.Scrub(RowTitle(session, sort)),
        NormalizeTool(session.Tool),
        SecretRedactor.Scrub(WorkspaceLabel(session)),
        session.UpdatedAt,
        ArchiveService.UserTags(session)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        session.SpecialPhrases
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(phrase => phrase, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        session.UserMessageCount,
        session.Pinned,
        _isResumable(session),
        ArchiveService.MultiplexSessionName(session));

    private static string RowTitle(ArchiveSession session, string sort) => sort switch
    {
        "last-user" => string.IsNullOrWhiteSpace(session.LastUserMessage)
            ? "No messages - " + session.DisplayTitle
            : session.LastUserMessage,
        "first-user" => string.IsNullOrWhiteSpace(session.FirstUserMessage)
            ? session.DisplayTitle
            : session.FirstUserMessage,
        _ => session.DisplayTitle,
    };

    private static IReadOnlyList<ArchiveSession> StableOrder(
        IReadOnlyList<ArchiveSession> sessions,
        NormalizedQuery query)
    {
        // Search() already supplies relevance ordering. Preserve it and use the session id only as a
        // deterministic fallback for rows that entered with the same position impossible to distinguish.
        if (!string.IsNullOrWhiteSpace(query.Q) && query.Sort == "recent")
            return sessions.Select((session, index) => (session, index))
                .OrderBy(x => x.index)
                .ThenBy(x => x.session.Id, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.session)
                .ToList();

        static DateTimeOffset Created(ArchiveSession session) =>
            DateTimeOffset.TryParse(
                session.CreatedAt,
                null,
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var value)
                ? value
                : DateTimeOffset.MinValue;

        IOrderedEnumerable<ArchiveSession> ordered = query.Sort switch
        {
            "created-newest" => sessions
                .OrderByDescending(Created)
                .ThenBy(session => session.Id, StringComparer.OrdinalIgnoreCase),
            "created-oldest" => sessions
                .OrderBy(session => Created(session) == DateTimeOffset.MinValue ? DateTimeOffset.MaxValue : Created(session))
                .ThenBy(session => session.Id, StringComparer.OrdinalIgnoreCase),
            _ => sessions
                .OrderByDescending(session => session.Pinned)
                .ThenByDescending(session => session.UpdatedAt, StringComparer.Ordinal)
                .ThenBy(session => session.Id, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ToList();
    }

    private static IReadOnlyList<DiscoveryFacet> CountValues(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DiscoveryFacet(group.Key, group.Count()))
            .OrderByDescending(facet => facet.Count)
            .ThenBy(facet => facet.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static NormalizedQuery Normalize(DiscoveryQuery? raw)
    {
        raw ??= new DiscoveryQuery();
        var agent = CleanEnum(raw.Agent, AllowedAgents);
        var date = CleanEnum(raw.Date, AllowedDates);
        var sort = CleanEnum(raw.Sort, AllowedSorts);
        if (sort.Length == 0) sort = "recent";

        var minimum = raw.MinUserMessages.GetValueOrDefault();
        if (!AllowedMinimums.Contains(minimum)) minimum = 0;

        return new NormalizedQuery(
            (raw.Q ?? "").Trim(),
            ParseList(raw.Include),
            ParseList(raw.Exclude),
            string.Equals(raw.Match, "all", StringComparison.OrdinalIgnoreCase),
            agent,
            date,
            minimum,
            raw.ShowHidden == true,
            (raw.Project ?? "").Trim(),
            sort,
            Math.Max(0, raw.Offset.GetValueOrDefault()),
            Math.Clamp(raw.Limit ?? DefaultLimit, 1, MaxLimit));
    }

    private static string CleanEnum(string? value, HashSet<string> allowed)
    {
        var clean = (value ?? "").Trim().ToLowerInvariant();
        return allowed.Contains(clean) ? clean : "";
    }

    private static List<string> ParseList(string? csv) =>
        (csv ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeTool(string? tool)
    {
        var normalized = (tool ?? "").Trim().ToLowerInvariant();
        return AllowedAgents.Contains(normalized) ? normalized : "";
    }

    private static string WorkspaceLabel(ArchiveSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.WorkspaceName)) return session.WorkspaceName.Trim();
        try
        {
            var workspace = (session.Workspace ?? "").TrimEnd('\\', '/');
            return Path.GetFileName(workspace);
        }
        catch
        {
            return "";
        }
    }

    private sealed record NormalizedQuery(
        string Q,
        List<string> IncludeTags,
        List<string> ExcludeTags,
        bool MatchAll,
        string Agent,
        string Date,
        int MinUserMessages,
        bool ShowHidden,
        string Project,
        string Sort,
        int Offset,
        int Limit);
}
