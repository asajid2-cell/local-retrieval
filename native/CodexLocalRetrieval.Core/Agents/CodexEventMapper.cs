using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Pure mapping: one line of `codex exec --json` output -> zero or more normalized AgentEvents.
// Kept separate from the process driver so it can be unit-tested against captured real output.
// Event shapes (codex 0.130): thread.started{thread_id} · turn.started · turn.completed{usage} ·
// turn.failed{error} · error{message} · item.{started,updated,completed}{item:{id,type,...}} where
// item.type ∈ command_execution{command,aggregated_output,exit_code,status} | agent_message{text} |
// reasoning{text} | file_change/patch | mcp_tool_call.
public static class CodexEventMapper
{
    public static IReadOnlyList<AgentEvent> Map(string line)
    {
        var outp = new List<AgentEvent>();
        if (string.IsNullOrWhiteSpace(line)) return outp;
        JsonElement root;
        try { using var doc = JsonDocument.Parse(line); root = doc.RootElement.Clone(); }
        catch { return outp; } // ignore non-JSON noise lines

        switch (Str(root, "type"))
        {
            case "thread.started":
                if (Str(root, "thread_id") is { } tid) outp.Add(AgentEvent.Session(tid));
                break;
            case "turn.started":
                outp.Add(AgentEvent.Stat("turn-start"));
                break;
            case "turn.completed":
                outp.Add(new AgentEvent { Kind = AgentEventKind.TurnResult, Usage = root.TryGetProperty("usage", out var u) ? JsonToObj(u) : null });
                outp.Add(AgentEvent.Stat("idle"));
                break;
            case "turn.failed":
                outp.Add(AgentEvent.Err(NestedErr(root)));
                break;
            case "error":
                outp.Add(AgentEvent.Err(Str(root, "message") ?? "error"));
                break;
            case "item.started":
            case "item.updated":
            case "item.completed":
                MapItem(Str(root, "type")!, root, outp);
                break;
        }
        return outp;
    }

    private static void MapItem(string evType, JsonElement root, List<AgentEvent> outp)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;
        var id = Str(item, "id");
        var itype = Str(item, "type");
        var completed = evType == "item.completed";
        var state = Str(item, "status") ?? (completed ? "completed" : "in_progress");

        switch (itype)
        {
            case "command_execution":
                if (evType == "item.started")
                    outp.Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "command", ToolInput = Str(item, "command"), State = state });
                else // updated/completed carry the (streaming/final) output
                    outp.Add(new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = id, Output = Str(item, "aggregated_output"), ExitCode = Int(item, "exit_code"), State = state });
                break;

            case "agent_message":
                if (completed) outp.Add(AgentEvent.Assistant(Str(item, "text") ?? ""));
                break;

            case "reasoning":
                if (completed) outp.Add(AgentEvent.Think(Str(item, "text") ?? Str(item, "summary") ?? ""));
                break;

            case "file_change":
            case "patch":
                outp.Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "edit", ToolInput = Str(item, "path") ?? Str(item, "summary"), State = state });
                if (completed && Str(item, "diff") is { } diff)
                    outp.Add(new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = id, Output = diff, State = state });
                break;

            case "mcp_tool_call":
                outp.Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = (Str(item, "server") ?? "mcp") + "/" + (Str(item, "tool") ?? ""), ToolInput = Str(item, "arguments"), State = state });
                if (completed) outp.Add(new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = id, Output = Str(item, "result"), State = state });
                break;

            default:
                // never silently drop an unknown item type
                if (completed) outp.Add(new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = itype ?? "item", State = state });
                break;
        }
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static object? JsonToObj(JsonElement e) { try { return JsonSerializer.Deserialize<object>(e.GetRawText()); } catch { return null; } }
    private static string NestedErr(JsonElement root) =>
        root.TryGetProperty("error", out var er) && er.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "turn failed";
}
