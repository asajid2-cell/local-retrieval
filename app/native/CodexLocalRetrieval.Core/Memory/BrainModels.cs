using System.Collections.Generic;

namespace CodexLocalRetrieval.Core.Memory;

// One message of a transcript, in full-history (no-window) order. Index is the message's position
// in the FULL parsed transcript (0-based, stable across re-parse as long as the file is unchanged) —
// it is the anchor coordinate the brain points back to. ToolName is set for tool/command steps.
public sealed record FullMessage(int Index, string Role, string Text, string Timestamp, string? ToolName)
{
    public bool IsTool => string.Equals(Role, "tool", System.StringComparison.OrdinalIgnoreCase);
}

// A precise pointer back to a span of raw transcript. This is the contract that lets any card prove
// where its claim came from — and lets the reader "open source span" to the exact messages.
// quoteHash binds the anchor to the actual text so a silently-rewritten transcript fails validation.
public sealed record SourceAnchor
{
    public string SessionId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string BlockId { get; init; } = "";
    public int MsgStartIndex { get; init; }
    public int MsgEndIndex { get; init; }
    public string TsStart { get; init; } = "";
    public string TsEnd { get; init; } = "";
    public string QuoteHash { get; init; } = "";
    public string FileStamp { get; init; } = "";
}

// A compressed local representation of a contiguous span of one chat — the NSA "selective block".
// Deterministic: same transcript bytes ⇒ same id, ranges, and hashes. The excerpt is redacted.
public sealed record SourceBlock
{
    public string Id { get; init; } = "";                 // chat_<sid>:block_<n>
    public string SessionId { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string Tool { get; init; } = "";
    public int Index { get; init; }                        // 0-based block number within the chat
    public int MsgStartIndex { get; init; }
    public int MsgEndIndex { get; init; }                  // inclusive
    public string TsStart { get; init; } = "";
    public string TsEnd { get; init; } = "";
    public int MessageCount { get; init; }
    public int ApproxTokens { get; init; }
    public string SourceHash { get; init; } = "";          // sha256 over the block's role|ts|text stream
    public string QuoteHash { get; init; } = "";           // == SourceHash here (whole-block quote)
    public string FileStamp { get; init; } = "";           // mtimeTicks:size at block time
    public string Excerpt { get; init; } = "";             // short REDACTED preview for cards/UI
    public IReadOnlyList<string> Roles { get; init; } = new List<string>();

    public SourceAnchor ToAnchor() => new()
    {
        SessionId = SessionId,
        SourcePath = SourcePath,
        BlockId = Id,
        MsgStartIndex = MsgStartIndex,
        MsgEndIndex = MsgEndIndex,
        TsStart = TsStart,
        TsEnd = TsEnd,
        QuoteHash = QuoteHash,
        FileStamp = FileStamp,
    };
}

// Controlled vocabularies — centralized so the exact frontmatter tokens never drift. Markdown is the
// canonical store, so these strings ARE the schema; keep them lowercase snake to read cleanly in YAML.
public static class Lanes { public const string Canonical = "canonical", Working = "working"; }

public static class CardTypes
{
    public const string Decision = "decision", CanonicalTruth = "canonical_truth", OpenLoop = "open_loop",
        RejectedApproach = "rejected_approach", MistakeOrReversal = "mistake_or_reversal",
        WinOrBreakthrough = "win_or_breakthrough", Constraint = "constraint", CurrentState = "current_state",
        UserPreference = "user_preference", ArchitectureFact = "architecture_fact", FileOrArtifact = "file_or_artifact",
        Risk = "risk", QuestionForHuman = "question_for_human", AgentHandoffContext = "agent_handoff_context";
}

public static class CardStatuses { public const string Active = "active", Superseded = "superseded", Rejected = "rejected", Disputed = "disputed"; }

// How strongly the SOURCE supports the claim (truth comes from the transcript, never from agreement).
public static class TruthEvidence
{
    public const string Weak = "weak", SingleSource = "single_source", RepeatedInSources = "repeated_in_sources",
        ResultBacked = "result_backed", UserConfirmed = "user_confirmed";
}

// How sure the EXTRACTION/parse is. Multi-agent agreement raises THIS only — never TruthEvidence.
public static class ExtractionConfidence { public const string Low = "low", Medium = "medium", High = "high"; }

public static class SourceCoverage { public const string ExactSpan = "exact_span", BroadChat = "broad_chat", ManualUnverified = "manual_unverified"; }

public static class Sensitivities { public const string Normal = "normal", ContainsRedactedSecret = "contains_redacted_secret", Private = "private"; }

public static class CreatedBy { public const string User = "user", DeepSeek = "deepseek", Claude = "claude", Codex = "codex", Mock = "mock"; }

// A durable typed unit of extracted knowledge. The .md file (YAML frontmatter + body + [[wikilinks]])
// is canonical; this record is what the app parses the frontmatter INTO for the SQLite index.
public sealed class MemoryCard
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string Id { get; set; } = "";                   // stable slug, also the filename stem
    public string Title { get; set; } = "";
    public string Lane { get; set; } = Lanes.Working;
    public string Type { get; set; } = CardTypes.OpenLoop;
    public string Status { get; set; } = CardStatuses.Active;
    public string TruthEvidence { get; set; } = Memory.TruthEvidence.Weak;
    public string ExtractionConfidence { get; set; } = Memory.ExtractionConfidence.Low;
    public int Importance { get; set; } = 3;               // 1..5
    public int Durability { get; set; } = 3;               // 1..5 (how long it stays true)
    public int Actionability { get; set; } = 3;            // 1..5
    public string CreatedBy { get; set; } = Memory.CreatedBy.Mock;
    public string CreatedAt { get; set; } = "";            // ISO-8601, stamped by the writer
    public string DerivedFromPatchId { get; set; } = "";
    public string SourceCoverage { get; set; } = Memory.SourceCoverage.ManualUnverified;
    public string Sensitivity { get; set; } = Sensitivities.Normal;
    public string SupersededReason { get; set; } = "";
    public string SupersededBy { get; set; } = "";
    public List<string> Supersedes { get; set; } = new();
    public List<string> LedTo { get; set; } = new();       // causal: this decision led to …
    public List<string> CausedBy { get; set; } = new();    // causal: this was caused by …
    public List<string> Topics { get; set; } = new();      // [[wikilink]] topics
    public List<string> Related { get; set; } = new();     // [[wikilink]] card ids
    public List<SourceAnchor> Sources { get; set; } = new();
    public string Body { get; set; } = "";                 // markdown body beneath the frontmatter

    // A card may live in the canonical lane only if it has at least one real source anchor with a
    // quote hash. No anchor ⇒ it cannot be canonical (it lands in working / manual_unverified).
    public bool HasSourceAnchor => Sources.Exists(s => !string.IsNullOrWhiteSpace(s.QuoteHash));
    public bool CanonicalEligible => HasSourceAnchor && Lane == Lanes.Canonical;
}

// A reviewable, non-silent memory update — the only way canonical files ever change.
public sealed class MemoryPatch
{
    public string Id { get; set; } = "";
    public string CollectionId { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<MemoryCard> Adds { get; set; } = new();
    public List<MemoryCard> Updates { get; set; } = new();
    public List<MemoryCard> Supersedes { get; set; } = new();   // cards being retired (status set + reason)
    public List<string> OpenQuestions { get; set; } = new();
    public List<string> Conflicts { get; set; } = new();
    public List<string> SourceChatIds { get; set; } = new();
}

// Per-brain manifest (vault/.brain.json) — what's built, from which chats, at which commit.
public sealed class BrainManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string CollectionId { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public string BuiltAt { get; set; } = "";
    public string LastPatchId { get; set; } = "";
    public string LastCommit { get; set; } = "";
    // chat id -> fileStamp (mtimeTicks:size) the brain last incorporated — drives staleness.
    public Dictionary<string, string> IncorporatedChats { get; set; } = new();
}
