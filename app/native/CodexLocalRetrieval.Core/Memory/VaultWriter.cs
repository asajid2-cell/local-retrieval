using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Memory;

// Writes (and re-writes) the canonical Obsidian Markdown vault for a brain. Everything here is
// deterministic and human-readable: one card per .md file, [[wikilinks]] for the Obsidian graph, and
// an auto-generated "Sources" section on every card so provenance ("from where") is never optional.
// This NEVER writes raw transcripts — only redacted excerpts, anchors, and hashes.
public sealed class VaultWriter
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void EnsureScaffold(BrainPaths paths, BrainManifest manifest)
    {
        Directory.CreateDirectory(paths.CardsCanonical);
        Directory.CreateDirectory(paths.CardsWorking);
        Directory.CreateDirectory(paths.WorkingDir);
        Directory.CreateDirectory(paths.TopicsDir);
        Directory.CreateDirectory(paths.PatchesDir);
        Directory.CreateDirectory(paths.IndexDir);
        Directory.CreateDirectory(paths.SourceCacheDir);

        if (!File.Exists(paths.GitIgnore))
            File.WriteAllText(paths.GitIgnore,
                "# The vault IS the canonical, tracked source. Only ignore per-user Obsidian state\n" +
                "# and OS cruft. The derived index/ and source-cache/ live OUTSIDE this repo.\n" +
                ".obsidian/workspace*\n.obsidian/cache\n.DS_Store\nThumbs.db\n");

        WriteManifest(paths, manifest);
    }

    public string CardPath(BrainPaths paths, MemoryCard card)
        => Path.Combine(paths.CardsDirFor(card.Lane), Slug(card.Id) + ".md");

    // Writes one card .md (frontmatter via CardMarkdown + body + an auto Sources section).
    public void WriteCard(BrainPaths paths, MemoryCard card)
    {
        Directory.CreateDirectory(paths.CardsDirFor(card.Lane));
        var body = ComposeBody(card);
        var toWrite = new MemoryCard
        {
            // shallow copy so we don't mutate the caller's body
            SchemaVersion = card.SchemaVersion, Id = card.Id, Title = card.Title, Lane = card.Lane,
            Type = card.Type, Status = card.Status, TruthEvidence = card.TruthEvidence,
            ExtractionConfidence = card.ExtractionConfidence, Importance = card.Importance,
            Durability = card.Durability, Actionability = card.Actionability, CreatedBy = card.CreatedBy,
            CreatedAt = card.CreatedAt, DerivedFromPatchId = card.DerivedFromPatchId,
            SourceCoverage = card.SourceCoverage, Sensitivity = card.Sensitivity,
            SupersededReason = card.SupersededReason, SupersededBy = card.SupersededBy,
            Supersedes = card.Supersedes, LedTo = card.LedTo, CausedBy = card.CausedBy,
            Topics = card.Topics, Related = card.Related, Sources = card.Sources, Body = body,
        };
        File.WriteAllText(CardPath(paths, toWrite), CardMarkdown.Serialize(toWrite));
    }

    // Rewrites the full card set (used on a fresh build/rebuild). Clears the card dirs first so a card
    // that moved lanes or was renamed doesn't leave a stale duplicate. Supersede ≠ delete: superseded
    // cards are KEPT (status=superseded), they just stop appearing in canonical overviews.
    public void WriteCards(BrainPaths paths, IEnumerable<MemoryCard> cards)
    {
        ClearDir(paths.CardsCanonical);
        ClearDir(paths.CardsWorking);
        foreach (var c in cards) WriteCard(paths, c);
    }

    public void WriteOverviews(BrainPaths paths, IReadOnlyList<MemoryCard> cards, BrainManifest manifest)
    {
        var canonical = cards.Where(c => c.Lane == Lanes.Canonical && c.Status == CardStatuses.Active)
            .OrderByDescending(c => c.Importance).ThenBy(c => c.Id).ToList();
        var working = cards.Where(c => c.Lane == Lanes.Working && c.Status == CardStatuses.Active)
            .OrderByDescending(c => c.Importance).ThenBy(c => c.Id).ToList();
        var openLoops = cards.Where(c => c.Status == CardStatuses.Active &&
            (c.Type == CardTypes.OpenLoop || c.Type == CardTypes.QuestionForHuman)).ToList();

        Write(paths.Vault, "00 Atlas.md", BuildAtlas(manifest, canonical, working, openLoops));
        Write(paths.Vault, "01 Current State.md", BuildCurrentState(manifest, cards));
        Write(paths.Vault, "02 Top Canonical Truths.md", BuildCardList("Top Canonical Truths",
            "Hard, source-anchored truths (repeated / result-backed / user-confirmed).", canonical));
        Write(paths.Vault, "03 Working & Exploratory.md", BuildCardList("Working & Exploratory",
            "Hypotheses and in-progress learning. NOT yet canonical — do not treat as settled.", working));
        Write(paths.Vault, "04 Open Loops.md", BuildCardList("Open Loops & Questions",
            "Unfinished threads and questions for the human.", openLoops));
        Write(paths.Vault, "10 Source Index.md", BuildSourceIndex(manifest));

        // working/ lane detail files
        WriteWorkingLane(paths, working, openLoops);
        WriteTopics(paths, cards);
    }

    public void WriteManifest(BrainPaths paths, BrainManifest manifest)
        => File.WriteAllText(paths.Manifest, JsonSerializer.Serialize(manifest, Json));

    public BrainManifest ReadManifest(BrainPaths paths)
    {
        try
        {
            if (File.Exists(paths.Manifest))
                return JsonSerializer.Deserialize<BrainManifest>(File.ReadAllText(paths.Manifest)) ?? new BrainManifest();
        }
        catch { }
        return new BrainManifest();
    }

    public void WritePatch(BrainPaths paths, MemoryPatch patch)
    {
        Directory.CreateDirectory(paths.PatchesDir);
        File.WriteAllText(Path.Combine(paths.PatchesDir, Slug(patch.Id) + ".json"),
            JsonSerializer.Serialize(patch, Json));
    }

    // Reads all cards back from the vault — the canonical read path. The index is built FROM this.
    public List<MemoryCard> ReadAllCards(BrainPaths paths)
    {
        var list = new List<MemoryCard>();
        foreach (var dir in new[] { paths.CardsCanonical, paths.CardsWorking })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
            {
                try { list.Add(CardMarkdown.Parse(File.ReadAllText(f))); } catch { }
            }
        }
        return list;
    }

    // --- source links (the "open source span" contract) ---
    public static string SourceUri(string collectionId, SourceAnchor a)
        => $"collections://source?collection={Uri.EscapeDataString(collectionId)}" +
           $"&session={Uri.EscapeDataString(a.SessionId)}&start={a.MsgStartIndex}&end={a.MsgEndIndex}";

    public static string SourceLink(string collectionId, SourceAnchor a)
        => $"[{a.SessionId} msg {a.MsgStartIndex}–{a.MsgEndIndex}]({SourceUri(collectionId, a)})";

    // --- body composition ---
    private static string ComposeBody(MemoryCard card)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(card.Title).Append("\n\n");
        var body = (card.Body ?? "").Trim();
        // strip a duplicate leading "# title" if the caller already included one
        if (body.StartsWith("# "))
        {
            var nl = body.IndexOf('\n');
            body = nl >= 0 ? body[(nl + 1)..].TrimStart('\n') : "";
        }
        // strip any prior auto Sources section so re-writes don't stack them
        var marker = "\n## Sources\n";
        var mi = body.IndexOf(marker, StringComparison.Ordinal);
        if (mi >= 0) body = body[..mi].TrimEnd();
        if (body.Length > 0) sb.Append(body).Append("\n\n");

        sb.Append("## Sources\n");
        if (card.Sources.Count == 0)
        {
            sb.Append("_No source anchor — this card is `").Append(card.SourceCoverage)
              .Append("` and cannot be promoted to canonical without one._\n");
        }
        else
        {
            // the body uses a placeholder collection id; the app rewrites links with the real id when
            // it knows it. We store the anchor coordinates verbatim so the link is always reconstructable.
            foreach (var s in card.Sources)
                sb.Append("- ").Append(SourceLink("{collection}", s)).Append("  `").Append(Short(s.QuoteHash)).Append("`\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string BuildAtlas(BrainManifest m, List<MemoryCard> canonical, List<MemoryCard> working, List<MemoryCard> openLoops)
    {
        var sb = new StringBuilder();
        sb.Append("# Atlas — ").Append(string.IsNullOrWhiteSpace(m.CollectionName) ? m.CollectionId : m.CollectionName).Append("\n\n");
        sb.Append("> Global map of this project's memory. Compress for orientation; open a card to ");
        sb.Append("select for precision; follow a card's Source link to verify against raw transcript.\n\n");
        sb.Append("- Built: ").Append(string.IsNullOrWhiteSpace(m.BuiltAt) ? "(not yet)" : m.BuiltAt).Append('\n');
        sb.Append("- Incorporated chats: ").Append(m.IncorporatedChats.Count).Append('\n');
        sb.Append("- Canonical truths: ").Append(canonical.Count).Append("  ·  Working: ").Append(working.Count)
          .Append("  ·  Open loops: ").Append(openLoops.Count).Append("\n\n");
        sb.Append("## Navigate\n");
        sb.Append("- [[01 Current State]]\n- [[02 Top Canonical Truths]]\n- [[03 Working & Exploratory]]\n");
        sb.Append("- [[04 Open Loops]]\n- [[10 Source Index]]\n\n");
        sb.Append("## Top canonical truths\n");
        AppendLinks(sb, canonical.Take(10));
        sb.Append("\n## Active working threads\n");
        AppendLinks(sb, working.Take(10));
        return sb.ToString();
    }

    private static string BuildCurrentState(BrainManifest m, IReadOnlyList<MemoryCard> cards)
    {
        var sb = new StringBuilder();
        sb.Append("# Current State\n\n");
        var state = cards.Where(c => c.Type == CardTypes.CurrentState && c.Status == CardStatuses.Active)
            .OrderByDescending(c => c.Importance).ToList();
        if (state.Count == 0) sb.Append("_No current-state card yet._\n");
        foreach (var c in state) sb.Append("## ").Append(c.Title).Append("\n[[").Append(c.Id).Append("]]\n\n");
        sb.Append("\n---\nCounts by lane: canonical ")
          .Append(cards.Count(c => c.Lane == Lanes.Canonical))
          .Append(", working ").Append(cards.Count(c => c.Lane == Lanes.Working))
          .Append(", superseded ").Append(cards.Count(c => c.Status == CardStatuses.Superseded)).Append('\n');
        return sb.ToString();
    }

    private static string BuildCardList(string title, string subtitle, IReadOnlyList<MemoryCard> cards)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(title).Append("\n\n> ").Append(subtitle).Append("\n\n");
        if (cards.Count == 0) { sb.Append("_None yet._\n"); return sb.ToString(); }
        foreach (var c in cards)
        {
            sb.Append("## [[").Append(c.Id).Append("|").Append(c.Title).Append("]]\n");
            sb.Append("`").Append(c.Type).Append("` · truth: `").Append(c.TruthEvidence)
              .Append("` · extraction: `").Append(c.ExtractionConfidence).Append("` · importance ")
              .Append(c.Importance).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string BuildSourceIndex(BrainManifest m)
    {
        var sb = new StringBuilder();
        sb.Append("# Source Index\n\n> Chats incorporated into this brain, with the file stamp at build time.\n\n");
        if (m.IncorporatedChats.Count == 0) { sb.Append("_No chats incorporated yet._\n"); return sb.ToString(); }
        foreach (var kv in m.IncorporatedChats.OrderBy(k => k.Key, StringComparer.Ordinal))
            sb.Append("- `").Append(kv.Key).Append("` — stamp `").Append(kv.Value).Append("`\n");
        return sb.ToString();
    }

    private void WriteWorkingLane(BrainPaths paths, IReadOnlyList<MemoryCard> working, IReadOnlyList<MemoryCard> openLoops)
    {
        string Section(string title, Func<MemoryCard, bool> pred, IReadOnlyList<MemoryCard> src)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(title).Append("\n\n");
            var sel = src.Where(pred).ToList();
            if (sel.Count == 0) sb.Append("_None._\n");
            foreach (var c in sel) sb.Append("- [[").Append(c.Id).Append("|").Append(c.Title).Append("]]\n");
            return sb.ToString();
        }
        Write(paths.WorkingDir, "Exploratory.md", Section("Exploratory", c => true, working));
        Write(paths.WorkingDir, "Hypotheses.md", Section("Hypotheses", c => c.Type == CardTypes.RejectedApproach || c.TruthEvidence == TruthEvidence.Weak || c.TruthEvidence == TruthEvidence.SingleSource, working));
        Write(paths.WorkingDir, "In-Progress.md", Section("In Progress", c => c.Type == CardTypes.CurrentState || c.Actionability >= 4, working));
        Write(paths.WorkingDir, "Open Questions.md", Section("Open Questions", c => true, openLoops));
    }

    private void WriteTopics(BrainPaths paths, IReadOnlyList<MemoryCard> cards)
    {
        ClearDir(paths.TopicsDir);
        var byTopic = new Dictionary<string, List<MemoryCard>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cards)
            foreach (var t in c.Topics)
            {
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (!byTopic.TryGetValue(t, out var l)) byTopic[t] = l = new List<MemoryCard>();
                l.Add(c);
            }
        foreach (var kv in byTopic)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(kv.Key).Append("\n\n");
            foreach (var c in kv.Value.OrderByDescending(c => c.Importance))
                sb.Append("- [[").Append(c.Id).Append("|").Append(c.Title).Append("]] `").Append(c.Lane).Append("`\n");
            Write(paths.TopicsDir, Slug(kv.Key) + ".md", sb.ToString());
        }
    }

    private static void AppendLinks(StringBuilder sb, IEnumerable<MemoryCard> cards)
    {
        var any = false;
        foreach (var c in cards) { sb.Append("- [[").Append(c.Id).Append("|").Append(c.Title).Append("]]\n"); any = true; }
        if (!any) sb.Append("_None yet._\n");
    }

    private static void Write(string dir, string name, string content)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), content.TrimEnd('\n') + "\n");
    }

    private static void ClearDir(string dir)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return; }
        foreach (var f in Directory.GetFiles(dir, "*.md")) { try { File.Delete(f); } catch { } }
    }

    private static string Slug(string id)
    {
        var sb = new StringBuilder();
        foreach (var ch in id.Trim())
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-');
        var s = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(s) ? "card" : s;
    }

    private static string Short(string hash) => string.IsNullOrEmpty(hash) ? "" : hash.Length <= 10 ? hash : hash[..10];
}
