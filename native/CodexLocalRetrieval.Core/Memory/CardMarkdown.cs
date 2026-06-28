using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodexLocalRetrieval.Core.Memory;

// Serialize a MemoryCard to its canonical `.md` (YAML frontmatter + body) AND parse it back. Both
// directions live HERE, side by side, so the writer and the index reader can never disagree about the
// format — that lockstep is what makes "the Markdown vault is canonical, SQLite is a derived index"
// actually true: a hand-edited card re-parses cleanly and the index rebuilds from it.
//
// Deliberately a small, tolerant YAML subset (scalars, inline string lists `[a, b]`, and one nested
// block list `sources:`), not a full YAML engine — enough for this fixed schema, no dependency.
public static class CardMarkdown
{
    private const string Fence = "---";

    public static string Serialize(MemoryCard c)
    {
        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n');
        Scalar(sb, "schema_version", c.SchemaVersion);
        QuotedScalar(sb, "id", c.Id);
        QuotedScalar(sb, "title", c.Title);
        Bare(sb, "lane", c.Lane);
        Bare(sb, "type", c.Type);
        Bare(sb, "status", c.Status);
        Bare(sb, "truth_evidence", c.TruthEvidence);
        Bare(sb, "extraction_confidence", c.ExtractionConfidence);
        Scalar(sb, "importance", c.Importance);
        Scalar(sb, "durability", c.Durability);
        Scalar(sb, "actionability", c.Actionability);
        Bare(sb, "created_by", c.CreatedBy);
        QuotedScalar(sb, "created_at", c.CreatedAt);
        QuotedScalar(sb, "derived_from_patch_id", c.DerivedFromPatchId);
        Bare(sb, "source_coverage", c.SourceCoverage);
        Bare(sb, "sensitivity", c.Sensitivity);
        QuotedScalar(sb, "superseded_reason", c.SupersededReason);
        QuotedScalar(sb, "superseded_by", c.SupersededBy);
        InlineList(sb, "supersedes", c.Supersedes);
        InlineList(sb, "led_to", c.LedTo);
        InlineList(sb, "caused_by", c.CausedBy);
        InlineList(sb, "topics", c.Topics);
        InlineList(sb, "related", c.Related);
        SourcesBlock(sb, c.Sources);
        sb.Append(Fence).Append('\n');
        sb.Append('\n');
        sb.Append(c.Body?.TrimEnd('\n') ?? "");
        sb.Append('\n');
        return sb.ToString();
    }

    public static MemoryCard Parse(string text)
    {
        var card = new MemoryCard();
        if (string.IsNullOrWhiteSpace(text)) return card;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var i = 0;
        // Skip to the opening fence.
        while (i < lines.Length && lines[i].Trim() != Fence) i++;
        if (i >= lines.Length) { card.Body = text.Trim(); return card; }
        i++; // past opening fence

        var fm = new List<string>();
        while (i < lines.Length && lines[i].Trim() != Fence) { fm.Add(lines[i]); i++; }
        i++; // past closing fence
        var body = i < lines.Length ? string.Join("\n", lines.Skip(i)).Trim('\n') : "";
        card.Body = body;

        for (var k = 0; k < fm.Count; k++)
        {
            var raw = fm[k];
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("#")) continue;
            var colon = IndexOfTopLevelColon(raw);
            if (colon < 0) continue;
            var key = raw[..colon].Trim();
            var val = raw[(colon + 1)..].Trim();

            if (key == "sources")
            {
                // Consume the indented block list that follows.
                var (sources, consumed) = ParseSources(fm, k + 1);
                card.Sources = sources;
                k += consumed;
                continue;
            }

            switch (key)
            {
                case "schema_version": card.SchemaVersion = ParseInt(val, MemoryCard.CurrentSchemaVersion); break;
                case "id": card.Id = Unquote(val); break;
                case "title": card.Title = Unquote(val); break;
                case "lane": card.Lane = Unquote(val); break;
                case "type": card.Type = Unquote(val); break;
                case "status": card.Status = Unquote(val); break;
                case "truth_evidence": card.TruthEvidence = Unquote(val); break;
                case "extraction_confidence": card.ExtractionConfidence = Unquote(val); break;
                case "importance": card.Importance = ParseInt(val, 3); break;
                case "durability": card.Durability = ParseInt(val, 3); break;
                case "actionability": card.Actionability = ParseInt(val, 3); break;
                case "created_by": card.CreatedBy = Unquote(val); break;
                case "created_at": card.CreatedAt = Unquote(val); break;
                case "derived_from_patch_id": card.DerivedFromPatchId = Unquote(val); break;
                case "source_coverage": card.SourceCoverage = Unquote(val); break;
                case "sensitivity": card.Sensitivity = Unquote(val); break;
                case "superseded_reason": card.SupersededReason = Unquote(val); break;
                case "superseded_by": card.SupersededBy = Unquote(val); break;
                case "supersedes": card.Supersedes = ParseInlineList(val); break;
                case "led_to": card.LedTo = ParseInlineList(val); break;
                case "caused_by": card.CausedBy = ParseInlineList(val); break;
                case "topics": card.Topics = ParseInlineList(val); break;
                case "related": card.Related = ParseInlineList(val); break;
            }
        }
        return card;
    }

    // --- serialize helpers ---
    private static void Scalar(StringBuilder sb, string k, int v) => sb.Append(k).Append(": ").Append(v.ToString(CultureInfo.InvariantCulture)).Append('\n');
    private static void Bare(StringBuilder sb, string k, string v) => sb.Append(k).Append(": ").Append(string.IsNullOrEmpty(v) ? "\"\"" : v).Append('\n');
    private static void QuotedScalar(StringBuilder sb, string k, string v) => sb.Append(k).Append(": ").Append(Quote(v)).Append('\n');

    private static void InlineList(StringBuilder sb, string k, List<string> items)
    {
        sb.Append(k).Append(": [");
        if (items != null && items.Count > 0)
            sb.Append(string.Join(", ", items.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())));
        sb.Append("]\n");
    }

    private static void SourcesBlock(StringBuilder sb, List<SourceAnchor> sources)
    {
        sb.Append("sources:");
        if (sources == null || sources.Count == 0) { sb.Append(" []\n"); return; }
        sb.Append('\n');
        foreach (var s in sources)
        {
            sb.Append("  - session_id: ").Append(Quote(s.SessionId)).Append('\n');
            sb.Append("    source_path: ").Append(Quote(s.SourcePath)).Append('\n');
            sb.Append("    block_id: ").Append(Quote(s.BlockId)).Append('\n');
            sb.Append("    msg_start_index: ").Append(s.MsgStartIndex).Append('\n');
            sb.Append("    msg_end_index: ").Append(s.MsgEndIndex).Append('\n');
            sb.Append("    ts_start: ").Append(Quote(s.TsStart)).Append('\n');
            sb.Append("    ts_end: ").Append(Quote(s.TsEnd)).Append('\n');
            sb.Append("    quote_hash: ").Append(Quote(s.QuoteHash)).Append('\n');
            sb.Append("    file_stamp: ").Append(Quote(s.FileStamp)).Append('\n');
        }
    }

    private static string Quote(string? v)
    {
        v ??= "";
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    // --- parse helpers ---
    private static (List<SourceAnchor> sources, int consumed) ParseSources(List<string> fm, int start)
    {
        var list = new List<SourceAnchor>();
        var k = start;
        SourceAnchor? cur = null;
        var consumed = 0;
        for (; k < fm.Count; k++)
        {
            var line = fm[k];
            if (string.IsNullOrWhiteSpace(line)) { consumed++; continue; }
            // a non-indented line ends the block (it's the next top-level key)
            if (!line.StartsWith(" ")) break;
            consumed++;
            var trimmed = line.Trim();
            var isNewItem = trimmed.StartsWith("- ");
            if (isNewItem) { cur = new SourceAnchor(); list.Add(cur); trimmed = trimmed[2..].Trim(); }
            if (cur == null) continue;
            var colon = trimmed.IndexOf(':');
            if (colon < 0) continue;
            var key = trimmed[..colon].Trim();
            var val = Unquote(trimmed[(colon + 1)..].Trim());
            cur = key switch
            {
                "session_id" => cur with { SessionId = val },
                "source_path" => cur with { SourcePath = val },
                "block_id" => cur with { BlockId = val },
                "msg_start_index" => cur with { MsgStartIndex = ParseInt(val, 0) },
                "msg_end_index" => cur with { MsgEndIndex = ParseInt(val, 0) },
                "ts_start" => cur with { TsStart = val },
                "ts_end" => cur with { TsEnd = val },
                "quote_hash" => cur with { QuoteHash = val },
                "file_stamp" => cur with { FileStamp = val },
                _ => cur,
            };
            // keep the list reference pointing at the latest immutable copy
            list[^1] = cur;
        }
        return (list, consumed);
    }

    private static int IndexOfTopLevelColon(string line)
    {
        // first colon not inside quotes
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"') inQuote = !inQuote;
            else if (ch == ':' && !inQuote) return i;
        }
        return -1;
    }

    private static List<string> ParseInlineList(string val)
    {
        val = val.Trim();
        if (val.StartsWith("[")) val = val[1..];
        if (val.EndsWith("]")) val = val[..^1];
        return val.Split(',')
            .Select(x => Unquote(x.Trim()))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static int ParseInt(string val, int fallback)
        => int.TryParse(Unquote(val).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static string Unquote(string v)
    {
        v = v.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return v;
    }
}
