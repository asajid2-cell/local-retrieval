using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Chat;

namespace CodexLocalRetrieval.Core.Memory;

public enum BuildScope { ProjectOnly, DeepSearch }

public sealed class BrainBuildOptions
{
    public BuildScope Scope { get; set; } = BuildScope.ProjectOnly;
    public int MaxCardsPerAnalyst { get; set; } = 40;
}

// One analyst's proposed card BEFORE it becomes a real MemoryCard (anchors resolved, lane enforced).
public sealed class CandidateCard
{
    public string Title { get; set; } = "";
    public string Type { get; set; } = CardTypes.OpenLoop;
    public string Lane { get; set; } = Lanes.Working;          // a HINT; the patch service has final say
    public string TruthEvidence { get; set; } = Memory.TruthEvidence.Weak;
    public int Importance { get; set; } = 3;
    public string Body { get; set; } = "";
    public List<string> Topics { get; set; } = new();
    public List<string> BlockIds { get; set; } = new();
    public string CreatedBy { get; set; } = Memory.CreatedBy.Mock;
}

// An analyst reads source blocks and proposes candidate cards. Implementations: a deterministic mock
// (keyless, so the flow never breaks) and a real LLM backend.
public interface IBrainAnalyst
{
    string Id { get; }
    Task<List<CandidateCard>> ExtractAsync(IReadOnlyList<SourceBlock> blocks, BrainBuildOptions opts, CancellationToken ct);
}

// Deterministic, no-API analyst. It does NOT pretend to judge truth: everything it emits is working-
// lane, single_source, created_by=mock — honest placeholders so the UI/flow work with no key set.
public sealed class MockAnalyst : IBrainAnalyst
{
    public string Id => CreatedBy.Mock;

    public Task<List<CandidateCard>> ExtractAsync(IReadOnlyList<SourceBlock> blocks, BrainBuildOptions opts, CancellationToken ct)
    {
        var cards = new List<CandidateCard>();
        if (blocks.Count == 0) return Task.FromResult(cards);

        // One "current state" card grounded in the last block, plus a working note per early block.
        var last = blocks[^1];
        cards.Add(new CandidateCard
        {
            Title = $"Current state of {last.SessionId}",
            Type = CardTypes.CurrentState,
            Lane = Lanes.Working,
            TruthEvidence = Memory.TruthEvidence.SingleSource,
            Importance = 3,
            Body = "Auto-extracted placeholder (no AI key set). Most recent activity excerpt:\n\n> " +
                   FirstLine(last.Excerpt),
            Topics = new() { last.SessionId },
            BlockIds = new() { last.Id },
            CreatedBy = CreatedBy.Mock,
        });

        var take = Math.Min(blocks.Count, Math.Max(1, opts.MaxCardsPerAnalyst - 1));
        for (var i = 0; i < take; i++)
        {
            var b = blocks[i];
            if (string.IsNullOrWhiteSpace(b.Excerpt)) continue;
            cards.Add(new CandidateCard
            {
                Title = $"Note from {b.Id}",
                Type = CardTypes.OpenLoop,
                Lane = Lanes.Working,
                TruthEvidence = Memory.TruthEvidence.SingleSource,
                Importance = Math.Clamp(1 + b.ApproxTokens / 800, 1, 5),
                Body = "> " + FirstLine(b.Excerpt),
                Topics = new() { b.SessionId },
                BlockIds = new() { b.Id },
                CreatedBy = CreatedBy.Mock,
            });
        }
        return Task.FromResult(cards);
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "(empty)";
        var nl = s.IndexOf('\n');
        return (nl >= 0 ? s[..nl] : s).Trim();
    }
}

// Real-LLM analyst over any IChatBackend (DeepSeek today, Claude later). One-shot extraction; tolerant
// JSON parsing (handles code fences and a {"cards":[...]} wrapper).
public sealed class BackendAnalyst : IBrainAnalyst
{
    private readonly IChatBackend _backend;
    private readonly string _createdBy;

    public BackendAnalyst(IChatBackend backend)
    {
        _backend = backend;
        _createdBy = MapCreatedBy(backend.Name);
    }

    public string Id => _createdBy;

    // Cap the excerpt text sent in one request so a long project doesn't overflow the model's context.
    public const int MaxExcerptCharsPerBatch = 40_000;

    public async Task<List<CandidateCard>> ExtractAsync(IReadOnlyList<SourceBlock> blocks, BrainBuildOptions opts, CancellationToken ct)
    {
        if (blocks.Count == 0) return new List<CandidateCard>();
        var all = new List<CandidateCard>();
        // Map over batches of blocks (a real multi-session project has far more than one request can hold);
        // accumulate cards across batches, stop once we have enough.
        foreach (var batch in Batch(blocks, MaxExcerptCharsPerBatch))
        {
            ct.ThrowIfCancellationRequested();
            var messages = new[]
            {
                ChatMessage.System(MemoryPrompts.ExtractionSystem),
                ChatMessage.User(MemoryPrompts.BlocksUserMessage(batch)),
            };
            var reply = await _backend.CompleteAsync(messages, Array.Empty<ChatToolSpec>(), ct);
            all.AddRange(ParseCards(reply.Message.Content ?? "", _createdBy, opts.MaxCardsPerAnalyst));
            if (all.Count >= opts.MaxCardsPerAnalyst) break;
        }
        return all;
    }

    // Group blocks so the total excerpt length per batch stays under maxChars (each block is its own
    // minimum unit). Deterministic given the block order.
    public static IEnumerable<IReadOnlyList<SourceBlock>> Batch(IReadOnlyList<SourceBlock> blocks, int maxChars)
    {
        var cur = new List<SourceBlock>();
        var chars = 0;
        foreach (var b in blocks)
        {
            var len = (b.Excerpt?.Length ?? 0) + 120; // + header overhead per block
            if (cur.Count > 0 && chars + len > maxChars)
            {
                yield return cur;
                cur = new List<SourceBlock>();
                chars = 0;
            }
            cur.Add(b);
            chars += len;
        }
        if (cur.Count > 0) yield return cur;
    }

    public static List<CandidateCard> ParseCards(string content, string createdBy, int max)
    {
        var cards = new List<CandidateCard>();
        var json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json)) return cards;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Object && arr.TryGetProperty("cards", out var inner)) arr = inner;
            if (arr.ValueKind != JsonValueKind.Array) return cards;
            foreach (var e in arr.EnumerateArray())
            {
                if (cards.Count >= max) break;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var c = new CandidateCard
                {
                    Title = Str(e, "title"),
                    Type = Str(e, "type", CardTypes.OpenLoop),
                    Lane = Str(e, "lane", Lanes.Working) == Lanes.Canonical ? Lanes.Canonical : Lanes.Working,
                    TruthEvidence = Str(e, "truth_evidence", Memory.TruthEvidence.Weak),
                    Importance = IntOr(e, "importance", 3),
                    Body = Str(e, "body"),
                    Topics = StrList(e, "topics"),
                    BlockIds = StrList(e, "block_ids"),
                    CreatedBy = createdBy,
                };
                if (!string.IsNullOrWhiteSpace(c.Title)) cards.Add(c);
            }
        }
        catch { /* a malformed model reply yields no cards rather than throwing */ }
        return cards;
    }

    private static string ExtractJson(string content)
    {
        content = content.Trim();
        // strip ```json ... ``` fences
        if (content.StartsWith("```"))
        {
            var firstNl = content.IndexOf('\n');
            if (firstNl >= 0) content = content[(firstNl + 1)..];
            var fence = content.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0) content = content[..fence];
            content = content.Trim();
        }
        // otherwise pull the outermost [ ... ] or { ... }
        var start = content.IndexOfAny(new[] { '[', '{' });
        var end = content.LastIndexOfAny(new[] { ']', '}' });
        if (start >= 0 && end > start) return content[start..(end + 1)];
        return content;
    }

    private static string MapCreatedBy(string name)
    {
        name = (name ?? "").ToLowerInvariant();
        if (name.Contains("deepseek")) return CreatedBy.DeepSeek;
        if (name.Contains("claude")) return CreatedBy.Claude;
        if (name.Contains("codex")) return CreatedBy.Codex;
        return CreatedBy.Mock;
    }

    private static string Str(JsonElement e, string prop, string fallback = "")
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static int IntOr(JsonElement e, string prop, int fallback)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
    private static List<string> StrList(JsonElement e, string prop)
    {
        var list = new List<string>();
        if (e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }
}

// Runs analysts over the blocks and converts their candidates into real MemoryCards with anchors
// resolved from the cited blocks. It does NOT decide adds/updates/lane — that's MemoryPatchService.
public sealed class BrainBuilder
{
    public async Task<List<MemoryCard>> RunAnalystsAsync(IReadOnlyList<SourceBlock> blocks,
        IReadOnlyList<IBrainAnalyst> analysts, BrainBuildOptions opts, string nowIso, CancellationToken ct)
    {
        var byId = blocks.ToDictionary(b => b.Id, b => b, StringComparer.Ordinal);
        var result = new List<MemoryCard>();
        foreach (var analyst in analysts)
        {
            List<CandidateCard> candidates;
            try { candidates = await analyst.ExtractAsync(blocks, opts, ct); }
            catch { candidates = new List<CandidateCard>(); }
            foreach (var cand in candidates)
                result.Add(ToCard(cand, byId, nowIso));
        }
        return result;
    }

    // Resolve a candidate into a MemoryCard. Anchors come from the cited block ids; a card with no
    // resolvable anchor is forced to manual_unverified (the patch service then keeps it out of canonical).
    public static MemoryCard ToCard(CandidateCard cand, IReadOnlyDictionary<string, SourceBlock> blocksById, string nowIso)
    {
        var anchors = new List<SourceAnchor>();
        foreach (var bid in cand.BlockIds.Distinct())
            if (blocksById.TryGetValue(bid, out var blk)) anchors.Add(blk.ToAnchor());

        var card = new MemoryCard
        {
            Id = Slugify(cand.Title),
            Title = SecretRedactor.StripLoneSurrogates(cand.Title.Trim()),
            Lane = cand.Lane,
            Type = cand.Type,
            Status = CardStatuses.Active,
            TruthEvidence = cand.TruthEvidence,
            ExtractionConfidence = ExtractionConfidence.Low, // a single analyst pass starts low; agreement raises it
            Importance = Math.Clamp(cand.Importance, 1, 5),
            Durability = 3,
            Actionability = 3,
            CreatedBy = cand.CreatedBy,
            CreatedAt = nowIso,
            Sensitivity = SecretRedactor.ContainsSecret(cand.Body) ? Sensitivities.ContainsRedactedSecret : Sensitivities.Normal,
            Topics = cand.Topics.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList(),
            Sources = anchors,
            SourceCoverage = anchors.Count > 0 ? SourceCoverage.ExactSpan : SourceCoverage.ManualUnverified,
            Body = SecretRedactor.Clean(cand.Body),
        };
        return card;
    }

    public static string Slugify(string title)
    {
        var chars = (title ?? "").Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-');
        var slug = new string(chars.ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "card" : (slug.Length > 60 ? slug[..60].Trim('-') : slug);
    }
}
