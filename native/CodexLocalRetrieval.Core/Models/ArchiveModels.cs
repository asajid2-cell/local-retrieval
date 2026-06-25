using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CodexLocalRetrieval.Core.Models;

public sealed class AppStoreData
{
    [JsonPropertyName("sessions")]
    public Dictionary<string, ArchiveSession> Sessions { get; set; } = new();

    [JsonPropertyName("settings")]
    public ArchiveSettings Settings { get; set; } = new();

    [JsonPropertyName("collections")]
    public Dictionary<string, ArchiveCollection> Collections { get; set; } = new();

    // Incremental sync: source file path -> "mtimeTicks:size". Unchanged files are skipped on
    // re-scan so relaunches are fast. Deletions are never propagated (chats keep accumulating).
    [JsonPropertyName("fileStamps")]
    public Dictionary<string, string> FileStamps { get; set; } = new();
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

    [JsonPropertyName("aiProviders")]
    public List<AiProviderSettings> AiProviders { get; set; } = new();
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
}

public sealed class ArchiveSession : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    // Title / CustomTitle / Pinned / UpdatedAt change at runtime (rename, pin, bump-on-resume,
    // re-sync), so they notify their computed display props to keep the live ListView in sync.
    private string _title = "";
    [JsonPropertyName("title")]
    public string Title { get => _title; set { _title = value; Raise(); Raise(nameof(DisplayTitle)); } }

    private string _customTitle = "";
    [JsonPropertyName("customTitle")]
    public string CustomTitle { get => _customTitle; set { _customTitle = value; Raise(); Raise(nameof(DisplayTitle)); } }

    [JsonPropertyName("sourcePath")]
    public string SourcePath { get; set; } = "";

    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = "";

    private string _updatedAt = "";
    [JsonPropertyName("updatedAt")]
    public string UpdatedAt { get => _updatedAt; set { _updatedAt = value; Raise(); Raise(nameof(DisplayDate)); } }

    [JsonPropertyName("workspace")]
    public string Workspace { get; set; } = "";

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
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("tags")]
    public ObservableCollection<string> Tags { get; set; } = new();

    [JsonPropertyName("reviewed")]
    public bool Reviewed { get; set; }

    [JsonPropertyName("starred")]
    public bool Starred { get; set; }

    private bool _pinned;
    [JsonPropertyName("pinned")]
    public bool Pinned { get => _pinned; set { _pinned = value; Raise(); Raise(nameof(PinGlyph)); } }

    [JsonPropertyName("archived")]
    public bool Archived { get; set; }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;

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
    public string op { get; set; } = "";          // init | addSource | favorite | addToProject | rename
    public string? project { get; set; }           // addToProject
    public string? localName { get; set; }          // rename (app-only title)
    public string? canonicalName { get; set; }      // rename (write back to codex/claude)
    public string? tool { get; set; }               // addSource / target filter: codex | claude
    public string? root { get; set; }               // addSource
    public string? cwd { get; set; }                // self/latest resolution by workspace
    public string? id { get; set; }                 // explicit session id
    public string? target { get; set; }             // "self" | "latest" | <session-id>
}

public sealed record AgentCommandResult(bool Ok, string Message);

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
