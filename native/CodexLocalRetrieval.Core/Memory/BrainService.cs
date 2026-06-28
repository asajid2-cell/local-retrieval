using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Memory;

public sealed record BrainBuildResult(MemoryPatch Patch, IReadOnlyList<SourceBlock> Blocks, Dictionary<string, string> ChatStamps);
public sealed record BrainApplyResult(string Commit, int CardCount, int CanonicalCount, int WorkingCount);

public sealed class BrainStatus
{
    public bool Built { get; init; }
    public string BuiltAt { get; init; } = "";
    public string LastCommit { get; init; } = "";
    public int CanonicalCount { get; init; }
    public int WorkingCount { get; init; }
    public int IncorporatedChats { get; init; }
    public int StaleChats { get; init; }   // chats changed/added since the last build
}

// Orchestrates a collection's brain: chats -> deterministic source blocks -> analyst candidates ->
// reviewable patch -> (on apply) canonical vault write + local git commit + derived-index rebuild.
// A build NEVER mutates the canonical vault; only ApplyPatch does, and it always commits.
public sealed class BrainService
{
    private readonly ArchiveService _archive;
    private readonly VaultWriter _vault = new();
    private readonly MemoryPatchService _patches = new();
    private readonly BrainBuilder _builder = new();
    private readonly GitHistory _git = new();

    public BrainService(ArchiveService archive) => _archive = archive;

    public BrainPaths PathsFor(string collectionId) => new(_archive.BrainsDir, collectionId);
    public GitHistory Git => _git;

    // chat -> blocks (full-history parse + deterministic blocking), plus the file stamp per chat.
    public async Task<(List<SourceBlock> blocks, Dictionary<string, string> stamps)> BuildBlocksAsync(
        IEnumerable<ArchiveSession> chats, CancellationToken ct = default)
    {
        var blocks = new List<SourceBlock>();
        var stamps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var chat in chats)
        {
            ct.ThrowIfCancellationRequested();
            var full = await _archive.ParseFullAsync(chat);
            if (full.Count == 0) continue;
            blocks.AddRange(SourceBlocker.Build(chat.Id, chat.SourcePath, chat.Tool, full));
            stamps[chat.Id] = SourceBlocker.ComputeFileStamp(chat.SourcePath);
        }
        return (blocks, stamps);
    }

    // Build a reviewable patch. Does NOT touch the canonical vault.
    public async Task<BrainBuildResult> BuildAsync(string collectionId, IEnumerable<ArchiveSession> chats,
        IReadOnlyList<IBrainAnalyst> analysts, BrainBuildOptions opts, string nowIso, CancellationToken ct = default)
    {
        var (blocks, stamps) = await BuildBlocksAsync(chats, ct);
        var candidates = await _builder.RunAnalystsAsync(blocks, analysts, opts, nowIso, ct);

        var paths = PathsFor(collectionId);
        var existing = _vault.ReadAllCards(paths);
        var patchId = PatchId(collectionId, candidates, nowIso);
        var patch = _patches.BuildPatch(collectionId, existing, candidates, AnalystLabel(analysts), patchId, nowIso);
        return new BrainBuildResult(patch, blocks, stamps);
    }

    // Apply an approved patch: write the canonical vault, commit to local git, rebuild the derived index.
    public BrainApplyResult ApplyPatch(string collectionId, string collectionName, MemoryPatch patch,
        IReadOnlyList<SourceBlock> blocks, Dictionary<string, string> chatStamps, string nowIso)
    {
        var paths = PathsFor(collectionId);
        var manifest = _vault.ReadManifest(paths);
        manifest.CollectionId = collectionId;
        manifest.CollectionName = collectionName;
        _vault.EnsureScaffold(paths, manifest);

        var existing = _vault.ReadAllCards(paths);
        var merged = _patches.Apply(existing, patch);

        _vault.WriteCards(paths, merged);
        _vault.WriteOverviews(paths, merged, manifest);
        _vault.WritePatch(paths, patch);

        manifest.BuiltAt = nowIso;
        manifest.LastPatchId = patch.Id;
        foreach (var kv in chatStamps) manifest.IncorporatedChats[kv.Key] = kv.Value;

        // Markdown wins, then git, then the derived index.
        var commit = _git.Commit(paths.Vault, $"Brain patch {patch.Id}: {patch.Summary}".Trim());
        manifest.LastCommit = commit;
        _vault.WriteManifest(paths, manifest);
        if (!string.IsNullOrEmpty(commit)) _git.Commit(paths.Vault, $"Update manifest for {patch.Id}");

        var idx = new BrainIndex(paths.Db);
        idx.Rebuild(merged, blocks, manifest);

        return new BrainApplyResult(
            commit,
            merged.Count(c => c.Status == CardStatuses.Active),
            merged.Count(c => c.Lane == Lanes.Canonical && c.Status == CardStatuses.Active),
            merged.Count(c => c.Lane == Lanes.Working && c.Status == CardStatuses.Active));
    }

    // Convenience for the headless flow / tests: build then apply in one call.
    public async Task<BrainApplyResult> BuildAndApplyAsync(string collectionId, string collectionName,
        IEnumerable<ArchiveSession> chats, IReadOnlyList<IBrainAnalyst> analysts, BrainBuildOptions opts,
        string nowIso, CancellationToken ct = default)
    {
        var build = await BuildAsync(collectionId, chats, analysts, opts, nowIso, ct);
        return ApplyPatch(collectionId, collectionName, build.Patch, build.Blocks, build.ChatStamps, nowIso);
    }

    // How many of the given chats are new or changed since the last build (drives the "stale +N" badge).
    public int StaleCount(string collectionId, IEnumerable<ArchiveSession> chats)
    {
        var manifest = _vault.ReadManifest(PathsFor(collectionId));
        var stale = 0;
        foreach (var chat in chats)
        {
            var now = SourceBlocker.ComputeFileStamp(chat.SourcePath);
            if (!manifest.IncorporatedChats.TryGetValue(chat.Id, out var was) || was != now) stale++;
        }
        return stale;
    }

    public BrainStatus Status(string collectionId, IEnumerable<ArchiveSession> chats)
    {
        var paths = PathsFor(collectionId);
        var manifest = _vault.ReadManifest(paths);
        var cards = _vault.ReadAllCards(paths);
        var built = !string.IsNullOrWhiteSpace(manifest.BuiltAt);
        return new BrainStatus
        {
            Built = built,
            BuiltAt = manifest.BuiltAt,
            LastCommit = manifest.LastCommit,
            CanonicalCount = cards.Count(c => c.Lane == Lanes.Canonical && c.Status == CardStatuses.Active),
            WorkingCount = cards.Count(c => c.Lane == Lanes.Working && c.Status == CardStatuses.Active),
            IncorporatedChats = manifest.IncorporatedChats.Count,
            StaleChats = StaleCount(collectionId, chats),
        };
    }

    // A compact, sparse handoff bundle for the next agent — current state, top canonical truths,
    // open loops, with source links. Built deterministically from the canonical vault.
    public string GetAgentContext(string collectionId, string task)
    {
        var paths = PathsFor(collectionId);
        var manifest = _vault.ReadManifest(paths);
        var cards = _vault.ReadAllCards(paths).Where(c => c.Status == CardStatuses.Active).ToList();
        var sb = new System.Text.StringBuilder();
        sb.Append("# Agent handoff — ").Append(string.IsNullOrWhiteSpace(manifest.CollectionName) ? collectionId : manifest.CollectionName).Append('\n');
        if (!string.IsNullOrWhiteSpace(task)) sb.Append("Task: ").Append(task).Append('\n');
        sb.Append('\n');

        void Section(string title, IEnumerable<MemoryCard> sel)
        {
            var list = sel.ToList();
            sb.Append("## ").Append(title).Append('\n');
            if (list.Count == 0) { sb.Append("_none_\n\n"); return; }
            foreach (var c in list.OrderByDescending(c => c.Importance).Take(8))
            {
                sb.Append("- ").Append(c.Title).Append(" `").Append(c.TruthEvidence).Append('`');
                if (c.Sources.Count > 0) sb.Append(" — ").Append(VaultWriter.SourceLink(collectionId, c.Sources[0]));
                sb.Append('\n');
            }
            sb.Append('\n');
        }

        Section("Current state", cards.Where(c => c.Type == CardTypes.CurrentState));
        Section("Canonical truths", cards.Where(c => c.Lane == Lanes.Canonical));
        Section("Open loops & questions", cards.Where(c => c.Type is CardTypes.OpenLoop or CardTypes.QuestionForHuman));
        Section("Working / exploratory (NOT settled)", cards.Where(c => c.Lane == Lanes.Working && c.Type != CardTypes.OpenLoop));
        return sb.ToString();
    }

    private static string AnalystLabel(IReadOnlyList<IBrainAnalyst> analysts)
        => analysts.Count == 0 ? "none" : string.Join("+", analysts.Select(a => a.Id).Distinct());

    private static string PatchId(string collectionId, IReadOnlyList<MemoryCard> candidates, string nowIso)
    {
        var basis = collectionId + "|" + nowIso + "|" + string.Join(",", candidates.Select(c => c.Id).OrderBy(x => x, StringComparer.Ordinal));
        return "patch_" + SourceBlocker.Sha256(basis)[..12];
    }
}
