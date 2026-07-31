using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Models;

public sealed class AppStoreData
{
    [JsonPropertyName("storeSchemaVersion")]
    public int StoreSchemaVersion { get; set; } = 1;

    [JsonPropertyName("generation")]
    public long Generation { get; set; }

    [JsonPropertyName("sessions")]
    public Dictionary<string, ArchiveSession> Sessions { get; set; } = new();

    [JsonPropertyName("templateSnapshots")]
    public Dictionary<string, TemplateSnapshot> TemplateSnapshots { get; set; } = new();

    [JsonPropertyName("settings")]
    public ArchiveSettings Settings { get; set; } = new();

    [JsonPropertyName("collections")]
    public Dictionary<string, ArchiveCollection> Collections { get; set; } = new();

    // Top-level decks (virtual desktops for collections). A "main" deck is ensured on load.
    [JsonPropertyName("decks")]
    public List<Deck> Decks { get; set; } = new();

    // Soft-deleted collections kept so an accidental delete can be undone (Recently Deleted).
    [JsonPropertyName("deletedCollections")]
    public List<DeletedCollection> DeletedCollections { get; set; } = new();

    // Incremental sync: source file path -> "mtimeTicks:size". Unchanged files are skipped on
    // re-scan so relaunches are fast. Deletions are never propagated (chats keep accumulating).
    [JsonPropertyName("fileStamps")]
    public Dictionary<string, string> FileStamps { get; set; } = new();

    // Per-tag-name color overrides (lowercased tag -> hex). Absent tags fall back to a deterministic
    // auto-color so every tag is colored with zero setup; an entry here is a user's explicit choice.
    [JsonPropertyName("tagColors")]
    public Dictionary<string, string> TagColors { get; set; } = new();

    // Layer rules (lowercased tag -> layer number, LOWER = HIGHER in the list). Chats in a collection
    // are grouped by their tags' layer, so e.g. `active`=1 floats to the top and `context`=900 sinks
    // to the bottom. Unlisted tags use the default layer. Sorting only - never hides anything.
    [JsonPropertyName("tagLayers")]
    public Dictionary<string, int> TagLayers { get; set; } = new();

    // "Start chat" can target a collection before the new chat's session id exists. We remember the
    // intent (tool + cwd + collection) and file the first matching new session into the collection on
    // the next index pass, then drop the entry. Expires so a never-started chat doesn't linger.
    [JsonPropertyName("pendingNewChats")]
    public List<PendingNewChat> PendingNewChats { get; set; } = new();

    // Per mux TAB (by name): the chat currently running in it + every chat that has run in it before, so
    // no session a tab ever hosted is lost. Maintained by the mux-tab resolver as tabs switch agents/chats;
    // powers "relaunch → pick which past session" and "add this tab (+ its history) to a collection".
    [JsonPropertyName("muxTabHistory")]
    public Dictionary<string, MuxTabRecord> MuxTabHistory { get; set; } = new();

    // Per mux TAB (by name) presentation: a user-chosen color (like a PowerShell tab color) and a kind
    // (e.g. "remote-resumed" for a session handed off from a local terminal via /tomux — tinted by default
    // so you can tell at a glance it's a resumed-remote). Projected to the web to tint the tab.
    [JsonPropertyName("muxTabMeta")]
    public Dictionary<string, MuxTabMeta> MuxTabMeta { get; set; } = new();
}

// One chat that has lived in a mux tab (current or historical).
public sealed class MuxTabChat
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("at")] public string At { get; set; } = "";   // ISO-8601 UTC, when last seen in the tab
}

// Per-tab presentation: a chosen color + a kind flag (e.g. remote-resumed) shown as a tint on the web.
public sealed class MuxTabMeta
{
    [JsonPropertyName("color")] public string Color { get; set; } = "";     // hex like "#e879f9", or "" for none
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";       // "" | "remote-resumed"
}

// A tab's session history: the current chat + previously-seen chats (most-recent first, current excluded).
public sealed class MuxTabRecord
{
    [JsonPropertyName("current")] public MuxTabChat? Current { get; set; }
    [JsonPropertyName("history")] public List<MuxTabChat> History { get; set; } = new();
    // When the app first saw this tab (ISO-8601 UTC). The folder-observation ledger only captures sessions
    // created AFTER this, so a tab shows what it hosted — not the cwd's whole back-catalog.
    [JsonPropertyName("firstSeen")] public string FirstSeen { get; set; } = "";
}

// A "Start chat" that should be filed into a collection once its session is indexed (matched by
// tool + working directory + creation time). See ArchiveService.ReconcilePendingNewChats.
public sealed class PendingNewChat
{
    [JsonPropertyName("intentId")]
    public string IntentId { get; set; } = "";

    [JsonPropertyName("cwd")]
    public string Cwd { get; set; } = "";          // the ORIGINAL launch cwd (used to find the tool's project folder)

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";         // "claude" | "codex"

    [JsonPropertyName("collectionId")]
    public string CollectionId { get; set; } = "";

    [JsonPropertyName("customTitle")]
    public string CustomTitle { get; set; } = "";

    [JsonPropertyName("specialPhrase")]
    public string SpecialPhrase { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";    // ISO-8601 UTC, when "Start chat" was launched

    // The Claude transcript ids that ALREADY existed in this cwd's project folder at launch time. The new
    // chat is whichever transcript appears in that folder afterwards (not in this set) — a precise identity
    // match that doesn't depend on parsing the workspace or on a shared cwd like the 301 root.
    [JsonPropertyName("knownIds")]
    public List<string> KnownIds { get; set; } = new();
}

// A place agent sessions are stored on disk. Defaults cover Codex + Claude; an agent or the user
// can add non-default roots so chats in unusual locations still surface.
public sealed class SessionSource
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "codex"; // codex | claude

    [JsonPropertyName("root")]
    public string Root { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ArchiveSettings
{
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "amoled";

    [JsonPropertyName("accent")]
    public string Accent { get; set; } = "rose";

    [JsonPropertyName("accentHex")]
    public string AccentHex { get; set; } = "#fb7185";

    [JsonPropertyName("radius")]
    public string Radius { get; set; } = "compact";

    [JsonPropertyName("density")]
    public string Density { get; set; } = "comfortable";

    [JsonPropertyName("readOnlySourceMode")]
    public bool ReadOnlySourceMode { get; set; } = true;

    [JsonPropertyName("bundledHistoryAbsorbed")]
    public bool BundledHistoryAbsorbed { get; set; }

    [JsonPropertyName("sources")]
    public List<SessionSource> Sources { get; set; } = new();

    // Bumped whenever the parser changes so the incremental file-stamp cache is invalidated and
    // every file is re-parsed once with the new logic.
    [JsonPropertyName("indexVersion")]
    public int IndexVersion { get; set; }

    [JsonPropertyName("panelRadius")]
    public int PanelRadius { get; set; } = 12;

    [JsonPropertyName("controlRadius")]
    public int ControlRadius { get; set; } = 12;

    [JsonPropertyName("activeAiProviderId")]
    public string ActiveAiProviderId { get; set; } = "deepseek";

    // The deck currently shown on the Collections screen (defaults to Main).
    [JsonPropertyName("activeDeckId")]
    public string ActiveDeckId { get; set; } = "main";

    [JsonPropertyName("aiProviders")]
    public List<AiProviderSettings> AiProviders { get; set; } = new();

    // Optional extra arguments inserted right AFTER the CLI exe (before the resume subcommand) when a
    // chat is resumed in a terminal — e.g. "--profile http_sse" to force Codex onto the stable HTTP/SSE
    // transport. Empty = the default launch is untouched. Per-tool because Codex/Claude differ.
    [JsonPropertyName("codexLaunchArgs")]
    public string CodexLaunchArgs { get; set; } = "";

    [JsonPropertyName("claudeLaunchArgs")]
    public string ClaudeLaunchArgs { get; set; } = "";

    // Remote (multiplex) session support. The PC-local muxd owns the terminal session and dials out to
    // the relay so harmonizerlabs.cc/multiplex can mirror it. MultiplexSshTarget is still used by the
    // app's owner-only command/projection bridge to the VPS loopback API.
    [JsonPropertyName("multiplexSshTarget")]
    public string MultiplexSshTarget { get; set; } = "harmonizer@192.168.1.142";

    [JsonPropertyName("multiplexApiPort")]
    public int MultiplexApiPort { get; set; } = 7682;
}

public sealed class AiProviderSettings
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "openai-compatible";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("models")]
    public List<string> Models { get; set; } = new();

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ArchiveCollection
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sessionIds")]
    public List<string> SessionIds { get; set; } = new();

    [JsonPropertyName("color")]
    public string Color { get; set; } = "#fb7185";

    // User labels to organize collections themselves (filterable on the Collections screen).
    // Part of the serialized store, so they ride along in every backup/export and survive restore.
    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    // Which deck this collection lives on (empty = the Main deck). Decks are top-level groupings of
    // collections - like virtual desktops - so you can keep e.g. active projects and context archives
    // on separate decks.
    [JsonPropertyName("deckId")]
    public string DeckId { get; set; } = "";
}

// A top-level grouping of collections (a "virtual desktop" for projects).
public sealed class Deck
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";
}

// A tag with how many chats/collections carry it - drives the filter strips (sorted by use).
public sealed record TagCount(string Tag, int Count);

// A compound chat filter: text query + tags you must have (include) + tags you must NOT have
// (exclude) + an optional project (collection) restriction. This is what lets you ask for
// "active chats that aren't cpp" (include=active, exclude=cpp).
public sealed class ChatFilter
{
    public string Query { get; set; } = "";
    public List<string> IncludeTags { get; set; } = new();
    public List<string> ExcludeTags { get; set; } = new();
    public bool MatchAllIncludes { get; set; }     // false = ANY include tag; true = must have ALL of them
    public string? CollectionId { get; set; }       // restrict to this collection's members

    // Restrict the list to one agent: "" = both, "codex", or "claude".
    public string Tool { get; set; } = "";

    // SORT of the list (single choice): "" = recent activity (touched last) | created-newest |
    // created-oldest | last-user (order by recency, title = last user message) | first-user (title =
    // first user message). Combines freely with DateRange, Tool, tags, and collection.
    public string DateMode { get; set; } = "";

    // DATE-RANGE filter (single choice, COMBINES with the sort): "" = any | today | week | month.
    // Restricts to chats CREATED within the range; the chosen sort still orders what's left.
    public string DateRange { get; set; } = "";

    // Keep only chats with at LEAST this many real user prompts (0 = off). "Filter out chats with <5"
    // = 5; "hide one-off chats" = 2. Combines with everything else.
    public int MinUserMessages { get; set; }

    // By default the list auto-hides "one-off" chats — a single user prompt and a tiny transcript
    // (the hundreds of spawned judge/probe sessions). Set true to reveal them everywhere (list,
    // search, scroll). This is a visibility toggle, NOT a narrowing filter, so it's excluded from
    // IsEmpty (turning it on doesn't count as "filtering").
    public bool ShowHidden { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Query) && IncludeTags.Count == 0
        && ExcludeTags.Count == 0 && string.IsNullOrEmpty(CollectionId) && string.IsNullOrEmpty(DateMode)
        && string.IsNullOrEmpty(DateRange) && string.IsNullOrEmpty(Tool) && MinUserMessages <= 0;
}

// A collection moved to "Recently Deleted" - the full grouping plus when it was removed, so it can
// be restored exactly as it was. Chats themselves are never deleted; this is metadata only.
public sealed class DeletedCollection
{
    [JsonPropertyName("collection")]
    public ArchiveCollection Collection { get; set; } = new();

    [JsonPropertyName("deletedAt")]
    public string DeletedAt { get; set; } = "";
}

public sealed class TemplateSnapshot
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("sourceSessionId")]
    public string SourceSessionId { get; set; } = "";

    [JsonPropertyName("sourceTitle")]
    public string SourceTitle { get; set; } = "";

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = "";

    [JsonPropertyName("snapshotPath")]
    public string SnapshotPath { get; set; } = "";

    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "";

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

    [JsonPropertyName("workspaceName")]
    public string WorkspaceName { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    [JsonPropertyName("capturedSourceLength")]
    public long CapturedSourceLength { get; set; }

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonPropertyName("lineCount")]
    public int LineCount { get; set; }

    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? SourceTitle : Name;
}

// The shape of an exported/downloaded collections backup - lightweight metadata only (no chat
// content), so it stays tiny and portable and can rebuild your projects on any machine.
public sealed class CollectionsBackup
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "codex-local-retrieval/collections";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("exportedAt")]
    public string ExportedAt { get; set; } = "";

    [JsonPropertyName("collections")]
    public List<ArchiveCollection> Collections { get; set; } = new();
}

public sealed class ArchiveSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public ArchiveSession()
    {
        // The two ObservableCollections that feed SearchText are wired here as well as in their setters,
        // because a field initializer bypasses the setter.
        _tags.CollectionChanged += OnSearchFieldCollectionChanged;
        _specialPhrases.CollectionChanged += OnSearchFieldCollectionChanged;
    }

    // ------------------------------------------------------------------ search-text cache
    //
    // SearchText composes a ~3 KB string out of eight fields, and the filter pass asks for it once per
    // session PER TERM — a two-word query over 4000 chats composed 8000 of them and allocated ~20 MB
    // before a single character was compared. The composition is cached here instead.
    //
    // Invalidation is STRUCTURAL, not by convention: every contributing field is either a property whose
    // setter clears the cache, or an ObservableCollection whose CollectionChanged clears it (rewired when
    // the collection instance itself is replaced, e.g. by the JSON deserializer). No call site anywhere in
    // the app has to remember to invalidate, which is the only way a cache like this stays correct.
    private string? _searchTextCache;

    private void InvalidateSearchText() => _searchTextCache = null;

    private void OnSearchFieldCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateSearchText();

    private ObservableCollection<string> WatchForSearchText(ObservableCollection<string> old, ObservableCollection<string> next)
    {
        if (ReferenceEquals(old, next)) return next;
        old.CollectionChanged -= OnSearchFieldCollectionChanged;
        next.CollectionChanged += OnSearchFieldCollectionChanged;
        InvalidateSearchText();
        return next;
    }

    // The haystack one search term is tested against. Composed at most once per mutation.
    [JsonIgnore]
    public string SearchText
    {
        get
        {
            var cached = _searchTextCache;
            if (cached is not null) return cached;
            PerfCounters.SearchTextComposed();
            var sb = new StringBuilder(
                Id.Length + DisplayTitle.Length + Title.Length + Text.Length
                + SourcePath.Length + Workspace.Length + 64);
            sb.Append(Id).Append('\n')
              .Append(DisplayTitle).Append('\n')
              .Append(Title).Append('\n')
              .Append(Text).Append('\n')
              .Append(SourcePath).Append('\n')
              .Append(Workspace).Append('\n');
            AppendJoined(sb, _tags);
            sb.Append('\n');
            AppendJoined(sb, _specialPhrases);
            return _searchTextCache = sb.ToString();
        }
    }

    private static void AppendJoined(StringBuilder sb, ObservableCollection<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(values[i]);
        }
    }

    private string _id = "";
    [JsonPropertyName("id")]
    public string Id { get => _id; set { _id = value; InvalidateSearchText(); } }

    // Title / CustomTitle / Pinned / UpdatedAt change at runtime (rename, pin, bump-on-resume,
    // re-sync), so they notify their computed display props to keep the live ListView in sync.
    private string _title = "";
    [JsonPropertyName("title")]
    public string Title { get => _title; set { _title = value; InvalidateSearchText(); Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(ListTitle)); } }

    private string _customTitle = "";
    [JsonPropertyName("customTitle")]
    public string CustomTitle { get => _customTitle; set { _customTitle = value; InvalidateSearchText(); Raise(); Raise(nameof(DisplayTitle)); Raise(nameof(ListTitle)); } }

    private string _sourcePath = "";
    [JsonPropertyName("sourcePath")]
    public string SourcePath { get => _sourcePath; set { _sourcePath = value; InvalidateSearchText(); } }

    // Alternate strong ids found in the transcript header/path. The session Id remains the current
    // resumable chat id; parent/fork ids live here only so exact-id operations can resolve safely.
    [JsonPropertyName("aliases")]
    public ObservableCollection<string> Aliases { get; set; } = new();

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    private string _updatedAt = "";
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get => _updatedAt; set { _updatedAt = value; Raise(); Raise(nameof(DisplayDate)); } }

    private string _workspace = "";
    [JsonPropertyName("workspace")]
    public string Workspace { get => _workspace; set { _workspace = value; InvalidateSearchText(); } }

    [JsonPropertyName("workspaceName")]
    public string WorkspaceName { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    // Which agent produced this chat: "codex" or "claude". Drives parse + resume routing + the badge.
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "codex";

    // Heavy content is NOT persisted or kept in memory at rest — it's lazy-loaded from SourcePath on open
    // (ArchiveService.EnsureContentAsync) and paginated in the reader. The store keeps only metadata +
    // a capped Text for fast in-memory search. This is what keeps the app light (was ~1GB resident).
    [JsonIgnore]
    public ObservableCollection<ArchiveMessage> Messages { get; set; } = new();

    [JsonIgnore]
    public ObservableCollection<CodeBlock> CodeBlocks { get; set; } = new();

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonIgnore]
    public bool ContentLoaded { get; set; }

    // Capped, searchable text (user+assistant+tool text, truncated). Serialized so search works without
    // holding full transcripts in memory.
    private string _text = "";
    [JsonPropertyName("text")]
    public string Text { get => _text; set { _text = value; InvalidateSearchText(); } }

    private ObservableCollection<string> _tags = new();
    [JsonPropertyName("tags")]
    public ObservableCollection<string> Tags { get => _tags; set => _tags = WatchForSearchText(_tags, value ?? new()); }

    // User-assigned searchable CODENAMES ("special phrases"). A chat can carry several; many chats can
    // share one (e.g. every chat under codename "petunia"). Folded into SearchText, so searching the
    // phrase surfaces every chat stashed under it. App-only metadata — NEVER written into the transcript.
    private ObservableCollection<string> _specialPhrases = new();
    [JsonPropertyName("specialPhrases")]
    public ObservableCollection<string> SpecialPhrases { get => _specialPhrases; set => _specialPhrases = WatchForSearchText(_specialPhrases, value ?? new()); }

    [JsonPropertyName("reviewed")]
    public bool Reviewed { get; set; }

    [JsonPropertyName("starred")]
    public bool Starred { get; set; }

    private bool _pinned;
    [JsonPropertyName("pinned")]
    public bool Pinned { get => _pinned; set { _pinned = value; Raise(); Raise(nameof(PinGlyph)); } }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    // Branch linkage. When this chat was created by the app's Branch action, BranchOfId is the PARENT
    // chat's id and BranchedAt is when the clone was taken. Presence of BranchOfId ⇒ this is a branch.
    // The parent link is also recoverable from the transcript's forkedFrom / forked_from_id marker, but
    // this explicit pair is the app's durable record so the "⑂ branch of …" badge never gets confusing.
    [JsonPropertyName("branchOfId")]
    public string BranchOfId { get; set; } = "";

    // Exact immutable checkpoint used to create this branch. BranchOfId remains the source chat id so
    // existing parent/descendant grouping and resumability semantics remain unchanged.
    [JsonPropertyName("fromSnapshotId")]
    public string FromSnapshotId { get; set; } = "";

    [JsonPropertyName("branchedAt")]
    public string BranchedAt { get; set; } = "";

    [JsonIgnore]
    public bool IsReadOnlySnapshot { get; set; }

    [JsonIgnore]
    public string ReadOnlySnapshotId { get; set; } = "";

    // Legacy migration input only. New checkpoints live in AppStoreData.TemplateSnapshots and are never
    // represented as sessions. The flag is cleared only after a private snapshot is durably persisted.
    private bool _isTemplate;
    [JsonPropertyName("isTemplate")]
    public bool IsTemplate { get => _isTemplate; set { _isTemplate = value; Raise(); Raise(nameof(ListMarks)); } }

    private int _templateSnapshotCount;
    [JsonIgnore]
    public int TemplateSnapshotCount
    {
        get => _templateSnapshotCount;
        set
        {
            if (_templateSnapshotCount == value) return;
            _templateSnapshotCount = value;
            Raise();
            Raise(nameof(ListMarks));
        }
    }

    [JsonIgnore]
    public bool IsBranch => !string.IsNullOrWhiteSpace(BranchOfId);

    // Sidebar marker for a branch (bound in the SessionList template). Empty for non-branches.
    [JsonIgnore]
    public string BranchGlyph => IsBranch ? "⑂" : "";

    // A legacy template flag with no snapshot is shown as pending migration, not as a checkpoint.
    [JsonIgnore]
    public string ListMarks =>
        (TemplateSnapshotCount > 0 ? $"★{TemplateSnapshotCount}" : IsTemplate ? "!" : "")
        + (IsBranch ? "⑂" : "");

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;

    // The last / first thing the USER typed in this chat (capped, single line). Captured at parse time from
    // the FULL transcript so the last/first-user-message list sorts + titles work without loading per row.
    [JsonPropertyName("lastUserMessage")]
    public string LastUserMessage { get; set; } = "";

    [JsonPropertyName("firstUserMessage")]
    public string FirstUserMessage { get; set; } = "";

    // How many REAL user prompts this chat has (your turns, not tool results) — counted from the FULL
    // transcript at parse time. Drives the "min user messages" filter and is shown on the row.
    [JsonPropertyName("userMessageCount")]
    public int UserMessageCount { get; set; }

    // Row-title mode the list sets per-row: "" = name, "last-user" = your last message, "first-user" = your
    // first message. Lets the "last/first user message" sorts show what you said instead of the chat name.
    private string _rowTitleMode = "";
    [JsonIgnore]
    public string RowTitleMode { get => _rowTitleMode; set { if (_rowTitleMode == value) return; _rowTitleMode = value; Raise(nameof(ListTitle)); } }

    [JsonIgnore]
    public string ListTitle => _rowTitleMode == "last-user"
            ? (string.IsNullOrWhiteSpace(LastUserMessage) ? "No messages · " + DisplayTitle : LastUserMessage)
        : _rowTitleMode == "first-user"
            ? (string.IsNullOrWhiteSpace(FirstUserMessage) ? DisplayTitle : FirstUserMessage)
        : DisplayTitle;

    [JsonIgnore]
    public string PinGlyph => Pinned ? "*" : "";

    [JsonIgnore]
    public string ToolShort => string.Equals(Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "CL" : "CX";

    [JsonIgnore]
    public string DisplayDate => DateTime.TryParse(UpdatedAt, out var date) ? date.ToString("MMM d") : "";
}

public sealed class ArchiveMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("codeBlocks")]
    public ObservableCollection<CodeBlock> CodeBlocks { get; set; } = new();

    // What this entry is, for the reader: "user" | "assistant" | "tool" | "reasoning". Falls back to Role.
    [JsonIgnore]
    public string Kind { get; set; } = "";

    // For Kind == "tool": a short label (e.g. the command/tool name) and its (collapsible) output.
    [JsonIgnore]
    public string ToolName { get; set; } = "";

    [JsonIgnore]
    public string ToolOutput { get; set; } = "";

    [JsonIgnore]
    public string EffectiveKind => string.IsNullOrWhiteSpace(Kind) ? Role : Kind;

    [JsonIgnore]
    public string RoleLabel => string.IsNullOrWhiteSpace(Role) ? "Message" : char.ToUpper(Role[0]) + Role[1..];
}

public sealed class CodeBlock
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "text";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";
}

// One command an outside agent writes to agent-inbox.jsonl to drive the app (see AGENTS.md).
public sealed class AgentCommand
{
    public string op { get; set; } = "";          // init | addSource | favorite | addToProject/addSelfToProject | rename | setName | bump | tag/untag
    public string? requestId { get; set; }         // echoed in outbox so callers can match acks
    public string? project { get; set; }           // addToProject/addSelfToProject
    public string? deck { get; set; }               // deck id/name to file the project into (default: main)
    public string? name { get; set; }               // optional app-only chat name (addSelfToProject / setName); alias of localName
    public List<string>? tags { get; set; }         // tag/untag: labels to add/remove (also accepts a single via name)
    public string? localName { get; set; }          // rename (app-only title)
    public string? canonicalName { get; set; }      // rename (write back to codex/claude)
    public string? tool { get; set; }               // addSource / target filter: codex | claude
    public string? root { get; set; }               // addSource
    public string? cwd { get; set; }                // self/latest resolution by workspace
    public string? id { get; set; }                 // explicit session id
    public string? target { get; set; }             // "self" | "latest" | <session-id>
    public int pid { get; set; }                    // tomux: the caller's host agent pid, for a reliable local kill
    public string? phrase { get; set; }             // stash: a searchable codename ("special phrase") to file this chat under
    public string? collection { get; set; }         // stash: the collection name (alias of project)
    public bool? template { get; set; }             // true: create one checkpoint; false: remove all checkpoints for this chat
    public string? templateName { get; set; }       // stash: optional name for the checkpoint created when template==true

    [JsonIgnore]
    internal int TemplateRemovalCount { get; set; } // preserves the reported count across one generation-conflict replay
}

public sealed record AgentCommandResult(
    bool Ok,
    string Message,
    string? InputId = null,
    string? ResolvedSessionId = null,
    string? Project = null,
    bool? Persisted = null);

public sealed record ResumeLaunch(string Exe, string Arguments, string WorkingDirectory, string DisplayCommand);

public sealed record RawEvent(string Kind, string Timestamp, string Preview);

// The result of an off-thread disk scan: freshly parsed/changed sessions (Disk), one-time
// recovered bundled history (Bundled), and the current file stamps (path -> mtime+size) used for
// incremental sync. Merged into the store on the UI thread by MergeScanAsync.
public sealed record DiskScan(List<ArchiveSession> Disk, List<ArchiveSession> Bundled)
{
    public Dictionary<string, string> Stamps { get; init; } = new();

    // True when the incremental cache was ignored (parser-version migration) and every file was
    // re-parsed — the only safe time to prune orphaned sessions whose id scheme changed.
    public bool FullRescan { get; init; }
}

public sealed class ArchiveSearchHit
{
    public ArchiveSession Session { get; set; } = new();

    public ArchiveMessage? Message { get; set; }

    public string Snippet { get; set; } = "";

    public string SourceLabel { get; set; } = "";

    public string MatchedTerms { get; set; } = "";

    public int Score { get; set; }
}
