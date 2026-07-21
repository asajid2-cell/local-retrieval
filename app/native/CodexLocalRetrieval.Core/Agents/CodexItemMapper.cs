using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Maps a codex app-server `ThreadItem` (the unified item type used by BOTH thread/read history and the
// live item/* notifications) into our AgentEvent model, so one renderer shows stored and live the same.
// ThreadItem variants: userMessage | agentMessage | plan | reasoning | commandExecution | fileChange |
// mcpToolCall | dynamicToolCall | hookPrompt.
public static class CodexItemMapper
{
    // A COMPLETE item (history, or an item/completed notification) -> its full event(s).
    public static IEnumerable<AgentEvent> MapComplete(JsonElement item)
    {
        var id = Str(item, "id");
        switch (Str(item, "type"))
        {
            case "userMessage":
                yield return new AgentEvent { Kind = AgentEventKind.UserMessage, ItemId = id, Text = JoinContent(item, "content") };
                break;
            case "agentMessage":
                yield return new AgentEvent { Kind = AgentEventKind.AssistantText, ItemId = id, Text = Str(item, "text") ?? "" };
                break;
            case "plan":
                yield return new AgentEvent { Kind = AgentEventKind.AssistantText, ItemId = id, Text = "**Plan**\n" + (Str(item, "text") ?? "") };
                break;
            case "reasoning":
                var r = (JoinArr(item, "summary") + "\n" + JoinArr(item, "content")).Trim();
                if (r.Length > 0) yield return new AgentEvent { Kind = AgentEventKind.Thinking, ItemId = id, Text = r };
                break;
            case "commandExecution":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "command", ToolInput = Str(item, "command"), State = Str(item, "status") ?? "completed" };
                yield return new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = id, Output = Str(item, "aggregatedOutput"), ExitCode = Int(item, "exitCode"), State = Str(item, "status") ?? "completed" };
                break;
            case "fileChange":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "edit", ToolInput = SummarizeChanges(item), State = Str(item, "status") ?? "completed" };
                break;
            case "mcpToolCall":
            case "dynamicToolCall":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = ToolLabel(item), ToolInput = Raw(item, "arguments"), State = Str(item, "status") ?? "completed" };
                if (Str(item, "result") is { } res) yield return new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = id, Output = res, State = "completed" };
                break;
        }
    }

    // A live app-server notification {method, params} -> event(s). params carry threadId/turnId/itemId.
    public static IEnumerable<AgentEvent> MapNotification(string method, JsonElement p)
    {
        switch (method)
        {
            case "item/started":
                if (p.TryGetProperty("item", out var i1)) foreach (var e in MapStarted(i1)) yield return e;
                break;
            case "item/completed":
                if (p.TryGetProperty("item", out var i2)) foreach (var e in MapComplete(i2)) yield return e;
                break;
            case "item/agentMessage/delta":
                yield return new AgentEvent { Kind = AgentEventKind.AssistantText, ItemId = Str(p, "itemId"), Text = Str(p, "delta") ?? "", Delta = true };
                break;
            case "item/reasoning/textDelta":
            case "item/reasoning/summaryTextDelta":
                yield return new AgentEvent { Kind = AgentEventKind.Thinking, ItemId = Str(p, "itemId"), Text = Str(p, "delta") ?? "", Delta = true };
                break;
            case "item/commandExecution/outputDelta":
                yield return new AgentEvent { Kind = AgentEventKind.ToolOutput, ItemId = Str(p, "itemId"), Output = Str(p, "delta") ?? "", Delta = true, State = "in_progress" };
                break;
            case "item/plan/delta":
                yield return new AgentEvent { Kind = AgentEventKind.AssistantText, ItemId = Str(p, "itemId"), Text = Str(p, "delta") ?? "", Delta = true };
                break;
            case "turn/started":
                yield return AgentEvent.Stat("turn-start");
                break;
            case "turn/completed":
                yield return new AgentEvent { Kind = AgentEventKind.TurnResult, Usage = p.TryGetProperty("usage", out var u) ? u.Clone() : null };
                yield return AgentEvent.Stat("idle");
                break;
            case "turn/failed":
                yield return AgentEvent.Err(p.TryGetProperty("error", out var er) && er.TryGetProperty("message", out var m) ? (m.GetString() ?? "turn failed") : "turn failed");
                yield return AgentEvent.Stat("idle");
                break;
            case "error":
                yield return AgentEvent.Err(Str(p, "message") ?? "error");
                break;
        }
    }

    // A STARTED item (live item/started) -> the in-progress representation (messages/reasoning arrive via deltas).
    public static IEnumerable<AgentEvent> MapStarted(JsonElement item)
    {
        var id = Str(item, "id");
        switch (Str(item, "type"))
        {
            case "commandExecution":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "command", ToolInput = Str(item, "command"), State = "in_progress" };
                break;
            case "fileChange":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = "edit", ToolInput = SummarizeChanges(item), State = "in_progress" };
                break;
            case "mcpToolCall":
            case "dynamicToolCall":
                yield return new AgentEvent { Kind = AgentEventKind.ToolCall, ItemId = id, ToolName = ToolLabel(item), State = "in_progress" };
                break;
        }
    }

    private static string ToolLabel(JsonElement item) =>
        (Str(item, "server") ?? Str(item, "namespace") ?? "tool") + "/" + (Str(item, "tool") ?? "");

    private static string SummarizeChanges(JsonElement item)
    {
        if (!item.TryGetProperty("changes", out var ch) || ch.ValueKind != JsonValueKind.Array) return "(file changes)";
        var paths = new List<string>();
        foreach (var c in ch.EnumerateArray())
            if (c.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String) paths.Add(pe.GetString()!);
        return paths.Count == 0 ? "(file changes)" : string.Join(", ", paths);
    }

    private static string JoinContent(JsonElement item, string prop)
    {
        if (!item.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var c in arr.EnumerateArray())
            if (c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) sb.Append(t.GetString());
        return sb.ToString();
    }

    private static string JoinArr(JsonElement item, string prop)
    {
        if (!item.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var e in arr.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) { if (sb.Length > 0) sb.Append('\n'); sb.Append(e.GetString()); }
        return sb.ToString();
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
    private static string? Raw(JsonElement e, string p) => e.TryGetProperty(p, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText()) : null;
}
