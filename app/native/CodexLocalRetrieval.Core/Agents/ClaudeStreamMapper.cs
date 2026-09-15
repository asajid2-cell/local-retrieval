using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Maps a line of `claude -p --output-format stream-json` into our AgentEvent model, so a live Claude
// turn streams into the same renderer as Codex. Line types: system/init (session id), assistant
// (content: text | thinking | tool_use), user (content: tool_result), result (success|error).
public static class ClaudeStreamMapper
{
    public static IEnumerable<AgentEvent> Map(string line)
    {
        JsonElement m;
        try { using var d = JsonDocument.Parse(line); m = d.RootElement.Clone(); }
        catch { yield break; }

        switch (Str(m, "type"))
        {
            case "system":
                if (Str(m, "subtype") == "init" && Str(m, "session_id") is { Length: > 0 } sid)
                    yield return AgentEvent.Session(sid);
                break;

            case "assistant":
                if (m.TryGetProperty("message", out var am) && am.TryGetProperty("content", out var ac) && ac.ValueKind == JsonValueKind.Array)
                    foreach (var c in ac.EnumerateArray())
                        switch (Str(c, "type"))
                        {
                            case "text":
                                yield return new AgentEvent { Kind = AgentEventKind.AssistantText, ItemId = Str(c, "id"), Text = Str(c, "text") ?? "" };
                                break;
                            case "thinking":
                                if (Str(c, "thinking") is { Length: > 0 } th)
                                    yield return new AgentEvent { Kind = AgentEventKind.Thinking, Text = th };
                                break;
                            case "tool_use":
                                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = Str(c, "id"), ToolName = Str(c, "name") ?? "tool", ToolInput = ClaudeSessionStore.RawToolInput(c), State = "in_progress" };
                                break;
                        }
                break;

            case "user":
                if (m.TryGetProperty("message", out var um) && um.TryGetProperty("content", out var uc) && uc.ValueKind == JsonValueKind.Array)
                    foreach (var c in uc.EnumerateArray())
                        if (Str(c, "type") == "tool_result")
                            yield return new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = Str(c, "tool_use_id"), Output = Cap(ClaudeSessionStore.FlattenResult(c)), State = "completed" };
                break;

            case "result":
                if (Str(m, "subtype") == "success")
                    // Token usage rides the same field the codex mappers populate, so a Gateway/Claude turn
                    // reports usage the way a codex turn does. Absent JSON leaves it null rather than a
                    // zeroed object, so a consumer can tell "not reported" from "reported zero".
                    yield return new AgentEvent { Kind = AgentEventKind.TurnResult, Usage = m.TryGetProperty("usage", out var usage) ? usage.Clone() : null };
                else
                    yield return AgentEvent.Err(Str(m, "result") ?? Str(m, "subtype") ?? "claude error");
                yield return AgentEvent.Stat("idle");
                break;
        }
    }

    private static string? Str(JsonElement e, string p) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Cap(string s, int n = 6000) => s.Length <= n ? s : s[..n] + "\n…(truncated)";
}
