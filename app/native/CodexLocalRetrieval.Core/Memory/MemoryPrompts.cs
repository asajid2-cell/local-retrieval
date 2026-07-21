using System.Collections.Generic;
using System.Text;

namespace CodexLocalRetrieval.Core.Memory;

// The prompts that turn source blocks into candidate memory cards. The rules here are the first line
// of defense against the "fake certainty" failure mode: quote + anchor, flag uncertainty into the
// working lane, never invent. The model proposes; nothing it writes becomes canonical without a
// reviewable patch + an apply step.
public static class MemoryPrompts
{
    public const string ExtractionSystem =
@"You extract DURABLE PROJECT MEMORY from AI coding-chat transcripts. You are building a long-lived
brain for a project, not summarizing a conversation.

Capture BOTH the technical facts AND the causal/narrative arc: decisions and their consequences,
method -> result (e.g. 'method X led to a working servant FX'), reversals, dead ends, breakthroughs,
constraints, current state, open loops, and user preferences.

HARD RULES:
- Ground every card in the provided blocks. Cite the block_ids you used. Do NOT invent facts.
- If something is a hypothesis, an in-progress probe, or supported by only a single mention, set
  lane=""working"" and truth_evidence=""single_source"" or ""weak"". Do not overstate.
- Use lane=""canonical"" ONLY for hard truths that are repeated across blocks, backed by an observed
  result, or explicitly confirmed by the user. Even then, you MUST cite block_ids.
- Prefer fewer, higher-signal cards over many shallow ones.

Output STRICT JSON: an array of cards. Each card:
{
  ""title"": string,
  ""type"": one of [decision, canonical_truth, open_loop, rejected_approach, mistake_or_reversal,
            win_or_breakthrough, constraint, current_state, user_preference, architecture_fact,
            file_or_artifact, risk, question_for_human],
  ""lane"": ""canonical"" | ""working"",
  ""truth_evidence"": ""weak"" | ""single_source"" | ""repeated_in_sources"" | ""result_backed"" | ""user_confirmed"",
  ""importance"": 1..5,
  ""body"": string (markdown; explain the WHY and the causal chain, not just the what),
  ""topics"": string[],
  ""block_ids"": string[]   // the blocks this card is grounded in — REQUIRED for canonical
}
Output ONLY the JSON array. No prose, no code fences.";

    // Renders the blocks for one chat (or many) into the user message. Excerpts are already REDACTED.
    public static string BlocksUserMessage(IReadOnlyList<SourceBlock> blocks)
    {
        var sb = new StringBuilder();
        sb.Append("Source blocks (excerpts are redacted of secrets). Cite these block_ids:\n\n");
        foreach (var b in blocks)
        {
            sb.Append("### ").Append(b.Id).Append('\n');
            sb.Append("session: ").Append(b.SessionId)
              .Append(" · messages ").Append(b.MsgStartIndex).Append('–').Append(b.MsgEndIndex)
              .Append(" · roles ").Append(string.Join(",", b.Roles)).Append('\n');
            sb.Append(b.Excerpt).Append("\n\n");
        }
        sb.Append("Extract the durable memory cards as a strict JSON array now.");
        return sb.ToString();
    }

    public const string HandoffSystem =
@"You are writing a COMPACT working-context handoff for the next agent picking up this project.
Use only the provided cards/atlas. Lead with current state, then the hard canonical truths, then
open loops and warnings. Be terse and high-signal. Point to source anchors for anything load-bearing.";
}
