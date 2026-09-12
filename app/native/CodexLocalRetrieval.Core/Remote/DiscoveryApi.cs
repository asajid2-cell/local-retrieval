using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using System.Security.Cryptography;
using System.Text;

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
    string? Archived = null,
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
    string MuxName,
    string Snippet = "",
    long MatchOffset = 0,
    long MatchLength = 0,
    string MatchProvenance = "",
    bool Navigable = true,
    string Revision = "",
    bool Archived = false,
    string CustomTitle = "",
    IReadOnlyList<string>? CollectionIds = null);

public sealed record DiscoveryMutationResult(bool Ok, string Message, string Id, bool Favorite, string Revision);

public sealed record DiscoveryPage(
    string Query,
    int Offset,
    int Limit,
    int Total,
    bool HasMore,
    IReadOnlyList<DiscoveryChatRow> Rows,
    SearchCoverage? Coverage = null);

public sealed record DiscoveryFacet(string Value, int Count);
public sealed record DiscoveryProjectFacet(string Id, string Label, int Count);
public sealed record DiscoveryFacets(
    int Total,
    int Hidden,
    IReadOnlyList<DiscoveryFacet> Tags,
    IReadOnlyList<DiscoveryFacet> Phrases,
    IReadOnlyList<DiscoveryProjectFacet> Projects,
    SearchCoverage? Coverage = null);

public sealed record StartDeckRow(string Id, string Label);
public sealed record StartDeckProjection(string ActiveDeckId, IReadOnlyList<StartDeckRow> Rows);
public sealed record StartCollectionRow(string Id, string Label, string Revision = "", string ManagementRevision = "");
public sealed record StartCollectionProjection(string DeckId, IReadOnlyList<StartCollectionRow> Rows, IReadOnlyList<StartDeletedCollectionRow>? RecentlyDeleted = null);
public sealed record StartDeletedCollectionRow(string Id, string Label, string DeckId, string DeletedAt, string Revision);
public sealed record ContainerDeckRow(string Id, string Label, string Revision);
public sealed record ContainerCollectionRow(string Id, string Label, string DeckId, string DeckLabel, string Revision, IReadOnlyList<string> SessionIds, IReadOnlyList<string>? Tags = null);
public sealed record ContainerAdminProjection(IReadOnlyList<ContainerDeckRow> Decks, IReadOnlyList<ContainerCollectionRow> Collections, IReadOnlyList<StartDeletedCollectionRow> RecentlyDeleted, string ActiveDeckId, string RecentlyDeletedManagementRevision = "", string DeckOrderRevision = "");
public sealed record StartCheckpointRow(
    string Id,
    string Label,
    string SourceTitle,
    string Tool,
    string WorkspaceLabel,
    string CreatedAt,
    int MessageCount,
    string Revision = "",
    string SourceSessionId = "");
public sealed record StartCheckpointProjection(IReadOnlyList<StartCheckpointRow> Rows);
public sealed record StartWorkspaceRow(string Id, string Label, IReadOnlyList<string> Tools);
public sealed record StartWorkspaceProjection(IReadOnlyList<StartWorkspaceRow> Rows);

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
            rows,
            _archive.LastSearchCoverage);
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
            projects,
            _archive.LastSearchCoverage);
    }

    public StartDeckProjection StartDecks()
    {
        _archive.EnsureDecks();
        var rows = _archive.Decks
            .Where(deck => !string.IsNullOrWhiteSpace(deck.Id))
            .Select(deck => new StartDeckRow(deck.Id.Trim(), deck.Name.Trim()))
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var active = rows.FirstOrDefault(row =>
            string.Equals(row.Id, _archive.ActiveDeckId, StringComparison.OrdinalIgnoreCase));
        var main = rows.FirstOrDefault(row =>
            string.Equals(row.Id, ArchiveService.MainDeckId, StringComparison.OrdinalIgnoreCase));
        return new StartDeckProjection(active?.Id ?? main?.Id ?? ArchiveService.MainDeckId, rows);
    }

    public StartCollectionProjection StartCollections(string deckId)
    {
        _archive.EnsureDecks();
        var requestedId = (deckId ?? "").Trim();
        var deck = _archive.Decks.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requestedId, StringComparison.OrdinalIgnoreCase));
        if (deck is null)
            return new StartCollectionProjection(
                requestedId,
                Array.Empty<StartCollectionRow>());

        var rows = _archive.CollectionsInDeck(deck.Id)
            .Where(collection => !string.IsNullOrWhiteSpace(collection.Id))
            .Select(collection => new StartCollectionRow(collection.Id.Trim(), collection.Name.Trim(), _archive.CollectionMembershipRevision(collection.Id), _archive.CollectionManagementRevision(collection.Id)))
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deleted = _archive.Store.DeletedCollections
            .Select(entry => new StartDeletedCollectionRow(entry.Collection.Id, entry.Collection.Name, ArchiveService.CollectionDeck(entry.Collection),
                entry.DeletedAt, _archive.DeletedCollectionManagementRevision(entry.Collection.Id)))
            .ToList();
        return new StartCollectionProjection(requestedId, rows, deleted);
    }

    public ContainerAdminProjection ContainerAdmin()
    {
        _archive.EnsureDecks();
        var decks = _archive.Decks.Select(deck => new ContainerDeckRow(deck.Id, deck.Name, _archive.DeckManagementRevision(deck.Id))).ToList();
        var collections = _archive.Store.Collections.Values.Select(collection => new ContainerCollectionRow(
            collection.Id, collection.Name, ArchiveService.CollectionDeck(collection),
            _archive.Decks.FirstOrDefault(deck => deck.Id.Equals(ArchiveService.CollectionDeck(collection), StringComparison.OrdinalIgnoreCase))?.Name ?? "Main",
            _archive.CollectionManagementRevision(collection.Id), collection.SessionIds.ToList(), collection.Tags.ToList())).ToList();
        var deleted = _archive.Store.DeletedCollections.Select(entry => new StartDeletedCollectionRow(entry.Collection.Id, entry.Collection.Name,
            ArchiveService.CollectionDeck(entry.Collection), entry.DeletedAt, _archive.DeletedCollectionManagementRevision(entry.Collection.Id))).ToList();
        return new ContainerAdminProjection(decks, collections, deleted, _archive.ActiveDeckId, _archive.RecentlyDeletedManagementRevision(), _archive.DeckOrderRevision());
    }

    public StartCheckpointProjection StartCheckpoints()
    {
        var rows = _archive.Templates()
            .Select(snapshot => new StartCheckpointRow(
                snapshot.Id,
                SecretRedactor.Scrub(ArchiveService.TemplateSnapshotDisplayLabel(snapshot)),
                SecretRedactor.Scrub((snapshot.SourceTitle ?? "").Trim()),
                NormalizeTool(snapshot.Tool),
                SecretRedactor.Scrub(WorkspaceLabel(snapshot.WorkspaceName, snapshot.Workspace)),
                snapshot.CreatedAt,
                snapshot.MessageCount,
                _archive.TemplateSnapshotManagementRevision(snapshot.Id),
                snapshot.SourceSessionId))
            .ToList();
        return new StartCheckpointProjection(rows);
    }

    public StartWorkspaceProjection StartWorkspaces()
    {
        var rows = BuildWorkspaceRegistry()
            .Select(workspace => new StartWorkspaceRow(workspace.Id, workspace.Label, workspace.Tools))
            .ToList();
        return new StartWorkspaceProjection(rows);
    }

    public bool TryResolveWorkspace(string workspaceId, out string path)
    {
        path = "";
        var requestedId = (workspaceId ?? "").Trim();
        if (requestedId.Length == 0) return false;

        var workspace = BuildWorkspaceRegistry().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, requestedId, StringComparison.OrdinalIgnoreCase));
        if (workspace is null || !Directory.Exists(workspace.Path)) return false;
        path = workspace.Path;
        return true;
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
            Archived = query.Archived,
        };

        var filtered = _archive.FilterChats(filter);
        return StableOrder(filtered, query);
    }

    private DiscoveryChatRow ToRow(ArchiveSession session, string sort)
    {
        var hit = _archive.LastSearchHit(session.Id);
        return new DiscoveryChatRow(
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
            ArchiveService.MultiplexSessionName(session),
            SecretRedactor.Scrub(hit?.Snippet ?? ""),
            hit?.ByteOffset ?? 0,
            hit?.ByteLength ?? 0,
            hit?.Provenance ?? "",
            hit?.Navigable ?? File.Exists(session.SourcePath),
            _archive.RemoteManagementRevision(session),
            session.Archived,
            SecretRedactor.Scrub(session.CustomTitle),
            _archive.CollectionIdsForSession(session.Id));
    }

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
        var archived = CleanEnum(raw.Archived, new(StringComparer.OrdinalIgnoreCase) { "active", "archived", "all" });
        if (archived.Length == 0) archived = "active";

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
            archived,
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
        => WorkspaceLabel(session.WorkspaceName, session.Workspace);

    private static string WorkspaceLabel(string? workspaceName, string? workspace)
    {
        if (!string.IsNullOrWhiteSpace(workspaceName)) return workspaceName.Trim();
        try
        {
            var normalized = (workspace ?? "").TrimEnd('\\', '/');
            return Path.GetFileName(normalized);
        }
        catch
        {
            return "";
        }
    }

    private IReadOnlyList<WorkspaceRegistryEntry> BuildWorkspaceRegistry()
    {
        var entries = new Dictionary<string, WorkspaceAccumulator>(StringComparer.OrdinalIgnoreCase);

        AddWorkspace(entries, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), null);
        foreach (var session in _archive.Store.Sessions.Values)
            AddWorkspace(entries, session.Workspace, session.Tool);

        return entries.Values
            .Select(entry => new WorkspaceRegistryEntry(
                WorkspaceId(entry.Path),
                entry.Path,
                WorkspaceDisplayLabel(entry.Path),
                entry.Tools
                    .OrderBy(tool => tool, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddWorkspace(
        Dictionary<string, WorkspaceAccumulator> entries,
        string? path,
        string? tool)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string normalized;
        try { normalized = NormalizeWorkspacePath(path); }
        catch { return; }
        if (normalized.Length == 0 || !Directory.Exists(normalized)) return;

        if (!entries.TryGetValue(normalized, out var entry))
        {
            entry = new WorkspaceAccumulator(normalized);
            entries[normalized] = entry;
        }

        var normalizedTool = NormalizeTool(tool);
        if (normalizedTool.Length > 0) entry.Tools.Add(normalizedTool);
    }

    private static string NormalizeWorkspacePath(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return root;
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string WorkspaceId(string normalizedPath)
    {
        var identity = normalizedPath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return "ws-" + hash;
    }

    private static string WorkspaceDisplayLabel(string normalizedPath)
    {
        try
        {
            var withoutTrailingSeparator = normalizedPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(withoutTrailingSeparator);
            if (string.IsNullOrWhiteSpace(name)) name = "workspace";
            return SecretRedactor.Scrub(name);
        }
        catch
        {
            return "workspace";
        }
    }

    private sealed class WorkspaceAccumulator(string path)
    {
        public string Path { get; } = path;
        public HashSet<string> Tools { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record WorkspaceRegistryEntry(
        string Id,
        string Path,
        string Label,
        IReadOnlyList<string> Tools);

    private sealed record NormalizedQuery(
        string Q,
        List<string> IncludeTags,
        List<string> ExcludeTags,
        bool MatchAll,
        string Agent,
        string Date,
        int MinUserMessages,
        bool ShowHidden,
        string Archived,
        string Project,
        string Sort,
        int Offset,
        int Limit);
}
